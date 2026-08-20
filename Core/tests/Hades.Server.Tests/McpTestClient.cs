using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hades.Server.Tests;

/// <summary>Shared helper for driving the MCP endpoint over HTTP the way a real client does.</summary>
public static class McpTestClient
{
    public const string Version = "2026-07-28";

    public static object Meta() => new Dictionary<string, object>
    {
        ["io.modelcontextprotocol/protocolVersion"] = Version,
        ["io.modelcontextprotocol/clientInfo"] = new { name = "test", version = "1" },
        ["io.modelcontextprotocol/clientCapabilities"] = new { },
    };

    public static HttpRequestMessage Request(object body, string? headerMethod, string? headerVersion,
        string? toolName = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(body) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (headerVersion is not null) request.Headers.Add("MCP-Protocol-Version", headerVersion);
        if (headerMethod is not null) request.Headers.Add("Mcp-Method", headerMethod);
        if (toolName is not null) request.Headers.Add("Mcp-Name", toolName);
        return request;
    }

    /// <summary>Responses may arrive as one JSON object or as SSE; unwrap either.</summary>
    public static async Task<JsonElement> ReadEnvelope(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        var payload = body.Split('\n')
            .Select(line => line.StartsWith("data: ", StringComparison.Ordinal) ? line[6..] : line)
            .FirstOrDefault(line => line.TrimStart().StartsWith('{')) ?? body;

        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    /// <summary>
    /// The human-readable text of a failed call, whether the SDK surfaced it as a JSON-RPC error
    /// or as an isError tool result. Reads the decoded string rather than matching raw JSON —
    /// apostrophes arrive escaped as \u0027, so substring matching on GetRawText() silently fails.
    /// </summary>
    public static string ErrorText(JsonElement envelope)
    {
        if (envelope.TryGetProperty("error", out var error))
            return error.GetProperty("message").GetString() ?? "";

        if (envelope.TryGetProperty("result", out var result)
            && result.TryGetProperty("content", out var content)
            && content.GetArrayLength() > 0)
        {
            return content[0].GetProperty("text").GetString() ?? "";
        }

        return envelope.GetRawText();
    }

    public static async Task<JsonElement> ListTools(WebApplicationFactory<Program> factory)
    {
        var body = new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { _meta = Meta() } };
        return await ReadEnvelope(
            await factory.CreateClient().SendAsync(Request(body, "tools/list", Version)));
    }

    public static async Task<JsonElement> CallTool(WebApplicationFactory<Program> factory,
        string tool, object? arguments = null)
    {
        var body = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments = arguments ?? new { }, _meta = Meta() },
        };
        return await ReadEnvelope(
            await factory.CreateClient().SendAsync(Request(body, "tools/call", Version, tool)));
    }
}
