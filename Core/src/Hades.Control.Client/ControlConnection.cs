using System.Text.Json.Serialization;

namespace Hades.Control.Client;

/// <summary>Where the running core's control API can be reached, and the bearer token every
/// request must carry. Mirrors Swift's <c>ControlConnection</c> and the server's
/// <c>ControlConnectionInfo</c> - same JSON shape, same discovery file.</summary>
public sealed record ControlConnection
{
    [JsonPropertyName("port")] public required int Port { get; init; }
    [JsonPropertyName("token")] public required string Token { get; init; }
}
