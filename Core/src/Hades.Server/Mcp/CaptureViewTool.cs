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

/// <summary>
/// capture_view's structured half. DESIGN-02 §4.3: deliberately carries NO image bytes and NO byte
/// count - the mechanism that makes hades_regression replay this call for free (DESIGN-02 §8.2):
/// RegressionRecorder.Normalize prefers StructuredContent whenever it is set, so the recorded
/// fixture is exactly this record - a few hundred bytes of deterministic framing metadata - and the
/// non-deterministic pixels never reach a fixture file at all. <b>Do not add a bytes/byte-count
/// field here later</b> - see CaptureViewToolTests' own reflection pin for why that would make every
/// recorded capture replay as a mismatch forever.
/// </summary>
public sealed record CaptureViewResult
{
    [JsonPropertyName("view")] public required string View { get; init; }
    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
    [JsonPropertyName("format")] public required string Format { get; init; }
    [JsonPropertyName("camera")] public required string Camera { get; init; }
    [JsonPropertyName("cameraPosition")] public required IReadOnlyList<double> CameraPosition { get; init; }
    [JsonPropertyName("cameraRotation")] public required IReadOnlyList<double> CameraRotation { get; init; }
    [JsonPropertyName("framedTarget")] public string? FramedTarget { get; init; }
    [JsonPropertyName("framedBounds")] public IReadOnlyList<double>? FramedBounds { get; init; }
    [JsonPropertyName("renderPipeline")] public required string RenderPipeline { get; init; }
    [JsonPropertyName("notes")] public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// DESIGN-02 (docs/design/visual-feedback), Lane F of ROADMAP.md's Phase 2: capture_view, 35 -&gt; 36
/// (see LiveMcpToolNames.cs for the plugin-side ledger and ROADMAP.md §4 for why the programme-wide
/// number is not anticipated here).
///
/// <b>Slice shipped: "game" and "scene" only.</b> "prefab" is accepted by the wire vocabulary
/// (CaptureCommands.cs validates it) but refused with an actionable message - see that file's own
/// class doc comment for why slice 2 was cut under implementation-time pressure, exactly as
/// ROADMAP.md's Lane F entry invites.
///
/// <b>The only tool in this server that returns <see cref="CallToolResult"/> directly</b> (DESIGN-02
/// §4.4) - every other tool returns a plain typed record and lets the SDK build the result. The one
/// reason: a typed record cannot carry a non-text content block, and this tool must return an
/// <see cref="ImageContentBlock"/> alongside its structured data.
/// <see cref="McpServerToolAttribute.OutputSchemaType"/> is what lets it still advertise
/// <see cref="CaptureViewResult"/> as a real output schema despite the return type being
/// <see cref="CallToolResult"/> - <see cref="McpServerToolAttribute.UseStructuredContent"/> must be
/// (and is) set alongside it for that to take effect.
/// </summary>
[McpServerToolType]
public sealed class CaptureViewTool(EditorProxy editor, ProjectService projects)
{
    [McpServerTool(Name = "capture_view", Title = "Capture View", ReadOnly = true,
                   UseStructuredContent = true, OutputSchemaType = typeof(CaptureViewResult))]
    [Description("Renders an image of the project from the attached Unity Editor, so you can "
               + "see what you built rather than only reading back serialized values. "
               + "Works in Play Mode too, where it shows live gameplay - the running frame, "
               + "mid-animation, with runtime state - which is usually the most informative "
               + "capture you can take. Renders through a camera inside Unity; never captures "
               + "your screen or any other window. Needs a live Editor - call "
               + "hades_charon_status first if unsure." + ToolSupport.LiveStateClause)]
    public async Task<CallToolResult> CaptureView(
        [Description("What to render: 'game' (through the scene's own camera - what the player "
                   + "would see) or 'scene' (an off-screen camera framed on 'target', or on the "
                   + "whole scene when 'target' is omitted). 'prefab' is not yet available in "
                   + "this build.")]
        string view,

        [Description("For view='scene': GameObject path to frame on, e.g. 'Canvas/HUD/HealthBar'. "
                   + "Omit to frame the whole scene. Ignored for view='game'.")]
        string? target = null,

        [Description("For view='game' only: name or path of the Camera to render through. "
                   + "Omit for Camera.main, falling back to the enabled camera with the highest "
                   + "depth. The camera actually used is reported in the result.")]
        string? camera = null,

        [Description("Image width in pixels (default 1024, min 256, max 1920). Out-of-range "
                   + "values are clamped, never refused, and the effective width is reported in "
                   + "the result. With format='png' the cap is 1280 instead of 1920. Height "
                   + "follows the view's own aspect ratio. Larger images cost proportionally "
                   + "more of your context - 1024 is right for almost every check.")]
        int? width = null,

        [Description("'jpeg' (default - right for anything photographic) or 'png' (lossless, for "
                   + "pixel-exact UI checks; lowers the width cap to 1280).")]
        string? format = null,

        [Description("Project handle from hades_status. Omit when Hades knows only one project.")]
        string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        EditorComponentTools.RequireNonBlank(view, nameof(view), "capture_view");

        var wireParams = WireJson.NewObject().SetProperty("view", WireJson.String(view));
        if (!string.IsNullOrWhiteSpace(target)) wireParams.SetProperty("target", WireJson.String(target));
        if (!string.IsNullOrWhiteSpace(camera)) wireParams.SetProperty("camera", WireJson.String(camera));
        if (width.HasValue) wireParams.SetProperty("width", WireJson.Integer(width.Value));
        if (!string.IsNullOrWhiteSpace(format)) wireParams.SetProperty("format", WireJson.String(format));

        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var result = await editor.SendCommandAsync(productGuid, "capture.view", wireParams).ConfigureAwait(false);

        byte[] imageBytes;
        try
        {
            imageBytes = Convert.FromBase64String(EditorComponentTools.Str(result, "image"));
        }
        catch (FormatException ex)
        {
            // DESIGN-02 §11.3: "a corrupt base64 string surfaces as an actionable McpException, not
            // a FormatException" - a raw FormatException here would cross the MCP boundary as an
            // opaque internal error instead of a diagnosable one.
            throw new McpException("capture_view: the Editor's response did not contain a valid base64 image (" + ex.Message + ").");
        }

        var mimeType = EditorComponentTools.Str(result, "mimeType");
        var effectiveFormat = mimeType == "image/png" ? "png" : "jpeg";
        var cameraLabel = EditorComponentTools.Str(result, "camera");
        var renderPipeline = EditorComponentTools.Str(result, "renderPipeline");
        var framedTarget = result.TryGetProperty("framedTarget", out var ft) && ft!.Kind == WireKind.String ? ft.AsString() : null;
        var framedBounds = result.TryGetProperty("framedBounds", out var fb) && fb!.Kind == WireKind.Array ? Doubles(fb) : null;
        var notes = result.TryGetProperty("notes", out var n) && n!.Kind == WireKind.Array ? Strings(n) : new List<string>();

        var structured = new CaptureViewResult
        {
            View = view,
            Width = (int)EditorComponentTools.Int(result, "width"),
            Height = (int)EditorComponentTools.Int(result, "height"),
            Format = effectiveFormat,
            Camera = cameraLabel,
            CameraPosition = result.TryGetProperty("cameraPosition", out var cp) ? Doubles(cp!) : new List<double>(),
            CameraRotation = result.TryGetProperty("cameraRotation", out var cr) ? Doubles(cr!) : new List<double>(),
            FramedTarget = framedTarget,
            FramedBounds = framedBounds,
            RenderPipeline = renderPipeline,
            Notes = notes,
        };

        var summaryText = "Captured '" + view + "' view (" + structured.Width + "x" + structured.Height + " " + effectiveFormat
                         + ") via " + cameraLabel + ", render pipeline " + renderPipeline + "."
                         + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");

        return new CallToolResult
        {
            Content = [ImageContentBlock.FromBytes(imageBytes, mimeType), new TextContentBlock { Text = summaryText }],
            StructuredContent = JsonSerializer.SerializeToElement(structured),
        };
    }

    static List<double> Doubles(WireJson value)
    {
        var list = new List<double>();
        if (value.Kind != WireKind.Array) return list;
        foreach (var item in value.Items)
            if (item.Kind == WireKind.Float || item.Kind == WireKind.Integer) list.Add(item.AsDouble());
        return list;
    }

    static List<string> Strings(WireJson value)
    {
        var list = new List<string>();
        if (value.Kind != WireKind.Array) return list;
        foreach (var item in value.Items)
            if (item.Kind == WireKind.String) list.Add(item.AsString());
        return list;
    }
}
