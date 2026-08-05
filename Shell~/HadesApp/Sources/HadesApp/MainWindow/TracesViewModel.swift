import HadesControl
import Observation

/// Builds a `ControlTracesFetching` for a given connection (normally `ControlClient.init`) - the
/// Traces-view analogue of `ProjectsClientFactory`.
public typealias TracesClientFactory = @Sendable (ControlConnection) -> any ControlTracesFetching

/// Owns the Traces section's own fetch and published state - nothing else. Per the settled data-
/// ownership split (`MainWindowViewModel`'s own doc comment, and `ProjectsViewModel`'s before it):
/// `MainWindowViewModel` owns navigation and the polling LIFECYCLE only; each section owns its own
/// view model and its own fetch. This is that seam for Traces - `refresh()` is what
/// `MainWindowViewModel.refreshSelectedSection` calls once per tick, but only while `.traces` is the
/// selected section (see `AppDelegate`, the composition root, for the actual wiring - see
/// `MainWindowViewModelTests.wiresTracesViewModelIntoRefreshSelectedSection` for the same pattern
/// proven against a real `TracesViewModel`). This type never starts a timer of its own - same
/// discipline `ProjectsViewModel`/`MenuBarViewModel` already hold to.
///
/// **Three independent fetches, not one.** Spec #3 §3.3 requires failures and slow calls to come
/// from their own endpoints, never filtered client-side out of the sequences list (see
/// `Hades.Server.Control.TracesEndpoint`'s own class doc comment: `GET /control/traces/sequences`,
/// `/failures`, `/slow` each map 1:1 onto `TraceStore`'s own separate query methods). `refresh()`
/// therefore calls all three every tick, each with its own independent self-heal - a failure in one
/// must not prevent the other two from updating, and must not clear what is already on screen (the
/// same "one unlucky poll must not flash existing data to empty" contract `ProjectsViewModel.refresh()`
/// already holds to).
///
/// **Sequences are the primary timeline.** `GET /control/traces/sequences` is the only traces
/// endpoint that accepts project/tool/outcome/duration filters at all - there is no separate "every
/// call, flat" endpoint in this API. A `TraceSequenceRow`'s own `tools`/`traceIds` (parallel arrays)
/// already list every individual call the sequence groups, in order; `selectTrace(traceId:)` is how
/// a view reaches one of them (or a `FailedCallRow`'s own `traceId`) for span detail.
@MainActor
@Observable
public final class TracesViewModel {
    public private(set) var sequences: [TraceSequenceRow] = []
    public private(set) var sequencesTruncated: Bool = false
    public private(set) var failures: [FailedCallRow] = []
    public private(set) var slowTools: [SlowToolRow] = []

    /// The currently selected call's span detail - see `TraceDetailFetchState`'s own doc comment.
    /// Populated only by `selectTrace(traceId:)`, never by `refresh()`: a selected call is a fixed
    /// historical record, not something that needs re-polling every tick the way `sequences` does.
    public private(set) var selectedTraceDetail: TraceDetailFetchState = .notSelected

    /// Every known project, from `GET /control/projects` - populates this view's own project Picker
    /// (`ProjectRow.name`/`productGuid`, verbatim - see `TracesView`) and is what `refresh()` consults
    /// to resolve `projectFilter`'s own default when it is still unset (see that property's own doc
    /// comment). A fetch failure here self-heals exactly like every other fetch in this type -
    /// `knownProjects` is left exactly as it was.
    public private(set) var knownProjects: [ProjectRow] = []

    /// The most recent REFRESH failure the shell cannot act on silently, verbatim from the server -
    /// see `refresh()`'s own doc comment for exactly which failures set this versus self-heal.
    /// Recomputed fresh at the start of every `refresh()` call, the same "reflects the last attempt's
    /// own outcome" shape `sequences`/`failures`/`slowTools` already have, NOT a sticky banner that
    /// outlives its own cause: `nil` once a later refresh (e.g. after the user picks a project)
    /// succeeds. Distinct from `selectedTraceDetail`'s own `.failed` case (that is one selected
    /// call's fetch failure; this is the whole section's).
    public private(set) var refreshError: String?

    /// Every Sequences filter, exactly as last set by `applyFilters` - except `project`, which is
    /// Picker-driven and set only by `selectProject(_:)` (see that method's own doc comment for why
    /// it is independent of `applyFilters`). Swift-chosen query-parameter state the user picked, not
    /// rendered API data - see `applyFilters`'s own doc comment. `project` doubles as the
    /// cross-cutting filter for `failures`/`slowTools` too (the only filter `tracesFailures`/
    /// `tracesSlow` accept at all); `tool`/`outcome`/`minDurationMs`/`maxDurationMs` apply to
    /// `sequences` only, matching exactly what the API itself accepts for each endpoint.
    ///
    /// **Defaults to the first known project once `refresh()` has seen at least one, never left
    /// ambiguous.** `GET /control/traces/*` refuses with a 400 ("Hades knows N projects, so this call
    /// needs a 'project' argument...") whenever more than one project is known and none is given -
    /// see `refresh()`'s own doc comment. Applied uniformly whenever `knownProjects` is non-empty, not
    /// only when it holds more than one: this keeps the exact-one-project case working with zero
    /// interaction too (today's behaviour, unchanged - the server would have auto-resolved to this
    /// same project anyway), and gives the Picker something concrete to show selected from the very
    /// first tick that knows about any project at all. Never overrides an already-chosen value.
    public private(set) var projectFilter: String = ""
    public private(set) var toolFilter: String = ""
    public private(set) var outcomeFilter: String?
    public private(set) var minDurationMsFilter: Int?
    public private(set) var maxDurationMsFilter: Int?

    private let discover: ConnectionProvider
    private let makeClient: TracesClientFactory

    public init(
        discover: @escaping ConnectionProvider = { Discovery.read() },
        makeClient: @escaping TracesClientFactory = { ControlClient(connection: $0) }
    ) {
        self.discover = discover
        self.makeClient = makeClient
    }

    /// `GET /control/projects` + `/traces/sequences` + `/failures` + `/slow`, using whatever filters
    /// `applyFilters`/`selectProject` last set - one discovery read and one client for all four,
    /// since they belong to the SAME ~1Hz tick `MainWindowViewModel.refreshSelectedSection` drives
    /// while Traces is selected. Called by `MainWindowViewModel.refreshSelectedSection` once per
    /// tick, and also by `applyFilters`/`selectProject` themselves for an immediate re-fetch rather
    /// than waiting for the next tick.
    ///
    /// **Every fetch here self-heals independently on a TRANSIENT failure** (`.staleToken`,
    /// `.transport`, `.decoding`, or a `.server` with no message) - a failure in one must not prevent
    /// the others from updating, and must not clear what is already on screen (the same "one unlucky
    /// poll must not flash existing data to empty" contract `ProjectsViewModel.refresh()` already
    /// holds to). **That self-heal is narrowed, not deleted, for a `.server` failure that DOES carry
    /// a message** - most commonly `Hades.Core.Projects.ProjectResolver`'s own "Hades knows N
    /// projects, so this call needs a 'project' argument" when more than one project is known and
    /// `project` is still unresolved. That is not a transient blip; it is the server explaining
    /// something the shell cannot act on silently, so it is surfaced verbatim via `refreshError`
    /// instead - data (`sequences`/`failures`/`slowTools`/`knownProjects`) is still left untouched
    /// exactly as the self-heal contract above requires. `refreshError` itself is recomputed fresh
    /// every tick (cleared here first, then set again by whichever fetch below fails with a message),
    /// the same "reflects the last attempt's own outcome" shape the data properties already have.
    ///
    /// **The `knownProjects` fetch runs first and feeds `projectFilter`'s own default before the
    /// other three fetches use it** - see `projectFilter`'s own doc comment for exactly when that
    /// default applies. This is why one `refresh()` call, not a separate poll, is what makes the
    /// "2+ projects known, nothing chosen yet" case resolve to a real project within its own single
    /// tick rather than needing a second one.
    public func refresh() async {
        guard let connection = await discover() else { return }
        let client = makeClient(connection)

        refreshError = nil

        do {
            knownProjects = try await client.projects().projects
        } catch {
            if case .server(_, let message?) = error { refreshError = message }
            // staleToken/transport/decoding, or a .server with no message: self-heals - knownProjects
            // is left exactly as it was.
        }

        if projectFilter.isEmpty, let firstKnown = knownProjects.first {
            projectFilter = firstKnown.productGuid
        }

        let project = projectFilter.isEmpty ? nil : projectFilter
        let tool = toolFilter.isEmpty ? nil : toolFilter

        do {
            let result = try await client.tracesSequences(
                project: project, tool: tool, outcome: outcomeFilter,
                minDurationMs: minDurationMsFilter, maxDurationMs: maxDurationMsFilter, limit: nil
            )
            sequences = result.sequences
            sequencesTruncated = result.truncated
        } catch {
            if case .server(_, let message?) = error {
                refreshError = message
            }
            // Otherwise self-heals next tick - see this method's own doc comment.
        }

        do {
            failures = try await client.tracesFailures(project: project, limit: nil).failures
        } catch {
            if case .server(_, let message?) = error {
                refreshError = message
            }
        }

        do {
            slowTools = try await client.tracesSlow(project: project, limit: nil).tools
        } catch {
            if case .server(_, let message?) = error {
                refreshError = message
            }
        }
    }

    /// Sets every Sequences filter EXCEPT the project (see `selectProject(_:)`) and immediately
    /// re-fetches with the new values, rather than waiting for the next tick - the View's "Apply
    /// Filters" action calls this. This type's filter state is never written from outside except
    /// through this intent-revealing method or `selectProject(_:)`, the same discipline
    /// `MainWindowViewModel.select(_:)`/`ProjectsViewModel.addProject(path:)` already hold to
    /// elsewhere in this app. An empty `tool` string is normalised to `nil` here (see `refresh()`),
    /// so the server's own no-filter behaviour applies rather than this type keeping a stale
    /// distinction between "empty" and "absent".
    public func applyFilters(
        tool: String, outcome: String?, minDurationMs: Int?, maxDurationMs: Int?
    ) async {
        toolFilter = tool
        outcomeFilter = outcome
        minDurationMsFilter = minDurationMs
        maxDurationMsFilter = maxDurationMs
        await refresh()
    }

    /// Sets `projectFilter` alone and immediately re-fetches - the project Picker's own selection,
    /// independent of `applyFilters`. A Picker's selection is not something a user "applies" the way
    /// free-text/duration filters are: changing it should take effect at once, without also
    /// re-applying whatever unconfirmed text currently sits in the tool/duration fields. Every OTHER
    /// filter stays exactly as it was last applied. `productGuid` is normally one of `knownProjects`'
    /// own entries (what the Picker actually offers), but this method does not require that - the
    /// server resolves by id OR name and reports its own "unknown project" error either way, the
    /// same "Swift never re-derives an error the server already owns" discipline `selectTrace`/
    /// `selectDocument` hold to for their own `.server` failures.
    public func selectProject(_ productGuid: String) async {
        projectFilter = productGuid
        await refresh()
    }

    /// `GET /control/traces/{traceId}` - span detail for one call, wherever its id came from (a
    /// sequence's own `traceIds`, or a `FailedCallRow.traceId`). Uses the current `projectFilter` the
    /// same way `refresh()` does, so a call selected while a project filter is active resolves
    /// against that same project.
    public func selectTrace(traceId: String) async {
        guard let connection = await discover() else { return }
        do {
            let detail = try await makeClient(connection).traceDetail(
                traceId: traceId, project: projectFilter.isEmpty ? nil : projectFilter
            )
            selectedTraceDetail = .loaded(detail)
        } catch {
            if case .server(_, let message?) = error {
                selectedTraceDetail = .failed(message: message)
            }
            // Any other failure (staleToken/transport/decoding, or a .server with no message): leave
            // selectedTraceDetail exactly as it was - same self-heal discipline as everywhere else in
            // this app, since re-selecting can succeed later.
        }
    }

    /// Returns to the ordinary "nothing selected" state - called when the view's own selection is
    /// cleared (e.g. the user deselects a row).
    public func clearSelectedTrace() {
        selectedTraceDetail = .notSelected
    }
}
