using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// inspect_asset / find_unset_references / graph_query (Plan 10 Task 6 consolidated replacements
/// for reference_get, reference_find_unset, event_list_listeners, event_find_all, query_graph)
/// driven over MCP exactly as an agent would, against the real Hades-Unity-Client corpus rather
/// than a synthetic fixture. Same directory-exists-guard pattern as RealProjectInspectionSmokeTest -
/// a local sanity check, skipped rather than failing on a machine that does not have this checkout.
/// Fixed fileIds are reused from RealProjectInspectionSmokeTest's own already-verified constants
/// rather than re-discovered, to keep this file's own scope narrow.
///
/// The real corpus ships NO UnityEvent usage at all (confirmed by grep - no m_PersistentCalls
/// anywhere under Assets/), so event_find_all's replacement (find_unset_references, project scope)
/// and event_list_listeners' replacement (inspect_asset's "properties" depth) are verified here to
/// correctly report "nothing to find" rather than throwing - the honest, and only truthful, result
/// on this particular project. That is not a weak test: a tool that quietly threw or fabricated a
/// hit on a project with no events would be a real, worse bug.
/// </summary>
public class RealProjectReferenceEventSmokeTest : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    const string RealProject = "/Users/mike/Projects/Hades-Unity-Client";
    const string EnemyPrefab = "Assets/Demo/Prefabs/Enemy.prefab";
    const long EnemyGameObject = 6930894080877784848;
    const long HealthComponent = 8377085903075235314;
    const string DamageConfigGuid = "29041eeea3fbe40049f6f1d2290f1f1b";

    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public RealProjectReferenceEventSmokeTest(WebApplicationFactory<Program> factory)
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
    public async Task InspectAsset_ResolvesHealthsDamageConfigToTheRealScriptableObjectAsset()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = EnemyPrefab, target = EnemyGameObject, component = HealthComponent, property = "damageConfig" }));
        var reference = structured.GetProperty("reference");

        Console.WriteLine($"[inspect_asset] {EnemyPrefab}#{HealthComponent}.damageConfig -> "
            + $"guid={reference.GetProperty("targetGuid").GetString()} "
            + $"resolvedPath={(reference.TryGetProperty("resolvedPath", out var p) ? p.GetString() : "(unresolved)")} "
            + $"resolved={reference.GetProperty("resolved").GetBoolean()}");

        Assert.Equal(DamageConfigGuid, reference.GetProperty("targetGuid").GetString());
        Assert.False(reference.GetProperty("isLocal").GetBoolean());
        Assert.True(reference.GetProperty("resolved").GetBoolean());
        Assert.Equal("Assets/Demo/DamageConfig.asset", reference.GetProperty("resolvedPath").GetString());
    }

    [Fact]
    public async Task InspectAsset_EnemyAIsBehaviourEnumReturnsItsRawValueWithNoReferenceField()
    {
        // Behavioural change, intentional and disclosed (see InspectTool's own class doc comment):
        // reference_get used to REJECT a non-reference-shaped field outright ("not an
        // object-reference field"). inspect_asset's depth="value" instead always returns the raw
        // value - the 'reference' key is present only when the value actually looked reference-
        // shaped ({fileID, guid, type}), never as a hard error for a field that plainly is not one.
        if (!Directory.Exists(RealProject)) return;

        var enemyAi = 5641116005526688007;
        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = EnemyPrefab, target = EnemyGameObject, component = enemyAi, property = "behaviour" }));

        Console.WriteLine($"[inspect_asset] {EnemyPrefab}#{enemyAi}.behaviour -> value={structured.GetProperty("value")}");

        Assert.False(structured.TryGetProperty("reference", out _));
    }

    [Fact]
    public async Task FindUnsetReferences_ScansTheRealEnemyPrefabAndFindsTheRootsUnsetFather()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "find_unset_references",
            new { path = EnemyPrefab, limit = 500 }));
        var results = structured.GetProperty("unsetReferences").EnumerateArray().ToList();

        Console.WriteLine($"[find_unset_references] {EnemyPrefab} unset={results.Count} "
            + $"truncated={structured.GetProperty("truncated").GetBoolean()}");
        foreach (var group in results.GroupBy(r => r.GetProperty("propertyPath").GetString()))
            Console.WriteLine($"  {group.Key}: {group.Count()}");

        // Every real object in this prefab (it is not itself a nested prefab instance) carries
        // Unity's own m_CorrespondingSourceObject/m_PrefabInstance/m_PrefabAsset bookkeeping,
        // unset - this is what proves find_unset_references is unfiltered exactly as designed and
        // documented, not a small hand-picked number.
        Assert.Contains(results, r => r.GetProperty("fileId").GetInt64() == EnemyGameObject
            && r.GetProperty("propertyPath").GetString() == "m_PrefabInstance");
        Assert.Contains(results, r => r.GetProperty("propertyPath").GetString() == "m_Father");
        Assert.True(results.Count >= 6, "expected at least one unset bookkeeping field per real object in the prefab");
    }

    [Fact]
    public async Task InspectAsset_ARealComponentWithNoUnityEventFieldsReturnsAnEmptyEventsListNotAnError()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = EnemyPrefab, target = EnemyGameObject, component = HealthComponent }));

        Console.WriteLine($"[inspect_asset] {EnemyPrefab}#{HealthComponent} "
            + $"events={structured.GetProperty("events").GetArrayLength()}");

        Assert.Empty(structured.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task FindUnsetReferences_TheRealProjectShipsNoWiredUnityEventsAtAll()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "find_unset_references", new { limit = 500 }));
        var results = structured.GetProperty("unityEvents").EnumerateArray().ToList();

        Console.WriteLine($"[find_unset_references] project scope: unityEvents={results.Count} (real corpus has no UnityEvent usage - confirmed by grep)");

        Assert.Empty(results);
    }

    [Fact]
    public async Task GraphQuery_FiltersTheRealProjectByKindAndNamePattern()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var monoBehaviours = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { kind = "MonoBehaviour", limit = 500 }));
        var monoBehaviourCount = monoBehaviours.GetProperty("results").EnumerateArray().Count();

        var healthHits = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { namePattern = "Health" }));
        var healthResults = healthHits.GetProperty("results").EnumerateArray().ToList();

        var instances = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { edgeKind = "instance_of", edgeDirection = "outgoing", limit = 500 }));
        var instanceCount = instances.GetProperty("results").EnumerateArray().Count();

        Console.WriteLine($"[graph_query] kind=MonoBehaviour -> {monoBehaviourCount}");
        Console.WriteLine($"[graph_query] namePattern=Health -> {healthResults.Count}");
        foreach (var hit in healthResults)
            Console.WriteLine($"  {hit.GetProperty("kind").GetString()} {hit.GetProperty("name").GetString()} @ {hit.GetProperty("path").GetString()}");
        Console.WriteLine($"[graph_query] edgeKind=instance_of (outgoing) -> {instanceCount}");

        Assert.True(monoBehaviourCount > 0, "expected at least one MonoBehaviour node in the real project");
        Assert.Contains(healthResults, h => h.GetProperty("name").GetString() == "Health" && h.GetProperty("kind").GetString() == "Class");
        Assert.True(instanceCount > 0, "expected at least one PrefabInstance in the real project's scenes");
    }

    [Fact]
    public async Task GraphQuery_SqlInjectionAttemptAgainstTheRealIndexedGraphReturnsEmptyAndLeavesItIntact()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var before = Structured(await McpTestClient.CallTool(_factory, "get_project_summary"))
            .GetProperty("totalNodes").GetInt32();

        var injected = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { namePattern = "'; DROP TABLE nodes; --" }));

        var after = Structured(await McpTestClient.CallTool(_factory, "get_project_summary"))
            .GetProperty("totalNodes").GetInt32();

        Console.WriteLine($"[graph_query injection] totalNodes before={before} after={after}");

        Assert.Empty(injected.GetProperty("results").EnumerateArray());
        Assert.Equal(before, after);
        Assert.True(after > 0, "the real project's graph must still be populated after the injection attempt");
    }

    public void Dispose()
    {
        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
