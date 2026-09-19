using Hades.Contract.Wire;
using Microsoft.AspNetCore.Mvc.Testing;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Tests;

/// <summary>
/// scene_manage: the declarative batch that replaces EditorSceneManagementTools' scene_save/
/// scene_create/scene_duplicate and EditorProjectTools' scene_open (4 tools). Same scope discipline
/// as ProjectSettingsApplyTests/AssetManageTests: this proves the tool-to-wire contract, not the
/// one-lease-window/self-managed-undo-group/real-per-op-behaviour properties (including F5/F6's
/// "create opens the new scene as the active scene, and both it and non-additive open refuse a
/// dirty currently-loaded scene" behaviour), which are plugin-side properties proven against a real
/// Editor in UnityPlugin/Tests/Editor/SceneManageCommandsTests.cs.
/// </summary>
public sealed class SceneManageTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
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
    public async Task SceneManage_EmptyOperationsArray_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "scene_manage", new { operations = Array.Empty<object>() });

        Assert.Contains("operations", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task SceneManage_UnknownOp_RejectsWholeCallBeforeDispatchingAnything_ListsValidOps()
    {
        var envelope = await McpTestClient.CallTool(Factory, "scene_manage", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "save" },
                new Dictionary<string, object> { ["op"] = "frobnicate" },
            },
        });

        var text = McpTestClient.ErrorText(envelope);
        Assert.Contains("frobnicate", text);
        Assert.Contains("operations[1]", text);
        foreach (var op in new[] { "save", "create", "open", "duplicate" })
            Assert.Contains(op, text);
    }

    // ---------------------------------------------------------------- F5/F6: description explains create/open/discardUnsaved

    [Fact]
    public async Task SceneManage_DescriptionExplainsCreateOpensTheNewSceneAndBothRefuseWhenDirty()
    {
        var tool = Assert.Single((await McpTestClient.ListTools(Factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "scene_manage");

        var description = tool.GetProperty("description").GetString()!;
        Assert.Contains("active scene", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unsaved changes", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("discardUnsaved", description);
    }

    // ---------------------------------------------------------------- unknown FIELD refused before any wire call (per-op, not per-tool)

    /// <summary>Enumerates EVERY op scene_manage accepts (not a spot check) and proves each one,
    /// individually, refuses an unknown field before any wire call.</summary>
    [Fact]
    public async Task SceneManage_UnknownField_RejectedForEveryOp()
    {
        foreach (var op in new[] { "save", "create", "open", "duplicate" })
        {
            var envelope = await McpTestClient.CallTool(Factory, "scene_manage", new
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
    public async Task SceneManage_FullOperationSweep_SendsOneWireCallWithEveryFieldTranslated_MapsAppliedAndSummary()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var results = JsonValue.NewArray()
            .Add(Obj(("index", JsonValue.Integer(0)), ("op", JsonValue.String("save")),
                ("result", Obj(("saved", JsonValue.String("Assets/Scenes/Main.unity"))))))
            .Add(Obj(("index", JsonValue.Integer(1)), ("op", JsonValue.String("create")),
                ("result", Obj(("created", JsonValue.String("Assets/Scenes/Level1.unity"))))))
            .Add(Obj(("index", JsonValue.Integer(2)), ("op", JsonValue.String("open")),
                ("result", Obj(("opened", JsonValue.String("Assets/Scenes/Level1.unity")), ("mode", JsonValue.String("Single")), ("isLoaded", JsonValue.Bool(true))))))
            .Add(Obj(("index", JsonValue.Integer(3)), ("op", JsonValue.String("duplicate")),
                ("result", Obj(("source", JsonValue.String("Assets/Scenes/Level1.unity")), ("destination", JsonValue.String("Assets/Scenes/Level1_Copy.unity"))))));

        var appliedAll = JsonValue.NewArray();
        for (var i = 0; i < 4; i++) appliedAll.Add(JsonValue.Integer(i));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", appliedAll),
            ("results", results),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("4 applied, 0 failed of 4 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_manage", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "save", ["path"] = "Assets/Scenes/Main.unity" },
                new Dictionary<string, object> { ["op"] = "create", ["path"] = "Assets/Scenes/Level1.unity" },
                new Dictionary<string, object> { ["op"] = "open", ["path"] = "Assets/Scenes/Level1.unity", ["additive"] = false },
                new Dictionary<string, object> { ["op"] = "duplicate", ["sourcePath"] = "Assets/Scenes/Level1.unity", ["destPath"] = "Assets/Scenes/Level1_Copy.unity" },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        // ONE wire call for the whole 4-operation spec.
        Assert.Equal("scene.manage", request.Method);
        var ops = Prop(request.Params!, "operations");
        Assert.Equal(4, ops.Items.Count);

        Assert.Equal("Assets/Scenes/Main.unity", Prop(ops.Items[0], "path").AsString());
        Assert.Equal("Assets/Scenes/Level1.unity", Prop(ops.Items[1], "path").AsString());
        Assert.Equal("Assets/Scenes/Level1.unity", Prop(ops.Items[2], "path").AsString());
        Assert.False(Prop(ops.Items[2], "additive").AsBoolean());
        Assert.Equal("Assets/Scenes/Level1.unity", Prop(ops.Items[3], "sourcePath").AsString());
        Assert.Equal("Assets/Scenes/Level1_Copy.unity", Prop(ops.Items[3], "destPath").AsString());

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(0, 4), applied);
        Assert.Equal(0, structured.GetProperty("failed").GetArrayLength());
        Assert.Contains("4", structured.GetProperty("summary").GetString());

        var resultsEl = structured.GetProperty("results");
        Assert.Equal(4, resultsEl.GetArrayLength());
        Assert.Equal("open", resultsEl[2].GetProperty("op").GetString());
        Assert.True(resultsEl[2].GetProperty("result").GetProperty("isLoaded").GetBoolean());
    }

    // ---------------------------------------------------------------- F5/F6: discardUnsaved translated verbatim

    [Fact]
    public async Task SceneManage_DiscardUnsaved_TranslatedVerbatimForCreateAndOpen()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var results = JsonValue.NewArray()
            .Add(Obj(("index", JsonValue.Integer(0)), ("op", JsonValue.String("create")),
                ("result", Obj(("created", JsonValue.String("Assets/Scenes/Level2.unity")), ("activeScene", JsonValue.String("Assets/Scenes/Level2.unity"))))))
            .Add(Obj(("index", JsonValue.Integer(1)), ("op", JsonValue.String("open")),
                ("result", Obj(("opened", JsonValue.String("Assets/Scenes/Level1.unity")), ("mode", JsonValue.String("Single")),
                    ("isLoaded", JsonValue.Bool(true)), ("activeScene", JsonValue.String("Assets/Scenes/Level1.unity"))))));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray().Add(JsonValue.Integer(0)).Add(JsonValue.Integer(1))),
            ("results", results),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("2 applied, 0 failed of 2 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_manage", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["path"] = "Assets/Scenes/Level2.unity", ["discardUnsaved"] = true },
                new Dictionary<string, object> { ["op"] = "open", ["path"] = "Assets/Scenes/Level1.unity", ["discardUnsaved"] = true },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        var ops = Prop(request.Params!, "operations");
        Assert.True(Prop(ops.Items[0], "discardUnsaved").AsBoolean());
        Assert.True(Prop(ops.Items[1], "discardUnsaved").AsBoolean());

        var resultsEl = structured.GetProperty("results");
        Assert.Equal("Assets/Scenes/Level2.unity", resultsEl[0].GetProperty("result").GetProperty("activeScene").GetString());
        Assert.Equal("Assets/Scenes/Level1.unity", resultsEl[1].GetProperty("result").GetProperty("activeScene").GetString());
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
                ("result", Obj(("created", JsonValue.String("Assets/Scenes/A.unity")))))
            )),
            ("failed", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(1)), ("op", JsonValue.String("open")),
                ("error", JsonValue.String("Scene not found at 'Assets/Ghost.unity'."))))),
            ("summary", JsonValue.String("1 applied, 1 failed of 2 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "scene_manage", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["path"] = "Assets/Scenes/A.unity" },
                new Dictionary<string, object> { ["op"] = "open", ["path"] = "Assets/Ghost.unity" },
            },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("scene.manage", request.Method);
        Assert.Equal(2, Prop(request.Params!, "operations").Items.Count);

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 0 }, applied);

        var failed = structured.GetProperty("failed");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.Equal(1, failed[0].GetProperty("index").GetInt32());
        Assert.Equal("open", failed[0].GetProperty("op").GetString());
        Assert.Contains("Ghost.unity", failed[0].GetProperty("error").GetString());

        // The unified partial-batch shape: 'results' surfaces an entry (with the op's own result
        // payload) for the APPLIED op, faithfully mapped from the wire - not just a bare index.
        var results = structured.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal(0, results[0].GetProperty("index").GetInt32());
        Assert.Equal("create", results[0].GetProperty("op").GetString());
        Assert.Equal("Assets/Scenes/A.unity", results[0].GetProperty("result").GetProperty("created").GetString());
    }

    // ---------------------------------------------------------------- whole-call (plugin-level) failure still propagates

    [Fact]
    public async Task SceneManage_PluginLevelError_PropagatesAsToolError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes, "scene.manage requires an 'operations' array parameter.");

        var envelope = await McpTestClient.CallTool(Factory, "scene_manage", new
        {
            operations = new[] { new Dictionary<string, object> { ["op"] = "save" } },
        });
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("scene.manage requires an 'operations' array parameter.", McpTestClient.ErrorText(envelope));
    }
}
