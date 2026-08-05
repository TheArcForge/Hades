import Foundation
import HadesControl
import Testing

@testable import HadesApp

/// `ProjectsViewModel` owns the Projects section's fetch and published state - see that type's own
/// doc comment for the settled data-ownership split (`MainWindowViewModel` owns navigation/polling
/// LIFECYCLE only; each section owns its own fetch). Unlike `MenuBarViewModel`, there is no
/// state-machine "resolve" step to test here: `ProjectRow` already IS the API's fully-resolved
/// per-project shape (name, path, unityVersion, indexState/indexStatus, node/edge counts, editor
/// info, warnings), so `projects` is exactly `ProjectsResult.projects`, unchanged - the same
/// discipline `DTOs.swift`'s own doc comment describes ("Swift renders, .NET decides"). What IS
/// tested here, mirroring `MenuBarViewModelTests`' own standard: the fetch itself, that a failure
/// self-heals on the next `refresh()` rather than clearing or crashing, and one instance of every
/// state spec #3 §5 asks a Projects snapshot test to cover.
///
/// Fixture values below mirror the real captures in `HadesControlTests/Fixtures/projects_*.json`
/// (`projects_editor_attached.json`, `projects_with_warning.json`) wherever a live capture exists;
/// the `serializationMode` Mixed-mode warning text is copied verbatim from
/// `App~/src/Hades.Server/Control/ProjectsEndpoint.cs`'s own `BuildWarnings` (no live fixture
/// captured that specific warning, but the exact string is right there in the method that emits
/// it) - same "grounded in a real response shape, not invented" standard `MenuBarContentTests`
/// already holds to for its own uncaptured states.
///
/// **`.busy` here is NOT "lease held".** Spec #3 §5 lists "lease held" as one of the states this
/// view needs a snapshot test for. It turns out `GET /control/projects` cannot express that state
/// at all: `ProjectsEndpoint.BuildAsync` never receives a `LeaseRegistry` reference (only
/// `/control/summary` and the lease-release action do), so nothing reachable from `ProjectRow` -
/// including `editor.state == .busy` - carries a lease id / held-for / expiry / releasable, or is
/// even CAUSED by a lease being held (`Busy` is a main-thread-responsiveness probe timeout,
/// attributable to compiling, importing, or any long-running block - confirmed against
/// `Hades.Core.ProjectService.CharonStatus`/`GetCharonStatus`, which never references
/// `LeaseRegistry`). Rather than mislabel `.busy` as "lease held" - exactly the kind of unverified
/// assumption this project's own `m_SerializationMode` near-miss warns against - `busy` below tests
/// `.busy` honestly, as itself; the missing state is reported as a genuine API gap, not faked here.
@Suite("ProjectsViewModel")
@MainActor
struct ProjectsViewModelTests {

    // MARK: - Fixtures

    static let attachedProject = ProjectRow(
        name: "Hades-Unity-Client",
        path: "/Users/mike/Projects/Hades-Unity-Client",
        productGuid: "15c012f27331e49229cef25e74537816",
        unityVersion: "6000.3.2f1",
        indexState: .indexed,
        indexStatus: "indexed 4m ago",
        nodeCount: 494,
        edgeCount: 332,
        editor: ProjectEditorInfo(
            state: .attached, status: "Editor attached", unityVersion: "6000.3.2f1",
            processId: 54321, connectionAgeSeconds: 180
        ),
        warnings: []
    )

    static let indexingProject = ProjectRow(
        name: "Hades-Unity-Client",
        path: "/Users/mike/Projects/Hades-Unity-Client",
        productGuid: "15c012f27331e49229cef25e74537816",
        unityVersion: nil,
        indexState: .indexing,
        indexStatus: "not yet indexed",
        nodeCount: 0,
        edgeCount: 0,
        editor: ProjectEditorInfo(
            state: .absent, status: "No Editor attached", unityVersion: nil, processId: nil, connectionAgeSeconds: nil
        ),
        warnings: []
    )

    static let busyProject = ProjectRow(
        name: "Hades-Unity-Client",
        path: "/Users/mike/Projects/Hades-Unity-Client",
        productGuid: "15c012f27331e49229cef25e74537816",
        unityVersion: "6000.3.2f1",
        indexState: .indexed,
        indexStatus: "indexed 4m ago",
        nodeCount: 494,
        edgeCount: 332,
        editor: ProjectEditorInfo(
            state: .busy, status: "Editor attached (busy)", unityVersion: "6000.3.2f1",
            processId: 54321, connectionAgeSeconds: 12
        ),
        warnings: []
    )

    /// Verbatim from the real captured `projects_with_warning.json` fixture.
    static let pathMissingWarning = ProjectWarning(
        code: "pathMissing",
        severity: .error,
        message: "Project path not found \u{2014} check that the volume is mounted or the drive is connected.",
        remedy: "Reconnect the volume, or remove this project from Hades if it no longer exists."
    )

    /// Verbatim from `ProjectsEndpoint.BuildWarnings`'s Mixed-mode branch - see this suite's own
    /// doc comment for why there is no live-captured fixture for this specific warning yet.
    static let mixedSerializationWarning = ProjectWarning(
        code: "serializationMode",
        severity: .warning,
        message:
            "Asset serialization is set to Mixed. Hades reads Unity's YAML directly from disk, so any asset serialized as binary under this mode is invisible to the graph \u{2014} the graph may be silently incomplete.",
        remedy: "In Unity: Edit \u{2192} Project Settings \u{2192} Editor \u{2192} Asset Serialization \u{2192} Mode \u{2192} Force Text."
    )

    static let errorWarningProject = ProjectRow(
        name: "OldExternalDrive-Project",
        path: "/Volumes/External/OldExternalDrive-Project",
        productGuid: "deadbeefdeadbeefdeadbeefdeadbeef",
        unityVersion: nil,
        indexState: .indexed,
        indexStatus: "indexed 2d ago",
        nodeCount: 210,
        edgeCount: 150,
        editor: ProjectEditorInfo(
            state: .absent, status: "No Editor attached", unityVersion: nil, processId: nil, connectionAgeSeconds: nil
        ),
        warnings: [pathMissingWarning]
    )

    static let warningsPresentProject = ProjectRow(
        name: "Hades-Unity-Client",
        path: "/Users/mike/Projects/Hades-Unity-Client",
        productGuid: "15c012f27331e49229cef25e74537816",
        unityVersion: "6000.3.2f1",
        indexState: .indexed,
        indexStatus: "indexed 4m ago",
        nodeCount: 494,
        edgeCount: 332,
        editor: ProjectEditorInfo(
            state: .absent, status: "No Editor attached", unityVersion: nil, processId: nil, connectionAgeSeconds: nil
        ),
        warnings: [mixedSerializationWarning]
    )

    // MARK: - Initial state

    @Test("starts with no projects before any refresh")
    func startsEmpty() {
        let viewModel = ProjectsViewModel(discover: { nil }, makeClient: { _ in FakeProjectsFetcher([]) })
        #expect(viewModel.projects.isEmpty)
    }

    // MARK: - Fetch

    @Test("refresh() populates projects verbatim from a successful fetch - the whole ProjectRow, unchanged")
    func refreshPopulatesProjectsVerbatim() async {
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: [Self.attachedProject]))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.projects == [Self.attachedProject])
        #expect(await fetcher.projectsCallCount == 1)
    }

    @Test("a later refresh() REPLACES projects with the new fetch, never appends to the old one")
    func refreshReplacesNotAppends() async {
        let fetcher = FakeProjectsFetcher([
            .success(ProjectsResult(projects: [Self.attachedProject])),
            .success(ProjectsResult(projects: [])),
        ])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()
        #expect(viewModel.projects == [Self.attachedProject])

        await viewModel.refresh()
        #expect(viewModel.projects.isEmpty)
    }

    // MARK: - Error handling: every failure self-heals on the next refresh(), same contract as
    // MenuBarViewModel.tick() - a project list already on screen must not flash empty because of
    // one unlucky poll.

    @Test("discover() returning nil leaves projects exactly as they were, and never attempts a fetch")
    func discoveryUnavailableLeavesProjectsUnchanged() async {
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: [Self.attachedProject]))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")])  // exhausts after one call
        let viewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()  // succeeds, connection consumed
        #expect(viewModel.projects == [Self.attachedProject])

        await viewModel.refresh()  // discover() now returns nil
        #expect(viewModel.projects == [Self.attachedProject], "one unlucky discovery read must not clear existing data")
        #expect(await fetcher.projectsCallCount == 1, "a fetch is never attempted without a connection")
    }

    @Test("a .staleToken fetch failure leaves projects unchanged and self-heals on the next refresh")
    func staleTokenSelfHeals() async {
        let connection = ControlConnection(port: 1, token: "t")
        let fetcher = FakeProjectsFetcher([.failure(.staleToken), .success(ProjectsResult(projects: [Self.attachedProject]))])
        let connections = FakeConnectionProvider([connection], repeatLast: true)
        let viewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()
        #expect(viewModel.projects.isEmpty)

        await viewModel.refresh()
        #expect(viewModel.projects == [Self.attachedProject])
    }

    @Test("a .transport fetch failure (core briefly unreachable) also self-heals without becoming an error state")
    func transportErrorSelfHeals() async {
        let connection = ControlConnection(port: 1, token: "t")
        let fetcher = FakeProjectsFetcher([
            .failure(.transport(URLError(.timedOut))),
            .success(ProjectsResult(projects: [Self.attachedProject])),
        ])
        let connections = FakeConnectionProvider([connection], repeatLast: true)
        let viewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()
        #expect(viewModel.projects.isEmpty)

        await viewModel.refresh()
        #expect(viewModel.projects == [Self.attachedProject])
    }

    @Test("an existing project list survives a later failure - not just an empty one")
    func failureAfterSuccessLeavesPriorDataInPlace() async {
        let connection = ControlConnection(port: 1, token: "t")
        let fetcher = FakeProjectsFetcher([
            .success(ProjectsResult(projects: [Self.attachedProject])),
            .failure(.staleToken),
        ])
        let connections = FakeConnectionProvider([connection], repeatLast: true)
        let viewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()
        #expect(viewModel.projects == [Self.attachedProject])

        await viewModel.refresh()  // fails
        #expect(viewModel.projects == [Self.attachedProject], "a failed refresh must not clear a project list already on screen")
    }

    // MARK: - Per-state coverage, spec #3 §5

    @Test("no projects: an empty result is a legitimate, valid state - not an error")
    func noProjects() async {
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: []))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.projects.isEmpty)
    }

    @Test("indexing: a project that has never completed an index in this process")
    func indexing() async {
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: [Self.indexingProject]))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.projects == [Self.indexingProject])
        #expect(viewModel.projects[0].indexState == .indexing)
    }

    @Test("attached: a project with an idle, attached Editor")
    func attached() async {
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: [Self.attachedProject]))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.projects == [Self.attachedProject])
        #expect(viewModel.projects[0].editor.state == .attached)
    }

    @Test(
        "busy: an attached Editor whose main thread has not answered a probe - see this suite's own doc comment for why this is NOT 'lease held'"
    )
    func busy() async {
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: [Self.busyProject]))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.projects == [Self.busyProject])
        #expect(viewModel.projects[0].editor.state == .busy)
    }

    @Test("error: a project carrying an error-severity warning (path missing)")
    func errorSeverityWarning() async {
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: [Self.errorWarningProject]))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.projects == [Self.errorWarningProject])
        #expect(viewModel.projects[0].warnings.map(\.severity) == [.error])
    }

    @Test("warnings present: a project carrying a warning-severity warning (Mixed serialization)")
    func warningsPresent() async {
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: [Self.warningsPresentProject]))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.projects == [Self.warningsPresentProject])
        #expect(viewModel.projects[0].warnings.map(\.severity) == [.warning])
    }
}
