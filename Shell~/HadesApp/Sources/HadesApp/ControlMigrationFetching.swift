import HadesControl

/// The narrow slice of `ControlClient` that migration consumers need - same "fetch plus act" shape
/// every other `Control*Fetching` protocol in this file group already establishes (see
/// `ControlProjectsFetching`'s own doc comment). Exists purely so tests can fake the control API
/// without a real `URLSession` round trip. `ControlClient` needed no changes to conform (empty
/// extension below): every one of these already matches this signature, typed throws included.
///
/// `projects()` lives here, not just in `ControlProjectsFetching`, for the same reason
/// `operation(id:)` lives directly in that protocol rather than a shared one: `MigrationOffering`'s
/// two methods (`isV12Project(projectPath:)`, `performMigration(projectPath:)`) both take a raw
/// path - the shape Plan 14 Task 6 already committed to - but every `/control/migration/*` route
/// this protocol backs is keyed by `{productGuid}`, matching every other per-project Control route.
/// `LiveMigrationOffering` bridges that gap with a fresh `GET /control/projects` lookup by path -
/// see that type's own doc comment.
///
/// **Now includes all five of `V12Cleanup`'s cleanup routes** - the per-item cleanup UI task's own
/// addition, plus `migrationCleanHadesHub` closing the later spec #4 §1 gap where
/// `~/.arcforge/hades-hub/launcher.js` was named among what v2 retires but no cleanup method ever
/// removed it. `MigrationCleanupViewModel` is the caller for `migrationCleanClaudeMd`/
/// `migrationCleanManifest`/`migrationCleanMcpConfig` (each keyed by `productGuid`, rendered inside
/// `ProjectDetailView`'s "v1.2 Cleanup" section); `SettingsViewModel` is the caller for
/// `migrationCleanClaudeDesktopConfig` AND `migrationCleanHadesHub` (neither has a `productGuid`
/// parameter to pass - see each method's own doc comment - both rendered inside `SettingsView`,
/// deliberately not a per-project surface). Each of the five is called with `proceed: false` first,
/// as a non-destructive dry run that returns the same `Message`/warning fields a real removal
/// would, then again with `proceed: true` only after the user has explicitly confirmed what that
/// dry run showed - see `MigrationCleanupViewModel`'s own doc comment for why that, not
/// Swift-authored warning text, is where every confirmation dialog's wording comes from.
public protocol ControlMigrationFetching: Sendable {
    func projects() async throws(ControlClientError) -> ProjectsResult

    /// `GET /control/migration/{productGuid}/detect`.
    func migrationDetect(productGuid: String) async throws(ControlClientError) -> MigrationDetectionResult

    /// `POST /control/migration/{productGuid}/importMemory`.
    func migrationImportMemory(productGuid: String) async throws(ControlClientError) -> MigrationMemoryImportResult

    /// `POST /control/migration/{productGuid}/importTraces`.
    func migrationImportTraces(productGuid: String) async throws(ControlClientError) -> MigrationTracesImportResult

    /// `POST /control/migration/{productGuid}/cleanClaudeMd`. `proceed: false` is a safe, real dry
    /// run - see `Hades.Core.Migration.V12Cleanup`'s own doc comment on why every cleanup method
    /// accepts it without writing anything.
    func migrationCleanClaudeMd(productGuid: String, proceed: Bool) async throws(ControlClientError) -> MigrationClaudeMdCleanupResult

    /// `POST /control/migration/{productGuid}/cleanManifest`.
    func migrationCleanManifest(productGuid: String, proceed: Bool) async throws(ControlClientError) -> MigrationManifestCleanupResult

    /// `POST /control/migration/{productGuid}/cleanMcpConfig`.
    func migrationCleanMcpConfig(productGuid: String, proceed: Bool) async throws(ControlClientError) -> MigrationMcpConfigCleanupResult

    /// `POST /control/migration/claudeDesktopConfig/clean` - deliberately carries no `productGuid`
    /// anywhere in its signature, matching the route itself: this file is global and per-user, not
    /// per-project (spec #4 §5), and there is structurally no argument through which a caller could
    /// make this act on a single project.
    func migrationCleanClaudeDesktopConfig(proceed: Bool) async throws(ControlClientError) -> MigrationClaudeDesktopConfigCleanupResult

    /// `POST /control/migration/hadesHub/clean` - the fifth `V12Cleanup` target, closing the spec
    /// #4 §1 gap where `~/.arcforge/hades-hub/launcher.js` (the retired v1.2 stdio launcher) was
    /// named among what v2 retires but no cleanup method ever removed it. Carries no `productGuid`
    /// anywhere in its signature either, for the identical reason
    /// `migrationCleanClaudeDesktopConfig(proceed:)` immediately above does not: `~/.arcforge/hades-hub/`
    /// is global and per-user, not per-project.
    func migrationCleanHadesHub(proceed: Bool) async throws(ControlClientError) -> MigrationHadesHubCleanupResult
}

extension ControlClient: ControlMigrationFetching {}
