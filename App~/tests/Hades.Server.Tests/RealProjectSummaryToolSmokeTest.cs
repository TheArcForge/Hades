using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// get_scene_summary and get_recently_changed, driven over MCP exactly as an agent would, against
/// the real Hades-Unity-Client corpus rather than a synthetic fixture. Same
/// directory-exists-guard pattern as Hades.Core.Tests' RealProjectIndexSmokeTest — a local sanity
/// check, skipped rather than failing on a machine that does not have this checkout.
/// </summary>
public class RealProjectSummaryToolSmokeTest : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    const string RealProject = "/Users/mike/Projects/Hades-Unity-Client";
    const string RealScene = "Assets/Scenes/SampleScene.unity";

    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public RealProjectSummaryToolSmokeTest(WebApplicationFactory<Program> factory)
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
    public async Task GetSceneSummary_ReportsSensibleCountsForARealScene()
    {
        if (!Directory.Exists(RealProject)) return;
        if (!File.Exists(Path.Combine(RealProject, RealScene))) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "get_scene_summary", new { path = RealScene }));

        var gameObjectCount = structured.GetProperty("gameObjectCount").GetInt32();
        var rootCount = structured.GetProperty("rootCount").GetInt32();
        var byKind = structured.GetProperty("componentsByKind");

        // Measured 2026-08-02 against Assets/Scenes/SampleScene.unity: 3 GameObjects (Main
        // Camera, Directional Light, Global Volume), all three at scene root, 17 total objects
        // across 11 kinds (GameObject, Transform, Camera, AudioListener, MonoBehaviour x3, Light,
        // OcclusionCullingSettings, RenderSettings, LightmapSettings, NavMeshSettings, SceneRoots).
        // Asserted as bounds/invariants rather than exact equality so a future edit to the real
        // scene does not spuriously fail this — see console output for the actual numbers.
        Console.WriteLine($"[{RealScene}] gameObjectCount={gameObjectCount} rootCount={rootCount} "
            + $"componentsByKind={byKind.GetRawText()}");

        Assert.True(gameObjectCount > 0, "expected at least one GameObject in a real scene");
        Assert.True(rootCount > 0 && rootCount <= gameObjectCount,
            $"rootCount ({rootCount}) must be positive and cannot exceed gameObjectCount ({gameObjectCount})");
        Assert.Equal(gameObjectCount, byKind.GetProperty("GameObject").GetInt32());
        Assert.True(byKind.GetProperty("Transform").GetInt32() > 0, "expected at least one Transform");
    }

    [Fact]
    public async Task GetSceneSummary_UnknownRealPathIsReportedCleanly()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "get_scene_summary",
            new { path = "Assets/Scenes/DoesNotExist.unity" }));

        Assert.Contains("not in the graph", text);
    }

    [Fact]
    public async Task GetRecentlyChanged_SortsRealFilesNewestFirstAndHonoursSince()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "get_recently_changed", new { limit = 10 }));
        var results = structured.GetProperty("results").EnumerateArray().ToList();

        Assert.NotEmpty(results);
        Console.WriteLine("[get_recently_changed] newest " + results.Count + " of the real project:");
        foreach (var r in results)
            Console.WriteLine($"  {r.GetProperty("mtimeUtc").GetDateTimeOffset():O}  {r.GetProperty("path").GetString()}");

        var mtimes = results.Select(r => r.GetProperty("mtimeUtc").GetDateTimeOffset()).ToList();
        Assert.Equal(mtimes.OrderByDescending(t => t), mtimes);

        // Nothing can have changed in the future — a robust behavioural check that does not
        // depend on the repo's actual file contents or timestamps.
        var future = Structured(await McpTestClient.CallTool(_factory, "get_recently_changed",
            new { since = DateTimeOffset.UtcNow.AddDays(1).ToString("O") }));
        Assert.Empty(future.GetProperty("results").EnumerateArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
