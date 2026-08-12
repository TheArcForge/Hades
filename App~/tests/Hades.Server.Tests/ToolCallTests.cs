using System.Text.Json;
using Hades.Core;
using Hades.Core.Graph;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>Drives tools over HTTP, not by calling C# directly — this is the whole path the
/// agent actually uses.</summary>
public class ToolCallTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ToolCallTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            "  productGUID: aaaabbbbccccddddeeeeffff00001111\n");

        var scripts = Path.Combine(_projectRoot, "Assets", "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "PlayerController.cs"),
            "using UnityEngine;\npublic class PlayerController : MonoBehaviour { }");
        File.WriteAllText(Path.Combine(scripts, "IDamageable.cs"),
            "public interface IDamageable { }");

        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));
            }));

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(_projectRoot);
    }

    static JsonElement Structured(JsonElement envelope) =>
        envelope.GetProperty("result").GetProperty("structuredContent");

    [Fact]
    public async Task SearchByName_FindsAnIndexedType()
    {
        var structured = Structured(
            await McpTestClient.CallTool(_factory, "search_by_name", new { namePattern = "player" }));

        Assert.Equal(1, structured.GetProperty("totalReturned").GetInt32());
        var first = structured.GetProperty("results")[0];
        Assert.Equal("PlayerController", first.GetProperty("name").GetString());
        Assert.Equal("Assets/Scripts/PlayerController.cs", first.GetProperty("path").GetString());
    }

    [Fact]
    public async Task SearchByName_FiltersByKind()
    {
        var results = Structured(await McpTestClient.CallTool(
            _factory, "search_by_name", new { namePattern = "a", kind = "Interface" })).GetProperty("results");

        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("IDamageable", results[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task SearchByName_ReportsTruncationWhenCapped()
    {
        var structured = Structured(await McpTestClient.CallTool(
            _factory, "search_by_name", new { namePattern = "a", limit = 1 }));

        Assert.Equal(1, structured.GetProperty("totalReturned").GetInt32());
        Assert.True(structured.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task SearchByName_AnOverMaxLimit_StillReportsTruncatedHonestly()
    {
        // Defect: search_by_name passed a raw, caller-supplied limit + 1 straight to
        // ProjectService.Search without first clamping it to search_by_name's own documented
        // maximum (200) - so a limit ABOVE 200 (e.g. an agent ignoring, or not knowing, the
        // documented range) skipped that clamp entirely, and truncated was computed against the
        // UNCLAMPED limit instead. 300 real matches (over the documented max of 200) are seeded
        // directly into this test's already-adopted project's graph database, bypassing the
        // (unrelated, already-proven-correct) Roslyn indexer, since this is pure limit/clamp
        // arithmetic, not an indexing question - same technique as QueryToolsTests'/
        // SummaryToolTests' own "at the documented max" truncation tests for graph_query/
        // get_recently_changed.
        var paths = _factory.Services.GetRequiredService<AppPaths>();
        using (var db = GraphDatabase.Open(paths.GraphDb("aaaabbbbccccddddeeeeffff00001111")))
        {
            db.UpsertNodes(Enumerable.Range(0, 300)
                .Select(i => new GraphNode { Kind = "Class", Name = $"Bulk{i}", Path = $"Assets/Bulk/Bulk{i}.cs" })
                .ToList());
        }

        var structured = Structured(await McpTestClient.CallTool(
            _factory, "search_by_name", new { namePattern = "Bulk", limit = 10000 }));

        // totalReturned must never exceed search_by_name's own documented maximum, regardless of
        // what the caller asked for - the sentinel is for detection, not delivery.
        Assert.Equal(200, structured.GetProperty("totalReturned").GetInt32());
        Assert.True(structured.GetProperty("truncated").GetBoolean(),
            "300 real matches exist but the documented max is 200 - truncated must be true even though the caller asked for 10000");
    }

    [Fact]
    public async Task SearchByName_AlsoMirrorsJsonIntoATextBlock()
    {
        // Spec: a tool returning structured content SHOULD also return the serialized JSON in a
        // TextContent block, for clients that do not read structuredContent.
        var content = (await McpTestClient.CallTool(_factory, "search_by_name", new { namePattern = "player" }))
            .GetProperty("result").GetProperty("content");

        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Contains("PlayerController", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetProjectSummary_ReportsNodeCounts()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "get_project_summary"));

        Assert.Equal(2, structured.GetProperty("totalNodes").GetInt32());
        Assert.Equal(1, structured.GetProperty("nodesByKind").GetProperty("Class").GetInt32());
        Assert.Equal(1, structured.GetProperty("nodesByKind").GetProperty("Interface").GetInt32());
    }

    [Fact]
    public async Task GetProjectSummary_ReportsAppliedDefines()
    {
        // Plan 15 Task 3 Step 4: the define set applied while indexing must be STATED, not left
        // for a caller to discover only by noticing #if-guarded code is missing.
        var structured = Structured(await McpTestClient.CallTool(_factory, "get_project_summary"));

        var appliedDefines = structured.GetProperty("appliedDefines").EnumerateArray()
            .Select(e => e.GetString()).ToList();

        Assert.Contains("UNITY_EDITOR", appliedDefines);
    }

    [Fact]
    public async Task HadesStatus_HandsOutProjectHandles()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_status"));

        Assert.False(string.IsNullOrEmpty(structured.GetProperty("version").GetString()));

        var project = structured.GetProperty("knownProjects")[0];
        Assert.Equal("aaaabbbbccccddddeeeeffff00001111", project.GetProperty("project").GetString());
        Assert.Equal(Path.GetFileName(_projectRoot), project.GetProperty("name").GetString());

        // Exactly one project known, so tools may omit the handle.
        Assert.Equal("aaaabbbbccccddddeeeeffff00001111", structured.GetProperty("defaultProject").GetString());
    }

    [Fact]
    public async Task AcceptsAnExplicitProjectHandle()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "player", project = "aaaabbbbccccddddeeeeffff00001111" }));

        Assert.Equal(1, structured.GetProperty("totalReturned").GetInt32());
    }

    [Fact]
    public async Task AcceptsAProjectNameAsAHandle()
    {
        // The id is what hades_status hands out, but a model that has seen a name will reach
        // for it — accepted when unambiguous.
        var structured = Structured(await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "player", project = Path.GetFileName(_projectRoot) }));

        Assert.Equal(1, structured.GetProperty("totalReturned").GetInt32());
    }

    [Fact]
    public async Task UnknownProjectHandleNamesTheAlternatives()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "player", project = "not-a-real-handle" }));

        Assert.Contains("Unknown project", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aaaabbbbccccddddeeeeffff00001111", text);
        Assert.Contains("hades_status", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownToolIsRejected()
    {
        var envelope = await McpTestClient.CallTool(_factory, "no_such_tool");

        var rejected = envelope.TryGetProperty("error", out _)
            || (envelope.GetProperty("result").TryGetProperty("isError", out var flag) && flag.GetBoolean());

        Assert.True(rejected, envelope.GetRawText());
    }

    [Fact]
    public async Task BlankPatternReturnsAnActionableError()
    {
        // Anthropic's tool guidance: errors should give "specific and actionable improvements,
        // rather than opaque error codes or tracebacks". This is the REACHABLE bad input — the
        // inputSchema marks namePattern required, so a conforming client cannot omit it, but it
        // can pass an empty one.
        var text = McpTestClient.ErrorText(
            await McpTestClient.CallTool(_factory, "search_by_name", new { namePattern = "  " }));
        Assert.Contains("namePattern", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("call again", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OmittingARequiredArgumentIsReportedAsAToolError()
    {
        // Known SDK limitation, asserted so a future improvement is noticed: omitting a required
        // argument yields only "An error occurred invoking 'search_by_name'." — no indication of
        // WHICH argument. Unreachable for a client that honours inputSchema's `required`, which
        // is why it is documented rather than worked around. If this ever starts naming the
        // parameter, the SDK improved and the workaround note in the plan can go.
        var envelope = await McpTestClient.CallTool(_factory, "search_by_name");

        var result = envelope.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean(), envelope.GetRawText());
        Assert.Contains("search_by_name", result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    public void Dispose()
    {
        // See EditorToolTestBase.Dispose's own comment: _factory is a fresh per-test
        // WebApplicationFactory whose own background services can still be touching
        // _appRoot/_projectRoot until the host itself is disposed - which must happen before
        // the recursive delete below.
        _factory.Dispose();

        TeardownDiagnostics.Delete(_appRoot, _projectRoot);
    }
}
