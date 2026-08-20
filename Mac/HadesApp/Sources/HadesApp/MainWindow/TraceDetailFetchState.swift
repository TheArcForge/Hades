import HadesControl

/// One selected call's span-detail fetch state for the Traces view - `GET /control/traces/{traceId}`,
/// resolved. `TracesViewModel.selectTrace(traceId:)` is the only thing that changes this; nothing
/// polls it on a timer - a `traceId` the user clicked on is a fixed historical record, not a live
/// value that changes tick to tick the way `OperationProgress` does for a running rebuild.
///
/// `.failed` covers a 404 ("Unknown trace '{traceId}'.") the same way `OperationProgress.pruned`
/// covers an unknown operation id - the server's own message, carried verbatim, never Swift-invented
/// text. Unlike a pruned operation this is not framed as an ordinary/expected outcome (there is no
/// retention-window story here the way there is for a finished rebuild): the message text itself is
/// the only thing that decides how it reads, so this view renders it exactly as sent either way.
public enum TraceDetailFetchState: Equatable, Sendable {
    /// No call selected yet - the ordinary starting state, not an error.
    case notSelected

    /// The full span detail for the most recently selected call.
    case loaded(TraceDetailResult)

    /// `GET /control/traces/{traceId}` answered with a server error - `message` is
    /// `ControlClientError.server(message:)`'s payload, verbatim.
    case failed(message: String)
}
