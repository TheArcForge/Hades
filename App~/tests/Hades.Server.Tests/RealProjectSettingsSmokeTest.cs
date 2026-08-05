using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// project_settings and graph_query (Plan 10 Task 6 consolidated replacements for
/// project_get_settings/tag_list/layer_list/scene_list_build/asset_get_info/asset_find), driven
/// over MCP exactly as an agent would, against the real Hades-Unity-Client corpus rather than a
/// synthetic fixture. Same directory-exists-guard pattern as RealProjectInspectionSmokeTest /
/// RealProjectSummaryToolSmokeTest - a local sanity check, skipped rather than failing on a machine
/// that does not have this checkout.
///
/// tag_list / layer_list's expected values are transcribed by hand from
/// ProjectSettings/TagManager.asset as it stood 2026-08-02: tags: [] (no custom tags), and a
/// 32-entry layers array with exactly one custom entry, "SmokeTestLayer" at index 8, everything
/// else either a Unity builtin name or an empty slot. If this project's TagManager.asset ever
/// changes, these hard-coded expectations - not the tool - are what must be updated.
/// </summary>
public class RealProjectSettingsSmokeTest : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    const string RealProject = "/Users/mike/Projects/Hades-Unity-Client";

    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public RealProjectSettingsSmokeTest(WebApplicationFactory<Program> factory)
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
    public async Task ProjectSettings_Tags_MatchesTagManagerAssetReadByHand()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "project_settings", new { section = "tags" }));
        var tags = structured.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();

        Console.WriteLine($"[project_settings:tags] {RealProject}: tags=[{string.Join(", ", tags)}]");
        Console.WriteLine("[project_settings:tags] hand-read from ProjectSettings/TagManager.asset: tags: [] (no custom tags)");

        Assert.Empty(tags);
    }

    [Fact]
    public async Task ProjectSettings_Layers_MatchesTagManagerAssetReadByHandPreservingAllThirtyTwoSlots()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "project_settings", new { section = "layers" }));
        var layers = structured.GetProperty("layers").EnumerateArray().Select(l => l.GetString() ?? "").ToList();

        Console.WriteLine($"[project_settings:layers] {RealProject}: {layers.Count} entries");
        for (var i = 0; i < layers.Count; i++)
            Console.WriteLine($"  [{i}] \"{layers[i]}\"");
        Console.WriteLine("[project_settings:layers] hand-read from ProjectSettings/TagManager.asset: 0=Default "
            + "1=TransparentFX 2=\"Ignore Raycast\" 3=(empty) 4=Water 5=UI 6=(empty) 7=(empty) "
            + "8=SmokeTestLayer 9-31=(empty)");

        var expected = new[]
        {
            "Default", "TransparentFX", "Ignore Raycast", "", "Water", "UI", "", "", "SmokeTestLayer",
        }.Concat(Enumerable.Repeat("", 23)).ToList();

        Assert.Equal(32, expected.Count); // sanity on the hand-transcription itself
        Assert.Equal(expected, layers);
    }

    [Fact]
    public async Task ProjectSettings_Player_ReportsTheRealProjectsIdentityAndPlayerSettings()
    {
        if (!Directory.Exists(RealProject)) return;

        var adopted = _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);
        Assert.NotNull(adopted);

        var structured = Structured(await McpTestClient.CallTool(_factory, "project_settings", new { section = "player" }));
        var player = structured.GetProperty("player");

        Console.WriteLine($"[project_settings:player] productGuid={player.GetProperty("productGuid").GetString()} "
            + $"companyName={player.GetProperty("companyName").GetString()} "
            + $"productName={player.GetProperty("productName").GetString()} "
            + $"bundleVersion={player.GetProperty("bundleVersion").GetString()}");

        Assert.Equal(adopted!.ProductGuid, player.GetProperty("productGuid").GetString());
        Assert.Equal("DefaultCompany", player.GetProperty("companyName").GetString());
        Assert.Equal("Hades-Unity-Client", player.GetProperty("productName").GetString());
        Assert.False(string.IsNullOrEmpty(player.GetProperty("bundleVersion").GetString()));
    }

    [Fact]
    public async Task ProjectSettings_BuildScenes_ReportsTheRealProjectsBuildScenesInOrder()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "project_settings", new { section = "buildScenes" }));
        var scenes = structured.GetProperty("buildScenes").EnumerateArray().ToList();

        Console.WriteLine($"[project_settings:buildScenes] {RealProject}: {scenes.Count} scene(s)");
        foreach (var scene in scenes)
            Console.WriteLine($"  path={scene.GetProperty("path").GetString()} "
                + $"enabled={scene.GetProperty("enabled").GetBoolean()}");

        Assert.NotEmpty(scenes);
        Assert.Contains(scenes, s => s.GetProperty("path").GetString() == "Assets/Scenes/SampleScene.unity"
            && s.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task InspectAssetAndGraphQuery_ResolveARealPrefabsGuidAndType()
    {
        // asset_get_info's {guid, type} split across two consolidated tools: inspect_asset's
        // top-level 'guid' (populated on every depth="structure" branch, including a Prefab's
        // hierarchy payload - Plan 10 Task 6's own fix for exactly this gap) for identity, and
        // graph_query's fileType filter (Plan 10 Task 6's asset_find replacement) for the
        // classification - both proven here against a real prefab, not just a synthetic fixture.
        const string enemyPrefab = "Assets/Demo/Prefabs/Enemy.prefab";
        if (!Directory.Exists(RealProject)) return;
        if (!File.Exists(Path.Combine(RealProject, enemyPrefab))) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var inspected = Structured(await McpTestClient.CallTool(_factory, "inspect_asset", new { path = enemyPrefab }));
        Console.WriteLine($"[inspect_asset] {enemyPrefab}: guid={inspected.GetProperty("guid").GetString()}");
        Assert.Equal(32, inspected.GetProperty("guid").GetString()?.Length);

        var found = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Prefab", pathPrefix = enemyPrefab }));
        var hit = Assert.Single(found.GetProperty("results").EnumerateArray());
        Console.WriteLine($"[graph_query fileType=Prefab] {enemyPrefab}: kind={hit.GetProperty("kind").GetString()}");
        Assert.Equal("Prefab", hit.GetProperty("kind").GetString());
        Assert.Equal(enemyPrefab, hit.GetProperty("path").GetString());
    }

    [Fact]
    public async Task GraphQuery_FindsEveryRealMaterialUnderPathPrefix()
    {
        if (!Directory.Exists(RealProject)) return;
        if (!Directory.Exists(Path.Combine(RealProject, "Assets", "Demo", "Materials"))) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "graph_query",
            new { fileType = "Material", pathPrefix = "Assets/Demo/Materials" }));
        var results = structured.GetProperty("results").EnumerateArray().ToList();

        Console.WriteLine($"[graph_query] fileType=Material pathPrefix=Assets/Demo/Materials: {results.Count} hit(s)");
        foreach (var hit in results) Console.WriteLine($"  {hit.GetProperty("path").GetString()}");

        Assert.Contains(results, r => r.GetProperty("path").GetString() == "Assets/Demo/Materials/M_Enemy.mat");
        Assert.All(results, r => Assert.Equal("Material", r.GetProperty("kind").GetString()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
