import Foundation
import HadesControl
import Testing

@testable import HadesApp

/// `MemoryViewModel` owns the Memory section's fetch and published state - the Task 6 analogue of
/// `ProjectsViewModel` (Task 3) / `TracesViewModel` (Task 5), same settled data-ownership split
/// (`MainWindowViewModel` owns navigation/polling LIFECYCLE only; each section owns its own fetch).
/// `refresh()` drives ONE endpoint (`GET /control/memory` - documents AND the proposal queue
/// together, see `Hades.Server.Control.MemoryEndpoint`'s own class doc comment: "spec #3 §3.4 is one
/// shell view showing both at once"). `selectDocument(name:)` is a second, user-initiated fetch
/// (`GET /control/memory/document`), never polled on a timer - the same shape
/// `TracesViewModel.selectTrace(traceId:)` already established for `selectedTraceDetail`.
///
/// Fixture values below mirror the real captures in `HadesControlTests/Fixtures/memory_*.json`
/// (Task 1) - same "grounded in a real response shape, not invented" standard
/// `TracesViewModelTests`/`ProjectsViewModelTests` already hold to. `realPendingProposal` and
/// `realInferredProposal` are deliberately different `status` values from the SAME real payload
/// (`memory_populated.json`) - proof that this view model passes `status` through unswitched,
/// whatever string it happens to be, rather than branching on a closed set of known values (see
/// `MemoryProposalRow`'s own doc comment on why `status` is never a closed enum).
@Suite("MemoryViewModel")
@MainActor
struct MemoryViewModelTests {

    // MARK: - Fixtures (verbatim from memory_populated.json / memory_document.json / memory_action_*.json)

    static let realDocument = MemoryDocumentRow(
        name: "conventions.md", sizeBytes: 191, sizeDisplay: "191 B", lastReviewed: "2026-05-12"
    )

    static let realDocumentNoFrontmatter = MemoryDocumentRow(
        name: "p13t1-no-frontmatter.md", sizeBytes: 70, sizeDisplay: "70 B", lastReviewed: nil
    )

    static let realPendingProposal = MemoryProposalRow(
        fileName: "convention-render_pipeline.md", targetFile: "conventions",
        createdAtUtc: "2026-07-09T09:19:21.17006+00:00", createdAgo: "27d ago",
        rationale: "RenderPipelineAsset pipeline_type = UniversalRenderPipelineAsset. (confidence 95 %)",
        status: "pending",
        content:
            "Targets URP (Universal Render Pipeline).\n\n_Evidence: RenderPipelineAsset pipeline_type = UniversalRenderPipelineAsset._\n<!-- hades-convention:render_pipeline -->"
    )

    static let realInferredProposal = MemoryProposalRow(
        fileName: "topic_cluster-b798e65931d6785f.md", targetFile: "", createdAtUtc: nil, createdAgo: nil,
        rationale: "", status: "inferred",
        content:
            "\nINFERRED PATTERN (not confirmed by team)\n\nFrequent topic: memory, validate (appeared in 483 of 2245 traces)\n\nObserved in 483 traces with 22% confidence.\n"
    )

    static let realDocumentContent = MemoryDocumentResult(
        name: "p13t1-fixture-conventions.md",
        content: "---\nlast_reviewed: 2026-08-05\n---\n# P13T1 Fixture Conventions\n\nWritten during Plan 13 Task 1 live fixture capture.\n"
    )

    /// Two known projects - the fixture shape the Plan 13 Task 8 re-run's whole finding turns on: see
    /// `TracesViewModelTests.projectAlpha`'s own doc comment for why every test above this point
    /// (single project only) could never have caught the ambiguous-project defect.
    static let projectAlpha = ProjectRow(
        name: "Alpha", path: "/Users/mike/Projects/Alpha", productGuid: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        unityVersion: "6000.3.2f1", indexState: .indexed, indexStatus: "indexed 1m ago", nodeCount: 10, edgeCount: 5,
        editor: ProjectEditorInfo(state: .absent, status: "No Editor attached", unityVersion: nil, processId: nil, connectionAgeSeconds: nil),
        warnings: []
    )
    static let projectBeta = ProjectRow(
        name: "Beta", path: "/Users/mike/Projects/Beta", productGuid: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        unityVersion: "6000.3.2f1", indexState: .indexed, indexStatus: "indexed 2m ago", nodeCount: 20, edgeCount: 8,
        editor: ProjectEditorInfo(state: .absent, status: "No Editor attached", unityVersion: nil, processId: nil, connectionAgeSeconds: nil),
        warnings: []
    )

    /// Verbatim shape of `Hades.Core.Projects.ProjectResolver.Resolve`'s own ambiguous-project
    /// message - see `TracesViewModelTests.ambiguousProjectMessage`'s own doc comment for why this is
    /// read directly from that method rather than a live two-project fixture capture.
    static let ambiguousProjectMessage =
        "Hades knows 2 projects, so this call needs a 'project' argument. Known projects: Alpha (aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa); Beta (bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb). Call hades_status for details."

    static func makeViewModel(fetcher: FakeMemoryFetcher) -> MemoryViewModel {
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        return MemoryViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })
    }

    // MARK: - Initial state

    @Test("starts with nothing before any refresh - no documents, proposals, or selected document")
    func startsEmpty() {
        let viewModel = MemoryViewModel(discover: { nil }, makeClient: { _ in FakeMemoryFetcher() })
        #expect(viewModel.documents.isEmpty)
        #expect(viewModel.proposals.isEmpty)
        #expect(viewModel.selectedDocument == .notSelected)
        #expect(viewModel.lastActionMessage == nil)
        #expect(viewModel.knownProjects.isEmpty)
        #expect(viewModel.refreshError == nil)
    }

    // MARK: - refresh(): documents + proposals together, one endpoint

    @Test("refresh() populates documents and proposals verbatim - every status value passes through unswitched")
    func refreshPopulatesDocumentsAndProposalsVerbatim() async {
        let fetcher = FakeMemoryFetcher([
            .success(
                MemoryResult(
                    documents: [Self.realDocument, Self.realDocumentNoFrontmatter],
                    proposals: [Self.realPendingProposal, Self.realInferredProposal]))
        ])
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.refresh()

        #expect(viewModel.documents == [Self.realDocument, Self.realDocumentNoFrontmatter])
        #expect(viewModel.proposals == [Self.realPendingProposal, Self.realInferredProposal])
        #expect(await fetcher.memoryCallCount == 1)
    }

    @Test("an empty result is a legitimate, ordinary state - not an error - the same as a fresh project with no memory yet")
    func emptyResultIsOrdinaryNotAnError() async {
        let fetcher = FakeMemoryFetcher()  // default: empty documents/proposals
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.refresh()

        #expect(viewModel.documents.isEmpty)
        #expect(viewModel.proposals.isEmpty)
    }

    @Test("discover() returning nil leaves state exactly as it was, and never attempts a fetch")
    func discoveryUnavailableLeavesStateUnchanged() async {
        let fetcher = FakeMemoryFetcher([.success(MemoryResult(documents: [Self.realDocument], proposals: []))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")])  // exhausts after one call
        let viewModel = MemoryViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()
        #expect(viewModel.documents == [Self.realDocument])

        await viewModel.refresh()  // discover() now returns nil
        #expect(viewModel.documents == [Self.realDocument], "one unlucky discovery read must not clear existing data")
        #expect(await fetcher.memoryCallCount == 1, "a fetch is never attempted without a connection")
    }

    @Test("a failed refresh self-heals: existing documents/proposals survive, next refresh repopulates")
    func failedRefreshSelfHeals() async {
        let fetcher = FakeMemoryFetcher([
            .success(MemoryResult(documents: [Self.realDocument], proposals: [Self.realPendingProposal])),
            .failure(.staleToken),
        ])
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.refresh()
        #expect(viewModel.documents == [Self.realDocument])

        await viewModel.refresh()  // fails
        #expect(viewModel.documents == [Self.realDocument], "a failed refresh must not clear data already on screen")
        #expect(viewModel.proposals == [Self.realPendingProposal])
    }

    // MARK: - knownProjects: populates the project Picker (Plan 13 Task 8 re-run, requirement B) -
    // Memory had NO project-filter UI at all before this fix, unlike Traces' pre-existing free-text
    // field - see this suite's own file-level doc comment.

    @Test("refresh() populates knownProjects verbatim from GET /control/projects")
    func refreshPopulatesKnownProjectsVerbatim() async {
        let fetcher = FakeMemoryFetcher(projectsScript: [.success(ProjectsResult(projects: [Self.projectAlpha, Self.projectBeta]))])
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.refresh()

        #expect(viewModel.knownProjects == [Self.projectAlpha, Self.projectBeta])
    }

    @Test("a failed knownProjects fetch self-heals: the previous list survives, exactly like every other fetch in this type")
    func knownProjectsFetchSelfHeals() async {
        let fetcher = FakeMemoryFetcher(projectsScript: [
            .success(ProjectsResult(projects: [Self.projectAlpha, Self.projectBeta])),
            .failure(.staleToken),
        ])
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.refresh()
        #expect(viewModel.knownProjects == [Self.projectAlpha, Self.projectBeta])

        await viewModel.refresh()

        #expect(viewModel.knownProjects == [Self.projectAlpha, Self.projectBeta], "one unlucky projects() poll must not clear the picker's own list")
    }

    // MARK: - Default project selection when ambiguous (requirement C)

    @Test("refresh() defaults projectFilter to the first known project when nothing has been explicitly chosen and more than one project exists")
    func refreshDefaultsToFirstKnownProjectWhenAmbiguous() async {
        let fetcher = FakeMemoryFetcher(projectsScript: [.success(ProjectsResult(projects: [Self.projectAlpha, Self.projectBeta]))])
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.refresh()

        #expect(viewModel.projectFilter == Self.projectAlpha.productGuid)
        #expect(
            await fetcher.lastMemoryProject == Self.projectAlpha.productGuid,
            "the resolved default must actually reach GET /control/memory, not just sit unused in projectFilter")
    }

    @Test("with exactly one known project, refresh() still resolves it with no interaction - today's single-project behaviour, unchanged")
    func singleKnownProjectStillWorksWithNoInteraction() async {
        let fetcher = FakeMemoryFetcher(
            [.success(MemoryResult(documents: [Self.realDocument], proposals: []))],
            projectsScript: [.success(ProjectsResult(projects: [Self.projectAlpha]))]
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.refresh()

        #expect(viewModel.documents == [Self.realDocument])
        #expect(viewModel.projectFilter == Self.projectAlpha.productGuid)
    }

    @Test("selectProject(_:) sets projectFilter and immediately re-fetches with the new project, without waiting for the next tick")
    func selectProjectImmediatelyRefetches() async {
        let fetcher = FakeMemoryFetcher(projectsScript: [.success(ProjectsResult(projects: [Self.projectAlpha, Self.projectBeta]))])
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.refresh()
        #expect(viewModel.projectFilter == Self.projectAlpha.productGuid, "defaulted to Alpha first")
        #expect(await fetcher.memoryCallCount == 1)

        await viewModel.selectProject(Self.projectBeta.productGuid)

        #expect(viewModel.projectFilter == Self.projectBeta.productGuid)
        #expect(await fetcher.lastMemoryProject == Self.projectBeta.productGuid)
        #expect(await fetcher.memoryCallCount == 2, "selecting a project re-fetches immediately, not just on the next tick")
    }

    @Test("an explicitly chosen project is never overridden by the first-known-project default on a later refresh")
    func explicitSelectionSurvivesLaterRefresh() async {
        let fetcher = FakeMemoryFetcher(projectsScript: [.success(ProjectsResult(projects: [Self.projectAlpha, Self.projectBeta]))])
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.selectProject(Self.projectBeta.productGuid)

        await viewModel.refresh()  // the ~1Hz tick path

        #expect(viewModel.projectFilter == Self.projectBeta.productGuid, "the tick must not silently reset an explicit choice back to the default")
    }

    // MARK: - Surfacing a real server error the shell cannot act on silently (requirement A)

    @Test("refresh() surfaces a 'needs a project argument' server error verbatim via refreshError, instead of silently self-healing to an empty list")
    func refreshSurfacesAmbiguousProjectErrorVerbatim() async {
        let fetcher = FakeMemoryFetcher([.failure(.server(status: 400, message: Self.ambiguousProjectMessage))])
        // projectsScript defaults to an empty list - the exact "two projects exist server-side, but
        // the picker has not (yet) resolved a default" gap this fix exists to close.
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.refresh()

        #expect(viewModel.refreshError == Self.ambiguousProjectMessage)
        #expect(viewModel.documents.isEmpty, "still no invented data - only a NEW published error, never a fabricated document list")
    }

    @Test("a transient (.transport) refresh failure does NOT set refreshError - self-heal is narrowed to explained server errors, not broadened to every failure")
    func transientFailureDoesNotSetRefreshError() async {
        let fetcher = FakeMemoryFetcher([.failure(.transport(URLError(.timedOut)))])
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.refresh()

        #expect(viewModel.refreshError == nil)
    }

    @Test("refreshError clears on the next fully successful refresh")
    func refreshErrorClearsOnNextSuccess() async {
        let fetcher = FakeMemoryFetcher(
            [
                .failure(.server(status: 400, message: Self.ambiguousProjectMessage)),
                .success(MemoryResult(documents: [Self.realDocument], proposals: [])),
            ],
            projectsScript: [
                .success(ProjectsResult(projects: [])),
                .success(ProjectsResult(projects: [Self.projectAlpha])),
            ]
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.refresh()
        #expect(viewModel.refreshError == Self.ambiguousProjectMessage)

        await viewModel.refresh()
        #expect(viewModel.refreshError == nil)
        #expect(viewModel.documents == [Self.realDocument])
    }

    @Test("a surfaced server error still must not clear documents/proposals already on screen - narrowing self-heal for errors must not weaken the existing must-not-clear-good-data contract")
    func serverErrorDoesNotClearExistingData() async {
        let fetcher = FakeMemoryFetcher([
            .success(MemoryResult(documents: [Self.realDocument], proposals: [Self.realPendingProposal])),
            .failure(.server(status: 400, message: Self.ambiguousProjectMessage)),
        ])
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.refresh()
        #expect(viewModel.documents == [Self.realDocument])

        await viewModel.refresh()
        #expect(viewModel.documents == [Self.realDocument], "an explained server error still must not erase data already on screen")
        #expect(viewModel.proposals == [Self.realPendingProposal])
        #expect(viewModel.refreshError == Self.ambiguousProjectMessage, "but it DOES now surface, unlike before this fix")
    }

    // MARK: - selectDocument(name:): one document's full content, independent of refresh()

    @Test("selectDocument(name:) populates selectedDocument verbatim on success")
    func selectDocumentPopulatesContentVerbatim() async {
        let fetcher = FakeMemoryFetcher(documentOutcome: .success(Self.realDocumentContent))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.selectDocument(name: Self.realDocumentContent.name)

        #expect(viewModel.selectedDocument == .loaded(Self.realDocumentContent))
        #expect(await fetcher.lastRequestedDocumentName == Self.realDocumentContent.name)
    }

    @Test("selectDocument(name:) uses the currently selected project, not a hardcoded nil - the fix must thread beyond refresh() alone, or picking a project in the Documents list would still break opening one")
    func selectDocumentUsesCurrentProjectFilter() async {
        let fetcher = FakeMemoryFetcher(documentOutcome: .success(Self.realDocumentContent))
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.selectProject(Self.projectBeta.productGuid)

        await viewModel.selectDocument(name: Self.realDocumentContent.name)

        #expect(await fetcher.lastDocumentProject == Self.projectBeta.productGuid)
    }

    @Test("selectDocument(name:) failure (e.g. unknown document, 404) becomes .failed with the server's own message verbatim")
    func selectDocumentServerFailureBecomesFailedState() async {
        let message = "'bogus.md' does not exist yet."
        let fetcher = FakeMemoryFetcher(documentOutcome: .failure(.server(status: 404, message: message)))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.selectDocument(name: "bogus.md")

        #expect(viewModel.selectedDocument == .failed(message: message))
    }

    @Test("a transient selectDocument failure (.transport) self-heals: an already-loaded document is left in place, not cleared")
    func selectDocumentTransientFailureSelfHeals() async {
        let fetcher = FakeMemoryFetcher(documentOutcome: .success(Self.realDocumentContent))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.selectDocument(name: Self.realDocumentContent.name)
        #expect(viewModel.selectedDocument == .loaded(Self.realDocumentContent))

        await fetcher.setDocumentOutcome(.failure(.transport(URLError(.timedOut))))
        await viewModel.selectDocument(name: Self.realDocumentContent.name)

        #expect(
            viewModel.selectedDocument == .loaded(Self.realDocumentContent),
            "a transient failure must not clobber an already-loaded document")
    }

    @Test("clearSelectedDocument() resets to .notSelected")
    func clearSelectedDocumentResets() async {
        let fetcher = FakeMemoryFetcher(documentOutcome: .success(Self.realDocumentContent))
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.selectDocument(name: Self.realDocumentContent.name)
        #expect(viewModel.selectedDocument != .notSelected)

        viewModel.clearSelectedDocument()

        #expect(viewModel.selectedDocument == .notSelected)
    }

    // MARK: - saveDocument: the confirmation gate is enforced here, not only in a SwiftUI dialog -
    // same discipline `ProjectsViewModel.removeProject(productGuid:confirmed:)` already holds to.
    // Unlike remove (which, task 4 discovered, deletes nothing at all today), a save always
    // OVERWRITES the file's current content: `Hades.Server.Control.MemoryEndpoint.WriteDocument` ->
    // `ProjectService.WriteMemoryDocument` -> `MemoryStore.Write` -> `AtomicWrite` ->
    // `File.Move(temp, path, overwrite: true)` - confirmed by reading the core. No merge, no version
    // history: this is the sharpest instance of "authored and irreplaceable" the brief names, so
    // `confirmed` gates the network call exactly like remove does, never just the dialog around it.

    @Test("saveDocument(confirmed: false) never calls the API")
    func saveWithoutConfirmationDoesNotCallAPI() async {
        let fetcher = FakeMemoryFetcher(writeOutcome: .success(ActionResult(success: true, message: "Saved conventions.md.")))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.saveDocument(name: "conventions.md", content: "new content", confirmed: false)

        #expect(await fetcher.writeCallCount == 0)
        #expect(viewModel.lastActionMessage == nil)
    }

    @Test("saveDocument(confirmed: true) calls the API with the exact name/content and renders ActionResult.message verbatim")
    func saveWithConfirmationCallsAPIAndRendersMessage() async {
        // Verbatim from the live-captured memory_action_write_document.json fixture (Task 1).
        let message = "Saved p13t1-fixture-conventions.md."
        let fetcher = FakeMemoryFetcher(writeOutcome: .success(ActionResult(success: true, message: message)))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.saveDocument(name: "p13t1-fixture-conventions.md", content: "# Edited\n", confirmed: true)

        #expect(await fetcher.writeCallCount == 1)
        #expect(await fetcher.lastWrittenName == "p13t1-fixture-conventions.md")
        #expect(await fetcher.lastWrittenContent == "# Edited\n")
        #expect(viewModel.lastActionMessage == message)
    }

    @Test("saveDocument failure (e.g. a rejected traversal attempt) renders the server's own message verbatim")
    func saveFailureRendersServerMessage() async {
        let message = "Invalid memory document name '../escape': it must be a plain file name, not a path."
        let fetcher = FakeMemoryFetcher(writeOutcome: .failure(.server(status: 400, message: message)))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.saveDocument(name: "../escape", content: "x", confirmed: true)

        #expect(viewModel.lastActionMessage == message)
    }

    // MARK: - acceptProposal / deferProposal - non-destructive (append-only, creating the target
    // file if needed; pure status bookkeeping, respectively - see MemoryEndpoint's own class doc
    // comment: "Accepting a proposal... never overwrites: an automatic overwrite could silently
    // discard prior authored text"), so neither needs a confirmation gate - single click,
    // structurally identical to ProjectDetailView's Rebuild/Install/Reveal/Open buttons.

    @Test("acceptProposal(fileName:) renders ActionResult.message verbatim")
    func acceptProposalRendersMessageVerbatim() async {
        // Verbatim from memory_action_accept_proposal.json.
        let message = "Accepted \u{2014} merged into p13t1-fixture-conventions.md."
        let fetcher = FakeMemoryFetcher(acceptOutcome: .success(ActionResult(success: true, message: message)))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.acceptProposal(fileName: "20260805-084055-p13t1-fixture-conventions.md")

        #expect(await fetcher.acceptCallCount == 1)
        #expect(await fetcher.lastAcceptedFileName == "20260805-084055-p13t1-fixture-conventions.md")
        #expect(viewModel.lastActionMessage == message)
    }

    @Test("deferProposal(fileName:) renders ActionResult.message verbatim")
    func deferProposalRendersMessageVerbatim() async {
        // Verbatim from memory_action_defer_proposal.json.
        let message = "Proposal deferred."
        let fetcher = FakeMemoryFetcher(deferOutcome: .success(ActionResult(success: true, message: message)))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.deferProposal(fileName: "convention-naming.md")

        #expect(await fetcher.deferCallCount == 1)
        #expect(await fetcher.lastDeferredFileName == "convention-naming.md")
        #expect(viewModel.lastActionMessage == message)
    }

    // MARK: - dismissProposal: the one proposal action that actually deletes something (the proposal
    // file itself - never a memory/*.md document) - confirmation gate enforced here too. The core
    // ALSO refuses with a 400 unless its own `confirm=true` is set (`MemoryEndpoint.DismissProposal`),
    // so this is defense in depth, not the only gate; `confirm: true` is only ever sent once Swift's
    // own gate has already passed.

    @Test("dismissProposal(confirmed: false) never calls the API")
    func dismissWithoutConfirmationDoesNotCallAPI() async {
        let fetcher = FakeMemoryFetcher(dismissOutcome: .success(ActionResult(success: true, message: "Proposal dismissed.")))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.dismissProposal(fileName: "topic_cluster-b798e65931d6785f.md", confirmed: false)

        #expect(await fetcher.dismissCallCount == 0)
        #expect(viewModel.lastActionMessage == nil)
    }

    @Test("dismissProposal(confirmed: true) calls the API with confirm=true and renders ActionResult.message verbatim")
    func dismissWithConfirmationCallsAPIAndRendersMessage() async {
        // Verbatim from memory_action_dismiss_proposal.json.
        let message = "Proposal dismissed."
        let fetcher = FakeMemoryFetcher(dismissOutcome: .success(ActionResult(success: true, message: message)))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.dismissProposal(fileName: "topic_cluster-b798e65931d6785f.md", confirmed: true)

        #expect(await fetcher.dismissCallCount == 1)
        #expect(await fetcher.lastDismissedFileName == "topic_cluster-b798e65931d6785f.md")
        #expect(await fetcher.lastDismissConfirm == true)
        #expect(viewModel.lastActionMessage == message)
    }

    @Test("dismissProposal failure (e.g. unknown proposal) renders the server's own message verbatim")
    func dismissFailureRendersServerMessage() async {
        let message = "Unknown proposal 'bogus.md'."
        let fetcher = FakeMemoryFetcher(dismissOutcome: .failure(.server(status: 404, message: message)))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.dismissProposal(fileName: "bogus.md", confirmed: true)

        #expect(viewModel.lastActionMessage == message)
    }

    // MARK: - Transport-level failures self-heal rather than clobbering the last good message - same
    // discipline `ProjectsViewModelActionsTests` proves generically for its six actions.

    @Test("a .transport failure leaves lastActionMessage exactly as it was - no Swift-invented error text")
    func transportFailureSelfHealsWithoutClobberingLastActionMessage() async {
        let fetcher = FakeMemoryFetcher(deferOutcome: .success(ActionResult(success: true, message: "Proposal deferred.")))
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.deferProposal(fileName: "convention-naming.md")
        #expect(viewModel.lastActionMessage == "Proposal deferred.")

        await fetcher.setDeferOutcome(.failure(.transport(URLError(.timedOut))))
        await viewModel.deferProposal(fileName: "convention-naming.md")

        #expect(
            viewModel.lastActionMessage == "Proposal deferred.",
            "a transport failure must not clobber the last real message with nothing")
    }
}
