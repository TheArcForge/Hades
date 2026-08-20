import HadesControl

/// The narrow slice of `ControlClient` that the Traces view needs: the four `GET /control/traces/*`
/// endpoints, nothing else. Unlike `ControlProjectsFetching`, this protocol is fetch-only - Traces
/// has no POST/action endpoint at all (confirmed by reading `Hades.Server.Control.TracesEndpoint.cs`:
/// every route it exposes is a `GET`). Same reason for existing as every other `Control*Fetching`
/// protocol in this app: tests fake the control API without a real `URLSession` round trip - see
/// `FakeTracesFetcher` in `Tests/HadesAppTests/Support/TestSupport.swift`. `ControlClient` needed no
/// changes to conform (empty extension below): every one of these already matches this signature,
/// typed throws included - the extra default argument values `ControlClient`'s own declarations
/// carry are simply not visible through this protocol, the same as everywhere else in this file.
public protocol ControlTracesFetching: Sendable {
    /// `GET /control/traces/sequences` - the ONLY traces endpoint that accepts project/tool/outcome/
    /// duration filters at all (see `ControlClient.tracesSequences`'s own doc comment: filtering
    /// happens server-side, after grouping, never re-applied here). There is no separate "flat list
    /// of every call" endpoint in this API - a `TraceSequenceRow`'s own `tools`/`traceIds` (parallel
    /// arrays) ARE the individual calls a sequence groups, in order.
    func tracesSequences(
        project: String?, tool: String?, outcome: String?,
        minDurationMs: Int?, maxDurationMs: Int?, limit: Int?
    ) async throws(ControlClientError) -> TraceSequencesResult

    /// `GET /control/traces/{traceId}` - one call's full span detail, independent of which list
    /// (sequences, failures) the id was selected from.
    func traceDetail(traceId: String, project: String?) async throws(ControlClientError) -> TraceDetailResult

    /// `GET /control/traces/slow` - the slowest tools, ranked, surfaced from their own endpoint
    /// rather than sorted client-side out of `tracesSequences`.
    func tracesSlow(project: String?, limit: Int?) async throws(ControlClientError) -> SlowToolsResult

    /// `GET /control/traces/failures` - failed calls, surfaced from their own endpoint rather than
    /// filtered client-side out of `tracesSequences`.
    func tracesFailures(project: String?, limit: Int?) async throws(ControlClientError) -> FailedCallsResult

    /// `GET /control/projects` - every known project. `TracesViewModel` uses this ONLY to populate
    /// its own project Picker and to resolve a defensible default when more than one project is
    /// known and nothing has been explicitly chosen yet (see `TracesViewModel.refresh()`'s own doc
    /// comment) - it is the exact same endpoint `ControlProjectsFetching.projects()` already exposes
    /// for the Projects view. This protocol needs its own copy of the requirement since
    /// `TracesViewModel` is typed against `ControlTracesFetching`, not `ControlProjectsFetching` -
    /// `ControlClient` needs no changes to conform: it already implements this exact signature (see
    /// `ControlProjectsFetching`'s own doc comment for why).
    func projects() async throws(ControlClientError) -> ProjectsResult
}

extension ControlClient: ControlTracesFetching {}
