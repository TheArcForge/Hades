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

/// <summary>One entry of scriptable_object_apply's 'operations' array - see SceneApplyOperation's
/// own doc comment (SceneApplyTool.cs) for why this is one flat record rather than a discriminated
/// union. 'path' is the ONE field name for "the ScriptableObject .asset this op is about" across
/// both ops - create's new file, setProperties' existing one - the same normalisation
/// material_apply's own class doc comment documents paying for once already.</summary>
public sealed record ScriptableObjectApplyOperation : IBatchOperation
{
    [JsonPropertyName("op")] public required string Op { get; init; }

    // create (new file), setProperties (existing file) - "the ScriptableObject asset this op is about".
    [JsonPropertyName("path")] [OpField("create", "setProperties")] public string? Path { get; init; }

    // create - the ScriptableObject-derived class to instantiate.
    [JsonPropertyName("type")] [OpField("create")] public string? Type { get; init; }

    // setProperties - {fieldName: value}, the same grammar scene_apply's setProperties uses.
    [JsonPropertyName("values")] [OpField("setProperties")] public JsonElement? Values { get; init; }

    // Backing store for [JsonExtensionData] - see OperationFieldValidator's own doc comment for why
    // an unrecognised field must be captured, not silently dropped, to be catchable at all.
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>One successful operation's own result, echoed verbatim - see MaterialApplyOpResult's own
/// doc comment for why this is a loosely-typed passthrough rather than a dozen-mostly-null-field
/// record: create returns 'type'/'assembly'/'guid', setProperties returns 'applied'/'failed'.</summary>
public sealed record ScriptableObjectApplyOpResult
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
}

public sealed record ScriptableObjectApplyFailure
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("error")] public required string Error { get; init; }
}

public sealed record ScriptableObjectApplyResult
{
    [JsonPropertyName("applied")] public required IReadOnlyList<int> Applied { get; init; }
    [JsonPropertyName("results")] public required IReadOnlyList<ScriptableObjectApplyOpResult> Results { get; init; }
    [JsonPropertyName("failed")] public required IReadOnlyList<ScriptableObjectApplyFailure> Failed { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
}

/// <summary>
/// DESIGN-04: the ONE new tool this design adds. A declarative batch shaped exactly like
/// material_apply/scene_apply - one MCP call sends the WHOLE 'operations' array in ONE
/// <c>scriptableObject.apply</c> wire call to <see cref="Hades.Tools.ScriptableObjectApplyCommands"/>
/// (UnityPlugin), which applies every operation directly inside one handler body, one Undo group.
/// Ordering, partial-failure reporting (<c>applied</c>/<c>failed</c> by index, never rolled back),
/// per-op result data in 'results', and the "unknown op refused before any wire call" rule are all
/// identical to material_apply/scene_apply - see SceneApplyTool's own class doc comment for the
/// shared rationale, not repeated here.
///
/// <para><b>Undo - deliberately NOT material_apply's wording.</b> DESIGN-04 §5.2 probe-verified that
/// <see cref="UnityEditor.Undo.RegisterCreatedObjectUndo"/> destroys the in-memory object on undo but
/// leaves the <c>.asset</c> FILE it wrote on disk. So one Ctrl/Cmd+Z reverts every 'setProperties'
/// field change and destroys every object a batch's own 'create' ops made - but the files those
/// 'create' ops wrote remain, and finishing the undo needs a separate <c>asset_manage</c> delete op.
/// material_apply's own live description makes the stronger, now-disproven claim ("a single Ctrl/Cmd+Z
/// reverts every operation") for batches containing its own 'create' - a shipped accuracy bug fixed
/// in MaterialApplyTool's own description as part of this same change (DESIGN-04 §5.2's finding;
/// ownership assigned to this lane by docs/design/ROADMAP.md's Phase 1 Lane D entry, overriding
/// DESIGN-04 §5.2's own "filed separately" note) - this tool's description does not repeat that
/// mistake for its own 'create'.</para>
///
/// <para><b>Type resolution refuses ambiguity rather than guessing.</b> 'type' is resolved against
/// every loaded assembly (<see cref="Hades.Tools.UnityTypeSearch.FindAll"/>, plugin-side); more than
/// one same-named ScriptableObject-derived type across different assemblies is refused, listing every
/// match, rather than picking one - unlike component.* 's own first-match-wins name resolution
/// (deliberately left alone - DESIGN-04 §11.5). <c>[CreateAssetMenu]</c> is ignored entirely: not
/// consulted for gating, defaults, or validation (DESIGN-04 §4.5).</para>
///
/// <para><b>An .asset node's graph Kind is 'MonoBehaviour', not 'ScriptableObject'</b> (DESIGN-04
/// §6.4) - a ScriptableObject .asset is a <c>!u!114</c> document, so
/// <c>graph_query(kind: "ScriptableObject")</c> will never find what this tool just created.
/// <c>graph_query(fileType: "ScriptableObject")</c> and <c>search_by_name</c> both will.</para>
/// </summary>
[McpServerToolType]
public sealed class ScriptableObjectApplyTool(EditorProxy editor, ProjectService projects)
{
    static readonly string[] ValidOps = ["create", "setProperties"];

    [McpServerTool(Name = "scriptable_object_apply", Title = "Apply ScriptableObject Operations (Batch)", ReadOnly = false, UseStructuredContent = true)]
    [Description("Applies a batch of ScriptableObject asset operations in one call, as ONE Undo "
               + "group: create(path, type), setProperties(path, values). 'type' is the class "
               + "name, short or namespace-qualified (required if ambiguous). 'values' is "
               + "{fieldName: value}; references resolve by asset path, including one created "
               + "earlier in the batch. Undo destroys create's in-memory objects but leaves its "
               + ".asset FILE on disk - use asset_manage's delete to finish undoing. Graph Kind is "
               + "'MonoBehaviour', not 'ScriptableObject' - find via graph_query(fileType: "
               + "\"ScriptableObject\") or search_by_name. Refused in Play Mode.")]
    public async Task<ScriptableObjectApplyResult> ScriptableObjectApply(
        [Description("Per-op fields (each op also needs 'op'): "
                   + "create{path,type}, setProperties{path,values}. 'path' always names the "
                   + ".asset the op is about.")]
        IReadOnlyList<ScriptableObjectApplyOperation> operations,
        [Description("Project handle from hades_status (omit if only one).")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        if (operations is null || operations.Count == 0)
            throw new McpException("scriptable_object_apply needs a non-empty 'operations' array.");

        // Refused, not ignored - see SceneApplyTool's own doc comment. Nothing is sent to the
        // Editor until every operation's 'op' is confirmed valid.
        for (var i = 0; i < operations.Count; i++)
        {
            if (Array.IndexOf(ValidOps, operations[i].Op) < 0)
            {
                throw new McpException(
                    $"scriptable_object_apply operations[{i}]: unknown op '{operations[i].Op}'. Valid ops: {string.Join(", ", ValidOps)}.");
            }
        }

        // Refused, not ignored - see OperationFieldValidator's own doc comment. An unrecognised
        // FIELD name on an otherwise-valid op is the same class of caller mistake as an unrecognised
        // op value above, and gets the identical whole-call, zero-wire-calls treatment.
        OperationFieldValidator.RejectUnknownFields("scriptable_object_apply", operations);

        var wireOperations = WireJson.NewArray();
        foreach (var op in operations) wireOperations.Add(BuildOperation(op));

        var @params = WireJson.NewObject().SetProperty("operations", wireOperations);
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var result = await editor.SendCommandAsync(productGuid, "scriptableObject.apply", @params).ConfigureAwait(false);

        return MapResult(result, operations.Count);
    }

    // ---------------------------------------------------------------- app op -> wire op (field-for-field, no renaming)

    static WireJson BuildOperation(ScriptableObjectApplyOperation op)
    {
        var o = WireJson.NewObject().SetProperty("op", WireJson.String(op.Op));

        if (!string.IsNullOrEmpty(op.Path)) o.SetProperty("path", WireJson.String(op.Path));
        if (!string.IsNullOrEmpty(op.Type)) o.SetProperty("type", WireJson.String(op.Type));
        if (op.Values is { } values) o.SetProperty("values", WireJsonBridge.ToWire(values));

        return o;
    }

    // ---------------------------------------------------------------- wire result -> ScriptableObjectApplyResult

    static ScriptableObjectApplyResult MapResult(WireJson result, int operationCount)
    {
        var applied = new List<int>();
        if (result.TryGetProperty("applied", out var appliedJson) && appliedJson!.Kind == WireKind.Array)
            foreach (var item in appliedJson.Items) applied.Add((int)item.AsInteger());

        var results = new List<ScriptableObjectApplyOpResult>();
        if (result.TryGetProperty("results", out var resultsJson) && resultsJson!.Kind == WireKind.Array)
        {
            foreach (var item in resultsJson.Items)
            {
                results.Add(new ScriptableObjectApplyOpResult
                {
                    Index = (int)EditorComponentTools.Int(item, "index"),
                    Op = EditorComponentTools.Str(item, "op"),
                    Result = item.TryGetProperty("result", out var r) ? WireJsonBridge.ToClr(r!) : null,
                });
            }
        }

        var failed = new List<ScriptableObjectApplyFailure>();
        if (result.TryGetProperty("failed", out var failedJson) && failedJson!.Kind == WireKind.Array)
        {
            foreach (var item in failedJson.Items)
            {
                failed.Add(new ScriptableObjectApplyFailure
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

        return new ScriptableObjectApplyResult { Applied = applied, Results = results, Failed = failed, Summary = summary };
    }
}
