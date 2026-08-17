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
    public async Task SearchByName_KindParameterIsAdvertisedAsSupportingImportedAssetKinds()
    {
        // search_by_name's 'kind' filter covers imported assets too (Texture2D, Model, AudioClip,
        // ...), not just script-declared kinds (Class, Struct, ...) - the parameter's own
        // Description is the only place a caller learns that without trial and error.
        var tool = Assert.Single((await McpTestClient.ListTools(_factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "search_by_name");

        var kindDescription = tool.GetProperty("inputSchema").GetProperty("properties")
            .GetProperty("kind").GetProperty("description").GetString()!;

        Assert.Contains("Texture2D", kindDescription);
        Assert.Contains("Model", kindDescription);
        Assert.Contains("AudioClip", kindDescription);
    }

    // ---------------------------------------------------------------- F7-sibling: unrecognised 'kind' errors, never a silent empty set
    //
    // search_by_name.kind never got graph_query.kind's own F7 fix (QueryTools.GraphQuery): a typo
    // or wrong-case kind silently returned {results: [], totalReturned: 0}, indistinguishable from
    // a genuinely empty area of the graph - even though CLAUDE.md tells an agent to reach for this
    // tool FIRST. Same fix, same vocabulary source (ProjectService.KnownNodeKinds) as graph_query's
    // own check, so the two tools can never name a different "known kind" list.

    [Fact]
    public async Task SearchByName_UnrecognisedKind_ErrorsInsteadOfSilentlyEmpty()
    {
        // "Texture" - a retired v1.2 term, never a real node kind (the real one is "Texture2D",
        // per this tool's own [Description]) - used to return an ordinary-looking empty result.
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "player", kind = "Texture" }));

        Assert.Contains("Texture", text);
        Assert.Contains("does not match any node kind", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("case-sensitive", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Class", text);
        Assert.Contains("Interface", text);
    }

    [Fact]
    public async Task SearchByName_WrongCaseKind_Errors()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "player", kind = "class" }));

        Assert.Contains("class", text);
        Assert.Contains("Class", text); // the real, correctly-cased kind
    }

    [Fact]
    public async Task SearchByName_ValidKindWithNoNameMatch_StillAnOrdinaryEmptyResult_NotAnError()
    {
        // The same F7 caveat graph_query's own check documents: a REAL kind combined with a
        // namePattern that happens to match nothing must stay an ordinary empty result, not a
        // false-positive error - this only fires when 'kind' ITSELF is the reason nothing matched.
        var structured = Structured(await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "zzzznomatchzzzz", kind = "Class" }));

        Assert.Equal(0, structured.GetProperty("totalReturned").GetInt32());
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

    // ---------------------------------------------------------------- F13a: unknown parameters are refused, not dropped
    //
    // Defect: a misspelled or invented parameter (a typo, or a caller remembering a retired v1.2
    // name) was silently ignored - a caller asking for a filtered result got an unfiltered one that
    // LOOKS filtered, with no error anywhere. Fixed as a server-side check in Program.cs's own
    // CallToolFilters, ahead of the tool's own body: an unknown argument name refuses the WHOLE
    // call, before any work happens, naming both the unknown parameter and the tool's real ones -
    // the same "refused, not ignored" convention OperationFieldValidator already established for an
    // unrecognised FIELD inside a batch operation (see that class's own doc comment), extended here
    // to top-level tool parameters, which nothing previously checked at all.

    [Fact]
    public async Task UnknownParameter_RejectsTheWholeCall_NamingItAndTheValidParameters()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "player", frobnicate = "Class" }));

        Assert.Contains("frobnicate", text);
        Assert.Contains("search_by_name", text);
        // Every one of search_by_name's real parameters is named, so a caller can self-correct
        // without guessing or re-reading the tool's own schema.
        Assert.Contains("namePattern", text);
        Assert.Contains("kind", text);
        Assert.Contains("limit", text);
        Assert.Contains("project", text);
    }

    [Fact]
    public async Task UnknownParameter_ResultIsAToolErrorNotASilentlyFilteredSuccess()
    {
        // The exact live symptom: a caller asking for a filtered result must never get back an
        // unfiltered one that looks filtered - the whole call is refused instead, zero results
        // returned either way.
        var envelope = await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "player", frobnicate = "Class" });

        var result = envelope.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean(), envelope.GetRawText());
    }

    [Fact]
    public async Task UnknownParameter_OnADifferentToolWithManyParameters_IsStillCaught()
    {
        // Proves the check is generic (schema-driven), not hardcoded to one tool's own parameter
        // list - graph_query has an entirely different parameter set from search_by_name.
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "Class", edgeTarget = "Assets/Scripts/PlayerController.cs" }));

        Assert.Contains("edgeTarget", text);
        Assert.Contains("graph_query", text);
    }

    [Fact]
    public async Task UnknownParameter_MisspellingAnOptionalKnownOne_IsRefusedNotSilentlyIgnored()
    {
        // The core symptom, concretely: 'knd' (typo for the OPTIONAL 'kind') would otherwise be
        // silently dropped, and the call would succeed with an UNFILTERED result indistinguishable
        // from a correctly-filtered one - the caller has no way to know their filter was ignored.
        // Must be refused instead, naming the misspelling - not silently coerced to the parameter
        // it merely resembles.
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "a", knd = "Interface" }));

        Assert.Contains("knd", text);
    }

    [Fact]
    public async Task OnlyKnownParameters_CallSucceedsNormally_NoFalsePositive()
    {
        // Regression guard for the check itself: a call using only real parameters (including the
        // optional ones) must never be refused.
        var structured = Structured(await McpTestClient.CallTool(_factory, "search_by_name",
            new { namePattern = "player", kind = "Class", limit = 10, project = "aaaabbbbccccddddeeeeffff00001111" }));

        Assert.Equal(1, structured.GetProperty("totalReturned").GetInt32());
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
