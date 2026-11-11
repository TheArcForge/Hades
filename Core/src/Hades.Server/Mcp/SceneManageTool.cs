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

/// <summary>One entry of scene_manage's 'operations' array - see SceneApplyOperation's own doc
/// comment (SceneApplyTool.cs) for why this is one flat record rather than a discriminated union.
/// Every field is spelled exactly as the corresponding single-purpose scene.* wire command already
/// names it. 'path' is deliberately reused across save/create/open (the "which scene" each op acts
/// on), the same way PrefabApplyOperation reuses 'gameObjectPath' across three of its own ops.</summary>
public sealed record SceneManageOperation : IBatchOperation
{
    [JsonPropertyName("op")] public required string Op { get; init; }

    // save (Save As path, omit to save in place); create (path for the new scene); open (path to open)
    [JsonPropertyName("path")] [OpField("save", "create", "open")] public string? Path { get; init; }

    // create
    [JsonPropertyName("template")] [OpField("create")] public string? Template { get; init; }

    // open
    [JsonPropertyName("additive")] [OpField("open")] public bool? Additive { get; init; }

    // open (non-additive), create: F5/F6 - both replace every currently loaded scene and refuse if
    // any is dirty; this opts into discarding those unsaved changes instead.
    [JsonPropertyName("discardUnsaved")] [OpField("open", "create")] public bool? DiscardUnsaved { get; init; }

    // duplicate
    [JsonPropertyName("sourcePath")] [OpField("duplicate")] public string? SourcePath { get; init; }
    [JsonPropertyName("destPath")] [OpField("duplicate")] public string? DestPath { get; init; }

    // Backing store for [JsonExtensionData] - see OperationFieldValidator's own doc comment for why
    // an unrecognised field must be captured, not silently dropped, to be catchable at all.
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>One successful operation's own result, echoed verbatim - see PrefabApplyOpResult's own
/// doc comment for why this is a loosely-typed passthrough rather than a dozen-mostly-null-field
/// record.</summary>
public sealed record SceneManageOpResult
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
}

public sealed record SceneManageFailure
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("op")] public required string Op { get; init; }
    [JsonPropertyName("error")] public required string Error { get; init; }
}

public sealed record SceneManageResult
{
    [JsonPropertyName("applied")] public required IReadOnlyList<int> Applied { get; init; }
    [JsonPropertyName("results")] public required IReadOnlyList<SceneManageOpResult> Results { get; init; }
    [JsonPropertyName("failed")] public required IReadOnlyList<SceneManageFailure> Failed { get; init; }
    [JsonPropertyName("summary")] public required string Summary { get; init; }
}

/// <summary>
/// The declarative batch that replaces EditorSceneManagementTools' scene_save/scene_create/
/// scene_duplicate and EditorProjectTools' scene_open - 4 tools - with the SAME shape Plan 10 Task
/// 1's <see cref="SceneApplyTool"/> established: one MCP call sends the WHOLE 'operations' array in
/// ONE <c>scene.manage</c> wire call to <see cref="Hades.Tools.SceneManageCommands"/> (UnityPlugin),
/// which applies every operation directly inside one handler body. Ordering, partial-failure
/// reporting (<c>applied</c>/<c>failed</c> by index, never rolled back), per-op result data in
/// 'results', and the "unknown op refused before any wire call" rule are all identical to
/// scene_apply/prefab_apply/project_settings_apply/asset_manage - see SceneApplyTool's own class doc
/// comment for the shared rationale, not repeated here.
///
/// <para><b>'create' now OPENS the new scene as the active scene (F5/F6), same as non-additive
/// 'open'.</b> An earlier revision kept 'create' additive-and-closed precisely to avoid Plan 9's own
/// E2E footgun (<c>OpenSceneMode.Single</c> silently discarding a caller's unsaved open scene with
/// no prompt) - but that traded one defect for another: a caller's very next scene_apply/scene_save
/// call landed on whatever scene happened to be active BEFORE 'create' ran, not the one just
/// created, live-observed writing test objects into a real, unrelated scene on disk. Both 'create'
/// and non-additive 'open' now REFUSE instead of silently discarding: if any currently loaded scene
/// has unsaved changes, the call is refused naming the dirty scene(s), unless this operation passes
/// <c>discardUnsaved: true</c>. Additive 'open' unloads nothing, so it is never refused this way.
/// This op routes to the EXACT SAME <see cref="Hades.Tools.SceneManagementCommands.CreateScene"/>
/// the standalone scene_create tool already uses - see <see cref="Hades.Tools.SceneManageCommands"/>'s
/// own doc comment - so the property holds by construction, not by a second, parallel
/// implementation that could drift.</para>
///
/// <para><b>Mixed lease classes, like prefab_apply/project_settings_apply/asset_manage.</b> 'save'/
/// 'create'/'duplicate' are class-1 (no reload lease); 'open' is class-2. The plugin-side handler
/// wraps the WHOLE batch in exactly ONE lease window regardless of which ops a given call happens to
/// contain - see <see cref="Hades.Tools.SceneManageCommands"/>' own doc comment for why calling
/// 'open's normal, self-leasing entry point per-op would be both wasteful and unsafe.</para>
///
/// <para><b>Undo is uneven, like project_settings_apply.</b> 'create'/'duplicate' each register Undo
/// for the new scene asset; 'save' (a pure filesystem write) and 'open' (a live-Editor state change)
/// have none - do not assume one Ctrl/Cmd+Z reverts a scene_manage batch the reliable way it does
/// scene_apply's.</para>
/// </summary>
[McpServerToolType]
public sealed class SceneManageTool(EditorProxy editor, ProjectService projects)
{
    static readonly string[] ValidOps = ["save", "create", "open", "duplicate"];

    [McpServerTool(Name = "scene_manage", Title = "Manage Scenes (Batch)", ReadOnly = false, UseStructuredContent = true)]
    [Description("Applies a batch of scene file-lifecycle operations in one call: save, "
               + "create, open, duplicate. Per-op fields are listed on 'operations'. 'create' OPENS "
               + "the new scene as the active scene, like a non-additive 'open' - both REPLACE every loaded "
               + "scene and REFUSE if any has unsaved changes (names the dirty scene, or pass "
               + "discardUnsaved:true to proceed). Additive open (additive:true) unloads nothing "
               + "and is never refused this way. UNDO IS UNEVEN: create/duplicate register Undo; "
               + "save/open do not. Needs a live Editor.")]
    public async Task<SceneManageResult> SceneManage(
        [Description("Per-op fields (each op also needs 'op'): "
                   + "save{path?}, create{path,template?,discardUnsaved?}, "
                   + "open{path,additive?,discardUnsaved?}, duplicate{sourcePath,destPath}.")]
        IReadOnlyList<SceneManageOperation> operations,
        [Description("Project handle from hades_status (omit if only one).")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        if (operations is null || operations.Count == 0)
            throw new McpException("scene_manage needs a non-empty 'operations' array.");

        // Refused, not ignored - see SceneApplyTool's own doc comment. Nothing is sent to the
        // Editor until every operation's 'op' is confirmed valid.
        for (var i = 0; i < operations.Count; i++)
        {
            if (Array.IndexOf(ValidOps, operations[i].Op) < 0)
            {
                throw new McpException(
                    $"scene_manage operations[{i}]: unknown op '{operations[i].Op}'. Valid ops: {string.Join(", ", ValidOps)}.");
            }
        }

        // Refused, not ignored - see OperationFieldValidator's own doc comment. An unrecognised
        // FIELD name on an otherwise-valid op is the same class of caller mistake as an unrecognised
        // op value above, and gets the identical whole-call, zero-wire-calls treatment.
        OperationFieldValidator.RejectUnknownFields("scene_manage", operations);

        var wireOperations = WireJson.NewArray();
        foreach (var op in operations) wireOperations.Add(BuildOperation(op));

        var @params = WireJson.NewObject().SetProperty("operations", wireOperations);
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var result = await editor.SendCommandAsync(productGuid, "scene.manage", @params).ConfigureAwait(false);

        return MapResult(result, operations.Count);
    }

    // ---------------------------------------------------------------- app op -> wire op (field-for-field, no renaming)

    static WireJson BuildOperation(SceneManageOperation op)
    {
        var o = WireJson.NewObject().SetProperty("op", WireJson.String(op.Op));

        if (!string.IsNullOrEmpty(op.Path)) o.SetProperty("path", WireJson.String(op.Path));
        if (!string.IsNullOrEmpty(op.Template)) o.SetProperty("template", WireJson.String(op.Template));
        if (op.Additive is { } additive) o.SetProperty("additive", WireJson.Bool(additive));
        if (op.DiscardUnsaved is { } discardUnsaved) o.SetProperty("discardUnsaved", WireJson.Bool(discardUnsaved));
        if (!string.IsNullOrEmpty(op.SourcePath)) o.SetProperty("sourcePath", WireJson.String(op.SourcePath));
        if (!string.IsNullOrEmpty(op.DestPath)) o.SetProperty("destPath", WireJson.String(op.DestPath));

        return o;
    }

    // ---------------------------------------------------------------- wire result -> SceneManageResult

    static SceneManageResult MapResult(WireJson result, int operationCount)
    {
        var applied = new List<int>();
        if (result.TryGetProperty("applied", out var appliedJson) && appliedJson!.Kind == WireKind.Array)
            foreach (var item in appliedJson.Items) applied.Add((int)item.AsInteger());

        var results = new List<SceneManageOpResult>();
        if (result.TryGetProperty("results", out var resultsJson) && resultsJson!.Kind == WireKind.Array)
        {
            foreach (var item in resultsJson.Items)
            {
                results.Add(new SceneManageOpResult
                {
                    Index = (int)EditorComponentTools.Int(item, "index"),
                    Op = EditorComponentTools.Str(item, "op"),
                    Result = item.TryGetProperty("result", out var r) ? WireJsonBridge.ToClr(r!) : null,
                });
            }
        }

        var failed = new List<SceneManageFailure>();
        if (result.TryGetProperty("failed", out var failedJson) && failedJson!.Kind == WireKind.Array)
        {
            foreach (var item in failedJson.Items)
            {
                failed.Add(new SceneManageFailure
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

        return new SceneManageResult { Applied = applied, Results = results, Failed = failed, Summary = summary };
    }
}
