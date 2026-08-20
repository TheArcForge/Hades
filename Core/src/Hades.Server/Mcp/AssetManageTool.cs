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

/// <summary>One entry of asset_manage's 'operations' array - see SceneApplyOperation's own doc
/// comment (SceneApplyTool.cs) for why this is one flat record rather than a discriminated union.
/// Every field is spelled exactly as the corresponding single-purpose asset.* wire command already
/// names it.</summary>
public sealed record AssetManageOperation : IBatchOperation
{
    [JsonPropertyName("op")] public required string Op { get; init; }

    // move
    [JsonPropertyName("sourcePath")] [OpField("move")] public string? SourcePath { get; init; }
    [JsonPropertyName("destPath")] [OpField("move")] public string? DestPath { get; init; }

    // import
    [JsonPropertyName("path")] [OpField("import")] public string? Path { get; init; }
    [JsonPropertyName("forceUpdate")] [OpField("import")] public bool? ForceUpdate { get; init; }
    [JsonPropertyName("recursive")] [OpField("import")] public bool? Recursive { get; init; }

    // refresh needs no fields of its own - see OperationFieldValidator's own doc comment for how an
    // op with no [OpField]-tagged property anywhere still gets every OTHER op's fields correctly
    // treated as "not mine" (Table<TOp>.AllFields fallback), so 'refresh' accepts nothing but 'op'.

    // Backing store for [JsonExtensionData] - see OperationFieldValidator's own doc comment for why
    // an unrecognised field must be captured, not silently dropped, to be catchable at all.
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>One successful operation's own result, echoed verbatim - see PrefabApplyOpResult's own
/// doc comment for why this is a loosely-typed passthrough rather than a dozen-mostly-null-field
/// record.</summary>
public sealed record AssetManageOpResult
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
}

public sealed record AssetManageFailure
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("error")] public required string Error { get; init; }
}

public sealed record AssetManageResult
{
    [JsonPropertyName("applied")] public required IReadOnlyList<int> Applied { get; init; }
    [JsonPropertyName("results")] public required IReadOnlyList<AssetManageOpResult> Results { get; init; }
    [JsonPropertyName("failed")] public required IReadOnlyList<AssetManageFailure> Failed { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
}

/// <summary>
/// The declarative batch that replaces EditorAssetTools' asset_move/asset_import and
/// EditorProjectTools' project_refresh_assets - 3 tools - with the SAME shape Plan 10 Task 1's
/// <see cref="SceneApplyTool"/> established: one MCP call sends the WHOLE 'operations' array in ONE
/// <c>asset.manage</c> wire call to <see cref="Hades.Tools.AssetManageCommands"/> (UnityPlugin), which
/// applies every operation directly inside one handler body. Ordering, partial-failure reporting
/// (<c>applied</c>/<c>failed</c> by index, never rolled back), per-op result data in 'results', and
/// the "unknown op refused before any wire call" rule are all identical to scene_apply/prefab_apply/
/// project_settings_apply - see SceneApplyTool's own class doc comment for the shared rationale, not
/// repeated here.
///
/// <para><b>Mixed lease classes, like prefab_apply/project_settings_apply.</b> "move" is class-1 (no
/// reload lease - AssetCommands never touches the gate for it); "import" and "refresh" are class-2
/// (reimporting/refreshing can trigger the asset pipeline and, for a script, compilation). The
/// plugin-side handler wraps the WHOLE batch in exactly ONE lease window regardless of which ops a
/// given call happens to contain - see <see cref="Hades.Tools.AssetManageCommands"/>' own doc comment
/// for why calling each operation's normal, self-leasing entry point per-op would be both wasteful
/// and unsafe.</para>
///
/// <para><b>No Undo, for all three ops - unlike prefab_apply/project_settings_apply.</b> Unlike every
/// earlier batch tool, NONE of asset_manage's three operations have ever had Undo coverage: moving/
/// renaming an asset has no Unity Undo primitive that covers a path the way
/// <see cref="UnityEditor.Undo.RecordObject"/> covers a serialized field (see the standalone
/// asset_move tool's own description), and import/refresh are project-file-level operations, not
/// in-memory object state. A caller must not expect a single Ctrl/Cmd+Z to undo an asset_manage call
/// at all - reversing a move is a second asset_manage call.</para>
/// </summary>
[McpServerToolType]
public sealed class AssetManageTool(EditorProxy editor, ProjectService projects)
{
    static readonly string[] ValidOps = ["move", "import", "refresh"];

    [McpServerTool(Name = "asset_manage", Title = "Manage Assets (Batch)", ReadOnly = false, UseStructuredContent = true)]
    [Description("Applies a batch of asset file-lifecycle operations - move(sourcePath, destPath), "
               + "import(path, forceUpdate?, recursive?), refresh() - in ONE call, in the order "
               + "given. This is the batch form of asset_move/asset_import/project_refresh_assets. "
               + "NO UNDO for any of the three ops: an asset's path/import state is not a serialized "
               + "field Unity's Undo can snapshot - reversing a move, or re-importing, is a second "
               + "asset_manage call, not Ctrl/Cmd+Z. "
               + "PARTIAL FAILURE, NOT ROLLED BACK: each operation's outcome is reported by its "
               + "0-based index in 'applied' (succeeded) or 'failed' (with its own error) - "
               + "operations that already succeeded are never undone because a LATER one failed. "
               + "Every successful operation's own result is reported in 'results' alongside "
               + "'applied'. An unrecognised 'op' value rejects the WHOLE call before anything is "
               + "sent to the Editor, listing the valid ops. Needs a live Editor - call "
               + "hades_charon_status first if unsure.")]
    public async Task<AssetManageResult> AssetManage(
        [Description("Operations to apply, in order. Each needs 'op' plus that op's own fields: "
                   + "move{sourcePath,destPath}, import{path,forceUpdate?,recursive?}, refresh{}.")]
        IReadOnlyList<AssetManageOperation> operations,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        if (operations is null || operations.Count == 0)
            throw new McpException("asset_manage needs a non-empty 'operations' array.");

        // Refused, not ignored - see SceneApplyTool's own doc comment. Nothing is sent to the
        // Editor until every operation's 'op' is confirmed valid.
        for (var i = 0; i < operations.Count; i++)
        {
            if (Array.IndexOf(ValidOps, operations[i].Op) < 0)
            {
                throw new McpException(
                    $"asset_manage operations[{i}]: unknown op '{operations[i].Op}'. Valid ops: {string.Join(", ", ValidOps)}.");
            }
        }

        // Refused, not ignored - see OperationFieldValidator's own doc comment. An unrecognised
        // FIELD name on an otherwise-valid op is the same class of caller mistake as an unrecognised
        // op value above, and gets the identical whole-call, zero-wire-calls treatment.
        OperationFieldValidator.RejectUnknownFields("asset_manage", operations);

        var wireOperations = WireJson.NewArray();
        foreach (var op in operations) wireOperations.Add(BuildOperation(op));

        var @params = WireJson.NewObject().SetProperty("operations", wireOperations);
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var result = await editor.SendCommandAsync(productGuid, "asset.manage", @params).ConfigureAwait(false);

        return MapResult(result, operations.Count);
    }

    // ---------------------------------------------------------------- app op -> wire op (field-for-field, no renaming)

    static WireJson BuildOperation(AssetManageOperation op)
    {
        var o = WireJson.NewObject().SetProperty("op", WireJson.String(op.Op));

        if (!string.IsNullOrEmpty(op.SourcePath)) o.SetProperty("sourcePath", WireJson.String(op.SourcePath));
        if (!string.IsNullOrEmpty(op.DestPath)) o.SetProperty("destPath", WireJson.String(op.DestPath));
        if (!string.IsNullOrEmpty(op.Path)) o.SetProperty("path", WireJson.String(op.Path));
        if (op.ForceUpdate is { } forceUpdate) o.SetProperty("forceUpdate", WireJson.Bool(forceUpdate));
        if (op.Recursive is { } recursive) o.SetProperty("recursive", WireJson.Bool(recursive));

        return o;
    }

    // ---------------------------------------------------------------- wire result -> AssetManageResult

    static AssetManageResult MapResult(WireJson result, int operationCount)
    {
        var applied = new List<int>();
        if (result.TryGetProperty("applied", out var appliedJson) && appliedJson!.Kind == WireKind.Array)
            foreach (var item in appliedJson.Items) applied.Add((int)item.AsInteger());

        var results = new List<AssetManageOpResult>();
        if (result.TryGetProperty("results", out var resultsJson) && resultsJson!.Kind == WireKind.Array)
        {
            foreach (var item in resultsJson.Items)
            {
                results.Add(new AssetManageOpResult
                {
                    Index = (int)EditorComponentTools.Int(item, "index"),
                    Op = EditorComponentTools.Str(item, "op"),
                    Result = item.TryGetProperty("result", out var r) ? WireJsonBridge.ToClr(r!) : null,
                });
            }
        }

        var failed = new List<AssetManageFailure>();
        if (result.TryGetProperty("failed", out var failedJson) && failedJson!.Kind == WireKind.Array)
        {
            foreach (var item in failedJson.Items)
            {
                failed.Add(new AssetManageFailure
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

        return new AssetManageResult { Applied = applied, Results = results, Failed = failed, Summary = summary };
    }
}
