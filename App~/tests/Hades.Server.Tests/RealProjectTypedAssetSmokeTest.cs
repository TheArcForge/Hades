using System.Text.Json;
using Hades.Core;
using Hades.Core.Indexing;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// inspect_asset and project_settings (Plan 10 Task 6 consolidated replacements for
/// material_get_properties, animation_get_controller and analyze_render_pipeline), driven over MCP
/// exactly as an agent would, against the real Hades-Unity-Client corpus rather than a synthetic
/// fixture. Same directory-exists-guard pattern as RealProjectInspectionSmokeTest /
/// RealProjectSettingsSmokeTest - a local sanity check, skipped rather than failing on a machine
/// that does not have this checkout.
///
/// project_settings(section="renderPipeline")'s expectation here is the most load-bearing of the
/// three: this project is confirmed (Packages/manifest.json declares
/// com.unity.render-pipelines.universal) to use URP, so this is the one real, external confirmation
/// that ReadThrough's hard-coded UniversalRenderPipelineAssetScriptGuid constant is actually
/// correct, not just internally consistent with itself - and the one test in this whole suite that
/// exercises project_settings' URP identification path at all (the synthetic SettingsToolsTests.cs
/// fixture only ever proves the "Built-in" default).
/// </summary>
public class RealProjectTypedAssetSmokeTest : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    const string RealProject = "/Users/mike/Projects/Hades-Unity-Client";

    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public RealProjectTypedAssetSmokeTest(WebApplicationFactory<Program> factory)
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
    public async Task InspectAsset_ReadsARealMaterialsShaderAndProperties()
    {
        const string material = "Assets/Demo/Materials/M_Enemy.mat";
        if (!Directory.Exists(RealProject)) return;
        if (!File.Exists(Path.Combine(RealProject, material))) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset", new { path = material }))
            .GetProperty("material");

        var shader = structured.GetProperty("shader");
        var floatCount = structured.GetProperty("floats").EnumerateObject().Count();
        var colorCount = structured.GetProperty("colors").EnumerateObject().Count();
        var shaderGuid = shader.TryGetProperty("guid", out var g) ? g.GetString() : "(absent)";
        Console.WriteLine($"[inspect_asset] {material}: shader.guid={shaderGuid} "
            + $"shader.resolved={shader.GetProperty("resolved").GetBoolean()} floats={floatCount} colors={colorCount} "
            + $"textures={structured.GetProperty("textures").GetArrayLength()}");

        // URP's Lit shader is a package asset - no file under any scan root, so the common,
        // expected outcome is an unresolved shader with every other property still present.
        Assert.False(shader.GetProperty("resolved").GetBoolean());
        Assert.True(floatCount > 0);
        Assert.True(colorCount > 0);
    }

    [Fact]
    public async Task InspectAsset_SweepsEveryRealMaterialWithoutThrowingOrReturningEmpty()
    {
        // Broader than the single hand-picked M_Enemy.mat above: every .mat in the real corpus,
        // found independently via the same ProjectWalker scan AssetIndexer itself uses, rather
        // than trusting a previously-reported list. This is exactly the check that caught the
        // "material is not always one document" bug (M_Enemy.mat AND SmokeTestMat.mat both carry
        // a leading editor AssetVersion document) - it stays here as a permanent regression guard.
        if (!Directory.Exists(RealProject)) return;

        var scanRoots = ProjectWalker.ResolveScanRoots(RealProject, warnings: []);
        var onDisk = scanRoots
            .SelectMany(root => ProjectWalker.EnumerateSourceFiles(root.AbsolutePath, "*.mat")
                .Select(file => ProjectWalker.ToRecordedPath(root, file)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(onDisk);

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

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

            var structured = Structured(envelope).GetProperty("material");
            var hasAnyProperty = structured.GetProperty("floats").EnumerateObject().Any()
                || structured.GetProperty("colors").EnumerateObject().Any()
                || structured.GetProperty("textures").GetArrayLength() > 0
                || structured.GetProperty("shader").TryGetProperty("guid", out _);
            if (!hasAnyProperty) failures.Add($"{path}: parsed but returned no shader/floats/colors/textures at all");
        }

        Console.WriteLine($"[inspect_asset sweep] on disk={onDisk.Count}");
        foreach (var path in onDisk) Console.WriteLine($"  {path}");
        foreach (var failure in failures) Console.WriteLine($"  FAILED: {failure}");

        Assert.True(failures.Count == 0, "inspect_asset failed or returned nothing for:\n" + string.Join('\n', failures));
    }

    [Fact]
    public async Task InspectAsset_ReadsTheRealSmokeTestControllersStatesAndTransition()
    {
        const string controller = "Assets/Animations/SmokeTest.controller";
        if (!Directory.Exists(RealProject)) return;
        if (!File.Exists(Path.Combine(RealProject, controller))) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset", new { path = controller }))
            .GetProperty("animatorController");
        var states = structured.GetProperty("states").EnumerateArray().ToList();
        var transitions = structured.GetProperty("transitions").EnumerateArray().ToList();

        Console.WriteLine($"[inspect_asset] {controller}: states=[{string.Join(", ", states.Select(s => s.GetProperty("name").GetString()))}] "
            + $"transitions={transitions.Count}");
        foreach (var t in transitions)
            Console.WriteLine($"  {t.GetProperty("sourceState").GetString()} -> {t.GetProperty("destinationState").GetString()} "
                + $"conditions={t.GetProperty("conditions").GetArrayLength()}");

        Assert.Equal(["Walk", "Idle"], states.Select(s => s.GetProperty("name").GetString()));
        Assert.True(states.Single(s => s.GetProperty("name").GetString() == "Idle").GetProperty("isDefaultState").GetBoolean());

        var transition = Assert.Single(transitions);
        Assert.Equal("Idle", transition.GetProperty("sourceState").GetString());
        Assert.Equal("Walk", transition.GetProperty("destinationState").GetString());
        var condition = Assert.Single(transition.GetProperty("conditions").EnumerateArray());
        Assert.Equal("Speed", condition.GetProperty("parameter").GetString());
    }

    [Fact]
    public async Task ProjectSettings_RenderPipeline_IdentifiesTheRealProjectAsUrp()
    {
        if (!Directory.Exists(RealProject)) return;

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(RealProject);

        var structured = Structured(await McpTestClient.CallTool(_factory, "project_settings", new { section = "renderPipeline" }))
            .GetProperty("renderPipeline");

        Console.WriteLine($"[project_settings:renderPipeline] {RealProject}: pipeline={structured.GetProperty("pipeline").GetString()} "
            + $"pipelineAssetPath={(structured.TryGetProperty("pipelineAssetPath", out var p) ? p.GetString() : "(absent)")}");
        Console.WriteLine("[project_settings:renderPipeline] Packages/manifest.json declares "
            + "\"com.unity.render-pipelines.universal\": \"17.3.0\" - ground truth this is URP.");

        Assert.Equal("URP", structured.GetProperty("pipeline").GetString());
        Assert.Equal("Assets/Settings/PC_RPAsset.asset", structured.GetProperty("pipelineAssetPath").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
