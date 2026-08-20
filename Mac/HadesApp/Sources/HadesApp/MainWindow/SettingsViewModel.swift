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
///
/// **Also owns the two GLOBAL migration-cleanup actions, `cleanClaudeDesktopConfig` and
/// `cleanHadesHub`** (the per-item cleanup UI task's own addition, plus `cleanHadesHub` closing the
/// later spec #4 §1 gap where `~/.arcforge/hades-hub/launcher.js` was named among what v2 retires
/// but no cleanup method ever removed it) - deliberately here, not on `MigrationCleanupViewModel`,
/// which owns the other three `V12Cleanup` actions instead. `migrationCleanClaudeDesktopConfig`/
/// `migrationCleanHadesHub` carry no `productGuid` anywhere in their signatures (see
/// `ControlMigrationFetching`'s own doc comment), matching their routes: `claude_desktop_config.json`
/// and `~/.arcforge/hades-hub/` are both global and per-user, not per-project (spec #4 §5, §1) -
/// putting either anywhere reachable only through a selected project would misstate its scope.
/// Settings is exactly that non-project-scoped surface already (see this type's own class doc
/// comment on why it is not tied to any per-project state at all), so `claudeDesktopConfigCleanup`
/// AND `hadesHubCleanup` are both refreshed unconditionally on every `refresh()` call, the same as
/// `settings` itself. Like every other cleanup target, each dry-run preview (`proceed: false`) is
/// fetched here and rendered verbatim by `SettingsView`; only `cleanClaudeDesktopConfig(confirmed:)`/
/// `cleanHadesHub(confirmed:)` below ever call `proceed: true` - see `MigrationCleanupViewModel`'s
/// own doc comment for the full reasoning on why a dry run, not Swift-authored text, is where every
/// cleanup confirmation's wording comes from.
@MainActor
@Observable
public final class SettingsViewModel {
    public private(set) var settings: SettingsResult?
    public private(set) var launchAtLoginEnabled: Bool = false
    public private(set) var isLowPowerModeEnabled: Bool = false
    public private(set) var thermalState: ProcessInfo.ThermalState = .nominal

    /// The most recent dry-run preview OR real result of the global `cleanClaudeDesktopConfig`
    /// action - `nil` only before the first successful `refresh()`. `occurrencesFound == 0` (no
    /// "hades" entry, or no file at all) is `SettingsView`'s own "do not offer to clean a file that
    /// is not there" signal - see `MigrationClaudeDesktopConfigCleanupResult.occurrencesFound`'s own
    /// doc comment for why this route needs that field at all (it has no companion per-project
    /// detect endpoint the other three cleanup targets get from `MigrationDetectionResult`).
    public private(set) var claudeDesktopConfigCleanup: MigrationClaudeDesktopConfigCleanupResult?

    /// The most recent dry-run preview OR real result of the global `cleanHadesHub` action - `nil`
    /// only before the first successful `refresh()`. `found == false` (no `~/.arcforge/hades-hub/`
    /// directory at all) is `SettingsView`'s own "do not offer to clean something that is not
    /// there" signal - see `MigrationHadesHubCleanupResult.found`'s own doc comment for why this
    /// route needs that field at all (same reasoning as `claudeDesktopConfigCleanup` above: it has
    /// no companion per-project detect endpoint).
    public private(set) var hadesHubCleanup: MigrationHadesHubCleanupResult?

    private let discover: ConnectionProvider
    private let makeClient: SettingsClientFactory
    private let makeMigrationClient: MigrationClientFactory
    private let launchAtLogin: any LaunchAtLoginReading
    private let resourceGuards: any ResourceGuardReading

    public init(
        discover: @escaping ConnectionProvider = { Discovery.read() },
        makeClient: @escaping SettingsClientFactory = { ControlClient(connection: $0) },
        launchAtLogin: any LaunchAtLoginReading = LaunchAtLoginService(),
        resourceGuards: any ResourceGuardReading = ResourceGuardReader(),
        makeMigrationClient: @escaping MigrationClientFactory = { ControlClient(connection: $0) }
    ) {
        self.discover = discover
        self.makeClient = makeClient
        self.launchAtLogin = launchAtLogin
        self.resourceGuards = resourceGuards
        self.makeMigrationClient = makeMigrationClient
    }

    /// Reads the two OS facts (always, regardless of connectivity), then `GET /control/settings` and
    /// the global cleanup dry run, independently of each other. Called by
    /// `SettingsWindowController.show()` every time the Settings window opens. Either fetch failing
    /// leaves ITS OWN state exactly as it was - the same self-healing-next-refresh contract every
    /// other view model in this app already holds to: one unlucky fetch must not flash state already
    /// on screen back to empty, and must not be allowed to affect the OTHER, unrelated fetch either.
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

        do {
            claudeDesktopConfigCleanup = try await makeMigrationClient(connection).migrationCleanClaudeDesktopConfig(proceed: false)
        } catch {
            // Self-heals next refresh, independently of the settings fetch above.
        }

        do {
            hadesHubCleanup = try await makeMigrationClient(connection).migrationCleanHadesHub(proceed: false)
        } catch {
            // Self-heals next refresh, independently of the settings and claudeDesktopConfigCleanup
            // fetches above.
        }
    }

    /// Requests a launch-at-login change, then immediately re-reads `launchAtLoginEnabled` from the
    /// SAME OS source - never the requested value - so a request the OS refuses OR silently ignores
    /// can never display as on. See this type's own class doc comment and
    /// `LaunchAtLoginReading.setEnabled`'s own doc comment for exactly why both failure modes
    /// matter. The re-read-after-write behavior itself is `LaunchAtLoginReading.settingEnabled(to:)`
    /// - shared with `OnboardingViewModel.toggleLaunchAtLogin`, not reimplemented here.
    public func toggleLaunchAtLogin(to requested: Bool) {
        launchAtLoginEnabled = launchAtLogin.settingEnabled(to: requested)
    }

    /// The ONLY path that ever calls `migrationCleanClaudeDesktopConfig(proceed: true)`. `confirmed`
    /// is the actual gate - `false` never reaches the network, matching every other destructive
    /// action's own confirmation contract in this app (see `MigrationCleanupViewModel`'s own three
    /// cleanup methods for the identical shape).
    public func cleanClaudeDesktopConfig(confirmed: Bool) async {
        guard confirmed else { return }
        guard let connection = await discover() else { return }
        do {
            claudeDesktopConfigCleanup = try await makeMigrationClient(connection).migrationCleanClaudeDesktopConfig(proceed: true)
        } catch {
            // A thrown error here (staleToken/transport/decoding, or a .server with no message) has
            // nothing server-authored to show - self-heals, leaving the prior preview/result exactly
            // as it was, retryable. Unlike the per-project actions, there is no productGuid-scoped
            // "unknown project" failure mode this route can even hit.
        }
    }

    /// The ONLY path that ever calls `migrationCleanHadesHub(proceed: true)`. `confirmed` is the
    /// actual gate - `false` never reaches the network, matching `cleanClaudeDesktopConfig(confirmed:)`
    /// immediately above and every other destructive action's own confirmation contract in this app.
    public func cleanHadesHub(confirmed: Bool) async {
        guard confirmed else { return }
        guard let connection = await discover() else { return }
        do {
            hadesHubCleanup = try await makeMigrationClient(connection).migrationCleanHadesHub(proceed: true)
        } catch {
            // Self-heals, same reasoning as cleanClaudeDesktopConfig(confirmed:) above.
        }
    }
}
