using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hades.Core.Storage;
using Hades.Server.Mcp;

namespace Hades.Server.Control;

/// <summary>
/// What the control listener writes to its discovery file on every <see cref="ControlListener.Start"/>,
/// and what a control API client (the Swift shell, a future <c>hades</c> CLI) reads to learn which
/// port to call and which bearer token to present.
///
/// Deliberately its own type, not a reuse of <see cref="Hades.Contract.Wire.EditorConnectionInfo"/>
/// even though the shape is identical today: that type is embedded verbatim into the Unity plugin
/// build (see Hades.Core.csproj's EmbeddedResource list) and is owned by the editor-link wire
/// protocol, which this listener has nothing to do with. Sharing it would let an editor token and a
/// control token - two different secrets for two different trust boundaries - be read from, or
/// presented to, the wrong listener without the compiler ever noticing the mistake. A distinct type
/// turns that into a compile error: <see cref="ControlListener"/> only ever produces and consumes
/// this type, and <see cref="Hades.Core.Editors.EditorListener"/> only ever produces and consumes
/// EditorConnectionInfo - there is no shared factory or field either could pass the other's value
/// through by accident. Plain <see cref="System.Text.Json"/> rather than the hand-rolled MiniJson
/// codec Contract/Wire types use: this file is never read by Unity's C# 9 compiler, so there is no
/// reason to pay that constraint here.
/// </summary>
public sealed record ControlConnectionInfo
{
    [JsonPropertyName("port")] public required int Port { get; init; }
    [JsonPropertyName("token")] public required string Token { get; init; }
}

/// <summary>
/// Token generation, constant-time comparison, discovery-file writing, and the two pieces of
/// per-request middleware every control endpoint runs behind. The control-listener analogue of
/// what <see cref="Hades.Core.Editors.EditorListener"/> does for the editor link, adapted from a
/// one-time TCP handshake to a per-request HTTP check: the editor link authenticates a connection
/// once, but the control API authenticates every request, including reads - a read-only leak of
/// project paths and trace contents is still a leak.
/// </summary>
public static class ControlAuth
{
    public static string GenerateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>Constant-time comparison so a mismatched token cannot be brute-forced faster by
    /// timing how quickly this call returns - same technique, same reasoning, as
    /// EditorListener.TokenMatches.</summary>
    public static bool TokenMatches(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));

    /// <summary>
    /// Writes the discovery file. Mode-0600 protection (and the reasoning behind exactly how it is
    /// applied) lives in <see cref="TokenFileWriter"/>, shared with
    /// <see cref="Hades.Core.Editors.EditorListener"/>'s identical discovery-file write.
    /// </summary>
    public static void WriteConnectionFile(string path, int port, string token)
    {
        var json = JsonSerializer.Serialize(new ControlConnectionInfo { Port = port, Token = token });
        TokenFileWriter.Write(path, json);
    }

    /// <summary>
    /// Rejects any request whose <c>Origin</c> header is present and not loopback/localhost, per
    /// the MCP spec's requirement for local servers and spec #3 §4 - the same DNS-rebinding
    /// concern <see cref="OriginValidation"/> exists for on the MCP endpoint, reused here rather
    /// than re-implemented: <see cref="OriginValidation.IsAllowed"/> is the shared predicate. The
    /// response body is plain JSON, not OriginValidation.UseMcpOriginValidation's own JSON-RPC
    /// envelope - that shape belongs to the MCP endpoint's protocol, not this API's.
    /// </summary>
    public static IApplicationBuilder UseControlOriginValidation(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!OriginValidation.IsAllowed(context.Request.Headers.Origin.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Origin not allowed" });
                return;
            }

            await next();
        });

    /// <summary>
    /// Requires <c>Authorization: Bearer &lt;token&gt;</c> matching <paramref name="expectedToken"/>
    /// on every request - reads as well as writes, per this listener's whole reason for existing.
    /// Applied globally, before any endpoint is mapped (see <see cref="ControlListener.Start"/>),
    /// so a future endpoint cannot be added without it.
    /// </summary>
    public static IApplicationBuilder UseControlTokenAuth(this IApplicationBuilder app, string expectedToken) =>
        app.Use(async (context, next) =>
        {
            const string prefix = "Bearer ";
            var header = context.Request.Headers.Authorization.ToString();

            if (!header.StartsWith(prefix, StringComparison.Ordinal)
                || !TokenMatches(expectedToken, header[prefix.Length..]))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid token" });
                return;
            }

            await next();
        });
}
