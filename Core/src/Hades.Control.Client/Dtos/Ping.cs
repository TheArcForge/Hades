using System.Text.Json.Serialization;

namespace Hades.Control.Client.Dtos;

/// <summary>
/// Mirrors <c>Hades.Server.Mcp.PingResult</c> — the body of <c>GET /control/ping</c>.
///
/// Deliberately noted: unlike every other type in this folder, its server counterpart lives under
/// <c>Hades.Server/Mcp/</c> rather than <c>Hades.Server/Control/</c>, so a conformance walk scoped
/// to the <c>Hades.Server.Control</c> namespace will not pair it automatically. It is ported here
/// because <c>/control/ping</c> is the endpoint the supervisor polls to decide whether a core is
/// alive and adoptable — the single most load-bearing call the client makes.
/// </summary>
public sealed record PingResult
{
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("uptimeSeconds")] public required double UptimeSeconds { get; init; }
}
