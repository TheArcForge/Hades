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

/// <summary>One entry of prefab_apply's 'operations' array - see SceneApplyOperation's own doc
/// comment (SceneApplyTool.cs) for why this is one flat record rather than a discriminated union.
/// 'prefabPath' is the ONE field name for "the prefab ASSET this op is about" across create (its
/// new file), instantiate (its existing source), and editProperty (its existing target) - see
/// PrefabApplyTool's own class doc comment for why this consolidates what used to be two different
/// names (create's 'assetPath', the other two ops' 'prefabPath') for the identical concept, and why
/// 'gameObjectPath' stays separate (a scene object, never a file on disk - genuinely different, and
/// reused across three different ops because it is the same KIND of value each time).</summary>
public sealed record PrefabApplyOperation : IBatchOperation
{
    [JsonPropertyName("op")] public required string Op { get; init; }

    // create (source scene object); applyOverrides (the instance); editProperty (optional nested
    // child inside the prefab, relative to its root) - always a SCENE OBJECT, never a file on disk.
    [JsonPropertyName("gameObjectPath")] [OpField("create", "applyOverrides", "editProperty")] public string? GameObjectPath { get; init; }

    // create (new file), instantiate (existing source), editProperty (existing target) - "the
    // prefab asset this op is about". One name for every op that has one; always a file on disk.
    [JsonPropertyName("prefabPath")] [OpField("create", "instantiate", "editProperty")] public string? PrefabPath { get; init; }

    // instantiate
    [JsonPropertyName("parent")] [OpField("instantiate")] public string? Parent { get; init; }

    // editProperty
    [JsonPropertyName("componentType")] [OpField("editProperty")] public string? ComponentType { get; init; }
    [JsonPropertyName("propertyName")] [OpField("editProperty")] public string? PropertyName { get; init; }
    [JsonPropertyName("value")] [OpField("editProperty")] public JsonElement? Value { get; init; }

    // createVariant - TWO different prefab assets in the SAME op (the base being varianted FROM,
    // and the new variant file being created), so neither collapses into 'prefabPath' above.
    [JsonPropertyName("basePrefabPath")] [OpField("createVariant")] public string? BasePrefabPath { get; init; }
    [JsonPropertyName("variantPath")] [OpField("createVariant")] public string? VariantPath { get; init; }

    // Backing store for [JsonExtensionData] - see OperationFieldValidator's own doc comment for why
    // an unrecognised field must be captured, not silently dropped, to be catchable at all.
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>One successful operation's own result, echoed verbatim - see MaterialApplyOpResult's
/// own doc comment (MaterialApplyTool.cs) for why this is a loosely-typed passthrough rather than a
/// dozen-mostly-null-field record. This is specifically how applyOverrides' 'unappliedProperties'/
/// 'note' survive into prefab_apply's response - see this file's own class doc comment.</summary>
public sealed record PrefabApplyOpResult
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
}

public sealed record PrefabApplyFailure
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("error")] public required string Error { get; init; }
}

public sealed record PrefabApplyResult
{
    [JsonPropertyName("applied")] public required IReadOnlyList<int> Applied { get; init; }
    [JsonPropertyName("results")] public required IReadOnlyList<PrefabApplyOpResult> Results { get; init; }
    [JsonPropertyName("failed")] public required IReadOnlyList<PrefabApplyFailure> Failed { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
}

/// <summary>
/// The declarative batch that replaces EditorPrefabTools' prefab_create/prefab_instantiate/
/// prefab_apply_overrides/prefab_edit_property/prefab_open_editing/prefab_save_editing/
/// prefab_create_variant - 7 tools - with the SAME shape Plan 10 Task 1's <see cref="SceneApplyTool"/>
/// established: one MCP call sends the WHOLE 'operations' array in ONE <c>prefab.apply</c> wire
/// call to <see cref="Hades.Tools.PrefabApplyCommands"/> (UnityPlugin), which applies every operation
/// directly inside one handler body - see that class's own doc comment for how it additionally
/// collapses the reload lease itself to ONE acquire/release for the whole batch, not one per
/// operation (prefab operations are class 2 - lease bounded - unlike scene/material/animation's
/// class 1, so this is a real extra property scene_apply/material_apply/animation_apply never had
/// to prove). Ordering, partial-failure reporting (<c>applied</c>/<c>failed</c> by index, never
/// rolled back), and the "unknown op refused before any wire call" rule are all identical to
/// scene_apply - see SceneApplyTool's own class doc comment for the shared rationale.
///
/// <para><b>No 'open'/'save' ops - only atomic ones.</b> prefab_open_editing/prefab_save_editing
/// are deliberately NOT part of this op vocabulary: collapsing open→edit→save into one atomic call
/// is what removes today's real footgun, where a caller can call prefab_open_editing, forget
/// prefab_save_editing, and leave a prefab genuinely stuck open (blocking every other prefab_apply/
/// prefab_open_editing call until someone remembers to close it). Every 'editProperty' op here loads,
/// edits, and saves in one atomic step - the SAME code path prefab_edit_property already uses when
/// no session is open - so prefab_apply simply cannot leave a prefab open across a return to the
/// caller. Changing several properties on the same prefab in one prefab_apply call is still
/// expressible: include several 'editProperty' ops (each is its own atomic load/edit/save).</para>
///
/// <para><b>'unappliedProperties' survives, never blanket success.</b> Plan 9's own finding, carried
/// forward verbatim: <c>PrefabUtility.ApplyPrefabInstance</c> can never write a prefab instance
/// ROOT's own default-override properties (name, local position/rotation/eulerAnglesHint - 11
/// entries present on every instance from the moment it is created) back to the prefab asset - this
/// is documented, permanent Unity behaviour, not a bug. An 'applyOverrides' op that leaves some
/// properties un-applied still throws nothing (it is not an error), so its OWN result -
/// 'unappliedProperties' plus an explanatory 'note' - rides along verbatim in this batch's 'results'
/// array. A caller who only looked at 'applied' (a bare list of succeeded indices) would see nothing
/// but success; 'results' is what prevents that from reading as a lie.</para>
///
/// <para><b>Field names: 'prefabPath' normalized across create/instantiate/editProperty;
/// 'gameObjectPath' reused three ways.</b> The three ops that concern a prefab ASSET used to spell
/// it two different ways - create called it 'assetPath', instantiate/editProperty called it
/// 'prefabPath' - even though all three mean the same thing: the prefab file the op is about. That
/// split was a live usability defect, not a deliberate design: a caller who successfully created a
/// prefab with 'assetPath' would then get a rejected editProperty call for using a name that
/// LOOKED consistent, because editProperty secretly wanted 'prefabPath' instead. Fixed by using
/// 'prefabPath' for all three. 'gameObjectPath' stays a SEPARATE field, and is deliberately the
/// SAME name for create's source object, applyOverrides' instance, and editProperty's optional
/// nested target - it is the same KIND of value each time (a GameObject path in the open SCENE,
/// never a file on disk), and each op only ever reads the fields its own vocabulary declares, so
/// there is no ambiguity in practice despite the shared name. Collapsing 'gameObjectPath' and
/// 'prefabPath' together (the way material_apply's 'path' now spans both new and existing files)
/// would NOT be an improvement here: prefab operations routinely need TWO different kinds of path
/// in the SAME op (a scene GameObject and a prefab asset), so a single name would be genuinely
/// ambiguous rather than merely inconsistent - see PrefabApplyCommands' own doc comment (UnityPlugin)
/// for the fuller rationale. This is an APP-LAYER rename only: the wire calls
/// <see cref="Hades.Tools.PrefabApplyCommands"/> (UnityPlugin) makes are byte-for-byte unchanged (still
/// 'assetPath' for create, 'prefabPath' for instantiate/editProperty) - <see cref="BuildOperation"/>
/// below is what translates the ONE caller-facing 'prefabPath' into whichever wire key each op's
/// underlying command actually expects.</para>
/// </summary>
[McpServerToolType]
public sealed class PrefabApplyTool(EditorProxy editor, ProjectService projects)
{
    static readonly string[] ValidOps = ["create", "instantiate", "applyOverrides", "editProperty", "createVariant"];

    [McpServerTool(Name = "prefab_apply", Title = "Apply Prefab Operations (Batch)", ReadOnly = false, UseStructuredContent = true)]
    [Description("Applies a batch of prefab operations - create (gameObjectPath, prefabPath), "
               + "instantiate (prefabPath, parent?), applyOverrides (gameObjectPath), editProperty "
               + "(prefabPath, componentType, propertyName, value, gameObjectPath? for a nested "
               + "child), createVariant (basePrefabPath, variantPath) - in ONE call, in the order "
               + "given. 'prefabPath' is the ONE field name for the prefab asset every op acts on "
               + "- its new file for create, its existing file for instantiate/editProperty - "
               + "always a file on disk. 'gameObjectPath' is always a SCENE object instead - "
               + "create's source, applyOverrides' instance, or editProperty's optional nested "
               + "child - never a file on disk, a different concept from 'prefabPath' entirely. "
               + "This is the batch form of prefab_create/prefab_instantiate/"
               + "prefab_apply_overrides/prefab_edit_property/prefab_open_editing/"
               + "prefab_save_editing/prefab_create_variant. Every editProperty op is ATOMIC "
               + "(load, edit, save in one step) - there is no 'open' op, so this tool cannot leave "
               + "a prefab stuck open the way the old open/edit/save sequence could. "
               + "UNDO: the whole batch is ONE Unity Undo group. RELOAD LEASE: the whole batch "
               + "acquires and releases Unity's reload lock exactly ONCE, not once per operation. "
               + "PARTIAL FAILURE, NOT ROLLED BACK: each operation's outcome is reported by its "
               + "0-based index in 'applied' (succeeded) or 'failed' (with its own error). Every "
               + "successful operation's own result is reported in 'results' alongside 'applied' - "
               + "in particular, applyOverrides ALWAYS reports 'unappliedProperties': Unity can never "
               + "write a prefab instance root's own name/position/rotation back to the prefab asset "
               + "(permanent Unity behaviour, not an error), so a caller must check 'results', never "
               + "assume 'applied' alone means everything was written. "
               + "An unrecognised 'op' value rejects the WHOLE call before anything is sent to the "
               + "Editor, listing the valid ops. Needs a live Editor - call hades_charon_status first "
               + "if unsure.")]
    public async Task<PrefabApplyResult> PrefabApply(
        [Description("Operations to apply, in order. Each needs 'op' plus that op's own fields: "
                   + "create{gameObjectPath,prefabPath}, instantiate{prefabPath,parent?}, "
                   + "applyOverrides{gameObjectPath}, editProperty{prefabPath,componentType,"
                   + "propertyName,value,gameObjectPath?}, createVariant{basePrefabPath,variantPath}. "
                   + "'prefabPath' always names the prefab asset the op is about (a file on disk); "
                   + "'gameObjectPath' always names a scene object, never a file on disk.")]
        IReadOnlyList<PrefabApplyOperation> operations,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        if (operations is null || operations.Count == 0)
            throw new McpException("prefab_apply needs a non-empty 'operations' array.");

        // Refused, not ignored - see SceneApplyTool's own doc comment. Nothing is sent to the
        // Editor until every operation's 'op' is confirmed valid.
        for (var i = 0; i < operations.Count; i++)
        {
            if (Array.IndexOf(ValidOps, operations[i].Op) < 0)
            {
                throw new McpException(
                    $"prefab_apply operations[{i}]: unknown op '{operations[i].Op}'. Valid ops: {string.Join(", ", ValidOps)}.");
            }
        }

        // Refused, not ignored - see OperationFieldValidator's own doc comment. An unrecognised
        // FIELD name on an otherwise-valid op is the same class of caller mistake as an unrecognised
        // op value above, and gets the identical whole-call, zero-wire-calls treatment.
        OperationFieldValidator.RejectUnknownFields("prefab_apply", operations);

        var wireOperations = WireJson.NewArray();
        foreach (var op in operations) wireOperations.Add(BuildOperation(op));

        var @params = WireJson.NewObject().SetProperty("operations", wireOperations);
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var result = await editor.SendCommandAsync(productGuid, "prefab.apply", @params).ConfigureAwait(false);

        return MapResult(result, operations.Count);
    }

    // ---------------------------------------------------------------- app op -> wire op ('prefabPath' renamed per-op to the wire key the plugin actually expects)

    static WireJson BuildOperation(PrefabApplyOperation op)
    {
        var o = WireJson.NewObject().SetProperty("op", WireJson.String(op.Op));

        if (!string.IsNullOrEmpty(op.GameObjectPath)) o.SetProperty("gameObjectPath", WireJson.String(op.GameObjectPath));

        // The plugin's wire contract (UnityPlugin/PrefabApplyCommands.cs, unchanged) still expects
        // create's target under 'assetPath' but instantiate/editProperty's under 'prefabPath' -
        // two different wire keys for what 'prefabPath' now names as ONE app-facing concept. This
        // is an app-layer rename only - see class doc comment.
        if (!string.IsNullOrEmpty(op.PrefabPath))
        {
            var wireKey = op.Op == "create" ? "assetPath" : "prefabPath";
            o.SetProperty(wireKey, WireJson.String(op.PrefabPath));
        }
        if (!string.IsNullOrEmpty(op.Parent)) o.SetProperty("parent", WireJson.String(op.Parent));
        if (!string.IsNullOrEmpty(op.ComponentType)) o.SetProperty("componentType", WireJson.String(op.ComponentType));
        if (!string.IsNullOrEmpty(op.PropertyName)) o.SetProperty("propertyName", WireJson.String(op.PropertyName));
        if (op.Value is { } value) o.SetProperty("value", WireJsonBridge.ToWire(value));
        if (!string.IsNullOrEmpty(op.BasePrefabPath)) o.SetProperty("basePrefabPath", WireJson.String(op.BasePrefabPath));
        if (!string.IsNullOrEmpty(op.VariantPath)) o.SetProperty("variantPath", WireJson.String(op.VariantPath));

        return o;
    }

    // ---------------------------------------------------------------- wire result -> PrefabApplyResult

    static PrefabApplyResult MapResult(WireJson result, int operationCount)
    {
        var applied = new List<int>();
        if (result.TryGetProperty("applied", out var appliedJson) && appliedJson!.Kind == WireKind.Array)
            foreach (var item in appliedJson.Items) applied.Add((int)item.AsInteger());

        var results = new List<PrefabApplyOpResult>();
        if (result.TryGetProperty("results", out var resultsJson) && resultsJson!.Kind == WireKind.Array)
        {
            foreach (var item in resultsJson.Items)
            {
                results.Add(new PrefabApplyOpResult
                {
                    Index = (int)EditorComponentTools.Int(item, "index"),
                    Op = EditorComponentTools.Str(item, "op"),
                    Result = item.TryGetProperty("result", out var r) ? WireJsonBridge.ToClr(r!) : null,
                });
            }
        }

        var failed = new List<PrefabApplyFailure>();
        if (result.TryGetProperty("failed", out var failedJson) && failedJson!.Kind == WireKind.Array)
        {
            foreach (var item in failedJson.Items)
            {
                failed.Add(new PrefabApplyFailure
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

        return new PrefabApplyResult { Applied = applied, Results = results, Failed = failed, Summary = summary };
    }
}
