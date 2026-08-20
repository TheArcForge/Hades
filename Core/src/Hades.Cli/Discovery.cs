using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hades.Cli;

/// <summary>
/// The control API's discovery file, exactly as <c>Hades.Server.Control.ControlAuth.WriteConnectionFile</c>
/// writes it to <c>AppPaths.ControlTokenFile</c>: a small JSON object carrying the port that process
/// bound and the bearer token every request must present. This CLI reads it the same way the Swift
/// shell will (Plan 11 Task 7's own requirement: "discover the port and token... do not hardcode") -
/// <c>Program.cs</c> resolves the file's path via <c>Hades.Core.Storage.AppPaths</c> (honoring
/// HADES_HOME exactly as the server does), then hands it to <see cref="Read"/> here.
///
/// <see cref="ConnectionInfo"/> is a deliberately separate type from
/// <c>Hades.Server.Control.ControlConnectionInfo</c>, even though the JSON shape is identical: this
/// project talks to the control API only over HTTP, the same boundary an external client (the Swift
/// shell, a future Windows client) is limited to, and referencing the server's own internal wire type
/// would blur that boundary - the same reasoning ControlConnectionInfo's own doc comment gives for
/// not reusing EditorConnectionInfo.
/// </summary>
public sealed record ConnectionInfo
{
    [JsonPropertyName("port")] public required int Port { get; init; }
    [JsonPropertyName("token")] public required string Token { get; init; }
}

public static class Discovery
{
    /// <summary>Null when Hades is not running (or has never started) - the file does not exist yet.
    /// A missing file is an ordinary, expected condition here, never an exception.</summary>
    public static ConnectionInfo? Read(string connectionFilePath) =>
        File.Exists(connectionFilePath)
            ? JsonSerializer.Deserialize<ConnectionInfo>(File.ReadAllText(connectionFilePath))
            : null;
}
