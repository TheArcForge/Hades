using System.Text.Json.Serialization;

namespace Hades.Server.Mcp;

/// <summary>
/// Plan 10 Task 6 removed this file's three MCP tools (material_get_properties,
/// animation_get_controller, analyze_render_pipeline - folded into inspect_asset and
/// project_settings respectively), but <see cref="RenderPipelineResult"/> stayed: it is
/// <see cref="ProjectSettingsSectionResult.RenderPipeline"/>'s own type
/// (<c>project_settings</c>'s <c>section: "renderPipeline"</c>, in SettingsTools.cs), reused
/// verbatim rather than duplicated. No <c>[McpServerToolType]</c> class is left in this file for
/// <c>Program.cs</c> to register.
/// </summary>
public sealed record RenderPipelineResult
{
    [JsonPropertyName("pipeline")] public required string Pipeline { get; init; }
    [JsonPropertyName("pipelineAssetPath")] public string? PipelineAssetPath { get; init; }
}
