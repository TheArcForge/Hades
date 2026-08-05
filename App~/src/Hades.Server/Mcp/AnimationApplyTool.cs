using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hades.Core.Editors;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WireJson = Hades.Contract.Wire.JsonValue;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Mcp;

/// <summary>One entry of animation_apply's 'operations' array - see SceneApplyOperation's own doc
/// comment (SceneApplyTool.cs) for why this is one flat record rather than a discriminated union.
/// Reuses AnimationParameterSpec/AnimationStateSpec/AnimationTransitionSpec/AnimationTransitionRefSpec
/// (EditorAnimationTools.cs) unchanged for the nested parameter/state/transition shapes - the SAME
/// specs animation_create_controller/animation_edit_controller already accept, not a second,
/// divergent copy. 'controllerPath' is deliberately the ONE field name for "which AnimatorController"
/// across all FOUR ops - see AnimationApplyTool's own class doc comment for why.</summary>
public sealed record AnimationApplyOperation : IBatchOperation
{
    [JsonPropertyName("op")] public required string Op { get; init; }

    // assignController
    [JsonPropertyName("gameObjectPath")] [OpField("assignController")] public string? GameObjectPath { get; init; }

    // assignController / assignClip / createController / editController
    [JsonPropertyName("controllerPath")] [OpField("assignController", "assignClip", "createController", "editController")] public string? ControllerPath { get; init; }

    // assignClip
    [JsonPropertyName("stateName")] [OpField("assignClip")] public string? StateName { get; init; }
    [JsonPropertyName("clipPath")] [OpField("assignClip")] public string? ClipPath { get; init; }

    // createController
    [JsonPropertyName("parameters")] [OpField("createController")] public IReadOnlyList<AnimationParameterSpec>? Parameters { get; init; }
    [JsonPropertyName("states")] [OpField("createController")] public IReadOnlyList<AnimationStateSpec>? States { get; init; }
    [JsonPropertyName("transitions")] [OpField("createController")] public IReadOnlyList<AnimationTransitionSpec>? Transitions { get; init; }

    // editController
    [JsonPropertyName("addParameters")] [OpField("editController")] public IReadOnlyList<AnimationParameterSpec>? AddParameters { get; init; }
    [JsonPropertyName("removeParameters")] [OpField("editController")] public IReadOnlyList<string>? RemoveParameters { get; init; }
    [JsonPropertyName("addStates")] [OpField("editController")] public IReadOnlyList<AnimationStateSpec>? AddStates { get; init; }
    [JsonPropertyName("removeStates")] [OpField("editController")] public IReadOnlyList<string>? RemoveStates { get; init; }
    [JsonPropertyName("addTransitions")] [OpField("editController")] public IReadOnlyList<AnimationTransitionSpec>? AddTransitions { get; init; }
    [JsonPropertyName("removeTransitions")] [OpField("editController")] public IReadOnlyList<AnimationTransitionRefSpec>? RemoveTransitions { get; init; }

    // Backing store for [JsonExtensionData] - see OperationFieldValidator's own doc comment for why
    // an unrecognised field must be captured, not silently dropped, to be catchable at all.
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>One successful operation's own result, echoed verbatim - see MaterialApplyOpResult's
/// own doc comment (MaterialApplyTool.cs) for why this is a loosely-typed passthrough rather than a
/// dozen-mostly-null-field record. In particular this is how createController/editController's own
/// 'errors' (a per-entry failure inside an otherwise-successful op - e.g. one bad transition
/// condition among several valid ones) and 'added'/'removed' lists survive into animation_apply's
/// response.</summary>
public sealed record AnimationApplyOpResult
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
}

public sealed record AnimationApplyFailure
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("error")] public required string Error { get; init; }
}

public sealed record AnimationApplyResult
{
    [JsonPropertyName("applied")] public required IReadOnlyList<int> Applied { get; init; }
    [JsonPropertyName("results")] public required IReadOnlyList<AnimationApplyOpResult> Results { get; init; }
    [JsonPropertyName("failed")] public required IReadOnlyList<AnimationApplyFailure> Failed { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
}

/// <summary>
/// The declarative batch that replaces EditorAnimationTools' animation_assign_controller/
/// animation_assign_clip/animation_create_controller/animation_edit_controller - 4 tools - with the
/// SAME shape Plan 10 Task 1's <see cref="SceneApplyTool"/> established: one MCP call sends the
/// WHOLE 'operations' array in ONE <c>animation.apply</c> wire call to
/// <see cref="Hades.Tools.AnimationApplyCommands"/> (Plugin~), which applies every operation
/// directly inside one handler body, one Undo group - see that class's own doc comment. Ordering,
/// partial-failure reporting, per-op result data in 'results', and the "unknown op refused before
/// any wire call" rule are all identical to scene_apply/material_apply - see SceneApplyTool's own
/// class doc comment for the shared rationale.
///
/// <para><b>'controllerPath' replaces two different underlying names.</b> The four single-purpose
/// tools this replaces spell "which AnimatorController" two different ways:
/// animation_assign_controller/animation_assign_clip call it 'controllerPath', animation_create_
/// controller/animation_edit_controller call it 'path' - a pre-existing inconsistency in the surface
/// this consolidation replaces, not something introduced here. animation_apply uses 'controllerPath'
/// for ALL FOUR ops instead: one caller-facing name for one concept, rather than requiring an agent
/// to remember which of two names a given op expects. The app sends 'controllerPath' verbatim onto
/// the wire for every op (a straight field copy, no renaming on this side - see SceneApplyTool's own
/// doc comment for why translation belongs on the OTHER side of the wire); <see cref="Hades.Tools.AnimationApplyCommands"/>
/// (Plugin~) is what renames it back to 'path' before calling animation.create_controller/
/// animation.edit_controller's own existing handlers.</para>
/// </summary>
[McpServerToolType]
public sealed class AnimationApplyTool(EditorProxy editor)
{
    static readonly string[] ValidOps = ["assignController", "assignClip", "createController", "editController"];

    [McpServerTool(Name = "animation_apply", Title = "Apply Animation Operations (Batch)", ReadOnly = false, UseStructuredContent = true)]
    [Description("Applies a batch of animation operations - assignController (gameObjectPath, "
               + "controllerPath), assignClip (controllerPath, stateName, clipPath), "
               + "createController (controllerPath, parameters?, states?, transitions?), "
               + "editController (controllerPath, addParameters?/removeParameters?/addStates?/"
               + "removeStates?/addTransitions?/removeTransitions?, at least one required) - in ONE "
               + "call, in the order given. This is the batch form of animation_assign_controller/"
               + "animation_assign_clip/animation_create_controller/animation_edit_controller. "
               + "'controllerPath' is the ONE field name for which AnimatorController every op "
               + "targets - always a file on disk. 'gameObjectPath' (assignController only) is a "
               + "scene object instead, never a file on disk - a different concept from "
               + "'controllerPath' entirely. "
               + "UNDO: the whole batch is ONE Unity Undo group - a single Ctrl/Cmd+Z reverts every "
               + "operation in the spec, not just the last one. "
               + "PARTIAL FAILURE, NOT ROLLED BACK: each operation's outcome is reported by its "
               + "0-based index in 'applied' (succeeded) or 'failed' (with its own error). Every "
               + "successful operation's own result (createController/editController's own "
               + "'errors'/'added'/'removed' lists for entries that individually failed inside an "
               + "otherwise-successful op, assignController's 'addedAnimator', ...) is reported in "
               + "'results' alongside 'applied'. "
               + "An unrecognised 'op' value rejects the WHOLE call before anything is sent to the "
               + "Editor, listing the valid ops. Needs a live Editor - call hades_charon_status first "
               + "if unsure.")]
    public async Task<AnimationApplyResult> AnimationApply(
        [Description("Operations to apply, in order. Each needs 'op' plus that op's own fields: "
                   + "assignController{gameObjectPath,controllerPath}, "
                   + "assignClip{controllerPath,stateName,clipPath}, "
                   + "createController{controllerPath,parameters?,states?,transitions?}, "
                   + "editController{controllerPath,addParameters?,removeParameters?,addStates?,"
                   + "removeStates?,addTransitions?,removeTransitions?}. 'controllerPath' always "
                   + "names the AnimatorController the op is about (a file on disk); "
                   + "assignController's 'gameObjectPath' always names a scene object, never a "
                   + "file on disk.")]
        IReadOnlyList<AnimationApplyOperation> operations,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        if (operations is null || operations.Count == 0)
            throw new McpException("animation_apply needs a non-empty 'operations' array.");

        // Refused, not ignored - see SceneApplyTool's own doc comment. Nothing is sent to the
        // Editor until every operation's 'op' is confirmed valid.
        for (var i = 0; i < operations.Count; i++)
        {
            if (Array.IndexOf(ValidOps, operations[i].Op) < 0)
            {
                throw new McpException(
                    $"animation_apply operations[{i}]: unknown op '{operations[i].Op}'. Valid ops: {string.Join(", ", ValidOps)}.");
            }
        }

        // Refused, not ignored - see OperationFieldValidator's own doc comment. An unrecognised
        // FIELD name on an otherwise-valid op is the same class of caller mistake as an unrecognised
        // op value above, and gets the identical whole-call, zero-wire-calls treatment.
        OperationFieldValidator.RejectUnknownFields("animation_apply", operations);

        var wireOperations = WireJson.NewArray();
        foreach (var op in operations) wireOperations.Add(BuildOperation(op));

        var @params = WireJson.NewObject().SetProperty("operations", wireOperations);
        var result = await editor.SendCommandAsync(project, "animation.apply", @params).ConfigureAwait(false);

        return MapResult(result, operations.Count);
    }

    // ---------------------------------------------------------------- app op -> wire op (field-for-field, no renaming)

    static WireJson BuildOperation(AnimationApplyOperation op)
    {
        var o = WireJson.NewObject().SetProperty("op", WireJson.String(op.Op));

        if (!string.IsNullOrEmpty(op.GameObjectPath)) o.SetProperty("gameObjectPath", WireJson.String(op.GameObjectPath));
        if (!string.IsNullOrEmpty(op.ControllerPath)) o.SetProperty("controllerPath", WireJson.String(op.ControllerPath));
        if (!string.IsNullOrEmpty(op.StateName)) o.SetProperty("stateName", WireJson.String(op.StateName));
        if (!string.IsNullOrEmpty(op.ClipPath)) o.SetProperty("clipPath", WireJson.String(op.ClipPath));

        if (EditorAnimationTools.HasItems(op.Parameters))
        {
            var arr = WireJson.NewArray();
            foreach (var p in op.Parameters!) arr.Add(EditorAnimationTools.ToWireParameter(p));
            o.SetProperty("parameters", arr);
        }
        if (EditorAnimationTools.HasItems(op.States))
        {
            var arr = WireJson.NewArray();
            foreach (var s in op.States!) arr.Add(EditorAnimationTools.ToWireState(s));
            o.SetProperty("states", arr);
        }
        if (EditorAnimationTools.HasItems(op.Transitions))
        {
            var arr = WireJson.NewArray();
            foreach (var t in op.Transitions!) arr.Add(EditorAnimationTools.ToWireTransition(t));
            o.SetProperty("transitions", arr);
        }

        if (EditorAnimationTools.HasItems(op.AddParameters))
        {
            var arr = WireJson.NewArray();
            foreach (var p in op.AddParameters!) arr.Add(EditorAnimationTools.ToWireParameter(p));
            o.SetProperty("addParameters", arr);
        }
        if (EditorAnimationTools.HasItems(op.RemoveParameters))
        {
            var arr = WireJson.NewArray();
            foreach (var name in op.RemoveParameters!) arr.Add(WireJson.String(name));
            o.SetProperty("removeParameters", arr);
        }
        if (EditorAnimationTools.HasItems(op.AddStates))
        {
            var arr = WireJson.NewArray();
            foreach (var s in op.AddStates!) arr.Add(EditorAnimationTools.ToWireState(s));
            o.SetProperty("addStates", arr);
        }
        if (EditorAnimationTools.HasItems(op.RemoveStates))
        {
            var arr = WireJson.NewArray();
            foreach (var name in op.RemoveStates!) arr.Add(WireJson.String(name));
            o.SetProperty("removeStates", arr);
        }
        if (EditorAnimationTools.HasItems(op.AddTransitions))
        {
            var arr = WireJson.NewArray();
            foreach (var t in op.AddTransitions!) arr.Add(EditorAnimationTools.ToWireTransition(t));
            o.SetProperty("addTransitions", arr);
        }
        if (EditorAnimationTools.HasItems(op.RemoveTransitions))
        {
            var arr = WireJson.NewArray();
            foreach (var t in op.RemoveTransitions!)
                arr.Add(WireJson.NewObject().SetProperty("from", WireJson.String(t.From)).SetProperty("to", WireJson.String(t.To)));
            o.SetProperty("removeTransitions", arr);
        }

        return o;
    }

    // ---------------------------------------------------------------- wire result -> AnimationApplyResult

    static AnimationApplyResult MapResult(WireJson result, int operationCount)
    {
        var applied = new List<int>();
        if (result.TryGetProperty("applied", out var appliedJson) && appliedJson!.Kind == WireKind.Array)
            foreach (var item in appliedJson.Items) applied.Add((int)item.AsInteger());

        var results = new List<AnimationApplyOpResult>();
        if (result.TryGetProperty("results", out var resultsJson) && resultsJson!.Kind == WireKind.Array)
        {
            foreach (var item in resultsJson.Items)
            {
                results.Add(new AnimationApplyOpResult
                {
                    Index = (int)EditorComponentTools.Int(item, "index"),
                    Op = EditorComponentTools.Str(item, "op"),
                    Result = item.TryGetProperty("result", out var r) ? WireJsonBridge.ToClr(r!) : null,
                });
            }
        }

        var failed = new List<AnimationApplyFailure>();
        if (result.TryGetProperty("failed", out var failedJson) && failedJson!.Kind == WireKind.Array)
        {
            foreach (var item in failedJson.Items)
            {
                failed.Add(new AnimationApplyFailure
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

        return new AnimationApplyResult { Applied = applied, Results = results, Failed = failed, Summary = summary };
    }
}
