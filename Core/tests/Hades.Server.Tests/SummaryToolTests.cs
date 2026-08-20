using System.Text.Json;
using Hades.Core;
using Hades.Core.Graph;
using Hades.Core.Observation;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// End-to-end, over HTTP, for four summary and lifecycle tools: get_scene_summary,
/// get_recently_changed, hades_rebuild_graph, hades_ping. Same fixture style as
/// FindReferencesToTests/GraphToolsTests. hades_charon_status has its own suite —
/// CharonStatusTests.cs — since exercising its attached/busy states needs a fake Editor plugin
/// dialled in over a real loopback socket, not just an adopted project.
/// </summary>
public class SummaryToolTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff00001111";
    const string ScriptGuid = "aaaa1111aaaa1111aaaa1111aaaa1111";
    const string HierarchyGuid = "dddd4444dddd4444dddd4444dddd4444";

    void Write(string relative, string body, string? guid = null)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        if (guid is not null) File.WriteAllText(full + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    public SummaryToolTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {ProjectGuid}\n");

        Write("Assets/PlayerController.cs", "using UnityEngine;\npublic class PlayerController : MonoBehaviour { }", ScriptGuid);

        // A root GameObject ("Root") with one child ("Child") and one MonoBehaviour on the root —
        // enough to tell GameObjectCount, RootCount, and the component breakdown apart from one
        // another (a flat, all-roots fixture couldn't distinguish RootCount from GameObjectCount).
        Write("Assets/Hierarchy.prefab",
            Header
            + "--- !u!1 &1\nGameObject:\n  m_Name: Root\n"
            + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n  m_Children:\n  - {fileID: 4}\n"
            + "--- !u!1 &3\nGameObject:\n  m_Name: Child\n"
            + "--- !u!4 &4\nTransform:\n  m_GameObject: {fileID: 3}\n  m_Father: {fileID: 2}\n"
            + $"--- !u!114 &5\nMonoBehaviour:\n  m_GameObject: {{fileID: 1}}\n  m_Script: {{fileID: 11500000, guid: {ScriptGuid}, type: 3}}\n",
            HierarchyGuid);

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

    // ---------------------------------------------------------------- get_scene_summary

    [Fact]
    public async Task GetSceneSummary_ReportsGameObjectAndRootCountsAndComponentBreakdown()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "get_scene_summary",
            new { path = "Assets/Hierarchy.prefab" }));

        Assert.Equal("Assets/Hierarchy.prefab", structured.GetProperty("path").GetString());
        Assert.Equal(2, structured.GetProperty("gameObjectCount").GetInt32());
        Assert.Equal(1, structured.GetProperty("rootCount").GetInt32());   // Child is parented, not a root

        var byKind = structured.GetProperty("componentsByKind");
        Assert.Equal(2, byKind.GetProperty("GameObject").GetInt32());
        Assert.Equal(2, byKind.GetProperty("Transform").GetInt32());
        Assert.Equal(1, byKind.GetProperty("MonoBehaviour").GetInt32());
    }

    [Fact]
    public async Task GetSceneSummary_UnknownPathGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "get_scene_summary",
            new { path = "Assets/DoesNotExist.unity" }));

        Assert.Contains("not in the graph", text);
        Assert.Contains("search_by_name", text);
    }

    [Fact]
    public async Task GetSceneSummary_BlankPathGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "get_scene_summary",
            new { path = "  " }));

        Assert.Contains("path", text);
    }

    [Fact]
    public async Task GetSceneSummary_IsAdvertisedAsReadOnlyWithASchema()
    {
        var tool = Assert.Single((await McpTestClient.ListTools(_factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "get_scene_summary");

        Assert.True(tool.TryGetProperty("outputSchema", out _));
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    // ---------------------------------------------------------------- get_recently_changed

    [Fact]
    public async Task GetRecentlyChanged_SortsNewestFirstAndHonoursSince()
    {
        // Explicit, well-separated mtimes: mtime has only one-second granularity on some
        // filesystems, so two files written moments apart in the constructor cannot be trusted
        // to sort deterministically on their natural wall-clock times.
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        var recent = DateTimeOffset.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(Path.Combine(_projectRoot, "Assets", "PlayerController.cs"), old.UtcDateTime);
        File.SetLastWriteTimeUtc(Path.Combine(_projectRoot, "Assets", "Hierarchy.prefab"), recent.UtcDateTime);
        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(_projectRoot);

        var structured = Structured(await McpTestClient.CallTool(_factory, "get_recently_changed"));
        var results = structured.GetProperty("results").EnumerateArray().ToList();

        Assert.Equal("Assets/Hierarchy.prefab", results[0].GetProperty("path").GetString());
        Assert.Contains(results, r => r.GetProperty("path").GetString() == "Assets/PlayerController.cs");

        var filtered = Structured(await McpTestClient.CallTool(_factory, "get_recently_changed",
            new { since = recent.AddMinutes(-30).ToString("O") }));
        var filteredPaths = filtered.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("path").GetString()).ToList();

        Assert.Contains("Assets/Hierarchy.prefab", filteredPaths);
        Assert.DoesNotContain("Assets/PlayerController.cs", filteredPaths);
    }

    [Fact]
    public async Task GetRecentlyChanged_InvalidSinceGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "get_recently_changed",
            new { since = "not-a-timestamp" }));

        Assert.Contains("since", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRecentlyChanged_AtTheDocumentedMaxLimit_ReportsTruncatedHonestlyWhenMoreThan500RealMatchesExist()
    {
        // docs/backlog/graph-correctness-defects.md defect 2 / Plan 15 Task 1. get_recently_changed
        // shares GraphDatabase.RecentlyChanged's limit+1-against-a-shared-clamp shape with
        // graph_query - GraphDatabase.cs:527 is literally the line the defect doc cites - so it is
        // suspect at its own documented maximum (500) for the identical reason. 600 real file_state
        // rows are seeded directly, bypassing the indexer entirely since this is pure limit/clamp
        // arithmetic, not an indexing question.
        var paths = _factory.Services.GetRequiredService<AppPaths>();
        using (var db = GraphDatabase.Open(paths.GraphDb(ProjectGuid)))
        {
            db.UpsertFileState(Enumerable.Range(0, 600)
                .Select(i => new FileState { Path = $"Assets/Bulk/Bulk{i}.cs", MTimeUtcMs = i, Size = 1 })
                .ToList());
        }

        var structured = Structured(await McpTestClient.CallTool(_factory, "get_recently_changed", new { limit = 500 }));

        Assert.Equal(500, structured.GetProperty("totalReturned").GetInt32());
        Assert.True(structured.GetProperty("truncated").GetBoolean(),
            "600 real file_state rows exist but only 500 were requested - truncated must be true");
    }

    [Fact]
    public async Task GetRecentlyChanged_AnOverMaxLimit_StillReportsTruncatedHonestly()
    {
        // A second, related defect on top of the one above: get_recently_changed passed a raw,
        // caller-supplied limit + 1 straight to ProjectService.RecentlyChanged without first
        // clamping it to get_recently_changed's own documented maximum (500) - so a limit ABOVE
        // 500 skipped that clamp entirely, and truncated was computed against the UNCLAMPED limit
        // instead of the documented one.
        var paths = _factory.Services.GetRequiredService<AppPaths>();
        using (var db = GraphDatabase.Open(paths.GraphDb(ProjectGuid)))
        {
            db.UpsertFileState(Enumerable.Range(0, 600)
                .Select(i => new FileState { Path = $"Assets/Bulk/Bulk{i}.cs", MTimeUtcMs = i, Size = 1 })
                .ToList());
        }

        var structured = Structured(await McpTestClient.CallTool(_factory, "get_recently_changed", new { limit = 5000 }));

        // totalReturned must never exceed get_recently_changed's own documented maximum,
        // regardless of what the caller asked for - the sentinel is for detection, not delivery.
        Assert.Equal(500, structured.GetProperty("totalReturned").GetInt32());
        Assert.True(structured.GetProperty("truncated").GetBoolean(),
            "600 real file_state rows exist but the documented max is 500 - truncated must be true even though the caller asked for 5000");
    }

    // ---------------------------------------------------------------- hades_rebuild_graph

    [Fact]
    public async Task RebuildGraph_ForcesAFullReindexAndReportsBeforeAfterCounts()
    {
        Write("Assets/NewScript.cs", "public class NewScript { }");

        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_rebuild_graph"));

        Assert.True(structured.GetProperty("nodesAfter").GetInt32() > structured.GetProperty("nodesBefore").GetInt32());

        var search = Structured(await McpTestClient.CallTool(_factory, "search_by_name", new { namePattern = "NewScript" }));
        Assert.Equal(1, search.GetProperty("totalReturned").GetInt32());
    }

    [Fact]
    public async Task RebuildGraph_IsNotAdvertisedAsReadOnly()
    {
        var tool = Assert.Single((await McpTestClient.ListTools(_factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "hades_rebuild_graph");

        Assert.False(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    [Fact]
    public async Task RebuildGraph_DescriptionIsHonestAboutBeingSynchronousWithNoCancellation()
    {
        // F18-class honesty half of the same fix that made this tool serialize against
        // EnsureIndexed (ProjectServiceTests.RebuildGraph_BlocksWhileEnsureIndexedsGateIsHeld...):
        // a caller deciding whether to await this on a large project needs to know up front that
        // there is no cancellation and no progress reporting, not discover it by timing out.
        var tool = Assert.Single((await McpTestClient.ListTools(_factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "hades_rebuild_graph");

        var description = tool.GetProperty("description").GetString()!;
        Assert.Contains("synchronous", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no cancellation", description, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- hades_ping

    [Fact]
    public async Task Ping_ReturnsVersionAndUptime()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_ping"));

        Assert.False(string.IsNullOrEmpty(structured.GetProperty("version").GetString()));
        Assert.True(structured.GetProperty("uptimeSeconds").GetDouble() >= 0);
    }

    [Fact]
    public async Task Ping_StillWorksWhenTheProjectDatabaseIsBroken()
    {
        // hades_ping's entire job is telling apart "the server is down" from "a project's
        // database is unhealthy" — so it must survive exactly the condition that breaks a
        // DB-touching tool. Break the graph by replacing it with a directory (opening a directory
        // as a SQLite file reliably fails, cross-platform) and prove the contrast.
        var dbPath = _factory.Services.GetRequiredService<AppPaths>().GraphDb(ProjectGuid);
        File.Delete(dbPath);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        Directory.CreateDirectory(dbPath);

        // Microsoft.Data.Sqlite pools native connections per connection string. POSIX lets a file
        // be deleted while a pooled handle still has it open, so without clearing the pool the
        // next Open() would silently keep reading the OLD (deleted) file through that stale
        // handle — the corruption above would never actually be observed.
        SqliteConnection.ClearAllPools();

        var brokenCall = await McpTestClient.CallTool(_factory, "get_project_summary");
        var failed = brokenCall.TryGetProperty("error", out _)
            || (brokenCall.GetProperty("result").TryGetProperty("isError", out var flag) && flag.GetBoolean());
        Assert.True(failed, brokenCall.GetRawText());   // control: this really is broken

        var pingResult = Structured(await McpTestClient.CallTool(_factory, "hades_ping"));
        Assert.False(string.IsNullOrEmpty(pingResult.GetProperty("version").GetString()));
    }

    public void Dispose()
    {
        // See EditorToolTestBase.Dispose's own comment: _factory is a fresh per-test
        // WebApplicationFactory whose own background services can still be touching
        // _appRoot/_projectRoot until the host itself is disposed - which must happen before
        // the recursive delete below.
        _factory.Dispose();

        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
