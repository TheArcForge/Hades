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

    // assignController / assignClip / createController / editController / addLayer / setBlendTree / describe
    [JsonPropertyName("controllerPath")]
    [OpField("assignController", "assignClip", "createController", "editController", "addLayer", "setBlendTree", "describe")]
    public string? ControllerPath { get; init; }

    // assignClip / editController / setBlendTree: which layer (by name) the op targets - omitted
    // means layer 0, exactly today's hardcoded behaviour (DESIGN-05 §7).
    [JsonPropertyName("layer")] [OpField("assignClip", "editController", "setBlendTree")] public string? Layer { get; init; }

    // assignClip / setBlendTree
    [JsonPropertyName("stateName")] [OpField("assignClip", "setBlendTree")] public string? StateName { get; init; }

    // assignClip / setEvents / createClip / setCurve / describe
    [JsonPropertyName("clipPath")] [OpField("assignClip", "setEvents", "createClip", "setCurve", "describe")] public string? ClipPath { get; init; }

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

    // addLayer
    [JsonPropertyName("name")] [OpField("addLayer")] public string? Name { get; init; }
    [JsonPropertyName("weight")] [OpField("addLayer")] public double? Weight { get; init; }
    [JsonPropertyName("blending")] [OpField("addLayer")] public string? Blending { get; init; }
    [JsonPropertyName("avatarMask")] [OpField("addLayer")] public string? AvatarMask { get; init; }
    [JsonPropertyName("ikPass")] [OpField("addLayer")] public bool? IkPass { get; init; }

    // setBlendTree - JsonElement, not a recursive record: see AnimationCommands.SetBlendTree's own
    // doc comment (plugin side) and DESIGN-05 §4.5 for why (the SceneApplyOperation.Position
    // precedent, plus a self-referential record being exactly the shape most likely to trip the
    // MCP SDK's schema generation/client-side validation). Validated, recursively and pre-flight,
    // entirely plugin-side.
    [JsonPropertyName("tree")] [OpField("setBlendTree")] public JsonElement? Tree { get; init; }

    // createClip
    [JsonPropertyName("frameRate")] [OpField("createClip")] public double? FrameRate { get; init; }
    [JsonPropertyName("loop")] [OpField("createClip")] public bool? Loop { get; init; }
    [JsonPropertyName("defaultEasing")] [OpField("createClip")] public string? DefaultEasing { get; init; }

    // setCurve
    [JsonPropertyName("path")] [OpField("setCurve")] public string? Path { get; init; }
    [JsonPropertyName("type")] [OpField("setCurve")] public string? Type { get; init; }
    [JsonPropertyName("property")] [OpField("setCurve")] public string? Property { get; init; }
    [JsonPropertyName("keys")] [OpField("setCurve")] public IReadOnlyList<AnimationCurveKeySpec>? Keys { get; init; }

    // setEvents
    [JsonPropertyName("events")] [OpField("setEvents")] public IReadOnlyList<AnimationEventSpec>? Events { get; init; }
    [JsonPropertyName("receiver")] [OpField("setEvents")] public string? Receiver { get; init; }

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
/// <see cref="Hades.Tools.AnimationApplyCommands"/> (UnityPlugin), which applies every operation
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
/// (UnityPlugin) is what renames it back to 'path' before calling animation.create_controller/
/// animation.edit_controller's own existing handlers.</para>
/// </summary>
[McpServerToolType]
public sealed class AnimationApplyTool(EditorProxy editor, ProjectService projects)
{
    static readonly string[] ValidOps =
    [
        "assignController", "assignClip", "createController", "editController",
        "addLayer", "setBlendTree", "createClip", "setCurve", "setEvents", "describe",
    ];

    /// <summary>DESIGN-05 §4.6. Real trees are 1-2 levels deep; this exists purely as a depth-bomb
    /// guard against a recursive, caller-supplied structure.</summary>
    const int MaxBlendTreeDepth = 4;

    [McpServerTool(Name = "animation_apply", Title = "Apply Animation Operations (Batch)", ReadOnly = false, UseStructuredContent = true)]
    [Description("Applies a batch of animation operations - assignController (gameObjectPath, "
               + "controllerPath), assignClip (controllerPath, layer?, stateName, clipPath), "
               + "createController (controllerPath, parameters?, states?, transitions?), "
               + "editController (controllerPath, layer?, addParameters?/removeParameters?/"
               + "addStates?/removeStates?/addTransitions?/removeTransitions?, at least one "
               + "required), addLayer (controllerPath, name, weight?, blending?, avatarMask?, "
               + "ikPass?), setBlendTree (controllerPath, layer?, stateName, tree - REPLACES the "
               + "state's whole motion; validated whole-tree before anything is touched, so a bad "
               + "tree leaves the controller exactly as it was), createClip (clipPath, frameRate?, "
               + "loop?, defaultEasing?), setCurve (clipPath, path, type, property, keys - REPLACES "
               + "that one EditorCurveBinding's curve wholesale), setEvents (clipPath, events, "
               + "receiver? - REPLACES all of the clip's events), describe (controllerPath OR "
               + "clipPath, exactly one - a READ, returns the controller's layers/parameters/states "
               + "incl. blend tree contents, or the clip's curves/events, in the same shape "
               + "setBlendTree/setCurve/setEvents accept - read, mutate the JSON, send back) - in "
               + "ONE call, in the order given. This is the batch form of "
               + "animation_assign_controller/animation_assign_clip/animation_create_controller/"
               + "animation_edit_controller plus six new ops. "
               + "'controllerPath' is the ONE field name for which AnimatorController every "
               + "controller-targeting op uses - always a file on disk; 'clipPath' is the same for "
               + "which AnimationClip. 'gameObjectPath' (assignController only) is a scene object "
               + "instead, never a file on disk. 'layer' (assignClip/editController/setBlendTree) "
               + "names a layer by NAME, not index - omitted means layer 0 (today's behaviour, "
               + "unchanged). "
               + "IMPORTED CLIPS: setCurve/setEvents work only on an authored .anim clip's own "
               + "asset - an FBX-embedded clip's curves/events are configured through "
               + "project_settings_apply setClipImportSettings instead (a different Unity "
               + "mechanism entirely); this tool detects an imported clip and names that tool in "
               + "its error. "
               + "UNDO: the whole batch is ONE Unity Undo group - a single Ctrl/Cmd+Z reverts every "
               + "operation in the spec, not just the last one; setBlendTree's created/replaced "
               + "sub-assets are ordinary, complete undo entries, no exceptions. "
               + "PARTIAL FAILURE, NOT ROLLED BACK: each operation's outcome is reported by its "
               + "0-based index in 'applied' (succeeded) or 'failed' (with its own error). Every "
               + "successful operation's own result (createController/editController's own "
               + "'errors'/'added'/'removed' lists for entries that individually failed inside an "
               + "otherwise-successful op, assignController's 'addedAnimator', ...) is reported in "
               + "'results' alongside 'applied'. "
               + "An unrecognised 'op' value rejects the WHOLE call before anything is sent to the "
               + "Editor, listing the valid ops. 'describe' is refused for the WHOLE batch while "
               + "Play Mode is active (like every other op here) - it is a read, but this tool has "
               + "no per-op play-mode granularity yet. Needs a live Editor - call hades_charon_status "
               + "first if unsure.")]
    public async Task<AnimationApplyResult> AnimationApply(
        [Description("Operations to apply, in order. Each needs 'op' plus that op's own fields: "
                   + "assignController{gameObjectPath,controllerPath}, "
                   + "assignClip{controllerPath,layer?,stateName,clipPath}, "
                   + "createController{controllerPath,parameters?,states?,transitions?}, "
                   + "editController{controllerPath,layer?,addParameters?,removeParameters?,addStates?,"
                   + "removeStates?,addTransitions?,removeTransitions?}, "
                   + "addLayer{controllerPath,name,weight?,blending?,avatarMask?,ikPass?}, "
                   + "setBlendTree{controllerPath,layer?,stateName,tree}, "
                   + "createClip{clipPath,frameRate?,loop?,defaultEasing?}, "
                   + "setCurve{clipPath,path,type,property,keys}, "
                   + "setEvents{clipPath,events,receiver?}, "
                   + "describe{controllerPath} OR describe{clipPath} (exactly one). "
                   + "'controllerPath'/'clipPath' always name a file on disk; "
                   + "assignController's 'gameObjectPath' always names a scene object, never a "
                   + "file on disk.")]
        IReadOnlyList<AnimationApplyOperation> operations,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
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

        // 'describe' names exactly one of controllerPath/clipPath - an XOR neither [OpField] (which
        // only says a field belongs to this op, not that it is exclusive-or with another) nor the
        // plugin's own JsonParams.RequireString (which cannot see a SECOND field at all) can
        // express, so it is checked here, before anything is sent to the Editor - same "refused,
        // not ignored" treatment as every other structural check above.
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i].Op != "describe") continue;
            var hasController = !string.IsNullOrEmpty(operations[i].ControllerPath);
            var hasClip = !string.IsNullOrEmpty(operations[i].ClipPath);
            if (hasController == hasClip)
            {
                throw new McpException(
                    $"animation_apply operations[{i}] (op 'describe'): needs exactly one of 'controllerPath' "
                    + "or 'clipPath', not " + (hasController ? "both" : "neither") + ".");
            }
        }

        // setBlendTree's 'tree' is a recursive, caller-supplied structure - a plausible depth bomb
        // from an LLM caller, and neither side of the wire otherwise bounds its own recursion
        // (DESIGN-05 §4.6). Rejected HERE, app-side, before a single byte reaches the Editor - the
        // plugin's own pre-flight walk (AnimationCommands.ValidateBlendTreeNode) enforces the same
        // cap independently, but that is a second, defence-in-depth check, not a substitute for
        // refusing before the wire at all.
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i].Tree is not { } tree) continue;
            var depth = BlendTreeDepth(tree);
            if (depth > MaxBlendTreeDepth)
            {
                throw new McpException(
                    $"animation_apply operations[{i}] (op 'setBlendTree'): 'tree' is nested {depth} levels deep - "
                    + $"the cap is {MaxBlendTreeDepth}. Real blend trees are 1-2 levels deep.");
            }
        }

        var wireOperations = WireJson.NewArray();
        foreach (var op in operations) wireOperations.Add(BuildOperation(op));

        var @params = WireJson.NewObject().SetProperty("operations", wireOperations);
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var result = await editor.SendCommandAsync(productGuid, "animation.apply", @params).ConfigureAwait(false);

        return MapResult(result, operations.Count);
    }

    // ---------------------------------------------------------------- app op -> wire op (field-for-field, no renaming)

    /// <summary>How many levels a 'tree' value nests, counting the root as depth 1 - the same
    /// counting convention <c>AnimationCommands.ValidateBlendTreeNode</c> (plugin-side) uses, so
    /// the two independent guards agree on what "depth 4" means. Only descends through
    /// children[].tree - the one recursive edge this shape has; any other malformed structure is
    /// the plugin's own pre-flight validation's problem to report, not this cheap app-side scan's.</summary>
    static int BlendTreeDepth(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return 1;
        if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return 1;

        var maxChildDepth = 0;
        foreach (var child in children.EnumerateArray())
        {
            if (child.ValueKind == JsonValueKind.Object && child.TryGetProperty("tree", out var nested) && nested.ValueKind == JsonValueKind.Object)
                maxChildDepth = Math.Max(maxChildDepth, BlendTreeDepth(nested));
        }
        return 1 + maxChildDepth;
    }

    static WireJson BuildOperation(AnimationApplyOperation op)
    {
        var o = WireJson.NewObject().SetProperty("op", WireJson.String(op.Op));

        if (!string.IsNullOrEmpty(op.GameObjectPath)) o.SetProperty("gameObjectPath", WireJson.String(op.GameObjectPath));
        if (!string.IsNullOrEmpty(op.ControllerPath)) o.SetProperty("controllerPath", WireJson.String(op.ControllerPath));
        if (!string.IsNullOrEmpty(op.Layer)) o.SetProperty("layer", WireJson.String(op.Layer));
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

        // addLayer
        if (!string.IsNullOrEmpty(op.Name)) o.SetProperty("name", WireJson.String(op.Name));
        if (op.Weight is { } weight) o.SetProperty("weight", WireJson.Float(weight));
        if (!string.IsNullOrEmpty(op.Blending)) o.SetProperty("blending", WireJson.String(op.Blending));
        if (!string.IsNullOrEmpty(op.AvatarMask)) o.SetProperty("avatarMask", WireJson.String(op.AvatarMask));
        if (op.IkPass is { } ikPass) o.SetProperty("ikPass", WireJson.Bool(ikPass));

        // setBlendTree
        if (op.Tree is { } tree) o.SetProperty("tree", WireJsonBridge.ToWire(tree));

        // createClip
        if (op.FrameRate is { } frameRate) o.SetProperty("frameRate", WireJson.Float(frameRate));
        if (op.Loop is { } loop) o.SetProperty("loop", WireJson.Bool(loop));
        if (!string.IsNullOrEmpty(op.DefaultEasing)) o.SetProperty("defaultEasing", WireJson.String(op.DefaultEasing));

        // setCurve
        if (op.Path is not null) o.SetProperty("path", WireJson.String(op.Path));
        if (!string.IsNullOrEmpty(op.Type)) o.SetProperty("type", WireJson.String(op.Type));
        if (!string.IsNullOrEmpty(op.Property)) o.SetProperty("property", WireJson.String(op.Property));
        if (EditorAnimationTools.HasItems(op.Keys))
        {
            var arr = WireJson.NewArray();
            foreach (var k in op.Keys!) arr.Add(EditorAnimationTools.ToWireCurveKey(k));
            o.SetProperty("keys", arr);
        }

        // setEvents - 'events' is REPLACE-ALL (DESIGN-05 §5.5): an explicit empty array means
        // "clear every event on this clip" and must reach the wire as [], not be omitted the way
        // an absent/empty OPTIONAL list (removeParameters etc, above) correctly is.
        if (op.Events is { } events)
        {
            var arr = WireJson.NewArray();
            foreach (var e in events) arr.Add(EditorAnimationTools.ToWireEvent(e));
            o.SetProperty("events", arr);
        }
        if (!string.IsNullOrEmpty(op.Receiver)) o.SetProperty("receiver", WireJson.String(op.Receiver));

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
