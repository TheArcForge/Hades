using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// End-to-end, over HTTP, for project_settings, the consolidated read replacing project_get_settings,
/// tag_list, layer_list, scene_list_build, asset_get_import_settings, and analyze_render_pipeline.
/// Same fixture style as SummaryToolTests.
///
/// Plan 10 Task 6 removed this file's former per-tool tests for project_get_settings/tag_list/
/// layer_list/scene_list_build/asset_get_import_settings (each folded into one project_settings
/// 'section', tested below) and asset_get_info/asset_find (folded into inspect_asset/graph_query
/// respectively - see InspectToolTests.cs's Structure_* tests, including the guid-on-every-branch
/// assertions Task 6 itself added, and QueryToolsTests.cs's FileType_* tests).
/// </summary>
public class SettingsToolsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
    const string ProjectGuid = "15c012f27331e49229cef25e74537816";
    const string PrefabGuid = "10c9cc48bfec4433a8ff61d4250438fd";
    const string ScriptGuid = "0b02b100d68ce4fd28a4b8cea62a32ef";

    void Write(string relative, string body, string? guid = null, string? metaBody = null)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        if (metaBody is not null) File.WriteAllText(full + ".meta", metaBody);
        else if (guid is not null) File.WriteAllText(full + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    // 32-entry fixed-length layers array, mirroring the real Hades-Unity-Client TagManager.asset:
    // one custom layer at index 8, empty slots elsewhere.
    const string TagManagerBody = """
        TagManager:
          serializedVersion: 3
          tags:
          - Player
          layers:
          - Default
          - TransparentFX
          - Ignore Raycast
          -
          - Water
          - UI
          -
          -
          - SmokeTestLayer
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
          -
        """;

    public SettingsToolsTests(WebApplicationFactory<Program> factory)
    {
        Write("ProjectSettings/ProjectSettings.asset", Header + """
            --- !u!129 &1
            PlayerSettings:
              productGUID: 15c012f27331e49229cef25e74537816
              companyName: DefaultCompany
              productName: SettingsToolsFixture
              bundleVersion: 1.2.3
            """);
        Write("ProjectSettings/TagManager.asset", Header + "--- !u!78 &1\n" + TagManagerBody);
        Write("ProjectSettings/EditorBuildSettings.asset", Header + """
            --- !u!1045 &1
            EditorBuildSettings:
              serializedVersion: 2
              m_Scenes:
              - enabled: 1
                path: Assets/Scenes/Main.unity
                guid: 99c9720ab356a0642a771bea13969a05
              - enabled: 0
                path: Assets/Scenes/Disabled.unity
                guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
              m_configObjects: {}
            """);
        // No custom render pipeline (m_CustomRenderPipeline is Unity's own null-reference shape,
        // {fileID: 0} with no guid) - the "Built-in" case, sufficient to prove project_settings'
        // renderPipeline section wires to ReadThrough.AnalyzeRenderPipeline; URP/HDRP/unknown
        // detection itself is ReadThroughTests' own subject, not re-proven here.
        Write("ProjectSettings/GraphicsSettings.asset", Header + """
            --- !u!30 &1
            GraphicsSettings:
              serializedVersion: 12
              m_CustomRenderPipeline: {fileID: 0}
            """);

        Write("Assets/Scripts/Health.cs",
            "using UnityEngine;\npublic class Health : MonoBehaviour { }", ScriptGuid);

        Write("Assets/Enemy.prefab", Header
            + "--- !u!1 &1\nGameObject:\n  m_Name: Enemy\n"
            + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n",
            metaBody: $"fileFormatVersion: 2\nguid: {PrefabGuid}\nPrefabImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n");

        // No .meta at all - a file added outside the Editor, not yet imported.
        Write("Assets/NotYetImported.asset", Header + "--- !u!114 &1\nMonoBehaviour:\n  m_Name: Orphan\n");

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

    async Task<JsonElement> ToolMeta(string name) =>
        Assert.Single((await McpTestClient.ListTools(_factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == name);

    // ================================================================== project_settings (Plan 10 Task 4)
    //
    // One section per replaced tool - project_get_settings, tag_list, layer_list, scene_list_build,
    // asset_get_import_settings, analyze_render_pipeline - reusing THIS SAME fixture and, for
    // player/tags/layers/buildScenes/importSettings, delegating straight to the already-tested
    // methods above rather than re-deriving the read.

    [Fact]
    public async Task ProjectSettings_Player_ReturnsIdentityAndPlayerSettingsFields()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "project_settings", new { section = "player" }));

        Assert.Equal("player", structured.GetProperty("section").GetString());
        var player = structured.GetProperty("player");
        Assert.Equal(ProjectGuid, player.GetProperty("productGuid").GetString());
        Assert.Equal("DefaultCompany", player.GetProperty("companyName").GetString());
        Assert.Equal("SettingsToolsFixture", player.GetProperty("productName").GetString());
        Assert.Equal("1.2.3", player.GetProperty("bundleVersion").GetString());
    }

    [Fact]
    public async Task ProjectSettings_Tags_ReturnsCustomTags()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "project_settings", new { section = "tags" }));

        Assert.Equal("tags", structured.GetProperty("section").GetString());
        var tags = structured.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Equal(["Player"], tags);
    }

    [Fact]
    public async Task ProjectSettings_Layers_Returns32EntriesPreservingEmptySlotsByIndex()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "project_settings", new { section = "layers" }));

        Assert.Equal("layers", structured.GetProperty("section").GetString());
        var layers = structured.GetProperty("layers").EnumerateArray().Select(l => l.GetString()).ToList();
        Assert.Equal(32, layers.Count);
        Assert.Equal("Default", layers[0]);
        Assert.Equal("SmokeTestLayer", layers[8]);
        // Same fixed-slot-indexing obligation layer_list itself proves - an empty slot is a real,
        // addressable index, not compacted away, and layer 8 being the only named custom layer
        // must not shrink the array to 9 entries.
        Assert.Equal("", layers[3]);
        Assert.Equal("", layers[9]);
        Assert.Equal("", layers[31]);
    }

    [Fact]
    public async Task ProjectSettings_BuildScenes_PreservesOrderAndEnabledFlag()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "project_settings", new { section = "buildScenes" }));

        Assert.Equal("buildScenes", structured.GetProperty("section").GetString());
        var scenes = structured.GetProperty("buildScenes").EnumerateArray().ToList();
        Assert.Equal(2, scenes.Count);
        Assert.Equal("Assets/Scenes/Main.unity", scenes[0].GetProperty("path").GetString());
        Assert.True(scenes[0].GetProperty("enabled").GetBoolean());
        Assert.Equal("Assets/Scenes/Disabled.unity", scenes[1].GetProperty("path").GetString());
        Assert.False(scenes[1].GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task ProjectSettings_RenderPipeline_ReturnsBuiltInHonestlyForTheFixturesDefault()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "project_settings", new { section = "renderPipeline" }));

        Assert.Equal("renderPipeline", structured.GetProperty("section").GetString());
        Assert.Equal("Built-in", structured.GetProperty("renderPipeline").GetProperty("pipeline").GetString());
    }

    [Fact]
    public async Task ProjectSettings_ImportSettings_ReturnsTheImporterBlock()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "project_settings",
            new { section = "importSettings", assetPath = "Assets/Enemy.prefab" }));

        Assert.Equal("importSettings", structured.GetProperty("section").GetString());
        var importSettings = structured.GetProperty("importSettings");
        Assert.Equal("Assets/Enemy.prefab", importSettings.GetProperty("path").GetString());
        Assert.Equal("PrefabImporter", importSettings.GetProperty("importerType").GetString());
    }

    [Fact]
    public async Task ProjectSettings_ImportSettings_WithoutAssetPathGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "project_settings",
            new { section = "importSettings" }));

        Assert.Contains("assetPath", text);
    }

    [Fact]
    public async Task ProjectSettings_UnrecognisedSectionIsRefusedListingValidOnes()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "project_settings",
            new { section = "nonsense" }));

        Assert.Contains("nonsense", text);
        Assert.Contains("player", text);
        Assert.Contains("importSettings", text);
    }

    [Fact]
    public async Task ProjectSettings_BlankSectionGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "project_settings", new { section = "  " }));
        Assert.Contains("section", text);
    }

    [Fact]
    public async Task ProjectSettings_IsAdvertisedAsReadOnlyWithASchemaAndTheSavedStateClause()
    {
        var tool = await ToolMeta("project_settings");
        Assert.True(tool.TryGetProperty("outputSchema", out _));
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Contains("saved state on disk", tool.GetProperty("description").GetString());
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
