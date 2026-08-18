using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Editors;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WireJson = Hades.Contract.Wire.JsonValue;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Mcp;

/// <summary>One entry of project_settings_apply's 'operations' array - see SceneApplyOperation's
/// own doc comment (SceneApplyTool.cs) for why this is one flat record rather than a discriminated
/// union. Every field is spelled exactly as the corresponding single-purpose tool it replaces
/// already names it. Field names are disambiguated by op where the SAME field name would otherwise
/// be genuinely ambiguous across two DIFFERENT domains this one batch spans (unlike material_apply/
/// animation_apply, which each stay within one domain) - 'name' means "tag name" for createTag/
/// deleteTag and "layer name" for createLayer, which is fine because 'op' already disambiguates
/// which one applies, the same way scene_apply's single 'target' means different things per op.
/// <see cref="Scenes"/> and <see cref="Clips"/> reuse <see cref="BuildSceneSpec"/> (from
/// EditorSceneManagementTools.cs) and <see cref="ClipImportConfig"/> (from EditorAssetTools.cs)
/// verbatim rather than inventing parallel shapes for the two ops whose fields are not flat
/// scalars.</summary>
public sealed record ProjectSettingsApplyOperation : IBatchOperation
{
    [JsonPropertyName("op")] public required string Op { get; init; }

    // createTag / deleteTag / createLayer
    [JsonPropertyName("name")] [OpField("createTag", "deleteTag", "createLayer")] public string? Name { get; init; }

    // createLayer
    [JsonPropertyName("layerIndex")] [OpField("createLayer")] public int? LayerIndex { get; init; }

    // setBuildScenes
    [JsonPropertyName("scenes")] [OpField("setBuildScenes")] public IReadOnlyList<BuildSceneSpec>? Scenes { get; init; }

    // setImportSettings / setClipImportSettings
    [JsonPropertyName("path")] [OpField("setImportSettings", "setClipImportSettings")] public string? Path { get; init; }

    // setImportSettings
    [JsonPropertyName("properties")] [OpField("setImportSettings")] public IReadOnlyDictionary<string, JsonElement>? Properties { get; init; }

    // setClipImportSettings
    [JsonPropertyName("clips")] [OpField("setClipImportSettings")] public IReadOnlyList<ClipImportConfig>? Clips { get; init; }

    // Backing store for [JsonExtensionData] - see OperationFieldValidator's own doc comment for why
    // an unrecognised field must be captured, not silently dropped, to be catchable at all.
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>One successful operation's own result, echoed verbatim - see MaterialApplyOpResult's
/// own doc comment for why this is a loosely-typed passthrough rather than a dozen-mostly-null-field
/// record: createLayer returns 'index' (the one piece of data a caller cannot know in advance),
/// setBuildScenes returns its own 'scenes'/'count', and setImportSettings/setClipImportSettings
/// BOTH return {path, applied, failed} - a per-property/per-clip outcome list using the SAME
/// applied/failed vocabulary this batch's own outer envelope uses, one level down (converged;
/// setClipImportSettings used to answer with a 'updatedClips'/'errors' pair found nowhere else in
/// this codebase - MutationToolValidation.md Table 6/Table 8 gap #4) - each op's own shape, never
/// invented or flattened here.
/// </summary>
public sealed record ProjectSettingsApplyOpResult
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
}

public sealed record ProjectSettingsApplyFailure
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("error")] public required string Error { get; init; }
}

public sealed record ProjectSettingsApplyResult
{
    [JsonPropertyName("applied")] public required IReadOnlyList<int> Applied { get; init; }
    [JsonPropertyName("results")] public required IReadOnlyList<ProjectSettingsApplyOpResult> Results { get; init; }
    [JsonPropertyName("failed")] public required IReadOnlyList<ProjectSettingsApplyFailure> Failed { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
}

/// <summary>
/// The declarative batch that replaces EditorTagLayerTools' tag_create/tag_delete/layer_create,
/// EditorSceneManagementTools' scene_set_build, and EditorAssetTools' asset_set_import_settings/
/// asset_set_clip_import_settings - 6 tools - with the SAME shape Plan 10 Task 1's
/// <see cref="SceneApplyTool"/> established: one MCP call sends the WHOLE 'operations' array in ONE
/// <c>projectSettings.apply</c> wire call to <see cref="Hades.Tools.ProjectSettingsApplyCommands"/>
/// (Plugin~), which applies every operation directly inside one handler body. Ordering,
/// partial-failure reporting (<c>applied</c>/<c>failed</c> by index, never rolled back), per-op
/// result data in 'results' (see <see cref="ProjectSettingsApplyOpResult"/>'s own doc comment), and
/// the "unknown op refused before any wire call" rule are all identical to scene_apply/
/// material_apply/animation_apply - see SceneApplyTool's own class doc comment for the shared
/// rationale, not repeated here.
///
/// <para><b>Mixed lease classes, unlike material_apply/animation_apply.</b> createTag/deleteTag/
/// createLayer/setBuildScenes are class-1 (no reload lease - TagLayerCommands/
/// SceneManagementCommands never touch the gate); setImportSettings/setClipImportSettings are
/// class-2 (AssetCommands' own "class-2 trio" wraps them in LeaseScope.Run, since reimporting can
/// trigger the asset pipeline). The plugin-side handler wraps the WHOLE batch in exactly ONE lease
/// window regardless of which ops a given call happens to contain - the same reasoning
/// <see cref="Hades.Tools.PrefabApplyCommands"/> (Plan 10 Task 2) already established for its own
/// mixed class-1/class-2 batch - see that class's own doc comment for why calling each operation's
/// normal, self-leasing entry point per-op would be both wasteful and unsafe.</para>
///
/// <para><b>Undo: self-managed, not a MutatingMethods entry.</b> Same reasoning as
/// <see cref="Hades.Tools.PrefabApplyCommands"/>: because this batch is already wrapped in its own
/// LeaseScope.Run (for the class-2 ops), it self-manages ONE Undo.IncrementCurrentGroup() call too,
/// rather than relying on CommandTable.Dispatch's pre-increment (which material.apply/
/// animation.apply, both entirely class-1, use instead). Whether Undo meaningfully covers any given
/// operation is itself uneven and already documented per-tool - TagLayerCommands' create/delete are
/// "best-effort, not a tested claim" (ProjectSettings/TagManager.asset is a project-level singleton,
/// not scene-local state); scene.set_build and the two asset-import-settings ops have NONE (a
/// static property and project configuration, respectively, neither of which
/// <see cref="UnityEditor.Undo.RecordObject"/> can snapshot) - a caller must not assume a single
/// Ctrl/Cmd+Z reverts a project_settings_apply batch the reliable way it does scene_apply's.</para>
/// </summary>
[McpServerToolType]
public sealed class ProjectSettingsApplyTool(EditorProxy editor, ProjectService projects)
{
    static readonly string[] ValidOps =
        ["createTag", "deleteTag", "createLayer", "setBuildScenes", "setImportSettings", "setClipImportSettings"];

    [McpServerTool(Name = "project_settings_apply", Title = "Apply Project Settings (Batch)", ReadOnly = false, UseStructuredContent = true)]
    [Description("Applies a batch of project-settings mutations - createTag(name), deleteTag(name), "
               + "createLayer(name, layerIndex?), setBuildScenes(scenes: [{path, enabled?}]), "
               + "setImportSettings(path, properties: {name: value}), setClipImportSettings(path, "
               + "clips: [{name, loopTime?, loopPose?, cycleOffset?, firstFrame?, lastFrame?}]) - in "
               + "ONE call, in the order given. This is the batch form of tag_create/tag_delete/"
               + "layer_create/scene_set_build/asset_set_import_settings/asset_set_clip_import_settings. "
               + "UNDO IS UNEVEN, UNLIKE scene_apply: tag/layer ops are best-effort (never a tested "
               + "claim - ProjectSettings/TagManager.asset is a project singleton, not scene-local "
               + "state); setBuildScenes and the two import-settings ops have NO Undo at all (a "
               + "static property and project configuration respectively) - do not assume one "
               + "Ctrl/Cmd+Z reverts this batch. "
               + "PARTIAL FAILURE, NOT ROLLED BACK: each operation's outcome is reported by its "
               + "0-based index in 'applied' (succeeded) or 'failed' (with its own error) - "
               + "operations that already succeeded are never undone because a LATER one failed. "
               + "Every successful operation's own result (e.g. createLayer's assigned 'index', the "
               + "one thing a caller cannot know in advance) is reported in 'results' alongside "
               + "'applied', never just a bare success flag. "
               + "An unrecognised 'op' value rejects the WHOLE call before anything is sent to the "
               + "Editor, listing the valid ops. Needs a live Editor - call hades_charon_status first "
               + "if unsure.")]
    public async Task<ProjectSettingsApplyResult> ProjectSettingsApply(
        [Description("Operations to apply, in order. Each needs 'op' plus that op's own fields: "
                   + "createTag{name}, deleteTag{name}, createLayer{name,layerIndex?}, "
                   + "setBuildScenes{scenes:[{path,enabled?}]}, "
                   + "setImportSettings{path,properties:{name:value}}, "
                   + "setClipImportSettings{path,clips:[{name,loopTime?,loopPose?,cycleOffset?,firstFrame?,lastFrame?}]}.")]
        IReadOnlyList<ProjectSettingsApplyOperation> operations,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        if (operations is null || operations.Count == 0)
            throw new McpException("project_settings_apply needs a non-empty 'operations' array.");

        // Refused, not ignored - see SceneApplyTool's own doc comment. Nothing is sent to the
        // Editor until every operation's 'op' is confirmed valid.
        for (var i = 0; i < operations.Count; i++)
        {
            if (Array.IndexOf(ValidOps, operations[i].Op) < 0)
            {
                throw new McpException(
                    $"project_settings_apply operations[{i}]: unknown op '{operations[i].Op}'. Valid ops: {string.Join(", ", ValidOps)}.");
            }
        }

        // Refused, not ignored - see OperationFieldValidator's own doc comment. An unrecognised
        // FIELD name on an otherwise-valid op is the same class of caller mistake as an unrecognised
        // op value above, and gets the identical whole-call, zero-wire-calls treatment.
        OperationFieldValidator.RejectUnknownFields("project_settings_apply", operations);

        var wireOperations = WireJson.NewArray();
        foreach (var op in operations) wireOperations.Add(BuildOperation(op));

        var @params = WireJson.NewObject().SetProperty("operations", wireOperations);
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var result = await editor.SendCommandAsync(productGuid, "projectSettings.apply", @params).ConfigureAwait(false);

        return MapResult(result, operations.Count);
    }

    // ---------------------------------------------------------------- app op -> wire op (field-for-field, no renaming)

    static WireJson BuildOperation(ProjectSettingsApplyOperation op)
    {
        var o = WireJson.NewObject().SetProperty("op", WireJson.String(op.Op));

        if (!string.IsNullOrEmpty(op.Name)) o.SetProperty("name", WireJson.String(op.Name));
        if (op.LayerIndex is not null) o.SetProperty("layerIndex", WireJson.Integer(op.LayerIndex.Value));

        if (op.Scenes is { Count: > 0 })
        {
            var scenes = WireJson.NewArray();
            foreach (var s in op.Scenes)
            {
                var entry = WireJson.NewObject().SetProperty("path", WireJson.String(s.Path));
                if (s.Enabled is { } enabled) entry.SetProperty("enabled", WireJson.Bool(enabled));
                scenes.Add(entry);
            }
            o.SetProperty("scenes", scenes);
        }

        if (!string.IsNullOrEmpty(op.Path)) o.SetProperty("path", WireJson.String(op.Path));

        if (op.Properties is { Count: > 0 })
        {
            var properties = WireJson.NewObject();
            foreach (var (key, value) in op.Properties) properties.SetProperty(key, WireJsonBridge.ToWire(value));
            o.SetProperty("properties", properties);
        }

        if (op.Clips is { Count: > 0 })
        {
            var clips = WireJson.NewArray();
            foreach (var clip in op.Clips)
            {
                var clipJson = WireJson.NewObject().SetProperty("name", WireJson.String(clip.Name));
                if (clip.LoopTime.HasValue) clipJson.SetProperty("loopTime", WireJson.Bool(clip.LoopTime.Value));
                if (clip.LoopPose.HasValue) clipJson.SetProperty("loopPose", WireJson.Bool(clip.LoopPose.Value));
                if (clip.CycleOffset.HasValue) clipJson.SetProperty("cycleOffset", WireJson.Float(clip.CycleOffset.Value));
                if (clip.FirstFrame.HasValue) clipJson.SetProperty("firstFrame", WireJson.Float(clip.FirstFrame.Value));
                if (clip.LastFrame.HasValue) clipJson.SetProperty("lastFrame", WireJson.Float(clip.LastFrame.Value));
                clips.Add(clipJson);
            }
            o.SetProperty("clips", clips);
        }

        return o;
    }

    // ---------------------------------------------------------------- wire result -> ProjectSettingsApplyResult

    static ProjectSettingsApplyResult MapResult(WireJson result, int operationCount)
    {
        var applied = new List<int>();
        if (result.TryGetProperty("applied", out var appliedJson) && appliedJson!.Kind == WireKind.Array)
            foreach (var item in appliedJson.Items) applied.Add((int)item.AsInteger());

        var results = new List<ProjectSettingsApplyOpResult>();
        if (result.TryGetProperty("results", out var resultsJson) && resultsJson!.Kind == WireKind.Array)
        {
            foreach (var item in resultsJson.Items)
            {
                results.Add(new ProjectSettingsApplyOpResult
                {
                    Index = (int)EditorComponentTools.Int(item, "index"),
                    Op = EditorComponentTools.Str(item, "op"),
                    Result = item.TryGetProperty("result", out var r) ? WireJsonBridge.ToClr(r!) : null,
                });
            }
        }

        var failed = new List<ProjectSettingsApplyFailure>();
        if (result.TryGetProperty("failed", out var failedJson) && failedJson!.Kind == WireKind.Array)
        {
            foreach (var item in failedJson.Items)
            {
                failed.Add(new ProjectSettingsApplyFailure
                {
                    Index = (int)EditorComponentTools.Int(item, "index"),
                    Op = EditorComponentTools.Str(item, "op"),
                    Error = EditorComponentTools.Str(item, "error"),
                });
            }
        }

        var summary = result.TryGetProperty("summary", out var summaryJson) && summaryJson!.Kind == WireKind.String
            ? summaryJson.AsString()
            : $"{applied.Count} applied, {failed.Count} failed of {operationCount} operation(s).";

        return new ProjectSettingsApplyResult { Applied = applied, Results = results, Failed = failed, Summary = summary };
    }
}
