using Hades.Contract.Wire;
using Microsoft.AspNetCore.Mvc.Testing;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Tests;

/// <summary>
/// scriptable_object_apply: DESIGN-04's new tool, shaped exactly like material_apply/asset_manage.
/// Same scope discipline as MaterialApplyTests/AssetManageTests: this proves the tool-to-wire
/// contract (one wire call, every operation's fields translated verbatim - no per-op renaming, since
/// both 'create' and 'setProperties' already use 'path' on the wire - applied/results/failed/summary
/// mapped back), not Undo/no-lease/real ordering/type-resolution, which are plugin-side properties
/// proven against a real Editor in UnityPlugin/Tests/Editor/ScriptableObjectApplyCommandsTests.cs.
/// </summary>
public sealed class ScriptableObjectApplyTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
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
    public async Task ScriptableObjectApply_EmptyOperationsArray_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "scriptable_object_apply", new { operations = Array.Empty<object>() });

        Assert.Contains("operations", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task ScriptableObjectApply_UnknownOp_RejectsWholeCallBeforeDispatchingAnything_ListsValidOps()
    {
        var envelope = await McpTestClient.CallTool(Factory, "scriptable_object_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["path"] = "Assets/Foo.asset", ["type"] = "ItemConfig" },
                new Dictionary<string, object> { ["op"] = "frobnicate" },
            },
        });

        var text = McpTestClient.ErrorText(envelope);
        Assert.Contains("frobnicate", text);
        Assert.Contains("operations[1]", text);
        foreach (var op in new[] { "create", "setProperties" })
            Assert.Contains(op, text);
    }

    // ---------------------------------------------------------------- unknown FIELD refused before any wire call (per-op, not per-tool)

    [Fact]
    public async Task ScriptableObjectApply_UnknownField_RejectedForEveryOp()
    {
        foreach (var op in new[] { "create", "setProperties" })
        {
            var envelope = await McpTestClient.CallTool(Factory, "scriptable_object_apply", new
            {
                operations = new[] { new Dictionary<string, object> { ["op"] = op, ["zzzNotAField"] = "x" } },
            });

            var text = McpTestClient.ErrorText(envelope);
            Assert.True(text.Contains("'zzzNotAField'") && text.Contains("operations[0]"),
                $"op '{op}' did not refuse an unknown field as expected. Got: {text}");
        }
    }

    /// <summary>Field validity is PER-OP, not per-tool: 'type' is create-only, so sending it
    /// alongside setProperties (a DIFFERENT op in the SAME flat record) must be refused exactly like
    /// a field the record does not declare at all.</summary>
    [Fact]
    public async Task ScriptableObjectApply_FieldFromSiblingOp_IsRejected_NotSilentlyAccepted()
    {
        var envelope = await McpTestClient.CallTool(Factory, "scriptable_object_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object>
                {
                    ["op"] = "setProperties", ["path"] = "Assets/Foo.asset",
                    ["values"] = new Dictionary<string, object> { ["damage"] = 12 },
                    ["type"] = "ItemConfig", // create-only
                },
            },
        });

        var text = McpTestClient.ErrorText(envelope);
        Assert.Contains("operations[0]", text);
        Assert.Contains("'type'", text);
    }

    // ---------------------------------------------------------------- one wire call, every field translated verbatim

    [Fact]
    public async Task ScriptableObjectApply_FullOperationSweep_SendsOneWireCallWithEveryFieldTranslated_MapsAppliedAndSummary()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var results = JsonValue.NewArray()
            .Add(Obj(("index", JsonValue.Integer(0)), ("op", JsonValue.String("create")),
                ("result", Obj(
                    ("path", JsonValue.String("Assets/Data/Sword.asset")),
                    ("type", JsonValue.String("Game.Items.ItemConfig")),
                    ("assembly", JsonValue.String("Assembly-CSharp")),
                    ("guid", JsonValue.String("abc123"))))))
            .Add(Obj(("index", JsonValue.Integer(1)), ("op", JsonValue.String("setProperties")),
                ("result", Obj(
                    ("path", JsonValue.String("Assets/Data/Sword.asset")),
                    ("applied", JsonValue.NewArray().Add(JsonValue.String("displayName")).Add(JsonValue.String("damage"))),
                    ("failed", JsonValue.NewArray())))));

        var appliedAll = JsonValue.NewArray().Add(JsonValue.Integer(0)).Add(JsonValue.Integer(1));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", appliedAll),
            ("results", results),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("2 applied, 0 failed of 2 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scriptable_object_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["path"] = "Assets/Data/Sword.asset", ["type"] = "Game.Items.ItemConfig" },
                new Dictionary<string, object>
                {
                    ["op"] = "setProperties", ["path"] = "Assets/Data/Sword.asset",
                    ["values"] = new Dictionary<string, object> { ["displayName"] = "Iron Sword", ["damage"] = 12 },
                },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        // ONE wire call for the whole 2-operation spec.
        Assert.Equal("scriptableObject.apply", request.Method);
        var ops = Prop(request.Params!, "operations");
        Assert.Equal(2, ops.Items.Count);

        Assert.Equal("Assets/Data/Sword.asset", Prop(ops.Items[0], "path").AsString());
        Assert.Equal("Game.Items.ItemConfig", Prop(ops.Items[0], "type").AsString());

        Assert.Equal("Assets/Data/Sword.asset", Prop(ops.Items[1], "path").AsString());
        Assert.Equal("Iron Sword", Prop(Prop(ops.Items[1], "values"), "displayName").AsString());
        Assert.Equal(12.0, Prop(Prop(ops.Items[1], "values"), "damage").AsDouble());

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 0, 1 }, applied);
        Assert.Equal(0, structured.GetProperty("failed").GetArrayLength());
        Assert.Contains("2", structured.GetProperty("summary").GetString());

        var resultsEl = structured.GetProperty("results");
        Assert.Equal(2, resultsEl.GetArrayLength());
        Assert.Equal("create", resultsEl[0].GetProperty("op").GetString());
        Assert.Equal("abc123", resultsEl[0].GetProperty("result").GetProperty("guid").GetString());
        Assert.Equal("Assembly-CSharp", resultsEl[0].GetProperty("result").GetProperty("assembly").GetString());
    }

    // ---------------------------------------------------------------- partial failure: mapped from the wire, no rollback

    [Fact]
    public async Task PartialFailure_WirePerOperationFailure_MapsIndexOpAndError_StillOneWireCall()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray().Add(JsonValue.Integer(0))),
            ("results", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(0)), ("op", JsonValue.String("create")),
                ("result", Obj(("path", JsonValue.String("Assets/Data/Sword.asset")), ("type", JsonValue.String("ItemConfig")),
                    ("assembly", JsonValue.String("Assembly-CSharp")), ("guid", JsonValue.String("abc"))))))),
            ("failed", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(1)), ("op", JsonValue.String("setProperties")),
                ("error", JsonValue.String("'Assets/Ghost.asset' is a Material, not a ScriptableObject - use material_apply instead."))))),
            ("summary", JsonValue.String("1 applied, 1 failed of 2 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scriptable_object_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["path"] = "Assets/Data/Sword.asset", ["type"] = "ItemConfig" },
                new Dictionary<string, object>
                {
                    ["op"] = "setProperties", ["path"] = "Assets/Ghost.asset",
                    ["values"] = new Dictionary<string, object> { ["x"] = 1 },
                },
            },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("scriptableObject.apply", request.Method);
        Assert.Equal(2, Prop(request.Params!, "operations").Items.Count);

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 0 }, applied);

        var failed = structured.GetProperty("failed");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.Equal(1, failed[0].GetProperty("index").GetInt32());
        Assert.Equal("setProperties", failed[0].GetProperty("op").GetString());
        Assert.Contains("not a ScriptableObject", failed[0].GetProperty("error").GetString());

        var results = structured.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal(0, results[0].GetProperty("index").GetInt32());
        Assert.Equal("create", results[0].GetProperty("op").GetString());
        Assert.Equal("Assets/Data/Sword.asset", results[0].GetProperty("result").GetProperty("path").GetString());
    }

    // ---------------------------------------------------------------- whole-call (plugin-level) failure still propagates

    [Fact]
    public async Task ScriptableObjectApply_PluginLevelError_PropagatesAsToolError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes, "scriptableObject.apply requires an 'operations' array parameter.");

        var envelope = await McpTestClient.CallTool(Factory, "scriptable_object_apply", new
        {
            operations = new[] { new Dictionary<string, object> { ["op"] = "create", ["path"] = "Assets/Foo.asset", ["type"] = "ItemConfig" } },
        });
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("scriptableObject.apply requires an 'operations' array parameter.", McpTestClient.ErrorText(envelope));
    }
}
