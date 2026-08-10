using System.Text;
using System.Text.Json;
using Hades.Core;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests;

/// <summary>
/// End-to-end, over HTTP, for Plan 10 Task 3's two consolidated read tools.
///
/// <c>inspect_asset</c> replaces prefab_get_contents, scene_get_hierarchy, component_get_all,
/// component_get_property, component_list_properties, material_get_properties,
/// animation_get_controller, asset_get_info, reference_get, event_list_listeners (10 tools) via one
/// path plus progressively more arguments:
///   - path only                          -> depth "structure"  (hierarchy / material / controller / asset info)
///   - path + target                      -> depth "components" (component_get_all)
///   - path + target + component          -> depth "properties" (component_list_properties + event_list_listeners)
///   - path + target + component + property -> depth "value"    (component_get_property + reference_get, merged)
///
/// <c>find_unset_references</c> replaces reference_find_unset and event_find_all (2 tools): given
/// 'path' it scans that one file for unset ({fileID: 0}) references, exactly as reference_find_unset
/// did; omitting 'path' instead finds UnityEvents with at least one wired listener across the whole
/// project, exactly as event_find_all did (graph-served, unlike the file-scoped mode).
///
/// Every behaviour the 12 replaced tools had is re-proven here through the new tools' own argument
/// shape, not removed - see InspectionToolsTests/TypedAssetToolsTests/ReferenceToolsTests/
/// GraphToolsTests (event_find_all's own home) for the ORIGINAL coverage this mirrors; those tests,
/// and the 12 tools they exercise, stay in place until Plan 10 Task 6's capability audit passes and
/// the hard cutover removes them.
/// </summary>
public class InspectToolTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
    const string ProjectGuid = "11112222333344445555666677778888";
    const string HealthScriptGuid = "aaaa1111aaaa1111aaaa1111aaaa1111";
    const string MissingScriptGuid = "cccc3333cccc3333cccc3333cccc3333";
    const string UnindexedGuid = "dddd4444dddd4444dddd4444dddd4444";
    const string VariantSourceGuid = "beb43c66c1c72416290db5dae24d452f";
    const string NestedInstanceSourceGuid = "cccccccccccccccccccccccccccccccc";
    const string ShaderGuid = "ccccccccccccccccccccccccccccccc1";
    const string TextureGuid = "ccccccccccccccccccccccccccccccc2";

    // Plan 10 Task 6: one guid per depth="structure" branch (Prefab/Scene/Material/AnimatorController),
    // so Structure_*'s own tests can prove InspectAssetResult.Guid is populated on every branch, not
    // just the "anything else" one asset_get_info's old test already covered (Structure_PlainAsset_
    // ReturnsTypeAndGuid, below) - the exact gap the capability audit found in inspect_asset.
    const string HierarchyPrefabGuid = "eeee5555eeee5555eeee5555eeee5555";
    const string HierarchySceneGuid = "ffff6666ffff6666ffff6666ffff6666";
    const string MaterialOwnGuid = "aaaa7777aaaa7777aaaa7777aaaa7777";
    const string ControllerOwnGuid = "bbbb8888bbbb8888bbbb8888bbbb8888";

    const int BigHierarchyNodeCount = 150;

    void Write(string relative, string body, string? guid = null)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        if (guid is not null) File.WriteAllText(full + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    public InspectToolTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {ProjectGuid}\n");

        // ---- depth "structure": hierarchy (prefab + scene), variant placeholder, legacy format ----

        Write("Assets/Hierarchy.prefab", Header
            + "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  - component: {fileID: 5}\n  m_Name: Root\n"
            + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n"
            + "--- !u!1 &3\nGameObject:\n  m_Component:\n  - component: {fileID: 4}\n  m_Name: Child\n"
            + "--- !u!4 &4\nTransform:\n  m_GameObject: {fileID: 3}\n  m_Father: {fileID: 2}\n"
            + "--- !u!23 &5\nMeshRenderer:\n  m_GameObject: {fileID: 1}\n",
            HierarchyPrefabGuid);

        Write("Assets/Hierarchy.unity", Header
            + "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: Root\n"
            + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n",
            HierarchySceneGuid);

        Write("Assets/Variant.prefab", Header + $$"""
            --- !u!1001 &259710713600510621
            PrefabInstance:
              serializedVersion: 2
              m_Modification:
                m_TransformParent: {fileID: 0}
                m_Modifications: []
                m_RemovedComponents: []
              m_SourcePrefab: {fileID: 100100000, guid: {{VariantSourceGuid}}, type: 3}
            """);

        // Plan 15 Task 2. Mirrors project_aurora's real Assets/_ResourcesStatic/Buildings/
        // BedroomWithChestAndTable.prefab, ground-truthed by reading its raw YAML and its source
        // prefab (Assets/_ResourcesStatic/BasePrefab.prefab): TWO independent local override
        // anchors sharing one PrefabInstance - a stripped Transform (fileId 101) that owns
        // nothing (present only to anchor the locally-added "Sprite" child's m_Father), and a
        // stripped GameObject (fileId 102) that owns 3 added MonoBehaviours, discoverable only by
        // scanning for components whose OWN m_GameObject names it (a stripped GameObject carries
        // no m_Component list of its own - confirmed on the real file). See
        // ComponentInspectionTests.NestedInstanceWithOverriddenRoot for the byte-for-byte same
        // shape at the Hades.Core level; this copy exists so the full MCP round trip - the
        // documented "feed the reported fileId back as target" workflow - is proven end-to-end,
        // not just at ReadThrough's own layer.
        Write("Assets/NestedInstance.prefab", Header
            + "--- !u!1001 &100\nPrefabInstance:\n  serializedVersion: 2\n  m_Modification:\n"
            + "    m_TransformParent: {fileID: 0}\n    m_Modifications: []\n    m_RemovedComponents: []\n"
            + $"  m_SourcePrefab: {{fileID: 100100000, guid: {NestedInstanceSourceGuid}, type: 3}}\n"
            + "--- !u!1 &102 stripped\nGameObject:\n"
            + $"  m_CorrespondingSourceObject: {{fileID: 6000, guid: {NestedInstanceSourceGuid}, type: 3}}\n"
            + "  m_PrefabInstance: {fileID: 100}\n"
            + "--- !u!114 &103\nMonoBehaviour:\n  m_GameObject: {fileID: 102}\n"
            + $"  m_Script: {{fileID: 11500000, guid: {HealthScriptGuid}, type: 3}}\n"
            + "--- !u!114 &104\nMonoBehaviour:\n  m_GameObject: {fileID: 102}\n"
            + $"  m_Script: {{fileID: 11500000, guid: {MissingScriptGuid}, type: 3}}\n"
            + "--- !u!4 &101 stripped\nTransform:\n"
            + $"  m_CorrespondingSourceObject: {{fileID: 5000, guid: {NestedInstanceSourceGuid}, type: 3}}\n"
            + "  m_PrefabInstance: {fileID: 100}\n"
            + "--- !u!1 &106\nGameObject:\n  m_Component:\n  - component: {fileID: 107}\n  m_Name: Sprite\n"
            + "--- !u!4 &107\nTransform:\n  m_GameObject: {fileID: 106}\n  m_Father: {fileID: 101}\n");

        Write("Assets/Legacy.prefab", Header + """
            --- !u!1001 &100100000
            Prefab:
              m_ObjectHideFlags: 1
              serializedVersion: 2
              m_Modification:
                m_TransformParent: {fileID: 0}
                m_Modifications: []
                m_RemovedComponents: []
              m_ParentPrefab: {fileID: 0}
              m_RootGameObject: {fileID: 100000}
              m_IsPrefabParent: 1
            """);

        // A hierarchy bigger than inspect_asset's default 'limit' (100), to prove truncation is
        // honest rather than silently dropping objects: 150 root GameObjects, no nesting.
        var big = new StringBuilder(Header);
        for (var i = 0; i < BigHierarchyNodeCount; i++)
        {
            var goId = 1000 + i * 2;
            var trId = goId + 1;
            big.Append($"--- !u!1 &{goId}\nGameObject:\n  m_Component:\n  - component: {{fileID: {trId}}}\n  m_Name: Node{i}\n");
            big.Append($"--- !u!4 &{trId}\nTransform:\n  m_GameObject: {{fileID: {goId}}}\n  m_Father: {{fileID: 0}}\n");
        }
        Write("Assets/Big.prefab", big.ToString());

        // ---- depth "structure": material, animator controller, plain asset ----

        Write("Assets/Textures/Rock.png", "fake texture bytes", TextureGuid);
        Write("Assets/M_Enemy.mat", Header + $$"""
            --- !u!21 &2100000
            Material:
              serializedVersion: 8
              m_Name: M_Enemy
              m_Shader: {fileID: 4800000, guid: {{ShaderGuid}}, type: 3}
              m_SavedProperties:
                serializedVersion: 3
                m_TexEnvs:
                - _BaseMap:
                    m_Texture: {fileID: 2800000, guid: {{TextureGuid}}, type: 3}
                    m_Scale: {x: 1, y: 1}
                    m_Offset: {x: 0, y: 0}
                m_Ints: []
                m_Floats:
                - _Cull: 2
                m_Colors:
                - _BaseColor: {r: 1, g: 1, b: 1, a: 1}
            """,
            MaterialOwnGuid);

        Write("Assets/SmokeTest.controller", Header + """
            --- !u!91 &9100000
            AnimatorController:
              m_Name: SmokeTest
              serializedVersion: 5
              m_AnimatorLayers:
              - serializedVersion: 5
                m_Name: Base Layer
                m_StateMachine: {fileID: 4207829524157895410}
                m_Controller: {fileID: 9100000}
            --- !u!1101 &2311171854486829514
            AnimatorStateTransition:
              m_Name:
              m_Conditions:
              - m_ConditionMode: 3
                m_ConditionEvent: Speed
                m_EventTreshold: 0.1
              m_DstStateMachine: {fileID: 0}
              m_DstState: {fileID: 5679841221323201010}
              m_IsExit: 0
              serializedVersion: 3
            --- !u!1107 &4207829524157895410
            AnimatorStateMachine:
              serializedVersion: 6
              m_Name: Base Layer
              m_ChildStates:
              - serializedVersion: 1
                m_State: {fileID: 6673564061869833467}
                m_Position: {x: 200, y: 0, z: 0}
              - serializedVersion: 1
                m_State: {fileID: 5679841221323201010}
                m_Position: {x: 235, y: 65, z: 0}
              m_ChildStateMachines: []
              m_AnyStateTransitions: []
              m_DefaultState: {fileID: 6673564061869833467}
            --- !u!1102 &5679841221323201010
            AnimatorState:
              serializedVersion: 6
              m_Name: Walk
              m_Speed: 1
              m_Transitions: []
              m_Motion: {fileID: 0}
            --- !u!1102 &6673564061869833467
            AnimatorState:
              serializedVersion: 6
              m_Name: Idle
              m_Speed: 1
              m_Transitions:
              - {fileID: 2311171854486829514}
              m_Motion: {fileID: 0}
            """,
            ControllerOwnGuid);

        Write("Assets/NotYetImported.asset", "not real unity yaml content"); // no .meta on purpose

        // ---- depth "components"/"properties"/"value": components, custom fields, references, events ----

        Write("Assets/Scripts/Health.cs",
            "using UnityEngine;\npublic class Health : MonoBehaviour { public float maxHealth = 100f; }",
            HealthScriptGuid);

        // Enemy (fileId 1): Transform (2, root, m_Father unset), a resolvable Health MonoBehaviour
        // (3) carrying a scalar, a local reference, a resolvable external reference, an unresolvable
        // external reference, an explicitly unset reference, and a UnityEvent field with one wired
        // and one unwired call, and a MonoBehaviour whose script guid is never indexed (4).
        Write("Assets/Enemy.prefab", Header
            + "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  - component: {fileID: 3}\n"
            + "  - component: {fileID: 4}\n  m_Name: Enemy\n"
            + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n"
            + "--- !u!114 &3\nMonoBehaviour:\n  m_GameObject: {fileID: 1}\n"
            + $"  m_Script: {{fileID: 11500000, guid: {HealthScriptGuid}, type: 3}}\n"
            + "  maxHealth: 100\n"
            + "  target: {fileID: 1}\n"
            + $"  otherAsset: {{fileID: 11500000, guid: {HealthScriptGuid}, type: 3}}\n"
            + $"  danglingAsset: {{fileID: 11500000, guid: {UnindexedGuid}, type: 3}}\n"
            + "  unassigned: {fileID: 0}\n"
            + "  onDamage:\n"
            + "    m_PersistentCalls:\n"
            + "      m_Calls:\n"
            + "      - m_Target: {fileID: 1}\n"
            + "        m_TargetAssemblyTypeName: UnityEngine.GameObject, UnityEngine\n"
            + "        m_MethodName: SetActive\n"
            + "        m_Mode: 6\n"
            + "        m_Arguments:\n"
            + "          m_ObjectArgument: {fileID: 0}\n"
            + "          m_ObjectArgumentAssemblyTypeName: \n"
            + "          m_IntArgument: 0\n"
            + "          m_FloatArgument: 0\n"
            + "          m_StringArgument: \n"
            + "          m_BoolArgument: 1\n"
            + "        m_CallState: 2\n"
            + "      - m_Target: {fileID: 0}\n"
            + "        m_TargetAssemblyTypeName: \n"
            + "        m_MethodName: \n"
            + "        m_Mode: 1\n"
            + "        m_Arguments:\n"
            + "          m_ObjectArgument: {fileID: 0}\n"
            + "          m_ObjectArgumentAssemblyTypeName: \n"
            + "          m_IntArgument: 0\n"
            + "          m_FloatArgument: 0\n"
            + "          m_StringArgument: \n"
            + "          m_BoolArgument: 0\n"
            + "        m_CallState: 2\n"
            + "--- !u!114 &4\nMonoBehaviour:\n  m_GameObject: {fileID: 1}\n"
            + $"  m_Script: {{fileID: 11500000, guid: {MissingScriptGuid}, type: 3}}\n");

        // A second, unrelated wired UnityEvent - project-scoped find_unset_references needs at
        // least two distinct (path, fileId, eventField) groups project-wide to make truncation
        // meaningful; Enemy.prefab's own "onDamage" alone would never truncate at limit=1.
        Write("Assets/Menu.prefab", Header
            + "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: Menu\n"
            + "--- !u!114 &2\nMonoBehaviour:\n  m_GameObject: {fileID: 1}\n"
            + "  m_OnClick:\n    m_PersistentCalls:\n      m_Calls:\n"
            + "      - m_Target: {fileID: 1}\n        m_MethodName: DoOne\n        m_Mode: 1\n        m_CallState: 2\n");

        // Two listeners on the SAME event field, targeting two DIFFERENT objects (fileId 1 and 2) -
        // ported from the deleted GraphToolsTests.cs (event_find_all's own fixture), which needed
        // this to prove listenerCount aggregates to 2, not 1: two calls to the SAME target on the
        // SAME field would collide under the edges table's own (from_path, from_file_id, to_guid,
        // to_file_id, property_path) uniqueness and collapse into one edge, undercounting - see
        // FindUnityEvents' own doc comment for why that collapsing is a real, disclosed limitation.
        Write("Assets/TwoTargets.prefab", Header
            + "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: TwoTargets\n"
            + "--- !u!114 &2\nMonoBehaviour:\n  m_GameObject: {fileID: 1}\n"
            + "  m_OnClick:\n    m_PersistentCalls:\n      m_Calls:\n"
            + "      - m_Target: {fileID: 1}\n        m_MethodName: DoOne\n        m_Mode: 1\n        m_CallState: 2\n"
            + "      - m_Target: {fileID: 2}\n        m_MethodName: DoTwo\n        m_Mode: 1\n        m_CallState: 2\n");

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

    // ================================================================== inspect_asset: depth "structure"

    [Fact]
    public async Task Structure_Prefab_ReturnsHierarchyWithParentChildStructureAndComponents()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Hierarchy.prefab" }));

        Assert.Equal("Assets/Hierarchy.prefab", structured.GetProperty("path").GetString());
        Assert.Equal("structure", structured.GetProperty("depth").GetString());
        // Plan 10 Task 6: asset_get_info's {guid} lookup, reachable here even though Hierarchy is
        // the payload, not AssetInfo - the exact gap the capability audit found and closed.
        Assert.Equal(HierarchyPrefabGuid, structured.GetProperty("guid").GetString());

        var hierarchy = structured.GetProperty("hierarchy");
        Assert.False(hierarchy.GetProperty("truncated").GetBoolean());

        var root = Assert.Single(hierarchy.GetProperty("roots").EnumerateArray());
        Assert.Equal("Root", root.GetProperty("name").GetString());
        Assert.Equal("GameObject", root.GetProperty("kind").GetString());
        Assert.Equal("MeshRenderer", Assert.Single(root.GetProperty("components").EnumerateArray()).GetString());

        var child = Assert.Single(root.GetProperty("children").EnumerateArray());
        Assert.Equal("Child", child.GetProperty("name").GetString());
        Assert.Empty(child.GetProperty("children").EnumerateArray());

        Assert.False(structured.TryGetProperty("material", out _));
        Assert.False(structured.TryGetProperty("assetInfo", out _));
    }

    [Fact]
    public async Task Structure_PureVariantPrefab_ReturnsAPlaceholderNode()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Variant.prefab" }));

        var root = Assert.Single(structured.GetProperty("hierarchy").GetProperty("roots").EnumerateArray());
        Assert.Equal("PrefabInstance", root.GetProperty("kind").GetString());
        Assert.Equal(VariantSourceGuid, root.GetProperty("sourcePrefabGuid").GetString());
        Assert.False(root.TryGetProperty("name", out _), "a placeholder's absent name must be omitted, not null");
    }

    [Fact]
    public async Task Structure_ANestedInstanceWithAnOverriddenRoot_ReportsBothLocalOverrideAnchorsSeparately()
    {
        // Before this fix, the real BedroomWithChestAndTable.prefab reported exactly ONE
        // "PrefabInstance" root here (the placeholder that owns nothing) - the other placeholder
        // (the one that actually owns the 3 real MonoBehaviour overrides) was completely absent
        // from the hierarchy, not even as an empty node.
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/NestedInstance.prefab" }));

        var roots = structured.GetProperty("hierarchy").GetProperty("roots").EnumerateArray().ToList();
        var placeholders = roots.Where(r => r.GetProperty("kind").GetString() == "PrefabInstance").ToList();
        Assert.Equal(2, placeholders.Count);
        Assert.All(placeholders, p => Assert.Equal(NestedInstanceSourceGuid, p.GetProperty("sourcePrefabGuid").GetString()));
        // Unchanged contract: a "PrefabInstance" placeholder's inline components stay empty in the
        // structure view either way - target= is still what surfaces the real ones.
        Assert.All(placeholders, p => Assert.Empty(p.GetProperty("components").EnumerateArray()));
    }

    [Fact]
    public async Task Structure_Scene_ReturnsTheSameShapeAsPrefab()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Hierarchy.unity" }));

        Assert.Equal("structure", structured.GetProperty("depth").GetString());
        Assert.Equal(HierarchySceneGuid, structured.GetProperty("guid").GetString());
        var root = Assert.Single(structured.GetProperty("hierarchy").GetProperty("roots").EnumerateArray());
        Assert.Equal("Root", root.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Structure_LegacyPrefabFormat_IsReportedAsUnsupportedByName()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Legacy.prefab" }));

        Assert.Contains("Legacy.prefab", text);
        Assert.Contains("pre-2018.3", text);
    }

    [Fact]
    public async Task Structure_HierarchyLargerThanLimit_TruncatesHonestlyAndReportsHowToNarrow()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Big.prefab" }));

        var hierarchy = structured.GetProperty("hierarchy");
        Assert.True(hierarchy.GetProperty("truncated").GetBoolean());
        Assert.Equal(100, hierarchy.GetProperty("totalReturned").GetInt32());
        Assert.Equal(100, hierarchy.GetProperty("roots").GetArrayLength());
    }

    [Fact]
    public async Task Structure_ALimitHighEnoughToFitEverything_ReportsNotTruncated()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Big.prefab", limit = 200 }));

        var hierarchy = structured.GetProperty("hierarchy");
        Assert.False(hierarchy.GetProperty("truncated").GetBoolean());
        Assert.Equal(BigHierarchyNodeCount, hierarchy.GetProperty("totalReturned").GetInt32());
        Assert.Equal(BigHierarchyNodeCount, hierarchy.GetProperty("roots").GetArrayLength());
    }

    [Fact]
    public async Task Structure_Material_ReturnsFloatsColorsAndAResolvedTexture()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/M_Enemy.mat" }));

        Assert.Equal("structure", structured.GetProperty("depth").GetString());
        // Plan 10 Task 6: the material asset's OWN guid, reachable here even though Material is the
        // payload, not AssetInfo - the exact gap the capability audit found and closed.
        Assert.Equal(MaterialOwnGuid, structured.GetProperty("guid").GetString());
        var material = structured.GetProperty("material");
        Assert.Equal("2", material.GetProperty("floats").GetProperty("_Cull").GetString());

        // material_get_properties' "colors" capability - the fixture's own m_Colors entry,
        // round-tripped through to a caller (test-coverage gap the capability audit found: this was
        // defined in the fixture but never actually asserted until now).
        var baseColor = material.GetProperty("colors").GetProperty("_BaseColor");
        Assert.Equal("1", baseColor.GetProperty("r").GetString());
        Assert.Equal("1", baseColor.GetProperty("a").GetString());

        var texture = Assert.Single(material.GetProperty("textures").EnumerateArray());
        Assert.Equal("Assets/Textures/Rock.png", texture.GetProperty("path").GetString());
        Assert.True(texture.GetProperty("resolved").GetBoolean());

        Assert.False(structured.TryGetProperty("hierarchy", out _));
    }

    [Fact]
    public async Task Structure_Material_AnUnresolvableShaderIsReportedNotFailed()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/M_Enemy.mat" }));

        var shader = structured.GetProperty("material").GetProperty("shader");
        Assert.Equal(ShaderGuid, shader.GetProperty("guid").GetString());
        Assert.False(shader.GetProperty("resolved").GetBoolean());
    }

    [Fact]
    public async Task Structure_AnimatorController_ReturnsStatesAndTransitions()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/SmokeTest.controller" }));

        Assert.Equal("structure", structured.GetProperty("depth").GetString());
        // Plan 10 Task 6: the controller asset's OWN guid, reachable here even though
        // AnimatorController is the payload, not AssetInfo - the exact gap the capability audit
        // found and closed.
        Assert.Equal(ControllerOwnGuid, structured.GetProperty("guid").GetString());

        var controller = structured.GetProperty("animatorController");
        var states = controller.GetProperty("states").EnumerateArray().ToList();
        Assert.Equal(2, states.Count);
        var idle = states.Single(s => s.GetProperty("name").GetString() == "Idle");
        Assert.True(idle.GetProperty("isDefaultState").GetBoolean());

        // animation_get_controller's "transitions"/"conditions" capability - test-coverage gap the
        // capability audit found: the old fixture's transition+condition shape was never actually
        // carried over, so this was untested through inspect_asset until now.
        var transition = Assert.Single(controller.GetProperty("transitions").EnumerateArray());
        Assert.Equal("Idle", transition.GetProperty("sourceState").GetString());
        Assert.Equal("Walk", transition.GetProperty("destinationState").GetString());

        var condition = Assert.Single(transition.GetProperty("conditions").EnumerateArray());
        Assert.Equal("Speed", condition.GetProperty("parameter").GetString());
        Assert.Equal("0.1", condition.GetProperty("threshold").GetString());
    }

    [Fact]
    public async Task Structure_PlainAsset_ReturnsTypeAndGuid()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Scripts/Health.cs" }));

        Assert.Equal("structure", structured.GetProperty("depth").GetString());
        var info = structured.GetProperty("assetInfo");
        Assert.Equal("Script", info.GetProperty("type").GetString());
        Assert.Equal(HealthScriptGuid, info.GetProperty("guid").GetString());
    }

    [Fact]
    public async Task Structure_AssetWithNoMetaFileYet_ReportsANullGuidRatherThanFailing()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/NotYetImported.asset" }));

        var info = structured.GetProperty("assetInfo");
        Assert.Equal("ScriptableObject", info.GetProperty("type").GetString());
        Assert.False(info.TryGetProperty("guid", out var guid) && guid.ValueKind != JsonValueKind.Null,
            "a missing .meta must report an absent/null guid, not fail the call");
    }

    [Fact]
    public async Task Structure_BlankPathGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "inspect_asset", new { path = "  " }));
        Assert.Contains("path", text);
    }

    [Fact]
    public async Task Structure_AFileNoLongerOnDiskGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/DoesNotExist.prefab" }));
        Assert.Contains("no longer on disk", text);
    }

    [Fact]
    public async Task Structure_PathEscapingTheProjectIsRefusedNotRead()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/../../../../../../etc/passwd" }));
        Assert.Contains("outside the project", text);
    }

    // ================================================================== inspect_asset: depth "components"

    [Fact]
    public async Task Components_ResolvesABuiltinComponentByItsUnityClassName()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", target = 1 }));

        Assert.Equal("components", structured.GetProperty("depth").GetString());
        var transform = structured.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("fileId").GetInt64() == 2);
        Assert.Equal("Transform", transform.GetProperty("typeName").GetString());
        Assert.False(transform.GetProperty("missing").GetBoolean());
    }

    [Fact]
    public async Task Components_ResolvesAMonoBehavioursScriptGuidToItsScriptPathViaTheGraph()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", target = 1 }));

        var health = structured.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("fileId").GetInt64() == 3);
        Assert.Equal("Assets/Scripts/Health.cs", health.GetProperty("typeName").GetString());
        Assert.Equal(HealthScriptGuid, health.GetProperty("scriptGuid").GetString());
        Assert.False(health.GetProperty("missing").GetBoolean());
    }

    [Fact]
    public async Task Components_AnUnresolvableScriptGuidReportsTheGuidAndFlagsItAsMissingRatherThanBeingSwallowed()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", target = 1 }));

        var broken = structured.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("fileId").GetInt64() == 4);
        Assert.True(broken.GetProperty("missing").GetBoolean());
        Assert.Equal(MissingScriptGuid, broken.GetProperty("scriptGuid").GetString());
        Assert.False(broken.TryGetProperty("typeName", out _), "an unresolved script's typeName must be omitted, not null");
    }

    [Fact]
    public async Task Components_UnknownGameObjectFileIdGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", target = 999 }));
        Assert.Contains("999", text);
    }

    // ---------------- Plan 15 Task 2: nested prefab instance placeholders (documented round trip)

    [Fact]
    public async Task Components_TheOverriddenPlaceholdersReportedFileId_ResolvesTheRealLocalOverrides()
    {
        // The literal, documented workflow: call inspect_asset(path) to see structure, then feed a
        // reported node's fileId straight back as 'target'. fileId 102 (the placeholder that owns
        // the 3 real overrides) is exactly what Structure_ANestedInstanceWithAnOverriddenRoot_...
        // reports above - this proves narrowing into it actually works end-to-end, script guid
        // resolution (the graph touch ProjectService.GetComponents adds) included.
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/NestedInstance.prefab", target = 102 }));

        Assert.Equal("components", structured.GetProperty("depth").GetString());
        var components = structured.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(2, components.Count);

        var resolved = components.Single(c => c.GetProperty("scriptGuid").GetString() == HealthScriptGuid);
        Assert.Equal("Assets/Scripts/Health.cs", resolved.GetProperty("typeName").GetString());
        Assert.False(resolved.GetProperty("missing").GetBoolean());

        var missing = components.Single(c => c.GetProperty("scriptGuid").GetString() == MissingScriptGuid);
        Assert.True(missing.GetProperty("missing").GetBoolean());
    }

    [Fact]
    public async Task Components_TheOtherPlaceholdersReportedFileId_NeverBlamesCorruption()
    {
        // fileId 101 (the placeholder that owns nothing - mirrors "Graphics" in the real repro) is
        // ALSO one of the two nodes Structure_ANestedInstanceWithAnOverriddenRoot_... reports.
        // Before this fix, feeding it back as 'target' - exactly the tool's own documented
        // instructions - threw "the file may be corrupted or hand-edited" for a completely
        // healthy file.
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/NestedInstance.prefab", target = 101 }));

        // The fix's own message explicitly DENIES corruption ("...is not corrupted or
        // hand-edited...") - a naive substring check for "corrupted" would false-fail on that
        // very denial, so this targets the specific claim that must never appear instead.
        Assert.DoesNotContain("may be corrupted", text, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================== inspect_asset: depth "properties"

    [Fact]
    public async Task Properties_ListsFieldNamesIncludingCustomOnes()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", target = 1, component = 3 }));

        Assert.Equal("properties", structured.GetProperty("depth").GetString());
        var names = structured.GetProperty("properties").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("maxHealth", names);
        Assert.Contains("m_Script", names);
    }

    [Fact]
    public async Task Properties_IncludesUnityEventListenersOnThatComponent()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", target = 1, component = 3 }));

        var events = structured.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("onDamage", e.GetProperty("eventField").GetString()));

        var wired = events.Single(e => e.GetProperty("index").GetInt32() == 0);
        Assert.Equal("SetActive", wired.GetProperty("methodName").GetString());
        var wiredTarget = wired.GetProperty("target");
        Assert.False(wiredTarget.GetProperty("isUnset").GetBoolean());
        Assert.True(wiredTarget.GetProperty("isLocal").GetBoolean());
        Assert.Equal("Assets/Enemy.prefab", wiredTarget.GetProperty("resolvedPath").GetString());

        var unwired = events.Single(e => e.GetProperty("index").GetInt32() == 1);
        Assert.True(unwired.GetProperty("target").GetProperty("isUnset").GetBoolean());
    }

    [Fact]
    public async Task Properties_ComponentWithoutTarget_IsRefusedWithGuidanceToNarrowFirst()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", component = 3 }));

        Assert.Contains("target", text);
    }

    // ================================================================== inspect_asset: depth "value"

    [Fact]
    public async Task Value_ReturnsTheNamedFieldsScalarValue()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", target = 1, component = 3, property = "maxHealth" }));

        Assert.Equal("value", structured.GetProperty("depth").GetString());
        Assert.Equal("100", structured.GetProperty("value").GetString());
        Assert.False(structured.TryGetProperty("reference", out _), "a scalar field must not fabricate reference metadata");
    }

    [Fact]
    public async Task Value_ALocalReferenceReturnsRawValueAndResolvesToItsOwnContainingFile()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", target = 1, component = 3, property = "target" }));

        Assert.Equal("1", structured.GetProperty("value").GetProperty("fileID").GetString());

        var reference = structured.GetProperty("reference");
        Assert.False(reference.GetProperty("isUnset").GetBoolean());
        Assert.True(reference.GetProperty("isLocal").GetBoolean());
        Assert.Equal("Assets/Enemy.prefab", reference.GetProperty("resolvedPath").GetString());
        Assert.True(reference.GetProperty("resolved").GetBoolean());
    }

    [Fact]
    public async Task Value_AnExternalReferenceResolvesThroughTheGraphToItsScriptPath()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", target = 1, component = 3, property = "otherAsset" }));

        var reference = structured.GetProperty("reference");
        Assert.False(reference.GetProperty("isLocal").GetBoolean());
        Assert.Equal("Assets/Scripts/Health.cs", reference.GetProperty("resolvedPath").GetString());
        Assert.True(reference.GetProperty("resolved").GetBoolean());
    }

    [Fact]
    public async Task Value_AnUnindexedExternalReferenceIsReportedUnresolvedNotAsAnError()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", target = 1, component = 3, property = "danglingAsset" }));

        var reference = structured.GetProperty("reference");
        Assert.Equal(UnindexedGuid, reference.GetProperty("targetGuid").GetString());
        Assert.False(reference.GetProperty("resolved").GetBoolean());
        Assert.False(reference.TryGetProperty("resolvedPath", out _), "an unresolved guid's resolvedPath must be omitted, not null");
    }

    [Fact]
    public async Task Value_AnExplicitlyUnsetReferenceIsReportedPlainlyNotAsAnError()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", target = 1, component = 3, property = "unassigned" }));

        Assert.True(structured.GetProperty("reference").GetProperty("isUnset").GetBoolean());
        Assert.False(structured.GetProperty("reference").GetProperty("resolved").GetBoolean());
    }

    [Fact]
    public async Task Value_AnUnknownPropertyNameGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", target = 1, component = 3, property = "notARealField" }));

        Assert.Contains("notARealField", text);
    }

    [Fact]
    public async Task Value_PropertyWithoutComponent_IsRefusedWithGuidanceToNarrowFirst()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "inspect_asset",
            new { path = "Assets/Enemy.prefab", property = "maxHealth" }));

        Assert.Contains("component", text);
    }

    // ================================================================== inspect_asset: metadata

    [Fact]
    public async Task InspectAsset_IsAdvertisedAsReadOnlyWithASchemaAndTheSavedStateClause()
    {
        var tool = await ToolMeta("inspect_asset");
        Assert.True(tool.TryGetProperty("outputSchema", out _));
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Contains("saved state on disk", tool.GetProperty("description").GetString());
    }

    // ================================================================== find_unset_references: file scope

    [Fact]
    public async Task FileScope_FindsTheUnsetReferencesInTheFile()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "find_unset_references",
            new { path = "Assets/Enemy.prefab" }));

        Assert.Equal("file", structured.GetProperty("scope").GetString());
        Assert.Equal("Assets/Enemy.prefab", structured.GetProperty("path").GetString());

        var results = structured.GetProperty("unsetReferences").EnumerateArray().ToList();
        Assert.Contains(results, r => r.GetProperty("fileId").GetInt64() == 2 && r.GetProperty("propertyPath").GetString() == "m_Father");
        Assert.Contains(results, r => r.GetProperty("fileId").GetInt64() == 3 && r.GetProperty("propertyPath").GetString() == "unassigned");
        Assert.False(structured.TryGetProperty("unityEvents", out _));
    }

    [Fact]
    public async Task FileScope_NeverReportsASetReference()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "find_unset_references",
            new { path = "Assets/Enemy.prefab" }));

        var paths = structured.GetProperty("unsetReferences").EnumerateArray()
            .Select(r => r.GetProperty("propertyPath").GetString()).ToList();

        Assert.DoesNotContain("target", paths);
        Assert.DoesNotContain("otherAsset", paths);
        Assert.DoesNotContain("danglingAsset", paths);
    }

    [Fact]
    public async Task FileScope_RespectsLimitAndReportsTruncationHonestly()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "find_unset_references",
            new { path = "Assets/Enemy.prefab", limit = 1 }));

        Assert.Single(structured.GetProperty("unsetReferences").EnumerateArray());
        Assert.True(structured.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task FileScope_UnknownPathGivesActionableGuidance()
    {
        var text = McpTestClient.ErrorText(await McpTestClient.CallTool(_factory, "find_unset_references",
            new { path = "Assets/DoesNotExist.prefab" }));

        Assert.Contains("no longer on disk", text);
    }

    // ================================================================== find_unset_references: project scope

    [Fact]
    public async Task ProjectScope_OmittingPathFindsWiredUnityEventsAcrossTheWholeProject()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "find_unset_references"));

        Assert.Equal("project", structured.GetProperty("scope").GetString());
        Assert.False(structured.TryGetProperty("path", out var pathProp) && pathProp.ValueKind != JsonValueKind.Null);

        var onDamage = structured.GetProperty("unityEvents").EnumerateArray()
            .Single(e => e.GetProperty("eventField").GetString() == "onDamage");
        Assert.Equal("Assets/Enemy.prefab", onDamage.GetProperty("path").GetString());
        Assert.Equal(3, onDamage.GetProperty("fileId").GetInt64());
        // Only ONE of the two persistent calls has a non-zero target - Unity's own null-reference
        // convention means the unwired call leaves no trace in the graph at all (see event_find_all's
        // own "HONEST LIMITATION #1" in GraphDatabase.FindUnityEvents).
        Assert.Equal(1, onDamage.GetProperty("listenerCount").GetInt32());

        Assert.False(structured.TryGetProperty("unsetReferences", out _));
    }

    [Fact]
    public async Task ProjectScope_TwoListenersOnOneFieldTargetingDifferentObjects_AggregatesListenerCountToTwo()
    {
        // Ported from the deleted GraphToolsTests.cs::EventFindAll_GroupsListenersByTheirOwnEventField -
        // find_unset_references' project scope is byte-for-byte the same ProjectService.FindUnityEvents
        // call event_find_all used, so this proves the identical aggregation behaviour survives.
        var structured = Structured(await McpTestClient.CallTool(_factory, "find_unset_references"));

        var onClick = structured.GetProperty("unityEvents").EnumerateArray()
            .Single(e => e.GetProperty("path").GetString() == "Assets/TwoTargets.prefab"
                      && e.GetProperty("eventField").GetString() == "m_OnClick");
        Assert.Equal(2, onClick.GetProperty("listenerCount").GetInt32());
    }

    [Fact]
    public async Task ProjectScope_ABlankPathIsTreatedAsOmitted()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "find_unset_references", new { path = "   " }));
        Assert.Equal("project", structured.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task ProjectScope_RespectsLimitAndReportsTruncationHonestly()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "find_unset_references", new { limit = 1 }));

        Assert.Single(structured.GetProperty("unityEvents").EnumerateArray());
        Assert.True(structured.GetProperty("truncated").GetBoolean());
    }

    // ================================================================== find_unset_references: metadata

    [Fact]
    public async Task FindUnsetReferences_IsAdvertisedAsReadOnlyWithASchemaAndTheSavedStateClause()
    {
        var tool = await ToolMeta("find_unset_references");
        Assert.True(tool.TryGetProperty("outputSchema", out _));
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Contains("saved state on disk", tool.GetProperty("description").GetString());

        // The plan's own requirement, carried forward from reference_find_unset: since Hades cannot
        // tell deliberate-vs-forgotten apart from the data, the description must say so plainly.
        var description = tool.GetProperty("description").GetString()!;
        Assert.Contains("cannot", description);
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
