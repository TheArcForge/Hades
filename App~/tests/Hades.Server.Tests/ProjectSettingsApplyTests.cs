using System.Text.Json;
using Hades.Contract.Wire;
using Microsoft.AspNetCore.Mvc.Testing;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Tests;

/// <summary>
/// project_settings_apply: the declarative batch that replaces EditorTagLayerTools' tag_create/
/// tag_delete/layer_create, EditorSceneManagementTools' scene_set_build, and EditorAssetTools'
/// asset_set_import_settings/asset_set_clip_import_settings (6 tools). Same scope discipline as
/// MaterialApplyTests: this proves the tool-to-wire contract (one wire call, every operation's
/// fields translated verbatim, applied/results/failed/summary mapped back), not the one-lease-
/// window/self-managed-undo-group/real-per-op-behaviour properties, which are plugin-side
/// properties proven against a real Editor in
/// Plugin~/Tests/Editor/ProjectSettingsApplyCommandsTests.cs.
/// </summary>
public sealed class ProjectSettingsApplyTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
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
    public async Task ProjectSettingsApply_EmptyOperationsArray_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "project_settings_apply", new { operations = Array.Empty<object>() });

        Assert.Contains("operations", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task ProjectSettingsApply_UnknownOp_RejectsWholeCallBeforeDispatchingAnything_ListsValidOps()
    {
        var envelope = await McpTestClient.CallTool(Factory, "project_settings_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "createTag", ["name"] = "Friendly" },
                new Dictionary<string, object> { ["op"] = "frobnicate", ["name"] = "Nonsense" },
            },
        });

        var text = McpTestClient.ErrorText(envelope);
        Assert.Contains("frobnicate", text);
        Assert.Contains("operations[1]", text);
        foreach (var op in new[] { "createTag", "deleteTag", "createLayer", "setBuildScenes", "setImportSettings", "setClipImportSettings" })
            Assert.Contains(op, text);
    }

    // ---------------------------------------------------------------- unknown FIELD refused before any wire call (per-op, not per-tool)

    /// <summary>Enumerates EVERY op project_settings_apply accepts (not a spot check) and proves
    /// each one, individually, refuses an unknown field before any wire call.</summary>
    [Fact]
    public async Task ProjectSettingsApply_UnknownField_RejectedForEveryOp()
    {
        foreach (var op in new[] { "createTag", "deleteTag", "createLayer", "setBuildScenes", "setImportSettings", "setClipImportSettings" })
        {
            var envelope = await McpTestClient.CallTool(Factory, "project_settings_apply", new
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
    public async Task ProjectSettingsApply_FullOperationSweep_SendsOneWireCallWithEveryFieldTranslated_MapsAppliedAndSummary()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var results = JsonValue.NewArray()
            .Add(Obj(("index", JsonValue.Integer(0)), ("op", JsonValue.String("createTag")),
                ("result", Obj(("created", JsonValue.String("Friendly"))))))
            .Add(Obj(("index", JsonValue.Integer(1)), ("op", JsonValue.String("deleteTag")),
                ("result", Obj(("deleted", JsonValue.String("Obsolete"))))))
            .Add(Obj(("index", JsonValue.Integer(2)), ("op", JsonValue.String("createLayer")),
                ("result", Obj(("created", JsonValue.String("Hazard")), ("index", JsonValue.Integer(9))))))
            .Add(Obj(("index", JsonValue.Integer(3)), ("op", JsonValue.String("setBuildScenes")),
                ("result", Obj(("count", JsonValue.Integer(1))))))
            .Add(Obj(("index", JsonValue.Integer(4)), ("op", JsonValue.String("setImportSettings")),
                ("result", Obj(("path", JsonValue.String("Assets/Rock.png")),
                    ("applied", JsonValue.NewArray().Add(JsonValue.String("m_IsReadable"))),
                    ("failed", JsonValue.NewArray())))))
            .Add(Obj(("index", JsonValue.Integer(5)), ("op", JsonValue.String("setClipImportSettings")),
                ("result", Obj(("path", JsonValue.String("Assets/Character.fbx")),
                    ("applied", JsonValue.NewArray().Add(JsonValue.String("Walk"))),
                    ("failed", JsonValue.NewArray())))));

        var appliedAll = JsonValue.NewArray();
        for (var i = 0; i < 6; i++) appliedAll.Add(JsonValue.Integer(i));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", appliedAll),
            ("results", results),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("6 applied, 0 failed of 6 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_settings_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "createTag", ["name"] = "Friendly" },
                new Dictionary<string, object> { ["op"] = "deleteTag", ["name"] = "Obsolete" },
                new Dictionary<string, object> { ["op"] = "createLayer", ["name"] = "Hazard", ["layerIndex"] = 9 },
                new Dictionary<string, object>
                {
                    ["op"] = "setBuildScenes",
                    ["scenes"] = new object[]
                    {
                        new Dictionary<string, object> { ["path"] = "Assets/Scenes/Main.unity", ["enabled"] = true },
                    },
                },
                new Dictionary<string, object>
                {
                    ["op"] = "setImportSettings",
                    ["path"] = "Assets/Rock.png",
                    ["properties"] = new Dictionary<string, object> { ["m_IsReadable"] = true },
                },
                new Dictionary<string, object>
                {
                    ["op"] = "setClipImportSettings",
                    ["path"] = "Assets/Character.fbx",
                    ["clips"] = new object[]
                    {
                        new Dictionary<string, object> { ["name"] = "Walk", ["loopTime"] = true, ["firstFrame"] = 0, ["lastFrame"] = 30 },
                    },
                },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        // ONE wire call for the whole 6-operation spec.
        Assert.Equal("projectSettings.apply", request.Method);
        var ops = Prop(request.Params!, "operations");
        Assert.Equal(6, ops.Items.Count);

        Assert.Equal("Friendly", Prop(ops.Items[0], "name").AsString());

        Assert.Equal("Obsolete", Prop(ops.Items[1], "name").AsString());

        Assert.Equal("Hazard", Prop(ops.Items[2], "name").AsString());
        Assert.Equal(9, Prop(ops.Items[2], "layerIndex").AsInteger());

        var scenes = Prop(ops.Items[3], "scenes");
        Assert.Single(scenes.Items);
        Assert.Equal("Assets/Scenes/Main.unity", Prop(scenes.Items[0], "path").AsString());
        Assert.True(Prop(scenes.Items[0], "enabled").AsBoolean());

        Assert.Equal("Assets/Rock.png", Prop(ops.Items[4], "path").AsString());
        Assert.True(Prop(Prop(ops.Items[4], "properties"), "m_IsReadable").AsBoolean());

        Assert.Equal("Assets/Character.fbx", Prop(ops.Items[5], "path").AsString());
        var clips = Prop(ops.Items[5], "clips");
        Assert.Single(clips.Items);
        Assert.Equal("Walk", Prop(clips.Items[0], "name").AsString());
        Assert.True(Prop(clips.Items[0], "loopTime").AsBoolean());
        Assert.Equal(0.0, Prop(clips.Items[0], "firstFrame").AsDouble());
        Assert.Equal(30.0, Prop(clips.Items[0], "lastFrame").AsDouble());

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(0, 6), applied);
        Assert.Equal(0, structured.GetProperty("failed").GetArrayLength());
        Assert.Contains("6", structured.GetProperty("summary").GetString());

        // createLayer's assigned index - the one piece of data a caller cannot know in advance -
        // rides along verbatim in 'results', never collapsed into a bare "applied" flag.
        var resultsEl = structured.GetProperty("results");
        Assert.Equal(6, resultsEl.GetArrayLength());
        var layerResult = resultsEl[2];
        Assert.Equal(2, layerResult.GetProperty("index").GetInt32());
        Assert.Equal("createLayer", layerResult.GetProperty("op").GetString());
        Assert.Equal(9, layerResult.GetProperty("result").GetProperty("index").GetInt32());
    }

    // ---------------------------------------------------------------- partial failure: mapped from the wire, no rollback

    [Fact]
    public async Task PartialFailure_WirePerOperationFailure_MapsIndexOpAndError_StillOneWireCall()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray().Add(JsonValue.Integer(0))),
            ("results", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(0)), ("op", JsonValue.String("createTag")),
                ("result", Obj(("created", JsonValue.String("Friendly"))))))),
            ("failed", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(1)), ("op", JsonValue.String("createLayer")),
                ("error", JsonValue.String("Layer index must be 8-31 (0-7 are reserved for Unity's built-in layers), got 3."))))),
            ("summary", JsonValue.String("1 applied, 1 failed of 2 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "project_settings_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "createTag", ["name"] = "Friendly" },
                new Dictionary<string, object> { ["op"] = "createLayer", ["name"] = "Bad", ["layerIndex"] = 3 },
            },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("projectSettings.apply", request.Method);
        Assert.Equal(2, Prop(request.Params!, "operations").Items.Count);

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 0 }, applied);

        var failed = structured.GetProperty("failed");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.Equal(1, failed[0].GetProperty("index").GetInt32());
        Assert.Equal("createLayer", failed[0].GetProperty("op").GetString());
        Assert.Contains("8-31", failed[0].GetProperty("error").GetString());

        // The unified partial-batch shape: 'results' surfaces an entry (with the op's own result
        // payload) for the APPLIED op, faithfully mapped from the wire - not just a bare index.
        var results = structured.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal(0, results[0].GetProperty("index").GetInt32());
        Assert.Equal("createTag", results[0].GetProperty("op").GetString());
        Assert.Equal("Friendly", results[0].GetProperty("result").GetProperty("created").GetString());
    }

    // ---------------------------------------------------------------- whole-call (plugin-level) failure still propagates

    [Fact]
    public async Task ProjectSettingsApply_PluginLevelError_PropagatesAsToolError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes, "projectSettings.apply requires an 'operations' array parameter.");

        var envelope = await McpTestClient.CallTool(Factory, "project_settings_apply", new
        {
            operations = new[] { new Dictionary<string, object> { ["op"] = "createTag", ["name"] = "Friendly" } },
        });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("projectSettings.apply requires an 'operations' array parameter.", McpTestClient.ErrorText(envelope));
    }
}
