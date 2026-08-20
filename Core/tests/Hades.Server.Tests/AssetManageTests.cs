using Hades.Contract.Wire;
using Microsoft.AspNetCore.Mvc.Testing;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Tests;

/// <summary>
/// asset_manage: the declarative batch that replaces EditorAssetTools' asset_move/asset_import and
/// EditorProjectTools' project_refresh_assets (3 tools). Same scope discipline as
/// ProjectSettingsApplyTests: this proves the tool-to-wire contract (one wire call, every
/// operation's fields translated verbatim, applied/results/failed/summary mapped back), not the
/// one-lease-window/self-managed-undo-group/real-per-op-behaviour properties, which are plugin-side
/// properties proven against a real Editor in UnityPlugin/Tests/Editor/AssetManageCommandsTests.cs.
/// </summary>
public sealed class AssetManageTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
{
    static JsonValue Obj(params (string Key, JsonValue Value)[] members)
    {
        var o = JsonValue.NewObject();
        foreach (var (key, value) in members) o.SetProperty(key, value);
        return o;
    }

    static JsonValue Prop(JsonValue obj, string key)
    {
        Assert.True(obj.TryGetProperty(key, out var value), $"expected wire param '{key}', got: {obj}");
        return value!;
    }

    // ---------------------------------------------------------------- structural validation (no Editor needed)

    [Fact]
    public async Task AssetManage_EmptyOperationsArray_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "asset_manage", new { operations = Array.Empty<object>() });

        Assert.Contains("operations", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task AssetManage_UnknownOp_RejectsWholeCallBeforeDispatchingAnything_ListsValidOps()
    {
        var envelope = await McpTestClient.CallTool(Factory, "asset_manage", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "move", ["sourcePath"] = "Assets/A.cs", ["destPath"] = "Assets/B.cs" },
                new Dictionary<string, object> { ["op"] = "frobnicate" },
            },
        });

        var text = McpTestClient.ErrorText(envelope);
        Assert.Contains("frobnicate", text);
        Assert.Contains("operations[1]", text);
        foreach (var op in new[] { "move", "import", "refresh" })
            Assert.Contains(op, text);
    }

    // ---------------------------------------------------------------- unknown FIELD refused before any wire call (per-op, not per-tool)

    /// <summary>Enumerates EVERY op asset_manage accepts (not a spot check) and proves each one,
    /// individually, refuses an unknown field before any wire call - including 'refresh', which
    /// (per this tool's own class doc comment) "needs no fields of its own" and so has no field of
    /// its own to have an op-typo on in the first place.</summary>
    [Fact]
    public async Task AssetManage_UnknownField_RejectedForEveryOp()
    {
        foreach (var op in new[] { "move", "import", "refresh" })
        {
            var envelope = await McpTestClient.CallTool(Factory, "asset_manage", new
            {
                operations = new[] { new Dictionary<string, object> { ["op"] = op, ["zzzNotAField"] = "x" } },
            });

            var text = McpTestClient.ErrorText(envelope);
            Assert.True(text.Contains("'zzzNotAField'") && text.Contains("operations[0]"),
                $"op '{op}' did not refuse an unknown field as expected. Got: {text}");
        }
    }

    // ---------------------------------------------------------------- one wire call, every field translated verbatim

    [Fact]
    public async Task AssetManage_FullOperationSweep_SendsOneWireCallWithEveryFieldTranslated_MapsAppliedAndSummary()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var results = JsonValue.NewArray()
            .Add(Obj(("index", JsonValue.Integer(0)), ("op", JsonValue.String("move")),
                ("result", Obj(("source", JsonValue.String("Assets/Old.cs")), ("destination", JsonValue.String("Assets/New.cs"))))))
            .Add(Obj(("index", JsonValue.Integer(1)), ("op", JsonValue.String("import")),
                ("result", Obj(("path", JsonValue.String("Assets/Art/Model.fbx")), ("guid", JsonValue.String("guid123")), ("type", JsonValue.String("GameObject"))))))
            .Add(Obj(("index", JsonValue.Integer(2)), ("op", JsonValue.String("refresh")),
                ("result", Obj(("refreshed", JsonValue.Bool(true))))));

        var appliedAll = JsonValue.NewArray().Add(JsonValue.Integer(0)).Add(JsonValue.Integer(1)).Add(JsonValue.Integer(2));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", appliedAll),
            ("results", results),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("3 applied, 0 failed of 3 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "asset_manage", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "move", ["sourcePath"] = "Assets/Old.cs", ["destPath"] = "Assets/New.cs" },
                new Dictionary<string, object> { ["op"] = "import", ["path"] = "Assets/Art/Model.fbx", ["forceUpdate"] = true },
                new Dictionary<string, object> { ["op"] = "refresh" },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        // ONE wire call for the whole 3-operation spec.
        Assert.Equal("asset.manage", request.Method);
        var ops = Prop(request.Params!, "operations");
        Assert.Equal(3, ops.Items.Count);

        Assert.Equal("Assets/Old.cs", Prop(ops.Items[0], "sourcePath").AsString());
        Assert.Equal("Assets/New.cs", Prop(ops.Items[0], "destPath").AsString());

        Assert.Equal("Assets/Art/Model.fbx", Prop(ops.Items[1], "path").AsString());
        Assert.True(Prop(ops.Items[1], "forceUpdate").AsBoolean());
        Assert.False(ops.Items[1].TryGetProperty("recursive", out _));

        Assert.Equal("refresh", Prop(ops.Items[2], "op").AsString());

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, applied);
        Assert.Equal(0, structured.GetProperty("failed").GetArrayLength());
        Assert.Contains("3", structured.GetProperty("summary").GetString());

        var resultsEl = structured.GetProperty("results");
        Assert.Equal(3, resultsEl.GetArrayLength());
        Assert.Equal("import", resultsEl[1].GetProperty("op").GetString());
        Assert.Equal("guid123", resultsEl[1].GetProperty("result").GetProperty("guid").GetString());
    }

    // ---------------------------------------------------------------- partial failure: mapped from the wire, no rollback

    [Fact]
    public async Task PartialFailure_WirePerOperationFailure_MapsIndexOpAndError_StillOneWireCall()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray().Add(JsonValue.Integer(0))),
            ("results", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(0)), ("op", JsonValue.String("move")),
                ("result", Obj(("source", JsonValue.String("Assets/A.cs")), ("destination", JsonValue.String("Assets/B.cs")))))
            )),
            ("failed", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(1)), ("op", JsonValue.String("import")),
                ("error", JsonValue.String("Nothing exists on disk at 'Assets/Ghost.png' to import."))))),
            ("summary", JsonValue.String("1 applied, 1 failed of 2 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "asset_manage", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "move", ["sourcePath"] = "Assets/A.cs", ["destPath"] = "Assets/B.cs" },
                new Dictionary<string, object> { ["op"] = "import", ["path"] = "Assets/Ghost.png" },
            },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("asset.manage", request.Method);
        Assert.Equal(2, Prop(request.Params!, "operations").Items.Count);

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 0 }, applied);

        var failed = structured.GetProperty("failed");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.Equal(1, failed[0].GetProperty("index").GetInt32());
        Assert.Equal("import", failed[0].GetProperty("op").GetString());
        Assert.Contains("Ghost.png", failed[0].GetProperty("error").GetString());

        // The unified partial-batch shape: 'results' surfaces an entry (with the op's own result
        // payload) for the APPLIED op, faithfully mapped from the wire - not just a bare index.
        var results = structured.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal(0, results[0].GetProperty("index").GetInt32());
        Assert.Equal("move", results[0].GetProperty("op").GetString());
        Assert.Equal("Assets/B.cs", results[0].GetProperty("result").GetProperty("destination").GetString());
    }

    // ---------------------------------------------------------------- whole-call (plugin-level) failure still propagates

    [Fact]
    public async Task AssetManage_PluginLevelError_PropagatesAsToolError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes, "asset.manage requires an 'operations' array parameter.");

        var envelope = await McpTestClient.CallTool(Factory, "asset_manage", new
        {
            operations = new[] { new Dictionary<string, object> { ["op"] = "refresh" } },
        });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("asset.manage requires an 'operations' array parameter.", McpTestClient.ErrorText(envelope));
    }
}
