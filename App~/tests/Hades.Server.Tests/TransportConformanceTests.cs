using System.Text.Json;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// Pins the transport behaviour Hades relies on the MCP SDK to provide. Nothing here tests Hades
/// code — it tests that an SDK upgrade has not regressed spec conformance underneath us. If one
/// of these fails after a bump, implement the behaviour in Hades rather than relaxing the test.
/// </summary>
public class TransportConformanceTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public TransportConformanceTests(WebApplicationFactory<Program> factory)
    {
        // Isolates AppPaths to a throwaway directory - without this, EditorListener (started
        // unconditionally by Program.cs) would write a real token file into this machine's actual
        // ~/Library/Application Support/Hades/, capable of hijacking a real Editor's reconnect
        // target. Same pattern as every other WebApplicationFactory-based fixture in this project.
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));
            }));
    }

    public void Dispose()
    {
        // See EditorToolTestBase.Dispose's own comment: _factory is a fresh per-test
        // WebApplicationFactory whose own background services can still be touching _appRoot
        // until the host itself is disposed - which must happen before the delete below.
        _factory.Dispose();

        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }

    static object ListBody() => new
    {
        jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { _meta = McpTestClient.Meta() },
    };

    static int ErrorCode(JsonElement envelope) =>
        envelope.GetProperty("error").GetProperty("code").GetInt32();

    [Fact]
    public async Task WellFormedRequestSucceeds()
    {
        var envelope = await McpTestClient.ListTools(_factory);
        Assert.True(envelope.TryGetProperty("result", out _), envelope.GetRawText());
    }

    [Fact]
    public async Task MissingProtocolVersionHeaderIsRejected()
    {
        var envelope = await McpTestClient.ReadEnvelope(await _factory.CreateClient()
            .SendAsync(McpTestClient.Request(ListBody(), "tools/list", headerVersion: null)));

        Assert.Equal(-32020, ErrorCode(envelope));
    }

    [Fact]
    public async Task McpMethodHeaderMismatchIsRejected()
    {
        var envelope = await McpTestClient.ReadEnvelope(await _factory.CreateClient()
            .SendAsync(McpTestClient.Request(ListBody(), "tools/call", McpTestClient.Version)));

        Assert.Equal(-32020, ErrorCode(envelope));
    }

    [Fact]
    public async Task EveryToolAdvertisesSchemasAndAnnotations()
    {
        var tools = (await McpTestClient.ListTools(_factory)).GetProperty("result").GetProperty("tools");
        Assert.True(tools.GetArrayLength() >= 3, tools.GetRawText());

        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString();
            Assert.Equal(JsonValueKind.Object, tool.GetProperty("inputSchema").ValueKind);
            Assert.True(tool.TryGetProperty("outputSchema", out _), $"{name} lacks outputSchema");

            // The SDK must advertise a readOnlyHint for every tool — that it does is transport
            // conformance. Which value a given tool gets is a per-tool business decision (most
            // are read-only; hades_rebuild_graph deliberately is not), pinned individually where
            // each tool's own tests live, not asserted uniformly here.
            var readOnlyHint = tool.GetProperty("annotations").GetProperty("readOnlyHint");
            Assert.True(readOnlyHint.ValueKind is JsonValueKind.True or JsonValueKind.False,
                $"{name}'s readOnlyHint is not a boolean");
        }
    }

    [Fact]
    public async Task ToolListOrderingIsDeterministic()
    {
        // Spec: deterministic ordering lets clients cache the list and improves prompt-cache hit
        // rates. Tools live in the model's context, so churn is a real cost.
        static async Task<List<string?>> Names(WebApplicationFactory<Program> f) =>
            (await McpTestClient.ListTools(f)).GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();

        Assert.Equal(await Names(_factory), await Names(_factory));
    }
}
