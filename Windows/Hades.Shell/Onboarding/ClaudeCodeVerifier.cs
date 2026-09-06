using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Hades.Control.Client;

namespace Hades.Shell.Onboarding;

public enum ClaudeCodeVerificationKind
{
    NotVerified,
    Verifying,

    /// <summary>The core answered a real MCP tools/list call. <see cref="ClaudeCodeVerification.ToolCount"/>
    /// is how many tools it reported.</summary>
    Reachable,

    /// <summary>Every way the check can fail, collapsed into one: no discovery file, settings
    /// unreachable, the MCP request timing out, a non-2xx, an unparsable body. There is deliberately
    /// no failure text - a transport failure means the core produced no message to render.</summary>
    Unreachable,
}

public readonly record struct ClaudeCodeVerification(ClaudeCodeVerificationKind Kind, int ToolCount)
{
    public static readonly ClaudeCodeVerification NotVerified = new(ClaudeCodeVerificationKind.NotVerified, 0);
    public static readonly ClaudeCodeVerification Verifying = new(ClaudeCodeVerificationKind.Verifying, 0);

    public static ClaudeCodeVerification Reachable(int toolCount) =>
        new(ClaudeCodeVerificationKind.Reachable, toolCount);

    public static ClaudeCodeVerification Unreachable(int _ = 0) =>
        new(ClaudeCodeVerificationKind.Unreachable, 0);
}

public interface IClaudeCodeVerifier
{
    Task<ClaudeCodeVerification> VerifyAsync();
}

/// <summary>
/// The one thing onboarding can honestly check from inside itself. The port of
/// <c>Onboarding/ClaudeCodeVerifying.swift</c>.
///
/// <b>WHAT THIS PROVES.</b> Two live facts in sequence: the control API is reachable and reports an
/// <c>mcpPort</c> (read from <c>GET /control/settings</c>, so a port override from a conflict remedy
/// is honoured rather than assuming the documented default); and a raw MCP <c>tools/list</c>
/// JSON-RPC call to <c>http://127.0.0.1:{that port}/mcp</c> comes back well-formed with one or more
/// tools. That is "the core is up and serving N tools".
///
/// <b>WHAT THIS DOES NOT PROVE.</b> That Claude Code itself has connected. This never inspects Claude
/// Code's own state - no shelling out to <c>claude mcp list</c>, no reading its config. Both would
/// mean touching another program's files, or depending on a CLI that may not be on PATH inside the
/// very app the check runs from. "The core is up" and "Claude Code can see it" are different claims,
/// and the step's copy says so in those words.
///
/// Not unit tested: it genuinely dials a loopback socket. The view model fakes the interface instead.
/// </summary>
public sealed class LiveClaudeCodeVerifier(HttpClient? httpClient = null) : IClaudeCodeVerifier
{
    /// <summary>
    /// Mirrors the MCP SDK's negotiated revision. Duplicated deliberately and never referenced: the
    /// server is unreachable from a client by design, so this is the same "keep in sync if that
    /// constant changes" rule the rest of the port already accepts.
    /// </summary>
    const string McpProtocolVersion = "2026-07-28";

    readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<ClaudeCodeVerification> VerifyAsync()
    {
        if (Discovery.Read(ClientPaths.DefaultRoot()) is not { } connection)
        {
            return ClaudeCodeVerification.Unreachable();
        }

        int port;
        try
        {
            using var controlHttp = new HttpClient();
            port = (await new ControlClient(connection, controlHttp).SettingsAsync().ConfigureAwait(false)).McpPort.Port;
        }
        catch (Exception)
        {
            return ClaudeCodeVerification.Unreachable();
        }

        return await CheckToolsListAsync(port).ConfigureAwait(false);
    }

    /// <summary>The raw MCP call. Internal so a live check can target a port directly.</summary>
    internal async Task<ClaudeCodeVerification> CheckToolsListAsync(int port)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp");

            // Both headers are required by the MCP SDK's transport - the server's own conformance
            // tests prove neither is optional. This revision needs no prior `initialize`, so one
            // POST is the whole check.
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", McpProtocolVersion);
            request.Headers.TryAddWithoutValidation("Mcp-Method", "tools/list");

            // Concatenated rather than an interpolated raw string: the JSON-RPC envelope's own
            // braces collide with raw-string interpolation delimiters, and escaping them is far
            // less readable than this.
            var envelope =
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"_meta\":{"
                + "\"io.modelcontextprotocol/protocolVersion\":\"" + McpProtocolVersion + "\","
                + "\"io.modelcontextprotocol/clientInfo\":{\"name\":\"Hades onboarding\",\"version\":\"1\"},"
                + "\"io.modelcontextprotocol/clientCapabilities\":{}}}}";

            request.Content = new StringContent(envelope, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return ClaudeCodeVerification.Unreachable();

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            return ToolCount(body) is { } count
                ? ClaudeCodeVerification.Reachable(count)
                : ClaudeCodeVerification.Unreachable();
        }
        catch (Exception)
        {
            return ClaudeCodeVerification.Unreachable();
        }
    }

    /// <summary>
    /// Unwraps a plain-JSON or SSE-wrapped (<c>data: {...}</c>) body the same way the server's own
    /// test client does, then counts <c>result.tools</c> - a raw array length, never a tool's name
    /// or shape, because nothing here needs either.
    /// </summary>
    internal static int? ToolCount(string body)
    {
        var jsonLine = body
            .Split('\n')
            .Select(line => line.StartsWith("data: ", StringComparison.Ordinal) ? line[6..] : line)
            .FirstOrDefault(line => line.TrimStart().StartsWith('{')) ?? body;

        try
        {
            using var document = JsonDocument.Parse(jsonLine);

            return document.RootElement.TryGetProperty("result", out var result)
                   && result.TryGetProperty("tools", out var tools)
                   && tools.ValueKind == JsonValueKind.Array
                ? tools.GetArrayLength()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
