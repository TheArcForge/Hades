using System.Text.Json;
using Hades.Core;
using Hades.Core.Graph;
using Hades.Core.Observation;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// End-to-end, over HTTP, for query_graph - the only tool in the whole port that takes
/// caller/model-authored query input (see QueryTools' class doc comment). The underlying SQL,
/// including its own dedicated SQL-injection and LIKE-escaping coverage, is already exercised at
/// the GraphDatabase level by QueryGraphTests; this file closes the gap those tests do not: that
/// the tool is wired up, advertised, validates its own arguments (at least one filter, a
/// recognised edge direction), and - the one thing only an end-to-end test can prove - that a
/// caller sending literal SQL through the live MCP endpoint gets back an empty, harmless result
/// with the rest of the tool surface still working normally afterward, not a crashed server or a
/// corrupted graph.
/// </summary>
public class QueryToolsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff00001111";
    const string ScriptGuid = "aaaa1111aaaa1111aaaa1111aaaa1111";
    const string OrphanGuid = "cccc3333cccc3333cccc3333cccc3333";
    const string PrefabGuid = "bbbb2222bbbb2222bbbb2222bbbb2222";

    void Write(string relative, string body, string? guid = null)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        if (guid is not null) File.WriteAllText(full + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    public QueryToolsTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {ProjectGuid}\n");

        Write("Assets/Scripts/PlayerController.cs",
            "using UnityEngine;\npublic class PlayerController : MonoBehaviour { }", ScriptGuid);
        Write("Assets/Scripts/OrphanScript.cs",
            "using UnityEngine;\npublic class OrphanScript : MonoBehaviour { }", OrphanGuid);
        Write("Assets/Player.prefab",
            Header + "--- !u!1 &1\nGameObject:\n  m_Name: Player\n"
            + $"--- !u!114 &2\nMonoBehaviour:\n  m_Script: {{fileID: 11500000, guid: {ScriptGuid}, type: 3}}\n",
            PrefabGuid);
        Write("Assets/Enemy.prefab",
            Header + "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: Enemy\n"
            + "--- !u!54 &2\nRigidbody:\n  m_GameObject: {fileID: 1}\n");
        Write("Assets/Scene.unity",
            Header + "--- !u!1001 &1\nPrefabInstance:\n  m_Modification:\n    m_TransformParent: {fileID: 0}\n"
            + "    m_Modifications: []\n"
            + $"  m_SourcePrefab: {{fileID: 100100000, guid: {PrefabGuid}, type: 3}}\n");
        // A ScriptableObject instance - its one node shares MonoBehaviour's generic kind with an
        // ordinary component, so `kind` alone can never tell this file apart from a component
        // inside a prefab. This is the fixture the fileType tests below need: no node-kind filter
        // could ever isolate it, which is exactly the gap fileType (file_state-backed) closes.
        Write("Assets/Data/Config.asset", Header + "--- !u!114 &11400000\nMonoBehaviour:\n  m_Name: Config\n");

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

    // Plan 10 Task 6 removed query_graph itself (the tool this file used to test under a
    // QueryGraph_* prefix) - folded into graph_query, its own consolidated replacement, which
    // shares the identical underlying GraphDatabase.QueryGraph call and validation, unchanged. Every
    // property those former tests proved (kind/pathPrefix/namePattern filtering, edgeKind/
    // edgeDirection, limit/truncation, the "needs at least one filter" and "unrecognised
    // edgeDirection" validation, the tool's own metadata, and SQL-injection safety across
    // namePattern/pathPrefix/kind) has a direct GraphQuery_* counterpart below, proven through the
    // SAME shared code path.

    // ================================================================== graph_query (Plan 10 Task 4)
    //
    // graph_query is query_graph's structured filter EXTENDED with edgeTargetPath/
    // edgeTargetNamePattern/edgeAbsent - see GraphDatabase.QueryGraph's own doc comment for the SQL,
    // and QueryTools' class doc comment for the full old-tool -> filter-combination map. The tests
    // below reuse this file's SAME fixture (PlayerController.cs/OrphanScript.cs, Player.prefab's
    // MonoBehaviour referencing PlayerController, Enemy.prefab's Rigidbody, Scene.unity's
    // PrefabInstance) rather than a second one, since the whole point is that ONE filter surface now
    // answers what SIX separate tools used to.

    [Fact]
    public async Task GraphQuery_FiltersByKindAndNamePattern_SameBehaviourAsQueryGraph()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "Class", namePattern = "player" }));

        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("PlayerController", hit.GetProperty("name").GetString());
    }

    // ---------------------------------------------------------------- defect 2: truncated lies at limit=500
    //
    // docs/backlog/graph-correctness-defects.md defect 2 / Plan 15 Task 1. graph_query asks for
    // limit+1 rows to detect truncation honestly (see the "limit + 1" comment in GraphQuery
    // below), but GraphDatabase.QueryGraph's own internal ceiling used to be exactly graph_query's
    // own documented maximum (500), so the +1 sentinel row was silently clamped away at exactly
    // that limit. 600 real graph nodes are seeded directly into this test's already-adopted
    // project's graph database - bypassing the (unrelated, already-proven-correct) Roslyn indexer,
    // since this defect is pure limit/clamp arithmetic, not an indexing question.

    [Fact]
    public async Task GraphQuery_AtTheDocumentedMaxLimit_ReportsTruncatedHonestlyWhenMoreThan500RealMatchesExist()
    {
        var paths = _factory.Services.GetRequiredService<AppPaths>();
        using (var db = GraphDatabase.Open(paths.GraphDb(ProjectGuid)))
        {
            db.UpsertNodes(Enumerable.Range(0, 600)
                .Select(i => new GraphNode { Kind = "Class", Name = $"Bulk{i}", Path = $"Assets/Bulk/Bulk{i}.cs", FileId = 0 })
                .ToList());
        }

        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { pathPrefix = "Assets/Bulk", limit = 500 }));

        // totalReturned must never exceed the caller's own requested limit - the sentinel is for
        // detection, not delivery.
        Assert.Equal(500, structured.GetProperty("totalReturned").GetInt32());
        Assert.True(structured.GetProperty("truncated").GetBoolean(),
            "600 real matches exist but only 500 were requested - truncated must be true");
    }

    // ---------------------------------------------------------------- an over-max limit collapses the sentinel too
    //
    // A second, related defect on top of the one above: graph_query passed a raw, caller-supplied
    // limit + 1 straight to ProjectService.QueryGraph without first clamping it to graph_query's
    // own documented maximum (500) - so a limit ABOVE 500 skipped that clamp entirely, and
    // truncated was computed against the UNCLAMPED limit instead of the documented one. Both the
    // structured-filter branch and the separate fileType branch (BuildFileTypeResult) shared the
    // same raw-limit shape, so both get their own test here.

    [Fact]
    public async Task GraphQuery_AnOverMaxLimit_StillReportsTruncatedHonestly()
    {
        var paths = _factory.Services.GetRequiredService<AppPaths>();
        using (var db = GraphDatabase.Open(paths.GraphDb(ProjectGuid)))
        {
            db.UpsertNodes(Enumerable.Range(0, 600)
                .Select(i => new GraphNode { Kind = "Class", Name = $"Bulk{i}", Path = $"Assets/Bulk/Bulk{i}.cs", FileId = 0 })
                .ToList());
        }

        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { pathPrefix = "Assets/Bulk", limit = 5000 }));

        // totalReturned must never exceed graph_query's own documented maximum, regardless of what
        // the caller asked for - the sentinel is for detection, not delivery.
        Assert.Equal(500, structured.GetProperty("totalReturned").GetInt32());
        Assert.True(structured.GetProperty("truncated").GetBoolean(),
            "600 real matches exist but the documented max is 500 - truncated must be true even though the caller asked for 5000");
    }

    [Fact]
    public async Task GraphQuery_FileTypeBranch_AnOverMaxLimit_StillReportsTruncatedHonestly()
    {
        // fileType is a SEPARATE code path (file_state-backed, via FindAssetsByFileState) from the
        // structured-filter branch above - see QueryTools.GraphQuery's own doc comment - so it needs
        // its own proof it was not missed. 600 file_state rows are seeded directly (plus the fixture's
        // own pre-existing Assets/Data/Config.asset - 601 real ScriptableObject matches under
        // Assets/Data in total, still comfortably over the documented max of 500), same technique as
        // SummaryToolTests' own get_recently_changed truncation tests, which read the same table.
        var paths = _factory.Services.GetRequiredService<AppPaths>();
        using (var db = GraphDatabase.Open(paths.GraphDb(ProjectGuid)))
        {
            db.UpsertFileState(Enumerable.Range(0, 600)
                .Select(i => new FileState { Path = $"Assets/Data/Bulk{i}.asset", MTimeUtcMs = i, Size = 1 })
                .ToList());
        }

        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "ScriptableObject", pathPrefix = "Assets/Data", limit = 5000 }));

        Assert.Equal(500, structured.GetProperty("totalReturned").GetInt32());
        Assert.True(structured.GetProperty("truncated").GetBoolean(),
            "600 real file_state rows exist but the documented max is 500 - truncated must be true even though the caller asked for 5000");
    }

    // ---------------------------------------------------------------- the 6 absorbed searches, enumerated

    [Fact]
    public async Task GraphQuery_Enumerated1_QueryGraphsOwnStructuredFilterStillWorks()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeKind = "instance_of", edgeDirection = "outgoing" }));

        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("Assets/Scene.unity", hit.GetProperty("path").GetString());
    }

    [Fact]
    public async Task GraphQuery_Enumerated2_FindPrefabsWithComponent_ViaEdgeTargetPath()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeKind = "references", edgeTargetPath = "Assets/Scripts/PlayerController.cs" }));

        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("Assets/Player.prefab", hit.GetProperty("path").GetString());
    }

    [Fact]
    public async Task GraphQuery_Enumerated3_FindComponentsUsingPattern_ViaEdgeTargetNamePattern()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeKind = "references", edgeTargetNamePattern = "player" }));

        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("Assets/Player.prefab", hit.GetProperty("path").GetString());
    }

    [Fact]
    public async Task GraphQuery_Enumerated4_FindOrphanScripts_ViaEdgeAbsent()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "Class", edgeKind = "references", edgeDirection = "incoming", edgeAbsent = true }));

        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("OrphanScript", hit.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GraphQuery_Enumerated5_ComponentFind_BuiltinKindExactMatch()
    {
        // component_find's direct-kind branch (a builtin like "Rigidbody") - already reachable via
        // the existing exact `kind` filter, no extension needed for this half of it.
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query", new { kind = "Rigidbody" }));

        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("Assets/Enemy.prefab", hit.GetProperty("path").GetString());
    }

    [Fact]
    public async Task GraphQuery_Enumerated5_ComponentFind_BuiltinKindSubstringMatch_ViaKindPattern()
    {
        // component_find's direct-kind branch is a SUBSTRING match (ComponentsMatching's own
        // "LOWER(kind) LIKE pattern"), not exact - "Rigid" must find "Rigidbody" the same way
        // component_find(typeNamePattern: "Rigid") always did. Plain `kind` is exact-only and
        // cannot express this (kind: "Rigid" would match nothing) - this is the gap the capability
        // audit found and kindPattern (Plan 10 Task 6) closes.
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { kindPattern = "Rigid" }));

        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("Assets/Enemy.prefab", hit.GetProperty("path").GetString());
        Assert.Equal("Rigidbody", hit.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task GraphQuery_KindPattern_CombinedWithKindIsRefused_SameAxis()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "Rigidbody", kindPattern = "Rigid" }));

        Assert.Contains("kind", text);
        Assert.Contains("kindPattern", text);
    }

    [Fact]
    public async Task GraphQuery_KindPattern_AloneSatisfiesAtLeastOneFilterRequirement()
    {
        // kindPattern alone (no kind/namePattern/pathPrefix/edgeKind) must not trip the shared
        // "needs at least one filter" validation query_graph also uses - it is itself a filter.
        var envelope = await McpTestClient.CallTool(_factory, "graph_query", new { kindPattern = "Rigid" });

        Assert.False(envelope.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean(),
            "kindPattern alone must not be treated as 'no filter given'");
    }

    [Fact]
    public async Task GraphQuery_KindPattern_SqlInjectionReturnsEmptyAndLeavesTheServerFullyFunctional()
    {
        var injected = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { kindPattern = "'; DROP TABLE nodes; --" }));

        Assert.Empty(injected.GetProperty("results").EnumerateArray());

        var summary = Structured(await McpTestClient.CallTool(_factory, "get_project_summary"));
        Assert.True(summary.GetProperty("totalNodes").GetInt32() > 0);

        var followUp = Structured(await McpTestClient.CallTool(_factory, "graph_query", new { kindPattern = "Rigid" }));
        Assert.Single(followUp.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task GraphQuery_Enumerated5_ComponentFind_MonoBehaviourResolutionBranch_ViaEdgeTargetNamePattern()
    {
        // component_find's second branch - a MonoBehaviour resolved through its m_Script reference
        // to a matching class name - needs kind="MonoBehaviour" (the node's own recorded kind) AND
        // edgeTargetNamePattern (the referenced script's name), combined.
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "MonoBehaviour", edgeKind = "references", edgeTargetNamePattern = "player" }));

        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("Assets/Player.prefab", hit.GetProperty("path").GetString());
    }

    // ---------------------------------------------------------------- edgeTargetKind (Plan 10 Task 6 correctness fix)
    //
    // edgeTargetNamePattern alone never checked the matched target's own kind - unlike the OLD
    // find_components_using_pattern/component_find SQL, which joined with "AND sn.kind = 'Class'".
    // A same-named non-script asset (Material, ScriptableObject, ...) reached via a `references`
    // edge would match indistinguishably. edgeTargetKind restores that guarantee. The full
    // false-positive-then-fixed proof, with a same-named Material fixture, lives at the
    // GraphDatabase level in QueryGraphTests.cs (GraphQuery_EdgeTargetKindRestrictsMatchToThat
    // NodeKind_ExcludingASameNamedNonScriptAsset) - these tests close the gap an SQL-level test
    // alone cannot: that the parameter is actually wired from this live MCP endpoint through
    // ProjectService down to GraphDatabase, not silently dropped somewhere in between.

    [Fact]
    public async Task GraphQuery_Enumerated3_FindComponentsUsingPattern_EdgeTargetKindRestoresScriptOnlyGuarantee()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeKind = "references", edgeTargetNamePattern = "player", edgeTargetKind = "Class" }));

        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("Assets/Player.prefab", hit.GetProperty("path").GetString());
    }

    [Fact]
    public async Task GraphQuery_Enumerated5_ComponentFind_MonoBehaviourResolutionBranch_EdgeTargetKindRestoresScriptOnlyGuarantee()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "MonoBehaviour", edgeKind = "references", edgeTargetNamePattern = "player", edgeTargetKind = "Class" }));

        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("Assets/Player.prefab", hit.GetProperty("path").GetString());
    }

    [Fact]
    public async Task GraphQuery_EdgeTargetKind_ExcludesAMatchWhoseTargetKindDiffers_ProvingItIsWiredThroughEndToEnd()
    {
        // PlayerController.cs's own node kind is "Class", never "MonoBehaviour" - a deliberately
        // WRONG edgeTargetKind must exclude the match edgeTargetNamePattern alone would find. If
        // the parameter were silently dropped anywhere between this tool and GraphDatabase, this
        // would still (incorrectly) return the one hit - the DB-level tests, which call
        // GraphDatabase directly, cannot catch that class of bug.
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeKind = "references", edgeTargetNamePattern = "player", edgeTargetKind = "MonoBehaviour" }));

        Assert.Empty(structured.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task GraphQuery_Enumerated6_AssetFind_ScriptTypeViaKindClassPlusPathPrefix()
    {
        // asset_find(type: "Script", pathPrefix: "Assets/Scripts") - a FILE-level classification,
        // reachable two ways: kind="Class" (ScriptIndexer's one node per top-level class, tested
        // here) combined with pathPrefix, OR the dedicated fileType filter (FileType_Script_*
        // below), which is the byte-for-byte faithful one (one hit per FILE, not per class). Kept
        // both documented rather than deleting this one: kind="Class" is still useful for a
        // code-search intent ("find script CLASSES"), fileType for an asset-browsing intent ("find
        // script FILES") - genuinely different questions that happen to coincide for a single-class
        // file. See the FileType_* tests below for Prefab/Scene/ScriptableObject, which - unlike
        // Script/Material/AnimatorController - have NO kind-based path at all: no single node
        // summarises a whole scene or prefab, and a ScriptableObject instance shares MonoBehaviour's
        // generic kind with an ordinary component. That was Plan 10 Task 6's flagged blocker; fileType
        // (file_state-backed, not node-backed) is how it closes.
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "Class", pathPrefix = "Assets/Scripts" }));

        var paths = structured.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("path").GetString()).ToList();
        Assert.Equal(2, paths.Count);
        Assert.Contains("Assets/Scripts/PlayerController.cs", paths);
        Assert.Contains("Assets/Scripts/OrphanScript.cs", paths);
    }

    // ---------------------------------------------------------------- fileType: the asset_find gap (Plan 10 Task 6)
    //
    // asset_find's whole-FILE classification, fully reachable via the SEPARATE 'fileType' filter -
    // answered from file_state (GraphDatabase.DistinctFileStatePaths / ProjectService.
    // FindAssetsByFileState), never touching graph nodes at all. Scene/Prefab/ScriptableObject are
    // the cases the Enumerated6 test above documents as having NO kind-based path whatsoever - this
    // section is the proof that gap is closed.

    [Fact]
    public async Task FileType_Prefab_OneHitPerFile()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Prefab" }));

        var paths = structured.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("path").GetString()).ToList();
        Assert.Equal(2, paths.Count);
        Assert.Contains("Assets/Player.prefab", paths);
        Assert.Contains("Assets/Enemy.prefab", paths);
        Assert.All(structured.GetProperty("results").EnumerateArray(),
            r => Assert.Equal("Prefab", r.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task FileType_Scene_ReachesWhatNoNodeKindCanClassify()
    {
        // A scene is many per-object nodes (here, just a PrefabInstance) with no single summarising
        // node - "kind" has nothing to filter on that means "this whole file is a scene".
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Scene" }));

        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("Assets/Scene.unity", hit.GetProperty("path").GetString());
        Assert.Equal("Scene", hit.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task FileType_ScriptableObject_ReachesWhatNoNodeKindCanClassify()
    {
        // Config.asset's own node (if it produced one at all) would carry MonoBehaviour's generic
        // kind, indistinguishable from an ordinary component - the exact case named unreachable via
        // 'kind' in this class's own doc comment. fileType never looks at node kind, so it is
        // unaffected either way.
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "ScriptableObject" }));

        var hit = Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.Equal("Assets/Data/Config.asset", hit.GetProperty("path").GetString());
        Assert.Equal("ScriptableObject", hit.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task FileType_Script_OneHitPerFile_UnlikeKindClassWhichIsOnePerTopLevelClass()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Script" }));

        var paths = structured.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("path").GetString()).ToList();
        Assert.Equal(2, paths.Count);
        Assert.Contains("Assets/Scripts/PlayerController.cs", paths);
        Assert.Contains("Assets/Scripts/OrphanScript.cs", paths);
    }

    [Fact]
    public async Task FileType_CombinesWithPathPrefix_ReproducesAssetFindExactly()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Script", pathPrefix = "Assets/Scripts" }));

        var paths = structured.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("path").GetString()).ToList();
        Assert.Equal(2, paths.Count);
        Assert.Contains("Assets/Scripts/PlayerController.cs", paths);
        Assert.Contains("Assets/Scripts/OrphanScript.cs", paths);
    }

    [Fact]
    public async Task FileType_NoFilterAtAll_ReturnsEveryIndexedAsset_LikeAssetFindWithNoFilters()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Asset" }));

        // Nothing in this fixture falls through to the "Asset" catch-all (every extension used
        // here - .cs/.unity/.prefab/.asset - maps to one of the other six named types), so this
        // just proves a legal, recognised fileType value that happens to match nothing is empty,
        // not an error.
        Assert.Empty(structured.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task FileType_UnrecognisedValueIsRefused_ListsValidOnes()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Sprite" }));

        Assert.Contains("Sprite", text);
        foreach (var type in new[] { "Script", "Scene", "Prefab", "Material", "AnimatorController", "ScriptableObject", "Asset" })
            Assert.Contains(type, text);
    }

    [Fact]
    public async Task FileType_CombinedWithKindIsRefused_DifferentQuestions()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Prefab", kind = "GameObject" }));

        Assert.Contains("fileType", text);
        Assert.Contains("kind", text);
    }

    [Fact]
    public async Task FileType_CombinedWithEdgeKindIsRefused()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Prefab", edgeKind = "references" }));

        Assert.Contains("fileType", text);
    }

    [Fact]
    public async Task FileType_CombinedWithEdgeTargetKindIsRefused()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Prefab", edgeTargetKind = "Class" }));

        Assert.Contains("fileType", text);
    }

    [Fact]
    public async Task FileType_RespectsLimitAndReportsTruncationHonestly()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Prefab", limit = 1 }));

        Assert.Single(structured.GetProperty("results").EnumerateArray());
        Assert.True(structured.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task FileType_SqlInjectionInPathPrefixReturnsEmptyAndLeavesTheServerFullyFunctional()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Prefab", pathPrefix = "'; DROP TABLE file_state; --" }));

        Assert.Empty(structured.GetProperty("results").EnumerateArray());

        var summary = Structured(await McpTestClient.CallTool(_factory, "get_project_summary"));
        Assert.True(summary.GetProperty("totalNodes").GetInt32() > 0);

        var followUp = Structured(await McpTestClient.CallTool(_factory, "graph_query", new { fileType = "Prefab" }));
        Assert.Equal(2, followUp.GetProperty("results").EnumerateArray().Count());
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public async Task GraphQuery_NoFiltersAtAllGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query", new { }));

        Assert.Contains("filter", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- F7: an unrecognised 'kind' errors, never a silent empty set
    //
    // kind: "Bananas" and a plausible-but-wrong kind: "Scene" both used to return {results: [],
    // totalReturned: 0} - indistinguishable from a genuinely empty area of an otherwise-populated
    // graph. project_settings already gets this right for an unrecognised 'section' (lists the
    // valid ones); this is the same treatment for graph_query's 'kind', drawn from the actual,
    // live kind vocabulary this project's graph contains (GraphDatabase.CountByKind - the same
    // computation get_project_summary's own nodesByKind reports), not a fixed, hand-maintained list.

    [Fact]
    public async Task GraphQuery_UnrecognisedKind_ErrorsNamingTheActualKindVocabulary()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "Bananas" }));

        Assert.Contains("Bananas", text);
        Assert.Contains("case-sensitive", text, StringComparison.OrdinalIgnoreCase);
        // The real, live kind vocabulary for THIS project's graph - this fixture's own Class/
        // MonoBehaviour nodes (PlayerController.cs/OrphanScript.cs; Player.prefab/Config.asset).
        Assert.Contains("Class", text);
        Assert.Contains("MonoBehaviour", text);
    }

    [Fact]
    public async Task GraphQuery_PlausibleButWrongKind_Scene_ErrorsRatherThanSilentlyEmpty()
    {
        // "Scene" is a real Hades/Unity concept (graph_query's OWN separate 'fileType' filter
        // value) but never a graph NODE kind - a scene is many per-object nodes (GameObject,
        // Transform, PrefabInstance, ...) with no single node summarising the whole file (see
        // QueryTools' own class doc comment on 'fileType'). Before this fix, indistinguishable
        // from a typo: both silently returned an empty result.
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "Scene" }));

        Assert.Contains("Scene", text);
        Assert.Contains("Class", text);
    }

    [Fact]
    public async Task GraphQuery_KindCaseMismatch_IsRefused_NotSilentlyCoercedOrEmpty()
    {
        // Kinds are case-sensitive (the underlying column uses SQLite's default BINARY collation,
        // exactly like every other exact-match column here) - a caller who gets the case wrong
        // needs to be told, not silently handed an empty result indistinguishable from "this
        // project genuinely has none".
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "monobehaviour" }));

        Assert.Contains("monobehaviour", text);
        Assert.Contains("MonoBehaviour", text); // the real, correctly-cased kind
    }

    [Fact]
    public async Task GraphQuery_ValidKindCombinedWithOtherFiltersMatchingNothing_IsStillAnOrdinaryEmptyResult()
    {
        // The fix must fire ONLY when 'kind' itself is unrecognised - a genuinely valid kind
        // combined with a namePattern that happens to match nothing is an ordinary empty result,
        // exactly as before, not an error.
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "MonoBehaviour", namePattern = "definitely-does-not-match-anything" }));

        Assert.Empty(structured.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task GraphQuery_AnUnrecognisedEdgeDirectionGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeKind = "references", edgeDirection = "sideways" }));

        Assert.Contains("sideways", text);
    }

    [Fact]
    public async Task GraphQuery_EdgeTargetPathWithoutEdgeKindIsRefused()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeTargetPath = "Assets/Scripts/PlayerController.cs" }));

        Assert.Contains("edgeKind", text);
    }

    [Fact]
    public async Task GraphQuery_EdgeTargetNamePatternWithoutEdgeKindIsRefused()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeTargetNamePattern = "player" }));

        Assert.Contains("edgeKind", text);
    }

    [Fact]
    public async Task GraphQuery_EdgeTargetKindWithoutEdgeKindIsRefused()
    {
        // 'kind' alongside it so this exercises the SPECIFIC "edgeTargetKind needs edgeKind"
        // branch rather than the generic "needs at least one filter" guidance, which would also
        // (coincidentally) mention "edgeKind" among the valid filter names.
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "Class", edgeTargetKind = "Class" }));

        Assert.Contains("edgeKind", text);
        Assert.Contains("edgeTargetKind", text);
    }

    [Fact]
    public async Task GraphQuery_EdgeAbsentWithoutEdgeKindIsRefused()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "Class", edgeAbsent = true }));

        Assert.Contains("edgeKind", text);
    }

    [Fact]
    public async Task GraphQuery_EdgeTargetPathThatDoesNotResolveReturnsEmptyNotAnError()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeKind = "references", edgeTargetPath = "Assets/DoesNotExist.cs" }));

        Assert.Empty(structured.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task GraphQuery_IsAdvertisedAsReadOnlyWithASchemaAndTheSavedStateClause()
    {
        var tool = Assert.Single((await McpTestClient.ListTools(_factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "graph_query");

        Assert.True(tool.TryGetProperty("outputSchema", out _));
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Contains("saved state on disk", tool.GetProperty("description").GetString());
    }

    // ---------------------------------------------------------------- SQL injection safety (end-to-end)

    [Fact]
    public async Task GraphQuery_SqlInjectionAttemptOverRealMcpReturnsEmptyAndLeavesTheServerFullyFunctional()
    {
        var injected = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { namePattern = "'; DROP TABLE nodes; --" }));

        Assert.Empty(injected.GetProperty("results").EnumerateArray());

        var summary = Structured(await McpTestClient.CallTool(_factory, "get_project_summary"));
        Assert.True(summary.GetProperty("totalNodes").GetInt32() > 0);

        var followUp = Structured(await McpTestClient.CallTool(_factory, "graph_query", new { kind = "Class" }));
        Assert.Equal(2, followUp.GetProperty("results").EnumerateArray().Count());
    }

    [Fact]
    public async Task GraphQuery_SqlInjectionAttemptInEdgeTargetNamePatternReturnsEmptyNotAnError()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeKind = "references", edgeTargetNamePattern = "'; DROP TABLE nodes; --" }));

        Assert.Empty(structured.GetProperty("results").EnumerateArray());

        var summary = Structured(await McpTestClient.CallTool(_factory, "get_project_summary"));
        Assert.True(summary.GetProperty("totalNodes").GetInt32() > 0);
    }

    [Fact]
    public async Task GraphQuery_SqlInjectionAttemptInEdgeTargetPathReturnsEmptyNotAnError()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeKind = "references", edgeTargetPath = "'; DROP TABLE nodes; --" }));

        Assert.Empty(structured.GetProperty("results").EnumerateArray());

        var summary = Structured(await McpTestClient.CallTool(_factory, "get_project_summary"));
        Assert.True(summary.GetProperty("totalNodes").GetInt32() > 0);
    }

    [Fact]
    public async Task GraphQuery_SqlInjectionAttemptInEdgeTargetKindReturnsEmptyNotAnError()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeKind = "references", edgeTargetKind = "'; DROP TABLE nodes; --" }));

        Assert.Empty(structured.GetProperty("results").EnumerateArray());

        var summary = Structured(await McpTestClient.CallTool(_factory, "get_project_summary"));
        Assert.True(summary.GetProperty("totalNodes").GetInt32() > 0);
    }

    public void Dispose()
    {
        // See EditorToolTestBase.Dispose's own comment: _factory is a fresh per-test
        // WebApplicationFactory (WithWebHostBuilder above) whose own background services
        // (EditorListener's live accept loop, ControlListener's, ObservationService's periodic
        // sweep) keep running, and can still be touching _appRoot/_projectRoot, until the host
        // itself is disposed - which must happen before the recursive delete below, not after.
        _factory.Dispose();

        TeardownDiagnostics.Delete(_appRoot, _projectRoot);
    }
}
