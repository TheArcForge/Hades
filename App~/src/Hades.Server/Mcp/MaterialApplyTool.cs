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

/// <summary>One entry of material_apply's 'operations' array - see SceneApplyOperation's own doc
/// comment (SceneApplyTool.cs) for why this is one flat record rather than a discriminated union.
/// 'path' is the ONE field name for "the material this operation is about" across every op that
/// has one - see MaterialApplyTool's own class doc comment for why this consolidates what used to
/// be two different names (create's 'path', the other four ops' 'materialPath'/'destPath') for the
/// identical concept, and why 'sourcePath'/'gameObjectPath' stay separate (genuinely different
/// things: an ADDITIONAL second material, and a scene object, respectively).</summary>
public sealed record MaterialApplyOperation : IBatchOperation
{
    [JsonPropertyName("op")] public required string Op { get; init; }

    // create (new file), setProperty/assign/swapShader (existing target), duplicate (new copy) -
    // "the material this op is about". One name for every op that has one; always a file on disk.
    [JsonPropertyName("path")] [OpField("create", "setProperty", "assign", "duplicate", "swapShader")] public string? Path { get; init; }

    // create / swapShader
    [JsonPropertyName("shader")] [OpField("create", "swapShader")] public string? Shader { get; init; }

    // setProperty
    [JsonPropertyName("propertyName")] [OpField("setProperty")] public string? PropertyName { get; init; }
    [JsonPropertyName("value")] [OpField("setProperty")] public JsonElement? Value { get; init; }

    // assign - a SCENE OBJECT, never a file on disk; a different concept from 'path' above.
    [JsonPropertyName("gameObjectPath")] [OpField("assign")] public string? GameObjectPath { get; init; }
    [JsonPropertyName("slot")] [OpField("assign")] public int? Slot { get; init; }

    // duplicate - the EXISTING material being copied FROM: a second, different material from
    // 'path' above (duplicate's own new copy) - see class doc comment.
    [JsonPropertyName("sourcePath")] [OpField("duplicate")] public string? SourcePath { get; init; }

    // Backing store for [JsonExtensionData] - see OperationFieldValidator's own doc comment for why
    // an unrecognised field must be captured, not silently dropped, to be catchable at all. This is
    // the live reproduction's own field: 'property' (a typo for 'propertyName') lands here instead
    // of vanishing.
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>One successful operation's own result, echoed verbatim - see this file's own class doc
/// comment ("Per-op result data") for why this is a loosely-typed passthrough (via
/// <see cref="WireJsonBridge.ToClr"/>) rather than a dozen-mostly-null-field record: swapShader
/// returns 'survivedProperties'/'lostProperties', create returns 'guid', assign returns 'renderer',
/// and so on - each op's own shape, never invented or flattened here.</summary>
public sealed record MaterialApplyOpResult
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
}

public sealed record MaterialApplyFailure
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("error")] public required string Error { get; init; }
}

public sealed record MaterialApplyResult
{
    [JsonPropertyName("applied")] public required IReadOnlyList<int> Applied { get; init; }
    [JsonPropertyName("results")] public required IReadOnlyList<MaterialApplyOpResult> Results { get; init; }
    [JsonPropertyName("failed")] public required IReadOnlyList<MaterialApplyFailure> Failed { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
}

/// <summary>
/// The declarative batch that replaces EditorMaterialTools' material_create/material_set_property/
/// material_assign/material_duplicate/material_swap_shader - 5 tools - with the SAME shape Plan 10
/// Task 1's <see cref="SceneApplyTool"/> established: one MCP call sends the WHOLE 'operations'
/// array in ONE <c>material.apply</c> wire call to <see cref="Hades.Tools.MaterialApplyCommands"/>
/// (Plugin~), which applies every operation directly inside one handler body, one Undo group - see
/// that class's own doc comment. Ordering, partial-failure reporting (<c>applied</c>/<c>failed</c>
/// by index, never rolled back), and the "unknown op refused before any wire call" rule are all
/// identical to scene_apply - see SceneApplyTool's own class doc comment for the shared rationale,
/// not repeated here.
///
/// <para><b>Per-op result data.</b> Unlike scene_apply's ops (fire-and-forget mutations with
/// nothing useful to report beyond success/failure), several material operations return data a
/// caller needs even when nothing failed - above all material_swap_shader's 'survivedProperties'/
/// 'lostProperties' (Plan 9's own finding: Unity silently drops any shader property the new shader
/// does not declare, by name AND type, and a caller has no other way to find out which values just
/// vanished). Every successful operation's own result therefore appears verbatim in a 'results'
/// array entry (<c>{index, op, result}</c>) alongside the bare index in 'applied' - never collapsed
/// into a blanket "it worked", exactly the carried-forward Plan 9 requirement.</para>
///
/// <para><b>Field names: 'path' normalized across every op.</b> The five single-purpose tools this
/// replaces spelled "which material" two different ways - material_create called it 'path',
/// material_set_property/material_assign/material_swap_shader called it 'materialPath' (and
/// material_duplicate's destination was 'destPath') - even though every one of them means the SAME
/// thing: the material the operation is about. That split was a live usability defect, not a
/// deliberate design: a caller who successfully created a material with 'path' would then get a
/// rejected setProperty call for using the very field name the tool had just accepted, because
/// setProperty secretly wanted 'materialPath' instead. Fixed by using 'path' for every op that has
/// one - create's new file, setProperty/assign/swapShader's existing target, and duplicate's own
/// new copy. 'sourcePath' stays separate on duplicate, because that op genuinely needs a SECOND,
/// different material (the template to copy from) alongside 'path' (the new copy) - collapsing
/// those two would lose information, not simplify anything. 'gameObjectPath' also stays separate
/// (assign's scene object, never a file on disk - a different concept from 'path' entirely). This
/// is an APP-LAYER rename only: the wire calls <see cref="Hades.Tools.MaterialApplyCommands"/>
/// (Plugin~) makes are byte-for-byte unchanged (still 'path' for create, 'materialPath' for
/// setProperty/assign/swapShader, 'destPath' for duplicate's destination) - <see cref="BuildOperation"/>
/// below is what translates the ONE caller-facing 'path' into whichever wire key each op's
/// underlying command actually expects.</para>
/// </summary>
[McpServerToolType]
public sealed class MaterialApplyTool(EditorProxy editor, ProjectService projects)
{
    static readonly string[] ValidOps = ["create", "setProperty", "assign", "duplicate", "swapShader"];

    [McpServerTool(Name = "material_apply", Title = "Apply Material Operations (Batch)", ReadOnly = false, UseStructuredContent = true)]
    [Description("Applies a batch of material operations - create (path, shader?), setProperty "
               + "(path, propertyName, value), assign (gameObjectPath, path, slot?), "
               + "duplicate (sourcePath, path), swapShader (path, shader) - in ONE call, "
               + "in the order given. 'path' is the ONE field name for the material every op acts "
               + "on - its new file for create, its existing file for setProperty/assign/"
               + "swapShader, its new copy for duplicate - always a file on disk. 'sourcePath' "
               + "(duplicate only) is a SECOND, different material: the existing one being copied "
               + "FROM. 'gameObjectPath' (assign only) is a scene object instead, never a file on "
               + "disk - a different concept from 'path' entirely. This is the batch form of "
               + "material_create/material_set_property/"
               + "material_assign/material_duplicate/material_swap_shader. "
               + "UNDO: the whole batch is ONE Unity Undo group - a single Ctrl/Cmd+Z reverts every "
               + "operation in the spec, not just the last one. "
               + "PARTIAL FAILURE, NOT ROLLED BACK: each operation's outcome is reported by its "
               + "0-based index in 'applied' (succeeded) or 'failed' (with its own error) - operations "
               + "that already succeeded are never undone because a LATER one failed. Every successful "
               + "operation's own result (e.g. the new material's guid, or swapShader's "
               + "'survivedProperties'/'lostProperties' - Unity silently drops shader properties the "
               + "new shader does not declare, and this tells you which ones) is reported in 'results' "
               + "alongside 'applied', never just a bare success flag. "
               + "An unrecognised 'op' value rejects the WHOLE call before anything is sent to the "
               + "Editor, listing the valid ops. Needs a live Editor - call hades_charon_status first "
               + "if unsure.")]
    public async Task<MaterialApplyResult> MaterialApply(
        [Description("Operations to apply, in order. Each needs 'op' plus that op's own fields: "
                   + "create{path,shader?}, setProperty{path,propertyName,value}, "
                   + "assign{gameObjectPath,path,slot?}, duplicate{sourcePath,path}, "
                   + "swapShader{path,shader}. 'path' always names the material the op is about "
                   + "(a file on disk); duplicate's 'sourcePath' is the separate, existing material "
                   + "it copies from; assign's 'gameObjectPath' is a scene object, not a file.")]
        IReadOnlyList<MaterialApplyOperation> operations,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        if (operations is null || operations.Count == 0)
            throw new McpException("material_apply needs a non-empty 'operations' array.");

        // Refused, not ignored - see SceneApplyTool's own doc comment. Nothing is sent to the
        // Editor until every operation's 'op' is confirmed valid.
        for (var i = 0; i < operations.Count; i++)
        {
            if (Array.IndexOf(ValidOps, operations[i].Op) < 0)
            {
                throw new McpException(
                    $"material_apply operations[{i}]: unknown op '{operations[i].Op}'. Valid ops: {string.Join(", ", ValidOps)}.");
            }
        }

        // Refused, not ignored - see OperationFieldValidator's own doc comment. An unrecognised
        // FIELD name on an otherwise-valid op is the same class of caller mistake as an unrecognised
        // op value above, and gets the identical whole-call, zero-wire-calls treatment. This is what
        // closes the live reproduction: 'property' instead of 'propertyName'.
        OperationFieldValidator.RejectUnknownFields("material_apply", operations);

        var wireOperations = WireJson.NewArray();
        foreach (var op in operations) wireOperations.Add(BuildOperation(op));

        var @params = WireJson.NewObject().SetProperty("operations", wireOperations);
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var result = await editor.SendCommandAsync(productGuid, "material.apply", @params).ConfigureAwait(false);

        return MapResult(result, operations.Count);
    }

    // ---------------------------------------------------------------- app op -> wire op ('path' renamed per-op to the wire key the plugin actually expects)

    static WireJson BuildOperation(MaterialApplyOperation op)
    {
        var o = WireJson.NewObject().SetProperty("op", WireJson.String(op.Op));

        // The plugin's wire contract (Plugin~/MaterialApplyCommands.cs, unchanged) still expects
        // create's target under 'path', setProperty/assign/swapShader's under 'materialPath', and
        // duplicate's new-copy destination under 'destPath' - three different wire keys for what
        // 'path' now names as ONE app-facing concept. This is an app-layer rename only - see class
        // doc comment.
        if (!string.IsNullOrEmpty(op.Path))
        {
            var wireKey = op.Op switch
            {
                "create" => "path",
                "duplicate" => "destPath",
                _ => "materialPath",
            };
            o.SetProperty(wireKey, WireJson.String(op.Path));
        }
        if (!string.IsNullOrEmpty(op.Shader)) o.SetProperty("shader", WireJson.String(op.Shader));
        if (!string.IsNullOrEmpty(op.PropertyName)) o.SetProperty("propertyName", WireJson.String(op.PropertyName));
        if (op.Value is { } value) o.SetProperty("value", WireJsonBridge.ToWire(value));
        if (!string.IsNullOrEmpty(op.GameObjectPath)) o.SetProperty("gameObjectPath", WireJson.String(op.GameObjectPath));
        if (op.Slot is not null) o.SetProperty("slot", WireJson.Integer(op.Slot.Value));
        if (!string.IsNullOrEmpty(op.SourcePath)) o.SetProperty("sourcePath", WireJson.String(op.SourcePath));

        return o;
    }

    // ---------------------------------------------------------------- wire result -> MaterialApplyResult

    static MaterialApplyResult MapResult(WireJson result, int operationCount)
    {
        var applied = new List<int>();
        if (result.TryGetProperty("applied", out var appliedJson) && appliedJson!.Kind == WireKind.Array)
            foreach (var item in appliedJson.Items) applied.Add((int)item.AsInteger());

        var results = new List<MaterialApplyOpResult>();
        if (result.TryGetProperty("results", out var resultsJson) && resultsJson!.Kind == WireKind.Array)
        {
            foreach (var item in resultsJson.Items)
            {
                results.Add(new MaterialApplyOpResult
                {
                    Index = (int)EditorComponentTools.Int(item, "index"),
                    Op = EditorComponentTools.Str(item, "op"),
                    Result = item.TryGetProperty("result", out var r) ? WireJsonBridge.ToClr(r!) : null,
                });
            }
        }

        var failed = new List<MaterialApplyFailure>();
        if (result.TryGetProperty("failed", out var failedJson) && failedJson!.Kind == WireKind.Array)
        {
            foreach (var item in failedJson.Items)
            {
                failed.Add(new MaterialApplyFailure
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

        return new MaterialApplyResult { Applied = applied, Results = results, Failed = failed, Summary = summary };
    }
}
