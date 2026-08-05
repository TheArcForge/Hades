using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hades.Core.Editors;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WireJson = Hades.Contract.Wire.JsonValue;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Mcp;

/// <summary>One entry of scene_apply's 'operations' array. All fields but <see cref="Op"/> are
/// optional because which ones apply depends on 'op' - a flat, single record (rather than eleven
/// separate per-op types plus a discriminated-union JSON converter) is the minimum shape that
/// still lets System.Text.Json bind this the same declarative way every other batch spec in this
/// codebase already does (SceneObjectSpec, PropertyOperationSpec) - see SceneApplyTool's own class
/// doc comment for the full op-to-field map.</summary>
public sealed record SceneApplyOperation : IBatchOperation
{
    [JsonPropertyName("op")] public required string Op { get; init; }

    // create
    [JsonPropertyName("name")] [OpField("create")] public string? Name { get; init; }
    [JsonPropertyName("parent")] [OpField("create")] public string? Parent { get; init; }
    [JsonPropertyName("primitive")] [OpField("create")] public string? Primitive { get; init; }
    [JsonPropertyName("tag")] [OpField("create")] public string? Tag { get; init; }
    [JsonPropertyName("layer")] [OpField("create")] public string? Layer { get; init; }
    [JsonPropertyName("position")] [OpField("create")] public float[]? Position { get; init; }
    [JsonPropertyName("rotation")] [OpField("create")] public float[]? Rotation { get; init; }
    [JsonPropertyName("scale")] [OpField("create")] public float[]? Scale { get; init; }

    // addComponent / removeComponent / setProperties / setReference / addListener / removeListener / delete / reparent / rename / select -
    // every op but create, per SceneApplyTool's own [Description] parameter text (the authoritative
    // per-op field list - NOT the "//" comment grouping above, which is looser than the real per-op
    // union: 'type' sits in this same comment block but is addComponent/removeComponent-only).
    [JsonPropertyName("target")]
    [OpField("addComponent", "removeComponent", "setProperties", "setReference", "addListener", "removeListener", "delete", "reparent", "rename", "select")]
    public string? Target { get; init; }
    [JsonPropertyName("type")] [OpField("addComponent", "removeComponent")] public string? Type { get; init; }
    [JsonPropertyName("component")] [OpField("setProperties", "setReference", "addListener", "removeListener")] public string? Component { get; init; }

    // setProperties
    [JsonPropertyName("values")] [OpField("setProperties")] public IReadOnlyDictionary<string, JsonElement>? Values { get; init; }

    // setReference
    [JsonPropertyName("property")] [OpField("setReference")] public string? Property { get; init; }
    [JsonPropertyName("value")] [OpField("setReference")] public string? Value { get; init; }
    [JsonPropertyName("targetPath")] [OpField("setReference")] public string? TargetPath { get; init; }
    [JsonPropertyName("targetComponentType")] [OpField("setReference")] public string? TargetComponentType { get; init; }

    // addListener / removeListener
    [JsonPropertyName("event")] [OpField("addListener", "removeListener")] public string? Event { get; init; }
    [JsonPropertyName("targetObject")] [OpField("addListener")] public string? TargetObject { get; init; }
    [JsonPropertyName("method")] [OpField("addListener")] public string? Method { get; init; }
    [JsonPropertyName("argument")] [OpField("addListener")] public string? Argument { get; init; }
    [JsonPropertyName("argumentType")] [OpField("addListener")] public string? ArgumentType { get; init; }
    [JsonPropertyName("index")] [OpField("removeListener")] public int? Index { get; init; }

    // reparent / rename
    [JsonPropertyName("newParent")] [OpField("reparent")] public string? NewParent { get; init; }
    [JsonPropertyName("newName")] [OpField("rename")] public string? NewName { get; init; }

    // Backing store for [JsonExtensionData] - see OperationFieldValidator's own doc comment for why
    // an unrecognised field must be captured, not silently dropped, to be catchable at all.
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record SceneApplyFailure
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("error")] public required string Error { get; init; }
}

public sealed record SceneApplyResult
{
    [JsonPropertyName("applied")] public required IReadOnlyList<int> Applied { get; init; }
    [JsonPropertyName("failed")] public required IReadOnlyList<SceneApplyFailure> Failed { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
}

/// <summary>
/// The declarative batch that replaces EditorSceneTools' scene_create_gameobject/scene_create_
/// primitive/scene_delete_gameobject/scene_reparent_gameobject/scene_rename_gameobject/scene_setup
/// and EditorComponentTools' component_add/component_remove/component_set_property/component_set_
/// properties/reference_set/event_add_listener/event_remove_listener - 13 tools - plus inspector_
/// select's "select" op (part of this tool's required op vocabulary though not one of the 13 the
/// consolidation map's own scene_apply row names). One MCP call takes an ordered list of {op, ...}
/// operations and sends the WHOLE array in ONE <c>scene.apply</c> wire call to
/// <see cref="Hades.Tools.SceneApplyCommands"/> (Plugin~) - see that class's own doc comment for how
/// it applies every operation directly, inside one handler body, one Undo group.
///
/// <para><b>Ordering.</b> Operations apply strictly in the order given - the plugin awaits nothing
/// (every op runs synchronously on Unity's main thread inside the one dispatch) - so a later
/// operation can target a GameObject an earlier one in the SAME call just created, renamed, or
/// reparented, exactly as if the equivalent individual tool calls had been made in sequence.</para>
///
/// <para><b>Partial failure, never rolled back.</b> Each operation's outcome is recorded by its
/// index in the 'operations' array: a succeeding index lands in <see cref="SceneApplyResult.Applied"/>,
/// a failing one in <see cref="SceneApplyResult.Failed"/> with its own error - and the plugin keeps
/// going regardless (mirroring component_set_properties/scene_setup's own established shape).
/// Operations that already succeeded are NOT undone when a later one fails: a partial result means
/// exactly that, a partially-applied scene, never an all-or-nothing transaction. Say this plainly
/// in the tool description below, because a caller who assumes atomicity on failure will misread a
/// partial result as either total success or total failure.</para>
///
/// <para><b>Unknown op: refused, not ignored.</b> Every operation's 'op' is validated against
/// <see cref="ValidOps"/> BEFORE anything is sent to the Editor - one bad entry anywhere in the
/// array rejects the whole call with nothing applied and no wire round trip at all, the same
/// "refused, not ignored" convention CommandTable.RejectUnknownParams (Plugin~) established for an
/// unrecognised lease.* parameter (a typo'd 'ttlMs' silently taking the 30s default cost a real
/// misdiagnosis). This is deliberately different from a per-operation FIELD problem (a known op
/// missing a required field, e.g. 'create' with no 'name') - that is a per-operation runtime
/// failure the PLUGIN reports (matching component_set_properties' own nested-operation precedent: a
/// blank 'gameObject' is that ONE operation's failure, not a whole-call rejection). An unrecognised
/// 'op' VALUE is a schema problem with the request itself, exactly like an unrecognised parameter
/// NAME - so it gets the schema-level, whole-call, zero-wire-calls treatment instead, entirely on
/// this side of the wire.</para>
///
/// <para><b>One call, one Undo group.</b> Every operation maps to the SAME field-for-field shape
/// <see cref="Hades.Tools.SceneApplyCommands"/> accepts (see BuildOperation below) - this tool does
/// no per-op routing or validation of its own beyond the whole-call 'op' check above; the plugin
/// owns field validation, dispatch, and Undo. That single plugin-side handler body increments
/// Unity's Undo group exactly once for the WHOLE spec (SceneApplyCommandsTests.
/// Apply_RegistersUndoAsOneGroup_PerformUndoRevertsEveryOperation, verified against a real Editor) -
/// a single Ctrl/Cmd+Z reverts every operation in the spec, the entire point of batching. This
/// replaces an earlier version of this tool that sent one wire call PER operation (N operations, N
/// Undo groups) - see Plan 10 Task 1's report for why that shape was rejected and this one built
/// instead.</para>
/// </summary>
[McpServerToolType]
public sealed class SceneApplyTool(EditorProxy editor)
{
    /// <summary>Every 'op' this tool accepts - also what an unknown op's rejection message lists,
    /// so a caller can self-correct without guessing. Must stay in sync with SceneApplyCommands.
    /// ValidOps (Plugin~) - the two lists exist independently (this one runs before any wire call;
    /// the plugin's own is a defensive fallback for a non-app caller), but a real capability the app
    /// accepts must be one the plugin can also dispatch.</summary>
    static readonly string[] ValidOps =
    [
        "create", "addComponent", "setProperties", "setReference", "removeComponent",
        "addListener", "removeListener", "delete", "reparent", "rename", "select",
    ];

    [McpServerTool(Name = "scene_apply", Title = "Apply Scene Operations (Batch)", ReadOnly = false, UseStructuredContent = true)]
    [Description("Applies a batch of scene/component/wiring operations - create (optionally a "
               + "primitive, and/or with tag/layer/position/rotation/scale), addComponent, "
               + "setProperties, setReference, removeComponent, addListener, removeListener, "
               + "delete, reparent, rename, select - in ONE call, in the order given, so a later "
               + "operation can act on a GameObject an earlier one in this SAME call just created, "
               + "renamed, or reparented. This is the batch form of scene_create_gameobject/"
               + "scene_create_primitive/scene_delete_gameobject/scene_reparent_gameobject/"
               + "scene_rename_gameobject/scene_setup/component_add/component_remove/component_"
               + "set_property/component_set_properties/reference_set/event_add_listener/event_"
               + "remove_listener/inspector_select. "
               + "UNDO: the whole batch is ONE Unity Undo group - a single Ctrl/Cmd+Z reverts every "
               + "operation in the spec, not just the last one. "
               + "PARTIAL FAILURE, NOT ROLLED BACK: each operation's outcome is reported by its "
               + "0-based index in 'applied' (succeeded) or 'failed' (with its own error) - "
               + "operations that already succeeded are never undone because a LATER one failed, so "
               + "a partial result is a partially-applied scene, not an all-or-nothing transaction. "
               + "An unrecognised 'op' value rejects the WHOLE call before anything is sent to the "
               + "Editor, listing the valid ops. Needs a live Editor - call hades_charon_status "
               + "first if unsure.")]
    public async Task<SceneApplyResult> SceneApply(
        [Description("Operations to apply, in order. Each needs 'op' plus that op's own fields: "
                   + "create{name,parent?,primitive?,tag?,layer?,position?,rotation?,scale?}, "
                   + "addComponent/removeComponent{target,type}, "
                   + "setProperties{target,component,values:{name:value}}, "
                   + "setReference{target,component,property,value? (asset path) XOR targetPath? "
                   + "(scene GameObject),targetComponentType?}, "
                   + "addListener{target,component,event,targetObject,method,argument?,argumentType?}, "
                   + "removeListener{target,component,event,index}, "
                   + "delete{target}, reparent{target,newParent?}, rename{target,newName}, "
                   + "select{target}.")]
        IReadOnlyList<SceneApplyOperation> operations,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        if (operations is null || operations.Count == 0)
            throw new McpException("scene_apply needs a non-empty 'operations' array.");

        // Refused, not ignored - see this class's own doc comment. Nothing is sent to the Editor
        // until every operation's 'op' is confirmed valid.
        for (var i = 0; i < operations.Count; i++)
        {
            if (Array.IndexOf(ValidOps, operations[i].Op) < 0)
            {
                throw new McpException(
                    $"scene_apply operations[{i}]: unknown op '{operations[i].Op}'. Valid ops: {string.Join(", ", ValidOps)}.");
            }
        }

        // Refused, not ignored - see OperationFieldValidator's own doc comment. An unrecognised
        // FIELD name on an otherwise-valid op is the same class of caller mistake as an unrecognised
        // op value above, and gets the identical whole-call, zero-wire-calls treatment.
        OperationFieldValidator.RejectUnknownFields("scene_apply", operations);

        var wireOperations = WireJson.NewArray();
        foreach (var op in operations) wireOperations.Add(BuildOperation(op));

        var @params = WireJson.NewObject().SetProperty("operations", wireOperations);
        var result = await editor.SendCommandAsync(project, "scene.apply", @params).ConfigureAwait(false);

        return MapResult(result, operations.Count);
    }

    // ---------------------------------------------------------------- app op -> wire op (field-for-field, no renaming)

    /// <summary>Every field carries the EXACT name <see cref="SceneApplyOperation"/> exposes onto
    /// the wire - see <see cref="Hades.Tools.SceneApplyCommands"/> (Plugin~), which accepts this
    /// same vocabulary directly, field for field. Absent/null fields are simply omitted (a JSON
    /// object with only the keys THIS operation actually set) rather than sent as explicit nulls -
    /// the plugin's JsonParams helpers already treat "absent" and "present but null" as equivalent
    /// for every optional field, so there is nothing to lose by omitting.</summary>
    static WireJson BuildOperation(SceneApplyOperation op)
    {
        var o = WireJson.NewObject().SetProperty("op", WireJson.String(op.Op));

        if (!string.IsNullOrEmpty(op.Name)) o.SetProperty("name", WireJson.String(op.Name));
        if (!string.IsNullOrEmpty(op.Parent)) o.SetProperty("parent", WireJson.String(op.Parent));
        if (!string.IsNullOrEmpty(op.Primitive)) o.SetProperty("primitive", WireJson.String(op.Primitive));
        if (!string.IsNullOrEmpty(op.Tag)) o.SetProperty("tag", WireJson.String(op.Tag));
        if (!string.IsNullOrEmpty(op.Layer)) o.SetProperty("layer", WireJson.String(op.Layer));
        if (op.Position is not null) o.SetProperty("position", FloatArray(op.Position));
        if (op.Rotation is not null) o.SetProperty("rotation", FloatArray(op.Rotation));
        if (op.Scale is not null) o.SetProperty("scale", FloatArray(op.Scale));

        if (!string.IsNullOrEmpty(op.Target)) o.SetProperty("target", WireJson.String(op.Target));
        if (!string.IsNullOrEmpty(op.Type)) o.SetProperty("type", WireJson.String(op.Type));
        if (!string.IsNullOrEmpty(op.Component)) o.SetProperty("component", WireJson.String(op.Component));

        if (op.Values is { Count: > 0 })
        {
            var values = WireJson.NewObject();
            foreach (var (key, value) in op.Values) values.SetProperty(key, WireJsonBridge.ToWire(value));
            o.SetProperty("values", values);
        }

        if (!string.IsNullOrEmpty(op.Property)) o.SetProperty("property", WireJson.String(op.Property));
        if (!string.IsNullOrEmpty(op.Value)) o.SetProperty("value", WireJson.String(op.Value));
        if (!string.IsNullOrEmpty(op.TargetPath)) o.SetProperty("targetPath", WireJson.String(op.TargetPath));
        if (!string.IsNullOrEmpty(op.TargetComponentType)) o.SetProperty("targetComponentType", WireJson.String(op.TargetComponentType));

        if (!string.IsNullOrEmpty(op.Event)) o.SetProperty("event", WireJson.String(op.Event));
        if (!string.IsNullOrEmpty(op.TargetObject)) o.SetProperty("targetObject", WireJson.String(op.TargetObject));
        if (!string.IsNullOrEmpty(op.Method)) o.SetProperty("method", WireJson.String(op.Method));
        if (op.Argument is not null) o.SetProperty("argument", WireJson.String(op.Argument));
        if (!string.IsNullOrEmpty(op.ArgumentType)) o.SetProperty("argumentType", WireJson.String(op.ArgumentType));
        if (op.Index is not null) o.SetProperty("index", WireJson.Integer(op.Index.Value));

        if (!string.IsNullOrEmpty(op.NewParent)) o.SetProperty("newParent", WireJson.String(op.NewParent));
        if (!string.IsNullOrEmpty(op.NewName)) o.SetProperty("newName", WireJson.String(op.NewName));

        return o;
    }

    static WireJson FloatArray(float[] xyz)
    {
        var array = WireJson.NewArray();
        foreach (var component in xyz) array.Add(WireJson.Float(component));
        return array;
    }

    // ---------------------------------------------------------------- wire result -> SceneApplyResult

    /// <summary>scene.apply's wire result is already {applied:[indices], failed:[{index,op,error}],
    /// summary} - the SAME shape <see cref="SceneApplyResult"/> exposes to the MCP caller - so this
    /// is a direct field-for-field read, not a transformation.</summary>
    static SceneApplyResult MapResult(WireJson result, int operationCount)
    {
        var applied = new List<int>();
        if (result.TryGetProperty("applied", out var appliedJson) && appliedJson!.Kind == WireKind.Array)
            foreach (var item in appliedJson.Items) applied.Add((int)item.AsInteger());

        var failed = new List<SceneApplyFailure>();
        if (result.TryGetProperty("failed", out var failedJson) && failedJson!.Kind == WireKind.Array)
        {
            foreach (var item in failedJson.Items)
            {
                failed.Add(new SceneApplyFailure
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

        return new SceneApplyResult { Applied = applied, Failed = failed, Summary = summary };
    }
}
