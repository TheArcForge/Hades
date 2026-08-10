using System.Text.Json;
using Hades.Core;
using Hades.Core.Memory;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// The four memory tools, driven over MCP exactly as an agent would, against the real
/// Hades-Unity-Client corpus (imported from its .arcforge/memory - see
/// RealProjectMemoryImportSmokeTest in Hades.Core.Tests) rather than a synthetic fixture. Same
/// directory-exists-guard pattern as that test and RealProjectSummaryToolSmokeTest - a local
/// sanity check, skipped rather than failing on a machine that does not have this checkout.
///
/// Adoption happens into a fresh, throwaway app-storage root (<see cref="_appRoot"/>) for every
/// test - RealProject itself is only ever read (AdoptAndIndex's own .arcforge import copies FROM
/// it, never writes back INTO it - see MemoryStore.ImportFromArcforge), and this fixture's own
/// Dispose deletes nothing but that throwaway root.
/// </summary>
public class RealProjectMemoryToolsSmokeTest : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    const string RealProject = "/Users/mike/Projects/Hades-Unity-Client";

    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public RealProjectMemoryToolsSmokeTest(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));
            }));
    }

    static JsonElement Structured(JsonElement envelope) =>
        envelope.GetProperty("result").GetProperty("structuredContent");

    [Fact]
    public async Task GetMemorySummary_ListsAllSixKnownDocumentsFromTheRealCorpus()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "get_memory_summary"));

        Assert.True(structured.GetProperty("hasMemory").GetBoolean());
        var docs = structured.GetProperty("documents").EnumerateArray().ToList();

        Console.WriteLine($"[get_memory_summary] {structured.GetProperty("message").GetString()}");
        foreach (var d in docs)
        {
            var lastReviewed = d.TryGetProperty("lastReviewed", out var lr) ? lr.GetString() : "(none)";
            Console.WriteLine($"  {d.GetProperty("name").GetString(),-16} {d.GetProperty("sizeBytes").GetInt64(),5} bytes  last_reviewed={lastReviewed}");
        }

        foreach (var known in MemoryStore.KnownDocuments)
            Assert.Contains(docs, d => d.GetProperty("name").GetString() == known);

        // proposals/ must never leak into this listing - 29 proposal files exist in the real
        // corpus (see RealProjectMemoryImportSmokeTest), which would make this assertion fail
        // immediately if the scoping regressed to include them.
        Assert.Equal(MemoryStore.KnownDocuments.Count, docs.Count);
    }

    [Fact]
    public async Task RecallMemory_RankedResultsForARealQueryAgainstRealContent()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        // "renderer" is real vocabulary from the real decisions.md (the URP decision), mentioned
        // several times - including inside its own embedded <!-- hades-validation --> block, which
        // is part of the document's Body exactly as much as its prose is.
        var structured = Structured(await McpTestClient.CallTool(_factory, "recall_memory", new { query = "renderer" }));
        var results = structured.GetProperty("results").EnumerateArray().ToList();

        Console.WriteLine("[recall_memory query=\"renderer\"]");
        foreach (var r in results)
        {
            Console.WriteLine($"  {r.GetProperty("document").GetString()}  score={r.GetProperty("score").GetDouble():F4}");
            Console.WriteLine($"    excerpt: {r.GetProperty("excerpt").GetString()}");
        }

        Assert.NotEmpty(results);
        Assert.Equal("decisions.md", results[0].GetProperty("document").GetString());
        Assert.True(results[0].GetProperty("score").GetDouble() > 0);

        // A second, real multi-document query: both glossary.md ("...used in this project.") and
        // patterns.md ("...the project uses.") independently mention "project" - real content that
        // happens to give a ranked, multi-hit result without any synthetic fixture involved.
        var second = Structured(await McpTestClient.CallTool(_factory, "recall_memory", new { query = "project" }));
        var secondResults = second.GetProperty("results").EnumerateArray().ToList();

        Console.WriteLine("[recall_memory query=\"project\"]");
        foreach (var r in secondResults)
            Console.WriteLine($"  {r.GetProperty("document").GetString()}  score={r.GetProperty("score").GetDouble():F4}");

        Assert.Contains(secondResults, r => r.GetProperty("document").GetString() == "glossary.md");
        Assert.Contains(secondResults, r => r.GetProperty("document").GetString() == "patterns.md");

        // recall_memory must never surface the real corpus's 29 proposals as if they were citable
        // guidance - none of the returned document names may be a proposals/ path.
        Assert.DoesNotContain(results, r => r.GetProperty("document").GetString()!.StartsWith("proposals/"));
    }

    [Fact]
    public async Task ValidateMemory_ReportsNoFalsePositivesAgainstTheRealAuthoredCorpus()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "validate_memory"));
        var results = structured.GetProperty("results").EnumerateArray().ToList();

        Console.WriteLine($"[validate_memory] {results.Count} finding(s) against the real corpus:");
        foreach (var r in results)
            Console.WriteLine($"  {r.GetProperty("document").GetString()}: {r.GetProperty("scriptPath").GetString()}");

        // decisions.md's backtick spans (`*_RPAsset`, `*_Renderer`, `Assets/Settings/`) are
        // wildcard naming patterns and a bare folder, never a literal ".cs" path - honestly, this
        // is expected to find nothing today. Asserted as "no false positives" (every reported
        // finding, if any, must genuinely not resolve) rather than a hard-coded zero, so a future
        // edit to the real corpus that legitimately adds a stale script mention does not turn this
        // smoke test into a false alarm about validate_memory itself.
        Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.GetProperty("scriptPath").GetString())));
    }

    [Fact]
    public async Task ProposeMemoryUpdate_AgainstTheRealCorpusWritesOnlyToTheThrowawayProposalsDirectory()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var memoryDir = _factory.Services.GetRequiredService<AppPaths>().MemoryDir(
            _factory.Services.GetRequiredService<ProjectService>().KnownProjects().Single().ProductGuid);
        var conventionsBefore = File.ReadAllText(Path.Combine(memoryDir, "conventions.md"));

        var structured = Structured(await McpTestClient.CallTool(_factory, "propose_memory_update", new
        {
            targetFile = "conventions.md",
            content = "Smoke-test proposal - safe to ignore.",
            rationale = "RealProjectMemoryToolsSmokeTest verification run.",
        }));

        var fileName = structured.GetProperty("fileName").GetString()!;
        Console.WriteLine($"[propose_memory_update] wrote {fileName}");

        // Defect fix: a plain basename, not a "proposals/"-prefixed path - see MemoryToolsTests's
        // own identical assertion for the full reasoning.
        Assert.DoesNotContain('/', fileName);
        Assert.True(File.Exists(Path.Combine(memoryDir, "proposals", fileName)));

        // Byte-identical, and - the strongest form of this guarantee - never anywhere near the
        // real Hades-Unity-Client checkout: everything written above lives under the throwaway
        // _appRoot, not RealProject.
        Assert.Equal(conventionsBefore, File.ReadAllText(Path.Combine(memoryDir, "conventions.md")));
        Assert.StartsWith(_appRoot, memoryDir);
    }

    public void Dispose()
    {
        // See EditorToolTestBase.Dispose's own comment: _factory is a fresh per-test
        // WebApplicationFactory whose own background services can still be touching _appRoot
        // until the host itself is disposed - which must happen before the delete below.
        _factory.Dispose();

        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
