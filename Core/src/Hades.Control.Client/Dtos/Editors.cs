using System.Text.Json.Serialization;

namespace Hades.Control.Client.Dtos;

/// <summary>Mirrors Hades.Server.Control.EditorRow.</summary>
public sealed record EditorRow
{
    [JsonPropertyName("project")] public required string Project { get; init; }
    [JsonPropertyName("productGuid")] public required string ProductGuid { get; init; }
    [JsonPropertyName("state")] public required ProjectEditorState State { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("unityVersion")] public string? UnityVersion { get; init; }
    [JsonPropertyName("processId")] public long? ProcessId { get; init; }
    [JsonPropertyName("connectionAgeSeconds")] public int? ConnectionAgeSeconds { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.EditorsResult, the response of GET /control/editors.</summary>
public sealed record EditorsResult
{
    [JsonPropertyName("editors")] public required IReadOnlyList<EditorRow> Editors { get; init; }
}
