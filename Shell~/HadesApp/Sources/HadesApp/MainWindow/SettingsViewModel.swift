import Foundation
import HadesControl
import Observation

/// Builds a `ControlSettingsFetching` for a given connection (normally `ControlClient.init`) - the
/// Settings-view analogue of `ProjectsClientFactory`/`TracesClientFactory`/`MemoryClientFactory`.
public typealias SettingsClientFactory = @Sendable (ControlConnection) -> any ControlSettingsFetching

/// Owns the Settings surface's own fetch and OS-fact reads - nothing else. Per the settled
/// data-ownership split every other section view model already follows (`ProjectsViewModel`'s own
/// doc comment, `TracesViewModel`'s and `MemoryViewModel`'s after it), this type owns its own fetch
/// rather than growing `MainWindowViewModel`.
///
/// **Settings is not tied to `MainWindowViewModel`'s poll lifecycle at all.** Spec #3 §3.5: a
/// standard macOS Settings scene, reachable by Cmd-, - see `Section`'s own doc comment for why it is
/// deliberately not a sidebar destination. There is therefore no "currently selected section" tick
/// to hook into; `refresh()` is called once by `SettingsWindowController.show()` every time the
/// Settings window opens (including reopens), never on a repeating timer - a closed Settings window,
/// like a closed main window, has no business polling.
///
/// **The carve-out, applied.** `launchAtLoginEnabled`/`isLowPowerModeEnabled`/`thermalState` come
/// from the injected `LaunchAtLoginReading`/`ResourceGuardReading` - OS facts about the shell's own
/// process and machine, never from the control API (see `Hades.Server.Control.SettingsEndpoint`'s
/// own class doc comment for why .NET stopped reporting either). `refresh()` reads both
/// UNCONDITIONALLY, before even attempting `discover()` - they need no running core and no network at
/// all, so a Settings window opened while Hades itself is unreachable still shows an honest
/// launch-at-login/resource-guard state, even though `settings` (the control-API half) stays
/// whatever it last was.
///
/// **`toggleLaunchAtLogin(to:)` never trusts the request it just made.** Per this plan's own explicit
/// requirement, it always re-reads `launchAtLogin.isEnabled` - the OS's own answer - after calling
/// `setEnabled`, regardless of whether that call threw. See `LaunchAtLoginReading.setEnabled`'s own
/// doc comment for why a thrown error is not the only failure mode this must guard against (the OS
/// can also silently no-op).
@MainActor
@Observable
public final class SettingsViewModel {
    public private(set) var settings: SettingsResult?
    public private(set) var launchAtLoginEnabled: Bool = false
    public private(set) var isLowPowerModeEnabled: Bool = false
    public private(set) var thermalState: ProcessInfo.ThermalState = .nominal

    private let discover: ConnectionProvider
    private let makeClient: SettingsClientFactory
    private let launchAtLogin: any LaunchAtLoginReading
    private let resourceGuards: any ResourceGuardReading

    public init(
        discover: @escaping ConnectionProvider = { Discovery.read() },
        makeClient: @escaping SettingsClientFactory = { ControlClient(connection: $0) },
        launchAtLogin: any LaunchAtLoginReading = LaunchAtLoginService(),
        resourceGuards: any ResourceGuardReading = ResourceGuardReader()
    ) {
        self.discover = discover
        self.makeClient = makeClient
        self.launchAtLogin = launchAtLogin
        self.resourceGuards = resourceGuards
    }

    /// Reads the two OS facts (always, regardless of connectivity), then `GET /control/settings`.
    /// Called by `SettingsWindowController.show()` every time the Settings window opens. A fetch
    /// failure (`discover()` returning nil, or the client throwing) leaves `settings` exactly as it
    /// was - the same self-healing-next-refresh contract every other view model in this app already
    /// holds to: one unlucky fetch must not flash settings already on screen back to empty.
    public func refresh() async {
        launchAtLoginEnabled = launchAtLogin.isEnabled
        isLowPowerModeEnabled = resourceGuards.isLowPowerModeEnabled
        thermalState = resourceGuards.thermalState

        guard let connection = await discover() else { return }
        do {
            settings = try await makeClient(connection).settings()
        } catch {
            // Self-heals next refresh - see this method's own doc comment. Nothing to do here.
        }
    }

    /// Requests a launch-at-login change, then immediately re-reads `launchAtLoginEnabled` from the
    /// SAME OS source - never the requested value - so a request the OS refuses OR silently ignores
    /// can never display as on. See this type's own class doc comment and
    /// `LaunchAtLoginReading.setEnabled`'s own doc comment for exactly why both failure modes matter.
    public func toggleLaunchAtLogin(to requested: Bool) {
        try? launchAtLogin.setEnabled(requested)
        launchAtLoginEnabled = launchAtLogin.isEnabled
    }
}
