import Foundation
import HadesControl
import Testing

@testable import HadesApp

/// `LiveMigrationOffering` is the real `MigrationOffering` conformance Plan 14 Task 10 adds -
/// `AppDelegate` no longer leaves this seam `nil` (see that type's own doc comment for the full
/// design and for why `performMigration(projectPath:)` only ever imports memory/traces, never
/// calls any of the four `V12Cleanup` cleanup routes).
///
/// Both `MigrationOffering` methods take a raw path, but every `/control/migration/*` route this
/// type calls is keyed by `{productGuid}` (matching every other per-project Control route). These
/// tests prove the one thing that gap requires: a fresh `GET /control/projects` lookup resolves
/// the path to a productGuid before anything else happens - and that failing to resolve one (an
/// unknown path, no reachable core) degrades to "do nothing" rather than throwing or guessing.
@Suite("LiveMigrationOffering")
struct LiveMigrationOfferingTests {
    static func row(path: String, productGuid: String) -> ProjectRow {
        ProjectRow(
            name: "P", path: path, productGuid: productGuid, unityVersion: nil,
            indexState: .indexed, indexStatus: "indexed 0s ago", nodeCount: 0, edgeCount: 0,
            editor: ProjectEditorInfo(
                state: .absent, status: "No Editor attached", unityVersion: nil, processId: nil,
                connectionAgeSeconds: nil),
            warnings: [])
    }

    static func detectionResult(isV12: Bool) -> MigrationDetectionResult {
        MigrationDetectionResult(
            projectRoot: "/tmp/p", isV12Project: isV12,
            manifestEntry: MigrationManifestEntryInfo(
                present: isV12, value: isV12 ? "file:/Users/mike/Projects/Hades" : nil, resolvedPath: nil),
            hasMemory: false, memoryDocumentCount: 0, hasTraces: false, hasGraph: false,
            hasGeneratedMcpConfig: false, claudeMd: MigrationClaudeMdInfo(shape: .absent), hasUnityPlugin: false)
    }

    // MARK: - isV12Project(projectPath:)

    @Test("isV12Project resolves the path to a productGuid via projects(), then calls migrationDetect with it")
    func isV12ProjectResolvesProductGuidThenDetects() async {
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [Self.row(path: "/tmp/v12-project", productGuid: "guid-1")])),
            detectOutcome: .success(Self.detectionResult(isV12: true)))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let offering = LiveMigrationOffering(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        let result = await offering.isV12Project(projectPath: "/tmp/v12-project")

        #expect(result == true)
        #expect(await fetcher.lastDetectedProductGuid == "guid-1")
    }

    @Test("isV12Project returns false, honestly, when detect reports the project is not v1.2")
    func isV12ProjectReturnsFalseWhenNotV12() async {
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [Self.row(path: "/tmp/p", productGuid: "guid-1")])),
            detectOutcome: .success(Self.detectionResult(isV12: false)))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let offering = LiveMigrationOffering(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        let result = await offering.isV12Project(projectPath: "/tmp/p")

        #expect(result == false)
    }

    @Test("isV12Project returns false, never crashes, when the path is not among known projects")
    func isV12ProjectReturnsFalseWhenPathUnknown() async {
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [Self.row(path: "/tmp/some-other-project", productGuid: "guid-1")])))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let offering = LiveMigrationOffering(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        let result = await offering.isV12Project(projectPath: "/tmp/not-known")

        #expect(result == false)
        #expect(await fetcher.detectCallCount == 0, "must never call detect for a path that resolved to nothing")
    }

    @Test("isV12Project returns false when no connection is discoverable")
    func isV12ProjectReturnsFalseWhenNoConnection() async {
        let offering = LiveMigrationOffering(
            discover: { nil }, makeClient: { _ in FakeMigrationFetcher(projectsOutcome: .failure(.staleToken)) })

        let result = await offering.isV12Project(projectPath: "/tmp/p")

        #expect(result == false)
    }

    @Test("isV12Project returns false when migrationDetect itself throws")
    func isV12ProjectReturnsFalseWhenDetectThrows() async {
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [Self.row(path: "/tmp/p", productGuid: "guid-1")])),
            detectOutcome: .failure(.transport(URLError(.notConnectedToInternet))))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let offering = LiveMigrationOffering(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        let result = await offering.isV12Project(projectPath: "/tmp/p")

        #expect(result == false)
    }

    // MARK: - performMigration(projectPath:)

    @Test("performMigration imports both memory and traces for the resolved productGuid - never cleanup")
    func performMigrationImportsMemoryAndTraces() async {
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [Self.row(path: "/tmp/p", productGuid: "guid-1")])),
            importMemoryOutcome: .success(MigrationMemoryImportResult(imported: ["conventions.md"], skipped: [])),
            importTracesOutcome: .success(MigrationTracesImportResult(imported: true, skippedReason: nil)))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let offering = LiveMigrationOffering(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await offering.performMigration(projectPath: "/tmp/p")

        #expect(await fetcher.importMemoryCallCount == 1)
        #expect(await fetcher.lastImportMemoryProductGuid == "guid-1")
        #expect(await fetcher.importTracesCallCount == 1)
        #expect(await fetcher.lastImportTracesProductGuid == "guid-1")
        // FakeMigrationFetcher conforms to ControlMigrationFetching, which has no cleanup methods
        // at all - there is structurally nothing here performMigration COULD call for cleanup.
    }

    @Test("performMigration still imports traces even when importing memory throws - the two are independent")
    func performMigrationTracesIndependentOfMemoryFailure() async {
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [Self.row(path: "/tmp/p", productGuid: "guid-1")])),
            importMemoryOutcome: .failure(.server(status: 500, message: "boom")),
            importTracesOutcome: .success(MigrationTracesImportResult(imported: true, skippedReason: nil)))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let offering = LiveMigrationOffering(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await offering.performMigration(projectPath: "/tmp/p")

        #expect(await fetcher.importTracesCallCount == 1)
    }

    @Test("performMigration still attempts memory import even when importing traces throws - the two are independent")
    func performMigrationMemoryIndependentOfTracesFailure() async {
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [Self.row(path: "/tmp/p", productGuid: "guid-1")])),
            importMemoryOutcome: .success(MigrationMemoryImportResult(imported: [], skipped: [])),
            importTracesOutcome: .failure(.server(status: 500, message: "boom")))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let offering = LiveMigrationOffering(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await offering.performMigration(projectPath: "/tmp/p")

        #expect(await fetcher.importMemoryCallCount == 1)
    }

    @Test("performMigration does nothing when the path cannot be resolved to a known project")
    func performMigrationDoesNothingWhenPathUnknown() async {
        let fetcher = FakeMigrationFetcher(projectsOutcome: .success(ProjectsResult(projects: [])))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let offering = LiveMigrationOffering(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await offering.performMigration(projectPath: "/tmp/unknown")

        #expect(await fetcher.importMemoryCallCount == 0)
        #expect(await fetcher.importTracesCallCount == 0)
    }

    @Test("performMigration does nothing when no connection is discoverable")
    func performMigrationDoesNothingWhenNoConnection() async {
        let fetcher = FakeMigrationFetcher(projectsOutcome: .failure(.staleToken))
        let offering = LiveMigrationOffering(discover: { nil }, makeClient: { _ in fetcher })

        await offering.performMigration(projectPath: "/tmp/p")

        #expect(await fetcher.projectsCallCount == 0, "discover() returning nil must short-circuit before any client call")
    }
}
