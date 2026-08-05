using System.Text.Json.Serialization;

namespace Hades.Server.Mcp;

/// <summary>
/// Plan 10 Task 6 removed this file's four MCP tools (asset_move, asset_import - folded into
/// asset_manage; asset_set_import_settings, asset_set_clip_import_settings - folded into
/// project_settings_apply), but <see cref="ClipImportConfig"/> stayed: it is
/// <c>ProjectSettingsApplyOperation.Clips</c>' own element type (ProjectSettingsApplyTool.cs's
/// <c>setClipImportSettings</c> op), reused verbatim - mirrors Plugin~'s
/// <c>AssetCommands.ApplyClipConfig</c> field-for-field - rather than a second, divergent shape. No
/// <c>[McpServerToolType]</c> class is left in this file for <c>Program.cs</c> to register.
/// </summary>
public sealed record ClipImportConfig
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("loopTime")] public bool? LoopTime { get; init; }
    [JsonPropertyName("loopPose")] public bool? LoopPose { get; init; }
    [JsonPropertyName("cycleOffset")] public float? CycleOffset { get; init; }
    [JsonPropertyName("firstFrame")] public float? FirstFrame { get; init; }
    [JsonPropertyName("lastFrame")] public float? LastFrame { get; init; }
}
