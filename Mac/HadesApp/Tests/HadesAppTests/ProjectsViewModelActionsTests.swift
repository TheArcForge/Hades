import Foundation
import HadesControl
import Testing

@testable import HadesApp

/// Task 4's six Projects actions - `addProject`, `removeProject`, `rebuildProject`,
/// `installPlugin`, `revealInFinder`, `openInUnity` - plus rebuild's operation-progress polling.
/// `ProjectsViewModelTests` (Task 3) already covers `refresh()`'s own fetch contract; this suite is
/// additive, covering only what Task 4 adds.
///
/// **Every action method takes a bare `productGuid: String` (`addProject` a bare `path: String`) -
/// never a whole `ProjectRow`.** That is not an oversight: it is the proof that none of these
/// methods COULD re-derive eligibility from `warnings`/`editor.state`/anything else even if it
/// wanted to, because the data is not in scope. Investigated for this task (see the Plan 13 Task 4
/// report): `GET /control/projects`'s `ProjectRow` carries no capability/eligibility/disabled field
/// for any of the six actions today, and `ProjectsEndpoint`'s C# implementation of `Remove`/`Rebuild`
/// never references `LeaseRegistry` at all (confirmed by reading `ProjectsEndpoint.cs` and
/// `ControlListener.cs`'s route table: `_leases` is wired only into `/control/summary` and
/// `/control/leases/{id}/release`). So "disabled with the API's own reason, never Swift re-deriving
/// eligibility" resolves, for today's wire shape, to: nothing is ever preemptively disabled: the
/// API's only mechanism for saying an action can't succeed is a `success: false` response AFTER the
/// attempt, rendered verbatim - see `revealInFinderDoesNotGateOnProjectWarnings` below.
@Suite("ProjectsViewModel actions")
@MainActor
struct ProjectsViewModelActionsTests {

    static func makeViewModel(fetcher: FakeProjectsFetcher) -> ProjectsViewModel {
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        return ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })
    }

    static let runningOperation = OperationResult(
        id: "op-1", kind: "rebuild", state: .running, startedAtUtc: "2026-08-05T08:39:05.172842+00:00",
        finishedAtUtc: nil, elapsedSeconds: 2, progress: nil, error: nil, result: nil
    )

    static let doneOperation = OperationResult(
        id: "op-1", kind: "rebuild", state: .done, startedAtUtc: "2026-08-05T08:39:05.172842+00:00",
        finishedAtUtc: "2026-08-05T08:39:05.516226+00:00", elapsedSeconds: 3, progress: nil, error: nil,
        result: .object([
            "nodesBefore": .int(494), "nodesAfter": .int(494),
            "message": .string("Rebuild complete \u{2014} 494 nodes (+0 from before)."),
        ])
    )

    // MARK: - addProject: the panel already chose the path, this just carries it

    @Test("addProject(path:) calls the API with exactly the chosen path")
    func addProjectCallsClientWithChosenPath() async {
        let added = ProjectRow(
            name: "SomeProject", path: "/Users/mike/Projects/SomeProject", productGuid: "new-guid",
            unityVersion: nil, indexState: .indexing, indexStatus: "not yet indexed", nodeCount: 0, edgeCount: 0,
            editor: ProjectEditorInfo(state: .absent, status: "No Editor attached", unityVersion: nil, processId: nil, connectionAgeSeconds: nil),
            warnings: []
        )
        // A successful add now re-reads `projects()` so the new row is visible without waiting for
        // an external tick - see `addProject`'s own doc comment - so the script must cover that read.
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: [added]))], addOutcome: .success(added))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.addProject(path: "/Users/mike/Projects/SomeProject")

        #expect(await fetcher.addCallCount == 1)
        #expect(await fetcher.lastAddedPath == "/Users/mike/Projects/SomeProject")
    }

    @Test("addProject(path:) failure renders the server's own message verbatim")
    func addProjectFailureRendersServerMessage() async {
        let fetcher = FakeProjectsFetcher([], addOutcome: .failure(.server(status: 400, message: "Path must not be blank.")))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.addProject(path: "")

        #expect(viewModel.lastActionMessage == "Path must not be blank.")
    }

    /// The bug this pins: `addProject` used to rely on "the next `refresh()` tick" for its feedback.
    /// The main window drives one; the ONBOARDING window drives none, so a successful add left
    /// "No Projects Yet" on screen and said nothing either way. The action now refreshes itself.
    @Test("addProject(path:) refreshes so the new row is visible without an external tick")
    func addProjectRefreshesSoTheNewRowAppears() async {
        let added = ProjectRow(
            name: "SomeProject", path: "/Users/mike/Projects/SomeProject", productGuid: "new-guid",
            unityVersion: nil, indexState: .indexing, indexStatus: "not yet indexed", nodeCount: 0, edgeCount: 0,
            editor: ProjectEditorInfo(state: .absent, status: "No Editor attached", unityVersion: nil, processId: nil, connectionAgeSeconds: nil),
            warnings: []
        )
        // The fetcher's own `projects()` returns the row, exactly as the server would after an add.
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: [added]))], addOutcome: .success(added))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        #expect(viewModel.projects.isEmpty)
        await viewModel.addProject(path: "/Users/mike/Projects/SomeProject")

        #expect(viewModel.projects.map { $0.productGuid } == ["new-guid"])
    }

    /// A stale failure must not outlive the success that replaced it - otherwise "not a Unity
    /// project" stays on screen directly above the row that just added fine.
    @Test("addProject(path:) success clears a previous failure's message")
    func addProjectSuccessClearsPreviousFailureMessage() async {
        let added = ProjectRow(
            name: "SomeProject", path: "/Users/mike/Projects/SomeProject", productGuid: "new-guid",
            unityVersion: nil, indexState: .indexing, indexStatus: "not yet indexed", nodeCount: 0, edgeCount: 0,
            editor: ProjectEditorInfo(state: .absent, status: "No Editor attached", unityVersion: nil, processId: nil, connectionAgeSeconds: nil),
            warnings: []
        )
        let fetcher = FakeProjectsFetcher(
            [.success(ProjectsResult(projects: [added]))],
            addOutcome: .failure(.server(status: 400, message: "Not a Unity project."))
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.addProject(path: "/tmp/not-unity")
        #expect(viewModel.lastActionMessage == "Not a Unity project.")

        await fetcher.setAddOutcome(.success(added))
        await viewModel.addProject(path: "/Users/mike/Projects/SomeProject")

        #expect(viewModel.lastActionMessage == nil)
    }

    // MARK: - removeProject: the confirmation gate is enforced here, not only in the dialog

    @Test("removeProject(confirmed: false) never calls the API")
    func removeWithoutConfirmationDoesNotCallAPI() async {
        let fetcher = FakeProjectsFetcher([], removeOutcome: .success(ActionResult(success: true, message: "removed")))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.removeProject(productGuid: "abc", confirmed: false)

        #expect(await fetcher.removeCallCount == 0)
        #expect(viewModel.lastActionMessage == nil)
    }

    @Test("removeProject(confirmed: true) calls the API and renders ActionResult.message verbatim")
    func removeWithConfirmationCallsAPIAndRendersMessage() async {
        // Verbatim from the live-captured projects_action_remove.json fixture (Task 1).
        let message =
            "fake-project-p13t1 removed from Hades. Nothing was deleted from disk \u{2014} the project itself, its indexed graph, and its authored memory all remain untouched."
        let fetcher = FakeProjectsFetcher([], removeOutcome: .success(ActionResult(success: true, message: message)))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.removeProject(productGuid: "abc", confirmed: true)

        #expect(await fetcher.removeCallCount == 1)
        #expect(await fetcher.lastRemovedProductGuid == "abc")
        #expect(viewModel.lastActionMessage == message)
    }

    @Test("removeProject failure (e.g. unknown project) renders the server's own message verbatim")
    func removeFailureRendersServerMessage() async {
        let fetcher = FakeProjectsFetcher([], removeOutcome: .failure(.server(status: 404, message: "Unknown project 'abc'.")))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.removeProject(productGuid: "abc", confirmed: true)

        #expect(viewModel.lastActionMessage == "Unknown project 'abc'.")
    }

    // MARK: - installPlugin / revealInFinder / openInUnity - structurally identical single
    // POST-then-render-message calls; each gets one success-path test here, and the shared
    // failure/self-heal behaviour is proven generically below rather than duplicated three times.

    @Test("installPlugin(productGuid:) renders InstallPluginResult.message verbatim")
    func installPluginRendersMessageVerbatim() async {
        let message = "Plugin installed. It will load automatically the next time this project opens in Unity."
        let fetcher = FakeProjectsFetcher([], installPluginOutcome: .success(InstallPluginResult(success: true, needsRestart: false, message: message)))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.installPlugin(productGuid: "abc")

        #expect(await fetcher.installPluginCallCount == 1)
        #expect(await fetcher.lastInstallPluginProductGuid == "abc")
        #expect(viewModel.lastActionMessage == message)
    }

    @Test(
        "revealInFinder(productGuid:) still calls the API even when the caller is acting on a project with a pathMissing warning - the API's own resolved success/message is the only eligibility answer, never a Swift guess from `warnings`"
    )
    func revealInFinderDoesNotGateOnProjectWarnings() async {
        // Verbatim from ProjectsEndpoint.PathMissingMessage, exactly what the real endpoint sends
        // for a project whose `warnings` already carry the `pathMissing` code. This method's own
        // signature (productGuid only) is what makes it structurally impossible for Swift to
        // short-circuit before making this call - there is no `warnings` in scope to inspect.
        let message = "Project path not found \u{2014} check that the volume is mounted or the drive is connected."
        let fetcher = FakeProjectsFetcher([], revealInFinderOutcome: .success(ActionResult(success: false, message: message)))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.revealInFinder(productGuid: "abc")

        #expect(await fetcher.revealInFinderCallCount == 1, "the call must still happen - the API decides eligibility, not Swift")
        #expect(viewModel.lastActionMessage == message)
    }

    @Test("openInUnity(productGuid:) renders ActionResult.message verbatim on failure")
    func openInUnityRendersFailureMessageVerbatim() async {
        // Verbatim from the live-captured projects_action_open_in_unity_not_found.json fixture
        // (Task 1) - a decoy Unity version guaranteed not to be installed.
        let message =
            "Unity 0000.0.0f1-p13t1-fixture-version-not-installed was not found at the default Unity Hub install location (/Applications/Unity/Hub/Editor/0000.0.0f1-p13t1-fixture-version-not-installed/Unity.app/Contents/MacOS/Unity). Open this project from Unity Hub instead."
        let fetcher = FakeProjectsFetcher([], openInUnityOutcome: .success(ActionResult(success: false, message: message)))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.openInUnity(productGuid: "abc")

        #expect(await fetcher.openInUnityCallCount == 1)
        #expect(await fetcher.lastOpenedProductGuid == "abc")
        #expect(viewModel.lastActionMessage == message)
    }

    // MARK: - Transport-level failures self-heal rather than clobbering the last good message -
    // same discipline `refresh()` already holds for `projects`, proven generically here for every
    // simple action (they all funnel through the same private message-recording helper).

    @Test("a .transport failure leaves lastActionMessage exactly as it was - no Swift-invented error text")
    func transportFailureSelfHealsWithoutClobberingLastActionMessage() async {
        let fetcher = FakeProjectsFetcher(
            [], revealInFinderOutcome: .success(ActionResult(success: true, message: "Revealed Hades-Unity-Client in Finder."))
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.revealInFinder(productGuid: "abc")
        #expect(viewModel.lastActionMessage == "Revealed Hades-Unity-Client in Finder.")

        await fetcher.setRevealInFinderOutcome(.failure(.transport(URLError(.timedOut))))
        await viewModel.revealInFinder(productGuid: "abc")

        #expect(
            viewModel.lastActionMessage == "Revealed Hades-Unity-Client in Finder.",
            "a transport failure must not clobber the last real message with nothing"
        )
    }

    // MARK: - rebuildProject + operation-progress polling

    @Test("rebuildProject(productGuid:) registers the returned operationId, but does not poll it itself - refresh() does, on the next tick")
    func rebuildRegistersOperationForTrackingWithoutPollingImmediately() async {
        let fetcher = FakeProjectsFetcher(
            [.success(ProjectsResult(projects: []))],
            rebuildOutcome: .success(RebuildStartedResult(operationId: "op-1")),
            operationScript: [.success(Self.runningOperation)]
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.rebuildProject(productGuid: "abc")
        #expect(viewModel.rebuildProgress["abc"] == nil, "nothing polled yet - rebuildProject itself never calls operation(id:)")
        #expect(await fetcher.operationCallCount == 0)

        await viewModel.refresh()

        #expect(viewModel.rebuildProgress["abc"] == .tracked(Self.runningOperation))
        #expect(await fetcher.lastRequestedOperationId == "op-1")
    }

    @Test("refresh() keeps polling a running operation across ticks, then stops once it reaches a terminal state")
    func refreshPollsUntilTerminalThenStops() async {
        let fetcher = FakeProjectsFetcher(
            [.success(ProjectsResult(projects: []))],
            rebuildOutcome: .success(RebuildStartedResult(operationId: "op-1")),
            operationScript: [.success(Self.runningOperation), .success(Self.doneOperation)]
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.rebuildProject(productGuid: "abc")

        await viewModel.refresh()
        #expect(viewModel.rebuildProgress["abc"] == .tracked(Self.runningOperation))

        await viewModel.refresh()
        #expect(viewModel.rebuildProgress["abc"] == .tracked(Self.doneOperation))

        let callCountAtDone = await fetcher.operationCallCount
        await viewModel.refresh()
        #expect(await fetcher.operationCallCount == callCountAtDone, "a terminal operation must stop being polled")
        #expect(viewModel.rebuildProgress["abc"] == .tracked(Self.doneOperation), "its last-known result stays frozen, not cleared")
    }

    @Test("an unknown operation id (404) becomes .pruned with the server's own message verbatim - NOT an error state")
    func unknownOperationIdBecomesPrunedNotError() async {
        let message = "Unknown operation 'op-1'. It may have completed and been pruned, or the id is wrong."
        let fetcher = FakeProjectsFetcher(
            [.success(ProjectsResult(projects: []))],
            rebuildOutcome: .success(RebuildStartedResult(operationId: "op-1")),
            operationScript: [.failure(.server(status: 404, message: message))]
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.rebuildProject(productGuid: "abc")

        await viewModel.refresh()

        #expect(viewModel.rebuildProgress["abc"] == .pruned(message: message))
    }

    @Test("once pruned, polling for that operation stops too - a 404 is terminal, not retried forever")
    func prunedOperationStopsBeingPolled() async {
        let message = "Unknown operation 'op-1'. It may have completed and been pruned, or the id is wrong."
        let fetcher = FakeProjectsFetcher(
            [.success(ProjectsResult(projects: []))],
            rebuildOutcome: .success(RebuildStartedResult(operationId: "op-1")),
            operationScript: [.failure(.server(status: 404, message: message))]
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.rebuildProject(productGuid: "abc")
        await viewModel.refresh()
        let callCountAfterPrune = await fetcher.operationCallCount

        await viewModel.refresh()

        #expect(await fetcher.operationCallCount == callCountAfterPrune)
        #expect(viewModel.rebuildProgress["abc"] == .pruned(message: message))
    }

    @Test("starting a new rebuild clears stale progress from a previous one")
    func startingNewRebuildClearsStaleProgress() async {
        let fetcher = FakeProjectsFetcher(
            [.success(ProjectsResult(projects: []))],
            rebuildOutcome: .success(RebuildStartedResult(operationId: "op-1")),
            operationScript: [.success(Self.doneOperation)]
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.rebuildProject(productGuid: "abc")
        await viewModel.refresh()
        #expect(viewModel.rebuildProgress["abc"] == .tracked(Self.doneOperation))

        await fetcher.setRebuildOutcome(.success(RebuildStartedResult(operationId: "op-2")))
        await viewModel.rebuildProject(productGuid: "abc")

        #expect(viewModel.rebuildProgress["abc"] == nil, "the stale .done from op-1 must not linger once op-2 starts")
    }

    @Test("rebuildProject(productGuid:) start failure renders the server's own message verbatim and tracks nothing")
    func rebuildStartFailureRendersMessageAndTracksNothing() async {
        let fetcher = FakeProjectsFetcher([], rebuildOutcome: .failure(.server(status: 404, message: "Unknown project 'abc'.")))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.rebuildProject(productGuid: "abc")

        #expect(viewModel.lastActionMessage == "Unknown project 'abc'.")
        #expect(viewModel.rebuildProgress["abc"] == nil)
    }
}
