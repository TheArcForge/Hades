import Foundation
import HadesControl
import Testing

@testable import HadesApp

/// `SettingsViewModel` owns the Settings surface's own fetch and OS-fact reads - see that type's own
/// doc comment for the settled data-ownership split every other section view model already follows.
/// Unlike `ProjectsViewModel`/`TracesViewModel`/`MemoryViewModel`, it is not tied to
/// `MainWindowViewModel`'s poll lifecycle at all: Settings is a standard macOS Settings scene (see
/// `Section`'s own doc comment for why it is deliberately not a sidebar destination), refreshed each
/// time its own window opens (`SettingsWindowController.show()`), never polled continuously.
///
/// **What these tests prove, and what they cannot.** `FakeLaunchAtLoginReading`/
/// `FakeResourceGuardReading` prove `SettingsViewModel`'s own CONTRACT with the `ShellFacts`
/// protocols - in particular the plan's own explicit requirement that a toggle "reflect the OS's
/// answer after toggling, not the value requested," including the silent-failure case a thrown error
/// alone would not catch. They cannot prove `SMAppService`/`ProcessInfo` THEMSELVES behave this way -
/// that is `LaunchAtLoginService`/`ResourceGuardReader`'s own job, proven once live outside this
/// automated suite (see the Plan 13 Task 7 report for that check), and Task 8's hand-run pass beyond
/// that (this project's standing "only a real OS session can prove AppKit/system-API contracts"
/// discipline - see `MainWindowSceneTests`'/`DirectoryPicking`'s own doc comments for the same split).
@Suite("SettingsViewModel")
@MainActor
struct SettingsViewModelTests {

    /// Verbatim shape `SettingsEndpoint.Resolve` produces when the port is free - mirrors the real
    /// live-captured `settings_mcp_port_in_use.json` fixture's OWN sibling free-port response
    /// (HadesControlTests' own `DTODecodingTests.settingsMcpPortInUse` covers the in-use shape).
    static let realSettings = SettingsResult(
        mcpPort: McpPortSetting(port: 7823, inUse: false, message: "Port 7823 is available."),
        logLevel: LogLevelSetting(level: "Information")
    )

    // MARK: - Initial state

    @Test("starts with no settings, and the OS-fact defaults, before any refresh")
    func startsEmpty() {
        let viewModel = SettingsViewModel(
            discover: { nil }, makeClient: { _ in FakeSettingsFetcher([]) },
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false), resourceGuards: FakeResourceGuardReading()
        )

        #expect(viewModel.settings == nil)
        #expect(viewModel.launchAtLoginEnabled == false)
        #expect(viewModel.isLowPowerModeEnabled == false)
        #expect(viewModel.thermalState == .nominal)
    }

    // MARK: - refresh()

    @Test("refresh() populates settings verbatim from a successful fetch, and the OS facts from ShellFacts")
    func refreshPopulatesSettingsAndOSFacts() async {
        let fetcher = FakeSettingsFetcher([.success(Self.realSettings)])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let launchAtLogin = FakeLaunchAtLoginReading(isEnabled: true)
        let resourceGuards = FakeResourceGuardReading(isLowPowerModeEnabled: true, thermalState: .serious)
        let viewModel = SettingsViewModel(
            discover: { await connections.provide() }, makeClient: { _ in fetcher },
            launchAtLogin: launchAtLogin, resourceGuards: resourceGuards
        )

        await viewModel.refresh()

        #expect(viewModel.settings == Self.realSettings)
        #expect(viewModel.launchAtLoginEnabled == true)
        #expect(viewModel.isLowPowerModeEnabled == true)
        #expect(viewModel.thermalState == .serious)
        #expect(await fetcher.settingsCallCount == 1)
    }

    @Test("refresh() reads the OS facts even when no core is reachable at all - they need no network")
    func refreshReadsOSFactsWithoutAConnection() async {
        let launchAtLogin = FakeLaunchAtLoginReading(isEnabled: true)
        let resourceGuards = FakeResourceGuardReading(isLowPowerModeEnabled: true, thermalState: .fair)
        let viewModel = SettingsViewModel(
            discover: { nil }, makeClient: { _ in FakeSettingsFetcher([]) },
            launchAtLogin: launchAtLogin, resourceGuards: resourceGuards
        )

        await viewModel.refresh()

        #expect(viewModel.settings == nil, "no connection means no fetch was even attempted")
        #expect(viewModel.launchAtLoginEnabled == true)
        #expect(viewModel.isLowPowerModeEnabled == true)
        #expect(viewModel.thermalState == .fair)
    }

    @Test("a fetch failure leaves settings unchanged - self-heals on the next refresh, same discipline every other view model holds")
    func fetchFailureSelfHeals() async {
        let fetcher = FakeSettingsFetcher([.success(Self.realSettings), .failure(.staleToken)])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = SettingsViewModel(
            discover: { await connections.provide() }, makeClient: { _ in fetcher },
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false), resourceGuards: FakeResourceGuardReading()
        )

        await viewModel.refresh()
        #expect(viewModel.settings == Self.realSettings)

        await viewModel.refresh()
        #expect(viewModel.settings == Self.realSettings, "a failed refresh must not clear settings already on screen")
    }

    // MARK: - toggleLaunchAtLogin(to:) - the plan's own explicit requirement: reflect the OS's
    // answer after toggling, never the requested value, so a toggle that silently fails cannot
    // display as on.

    @Test("toggleLaunchAtLogin reflects the OS's real answer once the request succeeds")
    func toggleReflectsRealSuccess() {
        let launchAtLogin = FakeLaunchAtLoginReading(isEnabled: false)
        let viewModel = SettingsViewModel(
            discover: { nil }, makeClient: { _ in FakeSettingsFetcher([]) },
            launchAtLogin: launchAtLogin, resourceGuards: FakeResourceGuardReading()
        )

        viewModel.toggleLaunchAtLogin(to: true)

        #expect(viewModel.launchAtLoginEnabled == true)
        #expect(launchAtLogin.lastRequestedValue == true)
    }

    @Test("a request the OS silently ignores (no throw, but isEnabled unchanged) must NOT display as on")
    func toggleDoesNotDisplayOnWhenTheOSSilentlyIgnoresTheRequest() {
        let launchAtLogin = FakeLaunchAtLoginReading(isEnabled: false)
        launchAtLogin.applyRequestToIsEnabled = false // the OS accepts the call but never actually registers
        let viewModel = SettingsViewModel(
            discover: { nil }, makeClient: { _ in FakeSettingsFetcher([]) },
            launchAtLogin: launchAtLogin, resourceGuards: FakeResourceGuardReading()
        )

        viewModel.toggleLaunchAtLogin(to: true)

        #expect(viewModel.launchAtLoginEnabled == false, "a silently-ignored request must read back as still off, not the requested true")
    }

    @Test("a thrown setEnabled error is swallowed, and isEnabled is still re-read afterward - never left stale")
    func toggleSwallowsAThrownErrorAndStillRereadsIsEnabled() {
        struct SomeError: Error {}
        let launchAtLogin = FakeLaunchAtLoginReading(isEnabled: false)
        launchAtLogin.errorToThrow = SomeError()
        let viewModel = SettingsViewModel(
            discover: { nil }, makeClient: { _ in FakeSettingsFetcher([]) },
            launchAtLogin: launchAtLogin, resourceGuards: FakeResourceGuardReading()
        )

        viewModel.toggleLaunchAtLogin(to: true)

        #expect(viewModel.launchAtLoginEnabled == false, "the OS refused - isEnabled is still false, not the requested true")
        #expect(launchAtLogin.setEnabledCallCount == 1)
    }
}
