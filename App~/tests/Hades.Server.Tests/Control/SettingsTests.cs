using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Hades.Server.Control;
using Hades.Server.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hades.Server.Tests.Control;

/// <summary>
/// Pure, deterministic tests of <see cref="SettingsEndpoint.Resolve"/> - the message-formatting
/// logic behind <c>GET /control/settings</c>, exercised directly against a hand-built
/// <see cref="SettingsSnapshot"/>, no sockets, no configuration. Same "verbatim" discipline as
/// SummaryTests.cs's own SummaryResolveTests: every expected string below is a hand-typed literal.
/// See <see cref="SettingsBuildTests"/> for proof the mcpPort conflict state is actually derived
/// from a real, live TCP probe rather than assumed.
/// </summary>
public sealed class SettingsResolveTests
{
    static SettingsSnapshot Snapshot(bool mcpPortInUse = false) => new()
    {
        McpPort = 7823,
        McpPortInUse = mcpPortInUse,
        LogLevel = "Information",
    };

    [Fact]
    public void McpPortAvailable_MessageIsExactLiteral()
    {
        var result = SettingsEndpoint.Resolve(Snapshot(mcpPortInUse: false));

        Assert.Equal(7823, result.McpPort.Port);
        Assert.False(result.McpPort.InUse);
        Assert.Equal("Port 7823 is available.", result.McpPort.Message);
    }

    [Fact]
    public void McpPortInUse_MessageNamesTheActualPortAndTheDocumentedPortConflict_NoHedgeIncludesMcpBindingsOwnActionableRemedy()
    {
        // Defect fix: McpPortInUse is only ever true (see SettingsEndpoint.Build's own doc
        // comment) when the snapshot's McpPort - where this instance is ACTUALLY running - differs
        // from the documented SettingsEndpoint.McpPort constant (7823). That is precisely what
        // makes the probe trustworthy, so the message no longer hedges "either this Hades instance
        // itself, or another process" - once InUse can be true at all, it is proven to be someone
        // else; the hedge was the tell that the old signal never carried information. The remedy
        // text itself is not re-authored here - it is McpBinding's own carefully worded
        // recommendation (RemedyForPortInUse), shared verbatim so there is exactly one authored
        // "what do I do about this" sentence in the whole app, never two independently drifting
        // copies. Swift renders this message verbatim - see SettingsViewModelTests.
        var result = SettingsEndpoint.Resolve(Snapshot(mcpPortInUse: true) with { McpPort = 9999 });

        Assert.True(result.McpPort.InUse);
        Assert.Equal(9999, result.McpPort.Port);
        Assert.DoesNotContain("either this Hades instance itself", result.McpPort.Message);
        Assert.Equal(
            $"Hades is running on port 9999 — the documented MCP port {SettingsEndpoint.McpPort} is "
            + "already in use by another process. " + McpBinding.RemedyForPortInUse(SettingsEndpoint.McpPort),
            result.McpPort.Message);
        Assert.Contains("lsof -nP -iTCP:7823 -sTCP:LISTEN", result.McpPort.Message);
    }

    [Fact]
    public void McpPort_ReflectsWhicheverPortTheSnapshotNames_NotAHardcodedLiteral()
    {
        var result = SettingsEndpoint.Resolve(Snapshot() with { McpPort = 9999 });

        Assert.Equal(9999, result.McpPort.Port);
        Assert.Equal("Port 9999 is available.", result.McpPort.Message);
    }

    [Fact]
    public void LogLevel_PassesTheSnapshotValueThrough()
    {
        Assert.Equal("Debug", SettingsEndpoint.Resolve(Snapshot() with { LogLevel = "Debug" }).LogLevel.Level);
    }
}

/// <summary>
/// Proves <see cref="SettingsEndpoint.Build"/> - the impure orchestrator behind
/// <see cref="SettingsResolveTests"/>'s pure formatting - actually resolves mcpPort's conflict
/// state from a REAL, live TCP probe (never assumed, per this task's own "the shell shows the
/// conflict; it does not detect it" requirement) and logLevel from the REAL running
/// <see cref="IConfiguration"/>, never a hardcoded guess. A caller-supplied port (never the real
/// 7823) is used throughout so this cannot flake depending on what else happens to be listening on
/// this machine.
///
/// <b>Defect fix, the central claim these tests now pin:</b> <see cref="Build"/> only EVER probes
/// <c>mcpPort</c> (the documented port) when <c>actualMcpPort</c> (where this instance is really
/// running) differs from it. When they are equal - the ordinary, unmodified case - nothing is
/// probed at all, because this method being reachable already proves this process holds that port
/// (see <see cref="SettingsEndpoint.Build"/>'s own doc comment: a failed bind exits the process
/// before <c>/control/settings</c> could ever answer). The old implementation probed
/// unconditionally and so always observed its own bind and misreported it as a conflict - two
/// sockets can never share one port, even in the same process - which is exactly the live finding
/// Plan 13 Task 8 recorded: "a normally-running, unconflicted Hades instance's own successful MCP
/// bind makes CanBindLoopback(7823) fail too... so /control/settings reads inUse: true on
/// essentially every healthy run."
/// </summary>
public sealed class SettingsBuildTests
{
    static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    [Fact]
    public void Build_ActualPortEqualsMcpPort_NeverProbes_AlwaysReportsNotInUse()
    {
        // THE regression test: bind a real port and hand it to Build as BOTH mcpPort and
        // actualMcpPort - exactly Program.cs's normal, unmodified-ASPNETCORE_URLS shape, where the
        // MCP endpoint's own real bind stands in for "this instance holds the documented port."
        // The old implementation probed this exact port regardless and could only ever observe it
        // as held - an unwinnable, always-false probe. Fixed: equal actual/documented ports mean
        // nothing is probed, so this passes even though the port is verifiably, provably bound for
        // the whole duration of the call.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var result = SettingsEndpoint.Build(EmptyConfiguration(), mcpPort: port, actualMcpPort: port);

        Assert.False(result.McpPort.InUse);
        Assert.Equal(port, result.McpPort.Port);
        listener.Stop();
    }

    [Fact]
    public void Build_ActualPortDiffersFromMcpPort_AndMcpPortIsFree_ReportsNotInUse()
    {
        // The overridden-but-uncontested case: ASPNETCORE_URLS moved this instance elsewhere, and
        // the documented port genuinely has nothing else on it either - not a conflict, just a
        // deliberately different address. Two independent ephemeral ports so this never depends on
        // (or risks colliding with) the real 7823.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var mcpPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // free again at the moment Build probes it

        using var actual = new TcpListener(IPAddress.Loopback, 0);
        actual.Start();
        var actualPort = ((IPEndPoint)actual.LocalEndpoint).Port;

        var result = SettingsEndpoint.Build(EmptyConfiguration(), mcpPort: mcpPort, actualMcpPort: actualPort);

        Assert.False(result.McpPort.InUse);
        Assert.Equal(actualPort, result.McpPort.Port);
        actual.Stop();
    }

    [Fact]
    public void Build_ActualPortDiffersFromMcpPort_AndMcpPortIsHeldByAnotherProcess_ReportsInUse()
    {
        // The real conflict this whole fix exists to surface: this instance runs elsewhere
        // (actualPort), and the documented port is genuinely held by something this process never
        // touched - so the live probe against it is trustworthy, unlike the self-conflicting case
        // above.
        using var mcpPortListener = new TcpListener(IPAddress.Loopback, 0);
        mcpPortListener.Start();
        var mcpPort = ((IPEndPoint)mcpPortListener.LocalEndpoint).Port;

        var actualProbe = new TcpListener(IPAddress.Loopback, 0);
        actualProbe.Start();
        var actualPort = ((IPEndPoint)actualProbe.LocalEndpoint).Port;
        actualProbe.Stop(); // Build never probes actualMcpPort, only mcpPort - fine either way

        var result = SettingsEndpoint.Build(EmptyConfiguration(), mcpPort: mcpPort, actualMcpPort: actualPort);

        Assert.True(result.McpPort.InUse);
        Assert.Equal(actualPort, result.McpPort.Port);
        mcpPortListener.Stop();
    }

    [Fact]
    public void Build_DefaultPort_Is7823_TheDocumentedMcpEndpointPort()
    {
        // Deliberately does not assert InUse either way here - whether 7823 happens to be free on
        // this machine is not this test's concern (see the two tests above for that, against a
        // controlled port). This only pins the documented default value itself.
        var result = SettingsEndpoint.Build(EmptyConfiguration());

        Assert.Equal(7823, result.McpPort.Port);
        Assert.Equal(SettingsEndpoint.McpPort, result.McpPort.Port);
    }

    [Fact]
    public void Build_NoConfiguredLogLevel_FallsBackToInformation()
    {
        var result = SettingsEndpoint.Build(EmptyConfiguration());

        Assert.Equal("Information", result.LogLevel.Level);
    }

    [Fact]
    public void Build_ReadsLogLevelFromTheRealConfiguration_NotHardcoded()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Debug" })
            .Build();

        var result = SettingsEndpoint.Build(configuration);

        Assert.Equal("Debug", result.LogLevel.Level);
    }
}

/// <summary>
/// <c>GET /control/settings</c> over real HTTP against a directly-constructed
/// <see cref="ControlListener"/> - same style as every other Control *EndpointHttpTests class:
/// proving auth/Origin/routing/shape, not re-proving the resolution logic already covered above.
/// </summary>
public sealed class SettingsEndpointHttpTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");

    static HttpRequestMessage Request(HttpMethod method, string path, string? token, string? origin = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    static HttpClient ClientFor(ControlListener listener) => new() { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };

    [Fact]
    public async Task GetSettings_NoToken_IsRefused()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/settings", token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSettings_ForeignOrigin_IsRejectedWith403_EvenWithAValidToken()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/settings", listener.Token, origin: "https://evil.example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetSettings_ValidToken_Returns200WithEveryExpectedSection()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/settings", listener.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(7823, body.GetProperty("mcpPort").GetProperty("port").GetInt32());
        Assert.True(body.GetProperty("mcpPort").TryGetProperty("inUse", out _));
        Assert.True(body.GetProperty("mcpPort").TryGetProperty("message", out _));
        Assert.Equal("Information", body.GetProperty("logLevel").GetProperty("level").GetString());
    }

    [Fact]
    public async Task GetSettings_ReflectsTheConstructorSuppliedActualMcpPort()
    {
        // Proves ControlListener actually plumbs its own actualMcpPort constructor argument
        // through to SettingsEndpoint.Build, rather than silently ignoring it - the wiring seam
        // Program.cs's own real McpBinding.ResolveBoundPort(mcpBindUrl) value depends on. Same
        // "wiring, not resolution logic" split as this class's own doc comment: SettingsBuildTests
        // already proves the conflict-detection algorithm itself in full isolation on controlled
        // ports nowhere near the real 7823. This deliberately does not assert inUse either way -
        // whether the real, literal 7823 happens to be free on the machine running this test is
        // not this test's concern.
        using var listener = new ControlListener(ConnectionFilePath, actualMcpPort: 54321);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/settings", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(54321, body.GetProperty("mcpPort").GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task GetSettings_NeverReturnsLaunchAtLoginOrResourceGuards_NeitherCanBeSubstantiatedByThisProcess()
    {
        // Plan 13 Task 7's own governing rule: an endpoint that omits a value it cannot know is
        // honest; one that invents a value is not. launchAtLogin (an SMAppService registration) and
        // resourceGuards (Low Power Mode / thermal state, ProcessInfo signals) are OS facts about
        // the Swift shell's own process and machine - this headless .NET core cannot observe either,
        // so the wire contract omits both keys entirely rather than sending a guess. See
        // Sources/HadesApp/ShellFacts/{LaunchAtLoginService,ResourceGuardReader}.swift for where
        // each now actually lives.
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/settings", listener.Token));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.TryGetProperty("launchAtLogin", out _));
        Assert.False(body.TryGetProperty("resourceGuards", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}

/// <summary>
/// Proves Program.cs actually wires <c>/control/settings</c> up to the real ASP.NET Core
/// <see cref="IConfiguration"/> (appsettings.json's own <c>Logging:LogLevel:Default</c> = "Information")
/// - same division of labour as every other *ProgramWiringTests class in this directory.
/// </summary>
public sealed class SettingsProgramWiringTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly WebApplicationFactory<Program> _factory;

    public SettingsProgramWiringTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task ControlListener_SettingsEndpoint_ReflectsTheRealAppsettingsLogLevel()
    {
        var listener = _factory.Services.GetRequiredService<ControlListener>();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };
        var request = new HttpRequestMessage(HttpMethod.Get, "/control/settings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", listener.Token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Information", body.GetProperty("logLevel").GetProperty("level").GetString());
    }
}
