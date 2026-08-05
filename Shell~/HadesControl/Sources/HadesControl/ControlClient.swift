import Foundation

/// Every way a control-API call can fail to hand back a decoded DTO. `staleToken` is deliberately
/// its own case, not folded into `.server(status: 401, ...)`: a 401 here means specifically that
/// the token this client was built with is stale, almost always because the core restarted and
/// wrote a fresh discovery file - the correct, and only, recovery is to call `Discovery.read()`
/// again and build a new `ControlClient`. Retrying the same request with the same token will fail
/// the same way every time. Making this a distinct case (rather than a status code the caller has
/// to compare) is what makes it actionable in the type, per this package's own contract.
public enum ControlClientError: Error, Sendable {
    /// The token this client presented was rejected (HTTP 401). Re-read the discovery file and
    /// build a new `ControlClient`.
    case staleToken

    /// A non-2xx, non-401 response. `message` is the server's own `"error"` field when the body
    /// carried one (every Control endpoint's error responses do - see `ControlAuth`,
    /// `ProjectsEndpoint`, `EditorsEndpoint`), never text invented client-side.
    case server(status: Int, message: String?)

    /// The response body did not decode into the expected DTO shape.
    case decoding(DecodingError)

    /// A request body (e.g. `AddProjectRequest`, `WriteMemoryDocumentRequest`) failed to encode to
    /// JSON. Every request body this package actually sends is a plain-String-fields struct, which
    /// `JSONEncoder` cannot fail to encode - this case exists only so the client stays total under
    /// Swift's typed-throws checking, the same reasoning `send`'s own decode fallback documents.
    case encoding(EncodingError)

    /// The request never got a response to check the status of - the core is not running, this
    /// connection's port is stale, the request timed out, and so on.
    case transport(URLError)
}

/// The control-API client: every phase-one endpoint, decoded straight into the DTOs in
/// `DTOs.swift` and nothing else. No retry policy, no caching, no derived state - a thin,
/// stateless wrapper over `URLSession` that attaches the bearer token and maps the response to
/// either a DTO or a `ControlClientError`.
public struct ControlClient: Sendable {
    private let baseURL: URL
    private let token: String
    private let session: URLSession

    /// `connection` is normally the result of `Discovery.read()`. `session` is overridable so
    /// tests can substitute a mocked `URLProtocol` instead of making real network calls.
    public init(connection: ControlConnection, session: URLSession = .shared) {
        // 127.0.0.1, never "localhost": matches ControlListener's own loopback bind exactly, and
        // sidesteps a DNS resolution step for what is always a same-machine call.
        self.baseURL = URL(string: "http://127.0.0.1:\(connection.port)")!
        self.token = connection.token
        self.session = session
    }

    /// `GET /control/ping`.
    public func ping() async throws(ControlClientError) -> PingResult {
        try await get("/control/ping")
    }

    /// `GET /control/summary` - the menu bar's one endpoint.
    public func summary() async throws(ControlClientError) -> SummaryResult {
        try await get("/control/summary")
    }

    /// `GET /control/projects`.
    public func projects() async throws(ControlClientError) -> ProjectsResult {
        try await get("/control/projects")
    }

    /// `GET /control/editors`.
    public func editors() async throws(ControlClientError) -> EditorsResult {
        try await get("/control/editors")
    }

    /// `POST /control/leases/{id}/release`. `id` is the holding project's `productGuid` (see
    /// `SummaryLease.leaseId`'s own doc comment) - percent-encoded defensively, matching the
    /// `hades` CLI's own `Uri.EscapeDataString`, even though a productGuid is always plain hex in
    /// practice. Idempotent and safe to call late: if the TTL already released the lease
    /// server-side, this still answers `success: true` with an explanatory `message`, never an
    /// error - see `ActionResult`'s own doc comment. Callers must not synthesize an error for a
    /// `success: false` result either; `message` already names what happened.
    public func releaseLease(id: String) async throws(ControlClientError) -> ActionResult {
        try await post("/control/leases/\(encodedPathSegment(id))/release")
    }

    // MARK: - Projects actions

    /// `POST /control/projects/add`. The panel that chose `path` is the only place the shell picks
    /// one - this call just adopts and fully indexes it, returning the same `ProjectRow` shape
    /// `projects()` uses for every other row.
    public func addProject(path: String) async throws(ControlClientError) -> ProjectRow {
        try await post("/control/projects/add", body: AddProjectRequest(path: path))
    }

    /// `POST /control/projects/{productGuid}/remove`. Deregisters only - see `ActionResult.message`
    /// (and this endpoint's own .NET doc comment) for why nothing on disk is ever deleted by this
    /// call, including the project's own graph and authored memory.
    public func removeProject(productGuid: String) async throws(ControlClientError) -> ActionResult {
        try await post("/control/projects/\(encodedPathSegment(productGuid))/remove")
    }

    /// `POST /control/projects/{productGuid}/rebuild`. Returns immediately with an operation id
    /// pollable via `operation(id:)` from the moment this call returns.
    public func rebuildProject(productGuid: String) async throws(ControlClientError) -> RebuildStartedResult {
        try await post("/control/projects/\(encodedPathSegment(productGuid))/rebuild")
    }

    /// `POST /control/projects/{productGuid}/installPlugin`.
    public func installPlugin(productGuid: String) async throws(ControlClientError) -> InstallPluginResult {
        try await post("/control/projects/\(encodedPathSegment(productGuid))/installPlugin")
    }

    /// `POST /control/projects/{productGuid}/revealInFinder`.
    public func revealInFinder(productGuid: String) async throws(ControlClientError) -> ActionResult {
        try await post("/control/projects/\(encodedPathSegment(productGuid))/revealInFinder")
    }

    /// `POST /control/projects/{productGuid}/openInUnity`.
    public func openInUnity(productGuid: String) async throws(ControlClientError) -> ActionResult {
        try await post("/control/projects/\(encodedPathSegment(productGuid))/openInUnity")
    }

    // MARK: - Operations

    /// `GET /control/operations/{id}` - the poll side of `rebuildProject(productGuid:)` (today's
    /// only long-running action). An unknown id is a `.server(status: 404, ...)` naming exactly why
    /// (it may have completed and been pruned, or the id is wrong) - never treat that as a generic
    /// failure the caller must guess at.
    public func operation(id: String) async throws(ControlClientError) -> OperationResult {
        try await get("/control/operations/\(encodedPathSegment(id))")
    }

    // MARK: - Settings

    /// `GET /control/settings`.
    public func settings() async throws(ControlClientError) -> SettingsResult {
        try await get("/control/settings")
    }

    // MARK: - Traces

    /// `GET /control/traces/sequences`. `project` is a flexible handle (name or productGuid,
    /// matching `hades_status`'s own "project" argument) - omit it when only one project is known.
    /// Every other filter is applied AFTER the core groups calls into sequences, never before (see
    /// `Hades.Server.Control.TracesEndpoint`'s own doc comment for why filtering first would
    /// corrupt grouping) - Swift does not re-filter client-side either. `limit` is omitted (not
    /// defaulted to today's `200`) when nil, so the core's own route default stays the one source
    /// of truth for it - Swift never keeps a stale copy of a server-owned policy value.
    public func tracesSequences(
        project: String? = nil, tool: String? = nil, outcome: String? = nil,
        minDurationMs: Int? = nil, maxDurationMs: Int? = nil, limit: Int? = nil
    ) async throws(ControlClientError) -> TraceSequencesResult {
        var query: [URLQueryItem] = []
        if let project { query.append(URLQueryItem(name: "project", value: project)) }
        if let tool { query.append(URLQueryItem(name: "tool", value: tool)) }
        if let outcome { query.append(URLQueryItem(name: "outcome", value: outcome)) }
        if let minDurationMs { query.append(URLQueryItem(name: "minDurationMs", value: String(minDurationMs))) }
        if let maxDurationMs { query.append(URLQueryItem(name: "maxDurationMs", value: String(maxDurationMs))) }
        if let limit { query.append(URLQueryItem(name: "limit", value: String(limit))) }
        return try await get("/control/traces/sequences", query: query)
    }

    /// `GET /control/traces/{traceId}` - one trace's full span detail.
    public func traceDetail(traceId: String, project: String? = nil) async throws(ControlClientError) -> TraceDetailResult {
        var query: [URLQueryItem] = []
        if let project { query.append(URLQueryItem(name: "project", value: project)) }
        return try await get("/control/traces/\(encodedPathSegment(traceId))", query: query)
    }

    /// `GET /control/traces/slow` - the slowest tools, ranked, surfaced from their own endpoint
    /// rather than sorted client-side out of `tracesSequences`. `limit` is omitted when nil - see
    /// `tracesSequences`'s own doc comment for why.
    public func tracesSlow(project: String? = nil, limit: Int? = nil) async throws(ControlClientError) -> SlowToolsResult {
        var query: [URLQueryItem] = []
        if let project { query.append(URLQueryItem(name: "project", value: project)) }
        if let limit { query.append(URLQueryItem(name: "limit", value: String(limit))) }
        return try await get("/control/traces/slow", query: query)
    }

    /// `GET /control/traces/failures` - failed calls, surfaced from their own endpoint rather than
    /// filtered client-side out of `tracesSequences`. `limit` is omitted when nil - see
    /// `tracesSequences`'s own doc comment for why.
    public func tracesFailures(project: String? = nil, limit: Int? = nil) async throws(ControlClientError) -> FailedCallsResult {
        var query: [URLQueryItem] = []
        if let project { query.append(URLQueryItem(name: "project", value: project)) }
        if let limit { query.append(URLQueryItem(name: "limit", value: String(limit))) }
        return try await get("/control/traces/failures", query: query)
    }

    // MARK: - Memory

    /// `GET /control/memory` - every authored document AND the proposal queue, in one round trip.
    public func memory(project: String? = nil) async throws(ControlClientError) -> MemoryResult {
        var query: [URLQueryItem] = []
        if let project { query.append(URLQueryItem(name: "project", value: project)) }
        return try await get("/control/memory", query: query)
    }

    /// `GET /control/memory/document` - one document's complete raw text. `name` is validated
    /// server-side as a basename (no traversal, no rooted paths) - see the Task 1 report for
    /// confirmation this is actually enforced, not just documented.
    public func memoryDocument(name: String, project: String? = nil) async throws(ControlClientError) -> MemoryDocumentResult {
        var query: [URLQueryItem] = []
        if let project { query.append(URLQueryItem(name: "project", value: project)) }
        query.append(URLQueryItem(name: "name", value: name))
        return try await get("/control/memory/document", query: query)
    }

    /// `POST /control/memory/document` - writes (creating or overwriting) one authored document
    /// verbatim. This is the shell's own text-editor save path, not a merge - see
    /// `acceptMemoryProposal(fileName:project:)` for the one path that appends instead.
    public func writeMemoryDocument(
        name: String, content: String, project: String? = nil
    ) async throws(ControlClientError) -> ActionResult {
        var query: [URLQueryItem] = []
        if let project { query.append(URLQueryItem(name: "project", value: project)) }
        query.append(URLQueryItem(name: "name", value: name))
        return try await post("/control/memory/document", query: query, body: WriteMemoryDocumentRequest(content: content))
    }

    /// `POST /control/memory/proposals/accept` - appends the proposal's content into its own
    /// `targetFile` (creating it if needed) and marks the proposal accepted; never deletes the
    /// proposal file itself.
    public func acceptMemoryProposal(fileName: String, project: String? = nil) async throws(ControlClientError) -> ActionResult {
        var query: [URLQueryItem] = []
        if let project { query.append(URLQueryItem(name: "project", value: project)) }
        query.append(URLQueryItem(name: "fileName", value: fileName))
        return try await post("/control/memory/proposals/accept", query: query)
    }

    /// `POST /control/memory/proposals/dismiss` - deletes the proposal file. `confirm` must be
    /// explicitly `true`; the core refuses with a 400 otherwise rather than silently no-op'ing or
    /// deleting anyway - the shell's own confirmation UI is what should set this to `true`, never a
    /// default.
    public func dismissMemoryProposal(
        fileName: String, confirm: Bool, project: String? = nil
    ) async throws(ControlClientError) -> ActionResult {
        var query: [URLQueryItem] = []
        if let project { query.append(URLQueryItem(name: "project", value: project)) }
        query.append(URLQueryItem(name: "fileName", value: fileName))
        query.append(URLQueryItem(name: "confirm", value: confirm ? "true" : "false"))
        return try await post("/control/memory/proposals/dismiss", query: query)
    }

    /// `POST /control/memory/proposals/defer` - pure bookkeeping: marks the proposal deferred,
    /// never deletes it, never writes an authored document.
    public func deferMemoryProposal(fileName: String, project: String? = nil) async throws(ControlClientError) -> ActionResult {
        var query: [URLQueryItem] = []
        if let project { query.append(URLQueryItem(name: "project", value: project)) }
        query.append(URLQueryItem(name: "fileName", value: fileName))
        return try await post("/control/memory/proposals/defer", query: query)
    }

    // MARK: - Request plumbing

    private func get<Response: Decodable>(_ path: String, query: [URLQueryItem] = []) async throws(ControlClientError) -> Response {
        try await send(request(method: "GET", path: path, query: query))
    }

    private func post<Response: Decodable>(_ path: String, query: [URLQueryItem] = []) async throws(ControlClientError) -> Response {
        try await send(request(method: "POST", path: path, query: query))
    }

    /// The one call site every request-body endpoint (`addProject`, `writeMemoryDocument`) goes
    /// through - `query` and `body` compose independently, so a route needing both (`writeMemoryDocument`'s
    /// `name`/`project` query items alongside its JSON `content` body) is one call, not two request
    /// objects merged after the fact.
    private func post<Body: Encodable, Response: Decodable>(
        _ path: String, query: [URLQueryItem] = [], body: Body
    ) async throws(ControlClientError) -> Response {
        var httpRequest = request(method: "POST", path: path, query: query)
        do {
            httpRequest.httpBody = try JSONEncoder().encode(body)
            httpRequest.setValue("application/json", forHTTPHeaderField: "Content-Type")
        } catch let error as EncodingError {
            throw .encoding(error)
        } catch {
            // JSONEncoder's documented failure mode is EncodingError; this branch only exists so
            // this function stays total under Swift's typed-throws checking - see `send`'s own
            // matching branch for the same reasoning against JSONDecoder.
            throw .encoding(.invalidValue(body, .init(codingPath: [], debugDescription: "\(error)")))
        }
        return try await send(httpRequest)
    }

    /// Percent-encodes one caller-supplied path segment (a productGuid, operation id, or traceId) -
    /// defensive even though every one of these is plain hex or a GUID in practice, matching
    /// `releaseLease(id:)`'s own existing precedent (see that method's own doc comment).
    private func encodedPathSegment(_ raw: String) -> String {
        raw.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? raw
    }

    private func request(method: String, path: String, query: [URLQueryItem] = []) -> URLRequest {
        // Every path passed in above is a literal control-API route this file owns; the one
        // caller-supplied path SEGMENT (a lease/operation id, a productGuid, a traceId) is already
        // percent-encoded before it reaches here - see `releaseLease` and `encodedPathSegment`.
        var components = URLComponents(url: URL(string: baseURL.absoluteString + path)!, resolvingAgainstBaseURL: false)!
        if !query.isEmpty { components.queryItems = query }

        var request = URLRequest(url: components.url!)
        request.httpMethod = method
        // Every request carries the bearer token - reads as well as writes, matching
        // ControlAuth.UseControlTokenAuth's own "applied globally, before any endpoint is mapped"
        // contract on the server side.
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        return request
    }

    private func send<Response: Decodable>(_ request: URLRequest) async throws(ControlClientError) -> Response {
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch let error as URLError {
            throw .transport(error)
        } catch {
            // URLSession's documented failure mode for `data(for:)` is URLError; anything else
            // (e.g. a test double throwing a different Error) still needs to surface as a
            // transport failure rather than crash.
            throw .transport(URLError(.unknown))
        }

        guard let httpResponse = response as? HTTPURLResponse else {
            throw .transport(URLError(.badServerResponse))
        }

        guard (200...299).contains(httpResponse.statusCode) else {
            if httpResponse.statusCode == 401 {
                throw .staleToken
            }
            let message = try? JSONDecoder().decode(ControlErrorBody.self, from: data).error
            throw .server(status: httpResponse.statusCode, message: message)
        }

        do {
            return try JSONDecoder().decode(Response.self, from: data)
        } catch let error as DecodingError {
            throw .decoding(error)
        } catch {
            // JSONDecoder's documented failure mode is DecodingError; this branch only exists so
            // the function stays total under Swift's typed-throws checking.
            throw .decoding(.dataCorrupted(.init(codingPath: [], debugDescription: "\(error)")))
        }
    }
}

/// The `{"error": "..."}` shape every non-2xx Control response body carries - see
/// `ControlAuth.UseControlTokenAuth`, `ProjectsEndpoint.Remove`, `EditorsEndpoint.ReleaseAsync`,
/// and every other error path in the control API. Not a public DTO: it exists only so
/// `ControlClient` can read the server's own message for `ControlClientError.server(message:)`
/// rather than inventing one.
private struct ControlErrorBody: Decodable {
    let error: String
}
