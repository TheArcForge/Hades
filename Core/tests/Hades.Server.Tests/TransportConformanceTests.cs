using System.Text.Json;
using Hades.Core.Storage;
using Hades.Server.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

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

    // ---------------------------------------------------------------- F5: initialize advertises the real product version
    //
    // serverInfo.version reported the ASSEMBLY version (Kestrel/the SDK's own default, "1.0.0.0")
    // rather than HadesTools.ServerVersion, the actual product version constant - so when the tool
    // list fails to load (F1), the one version string a tester could still read looked like a v1
    // server. Fixed by setting McpServerOptions.ServerInfo explicitly, from the existing constant,
    // at AddMcpServer registration in Program.cs.

    [Fact]
    public async Task InitializeReportsTheProductVersionConstant_NeverTheAssemblyVersion()
    {
        // Uses the SDK's own real McpClient over the TestServer's real in-memory HttpClient - the
        // actual initialize handshake a real MCP client (Claude Code included) performs - rather
        // than a hand-rolled JSON-RPC request: this transport's per-request protocol-version
        // metadata makes a raw POST to "initialize" surprisingly easy to get subtly wrong in ways
        // that have nothing to do with this defect, and the whole point of a transport-level test
        // is to observe what a real client actually sees.
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp") },
            _factory.CreateClient(), NullLoggerFactory.Instance, ownsHttpClient: true);

        await using var client = await McpClient.CreateAsync(transport,
            new McpClientOptions { ClientInfo = new() { Name = "test", Version = "1" } },
            NullLoggerFactory.Instance, CancellationToken.None);

        // The exact product constant, not merely "not the assembly version" - a re-typed literal
        // here would pass today and silently drift the moment ServerVersion next changes.
        Assert.Equal(HadesTools.ServerVersion, client.ServerInfo.Version);
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

    // ---------------------------------------------------------------- F1: no boolean JSON Schema subschemas
    //
    // System.Text.Json's schema exporter represents an 'object'/JsonElement/JsonValue-typed member
    // (e.g. InspectAssetResult.Value, InspectEventListenerResult.Arguments, a Dictionary<string,
    // object?>'s own additionalProperties) as the JSON Schema boolean `true` - "matches anything",
    // legal JSON Schema - rather than the equivalent object form `{}`. Real Claude Code (2.1.220)
    // validates tools/list CLIENT-SIDE and requires an object at every schema position; one bare
    // boolean anywhere rejects the ENTIRE list, taking down all 32 tools, not just the offending
    // one. Verified externally: rewriting the one offending field to `{}` restores every tool.
    //
    // This is the durable guard, not a one-field regression test: it walks every advertised tool's
    // inputSchema/outputSchema recursively, into every position JSON Schema itself treats as a
    // subschema slot (properties/items/additionalProperties/patternProperties/not/allOf/anyOf/
    // oneOf/$defs/...) - the same positions a real validator recurses into - and fails naming the
    // exact tool/path the moment any of them is a bare JSON boolean instead of an object, so a
    // future member typed object/JsonElement/JsonValue anywhere in this codebase is caught here
    // automatically, without this test needing to change.

    [Fact]
    public async Task NoToolSchemaContainsABooleanSubschemaAnywhere()
    {
        var tools = (await McpTestClient.ListTools(_factory)).GetProperty("result").GetProperty("tools");
        Assert.True(tools.GetArrayLength() > 0, "tools/list returned no tools at all");

        var offenders = new List<string>();
        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString();
            offenders.AddRange(BooleanSubschemaPaths(tool.GetProperty("inputSchema"), $"{name}.inputSchema"));

            if (tool.TryGetProperty("outputSchema", out var outputSchema) && outputSchema.ValueKind != JsonValueKind.Null)
                offenders.AddRange(BooleanSubschemaPaths(outputSchema, $"{name}.outputSchema"));
        }

        Assert.True(offenders.Count == 0,
            "Boolean-valued JSON Schema subschema(s) found - each one drops the ENTIRE tool list "
            + "for a client that requires an object there, like real Claude Code: " + string.Join(", ", offenders));
    }

    /// <summary>Recursively finds every position in <paramref name="schema"/> that JSON Schema
    /// itself treats as a subschema slot (not just any JSON boolean anywhere - keywords like
    /// "readOnly" or "uniqueItems" are legitimately boolean-VALUED and are not subschemas) and
    /// reports the dotted path of any that is a bare JSON boolean (<c>true</c>/<c>false</c>)
    /// instead of an object. A boolean subschema is itself a leaf - nothing to recurse into
    /// further.</summary>
    static IEnumerable<string> BooleanSubschemaPaths(JsonElement schema, string path)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            yield return path;
            yield break;
        }

        if (schema.ValueKind != JsonValueKind.Object) yield break;

        foreach (var mapKeyword in new[] { "properties", "patternProperties", "$defs", "definitions" })
        {
            if (!schema.TryGetProperty(mapKeyword, out var map) || map.ValueKind != JsonValueKind.Object) continue;
            foreach (var member in map.EnumerateObject())
                foreach (var hit in BooleanSubschemaPaths(member.Value, $"{path}.{mapKeyword}.{member.Name}"))
                    yield return hit;
        }

        foreach (var singleKeyword in new[] { "additionalProperties", "unevaluatedProperties", "propertyNames", "not", "if", "then", "else", "contains" })
        {
            if (!schema.TryGetProperty(singleKeyword, out var sub)) continue;
            foreach (var hit in BooleanSubschemaPaths(sub, $"{path}.{singleKeyword}"))
                yield return hit;
        }

        foreach (var listKeyword in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!schema.TryGetProperty(listKeyword, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            var i = 0;
            foreach (var item in list.EnumerateArray())
            {
                foreach (var hit in BooleanSubschemaPaths(item, $"{path}.{listKeyword}[{i}]")) yield return hit;
                i++;
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            if (items.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in items.EnumerateArray())
                {
                    foreach (var hit in BooleanSubschemaPaths(item, $"{path}.items[{i}]")) yield return hit;
                    i++;
                }
            }
            else
            {
                foreach (var hit in BooleanSubschemaPaths(items, $"{path}.items")) yield return hit;
            }
        }
    }
}
