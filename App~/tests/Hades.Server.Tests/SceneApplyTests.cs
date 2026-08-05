using System.Text.Json;
using Hades.Contract.Wire;
using Microsoft.AspNetCore.Mvc.Testing;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Tests;

/// <summary>
/// scene_apply: the declarative batch that replaces EditorSceneTools' six tools (scene_create_
/// gameobject, scene_create_primitive, scene_delete_gameobject, scene_reparent_gameobject,
/// scene_rename_gameobject, scene_setup) and EditorComponentTools' seven (component_add,
/// component_remove, component_set_property, component_set_properties, reference_set,
/// event_add_listener, event_remove_listener) - 13 tools - plus inspector_select's "select" op.
///
/// Plan 10 Task 1 shipped the plugin-side <c>scene.apply</c> wire command (Hades.Tools.
/// SceneApplyCommands, Plugin~) that applies the WHOLE batch inside one handler body, one Undo
/// group - so this tool now sends the entire 'operations' array in ONE EditorProxy.SendCommandAsync
/// round trip, not one per operation as an earlier version of this tool did. This file proves the
/// tool-to-wire contract at THAT shape: one wire call, every operation's fields translated verbatim
/// (see SceneApplyTool.BuildOperation - field names are IDENTICAL on both sides of the wire, no
/// renaming, unlike the old per-op-routing version), and the plugin's applied/failed/summary result
/// mapped straight back. Same scope discipline as EditorSceneToolsTests/EditorComponentToolsTests:
/// this proves the tool-to-wire contract, not Undo/no-lease/real ordering, which are plugin-side
/// properties this file cannot observe (no real Undo stack or Editor on this side of the wire) -
/// those are proven against a real Editor in Plugin~/Tests/Editor/SceneApplyCommandsTests.cs.
/// </summary>
public sealed class SceneApplyTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
{
    static JsonValue Obj(params (string Key, JsonValue Value)[] members)
    {
        var o = JsonValue.NewObject();
        foreach (var (key, value) in members) o.SetProperty(key, value);
        return o;
    }

    /// <summary>WireJson (Hades.Contract.Wire.JsonValue) has TryGetProperty, not GetProperty -
    /// the terse required-property accessor every other test file in this suite spells out inline.</summary>
    static JsonValue Prop(JsonValue obj, string key)
    {
        Assert.True(obj.TryGetProperty(key, out var value), $"expected wire param '{key}', got: {obj}");
        return value!;
    }

    // ---------------------------------------------------------------- structural validation (no Editor needed)

    [Fact]
    public async Task SceneApply_EmptyOperationsArray_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "scene_apply", new { operations = Array.Empty<object>() });

        Assert.Contains("operations", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task SceneApply_UnknownOp_RejectsWholeCallBeforeDispatchingAnything_ListsValidOps()
    {
        // No fake Unity connects at all - proves the whole call is refused before EditorProxy is
        // ever touched, zero wire calls, the same "refused, not ignored" shape RejectUnknownParams
        // established for an unrecognised lease.* parameter (CommandTable.cs).
        var envelope = await McpTestClient.CallTool(Factory, "scene_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["name"] = "Enemy" },
                new Dictionary<string, object> { ["op"] = "frobnicate", ["target"] = "Enemy" },
            },
        });

        var text = McpTestClient.ErrorText(envelope);
        Assert.Contains("frobnicate", text);
        Assert.Contains("operations[1]", text);
        // Every valid op should be named so the caller can self-correct without guessing.
        foreach (var op in new[] { "create", "addComponent", "setProperties", "setReference", "removeComponent", "addListener", "removeListener", "delete", "reparent", "rename", "select" })
            Assert.Contains(op, text);
    }

    // ---------------------------------------------------------------- unknown FIELD refused before any wire call (per-op, not per-tool)

    /// <summary>Field validity is PER-OP, not per-tool, even for fields the record's own "//"
    /// source comment groups TOGETHER for readability: 'type' sits in the SAME comment block as
    /// 'target'/'component' ("addComponent / removeComponent / setProperties / setReference / ...
    /// / select"), but the tool's own accepted-fields documentation makes clear only addComponent/
    /// removeComponent actually use 'type' - setProperties does not. Sending 'type' on setProperties
    /// must be refused exactly like any other sibling op's field - see OperationFieldValidator's own
    /// doc comment for why the record's loose comment grouping cannot drive this table.</summary>
    [Fact]
    public async Task SceneApply_FieldFromSiblingOp_IsRejected_EvenWhenSourceCommentGroupsThemTogether()
    {
        var envelope = await McpTestClient.CallTool(Factory, "scene_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object>
                {
                    ["op"] = "setProperties", ["target"] = "Enemy", ["component"] = "Health",
                    ["values"] = new Dictionary<string, object> { ["maxHealth"] = 100 },
                    ["type"] = "Health", // addComponent/removeComponent-only, NOT setProperties
                },
            },
        });

        var text = McpTestClient.ErrorText(envelope);
        Assert.Contains("operations[0]", text);
        Assert.Contains("'type'", text);
    }

    /// <summary>Enumerates EVERY op scene_apply accepts (not a spot check) and proves each one,
    /// individually, refuses an unknown field before any wire call.</summary>
    [Fact]
    public async Task SceneApply_UnknownField_RejectedForEveryOp()
    {
        foreach (var op in new[] { "create", "addComponent", "setProperties", "setReference", "removeComponent", "addListener", "removeListener", "delete", "reparent", "rename", "select" })
        {
            var envelope = await McpTestClient.CallTool(Factory, "scene_apply", new
            {
                operations = new[] { new Dictionary<string, object> { ["op"] = op, ["zzzNotAField"] = "x" } },
            });

            var text = McpTestClient.ErrorText(envelope);
            Assert.True(text.Contains("'zzzNotAField'") && text.Contains("operations[0]"),
                $"op '{op}' did not refuse an unknown field as expected. Got: {text}");
        }
    }

    // ---------------------------------------------------------------- one wire call, every field translated verbatim

    /// <summary>
    /// One scene_apply call touching every op this tool supports, with every optional field this op
    /// vocabulary defines populated at least once somewhere in the spec - the "no capability lost,
    /// no field silently dropped in translation" proof, now that a single wire call (not per-op
    /// routing to different wire methods) carries the whole thing. Two 'create' entries cover both
    /// shapes scene_apply's create supports (primitive; plain-with-tag/layer/transform) without
    /// combining them in one entry - that combination is refused, covered separately below - and two
    /// 'setReference' entries cover both the asset-path and scene-object flavours.
    /// </summary>
    [Fact]
    public async Task SceneApply_FullOperationSweep_SendsOneWireCallWithEveryFieldTranslated_MapsAppliedAndSummary()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var appliedAll = JsonValue.NewArray();
        for (var i = 0; i < 13; i++) appliedAll.Add(JsonValue.Integer(i));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", appliedAll),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("13 applied, 0 failed of 13 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["name"] = "Enemy", ["primitive"] = "Cube" },
                new Dictionary<string, object>
                {
                    ["op"] = "create", ["name"] = "Tagged", ["parent"] = "Spawns", ["tag"] = "Player", ["layer"] = "Default",
                    ["position"] = new[] { 1f, 2f, 3f }, ["rotation"] = new[] { 0f, 90f, 0f }, ["scale"] = new[] { 2f, 2f, 2f },
                },
                new Dictionary<string, object> { ["op"] = "addComponent", ["target"] = "Spawns/Enemy", ["type"] = "Health" },
                new Dictionary<string, object> { ["op"] = "removeComponent", ["target"] = "Spawns/Enemy", ["type"] = "BoxCollider" },
                new Dictionary<string, object> { ["op"] = "setProperties", ["target"] = "Spawns/Enemy", ["component"] = "Health", ["values"] = new Dictionary<string, object> { ["maxHealth"] = 100 } },
                new Dictionary<string, object> { ["op"] = "setReference", ["target"] = "Spawns/Enemy", ["component"] = "Health", ["property"] = "damageConfig", ["value"] = "Assets/Demo/DamageConfig.asset" },
                new Dictionary<string, object> { ["op"] = "setReference", ["target"] = "Spawns/Enemy", ["component"] = "Health", ["property"] = "manager", ["targetPath"] = "Spawns/Manager", ["targetComponentType"] = "Manager" },
                new Dictionary<string, object> { ["op"] = "addListener", ["target"] = "Spawns/Enemy", ["component"] = "Health", ["event"] = "onDeath", ["targetObject"] = "Spawns/Manager", ["method"] = "HandleDeath", ["argument"] = "boom", ["argumentType"] = "string" },
                new Dictionary<string, object> { ["op"] = "removeListener", ["target"] = "Spawns/Enemy", ["component"] = "Health", ["event"] = "onDeath", ["index"] = 0 },
                new Dictionary<string, object> { ["op"] = "delete", ["target"] = "Spawns/Old" },
                new Dictionary<string, object> { ["op"] = "reparent", ["target"] = "Spawns/Enemy", ["newParent"] = "Active" },
                new Dictionary<string, object> { ["op"] = "rename", ["target"] = "Active/Enemy", ["newName"] = "Enemy_01" },
                new Dictionary<string, object> { ["op"] = "select", ["target"] = "Active/Enemy_01" },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        // ONE wire call for the whole 13-operation spec - the entire point of this task.
        Assert.Equal("scene.apply", request.Method);
        var ops = Prop(request.Params!, "operations");
        Assert.Equal(13, ops.Items.Count);

        Assert.Equal("create", Prop(ops.Items[0], "op").AsString());
        Assert.Equal("Cube", Prop(ops.Items[0], "primitive").AsString());
        Assert.False(ops.Items[0].TryGetProperty("tag", out _));

        var taggedOp = ops.Items[1];
        Assert.Equal("Spawns", Prop(taggedOp, "parent").AsString());
        Assert.Equal("Player", Prop(taggedOp, "tag").AsString());
        Assert.Equal("Default", Prop(taggedOp, "layer").AsString());
        Assert.Equal(3, Prop(taggedOp, "position").Items.Count);
        Assert.Equal(1.0, Prop(taggedOp, "position").Items[0].AsDouble());
        Assert.Equal(90.0, Prop(taggedOp, "rotation").Items[1].AsDouble());
        Assert.Equal(2.0, Prop(taggedOp, "scale").Items[0].AsDouble());
        Assert.False(taggedOp.TryGetProperty("primitive", out _));

        Assert.Equal("Spawns/Enemy", Prop(ops.Items[2], "target").AsString());
        Assert.Equal("Health", Prop(ops.Items[2], "type").AsString());
        Assert.Equal("BoxCollider", Prop(ops.Items[3], "type").AsString());

        var mh = Prop(Prop(ops.Items[4], "values"), "maxHealth");
        Assert.Equal(100, mh.AsInteger());

        Assert.Equal("Assets/Demo/DamageConfig.asset", Prop(ops.Items[5], "value").AsString());
        Assert.False(ops.Items[5].TryGetProperty("targetPath", out _));

        Assert.Equal("Spawns/Manager", Prop(ops.Items[6], "targetPath").AsString());
        Assert.Equal("Manager", Prop(ops.Items[6], "targetComponentType").AsString());
        Assert.False(ops.Items[6].TryGetProperty("value", out _));

        Assert.Equal("Spawns/Manager", Prop(ops.Items[7], "targetObject").AsString());
        Assert.Equal("HandleDeath", Prop(ops.Items[7], "method").AsString());
        Assert.Equal("boom", Prop(ops.Items[7], "argument").AsString());
        Assert.Equal("string", Prop(ops.Items[7], "argumentType").AsString());

        Assert.Equal("onDeath", Prop(ops.Items[8], "event").AsString());
        Assert.Equal(0, Prop(ops.Items[8], "index").AsInteger());

        Assert.Equal("Spawns/Old", Prop(ops.Items[9], "target").AsString());
        Assert.Equal("Active", Prop(ops.Items[10], "newParent").AsString());
        Assert.Equal("Enemy_01", Prop(ops.Items[11], "newName").AsString());
        Assert.Equal("Active/Enemy_01", Prop(ops.Items[12], "target").AsString());

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(0, 13), applied);
        Assert.Equal(0, structured.GetProperty("failed").GetArrayLength());
        Assert.Contains("13", structured.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Create_PrimitiveCombinedWithTagOrLayer_StillSentAsOneOperation_PluginDecidesValidity()
    {
        // Unlike the old per-op-routing version, this tool no longer decides whether primitive+tag
        // is valid - it just forwards the fields verbatim in the one wire call and reports whatever
        // the plugin's 'failed' entry says. Proves the field translation doesn't silently drop
        // either field even for a combination the plugin is expected to refuse.
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray()),
            ("failed", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(0)), ("op", JsonValue.String("create")),
                ("error", JsonValue.String("scene_apply create: 'tag'/'layer' cannot be combined with 'primitive' - "
                    + "creating a primitive does not support setting a tag or layer at creation time."))))),
            ("summary", JsonValue.String("0 applied, 1 failed of 1 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_apply", new
        {
            operations = new[] { new Dictionary<string, object> { ["op"] = "create", ["name"] = "Enemy", ["primitive"] = "Cube", ["tag"] = "Player" } },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        var sentOp = Prop(request.Params!, "operations").Items[0];
        Assert.Equal("Cube", Prop(sentOp, "primitive").AsString());
        Assert.Equal("Player", Prop(sentOp, "tag").AsString());

        Assert.Equal(0, structured.GetProperty("applied").GetArrayLength());
        var failed = structured.GetProperty("failed");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.Contains("primitive", failed[0].GetProperty("error").GetString());
    }

    // ---------------------------------------------------------------- ordering still expressible in one call

    [Fact]
    public async Task Create_SecondOperationParentsUnderFirst_SentInOneWireCallInOrder()
    {
        // scene_setup's 'children' array nested a whole sub-tree inside one wire call; scene_apply
        // has no such nesting, but "operations apply in order, so a later one can reference an
        // object an earlier one created" reproduces the identical tree: a second 'create' whose
        // 'parent' names the first one's own path - both entries travel in the SAME wire call, in
        // the SAME order, for the plugin to apply sequentially.
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray().Add(JsonValue.Integer(0)).Add(JsonValue.Integer(1))),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("2 applied, 0 failed of 2 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["name"] = "Root" },
                new Dictionary<string, object> { ["op"] = "create", ["name"] = "Child", ["parent"] = "Root" },
            },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        var ops = Prop(request.Params!, "operations");
        Assert.Equal(2, ops.Items.Count);
        Assert.Equal("Root", Prop(ops.Items[0], "name").AsString());
        Assert.Equal("Child", Prop(ops.Items[1], "name").AsString());
        Assert.Equal("Root", Prop(ops.Items[1], "parent").AsString());
        Assert.Equal(2, structured.GetProperty("applied").GetArrayLength());
    }

    // ---------------------------------------------------------------- partial failure: mapped from the wire, no rollback

    [Fact]
    public async Task PartialFailure_WirePerOperationFailure_MapsIndexOpAndError_StillOneWireCall()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray().Add(JsonValue.Integer(0)).Add(JsonValue.Integer(2))),
            ("failed", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(1)), ("op", JsonValue.String("delete")),
                ("error", JsonValue.String("GameObject not found: 'Spawns/Old'. Root objects in the active scene: Enemy. Call scene_get_hierarchy to see the full tree."))))),
            ("summary", JsonValue.String("2 applied, 1 failed of 3 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["name"] = "Enemy" },
                new Dictionary<string, object> { ["op"] = "delete", ["target"] = "Spawns/Old" },
                new Dictionary<string, object> { ["op"] = "select", ["target"] = "Enemy" },
            },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        // All three operations travelled in the SAME single wire call - the failure of one does not
        // stop the app from sending the rest; that is entirely the plugin's job now.
        Assert.Equal("scene.apply", request.Method);
        Assert.Equal(3, Prop(request.Params!, "operations").Items.Count);

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 0, 2 }, applied);

        var failed = structured.GetProperty("failed");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.Equal(1, failed[0].GetProperty("index").GetInt32());
        Assert.Equal("delete", failed[0].GetProperty("op").GetString());
        // GameObjectPaths.NotFoundError shape preserved verbatim: names what exists.
        Assert.Contains("GameObject not found: 'Spawns/Old'", failed[0].GetProperty("error").GetString());
        Assert.Contains("Root objects in the active scene: Enemy", failed[0].GetProperty("error").GetString());
    }

    // ---------------------------------------------------------------- whole-call (plugin-level) failure still propagates

    [Fact]
    public async Task SceneApply_PluginLevelError_PropagatesAsToolError()
    {
        // A failure that is not per-operation at all (e.g. a malformed request the plugin rejects
        // outright) surfaces as an ordinary tool error, the same EditorProxy pass-through every
        // other Editor tool already relies on (EditorProxyTests proves the not-attached/busy/
        // timeout states once, thoroughly; this is the plugin-error smoke test for THIS tool).
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes, "scene.apply requires an 'operations' array parameter.");

        var envelope = await McpTestClient.CallTool(Factory, "scene_apply", new
        {
            operations = new[] { new Dictionary<string, object> { ["op"] = "create", ["name"] = "Enemy" } },
        });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("scene.apply requires an 'operations' array parameter.", McpTestClient.ErrorText(envelope));
    }
}
