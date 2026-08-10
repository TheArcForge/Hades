import HadesControl

/// Builds a `ControlMigrationFetching` for a given connection (normally `ControlClient.init`) -
/// the migration analogue of `ProjectsClientFactory`.
public typealias MigrationClientFactory = @Sendable (ControlConnection) -> any ControlMigrationFetching

/// The real `MigrationOffering` conformance (Plan 14 Task 10) - the control API now has a real
/// `/control/migration/*` surface (see `Hades.Server.Control.MigrationEndpoint`) to wire this seam
/// to, so `AppDelegate` no longer has to leave it `nil` the way `OnboardingViewModel.migrationOffering`'s
/// own doc comment describes Task 6 leaving it.
///
/// **Bridging path to productGuid.** `isV12Project(projectPath:)`/`performMigration(projectPath:)`
/// both take a raw path - the shape Task 6 already committed to, and this task does not change -
/// but every `/control/migration/*` route is keyed by `{productGuid}`, matching every other
/// per-project Control route (`/control/projects/{id}`, `/control/editors/...`). `resolveProductGuid`
/// below is the one place that gap is bridged: a fresh `GET /control/projects` lookup by path.
/// Safe to rely on here specifically because `OnboardingViewModel.addProject(path:)` always calls
/// `ProjectsViewModel.addProject(path:)` - which fully awaits `POST /control/projects/add`, itself
/// synchronously adopting and indexing the project - BEFORE ever checking `isV12Project`, so by the
/// time either method below runs, the project this path names is always already known. A path that
/// somehow is not found (a future caller of this same conformance that skips that ordering) degrades
/// to "not a v1.2 project" / "nothing to migrate" rather than guessing or crashing - the same
/// self-heal discipline `ProjectsViewModel.refresh()` already holds for a failed fetch.
///
/// **`performMigration(projectPath:)` imports memory and traces ONLY - it never calls any of
/// `V12Cleanup`'s four cleanup routes.** `MigrationEndpoint`/`V12Cleanup` require an explicit,
/// independent `proceed` for each cleanup step precisely so a single gesture cannot fire all four
/// at once (see `MigrationEndpoint`'s own class doc comment: "there is no CleanupAll route either").
/// The onboarding banner's one generic confirmation - `OnboardingProjectsStepView`'s "Hades can
/// migrate it into this app. Nothing changes until you confirm." - is honest authorization for
/// IMPORTING (memory is mandatory-safe per spec #4 §5, "Optional? No"; traces is optional but
/// equally non-destructive - neither can lose data, so neither needs its own confirmation). It was
/// never written to warn about deleting `Packages/manifest.json`'s entry, rewriting `CLAUDE.md`,
/// deleting `.mcp.json`, or editing the GLOBAL `claude_desktop_config.json` - so this conformance
/// does not perform any of those under that same click. Every cleanup route is real, independently
/// tested (`Hades.Server.Tests.Control.MigrationEndpointHttpTests`), and already callable from
/// `ControlClient` directly (see that type's own "Migration" section) - wiring a per-item
/// confirmation UI for them is follow-up work, not a silent gap; see this task's own final report.
public struct LiveMigrationOffering: MigrationOffering {
    private let discover: ConnectionProvider
    private let makeClient: MigrationClientFactory

    public init(
        discover: @escaping ConnectionProvider = { Discovery.read() },
        makeClient: @escaping MigrationClientFactory = { ControlClient(connection: $0) }
    ) {
        self.discover = discover
        self.makeClient = makeClient
    }

    public func isV12Project(projectPath: String) async -> Bool {
        guard let connection = await discover() else { return false }
        let client = makeClient(connection)
        guard let productGuid = await resolveProductGuid(forPath: projectPath, using: client) else { return false }

        do {
            return try await client.migrationDetect(productGuid: productGuid).isV12Project
        } catch {
            return false
        }
    }

    public func performMigration(projectPath: String) async {
        guard let connection = await discover() else { return }
        let client = makeClient(connection)
        guard let productGuid = await resolveProductGuid(forPath: projectPath, using: client) else { return }

        // Both independent, both attempted even if the other fails - see this type's own doc
        // comment. Neither result is surfaced anywhere today: `MigrationOffering.performMigration`
        // returns Void by design (Task 6) - "Swift never looks inside".
        _ = try? await client.migrationImportMemory(productGuid: productGuid)
        _ = try? await client.migrationImportTraces(productGuid: productGuid)
    }

    private func resolveProductGuid(forPath path: String, using client: any ControlMigrationFetching) async -> String? {
        guard let projects = try? await client.projects().projects else { return nil }
        return projects.first(where: { $0.path == path })?.productGuid
    }
}
