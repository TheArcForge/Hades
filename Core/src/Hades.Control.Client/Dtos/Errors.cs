using System.Text.Json.Serialization;

namespace Hades.Control.Client.Dtos;

/// <summary>Mirrors Hades.Server.Control.ActionResult, the common success/failure shape shared by
/// several endpoints (project remove/reveal/open, memory proposal accept/dismiss/defer, ...).</summary>
public sealed record ActionResult
{
    [JsonPropertyName("success")] public required bool Success { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
}
