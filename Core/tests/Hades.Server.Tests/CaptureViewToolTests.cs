using Hades.Contract.Wire;
using Hades.Server.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hades.Server.Tests;

/// <summary>
/// DESIGN-02 (docs/design/DESIGN-02-visual-feedback.md) §11.3's core-side suite for capture_view.
/// Same fake-Unity-over-a-real-socket harness every other editor-routed tool test in this project
/// uses (see EditorInspectorToolsTests' own doc comment) - proves the wire params this tool sends
/// and how it maps the plugin's raw JSON back into <see cref="CallToolResult"/>'s content blocks and
/// <see cref="CaptureViewResult"/>, never any plugin-side rendering behaviour (which cannot run
/// outside a real Unity Editor at all - DESIGN-02 §11).
/// </summary>
public sealed class CaptureViewToolTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
{
    static JsonValue Obj(params (string Key, JsonValue Value)[] members)
    {
        var o = JsonValue.NewObject();
        foreach (var (key, value) in members) o.SetProperty(key, value);
        return o;
    }

    static JsonValue Arr(params JsonValue[] items)
    {
        var a = JsonValue.NewArray();
        foreach (var item in items) a.Add(item);
        return a;
    }

    static JsonValue FullPluginResult(string image = "aGVsbG8=" /* "hello" */, string mimeType = "image/jpeg") => Obj(
        ("image", JsonValue.String(image)),
        ("mimeType", JsonValue.String(mimeType)),
        ("width", JsonValue.Integer(1024)),
        ("height", JsonValue.Integer(576)),
        ("camera", JsonValue.String("(temporary capture camera)")),
        ("cameraPosition", Arr(JsonValue.Float(1), JsonValue.Float(2), JsonValue.Float(3))),
        ("cameraRotation", Arr(JsonValue.Float(4), JsonValue.Float(5), JsonValue.Float(6))),
        ("framedTarget", JsonValue.String("Canvas/HUD/HealthBar")),
        ("framedBounds", Arr(JsonValue.Float(0), JsonValue.Float(0), JsonValue.Float(0), JsonValue.Float(1), JsonValue.Float(1), JsonValue.Float(1))),
        ("renderPipeline", JsonValue.String("URP")),
        ("notes", Arr(JsonValue.String("some note"))));

    // ---------------------------------------------------------------- happy path / wire params

    [Fact]
    public async Task CaptureView_Game_SendsOnlyViewParam_MapsFullResult_IncludingImageContentBlock()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, FullPluginResult());

        var envelope = await McpTestClient.CallTool(Factory, "capture_view", new { view = "game" });
        var structured = Structured(envelope);
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("capture.view", request.Method);
        Assert.True(request.Params!.TryGetProperty("view", out var v) && v!.AsString() == "game");
        Assert.False(request.Params!.TryGetProperty("target", out _));
        Assert.False(request.Params!.TryGetProperty("camera", out _));
        Assert.False(request.Params!.TryGetProperty("width", out _));
        Assert.False(request.Params!.TryGetProperty("format", out _));

        Assert.Equal("game", structured.GetProperty("view").GetString());
        Assert.Equal(1024, structured.GetProperty("width").GetInt32());
        Assert.Equal(576, structured.GetProperty("height").GetInt32());
        Assert.Equal("jpeg", structured.GetProperty("format").GetString());
        Assert.Equal("(temporary capture camera)", structured.GetProperty("camera").GetString());
        Assert.Equal(3, structured.GetProperty("cameraPosition").GetArrayLength());
        Assert.Equal("Canvas/HUD/HealthBar", structured.GetProperty("framedTarget").GetString());
        Assert.Equal(6, structured.GetProperty("framedBounds").GetArrayLength());
        Assert.Equal("URP", structured.GetProperty("renderPipeline").GetString());
        Assert.Equal("some note", structured.GetProperty("notes")[0].GetString());

        var content = envelope.GetProperty("result").GetProperty("content");
        Assert.True(content.GetArrayLength() >= 2, envelope.GetRawText());
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal("image/jpeg", content[0].GetProperty("mimeType").GetString());
        Assert.Equal("aGVsbG8=", content[0].GetProperty("data").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task CaptureView_Scene_WithTargetCameraWidthAndFormat_SendsEveryParamOnTheWire()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, FullPluginResult(mimeType: "image/png"));

        await McpTestClient.CallTool(Factory, "capture_view",
            new { view = "scene", target = "Canvas/HUD/HealthBar", width = 800, format = "png" });
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("capture.view", request.Method);
        Assert.True(request.Params!.TryGetProperty("view", out var v) && v!.AsString() == "scene");
        Assert.True(request.Params!.TryGetProperty("target", out var t) && t!.AsString() == "Canvas/HUD/HealthBar");
        Assert.True(request.Params!.TryGetProperty("width", out var w) && w!.AsInteger() == 800);
        Assert.True(request.Params!.TryGetProperty("format", out var f) && f!.AsString() == "png");
    }

    [Fact]
    public async Task CaptureView_PngMimeType_ReportsFormatPng()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, FullPluginResult(mimeType: "image/png"));

        var structured = Structured(await McpTestClient.CallTool(Factory, "capture_view", new { view = "scene" }));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("png", structured.GetProperty("format").GetString());
    }

    [Fact]
    public async Task CaptureView_NoFramedTargetOrBounds_ReportsThemAsNull()
    {
        var pluginResult = Obj(
            ("image", JsonValue.String("aGVsbG8=")),
            ("mimeType", JsonValue.String("image/jpeg")),
            ("width", JsonValue.Integer(1024)),
            ("height", JsonValue.Integer(576)),
            ("camera", JsonValue.String("Main Camera")),
            ("cameraPosition", Arr(JsonValue.Float(0), JsonValue.Float(0), JsonValue.Float(0))),
            ("cameraRotation", Arr(JsonValue.Float(0), JsonValue.Float(0), JsonValue.Float(0))),
            ("framedTarget", JsonValue.Null),
            ("framedBounds", JsonValue.Null),
            ("renderPipeline", JsonValue.String("Built-in")),
            ("notes", JsonValue.NewArray()));

        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, pluginResult);

        var structured = Structured(await McpTestClient.CallTool(Factory, "capture_view", new { view = "game" }));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(System.Text.Json.JsonValueKind.Null, structured.GetProperty("framedTarget").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, structured.GetProperty("framedBounds").ValueKind);
    }

    // ---------------------------------------------------------------- corrupt base64 (DESIGN-02 §11.3)

    [Fact]
    public async Task CaptureView_CorruptBase64FromThePlugin_SurfacesAsAnActionableToolError_NotACrash()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, FullPluginResult(image: "not-valid-base64!!"));

        var envelope = await McpTestClient.CallTool(Factory, "capture_view", new { view = "game" });
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(envelope.GetProperty("result").GetProperty("isError").GetBoolean(), envelope.GetRawText());
        var text = envelope.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("base64", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("capture_view", text);
    }

    // ---------------------------------------------------------------- DESIGN-02 §8.2's "no bytes" invariant

    [Fact]
    public void CaptureViewResult_CarriesNoImageBytesOrByteCount_SoRegressionReplayExcludesThePixelsStructurally()
    {
        // DESIGN-02 §4.3/§8.2: hades_regression's Normalize prefers StructuredContent whenever it is
        // set, so a bytes/byte-count field here would make every recorded capture replay as a
        // mismatch forever (real renders are never bit-identical - DESIGN-02 §8.1). This reflection
        // pin is what stops a future convenience field from silently reintroducing that.
        var offenders = typeof(CaptureViewResult).GetProperties()
            .Where(p => p.PropertyType == typeof(byte[])
                     || p.Name.Contains("Bytes", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("ByteCount", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Equals("Image", StringComparison.OrdinalIgnoreCase)
                     || p.Name.Equals("Data", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "CaptureViewResult must never carry image bytes or a byte count (DESIGN-02 §8.2) - found: "
            + string.Join(", ", offenders) + ". A bytes/byte-count field makes every recorded "
            + "hades_regression capture replay as a mismatch forever, because real renders are never "
            + "bit-identical. Keep the image out of StructuredContent; it already reaches the caller "
            + "via the ImageContentBlock in CallToolResult.Content.");
    }
}
