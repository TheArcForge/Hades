using Hades.Server.Control;
using Microsoft.AspNetCore.Connections;

namespace Hades.Server.Mcp;

/// <summary>
/// Resolves and starts the MCP endpoint's own Kestrel binding - distinct from, and see
/// <see cref="ControlListener"/>'s own constructor doc comment for the contrast, the control API's
/// port: that one is deliberately ephemeral and OS-assigned, discovered by its clients from a
/// connection file, because nothing declares it statically. The MCP endpoint is the opposite -
/// Program.cs's own comment on <c>MapMcp("/mcp")</c> already pins the PATH for exactly this reason
/// ("the Claude Code plugin declares this URL statically, so it must never move silently"); the
/// same reasoning applies to the PORT (<see cref="SettingsEndpoint.McpPort"/> = 7823), so it must
/// be chosen deliberately rather than left to whatever Kestrel's bare default happens to be.
///
/// That default (5000) is not a safe fallback on a Mac: ControlCenter's own AirPlay Receiver binds
/// <c>*:5000</c> out of the box on any current macOS - verified on the machine that found this bug
/// (Plan 12 Task 4 results). A spawned core with no ASPNETCORE_URLS set therefore died instantly
/// with an unhandled <see cref="AddressInUseException"/> and exhausted every supervisor retry. See
/// this class's own tests for both halves of the fix: <see cref="ResolveBindUrl"/> chooses 7823
/// deliberately, and <see cref="Run"/> fails loudly - never with a raw framework stack trace - if
/// even that fixed port turns out to be unavailable.
///
/// <b>Deliberately NOT a proactive bind-then-release pre-flight check</b> (the kind
/// <see cref="SettingsEndpoint"/>'s own private CanBindLoopback uses to answer the same question
/// for <c>/control/settings</c>): an earlier version of this class did exactly that here too, and
/// it broke the test suite. `dotnet test` constructs dozens of
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>-backed
/// instances of the real Program.cs concurrently (xunit's default parallel test collections) - safe
/// for <see cref="Run"/>'s real work, because WebApplicationFactory substitutes an in-memory
/// TestServer for Kestrel, so <c>app.Run()</c> never touches a real socket under test (verified
/// empirically: the full suite passes with this class's REACTIVE catch alone). A proactive
/// bind-and-release probe is NOT substituted, though - it is a real, independent socket operation
/// regardless of TestServer, so dozens of them racing the exact same real port 7823 at once
/// intermittently "lost" the race against each other and crashed the shared test-host process.
/// Reacting to Kestrel's own real failure, only after it actually happens, needs no such probe and
/// carries none of that risk.
/// </summary>
public static class McpBinding
{
    /// <summary>The URL Kestrel should bind for the MCP endpoint. <paramref name="explicitAspNetCoreUrls"/>
    /// - the real ASPNETCORE_URLS environment variable, when the caller already set one (tests via
    /// WebApplicationFactory, CI, launchSettings.json, Core/scripts/e2e-editor-attach.sh) - always
    /// wins: every one of those callers relies on choosing its own port, and overriding it here
    /// would break all of them. Absent that, the fixed, documented MCP port (see this class's own
    /// doc comment) - deliberately not Kestrel's bare default.</summary>
    public static string ResolveBindUrl(string? explicitAspNetCoreUrls) =>
        string.IsNullOrEmpty(explicitAspNetCoreUrls)
            ? $"http://127.0.0.1:{SettingsEndpoint.McpPort}"
            : explicitAspNetCoreUrls;

    /// <summary>Extracts the numeric port <paramref name="boundUrl"/> names - the same "first URL
    /// wins" parsing <see cref="DescribePortInUseFailure"/> already applies to its own message,
    /// factored out so Program.cs can tell <see cref="SettingsEndpoint.Build"/> (via
    /// <see cref="ControlListener"/>'s own <c>actualMcpPort</c> constructor parameter) which port
    /// THIS instance's MCP endpoint is really bound to, without a second, independently-maintained
    /// copy of the same parsing. This is what lets <c>GET /control/settings</c> tell "running on
    /// the documented port" apart from "ASPNETCORE_URLS moved us elsewhere" - see
    /// <see cref="SettingsEndpoint.Build"/>'s own doc comment for why that distinction is the whole
    /// fix. Falls back to the documented <see cref="SettingsEndpoint.McpPort"/> when
    /// <paramref name="boundUrl"/> is not a parseable absolute URL - should not happen in practice,
    /// since Kestrel itself would already have refused to bind an unparseable URL, so this call
    /// would never be reached with one.</summary>
    public static int ResolveBoundPort(string boundUrl)
    {
        var firstUrl = boundUrl.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? boundUrl;
        return Uri.TryCreate(firstUrl, UriKind.Absolute, out var uri) ? uri.Port : SettingsEndpoint.McpPort;
    }

    /// <summary>Runs <paramref name="app"/> (already configured via <c>UseUrls(boundUrl)</c>),
    /// translating a bind failure on <paramref name="boundUrl"/> into a
    /// <see cref="McpPortBindException"/> carrying a specific, actionable message - never a raw
    /// <see cref="AddressInUseException"/> reaching an unhandled-exception stack trace (spec #1 §8's
    /// error-handling standard: "specific and actionable... rather than opaque error codes or
    /// tracebacks", applied here to startup instead of a tool call). Kestrel wraps that exception in
    /// an <see cref="IOException"/> (verified empirically against this exact SDK - there is no
    /// documented public contract for it), hence the exception filter rather than a direct catch.
    ///
    /// ASP.NET Core's own <c>Host.StartAsync</c> ALSO logs that same raw exception - full stack
    /// trace included - via the default console logger before it ever reaches this catch (also
    /// verified empirically). There is no narrowly-scoped way to intercept just that one message,
    /// so Program.cs suppresses the specific log category it comes from
    /// (<c>Microsoft.Extensions.Hosting.Internal.Host</c>) when it configures the builder, ahead of
    /// calling this method - see Program.cs's own comment on that call for why a logging filter
    /// (pure configuration, no real I/O) was chosen there instead of a proactive socket probe here
    /// (see this class's own doc comment for why that alternative was rejected). Every other
    /// startup failure propagates completely unchanged.</summary>
    public static void Run(WebApplication app, string boundUrl)
    {
        try
        {
            app.Run();
        }
        catch (IOException ex) when (ex.InnerException is AddressInUseException)
        {
            throw new McpPortBindException(DescribePortInUseFailure(boundUrl), ex);
        }
    }

    /// <summary>The exact text <see cref="Run"/> surfaces for a bind failure - names the port,
    /// says what binding it deliberately means, and suggests what to do (spec #1 §8's standard) -
    /// a pure function of the URL that failed, so it is testable without ever actually binding a
    /// socket.</summary>
    public static string DescribePortInUseFailure(string boundUrl)
    {
        var firstUrl = boundUrl.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? boundUrl;
        var port = Uri.TryCreate(firstUrl, UriKind.Absolute, out var uri) ? $"{uri.Port}" : firstUrl;

        return $"Hades could not start: port {port} is already in use. The MCP endpoint binds this port "
            + "deliberately and always, so Claude Code and other MCP clients can find it at a fixed address — "
            + $"Hades will not silently switch to a different one. {RemedyForPortInUse(port)}";
    }

    /// <summary>The actionable remedy for port <paramref name="port"/> being occupied - the same
    /// recommendation <see cref="DescribePortInUseFailure"/> gives for a hard startup failure,
    /// factored out so <see cref="SettingsEndpoint"/>'s own live conflict probe
    /// (<c>GET /control/settings</c>, a normally-benign "it's probably this instance's own MCP
    /// listener" state, not a startup failure) can word its message the identical way rather than a
    /// second, independently-drifting copy. Plan 13 Task 7: "render the conflict including the
    /// actionable remedy the core already words" - this is that remedy, authored once.</summary>
    public static string RemedyForPortInUse(int port) => RemedyForPortInUse($"{port}");

    /// <summary>The same remedy for a port already reduced to text - what
    /// <see cref="DescribePortInUseFailure"/> holds, since it falls back to the whole URL when one
    /// cannot be parsed. Having that method call this one is the point: it used to carry its own
    /// copy of this sentence, which is exactly the drift the doc comment above warned about.</summary>
    public static string RemedyForPortInUse(string port) =>
        $"Find and stop whatever is using port {port} (`{FindPortHolderCommand(port)}`), or set "
        + "ASPNETCORE_URLS to run Hades at a different address.";

    /// <summary>
    /// The command that names whoever is holding the port, in the shape of the platform actually
    /// running.
    ///
    /// <para><c>lsof</c> does not exist on Windows. Printing it there sends a stuck user nowhere, at
    /// the one moment they most need a command that works — found on the first Windows hand-run of
    /// <c>hades serve</c>, where force-killing it orphaned the core, the orphan kept port 7823, and
    /// the resulting failure recommended a macOS-only tool. Two tests pinned the <c>lsof</c> literal
    /// and passed on Windows, so the suite was endorsing it.</para>
    /// </summary>
    static string FindPortHolderCommand(string port) =>
        OperatingSystem.IsWindows()
            ? $"netstat -ano | findstr :{port}"
            : $"lsof -nP -iTCP:{port} -sTCP:LISTEN";
}

/// <summary>Thrown by <see cref="McpBinding.Run"/> in place of a raw
/// <see cref="AddressInUseException"/> - see that method's own doc comment.</summary>
public sealed class McpPortBindException(string message, Exception innerException)
    : Exception(message, innerException);
