using System.Text.Json;
using Hades.Contract.Wire;
using Microsoft.AspNetCore.Mvc.Testing;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Tests;

/// <summary>
/// prefab_apply: the declarative batch that replaces EditorPrefabTools' seven tools (prefab_create,
/// prefab_instantiate, prefab_apply_overrides, prefab_edit_property, prefab_open_editing,
/// prefab_save_editing, prefab_create_variant) - minus open/save, which are deliberately not part of
/// this op vocabulary (see PrefabApplyTool's own class doc comment for why). Same scope discipline
/// as SceneApplyTests: this proves the tool-to-wire contract, not Undo/lease/real ordering, which
/// are plugin-side properties proven against a real Editor in
/// UnityPlugin/Tests/Editor/PrefabApplyCommandsTests.cs.
/// </summary>
public sealed class PrefabApplyTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
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
    public async Task PrefabApply_EmptyOperationsArray_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "prefab_apply", new { operations = Array.Empty<object>() });

        Assert.Contains("operations", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task PrefabApply_UnknownOp_RejectsWholeCallBeforeDispatchingAnything_ListsValidOps()
    {
        var envelope = await McpTestClient.CallTool(Factory, "prefab_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["gameObjectPath"] = "Widget", ["prefabPath"] = "Assets/Widget.prefab" },
                new Dictionary<string, object> { ["op"] = "frobnicate", ["gameObjectPath"] = "Widget" },
            },
        });

        var text = McpTestClient.ErrorText(envelope);
        Assert.Contains("frobnicate", text);
        Assert.Contains("operations[1]", text);
        foreach (var op in new[] { "create", "instantiate", "applyOverrides", "editProperty", "createVariant" })
            Assert.Contains(op, text);
        // The removed footgun: no 'open'/'save' op exists to reject-list in the first place.
        Assert.DoesNotContain("openEditing", text);
        Assert.DoesNotContain("saveEditing", text);
    }

    // ---------------------------------------------------------------- unknown FIELD refused before any wire call (per-op, not per-tool)

    /// <summary>Enumerates EVERY op prefab_apply accepts (not a spot check) and proves each one,
    /// individually, refuses an unknown field before any wire call.</summary>
    [Fact]
    public async Task PrefabApply_UnknownField_RejectedForEveryOp()
    {
        foreach (var op in new[] { "create", "instantiate", "applyOverrides", "editProperty", "createVariant" })
        {
            var envelope = await McpTestClient.CallTool(Factory, "prefab_apply", new
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
    public async Task PrefabApply_FullOperationSweep_SendsOneWireCallWithEveryFieldTranslated_MapsAppliedAndSummary()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var results = JsonValue.NewArray()
            .Add(Obj(("index", JsonValue.Integer(0)), ("op", JsonValue.String("create")),
                ("result", Obj(("createdAsset", JsonValue.String("Assets/Widget.prefab")), ("guid", JsonValue.String("guid1"))))))
            .Add(Obj(("index", JsonValue.Integer(1)), ("op", JsonValue.String("instantiate")),
                ("result", Obj(("name", JsonValue.String("Widget")), ("path", JsonValue.String("Widget")), ("fileId", JsonValue.Integer(12345))))))
            .Add(Obj(("index", JsonValue.Integer(2)), ("op", JsonValue.String("applyOverrides")),
                ("result", Obj(
                    ("applied", JsonValue.String("Widget")),
                    ("sourcePrefab", JsonValue.String("Assets/Widget.prefab")),
                    ("unappliedProperties", JsonValue.NewArray().Add(JsonValue.String("m_Name")).Add(JsonValue.String("m_LocalPosition.x"))),
                    ("note", JsonValue.String("'unappliedProperties' lists only this prefab instance's own root-level 'default override' properties..."))))))
            .Add(Obj(("index", JsonValue.Integer(3)), ("op", JsonValue.String("editProperty")),
                ("result", Obj(
                    ("prefab", JsonValue.String("Assets/Widget.prefab")), ("component", JsonValue.String("Transform")),
                    ("property", JsonValue.String("m_LocalPosition")), ("newValue", JsonValue.Integer(1)),
                    ("savedImmediately", JsonValue.Bool(true))))))
            .Add(Obj(("index", JsonValue.Integer(4)), ("op", JsonValue.String("createVariant")),
                ("result", Obj(("basePrefab", JsonValue.String("Assets/Widget.prefab")), ("variant", JsonValue.String("Assets/WidgetVariant.prefab"))))));

        var appliedAll = JsonValue.NewArray();
        for (var i = 0; i < 5; i++) appliedAll.Add(JsonValue.Integer(i));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", appliedAll),
            ("results", results),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("5 applied, 0 failed of 5 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "prefab_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["gameObjectPath"] = "Widget", ["prefabPath"] = "Assets/Widget.prefab" },
                new Dictionary<string, object> { ["op"] = "instantiate", ["prefabPath"] = "Assets/Widget.prefab", ["parent"] = "Spawns" },
                new Dictionary<string, object> { ["op"] = "applyOverrides", ["gameObjectPath"] = "Widget" },
                new Dictionary<string, object> { ["op"] = "editProperty", ["prefabPath"] = "Assets/Widget.prefab", ["componentType"] = "Transform", ["propertyName"] = "m_LocalPosition", ["value"] = new Dictionary<string, object> { ["x"] = 1, ["y"] = 2, ["z"] = 3 }, ["gameObjectPath"] = "Child" },
                new Dictionary<string, object> { ["op"] = "createVariant", ["basePrefabPath"] = "Assets/Widget.prefab", ["variantPath"] = "Assets/WidgetVariant.prefab" },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("prefab.apply", request.Method);
        var ops = Prop(request.Params!, "operations");
        Assert.Equal(5, ops.Items.Count);

        Assert.Equal("Widget", Prop(ops.Items[0], "gameObjectPath").AsString());
        Assert.Equal("Assets/Widget.prefab", Prop(ops.Items[0], "assetPath").AsString());

        Assert.Equal("Assets/Widget.prefab", Prop(ops.Items[1], "prefabPath").AsString());
        Assert.Equal("Spawns", Prop(ops.Items[1], "parent").AsString());

        Assert.Equal("Widget", Prop(ops.Items[2], "gameObjectPath").AsString());

        Assert.Equal("Assets/Widget.prefab", Prop(ops.Items[3], "prefabPath").AsString());
        Assert.Equal("Transform", Prop(ops.Items[3], "componentType").AsString());
        Assert.Equal("m_LocalPosition", Prop(ops.Items[3], "propertyName").AsString());
        Assert.Equal(1.0, Prop(Prop(ops.Items[3], "value"), "x").AsDouble());
        Assert.Equal("Child", Prop(ops.Items[3], "gameObjectPath").AsString());

        Assert.Equal("Assets/Widget.prefab", Prop(ops.Items[4], "basePrefabPath").AsString());
        Assert.Equal("Assets/WidgetVariant.prefab", Prop(ops.Items[4], "variantPath").AsString());

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(0, 5), applied);
        Assert.Equal(0, structured.GetProperty("failed").GetArrayLength());
        Assert.Contains("5", structured.GetProperty("summary").GetString());

        // The carried-forward Plan 9 finding: applyOverrides' 'unappliedProperties'/'note' survive
        // verbatim in 'results' - never collapsed into a bare "applied" index, which alone would
        // read as blanket success.
        var resultsEl = structured.GetProperty("results");
        Assert.Equal(5, resultsEl.GetArrayLength());
        var overridesResult = resultsEl[2];
        Assert.Equal(2, overridesResult.GetProperty("index").GetInt32());
        Assert.Equal("applyOverrides", overridesResult.GetProperty("op").GetString());
        var overridesData = overridesResult.GetProperty("result");
        var unapplied = overridesData.GetProperty("unappliedProperties").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("m_Name", unapplied);
        Assert.Contains("m_LocalPosition.x", unapplied);
        Assert.False(string.IsNullOrEmpty(overridesData.GetProperty("note").GetString()));
    }

    // ---------------------------------------------------------------- open->edit(xN)->save workflow, reproduced without open/save

    [Fact]
    public async Task MultipleEditPropertyOps_OnTheSamePrefab_OneBatchedWireCall_ReproducesTheOldOpenEditNTimesSaveWorkflow()
    {
        // The old workflow this replaces: prefab_open_editing (one round trip), N x
        // prefab_edit_property against that open session (each 'savedImmediately'=false), then
        // prefab_save_editing. Here: N 'editProperty' ops targeting the SAME prefabPath in ONE
        // prefab_apply call - still one wire call, one Undo group, one reload lease acquire/release
        // for the whole batch (see PrefabApplyTool's own class doc comment) - net effect identical
        // (both property values end up written to the same prefab asset), with no window where a
        // caller can forget to save and leave the prefab stuck open.
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var results = JsonValue.NewArray()
            .Add(Obj(("index", JsonValue.Integer(0)), ("op", JsonValue.String("editProperty")),
                ("result", Obj(
                    ("prefab", JsonValue.String("Assets/Widget.prefab")), ("component", JsonValue.String("Transform")),
                    ("property", JsonValue.String("m_LocalPosition")), ("newValue", JsonValue.Integer(1)),
                    ("savedImmediately", JsonValue.Bool(true))))))
            .Add(Obj(("index", JsonValue.Integer(1)), ("op", JsonValue.String("editProperty")),
                ("result", Obj(
                    ("prefab", JsonValue.String("Assets/Widget.prefab")), ("component", JsonValue.String("Health")),
                    ("property", JsonValue.String("maxHealth")), ("newValue", JsonValue.Integer(200)),
                    ("savedImmediately", JsonValue.Bool(true))))));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray().Add(JsonValue.Integer(0)).Add(JsonValue.Integer(1))),
            ("results", results),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("2 applied, 0 failed of 2 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "prefab_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "editProperty", ["prefabPath"] = "Assets/Widget.prefab", ["componentType"] = "Transform", ["propertyName"] = "m_LocalPosition", ["value"] = 1 },
                new Dictionary<string, object> { ["op"] = "editProperty", ["prefabPath"] = "Assets/Widget.prefab", ["componentType"] = "Health", ["propertyName"] = "maxHealth", ["value"] = 200 },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        // ONE wire call carries both edits to the SAME prefab - not two separate open/edit/save
        // round trips, and definitely not a "prefab.open_editing" + "prefab.save_editing" pair.
        Assert.Equal("prefab.apply", request.Method);
        var ops = Prop(request.Params!, "operations");
        Assert.Equal(2, ops.Items.Count);
        Assert.Equal("Assets/Widget.prefab", Prop(ops.Items[0], "prefabPath").AsString());
        Assert.Equal("Assets/Widget.prefab", Prop(ops.Items[1], "prefabPath").AsString());
        Assert.Equal("Transform", Prop(ops.Items[0], "componentType").AsString());
        Assert.Equal("Health", Prop(ops.Items[1], "componentType").AsString());

        Assert.Equal(2, structured.GetProperty("applied").EnumerateArray().Count());
        Assert.Equal(0, structured.GetProperty("failed").GetArrayLength());
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
                ("result", Obj(("createdAsset", JsonValue.String("Assets/Widget.prefab")), ("guid", JsonValue.String("guid1"))))))),
            ("failed", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(1)), ("op", JsonValue.String("instantiate")),
                ("error", JsonValue.String("Prefab not found at 'Assets/Ghost.prefab'."))))),
            ("summary", JsonValue.String("1 applied, 1 failed of 2 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "prefab_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["gameObjectPath"] = "Widget", ["prefabPath"] = "Assets/Widget.prefab" },
                new Dictionary<string, object> { ["op"] = "instantiate", ["prefabPath"] = "Assets/Ghost.prefab" },
            },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("prefab.apply", request.Method);
        Assert.Equal(2, Prop(request.Params!, "operations").Items.Count);

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 0 }, applied);

        var failed = structured.GetProperty("failed");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.Equal(1, failed[0].GetProperty("index").GetInt32());
        Assert.Equal("instantiate", failed[0].GetProperty("op").GetString());
        Assert.Contains("Ghost.prefab", failed[0].GetProperty("error").GetString());

        // The unified partial-batch shape: 'results' surfaces an entry (with the op's own result
        // payload) for the APPLIED op, faithfully mapped from the wire - not just a bare index.
        var results = structured.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal(0, results[0].GetProperty("index").GetInt32());
        Assert.Equal("create", results[0].GetProperty("op").GetString());
        Assert.Equal("Assets/Widget.prefab", results[0].GetProperty("result").GetProperty("createdAsset").GetString());
    }

    // ---------------------------------------------------------------- whole-call (plugin-level) failure still propagates

    [Fact]
    public async Task PrefabApply_PluginLevelError_PropagatesAsToolError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes, "prefab.apply requires an 'operations' array parameter.");

        var envelope = await McpTestClient.CallTool(Factory, "prefab_apply", new
        {
            operations = new[] { new Dictionary<string, object> { ["op"] = "create", ["gameObjectPath"] = "Widget", ["prefabPath"] = "Assets/Widget.prefab" } },
        });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("prefab.apply requires an 'operations' array parameter.", McpTestClient.ErrorText(envelope));
    }

    // ---------------------------------------------------------------- the property that broke: one field name, every op

    /// <summary>Asserts directly - not by trusting the implementation - that 'prefabPath' is
    /// accepted by EVERY op that has a prefab ASSET of its own (create, instantiate, editProperty),
    /// each translated to whatever wire key that op's underlying plugin command actually expects.
    /// 'gameObjectPath' (a scene object, never a file on disk) is a genuinely distinct concept and
    /// keeps its own name, reused across create/applyOverrides/editProperty - see PrefabApplyTool's
    /// own class doc comment.</summary>
    [Fact]
    public async Task PrefabApply_PrefabPathIsAcceptedByEveryOpForTheAssetItActsOn()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        const string path = "Assets/_E2E10/Shared.prefab";
        string[] opNames = ["create", "instantiate", "editProperty"];
        var appliedAll = JsonValue.NewArray();
        var results = JsonValue.NewArray();
        for (var i = 0; i < opNames.Length; i++)
        {
            appliedAll.Add(JsonValue.Integer(i));
            results.Add(Obj(("index", JsonValue.Integer(i)), ("op", JsonValue.String(opNames[i])), ("result", JsonValue.NewObject())));
        }

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", appliedAll), ("results", results), ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("3 applied, 0 failed of 3 operation(s)."))));

        Structured(await McpTestClient.CallTool(Factory, "prefab_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["gameObjectPath"] = "Widget", ["prefabPath"] = path },
                new Dictionary<string, object> { ["op"] = "instantiate", ["prefabPath"] = path },
                new Dictionary<string, object> { ["op"] = "editProperty", ["prefabPath"] = path, ["componentType"] = "Transform", ["propertyName"] = "m_LocalPosition", ["value"] = 1 },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));
        var ops = Prop(request.Params!, "operations");
        Assert.Equal(3, ops.Items.Count);

        // create: 'prefabPath' -> wire 'assetPath' (the plugin's own wire contract for create,
        // unchanged - see PrefabApplyTool's own class doc comment for why this app-layer rename
        // exists).
        Assert.Equal(path, Prop(ops.Items[0], "assetPath").AsString());
        Assert.False(ops.Items[0].TryGetProperty("prefabPath", out _));

        // instantiate: 'prefabPath' -> wire 'prefabPath' (already matched)
        Assert.Equal(path, Prop(ops.Items[1], "prefabPath").AsString());

        // editProperty: 'prefabPath' -> wire 'prefabPath' (already matched)
        Assert.Equal(path, Prop(ops.Items[2], "prefabPath").AsString());
    }
}
