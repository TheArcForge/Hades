import HadesControl

/// The narrow slice of `ControlClient` that the Memory view needs: `GET /control/memory` plus every
/// write/action endpoint spec #3 §3.4 requires - same "fetch plus act" shape `ControlProjectsFetching`
/// already established for Projects, not a separate protocol per HTTP verb. Exists purely so tests
/// fake the control API without a real `URLSession` round trip - see `FakeMemoryFetcher` in
/// `Tests/HadesAppTests/Support/TestSupport.swift`. `ControlClient` needed no changes to conform
/// (empty extension below): every one of these already matches this signature, typed throws
/// included - the extra default argument values `ControlClient`'s own declarations carry (`project:
/// String? = nil`) are simply not visible through this protocol, the same as `ControlTracesFetching`.
public protocol ControlMemoryFetching: Sendable {
    /// `GET /control/memory` - every authored document AND the proposal queue, in one round trip
    /// (see `Hades.Server.Control.MemoryEndpoint`'s own class doc comment).
    func memory(project: String?) async throws(ControlClientError) -> MemoryResult

    /// `GET /control/memory/document` - one document's complete raw text. `name` is validated
    /// server-side as a basename (no traversal, no rooted paths).
    func memoryDocument(name: String, project: String?) async throws(ControlClientError) -> MemoryDocumentResult

    /// `POST /control/memory/document` - writes (creating or overwriting) one authored document
    /// verbatim. Callers must only reach this after the shell's own confirmation - see
    /// `MemoryViewModel.saveDocument(name:content:confirmed:)`.
    func writeMemoryDocument(name: String, content: String, project: String?) async throws(ControlClientError) -> ActionResult

    /// `POST /control/memory/proposals/accept` - appends the proposal's content into its own
    /// `targetFile`, never overwrites, never deletes the proposal file.
    func acceptMemoryProposal(fileName: String, project: String?) async throws(ControlClientError) -> ActionResult

    /// `POST /control/memory/proposals/dismiss` - deletes the proposal file. Callers must only reach
    /// this after the shell's own confirmation - see
    /// `MemoryViewModel.dismissProposal(fileName:confirmed:)`.
    func dismissMemoryProposal(fileName: String, confirm: Bool, project: String?) async throws(ControlClientError) -> ActionResult

    /// `POST /control/memory/proposals/defer` - pure bookkeeping: never deletes, never writes an
    /// authored document.
    func deferMemoryProposal(fileName: String, project: String?) async throws(ControlClientError) -> ActionResult

    /// `GET /control/projects` - every known project. `MemoryViewModel` uses this ONLY to populate
    /// its own project Picker and to resolve a defensible default when more than one project is
    /// known and nothing has been explicitly chosen yet (see `MemoryViewModel.refresh()`'s own doc
    /// comment) - the same requirement `ControlTracesFetching.projects()` adds for the identical
    /// reason. `ControlClient` needs no changes to conform: it already implements this exact
    /// signature (see `ControlProjectsFetching`'s own doc comment for why).
    func projects() async throws(ControlClientError) -> ProjectsResult
}

extension ControlClient: ControlMemoryFetching {}
