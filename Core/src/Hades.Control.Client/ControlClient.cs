using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hades.Control.Client.Dtos;

namespace Hades.Control.Client;

/// <summary>
/// The control-API client: every read-only endpoint that exists so far, decoded straight into the
/// DTOs in <c>Dtos/</c> and nothing else. No retry policy, no caching, no derived state - a thin,
/// stateless wrapper over <see cref="HttpClient"/> that attaches the bearer token once and maps
/// every response to either a DTO or a <see cref="ControlClientException"/>.
///
/// The .NET twin of Swift's <c>ControlClient</c>
/// (Mac/HadesControl/Sources/HadesControl/ControlClient.swift) - see that file's own class doc
/// comment for the "renders what the core decided and nothing else" contract this port holds
/// itself to as well. Beyond the phase-one read routes (ping, summary, projects, settings,
/// editors) and the one mutation <c>Hades.Cli</c> needs (release a lease), every other
/// action/mutation route (add/remove/rebuild project, edit memory, migration) that the Swift
/// client already speaks is deliberately left for a later task - this file does not guess at, or
/// stub out, routes it has not yet confirmed a caller for.
/// </summary>
public sealed class ControlClient
{
    readonly HttpClient _http;

    /// <summary><paramref name="connection"/> is normally the result of <c>Discovery.Read</c>.
    /// <paramref name="httpClient"/> is overridable so tests can substitute a stub
    /// <see cref="HttpMessageHandler"/> instead of making real network calls - same reasoning as
    /// the Swift original's own <c>session: URLSession</c> constructor parameter.</summary>
    public ControlClient(ControlConnection connection, HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();

        // 127.0.0.1, never "localhost": matches ControlListener's own loopback bind exactly, and
        // sidesteps a DNS resolution step for what is always a same-machine call - see the Swift
        // original's identical reasoning on its own baseURL.
        _http.BaseAddress = new Uri($"http://127.0.0.1:{connection.Port}");

        // Every request carries the bearer token - reads as well as writes, matching
        // ControlAuth.UseControlTokenAuth's own "applied globally, before any endpoint is mapped"
        // contract on the server side. Set once here, not per-request, since it never changes for
        // the lifetime of a client built over one ControlConnection.
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
    }

    /// <summary><c>GET /control/ping</c>.</summary>
    public Task<PingResult> PingAsync() => GetAsync<PingResult>("/control/ping");

    /// <summary><c>GET /control/summary</c> - the menu bar's one endpoint.</summary>
    public Task<SummaryResult> SummaryAsync() => GetAsync<SummaryResult>("/control/summary");

    /// <summary><c>GET /control/projects</c>.</summary>
    public Task<ProjectsResult> ProjectsAsync() => GetAsync<ProjectsResult>("/control/projects");

    /// <summary><c>GET /control/settings</c>.</summary>
    public Task<SettingsResult> SettingsAsync() => GetAsync<SettingsResult>("/control/settings");

    /// <summary><c>GET /control/editors</c>.</summary>
    public Task<EditorsResult> EditorsAsync() => GetAsync<EditorsResult>("/control/editors");

    /// <summary><c>POST /control/leases/{id}/release</c> - force-releases the reload lease
    /// <paramref name="leaseId"/> names (the same id <see cref="SummaryLease.LeaseId"/> reports).
    /// Idempotent on the server side: releasing when nothing is held still succeeds.</summary>
    public Task<ActionResult> ReleaseLeaseAsync(string leaseId) =>
        PostAsync<ActionResult>($"/control/leases/{Uri.EscapeDataString(leaseId)}/release");

    /// <summary><c>GET /control/operations/{id}</c> - one tracked operation's current state.
    /// An unknown id answers 404 with the server's own explanation ("It may have completed and been
    /// pruned, or the id is wrong"), which is an ORDINARY outcome for a rebuild that finished a
    /// while ago rather than a failure; callers tell it apart by
    /// <see cref="ControlClientException.StatusCode"/>.</summary>
    public Task<OperationResult> OperationAsync(string id) =>
        GetAsync<OperationResult>($"/control/operations/{Uri.EscapeDataString(id)}");

    /// <summary><c>POST /control/projects/add</c>. Answers the new <see cref="ProjectRow"/> itself -
    /// deliberately no <c>message</c> field, because the row appearing in the list IS the feedback,
    /// so there is no server text for a caller to display and none should be invented.</summary>
    public Task<ProjectRow> AddProjectAsync(string path) =>
        PostAsync<AddProjectRequest, ProjectRow>("/control/projects/add", new AddProjectRequest { Path = path });

    /// <summary><c>POST /control/projects/{productGuid}/remove</c>.</summary>
    public Task<ActionResult> RemoveProjectAsync(string productGuid) =>
        PostAsync<ActionResult>($"/control/projects/{Uri.EscapeDataString(productGuid)}/remove");

    /// <summary><c>POST /control/projects/{productGuid}/rebuild</c> - starts a rebuild and hands
    /// back the operation id to poll with <see cref="OperationAsync"/>. Returns as soon as the
    /// operation is registered; it does not wait for the rebuild.</summary>
    public Task<RebuildStartedResult> RebuildProjectAsync(string productGuid) =>
        PostAsync<RebuildStartedResult>($"/control/projects/{Uri.EscapeDataString(productGuid)}/rebuild");

    /// <summary><c>POST /control/projects/{productGuid}/installPlugin</c>.
    /// <see cref="InstallPluginResult.Message"/> already says whether a restart is needed in plain
    /// language - render it verbatim rather than re-stating <c>needsRestart</c> as your own text.</summary>
    public Task<InstallPluginResult> InstallPluginAsync(string productGuid) =>
        PostAsync<InstallPluginResult>($"/control/projects/{Uri.EscapeDataString(productGuid)}/installPlugin");

    /// <summary><c>POST /control/projects/{productGuid}/revealInFinder</c>. The route keeps its
    /// macOS name on both platforms - it is the server's, and the server decides what revealing
    /// means per platform (Explorer on Windows). A client that renamed it would be guessing.</summary>
    public Task<ActionResult> RevealInFinderAsync(string productGuid) =>
        PostAsync<ActionResult>($"/control/projects/{Uri.EscapeDataString(productGuid)}/revealInFinder");

    /// <summary><c>POST /control/projects/{productGuid}/openInUnity</c>.</summary>
    public Task<ActionResult> OpenInUnityAsync(string productGuid) =>
        PostAsync<ActionResult>($"/control/projects/{Uri.EscapeDataString(productGuid)}/openInUnity");

    /// <summary>
    /// <c>GET /control/traces/sequences</c> - the primary timeline, and the only traces route that
    /// accepts filters at all.
    ///
    /// Filtering happens SERVER-side: a caller must not re-filter the result, because the server
    /// groups calls into sequences before filtering and doing it the other way round corrupts the
    /// grouping. <paramref name="limit"/> is OMITTED when null rather than defaulted to today's 200,
    /// so the route's own default stays the single source of truth - a client that hardcoded it
    /// would be keeping a stale copy of a server-owned policy value.
    /// </summary>
    public Task<TraceSequencesResult> TracesSequencesAsync(
        string? project = null,
        string? tool = null,
        string? outcome = null,
        long? minDurationMs = null,
        long? maxDurationMs = null,
        int? limit = null) =>
        GetAsync<TraceSequencesResult>(WithQuery("/control/traces/sequences",
            ("project", project),
            ("tool", tool),
            ("outcome", outcome),
            ("minDurationMs", minDurationMs?.ToString(CultureInfo.InvariantCulture)),
            ("maxDurationMs", maxDurationMs?.ToString(CultureInfo.InvariantCulture)),
            ("limit", limit?.ToString(CultureInfo.InvariantCulture))));

    /// <summary><c>GET /control/traces/failures</c> - its own endpoint, deliberately: failures are
    /// never to be filtered client-side out of the sequences list.</summary>
    public Task<FailedCallsResult> TracesFailuresAsync(string? project = null, int? limit = null) =>
        GetAsync<FailedCallsResult>(WithQuery("/control/traces/failures",
            ("project", project),
            ("limit", limit?.ToString(CultureInfo.InvariantCulture))));

    /// <summary><c>GET /control/traces/slow</c> - likewise its own endpoint.</summary>
    public Task<SlowToolsResult> TracesSlowAsync(string? project = null, int? limit = null) =>
        GetAsync<SlowToolsResult>(WithQuery("/control/traces/slow",
            ("project", project),
            ("limit", limit?.ToString(CultureInfo.InvariantCulture))));

    /// <summary>
    /// <c>GET /control/traces/{traceId}</c> - one call's full span detail.
    ///
    /// "sequences", "slow" and "failures" can never be mistaken for a trace id: ASP.NET Core prefers
    /// a literal segment over a route parameter at the same position, regardless of registration
    /// order, so the three routes above always win. See ControlListener's own comment.
    /// </summary>
    public Task<TraceDetailResult> TraceDetailAsync(string traceId, string? project = null) =>
        GetAsync<TraceDetailResult>(WithQuery(
            $"/control/traces/{Uri.EscapeDataString(traceId)}", ("project", project)));

    /// <summary><c>GET /control/memory</c> - authored documents and the proposal queue together, in
    /// the one round trip the endpoint provides.</summary>
    public Task<MemoryResult> MemoryAsync(string? project = null) =>
        GetAsync<MemoryResult>(WithQuery("/control/memory", ("project", project)));

    /// <summary>
    /// <c>GET /control/memory/document</c> - one document's complete raw text.
    ///
    /// <paramref name="name"/> is a QUERY parameter, not a route segment - see MemoryEndpoint's own
    /// doc comment for why. A document that does not exist answers a server error carrying its own
    /// explanation ("'{name}' does not exist yet."), which callers render verbatim.
    /// </summary>
    public Task<MemoryDocumentResult> MemoryDocumentAsync(string name, string? project = null) =>
        GetAsync<MemoryDocumentResult>(WithQuery("/control/memory/document",
            ("project", project), ("name", name)));

    /// <summary>
    /// <c>POST /control/memory/document</c> - OVERWRITES the named document.
    ///
    /// There is no merge and no version history: the core writes atomically over whatever was there.
    /// Memory is authored and irreplaceable, unlike the derived databases, so a caller must not
    /// reach this without an explicit confirmation - see MemoryViewModel's own gate.
    /// </summary>
    public Task<ActionResult> WriteMemoryDocumentAsync(string name, string content, string? project = null) =>
        PostAsync<WriteMemoryDocumentRequest, ActionResult>(
            WithQuery("/control/memory/document", ("project", project), ("name", name)),
            new WriteMemoryDocumentRequest { Content = content });

    /// <summary><c>POST /control/memory/proposals/accept</c>. Never destructive: accepting only ever
    /// APPENDS to the target document, creating it if it is missing.</summary>
    public Task<ActionResult> AcceptMemoryProposalAsync(string fileName, string? project = null) =>
        PostAsync<ActionResult>(WithQuery("/control/memory/proposals/accept",
            ("project", project), ("fileName", fileName)));

    /// <summary><c>POST /control/memory/proposals/defer</c>. Pure bookkeeping - never deletes, never
    /// writes an authored document.</summary>
    public Task<ActionResult> DeferMemoryProposalAsync(string fileName, string? project = null) =>
        PostAsync<ActionResult>(WithQuery("/control/memory/proposals/defer",
            ("project", project), ("fileName", fileName)));

    /// <summary>
    /// <c>POST /control/memory/proposals/dismiss</c> - DELETES the proposal file.
    ///
    /// <paramref name="confirm"/> defaults to false server-side and the endpoint refuses without it,
    /// so this parameter is the server's own gate rather than a client-side nicety. It is deliberately
    /// not defaulted to true here: a caller has to say so.
    /// </summary>
    public Task<ActionResult> DismissMemoryProposalAsync(string fileName, bool confirm, string? project = null) =>
        PostAsync<ActionResult>(WithQuery("/control/memory/proposals/dismiss",
            ("project", project),
            ("fileName", fileName),
            ("confirm", confirm ? "true" : "false")));

    // MARK: - Request plumbing

    /// <summary>Appends only the parameters that have a value. An absent parameter and an empty one
    /// are different things to this API - absent means "no filter", so a null is dropped rather than
    /// sent as an empty string.</summary>
    static string WithQuery(string path, params (string Name, string? Value)[] parameters)
    {
        var present = parameters
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value!)}")
            .ToArray();

        return present.Length == 0 ? path : $"{path}?{string.Join("&", present)}";
    }

    async Task<TResponse> GetAsync<TResponse>(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendAsync<TResponse>(request);
    }

    async Task<TResponse> PostAsync<TResponse>(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        return await SendAsync<TResponse>(request);
    }

    async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            // The DTO's own [JsonPropertyName] attributes carry the wire names, exactly as they do
            // when decoding - no serializer options here for the same reason SendAsync uses none
            // when reading: anything configured on one side and not the other is drift waiting to
            // happen, and the golden fixtures pin the shape either way.
            Content = JsonContent.Create(body),
        };

        return await SendAsync<TResponse>(request);
    }

    /// <summary>The one call site every request above goes through - maps a transport failure, a
    /// non-2xx status, or a malformed body onto one of the four <see cref="ControlClientError"/>
    /// cases, mirroring the Swift original's own <c>send&lt;Response&gt;</c> case for case.</summary>
    async Task<TResponse> SendAsync<TResponse>(HttpRequestMessage request)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request);
        }
        catch (Exception ex)
        {
            // HttpClient.SendAsync's documented failure mode is HttpRequestException (the core is
            // not running, this connection's port is stale, and so on); anything else still needs
            // to surface as a transport failure rather than crash - the request never got a
            // response to check the status of either way, matching the Swift original's own
            // `catch let error as URLError` plus its unconditional catch-all fallback.
            throw new ControlClientException(ControlClientError.Transport, ex.Message, ex);
        }

        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ControlClientException(
                    ControlClientError.StaleToken, "The token this client presented was rejected.");
            }

            // The server's own "error" field, never text invented client-side - see
            // ControlErrorBody below and the Swift original's identical ControlErrorBody. A body
            // that fails to parse (or carries no "error" field) falls back to naming the raw
            // status code rather than throwing a second, unrelated decoding error out of an error
            // path.
            var message = TryReadErrorMessage(body) ?? $"Request failed with status {(int)response.StatusCode}.";
            throw new ControlClientException(
                ControlClientError.Server, message, innerException: null, statusCode: (int)response.StatusCode);
        }

        try
        {
            return JsonSerializer.Deserialize<TResponse>(body)
                ?? throw new ControlClientException(ControlClientError.Decoding, "Response body decoded to null.");
        }
        catch (JsonException ex)
        {
            throw new ControlClientException(ControlClientError.Decoding, ex.Message, ex);
        }
    }

    static string? TryReadErrorMessage(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ControlErrorBody>(body)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The <c>{"error": "..."}</c> shape every non-2xx Control response body carries - see
/// <c>ControlAuth.UseControlTokenAuth</c>, <c>ProjectsEndpoint.Remove</c>,
/// <c>EditorsEndpoint.ReleaseAsync</c>, and every other error path in the control API. Not a
/// public DTO: it exists only so <see cref="ControlClient"/> can read the server's own message
/// for <see cref="ControlClientError.Server"/> rather than inventing one - the .NET twin of the
/// Swift original's own private <c>ControlErrorBody</c>.</summary>
sealed record ControlErrorBody
{
    [JsonPropertyName("error")] public string? Error { get; init; }
}
