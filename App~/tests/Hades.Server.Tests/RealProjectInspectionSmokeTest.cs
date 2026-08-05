using System.Text.Json;
using Hades.Core;
using Hades.Core.Indexing;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// inspect_asset (Plan 10 Task 6 consolidated replacement for prefab_get_contents /
/// scene_get_hierarchy / component_get_all / component_get_property / component_list_properties)
/// and graph_query (replacing component_find), driven over MCP exactly as an agent would, against
/// every prefab in the real Hades-Unity-Client corpus rather than a synthetic fixture. Same
/// directory-exists-guard pattern as RealProjectSummaryToolSmokeTest / Hades.Core.Tests'
/// RealProjectIndexSmokeTest - a local sanity check, skipped rather than failing on a machine that
/// does not have this checkout.
///
/// Counts prefabs on disk independently, via the same ProjectWalker scan-root resolution
/// AssetIndexer itself scans with, rather than trusting any previously-reported number: this
/// plan's own doc cites a prior plan that shipped a wrong prefab count exactly by skipping that
/// check.
/// </summary>
public class RealProjectInspectionSmokeTest : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    const string RealProject = "/Users/mike/Projects/Hades-Unity-Client";

    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public RealProjectInspectionSmokeTest(WebApplicationFactory<Program> factory)
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
    public async Task InspectAsset_ParsesEveryRealPrefabWithoutThrowingOrReturningEmpty()
    {
        if (!Directory.Exists(RealProject)) return;

        var scanRoots = ProjectWalker.ResolveScanRoots(RealProject, warnings: []);
        var onDisk = scanRoots
            .SelectMany(root => ProjectWalker.EnumerateSourceFiles(root.AbsolutePath, "*.prefab")
                .Select(file => ProjectWalker.ToRecordedPath(root, file)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        // Control: prove this really did find files, so a bug that made the enumeration return
        // nothing cannot masquerade as "every prefab parsed" over zero prefabs.
        Assert.NotEmpty(onDisk);

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var parsed = 0;
        var pureVariantRoots = 0;
        var failures = new List<string>();

        foreach (var path in onDisk)
        {
            var envelope = await McpTestClient.CallTool(_factory, "inspect_asset", new { path });
            var failed = envelope.TryGetProperty("error", out _)
                || (envelope.GetProperty("result").TryGetProperty("isError", out var flag) && flag.GetBoolean());

            if (failed)
            {
                failures.Add($"{path}: {McpTestClient.ErrorText(envelope)}");
                continue;
            }

            // depth="structure" for a .prefab path nests the hierarchy under "hierarchy" (not
            // top-level "roots" the way prefab_get_contents used to return it) - see
            // InspectTool.StructureResult.
            var hierarchyRoots = Structured(envelope).GetProperty("hierarchy").GetProperty("roots").EnumerateArray().ToList();
            if (hierarchyRoots.Count == 0)
            {
                failures.Add($"{path}: returned an EMPTY hierarchy for a non-empty file");
                continue;
            }

            parsed++;
            if (hierarchyRoots.All(r => r.GetProperty("kind").GetString() == "PrefabInstance"))
                pureVariantRoots++;
        }

        Console.WriteLine($"[inspect_asset] on disk={onDisk.Count} parsed={parsed} "
            + $"pure-variant-placeholder-roots={pureVariantRoots}");
        foreach (var path in onDisk) Console.WriteLine($"  {path}");
        foreach (var failure in failures) Console.WriteLine($"  FAILED: {failure}");

        Assert.True(failures.Count == 0,
            "inspect_asset failed or returned an empty hierarchy for:\n" + string.Join('\n', failures));
        Assert.Equal(onDisk.Count, parsed);
    }

    [Fact]
    public async Task InspectAsset_HandlesARealSceneWithRootLevelPrefabInstances()
    {
        // The previous check only requires a prefab; the two file kinds share one renderer, and a
        // scene is where a root-level PrefabInstance (m_TransformParent: {fileID: 0}, no local
        // stripped placeholder) is actually common - Assets/Demo/Scenes/BossArena.unity has two.
        // Worth a real check, not just the synthetic prefab-variant fixture in ReadThroughTests.
        const string realScene = "Assets/Demo/Scenes/BossArena.unity";
        if (!Directory.Exists(RealProject)) return;
        if (!File.Exists(Path.Combine(RealProject, realScene))) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset", new { path = realScene }));
        var roots = structured.GetProperty("hierarchy").GetProperty("roots").EnumerateArray().ToList();

        // name / sourcePrefabGuid are omitted from the JSON entirely when null (the MCP SDK's
        // default serializer options enable JsonIgnoreCondition.WhenWritingNull) rather than sent
        // as an explicit null, so reading them defensively is not optional here.
        static string Optional(JsonElement node, string property) =>
            node.TryGetProperty(property, out var value) ? value.ToString() : "(absent)";

        Console.WriteLine($"[inspect_asset] {realScene} roots={roots.Count}");
        foreach (var r in roots)
            Console.WriteLine($"  kind={r.GetProperty("kind").GetString()} name={Optional(r, "name")} "
                + $"sourcePrefabGuid={Optional(r, "sourcePrefabGuid")}");

        Assert.NotEmpty(roots);
        Assert.Contains(roots, r => r.GetProperty("kind").GetString() == "PrefabInstance");
    }

    [Fact]
    public async Task InspectAsset_ResolvesEveryComponentOnARealGameObjectIncludingBothCustomScripts()
    {
        // Assets/Demo/Prefabs/Enemy.prefab's "Enemy" GameObject, read directly off disk: a
        // Transform, MeshFilter, BoxCollider, MeshRenderer, and two MonoBehaviours (Health,
        // EnemyAI) - fixed fileIds, pinned here rather than discovered, so a regression shows up
        // as a named assertion failure instead of a silent count change.
        const string enemyPrefab = "Assets/Demo/Prefabs/Enemy.prefab";
        const long enemyGameObject = 6930894080877784848;
        if (!Directory.Exists(RealProject)) return;
        if (!File.Exists(Path.Combine(RealProject, enemyPrefab))) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = enemyPrefab, target = enemyGameObject }));
        var components = structured.GetProperty("components").EnumerateArray().ToList();

        Console.WriteLine($"[inspect_asset] {enemyPrefab} target={enemyGameObject} components={components.Count}");
        foreach (var c in components)
        {
            var typeName = c.TryGetProperty("typeName", out var t) ? t.GetString() : "(unresolved)";
            Console.WriteLine($"  fileId={c.GetProperty("fileId").GetInt64()} typeName={typeName} "
                + $"missing={c.GetProperty("missing").GetBoolean()}");
        }

        Assert.Equal(6, components.Count);
        Assert.All(components, c => Assert.False(c.GetProperty("missing").GetBoolean()));
        Assert.Equal(["Transform", "MeshFilter", "BoxCollider", "MeshRenderer"],
            components.Take(4).Select(c => c.GetProperty("typeName").GetString()));

        var health = components.Single(c => c.GetProperty("fileId").GetInt64() == 8377085903075235314);
        Assert.Equal("Assets/Demo/Scripts/Health.cs", health.GetProperty("typeName").GetString());

        var enemyAi = components.Single(c => c.GetProperty("fileId").GetInt64() == 5641116005526688007);
        Assert.Equal("Assets/Demo/Scripts/EnemyAI.cs", enemyAi.GetProperty("typeName").GetString());
    }

    [Fact]
    public async Task InspectAsset_SweepsEveryGameObjectInEveryRealPrefabWithoutThrowing()
    {
        // Broader than the targeted Enemy.prefab test above: every GameObject-kind node
        // inspect_asset's own structure reports, across all 9 real prefabs, fed straight back into
        // inspect_asset with 'target' set. Proves the tool survives the corpus's real variety (pure
        // variants with zero local GameObjects, nested instances, nothing but builtin components)
        // rather than just the one hand-picked fixture.
        if (!Directory.Exists(RealProject)) return;

        var scanRoots = ProjectWalker.ResolveScanRoots(RealProject, warnings: []);
        var onDisk = scanRoots
            .SelectMany(root => ProjectWalker.EnumerateSourceFiles(root.AbsolutePath, "*.prefab")
                .Select(file => ProjectWalker.ToRecordedPath(root, file)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(onDisk);

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var gameObjectsVisited = 0;
        var componentsSeen = 0;
        var monoBehavioursResolved = 0;
        var monoBehavioursMissing = 0;
        var failures = new List<string>();

        foreach (var path in onDisk)
        {
            var hierarchy = Structured(await McpTestClient.CallTool(_factory, "inspect_asset", new { path }))
                .GetProperty("hierarchy");
            foreach (var gameObjectFileId in GameObjectFileIds(hierarchy.GetProperty("roots")))
            {
                gameObjectsVisited++;
                var envelope = await McpTestClient.CallTool(_factory, "inspect_asset",
                    new { path, target = gameObjectFileId });
                var failed = envelope.TryGetProperty("error", out _)
                    || (envelope.GetProperty("result").TryGetProperty("isError", out var flag) && flag.GetBoolean());

                if (failed)
                {
                    failures.Add($"{path}#{gameObjectFileId}: {McpTestClient.ErrorText(envelope)}");
                    continue;
                }

                foreach (var component in Structured(envelope).GetProperty("components").EnumerateArray())
                {
                    componentsSeen++;
                    if (component.GetProperty("missing").GetBoolean()) monoBehavioursMissing++;
                    else if (component.TryGetProperty("scriptGuid", out _)) monoBehavioursResolved++;
                }
            }
        }

        Console.WriteLine($"[inspect_asset sweep] prefabs={onDisk.Count} gameObjects={gameObjectsVisited} "
            + $"components={componentsSeen} monoBehavioursResolved={monoBehavioursResolved} "
            + $"monoBehavioursMissing={monoBehavioursMissing}");
        foreach (var failure in failures) Console.WriteLine($"  FAILED: {failure}");

        Assert.True(failures.Count == 0, "inspect_asset (target=) failed for:\n" + string.Join('\n', failures));
        Assert.True(gameObjectsVisited > 0, "no real GameObject was found to sweep - the corpus or inspect_asset's structure depth changed");
        Assert.Equal(0, monoBehavioursMissing); // the real corpus ships no deleted-script components

        static IEnumerable<long> GameObjectFileIds(JsonElement nodes)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.GetProperty("kind").GetString() == "GameObject") yield return node.GetProperty("fileId").GetInt64();
                if (node.TryGetProperty("children", out var children))
                    foreach (var id in GameObjectFileIds(children)) yield return id;
            }
        }
    }

    [Fact]
    public async Task InspectAsset_PropertiesMatchTheRealHealthScriptsSerializedFields()
    {
        const string enemyPrefab = "Assets/Demo/Prefabs/Enemy.prefab";
        const long enemyGameObject = 6930894080877784848;
        const long healthComponent = 8377085903075235314;
        if (!Directory.Exists(RealProject)) return;
        if (!File.Exists(Path.Combine(RealProject, enemyPrefab))) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var listed = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = enemyPrefab, target = enemyGameObject, component = healthComponent }));
        var names = listed.GetProperty("properties").EnumerateArray().Select(p => p.GetString()).ToList();

        Console.WriteLine($"[inspect_asset] {enemyPrefab}#{healthComponent} properties=[{string.Join(", ", names)}]");

        Assert.Contains("maxHealth", names);
        Assert.Contains("damageConfig", names);

        var maxHealth = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = enemyPrefab, target = enemyGameObject, component = healthComponent, property = "maxHealth" }));
        Assert.Equal("100", maxHealth.GetProperty("value").GetString());

        var damageConfig = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = enemyPrefab, target = enemyGameObject, component = healthComponent, property = "damageConfig" }));
        Assert.Equal("29041eeea3fbe40049f6f1d2290f1f1b", damageConfig.GetProperty("value").GetProperty("guid").GetString());
    }

    [Fact]
    public async Task GraphQuery_MatchesHealthAcrossTheRealProject_MonoBehaviourResolutionBranch()
    {
        // component_find's MonoBehaviour-resolution branch -> graph_query(kind:"MonoBehaviour",
        // edgeKind:"references", edgeTargetNamePattern:pattern) - see QueryTools' own class doc
        // comment. Known, disclosed narrowing versus the old tool: component_find's own result
        // also carried the RESOLVED script name ("Health") as 'typeName'; graph_query's uniform hit
        // shape reports the matched node's own kind/name ("MonoBehaviour"), not the edge target's -
        // a caller still gets 'path'+'fileId' (enough to re-inspect via inspect_asset), just not
        // the resolved name in the SAME call. This test asserts what graph_query actually returns
        // rather than repeating the old, now-unreachable assertion.
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "MonoBehaviour", edgeKind = "references", edgeTargetNamePattern = "Health" }));
        var hits = structured.GetProperty("results").EnumerateArray().ToList();

        Console.WriteLine($"[graph_query] kind=MonoBehaviour edgeTargetNamePattern=Health hits={hits.Count}");
        foreach (var hit in hits)
            Console.WriteLine($"  path={hit.GetProperty("path").GetString()} fileId={hit.GetProperty("fileId").GetInt64()} "
                + $"kind={hit.GetProperty("kind").GetString()}");

        Assert.Contains(hits, h => h.GetProperty("path").GetString() == "Assets/Demo/Prefabs/Enemy.prefab");
    }

    public void Dispose()
    {
        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
