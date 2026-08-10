using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Hades.Server.Mcp;

namespace Hades.Server.Control;

/// <summary>The MCP endpoint's port and its resolved conflict state - see
/// <see cref="SettingsEndpoint"/>'s own class doc comment. <see cref="InUse"/> is decided here, a
/// real live probe, never something the shell infers from a failed connection of its own.</summary>
public sealed record McpPortSetting
{
    [JsonPropertyName("port")] public required int Port { get; init; }
    [JsonPropertyName("inUse")] public required bool InUse { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
}

public sealed record LogLevelSetting
{
    [JsonPropertyName("level")] public required string Level { get; init; }
}

/// <summary>The full <c>GET /control/settings</c> response - see <see cref="SettingsEndpoint"/>'s
/// own class doc comment. Deliberately just two fields: <c>launchAtLogin</c> and
/// <c>resourceGuards</c> used to live here too, both hardcoded constants with nothing behind them -
/// see that same doc comment for why Plan 13 Task 7 removed both rather than leave them "real
/// enough".</summary>
public sealed record SettingsResult
{
    [JsonPropertyName("mcpPort")] public required McpPortSetting McpPort { get; init; }
    [JsonPropertyName("logLevel")] public required LogLevelSetting LogLevel { get; init; }
}

/// <summary>
/// Every field <see cref="SettingsEndpoint.Resolve"/> turns into a <see cref="SettingsResult"/> -
/// same two-layer reasoning as every other Control endpoint's own snapshot type: plain data in, so
/// <see cref="SettingsEndpoint.Resolve"/> is testable with no socket, no configuration, no clock.
/// </summary>
public sealed record SettingsSnapshot
{
    public required int McpPort { get; init; }
    public required bool McpPortInUse { get; init; }
    public required string LogLevel { get; init; }
}

/// <summary>
/// <c>GET /control/settings</c> - current values <b>and their resolved conflict states</b> (spec
/// #3 §3.5), so the shell shows a conflict rather than detecting one itself. Same two-layer split
/// as every other Control endpoint: <see cref="Resolve"/> is pure (a <see cref="SettingsSnapshot"/>
/// in, a <see cref="SettingsResult"/> out), <see cref="Build"/> is the orchestrator that gathers
/// real state (a live TCP probe, the app's real <see cref="IConfiguration"/>) and feeds it to
/// <see cref="Resolve"/>.
///
/// <b>Two settings here, not four - Plan 13 Task 7 removed <c>launchAtLogin</c> and
/// <c>resourceGuards</c> entirely.</b> Both used to be reported as hardcoded constants
/// (<c>false</c>, <c>true</c>/<c>false</c>) with no persistence anywhere in this codebase ever
/// writing a different value - a permanently-fixed literal dressed up as a live setting. A view
/// rendering either verbatim would state, with total confidence, that launch-at-login is off on a
/// machine where the user turned it on. This task's own governing rule: an endpoint that omits a
/// value it cannot know is honest; one that invents a value is not - so both are gone from the wire
/// contract entirely (see <see cref="SettingsResult"/>'s own doc comment), never replaced with a
/// nullable "maybe" field, which would just be the same invented-vs-honest problem one layer down.
///
/// Both are the plan's own named carve-out - facts only the shell can observe:
/// <list type="bullet">
/// <item><b>launchAtLogin</b> is an <c>SMAppService.mainApp</c> registration - an AppKit/
/// ServiceManagement fact this headless .NET core has no access to and can never observe (it does
/// not run as, or alongside, the Swift process; it cannot even run on a platform where
/// <c>SMAppService</c> exists in every deployment). The OS owns this fact outright. See
/// <c>Shell~/HadesApp/Sources/HadesApp/ShellFacts/LaunchAtLoginService.swift</c>, which reads
/// <c>SMAppService.mainApp.status</c> directly and reflects it AFTER every toggle, never the
/// requested value.</item>
/// <item><b>resourceGuards</b> (Low Power Mode, thermal pressure) are <c>ProcessInfo</c> signals
/// about the shell's own machine - equally unobservable from this process. See
/// <c>Shell~/HadesApp/Sources/HadesApp/ShellFacts/ResourceGuardReader.swift</c>.</item>
/// </list>
///
/// <b>Unity Hub discovery opt-in and update channel remain out of scope</b>, unchanged from Plan
/// 11's own decision (still true today: Hub discovery is "its own piece of work" with no discovery
/// mechanism built anywhere in this codebase yet; update channel "belongs with spec #4", explicitly
/// out of Plan 13 too - see that plan's own "Explicitly out" section). Neither is an OS fact the
/// shell can read on its own the way launchAtLogin/resourceGuards are, so neither gets the same
/// carve-out treatment; both stay a real API gap for a later task, not invented here.
///
/// <b>mcpPort - and the self-conflict defect this fixed.</b> The MCP endpoint's documented, fixed
/// port (<see cref="McpPort"/> = 7823 - the same literal already load-bearing in
/// OriginValidationTests.cs and this file's own sibling ControlListener.cs's doc comment: the
/// Claude Code plugin dials this exact number statically). <see cref="McpPortSetting.InUse"/> is a
/// REAL <see cref="TcpListener"/> bind-then-release probe against loopback (never cached) - but
/// <see cref="Build"/> only ever RUNS that probe when this instance's own MCP endpoint is NOT
/// itself bound to the documented port. Reaching <c>GET /control/settings</c> at all already
/// proves this process bound its OWN MCP endpoint somewhere (a failed bind exits the process
/// before this listener is ever started - see Program.cs's own comment on that ordering), so when
/// that somewhere IS the documented port, nothing else can possibly hold it too - a live probe
/// there could only ever observe this process's own bind and misreport it as a conflict, because
/// two sockets cannot share one port even within the same process. This was a real, live-verified
/// defect (Plan 13 Task 8): "a normally-running, unconflicted Hades instance's own successful MCP
/// bind makes CanBindLoopback(7823) fail too... so /control/settings reads inUse: true on
/// essentially every healthy run." The fix is <see cref="Build"/>'s own <c>actualMcpPort</c>
/// parameter (Program.cs supplies <see cref="McpBinding.ResolveBoundPort"/> of
/// <see cref="McpBinding.ResolveBindUrl"/>'s own result): only when it differs from the documented
/// port - meaning ASPNETCORE_URLS explicitly moved this instance's MCP endpoint elsewhere - is the
/// documented port a socket this process never touches, so probing it is trustworthy and a
/// positive result names a genuine external conflict. <see cref="Build"/>'s own <c>mcpPort</c>
/// parameter (independent of <c>actualMcpPort</c>) is what makes this honestly testable without
/// depending on whatever happens to occupy the real 7823 on the machine running the test. When a
/// conflict IS found, <see cref="McpPortSetting.Message"/> ends with
/// <see cref="McpBinding.RemedyForPortInUse"/> - the same actionable recommendation a hard startup
/// failure gives, authored once and shared, not paraphrased here - and, because the probe is now
/// trustworthy, no longer hedges between "this instance" and "another process": a true result can
/// only ever mean the latter.
///
/// <b>logLevel.</b> Read from the REAL, currently-running ASP.NET Core
/// <see cref="IConfiguration"/> (<c>Logging:LogLevel:Default</c>, appsettings.json - "Information"
/// today) - genuinely live, not a second hardcoded copy that could silently drift from what the
/// process is actually logging at.
///
/// <b>Design decision this task made that the plan did not specify: no settings-WRITE action.</b>
/// The plan's own Task 6 text describes Traces and Memory with explicit write/action surfaces
/// (memory's write-one-document, accept/dismiss/defer) but names none for Settings beyond "current
/// values and resolved conflict states" - and spec #1 §7's own surface list separates "settings"
/// from every explicitly-mutating item in the same sentence (add/remove projects, force-release a
/// lease, edit memory). logLevel is therefore reported as a real value with no write path of its
/// own here - building a persistence format and a POST route nothing asked for would be exactly the
/// speculative abstraction this codebase's own "minimum code that solves the problem" standard
/// argues against.
/// </summary>
public static class SettingsEndpoint
{
    /// <summary>The MCP endpoint's documented, fixed port - see this class's own doc comment.
    /// Sourced from <see cref="Hades.Core.Mcp.McpDefaults.Port"/>, the single place the literal
    /// 7823 is written - see that type's own doc comment for why it lives in Hades.Core rather
    /// than here.</summary>
    public const int McpPort = Hades.Core.Mcp.McpDefaults.Port;

    const string DefaultLogLevel = "Information";

    /// <summary>Orchestrates real state into a <see cref="SettingsResult"/>: a live loopback bind
    /// probe for <paramref name="mcpPort"/> - but ONLY when <paramref name="actualMcpPort"/>
    /// differs from it - and the app's real <paramref name="configuration"/> for logLevel, fed to
    /// the pure <see cref="Resolve"/>.
    ///
    /// <paramref name="mcpPort"/> is the documented port to check for a conflict; defaults to the
    /// real <see cref="McpPort"/> (7823) and is independently overridable so tests can probe a
    /// controlled, OS-assigned port instead of depending on whatever this machine happens to have
    /// bound to 7823.
    ///
    /// <paramref name="actualMcpPort"/> is the port THIS running instance's own MCP endpoint is
    /// actually bound to - Program.cs supplies <see cref="McpBinding.ResolveBoundPort"/> of
    /// <see cref="McpBinding.ResolveBindUrl"/>'s own result via <see cref="ControlListener"/>'s
    /// <c>actualMcpPort</c> constructor parameter. Defaults to <paramref name="mcpPort"/> itself -
    /// the ordinary, unmodified case.
    ///
    /// <b>THE FIX, in one sentence:</b> when <paramref name="actualMcpPort"/> equals
    /// <paramref name="mcpPort"/>, <paramref name="mcpPort"/> is never probed at all. This method
    /// being reachable already proves this same process's own MCP endpoint bound that exact port
    /// successfully (see <see cref="McpBinding.Run"/> - a failed bind exits the process before
    /// <c>/control/settings</c> could ever be wired up to answer this call), so a live probe there
    /// could only ever observe that same bind and misreport it as a conflict: two sockets cannot
    /// share one port, even in the same process (the live finding behind this fix - see this
    /// class's own doc comment). Only when <paramref name="actualMcpPort"/> differs - meaning
    /// ASPNETCORE_URLS explicitly moved this instance's MCP endpoint elsewhere - is
    /// <paramref name="mcpPort"/> a port this process never touches, so probing it actually answers
    /// a question this process cannot answer about itself, and a positive result names a REAL
    /// external conflict worth surfacing.</summary>
    public static SettingsResult Build(IConfiguration configuration, int mcpPort = McpPort, int actualMcpPort = McpPort)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var onDocumentedPort = actualMcpPort == mcpPort;

        var snapshot = new SettingsSnapshot
        {
            McpPort = actualMcpPort,
            McpPortInUse = !onDocumentedPort && !CanBindLoopback(mcpPort),
            LogLevel = configuration["Logging:LogLevel:Default"] ?? DefaultLogLevel,
        };

        return Resolve(snapshot);
    }

    /// <summary>The pure resolution core - see this class's own doc comment for exactly how each
    /// field is decided. <paramref name="s"/>'s own <see cref="SettingsSnapshot.McpPort"/> is
    /// wherever THIS instance is actually running (see <see cref="Build"/>'s own doc comment); the
    /// conflict message below names that alongside the documented <see cref="McpPort"/> constant
    /// specifically, since <see cref="Build"/> guarantees <see cref="SettingsSnapshot.McpPortInUse"/>
    /// is only ever true when the two differ.</summary>
    public static SettingsResult Resolve(SettingsSnapshot s) => new()
    {
        McpPort = new McpPortSetting
        {
            Port = s.McpPort,
            InUse = s.McpPortInUse,
            Message = s.McpPortInUse
                ? $"Hades is running on port {s.McpPort} — the documented MCP port {McpPort} is "
                    + "already in use by another process. " + McpBinding.RemedyForPortInUse(McpPort)
                : $"Port {s.McpPort} is available.",
        },
        LogLevel = new LogLevelSetting { Level = s.LogLevel },
    };

    /// <summary>True when <paramref name="port"/> can be bound on loopback right now (and is
    /// therefore free) - binds and immediately releases, never holding it. False for any bind
    /// failure, which on a loopback address means exactly one thing: something else already has it.</summary>
    static bool CanBindLoopback(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
