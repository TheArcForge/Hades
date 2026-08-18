using System.Text.Json;
using Hades.Contract.Wire;
using Microsoft.AspNetCore.Mvc.Testing;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Tests;

/// <summary>
/// animation_apply: the declarative batch that replaces EditorAnimationTools' four tools
/// (animation_assign_controller, animation_assign_clip, animation_create_controller,
/// animation_edit_controller). Same scope discipline as SceneApplyTests: this proves the
/// tool-to-wire contract - including that the app sends 'controllerPath' VERBATIM for every op
/// (the app does a straight field copy, no renaming - see AnimationApplyTool's own class doc
/// comment for why the 'controllerPath' -&gt; 'path' rename for createController/editController
/// happens on the PLUGIN side instead) - not Undo/no-lease/real ordering, which are plugin-side
/// properties proven against a real Editor in Plugin~/Tests/Editor/AnimationApplyCommandsTests.cs.
/// </summary>
public sealed class AnimationApplyTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
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
    public async Task AnimationApply_EmptyOperationsArray_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "animation_apply", new { operations = Array.Empty<object>() });

        Assert.Contains("operations", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task AnimationApply_UnknownOp_RejectsWholeCallBeforeDispatchingAnything_ListsValidOps()
    {
        var envelope = await McpTestClient.CallTool(Factory, "animation_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "assignController", ["gameObjectPath"] = "Player", ["controllerPath"] = "Assets/Player.controller" },
                new Dictionary<string, object> { ["op"] = "frobnicate", ["controllerPath"] = "Assets/Player.controller" },
            },
        });

        var text = McpTestClient.ErrorText(envelope);
        Assert.Contains("frobnicate", text);
        Assert.Contains("operations[1]", text);
        foreach (var op in new[] { "assignController", "assignClip", "createController", "editController" })
            Assert.Contains(op, text);
    }

    // ---------------------------------------------------------------- unknown FIELD refused before any wire call (per-op, not per-tool)

    /// <summary>Enumerates EVERY op animation_apply accepts (not a spot check) and proves each one,
    /// individually, refuses an unknown field before any wire call.</summary>
    [Fact]
    public async Task AnimationApply_UnknownField_RejectedForEveryOp()
    {
        foreach (var op in new[] { "assignController", "assignClip", "createController", "editController" })
        {
            var envelope = await McpTestClient.CallTool(Factory, "animation_apply", new
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
    public async Task AnimationApply_FullOperationSweep_SendsOneWireCallWithEveryFieldTranslated_ControllerPathVerbatimForAllFourOps()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var results = JsonValue.NewArray()
            .Add(Obj(("index", JsonValue.Integer(0)), ("op", JsonValue.String("assignController")),
                ("result", Obj(("gameObject", JsonValue.String("Player")), ("controller", JsonValue.String("Assets/Player.controller")), ("addedAnimator", JsonValue.Bool(true))))))
            .Add(Obj(("index", JsonValue.Integer(1)), ("op", JsonValue.String("assignClip")),
                ("result", Obj(("controller", JsonValue.String("Assets/Player.controller")), ("state", JsonValue.String("Idle")), ("clip", JsonValue.String("Assets/Idle.anim"))))))
            .Add(Obj(("index", JsonValue.Integer(2)), ("op", JsonValue.String("createController")),
                ("result", Obj(
                    ("path", JsonValue.String("Assets/New.controller")), ("parameterCount", JsonValue.Integer(1)),
                    ("stateCount", JsonValue.Integer(1)), ("transitionCount", JsonValue.Integer(0)),
                    ("stateNames", JsonValue.NewArray().Add(JsonValue.String("Idle"))), ("errors", JsonValue.NewArray())))))
            .Add(Obj(("index", JsonValue.Integer(3)), ("op", JsonValue.String("editController")),
                ("result", Obj(
                    ("path", JsonValue.String("Assets/New.controller")),
                    ("added", JsonValue.NewArray().Add(JsonValue.String("parameter:Speed"))),
                    ("removed", JsonValue.NewArray()),
                    ("errors", JsonValue.NewArray())))));

        var appliedAll = JsonValue.NewArray();
        for (var i = 0; i < 4; i++) appliedAll.Add(JsonValue.Integer(i));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", appliedAll),
            ("results", results),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("4 applied, 0 failed of 4 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "animation_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "assignController", ["gameObjectPath"] = "Player", ["controllerPath"] = "Assets/Player.controller" },
                new Dictionary<string, object> { ["op"] = "assignClip", ["controllerPath"] = "Assets/Player.controller", ["stateName"] = "Idle", ["clipPath"] = "Assets/Idle.anim" },
                new Dictionary<string, object>
                {
                    ["op"] = "createController", ["controllerPath"] = "Assets/New.controller",
                    ["parameters"] = new[] { new Dictionary<string, object> { ["name"] = "Speed", ["type"] = "Float", ["default"] = 0 } },
                    ["states"] = new[] { new Dictionary<string, object> { ["name"] = "Idle", ["isDefault"] = true } },
                    ["transitions"] = new[] { new Dictionary<string, object> { ["from"] = "AnyState", ["to"] = "Idle" } },
                },
                new Dictionary<string, object>
                {
                    ["op"] = "editController", ["controllerPath"] = "Assets/New.controller",
                    ["addParameters"] = new[] { new Dictionary<string, object> { ["name"] = "Speed2", ["type"] = "Float" } },
                    ["removeStates"] = new[] { "Idle" },
                },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("animation.apply", request.Method);
        var ops = Prop(request.Params!, "operations");
        Assert.Equal(4, ops.Items.Count);

        // 'controllerPath' verbatim on EVERY op - including createController/editController, whose
        // underlying single-purpose wire commands actually call this field 'path'. The rename is a
        // PLUGIN-side responsibility (AnimationApplyCommands) - the app never touches it.
        Assert.Equal("Assets/Player.controller", Prop(ops.Items[0], "controllerPath").AsString());
        Assert.Equal("Player", Prop(ops.Items[0], "gameObjectPath").AsString());
        Assert.False(ops.Items[0].TryGetProperty("path", out _));

        Assert.Equal("Assets/Player.controller", Prop(ops.Items[1], "controllerPath").AsString());
        Assert.Equal("Idle", Prop(ops.Items[1], "stateName").AsString());
        Assert.Equal("Assets/Idle.anim", Prop(ops.Items[1], "clipPath").AsString());

        Assert.Equal("Assets/New.controller", Prop(ops.Items[2], "controllerPath").AsString());
        Assert.False(ops.Items[2].TryGetProperty("path", out _));
        var createParams = Prop(ops.Items[2], "parameters");
        Assert.Equal("Speed", Prop(createParams.Items[0], "name").AsString());
        Assert.Equal("Float", Prop(createParams.Items[0], "type").AsString());
        var createStates = Prop(ops.Items[2], "states");
        Assert.Equal("Idle", Prop(createStates.Items[0], "name").AsString());
        Assert.True(Prop(createStates.Items[0], "isDefault").AsBoolean());
        var createTransitions = Prop(ops.Items[2], "transitions");
        Assert.Equal("AnyState", Prop(createTransitions.Items[0], "from").AsString());
        Assert.Equal("Idle", Prop(createTransitions.Items[0], "to").AsString());

        Assert.Equal("Assets/New.controller", Prop(ops.Items[3], "controllerPath").AsString());
        Assert.False(ops.Items[3].TryGetProperty("path", out _));
        var addParams = Prop(ops.Items[3], "addParameters");
        Assert.Equal("Speed2", Prop(addParams.Items[0], "name").AsString());
        var removeStates = Prop(ops.Items[3], "removeStates");
        Assert.Equal("Idle", removeStates.Items[0].AsString());

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(0, 4), applied);
        Assert.Equal(0, structured.GetProperty("failed").GetArrayLength());
        Assert.Contains("4", structured.GetProperty("summary").GetString());

        var resultsEl = structured.GetProperty("results");
        Assert.Equal(4, resultsEl.GetArrayLength());
        Assert.Equal("editController", resultsEl[3].GetProperty("op").GetString());
        Assert.Contains("parameter:Speed", resultsEl[3].GetProperty("result").GetProperty("added")[0].GetString());
    }

    [Fact]
    public async Task RemoveTransitions_ByFromTo_TranslatedVerbatim()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray().Add(JsonValue.Integer(0))),
            ("results", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(0)), ("op", JsonValue.String("editController")),
                ("result", Obj(("path", JsonValue.String("Assets/New.controller")), ("added", JsonValue.NewArray()),
                    ("removed", JsonValue.NewArray().Add(JsonValue.String("transition:AnyState->Idle (x1)"))), ("errors", JsonValue.NewArray())))))),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("1 applied, 0 failed of 1 operation(s)."))));

        Structured(await McpTestClient.CallTool(Factory, "animation_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object>
                {
                    ["op"] = "editController", ["controllerPath"] = "Assets/New.controller",
                    ["removeTransitions"] = new[] { new Dictionary<string, object> { ["from"] = "AnyState", ["to"] = "Idle" } },
                },
            },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        var op = Prop(request.Params!, "operations").Items[0];
        var removeTransitions = Prop(op, "removeTransitions");
        Assert.Equal("AnyState", Prop(removeTransitions.Items[0], "from").AsString());
        Assert.Equal("Idle", Prop(removeTransitions.Items[0], "to").AsString());
    }

    // ---------------------------------------------------------------- partial failure: mapped from the wire, no rollback

    [Fact]
    public async Task PartialFailure_WirePerOperationFailure_MapsIndexOpAndError_StillOneWireCall()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray().Add(JsonValue.Integer(0))),
            ("results", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(0)), ("op", JsonValue.String("assignController")),
                ("result", Obj(("gameObject", JsonValue.String("Player")), ("controller", JsonValue.String("Assets/Player.controller")), ("addedAnimator", JsonValue.Bool(false))))))),
            ("failed", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(1)), ("op", JsonValue.String("assignClip")),
                ("error", JsonValue.String("State 'Ghost' not found in controller. Available states: Idle."))))),
            ("summary", JsonValue.String("1 applied, 1 failed of 2 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "animation_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "assignController", ["gameObjectPath"] = "Player", ["controllerPath"] = "Assets/Player.controller" },
                new Dictionary<string, object> { ["op"] = "assignClip", ["controllerPath"] = "Assets/Player.controller", ["stateName"] = "Ghost", ["clipPath"] = "Assets/Idle.anim" },
            },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("animation.apply", request.Method);
        Assert.Equal(2, Prop(request.Params!, "operations").Items.Count);

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 0 }, applied);

        var failed = structured.GetProperty("failed");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.Equal(1, failed[0].GetProperty("index").GetInt32());
        Assert.Equal("assignClip", failed[0].GetProperty("op").GetString());
        Assert.Contains("Ghost", failed[0].GetProperty("error").GetString());

        // The unified partial-batch shape: 'results' surfaces an entry (with the op's own result
        // payload) for the APPLIED op, faithfully mapped from the wire - not just a bare index.
        var results = structured.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal(0, results[0].GetProperty("index").GetInt32());
        Assert.Equal("assignController", results[0].GetProperty("op").GetString());
        Assert.Equal("Player", results[0].GetProperty("result").GetProperty("gameObject").GetString());
    }

    // ---------------------------------------------------------------- whole-call (plugin-level) failure still propagates

    [Fact]
    public async Task AnimationApply_PluginLevelError_PropagatesAsToolError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes, "animation.apply requires an 'operations' array parameter.");

        var envelope = await McpTestClient.CallTool(Factory, "animation_apply", new
        {
            operations = new[] { new Dictionary<string, object> { ["op"] = "assignController", ["gameObjectPath"] = "Player", ["controllerPath"] = "Assets/Player.controller" } },
        });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("animation.apply requires an 'operations' array parameter.", McpTestClient.ErrorText(envelope));
    }

    // ---------------------------------------------------------------- the property that broke: one field name, every op

    /// <summary>Asserts directly - not by trusting the implementation - that 'controllerPath' is
    /// accepted by EVERY op (assignController, assignClip, createController, editController),
    /// verbatim on the wire in every case (the plugin, not the app, renames it to 'path' for
    /// createController/editController - see AnimationApplyTool's own class doc comment).
    /// 'gameObjectPath' (assignController's scene object, never a file on disk) is a genuinely
    /// distinct concept and keeps its own name. Unlike material_apply/prefab_apply, animation_apply
    /// already had this property before the sibling tools' fix - this test locks it in rather than
    /// just trusting the doc comment's claim.</summary>
    [Fact]
    public async Task AnimationApply_ControllerPathIsAcceptedByEveryOp()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        const string path = "Assets/_E2E10/Shared.controller";
        string[] opNames = ["assignController", "assignClip", "createController", "editController"];
        var appliedAll = JsonValue.NewArray();
        var results = JsonValue.NewArray();
        for (var i = 0; i < opNames.Length; i++)
        {
            appliedAll.Add(JsonValue.Integer(i));
            results.Add(Obj(("index", JsonValue.Integer(i)), ("op", JsonValue.String(opNames[i])), ("result", JsonValue.NewObject())));
        }

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", appliedAll), ("results", results), ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("4 applied, 0 failed of 4 operation(s)."))));

        Structured(await McpTestClient.CallTool(Factory, "animation_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "assignController", ["gameObjectPath"] = "Player", ["controllerPath"] = path },
                new Dictionary<string, object> { ["op"] = "assignClip", ["controllerPath"] = path, ["stateName"] = "Idle", ["clipPath"] = "Assets/Idle.anim" },
                new Dictionary<string, object> { ["op"] = "createController", ["controllerPath"] = path },
                new Dictionary<string, object> { ["op"] = "editController", ["controllerPath"] = path, ["removeStates"] = new[] { "Idle" } },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));
        var ops = Prop(request.Params!, "operations");
        Assert.Equal(4, ops.Items.Count);

        for (var i = 0; i < ops.Items.Count; i++)
        {
            Assert.Equal(path, Prop(ops.Items[i], "controllerPath").AsString());
            Assert.False(ops.Items[i].TryGetProperty("path", out _), $"operations[{i}] should not carry a 'path' key - the rename to 'path' is the PLUGIN's job, not the app's.");
        }
    }
}
