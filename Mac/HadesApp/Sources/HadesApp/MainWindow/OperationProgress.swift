import HadesControl

/// One tracked rebuild operation's display state for the Projects view - `GET
/// /control/operations/{id}`, resolved. `ProjectsViewModel.rebuildProject(productGuid:)` starts
/// tracking the id `POST .../rebuild` hands back; `ProjectsViewModel.refresh()` - the SAME ~1Hz
/// tick that already refreshes `projects` while Projects is the selected section (see that
/// method's own doc comment) - is what re-polls it on every subsequent tick. This type never
/// starts a timer of its own, same "no timer of its own" discipline `ProjectsViewModel` already
/// holds to for `projects` itself; a section with no window open, or not currently selected, has
/// no business polling an operation any more than it does `/control/projects`.
///
/// `.pruned` is what an unknown-id 404 becomes - see `ControlProjectsFetching.operation(id:)`'s own
/// doc comment and `Hades.Server.Control.Operations.Get`'s own resolved message: "Unknown operation
/// '{id}'. It may have completed and been pruned, or the id is wrong." `OperationRegistry`'s
/// 5-minute post-completion retention (see that type's own class doc comment) makes this an
/// ORDINARY outcome for a rebuild that finished a while ago, not a failure - `message` is the
/// server's own explanation, carried verbatim, never Swift-invented text papering over "this looks
/// identical to a wrong id".
public enum OperationProgress: Equatable, Sendable {
    /// The operation is still trackable - a view renders the wrapped `OperationResult` verbatim:
    /// `state` (an icon only, via `StatusIcon.symbolName(for:)` - never invented display text),
    /// `elapsedSeconds` (already whole seconds the core computed - see that property's own doc
    /// comment - never re-derived from `startedAtUtc`/`finishedAtUtc`), and whichever of
    /// `progress`/`error`/`result` the current `state` populated (e.g. a finished rebuild's own
    /// resolved `result?["message"]?.stringValue`).
    case tracked(OperationResult)

    /// `GET /control/operations/{id}` answered 404 - see this type's own doc comment. `message` is
    /// `ControlClientError.server(message:)`'s payload, verbatim.
    case pruned(message: String)
}
