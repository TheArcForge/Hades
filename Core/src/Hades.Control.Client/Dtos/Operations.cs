using System.Text.Json.Serialization;
using Hades.Control.Client;

namespace Hades.Control.Client.Dtos;

/// <summary>Mirrors Hades.Server.Control.OperationState.</summary>
[JsonConverter(typeof(UnknownFallbackConverter<OperationState>))]
public enum OperationState { Unknown, Running, Done, Failed }

/// <summary>Mirrors Hades.Server.Control.OperationResult, the response of
/// GET /control/operations/{id}.</summary>
public sealed record OperationResult
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("state")] public required OperationState State { get; init; }
    [JsonPropertyName("startedAtUtc")] public required DateTimeOffset StartedAtUtc { get; init; }
    [JsonPropertyName("finishedAtUtc")] public DateTimeOffset? FinishedAtUtc { get; init; }
    [JsonPropertyName("elapsedSeconds")] public required int ElapsedSeconds { get; init; }
    [JsonPropertyName("progress")] public string? Progress { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }
}
