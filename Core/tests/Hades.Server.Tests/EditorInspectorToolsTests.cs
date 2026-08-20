using Hades.Contract.Wire;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hades.Server.Tests;

/// <summary>
/// EditorInspectorTools' surviving tool, inspector_inspect (class 4, live-state read - Plan 9 Task
/// 5), over the full MCP/HTTP path - same scope and conventions the deleted EditorSceneToolsTests/
/// EditorMaterialToolsTests used: a fake Unity Editor plays canned wire responses, so these prove
/// the params sent and the result mapped, not any plugin-side behaviour.
///
/// Plan 10 Task 6 removed this file's other tests (inspector_select's own three) along with the
/// tool itself - folded into scene_apply's "select" operation, see SceneApplyTests.cs's own
/// full-operation-sweep test and SceneApplyTool.cs's class doc comment.
/// </summary>
public sealed class EditorInspectorToolsTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
{
    static JsonValue Obj(params (string Key, JsonValue Value)[] members)
    {
        var o = JsonValue.NewObject();
        foreach (var (key, value) in members) o.SetProperty(key, value);
        return o;
    }

    static JsonValue Vec3(double x, double y, double z) =>
        Obj(("x", JsonValue.Float(x)), ("y", JsonValue.Float(y)), ("z", JsonValue.Float(z)));

    // ---------------------------------------------------------------- inspector_inspect

    [Fact]
    public async Task InspectorInspect_SendsPath_MapsFullResultIncludingComponentsAndProperties()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var pluginResult = Obj(
            ("name", JsonValue.String("Player")),
            ("path", JsonValue.String("Player")),
            ("fileId", JsonValue.Integer(12345)),
            ("active", JsonValue.Bool(true)),
            ("layer", JsonValue.String("Default")),
            ("tag", JsonValue.String("Untagged")),
            ("isStatic", JsonValue.Bool(false)),
            ("position", Vec3(1, 2, 3)),
            ("rotation", Vec3(0, 0, 0)),
            ("scale", Vec3(1, 1, 1)),
            ("childCount", JsonValue.Integer(1)),
            ("children", JsonValue.NewArray().Add(JsonValue.String("Weapon"))),
            ("components", JsonValue.NewArray().Add(Obj(
                ("fileId", JsonValue.Integer(999)),
                ("type", JsonValue.String("BoxCollider")),
                ("fullType", JsonValue.String("UnityEngine.BoxCollider")),
                ("enabled", JsonValue.Bool(true)),
                ("properties", JsonValue.NewArray().Add(Obj(
                    ("name", JsonValue.String("m_IsTrigger")),
                    ("displayName", JsonValue.String("Is Trigger")),
                    ("type", JsonValue.String("Boolean")),
                    ("value", JsonValue.Bool(true)))))))));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, pluginResult);

        var structured = Structured(await McpTestClient.CallTool(Factory, "inspector_inspect", new { path = "Player" }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("inspector.inspect", request.Method);
        Assert.True(request.Params!.TryGetProperty("path", out var p) && p!.AsString() == "Player");

        Assert.Equal("Player", structured.GetProperty("name").GetString());
        Assert.Equal(12345, structured.GetProperty("fileId").GetInt64());
        Assert.True(structured.GetProperty("active").GetBoolean());
        Assert.Equal("Default", structured.GetProperty("layer").GetString());
        Assert.Equal("Untagged", structured.GetProperty("tag").GetString());
        Assert.False(structured.GetProperty("isStatic").GetBoolean());
        Assert.Equal(1, structured.GetProperty("childCount").GetInt32());
        Assert.Equal("Weapon", structured.GetProperty("children")[0].GetString());

        Assert.Equal(1.0, structured.GetProperty("position").GetProperty("x").GetDouble());
        Assert.Equal(2.0, structured.GetProperty("position").GetProperty("y").GetDouble());
        Assert.Equal(3.0, structured.GetProperty("position").GetProperty("z").GetDouble());

        var component = structured.GetProperty("components")[0];
        Assert.Equal("BoxCollider", component.GetProperty("type").GetString());
        Assert.Equal("UnityEngine.BoxCollider", component.GetProperty("fullType").GetString());
        Assert.True(component.GetProperty("enabled").GetBoolean());

        var property = component.GetProperty("properties")[0];
        Assert.Equal("m_IsTrigger", property.GetProperty("name").GetString());
        Assert.Equal("Boolean", property.GetProperty("type").GetString());
        Assert.True(property.GetProperty("value").GetBoolean());
    }

    [Fact]
    public async Task InspectorInspect_NoChildrenOrComponents_MapsEmptyListsNotACrash()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var pluginResult = Obj(
            ("name", JsonValue.String("Empty")),
            ("path", JsonValue.String("Empty")),
            ("fileId", JsonValue.Integer(1)),
            ("active", JsonValue.Bool(true)),
            ("layer", JsonValue.String("Default")),
            ("tag", JsonValue.String("Untagged")),
            ("isStatic", JsonValue.Bool(false)),
            ("position", Vec3(0, 0, 0)),
            ("rotation", Vec3(0, 0, 0)),
            ("scale", Vec3(1, 1, 1)),
            ("childCount", JsonValue.Integer(0)),
            ("children", JsonValue.NewArray()),
            ("components", JsonValue.NewArray()));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, pluginResult);
        var structured = Structured(await McpTestClient.CallTool(Factory, "inspector_inspect", new { path = "Empty" }));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, structured.GetProperty("children").GetArrayLength());
        Assert.Equal(0, structured.GetProperty("components").GetArrayLength());
    }

    [Fact]
    public async Task InspectorInspect_BlankPath_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "inspector_inspect", new { path = "" });

        Assert.Contains("path", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task InspectorInspect_PluginError_PropagatesAsToolError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes, "GameObject not found: 'Ghost'.");

        var envelope = await McpTestClient.CallTool(Factory, "inspector_inspect", new { path = "Ghost" });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("GameObject not found: 'Ghost'.", McpTestClient.ErrorText(envelope));
    }
}
