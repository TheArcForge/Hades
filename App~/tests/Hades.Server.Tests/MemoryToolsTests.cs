using System.Text.Json;
using Hades.Core;
using Hades.Core.Memory;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// End-to-end, over HTTP, for the four memory tools: get_memory_summary, recall_memory,
/// propose_memory_update, validate_memory. Same fixture style as SummaryToolTests/QueryToolsTests.
/// </summary>
public class MemoryToolsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    const string ProjectGuid = "aaaabbbbccccddddeeeeffff00001111";
    const string ScriptGuid = "aaaa1111aaaa1111aaaa1111aaaa1111";

    AppPaths Paths => _factory.Services.GetRequiredService<AppPaths>();

    void WriteMemoryDoc(string name, string content)
    {
        var dir = Paths.MemoryDir(ProjectGuid);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), content);
    }

    public MemoryToolsTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {ProjectGuid}\n");

        Directory.CreateDirectory(Path.Combine(_projectRoot, "Assets"));
        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "PlayerController.cs"),
            "using UnityEngine;\npublic class PlayerController : MonoBehaviour { }");
        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "PlayerController.cs.meta"),
            $"fileFormatVersion: 2\nguid: {ScriptGuid}\n");

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

    // ---------------------------------------------------------------- get_memory_summary

    [Fact]
    public async Task GetMemorySummary_ReportsNothingRecordedYetForAFreshProject()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "get_memory_summary"));

        Assert.False(structured.GetProperty("hasMemory").GetBoolean());
        Assert.Contains("nothing", structured.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(structured.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task GetMemorySummary_ListsDocumentsWithSizeAndLastReviewedDate()
    {
        WriteMemoryDoc("conventions.md", "---\nlast_reviewed: 2026-05-12\n---\n# Conventions\n");
        WriteMemoryDoc("decisions.md", "# Decisions\n\nNo frontmatter here.\n");

        var structured = Structured(await McpTestClient.CallTool(_factory, "get_memory_summary"));

        Assert.True(structured.GetProperty("hasMemory").GetBoolean());
        Assert.Contains("2", structured.GetProperty("message").GetString());

        var docs = structured.GetProperty("documents").EnumerateArray().ToList();
        Assert.Equal(2, docs.Count);

        var conventions = docs.Single(d => d.GetProperty("name").GetString() == "conventions.md");
        Assert.Equal("2026-05-12", conventions.GetProperty("lastReviewed").GetString());
        Assert.True(conventions.GetProperty("sizeBytes").GetInt64() > 0);

        var decisions = docs.Single(d => d.GetProperty("name").GetString() == "decisions.md");
        Assert.False(decisions.TryGetProperty("lastReviewed", out _));
    }

    [Fact]
    public async Task GetMemorySummary_DoesNotIncludeProposals()
    {
        WriteMemoryDoc("conventions.md", "# Conventions\n");
        var proposalsDir = Path.Combine(Paths.MemoryDir(ProjectGuid), "proposals");
        Directory.CreateDirectory(proposalsDir);
        File.WriteAllText(Path.Combine(proposalsDir, "idea.md"), "An idea.\n");

        var structured = Structured(await McpTestClient.CallTool(_factory, "get_memory_summary"));

        var docs = structured.GetProperty("documents").EnumerateArray().ToList();
        var hit = Assert.Single(docs);
        Assert.Equal("conventions.md", hit.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetMemorySummary_IsAdvertisedAsReadOnlyWithASchema()
    {
        var tool = Assert.Single((await McpTestClient.ListTools(_factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "get_memory_summary");

        Assert.True(tool.TryGetProperty("outputSchema", out _));
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    // ---------------------------------------------------------------- recall_memory

    [Fact]
    public async Task RecallMemory_ReturnsRankedExcerptsWithSourceDocumentNamed()
    {
        // Equal token count in both, so BM25's length normalisation cannot be what decides the
        // order - isolates term frequency (2 hits vs 1) as the only variable.
        WriteMemoryDoc("conventions.md", "widget alpha bravo charlie delta echo");
        WriteMemoryDoc("decisions.md", "widget widget alpha bravo charlie delta");

        var structured = Structured(await McpTestClient.CallTool(_factory, "recall_memory", new { query = "widget" }));
        var results = structured.GetProperty("results").EnumerateArray().ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("decisions.md", results[0].GetProperty("document").GetString());
        Assert.Equal("conventions.md", results[1].GetProperty("document").GetString());
        Assert.True(results[0].GetProperty("score").GetDouble() > results[1].GetProperty("score").GetDouble());
        Assert.False(string.IsNullOrEmpty(results[0].GetProperty("excerpt").GetString()));
    }

    [Fact]
    public async Task RecallMemory_BlankQueryGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "recall_memory", new { query = "  " }));

        Assert.Contains("query", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecallMemory_OrIsNotInterpretedAsABooleanOperator()
    {
        // Unsanitised, "cat OR dog" is a real FTS5 boolean-OR query that would hit all three
        // documents below. Neutralised, only the one document containing all three literal
        // tokens - cat, or, dog - matches. Proven over the real MCP endpoint, not just at the
        // MemoryIndex unit level (see MemoryIndexTests for that).
        WriteMemoryDoc("has-or.md", "We chose cat or dog for the mascot.");
        WriteMemoryDoc("cat-only.md", "The cat sat on the mat.");
        WriteMemoryDoc("dog-only.md", "The dog barked loudly.");

        var structured = Structured(await McpTestClient.CallTool(_factory, "recall_memory", new { query = "cat OR dog" }));
        var results = structured.GetProperty("results").EnumerateArray().ToList();

        var hit = Assert.Single(results);
        Assert.Equal("has-or.md", hit.GetProperty("document").GetString());
    }

    [Fact]
    public async Task RecallMemory_DoesNotSearchProposals()
    {
        WriteMemoryDoc("conventions.md", "Nothing relevant in here.");
        var proposalsDir = Path.Combine(Paths.MemoryDir(ProjectGuid), "proposals");
        Directory.CreateDirectory(proposalsDir);
        File.WriteAllText(Path.Combine(proposalsDir, "idea.md"), "A kumquat-flavoured proposal.");

        var structured = Structured(await McpTestClient.CallTool(_factory, "recall_memory", new { query = "kumquat" }));

        Assert.Empty(structured.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task RecallMemory_IsAdvertisedAsReadOnlyWithASchema()
    {
        var tool = Assert.Single((await McpTestClient.ListTools(_factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "recall_memory");

        Assert.True(tool.TryGetProperty("outputSchema", out _));
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    // ---------------------------------------------------------------- propose_memory_update

    [Fact]
    public async Task ProposeMemoryUpdate_WritesOnlyToProposalsAndLeavesAuthoredDocumentsByteIdentical()
    {
        WriteMemoryDoc("conventions.md", "---\nlast_reviewed: 2026-05-12\n---\n# Conventions\n\nOriginal text.\n");
        WriteMemoryDoc("patterns.md", "# Patterns\n");

        var memoryDir = Paths.MemoryDir(ProjectGuid);
        var conventionsBefore = File.ReadAllText(Path.Combine(memoryDir, "conventions.md"));
        var patternsBefore = File.ReadAllText(Path.Combine(memoryDir, "patterns.md"));

        var structured = Structured(await McpTestClient.CallTool(_factory, "propose_memory_update", new
        {
            targetFile = "patterns.md",
            content = "Relies on object pooling for bullets.",
            rationale = "Observed in 12 prefabs.",
        }));

        var fileName = structured.GetProperty("fileName").GetString()!;
        // Defect fix: fileName is a plain basename, the exact same shape GET /control/memory's own
        // listing reports for the same file and accept/dismiss/defer already require - never a
        // "proposals/"-prefixed path a caller would have to strip before passing it back in.
        Assert.DoesNotContain('/', fileName);
        Assert.EndsWith(".md", fileName);

        var proposalFullPath = Path.Combine(memoryDir, "proposals", fileName);
        Assert.True(File.Exists(proposalFullPath));

        var parsed = MemoryFile.Parse(Path.GetFileName(fileName), File.ReadAllText(proposalFullPath));
        Assert.True(parsed.HasFrontmatter);
        Assert.Null(parsed.FrontmatterError);
        Assert.Equal("patterns.md", parsed.Frontmatter["target_file"]);
        Assert.Equal("Observed in 12 prefabs.", parsed.Frontmatter["rationale"]);
        Assert.Equal("pending", parsed.Frontmatter["status"]);
        Assert.Equal("Relies on object pooling for bullets.", parsed.Body);

        // The whole point: an authored document must be byte-identical after this call, whether
        // or not it is the one targeted by the proposal.
        Assert.Equal(conventionsBefore, File.ReadAllText(Path.Combine(memoryDir, "conventions.md")));
        Assert.Equal(patternsBefore, File.ReadAllText(Path.Combine(memoryDir, "patterns.md")));
    }

    [Fact]
    public async Task ProposeMemoryUpdate_RejectsEmptyTargetFileWithActionableError()
    {
        // The regression case named in the plan: the real Hades-Unity-Client corpus has
        // proposals/20260614-174745-.md from an old system that let an empty target_file through,
        // producing a malformed filename with an empty slug. This must be rejected up front.
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "propose_memory_update",
            new { targetFile = "", content = "Some proposed text." }));

        Assert.Contains("targetFile", text);
    }

    [Fact]
    public async Task ProposeMemoryUpdate_RejectsBlankTargetFileWithActionableError()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "propose_memory_update",
            new { targetFile = "   ", content = "Some proposed text." }));

        Assert.Contains("targetFile", text);
    }

    [Fact]
    public async Task ProposeMemoryUpdate_RejectsNonBasenameTargetFileWithActionableError()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "propose_memory_update",
            new { targetFile = "../escape", content = "Some proposed text." }));

        Assert.Contains("targetFile", text);
    }

    [Fact]
    public async Task ProposeMemoryUpdate_RejectsEmptyContentWithActionableError()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "propose_memory_update",
            new { targetFile = "patterns.md", content = "  " }));

        Assert.Contains("content", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProposeMemoryUpdate_TwoProposalsForTheSameTargetBothSurvive()
    {
        // Guards against the collision a naive timestamp-only filename would risk: two proposals
        // for the same target must never overwrite one another, regardless of timing.
        var first = Structured(await McpTestClient.CallTool(_factory, "propose_memory_update",
            new { targetFile = "patterns.md", content = "First proposal." }));
        var second = Structured(await McpTestClient.CallTool(_factory, "propose_memory_update",
            new { targetFile = "patterns.md", content = "Second proposal." }));

        var firstName = first.GetProperty("fileName").GetString()!;
        var secondName = second.GetProperty("fileName").GetString()!;
        Assert.NotEqual(firstName, secondName);

        var memoryDir = Paths.MemoryDir(ProjectGuid);
        Assert.Contains("First proposal.",
            File.ReadAllText(Path.Combine(memoryDir, "proposals", Path.GetFileName(firstName))));
        Assert.Contains("Second proposal.",
            File.ReadAllText(Path.Combine(memoryDir, "proposals", Path.GetFileName(secondName))));
    }

    [Fact]
    public async Task ProposeMemoryUpdate_IsNotAdvertisedAsReadOnly()
    {
        var tool = Assert.Single((await McpTestClient.ListTools(_factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "propose_memory_update");

        Assert.False(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    // ---------------------------------------------------------------- validate_memory

    [Fact]
    public async Task ValidateMemory_ReportsAMemoryNamingAScriptThatNoLongerExists()
    {
        WriteMemoryDoc("pitfalls.md",
            "Watch out for `Assets/Scripts/DeletedController.cs` - it has a race condition.");

        var structured = Structured(await McpTestClient.CallTool(_factory, "validate_memory"));
        var results = structured.GetProperty("results").EnumerateArray().ToList();

        var hit = Assert.Single(results);
        Assert.Equal("pitfalls.md", hit.GetProperty("document").GetString());
        Assert.Equal("Assets/Scripts/DeletedController.cs", hit.GetProperty("scriptPath").GetString());
    }

    [Fact]
    public async Task ValidateMemory_DoesNotReportAScriptThatStillExists()
    {
        WriteMemoryDoc("conventions.md", "See `Assets/PlayerController.cs` for the canonical example.");

        var structured = Structured(await McpTestClient.CallTool(_factory, "validate_memory"));

        Assert.Empty(structured.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task ValidateMemory_NeverEditsAnyMemoryDocument()
    {
        const string content = "References `Assets/Scripts/GoneNow.cs` which is stale.";
        WriteMemoryDoc("pitfalls.md", content);

        await McpTestClient.CallTool(_factory, "validate_memory");

        Assert.Equal(content, File.ReadAllText(Path.Combine(Paths.MemoryDir(ProjectGuid), "pitfalls.md")));
    }

    [Fact]
    public async Task ValidateMemory_IsAdvertisedAsReadOnlyWithASchema()
    {
        var tool = Assert.Single((await McpTestClient.ListTools(_factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "validate_memory");

        Assert.True(tool.TryGetProperty("outputSchema", out _));
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
