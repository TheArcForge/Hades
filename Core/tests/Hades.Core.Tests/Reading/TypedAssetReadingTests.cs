using Hades.Core.Reading;

namespace Hades.Core.Tests.Reading;

/// <summary>
/// The read-through mechanism behind material_get_properties, animation_get_controller and
/// analyze_render_pipeline - typed readers for single-purpose asset kinds, all built on the same
/// ReadNode / UnityYamlReader machinery ReadThrough already exercises for scenes, prefabs and
/// components. See ReadThroughTests' class doc comment for why this sits in its own file per plan
/// task rather than growing ReadThroughTests.cs indefinitely.
/// </summary>
public class TypedAssetReadingTests : IDisposable
{
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
    const string LocalShaderGuid = "ccccccccccccccccccccccccccccccc1";
    const string BaseMapGuid = "ccccccccccccccccccccccccccccccc2";
    const string UnresolvableGuid = "0000000000000000f000000000000000"; // Unity's builtin-resources sentinel

    public TypedAssetReadingTests() => Directory.CreateDirectory(Path.Combine(_projectRoot, "Assets"));

    void Write(string relative, string body, string? guid = null)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        if (guid is not null) File.WriteAllText(full + ".meta", $"fileFormatVersion: 2\nguid: {guid}\n");
    }

    // Mirrors the real Hades-Unity-Client M_Enemy.mat shape: a resolvable shader, one set texture
    // (_BaseMap) and several unset ones (fileID: 0), a handful of floats, and two colors.
    const string MaterialBody = """
        --- !u!21 &2100000
        Material:
          serializedVersion: 8
          m_Name: M_Enemy
          m_Shader: {fileID: 4800000, guid: SHADER_GUID, type: 3}
          m_SavedProperties:
            serializedVersion: 3
            m_TexEnvs:
            - _BaseMap:
                m_Texture: {fileID: 2800000, guid: TEXTURE_GUID, type: 3}
                m_Scale: {x: 1, y: 1}
                m_Offset: {x: 0, y: 0}
            - _BumpMap:
                m_Texture: {fileID: 0}
                m_Scale: {x: 1, y: 1}
                m_Offset: {x: 0, y: 0}
            m_Ints: []
            m_Floats:
            - _Cull: 2
            - _Metallic: 0.5
            m_Colors:
            - _BaseColor: {r: 1, g: 1, b: 1, a: 1}
            - _EmissionColor: {r: 0, g: 0, b: 0, a: 1}
        """;

    [Fact]
    public void GetMaterialProperties_AResolvableShaderReportsItsGuidAndPath()
    {
        Write("Assets/Shaders/Custom.shader", "fake shader source", LocalShaderGuid);
        Write("Assets/M_Enemy.mat", Header + MaterialBody.Replace("SHADER_GUID", LocalShaderGuid).Replace("TEXTURE_GUID", BaseMapGuid));

        var material = ReadThrough.GetMaterialProperties(_projectRoot, "Assets/M_Enemy.mat");

        Assert.Equal(LocalShaderGuid, material.Shader.Guid);
        Assert.Equal("Assets/Shaders/Custom.shader", material.Shader.Path);
        Assert.True(material.Shader.Resolved);
    }

    [Fact]
    public void GetMaterialProperties_AnUnresolvableShaderStillReturnsEveryOtherPropertyRatherThanFailing()
    {
        // Built-in and package shaders (Standard, URP/Lit, ...) have no file under any scan root -
        // this is the COMMON case for a real material, not an edge case.
        Write("Assets/M_Enemy.mat", Header + MaterialBody.Replace("SHADER_GUID", UnresolvableGuid).Replace("TEXTURE_GUID", BaseMapGuid));

        var material = ReadThrough.GetMaterialProperties(_projectRoot, "Assets/M_Enemy.mat");

        Assert.Equal(UnresolvableGuid, material.Shader.Guid);
        Assert.Null(material.Shader.Path);
        Assert.False(material.Shader.Resolved);
        Assert.NotEmpty(material.Floats);
        Assert.NotEmpty(material.Colors);
    }

    [Fact]
    public void GetMaterialProperties_ReadsFloatAndColorPropertiesByName()
    {
        Write("Assets/M_Enemy.mat", Header + MaterialBody.Replace("SHADER_GUID", UnresolvableGuid).Replace("TEXTURE_GUID", BaseMapGuid));

        var material = ReadThrough.GetMaterialProperties(_projectRoot, "Assets/M_Enemy.mat");

        Assert.Equal("2", material.Floats["_Cull"]);
        Assert.Equal("0.5", material.Floats["_Metallic"]);

        Assert.Equal("1", material.Colors["_BaseColor"]["r"]);
        Assert.Equal("0", material.Colors["_EmissionColor"]["r"]);
    }

    [Fact]
    public void GetMaterialProperties_ASetTextureResolvesFromGuidToAssetPath()
    {
        Write("Assets/Textures/Rock.png", "fake texture bytes", BaseMapGuid);
        Write("Assets/M_Enemy.mat", Header + MaterialBody.Replace("SHADER_GUID", UnresolvableGuid).Replace("TEXTURE_GUID", BaseMapGuid));

        var material = ReadThrough.GetMaterialProperties(_projectRoot, "Assets/M_Enemy.mat");

        var baseMap = Assert.Single(material.Textures);
        Assert.Equal("_BaseMap", baseMap.Property);
        Assert.Equal(BaseMapGuid, baseMap.Guid);
        Assert.Equal("Assets/Textures/Rock.png", baseMap.Path);
        Assert.True(baseMap.Resolved);
    }

    [Fact]
    public void GetMaterialProperties_AnUnsetTextureSlotIsOmittedRatherThanReportedAsUnresolved()
    {
        // _BumpMap in the fixture is {fileID: 0} - Unity's "nothing assigned" convention, not a
        // broken reference. Reporting 13 empty slots per material would bury the one that matters.
        Write("Assets/M_Enemy.mat", Header + MaterialBody.Replace("SHADER_GUID", UnresolvableGuid).Replace("TEXTURE_GUID", BaseMapGuid));

        var material = ReadThrough.GetMaterialProperties(_projectRoot, "Assets/M_Enemy.mat");

        Assert.DoesNotContain(material.Textures, t => t.Property == "_BumpMap");
    }

    [Fact]
    public void GetMaterialProperties_PathEscapingTheProjectIsRefused()
    {
        Write("Assets/M_Enemy.mat", Header + MaterialBody.Replace("SHADER_GUID", UnresolvableGuid).Replace("TEXTURE_GUID", BaseMapGuid));

        Assert.Throws<ArgumentException>(
            () => ReadThrough.GetMaterialProperties(_projectRoot, "Assets/../../../../etc/passwd"));
    }

    [Fact]
    public void GetMaterialProperties_AFileNotOnDiskGivesAClearError()
    {
        var ex = Assert.Throws<FileNotFoundException>(
            () => ReadThrough.GetMaterialProperties(_projectRoot, "Assets/Missing.mat"));
        Assert.Contains("Missing.mat", ex.Message);
    }

    [Fact]
    public void GetMaterialProperties_AMaterialPrecededByAnEditorAssetVersionDocumentStillReadsTheMaterial()
    {
        // A material is not always exactly one document: the real Hades-Unity-Client
        // M_Enemy.mat (URP) writes an editor-only "AssetVersion" MonoBehaviour BEFORE the actual
        // Material document. This is exactly the bug the real-project smoke test caught - reading
        // "the first document" silently returned an empty, wrong result instead of failing loudly.
        var withLeadingAssetVersionDocument = $$"""
            --- !u!114 &-5485466173493303354
            MonoBehaviour:
              m_GameObject: {fileID: 0}
              m_Script: {fileID: 11500000, guid: d0353a89b1f911e48b9e16bdc9f2e058, type: 3}
              m_Name:
              m_EditorClassIdentifier: Unity.RenderPipelines.Universal.Editor::UnityEditor.Rendering.Universal.AssetVersion
              version: 10
            --- !u!21 &2100000
            Material:
              serializedVersion: 8
              m_Name: M_Enemy
              m_Shader: {fileID: 4800000, guid: {{UnresolvableGuid}}, type: 3}
              m_SavedProperties:
                serializedVersion: 3
                m_TexEnvs: []
                m_Ints: []
                m_Floats:
                - _Cull: 2
                m_Colors:
                - _BaseColor: {r: 1, g: 1, b: 1, a: 1}
            """;
        Write("Assets/M_Enemy.mat", Header + withLeadingAssetVersionDocument);

        var material = ReadThrough.GetMaterialProperties(_projectRoot, "Assets/M_Enemy.mat");

        Assert.Equal(UnresolvableGuid, material.Shader.Guid);
        Assert.Equal("2", material.Floats["_Cull"]);
        Assert.Equal("1", material.Colors["_BaseColor"]["r"]);
    }

    // ---------------------------------------------------------------- guid resolution

    [Fact]
    public void ResolveGuidsToPaths_FindsAnAssetByItsMetaGuidAnywhereUnderAScanRoot()
    {
        Write("Assets/Deep/Nested/Texture.png", "bytes", BaseMapGuid);

        var resolved = ReadThrough.ResolveGuidsToPaths(_projectRoot, new HashSet<string> { BaseMapGuid });

        Assert.Equal("Assets/Deep/Nested/Texture.png", resolved[BaseMapGuid]);
    }

    [Fact]
    public void ResolveGuidsToPaths_AGuidNothingOwnsIsSimplyAbsentFromTheResult()
    {
        var resolved = ReadThrough.ResolveGuidsToPaths(_projectRoot, new HashSet<string> { UnresolvableGuid });

        Assert.False(resolved.ContainsKey(UnresolvableGuid));
    }

    [Fact]
    public void ResolveGuidsToPaths_AnEmptyRequestReturnsEmptyWithoutScanningAnything()
    {
        var resolved = ReadThrough.ResolveGuidsToPaths(_projectRoot, new HashSet<string>());
        Assert.Empty(resolved);
    }

    // ---------------------------------------------------------------- GetAnimatorController

    // Transcribed verbatim from the real Hades-Unity-Client Assets/Animations/SmokeTest.controller:
    // two states (Idle, the default; Walk) in one layer's state machine, one transition from Idle
    // to Walk gated on a "Speed" float parameter. Real fileIds kept, not renumbered - this is
    // exactly the shape animation_get_controller must handle, including the two gotchas that shape
    // exposes: m_Transitions ([{fileID: ...}], a BARE flow mapping directly under a sequence with
    // no per-item key) and m_DstState ({fileID: ...} following a proper key) are structurally
    // different despite looking similar, and UnityYamlReader only captures references for the
    // latter (see ReadThrough.BuildHierarchy's own note on the identical m_Children gotcha).
    const string ControllerBody = """
        --- !u!91 &9100000
        AnimatorController:
          m_Name: SmokeTest
          serializedVersion: 5
          m_AnimatorParameters:
          - m_Name: Speed
            m_Type: 1
            m_DefaultFloat: 0
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
        """;

    [Fact]
    public void GetAnimatorController_ListsEveryStateWithTheDefaultFlaggedCorrectly()
    {
        Write("Assets/SmokeTest.controller", Header + ControllerBody);

        var controller = ReadThrough.GetAnimatorController(_projectRoot, "Assets/SmokeTest.controller");

        Assert.Equal(["Walk", "Idle"], controller.States.Select(s => s.Name));

        var idle = controller.States.Single(s => s.Name == "Idle");
        Assert.True(idle.IsDefaultState);
        Assert.Equal(6673564061869833467, idle.FileId);

        var walk = controller.States.Single(s => s.Name == "Walk");
        Assert.False(walk.IsDefaultState);
    }

    [Fact]
    public void GetAnimatorController_ATransitionReportsItsSourceAndDestinationStateNames()
    {
        // The crux of the m_Transitions gotcha: Idle's OWN m_Transitions list (a bare
        // {fileID: ...} under a sequence, invisible to UnityYamlReader's reference extraction) is
        // the only local signal that this transition belongs to Idle, not Walk.
        Write("Assets/SmokeTest.controller", Header + ControllerBody);

        var controller = ReadThrough.GetAnimatorController(_projectRoot, "Assets/SmokeTest.controller");

        var transition = Assert.Single(controller.Transitions);
        Assert.Equal("Idle", transition.SourceState);
        Assert.Equal("Walk", transition.DestinationState);
    }

    [Fact]
    public void GetAnimatorController_ATransitionReportsItsConditions()
    {
        Write("Assets/SmokeTest.controller", Header + ControllerBody);

        var controller = ReadThrough.GetAnimatorController(_projectRoot, "Assets/SmokeTest.controller");

        var condition = Assert.Single(Assert.Single(controller.Transitions).Conditions);
        Assert.Equal("Speed", condition.Parameter);
        Assert.Equal("3", condition.ConditionMode);
        Assert.Equal("0.1", condition.Threshold);
    }

    [Fact]
    public void GetAnimatorController_AnAnyStateTransitionReportsAnyStateAsItsSource()
    {
        const string withAnyState = """
            --- !u!91 &9100000
            AnimatorController:
              m_Name: AnyStateTest
              serializedVersion: 5
              m_AnimatorLayers:
              - serializedVersion: 5
                m_Name: Base Layer
                m_StateMachine: {fileID: 100}
                m_Controller: {fileID: 9100000}
            --- !u!1107 &100
            AnimatorStateMachine:
              serializedVersion: 6
              m_Name: Base Layer
              m_ChildStates:
              - serializedVersion: 1
                m_State: {fileID: 200}
                m_Position: {x: 0, y: 0, z: 0}
              m_ChildStateMachines: []
              m_AnyStateTransitions:
              - {fileID: 300}
              m_DefaultState: {fileID: 200}
            --- !u!1102 &200
            AnimatorState:
              serializedVersion: 6
              m_Name: Stunned
              m_Transitions: []
              m_Motion: {fileID: 0}
            --- !u!1101 &300
            AnimatorStateTransition:
              m_Name:
              m_Conditions: []
              m_DstStateMachine: {fileID: 0}
              m_DstState: {fileID: 200}
              m_IsExit: 0
              serializedVersion: 3
            """;
        Write("Assets/AnyState.controller", Header + withAnyState);

        var controller = ReadThrough.GetAnimatorController(_projectRoot, "Assets/AnyState.controller");

        var transition = Assert.Single(controller.Transitions);
        Assert.Equal("Any State", transition.SourceState);
        Assert.Equal("Stunned", transition.DestinationState);
    }

    [Fact]
    public void GetAnimatorController_AnExitTransitionHasNoDestinationState()
    {
        const string withExit = """
            --- !u!91 &9100000
            AnimatorController:
              m_Name: ExitTest
              serializedVersion: 5
              m_AnimatorLayers:
              - serializedVersion: 5
                m_Name: Base Layer
                m_StateMachine: {fileID: 100}
                m_Controller: {fileID: 9100000}
            --- !u!1107 &100
            AnimatorStateMachine:
              serializedVersion: 6
              m_Name: Base Layer
              m_ChildStates:
              - serializedVersion: 1
                m_State: {fileID: 200}
                m_Position: {x: 0, y: 0, z: 0}
              m_ChildStateMachines: []
              m_AnyStateTransitions: []
              m_DefaultState: {fileID: 200}
            --- !u!1102 &200
            AnimatorState:
              serializedVersion: 6
              m_Name: Death
              m_Transitions:
              - {fileID: 300}
              m_Motion: {fileID: 0}
            --- !u!1101 &300
            AnimatorStateTransition:
              m_Name:
              m_Conditions: []
              m_DstStateMachine: {fileID: 0}
              m_DstState: {fileID: 0}
              m_IsExit: 1
              serializedVersion: 3
            """;
        Write("Assets/Exit.controller", Header + withExit);

        var controller = ReadThrough.GetAnimatorController(_projectRoot, "Assets/Exit.controller");

        var transition = Assert.Single(controller.Transitions);
        Assert.Equal("Death", transition.SourceState);
        Assert.Null(transition.DestinationState);
    }

    [Fact]
    public void GetAnimatorController_PathEscapingTheProjectIsRefused()
    {
        Write("Assets/SmokeTest.controller", Header + ControllerBody);

        Assert.Throws<ArgumentException>(
            () => ReadThrough.GetAnimatorController(_projectRoot, "Assets/../../../../etc/passwd"));
    }

    [Fact]
    public void GetAnimatorController_AFileNotOnDiskGivesAClearError()
    {
        var ex = Assert.Throws<FileNotFoundException>(
            () => ReadThrough.GetAnimatorController(_projectRoot, "Assets/Missing.controller"));
        Assert.Contains("Missing.controller", ex.Message);
    }

    // ---------------------------------------------------------------- AnalyzeRenderPipeline

    const string UrpScriptGuid = "bf2edee5c58d82540a51f03df9d42094";
    const string HdrpScriptGuid = "0cf1dab834d4ec34195b920ea7bbf9ec";
    const string CustomSrpScriptGuid = "ddddddddddddddddddddddddddddddd1";
    const string PipelineAssetGuid = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    static string GraphicsSettingsBody(string customRenderPipelineLine) => $$"""
        --- !u!30 &1
        GraphicsSettings:
          serializedVersion: 16
          {{customRenderPipelineLine}}
        """;

    static string PipelineAssetBody(string scriptGuid) => $$"""
        --- !u!114 &11400000
        MonoBehaviour:
          m_GameObject: {fileID: 0}
          m_Script: {fileID: 11500000, guid: {{scriptGuid}}, type: 3}
          m_Name: PC_RPAsset
        """;

    [Fact]
    public void AnalyzeRenderPipeline_NoCustomPipelineReportsBuiltIn()
    {
        Write("ProjectSettings/GraphicsSettings.asset", Header + GraphicsSettingsBody("m_CustomRenderPipeline: {fileID: 0}"));

        var result = ReadThrough.AnalyzeRenderPipeline(_projectRoot);

        Assert.Equal("Built-in", result.Pipeline);
        Assert.Null(result.PipelineAssetPath);
    }

    [Fact]
    public void AnalyzeRenderPipeline_AUniversalRenderPipelineAssetIsIdentifiedByItsScriptGuid()
    {
        Write("ProjectSettings/GraphicsSettings.asset", Header + GraphicsSettingsBody(
            $"m_CustomRenderPipeline: {{fileID: 11400000, guid: {PipelineAssetGuid}, type: 2}}"));
        Write("Assets/Settings/PC_RPAsset.asset", Header + PipelineAssetBody(UrpScriptGuid), PipelineAssetGuid);

        var result = ReadThrough.AnalyzeRenderPipeline(_projectRoot);

        Assert.Equal("URP", result.Pipeline);
        Assert.Equal("Assets/Settings/PC_RPAsset.asset", result.PipelineAssetPath);
    }

    [Fact]
    public void AnalyzeRenderPipeline_AHighDefinitionRenderPipelineAssetIsIdentifiedByItsScriptGuid()
    {
        Write("ProjectSettings/GraphicsSettings.asset", Header + GraphicsSettingsBody(
            $"m_CustomRenderPipeline: {{fileID: 11400000, guid: {PipelineAssetGuid}, type: 2}}"));
        Write("Assets/Settings/HDRPAsset.asset", Header + PipelineAssetBody(HdrpScriptGuid), PipelineAssetGuid);

        var result = ReadThrough.AnalyzeRenderPipeline(_projectRoot);

        Assert.Equal("HDRP", result.Pipeline);
        Assert.Equal("Assets/Settings/HDRPAsset.asset", result.PipelineAssetPath);
    }

    [Fact]
    public void AnalyzeRenderPipeline_ACustomPipelineWithAnUnrecognisedScriptGuidReportsUnknownRatherThanGuessing()
    {
        // A third-party or hand-rolled SRP - a real, unremarkable state. Guessing "probably URP"
        // because that is the common case would be exactly the partial-match guess this must avoid.
        Write("ProjectSettings/GraphicsSettings.asset", Header + GraphicsSettingsBody(
            $"m_CustomRenderPipeline: {{fileID: 11400000, guid: {PipelineAssetGuid}, type: 2}}"));
        Write("Assets/Settings/CustomSrp.asset", Header + PipelineAssetBody(CustomSrpScriptGuid), PipelineAssetGuid);

        var result = ReadThrough.AnalyzeRenderPipeline(_projectRoot);

        Assert.Equal("unknown", result.Pipeline);
        // Still reports where the (unidentified) pipeline asset lives - found, just not named.
        Assert.Equal("Assets/Settings/CustomSrp.asset", result.PipelineAssetPath);
    }

    [Fact]
    public void AnalyzeRenderPipeline_ACustomPipelineGuidThatResolvesToNothingOnDiskReportsUnknown()
    {
        // The referenced asset could be a package default this project's scan roots never reach -
        // must degrade to unknown, not throw, and not report a path it never actually found.
        Write("ProjectSettings/GraphicsSettings.asset", Header + GraphicsSettingsBody(
            $"m_CustomRenderPipeline: {{fileID: 11400000, guid: {PipelineAssetGuid}, type: 2}}"));

        var result = ReadThrough.AnalyzeRenderPipeline(_projectRoot);

        Assert.Equal("unknown", result.Pipeline);
        Assert.Null(result.PipelineAssetPath);
    }

    [Fact]
    public void AnalyzeRenderPipeline_MissingGraphicsSettingsGivesAClearError()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => ReadThrough.AnalyzeRenderPipeline(_projectRoot));
        Assert.Contains("GraphicsSettings.asset", ex.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
    }
}
