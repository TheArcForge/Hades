using System.Text.Json.Serialization;

namespace Hades.Control.Client.Dtos;

/// <summary>Mirrors Hades.Server.Control.McpPortSetting.</summary>
public sealed record McpPortSetting
{
    [JsonPropertyName("port")] public required int Port { get; init; }
    [JsonPropertyName("inUse")] public required bool InUse { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.LogLevelSetting.</summary>
public sealed record LogLevelSetting
{
    [JsonPropertyName("level")] public required string Level { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.SettingsResult, the response of GET /control/settings.</summary>
public sealed record SettingsResult
{
    [JsonPropertyName("mcpPort")] public required McpPortSetting McpPort { get; init; }
    [JsonPropertyName("logLevel")] public required LogLevelSetting LogLevel { get; init; }
}
