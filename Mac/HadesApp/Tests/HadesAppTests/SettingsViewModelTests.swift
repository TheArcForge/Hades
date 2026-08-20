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

    // MARK: - v1.2 Cleanup: the global cleanClaudeDesktopConfig action (per-item cleanup UI task)
    //
    // Deliberately lives HERE, not on `MigrationCleanupViewModel` - `migrationCleanClaudeDesktopConfig`
    // carries no `productGuid` anywhere in its signature (see `ControlMigrationFetching`'s own doc
    // comment), matching the route itself: `claude_desktop_config.json` is global and per-user, not
    // per-project. `MigrationCleanupViewModel` owns the other three, per-project cleanup actions -
    // see that type's own doc comment, and `MigrationCleanupViewModelTests` for their coverage.

    @Test("refresh() also dry-runs the global claude_desktop_config cleanup and stores the result verbatim")
    func refreshLoadsGlobalCleanupPreview() async {
        let preview = MigrationClaudeDesktopConfigCleanupResult(
            removed: false, message: "Found the 'hades' entry; not removed (no go-ahead).",
            scopeWarning: "This changes claude_desktop_config.json globally for Claude Desktop on this machine, not just this project - any other MCP server entries are left untouched.",
            occurrencesFound: 1
        )
        let settingsFetcher = FakeSettingsFetcher([.success(Self.realSettings)])
        let migrationFetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])), cleanClaudeDesktopConfigOutcome: .success(preview))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = SettingsViewModel(
            discover: { await connections.provide() }, makeClient: { _ in settingsFetcher },
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false), resourceGuards: FakeResourceGuardReading(),
            makeMigrationClient: { _ in migrationFetcher }
        )

        await viewModel.refresh()

        #expect(viewModel.claudeDesktopConfigCleanup == preview)
        #expect(await migrationFetcher.cleanClaudeDesktopConfigCallCount == 1)
        #expect(await migrationFetcher.lastCleanClaudeDesktopConfigProceed == false)
    }

    @Test("a failed global-cleanup dry run self-heals, leaving prior state - and the unrelated settings fetch is unaffected")
    func refreshGlobalCleanupPreviewFailureSelfHeals() async {
        let migrationFetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])), cleanClaudeDesktopConfigOutcome: .failure(.staleToken))
        let settingsFetcher = FakeSettingsFetcher([.success(Self.realSettings)])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = SettingsViewModel(
            discover: { await connections.provide() }, makeClient: { _ in settingsFetcher },
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false), resourceGuards: FakeResourceGuardReading(),
            makeMigrationClient: { _ in migrationFetcher }
        )

        await viewModel.refresh()

        #expect(viewModel.claudeDesktopConfigCleanup == nil)
        #expect(viewModel.settings == Self.realSettings)
    }

    @Test("cleanClaudeDesktopConfig(confirmed: false) never calls the API")
    func cleanClaudeDesktopConfigDeclinedNeverCallsAPI() async {
        let migrationFetcher = FakeMigrationFetcher(projectsOutcome: .success(ProjectsResult(projects: [])))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = SettingsViewModel(
            discover: { await connections.provide() }, makeClient: { _ in FakeSettingsFetcher([]) },
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false), resourceGuards: FakeResourceGuardReading(),
            makeMigrationClient: { _ in migrationFetcher }
        )

        await viewModel.cleanClaudeDesktopConfig(confirmed: false)

        #expect(await migrationFetcher.cleanClaudeDesktopConfigCallCount == 0)
        #expect(viewModel.claudeDesktopConfigCleanup == nil)
    }

    @Test("cleanClaudeDesktopConfig(confirmed: true) calls proceed:true and stores the result verbatim, including the global scope warning")
    func cleanClaudeDesktopConfigConfirmedStoresResultVerbatim() async {
        let confirmed = MigrationClaudeDesktopConfigCleanupResult(
            removed: true, message: "Removed the 'hades' entry from claude_desktop_config.json. Every other server entry is untouched.",
            scopeWarning: "This changes claude_desktop_config.json globally for Claude Desktop on this machine, not just this project - any other MCP server entries are left untouched.",
            occurrencesFound: 1
        )
        let migrationFetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])), cleanClaudeDesktopConfigOutcome: .success(confirmed))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = SettingsViewModel(
            discover: { await connections.provide() }, makeClient: { _ in FakeSettingsFetcher([]) },
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false), resourceGuards: FakeResourceGuardReading(),
            makeMigrationClient: { _ in migrationFetcher }
        )

        await viewModel.cleanClaudeDesktopConfig(confirmed: true)

        #expect(await migrationFetcher.cleanClaudeDesktopConfigCallCount == 1)
        #expect(await migrationFetcher.lastCleanClaudeDesktopConfigProceed == true)
        #expect(viewModel.claudeDesktopConfigCleanup == confirmed)
    }

    // MARK: - v1.2 Cleanup: the global cleanHadesHub action - closes the spec #4 §1 gap where
    // ~/.arcforge/hades-hub/launcher.js (the retired v1.2 stdio launcher) was named among what v2
    // retires but no cleanup method ever removed it.
    //
    // Lives HERE for the identical reason cleanClaudeDesktopConfig does (see this file's own
    // section above): migrationCleanHadesHub carries no productGuid anywhere in its signature,
    // matching the route itself - ~/.arcforge/hades-hub/ is global and per-user, not per-project.

    @Test("refresh() also dry-runs the global hades-hub cleanup and stores the result verbatim")
    func refreshLoadsHadesHubCleanupPreview() async {
        let preview = MigrationHadesHubCleanupResult(
            removed: false,
            message: "Found ~/.arcforge/hades-hub/ - the retired v1.2 Node launcher and its hub state (launcher.js, hub.json, hub-path.json, and anything else left there); not removed (no go-ahead). The whole directory would be removed, but ~/.arcforge/ itself and everything else under it would be untouched.",
            found: true
        )
        let settingsFetcher = FakeSettingsFetcher([.success(Self.realSettings)])
        let migrationFetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])), hadesHubCleanupOutcome: .success(preview))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = SettingsViewModel(
            discover: { await connections.provide() }, makeClient: { _ in settingsFetcher },
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false), resourceGuards: FakeResourceGuardReading(),
            makeMigrationClient: { _ in migrationFetcher }
        )

        await viewModel.refresh()

        #expect(viewModel.hadesHubCleanup == preview)
        #expect(await migrationFetcher.cleanHadesHubCallCount == 1)
        #expect(await migrationFetcher.lastCleanHadesHubProceed == false)
    }

    @Test("a failed hades-hub cleanup dry run self-heals, leaving prior state - and the unrelated settings fetch is unaffected")
    func refreshHadesHubCleanupPreviewFailureSelfHeals() async {
        let migrationFetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])), hadesHubCleanupOutcome: .failure(.staleToken))
        let settingsFetcher = FakeSettingsFetcher([.success(Self.realSettings)])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = SettingsViewModel(
            discover: { await connections.provide() }, makeClient: { _ in settingsFetcher },
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false), resourceGuards: FakeResourceGuardReading(),
            makeMigrationClient: { _ in migrationFetcher }
        )

        await viewModel.refresh()

        #expect(viewModel.hadesHubCleanup == nil)
        #expect(viewModel.settings == Self.realSettings)
    }

    @Test("cleanHadesHub(confirmed: false) never calls the API")
    func cleanHadesHubDeclinedNeverCallsAPI() async {
        let migrationFetcher = FakeMigrationFetcher(projectsOutcome: .success(ProjectsResult(projects: [])))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = SettingsViewModel(
            discover: { await connections.provide() }, makeClient: { _ in FakeSettingsFetcher([]) },
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false), resourceGuards: FakeResourceGuardReading(),
            makeMigrationClient: { _ in migrationFetcher }
        )

        await viewModel.cleanHadesHub(confirmed: false)

        #expect(await migrationFetcher.cleanHadesHubCallCount == 0)
        #expect(viewModel.hadesHubCleanup == nil)
    }

    @Test("cleanHadesHub(confirmed: true) calls proceed:true and stores the result verbatim")
    func cleanHadesHubConfirmedStoresResultVerbatim() async {
        let confirmed = MigrationHadesHubCleanupResult(
            removed: true,
            message: "Removed ~/.arcforge/hades-hub/ - the retired v1.2 Node launcher and its hub state (launcher.js, hub.json, hub-path.json, and anything else left there). ~/.arcforge/ itself and everything else under it is untouched.",
            found: true
        )
        let migrationFetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])), hadesHubCleanupOutcome: .success(confirmed))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = SettingsViewModel(
            discover: { await connections.provide() }, makeClient: { _ in FakeSettingsFetcher([]) },
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false), resourceGuards: FakeResourceGuardReading(),
            makeMigrationClient: { _ in migrationFetcher }
        )

        await viewModel.cleanHadesHub(confirmed: true)

        #expect(await migrationFetcher.cleanHadesHubCallCount == 1)
        #expect(await migrationFetcher.lastCleanHadesHubProceed == true)
        #expect(viewModel.hadesHubCleanup == confirmed)
    }
}
