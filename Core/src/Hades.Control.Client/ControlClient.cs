using System.Net;
using System.Net.Http.Headers;
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

    // MARK: - Request plumbing

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
            throw new ControlClientException(ControlClientError.Server, message);
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
