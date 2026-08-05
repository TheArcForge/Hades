import HadesControl

/// One selected document's fetch state for the Memory view - `GET /control/memory/document`,
/// resolved. `MemoryViewModel.selectDocument(name:)` is the only thing that changes this; nothing
/// polls it on a timer - the same shape `TraceDetailFetchState` already established for a selected
/// trace's span detail: a document the user opened to read/edit is a fixed snapshot for as long as
/// they are looking at it, not a live value `refresh()` should silently overwrite out from under an
/// in-progress edit.
public enum MemoryDocumentFetchState: Equatable, Sendable {
    /// No document selected yet - the ordinary starting state, not an error.
    case notSelected

    /// The full raw content for the most recently selected document.
    case loaded(MemoryDocumentResult)

    /// `GET /control/memory/document` answered with a server error (e.g. `'{name}' does not exist
    /// yet.`) - `message` is `ControlClientError.server(message:)`'s payload, verbatim.
    case failed(message: String)
}
