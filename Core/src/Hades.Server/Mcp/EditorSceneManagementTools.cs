using System.Text.Json.Serialization;

namespace Hades.Server.Mcp;

/// <summary>
/// Plan 10 Task 6 removed this file's four MCP tools (scene_save, scene_create, scene_duplicate -
/// folded into scene_manage; scene_set_build - folded into project_settings_apply), but
/// <see cref="BuildSceneSpec"/> stayed: it is <c>ProjectSettingsApplyOperation.Scenes</c>' own
/// element type (ProjectSettingsApplyTool.cs's <c>setBuildScenes</c> op), reused verbatim rather
/// than a second, divergent shape. No <c>[McpServerToolType]</c> class is left in this file for
/// <c>Program.cs</c> to register.
/// </summary>
public sealed record BuildSceneSpec
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; init; }
}
