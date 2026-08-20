using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Hades.Core.Editors;
using Hades.Core.Storage;
using Hades.Server.Control;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests.Control;

/// <summary>
/// Direct construction, the same way EditorListenerTests exercises EditorListener: no Program.cs,
/// no WebApplicationFactory, just the listener itself against a real loopback socket. See
/// ControlListenerProgramWiringTests below for proof that Program.cs actually wires this in.
/// </summary>
public sealed class ControlAuthTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");

    /// <summary>A real, routable, non-loopback IPv4 address for this machine, if one is up - what
    /// an attacker elsewhere on the LAN would actually connect from. Falls back to a loopback
    /// alias (127.0.0.2 - still not 127.0.0.1, so still proves a specific-address bind) for a
    /// fully offline sandbox with no such interface. Empirically, macOS refuses a connection to
    /// the real address immediately but merely times out connecting to an unbound loopback alias -
    /// both prove unreachability, so the caller accepts either outcome. Same technique
    /// EditorListenerTests.FindProbeAddress uses for the editor link's own loopback-only listener.</summary>
    static IPAddress FindProbeAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                    return addr.Address;
            }
        }

        return IPAddress.Parse("127.0.0.2");
    }

    static HttpRequestMessage PingRequest(string? token, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/control/ping");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    static HttpClient ClientFor(ControlListener listener) => new() { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };

    [Fact]
    public async Task BindsLoopbackOnly_IsNotReachableViaAnyOtherLocalAddress()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();

        // Sanity: the listener really is up on loopback, so the refusal below is meaningful and
        // not just "nothing is listening at all".
        using (var sanity = new TcpClient())
        {
            await sanity.ConnectAsync(IPAddress.Loopback, listener.Port);
            Assert.True(sanity.Connected);
        }

        // The listener is bound to the single specific address 127.0.0.1, so the OS never accepts
        // a connection addressed anywhere else - proven here against a real non-loopback address
        // where possible. See FindProbeAddress for why either a refusal or a timeout counts as
        // proof: which one happens is a platform detail, not the property under test.
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<Exception>(
            () => client.ConnectAsync(FindProbeAddress(), listener.Port, cts.Token).AsTask());
    }

    [Fact]
    public async Task Ping_NoToken_IsRefused()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(PingRequest(token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ping_WrongToken_IsRefused()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(PingRequest(token: "definitely-the-wrong-token"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Ping_ValidToken_Succeeds_AndReportsVersionAndUptime()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(PingRequest(listener.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("version").GetString()));
        Assert.True(body.GetProperty("uptimeSeconds").GetDouble() >= 0);
    }

    [Fact]
    public async Task Ping_ForeignOrigin_IsRejectedWith403_EvenWithAValidToken()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(PingRequest(listener.Token, origin: "https://evil.example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Ping_MalformedOrigin_IsRejectedWith403()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(PingRequest(listener.Token, origin: "not-a-url"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Ping_LoopbackOrigin_IsAllowed()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(PingRequest(listener.Token, origin: "http://127.0.0.1:9999"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ping_AbsentOrigin_IsAllowed()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(PingRequest(listener.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Start_WritesTheDiscoveryFileAtMode0600()
    {
        // Hades targets macOS; Unix file mode has no meaning on Windows, and File.GetUnixFileMode
        // is unsupported there. OperatingSystem.IsWindows() is the analyzer-recognised guard - same
        // as EditorListenerTests.Start_WritesTheTokenFileAtMode0600.
        if (OperatingSystem.IsWindows()) return;

        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();

        var mode = File.GetUnixFileMode(ConnectionFilePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void WriteConnectionFile_CreatesTheFileDirectlyAtMode0600()
    {
        // Direct unit test of ControlAuth.WriteConnectionFile itself (public static, so callable
        // without a real listener/socket) - the fixed method creates the inode at 0600 in one
        // syscall (FileStreamOptions.UnixCreateMode) rather than the old write-at-default-mode-
        // then-chmod, which left the token briefly readable at 0644. Same OS guard as
        // Start_WritesTheDiscoveryFileAtMode0600 above.
        if (OperatingSystem.IsWindows()) return;

        ControlAuth.WriteConnectionFile(ConnectionFilePath, port: 12345, token: "test-token");

        var mode = File.GetUnixFileMode(ConnectionFilePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void WriteConnectionFile_NarrowsAPreExistingFilesModeTo0600()
    {
        // FileStreamOptions.UnixCreateMode only takes effect when the call creates a genuinely
        // NEW inode (see WriteConnectionFile's own doc comment) - a file already sitting at this
        // path (a stale discovery file from a previous run, left at whatever mode it had) is
        // reused/truncated by FileMode.Create instead, so this pins the defensive
        // File.SetUnixFileMode that still runs after the write: the end state must be 0600
        // regardless of what mode the file started at.
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(ConnectionFilePath, "stale");
        File.SetUnixFileMode(ConnectionFilePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        ControlAuth.WriteConnectionFile(ConnectionFilePath, port: 12345, token: "test-token");

        var mode = File.GetUnixFileMode(ConnectionFilePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Start_WritesTheConnectionFileContainingThePortAndToken()
    {
        // The listener binds an OS-assigned ephemeral port (port 0 requested), so the file must
        // carry the ACTUAL bound port, not the requested one - same property
        // EditorListenerTests.Start_WritesTheConnectionFileContainingThePortAndToken proves for
        // the editor link's own discovery file.
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();

        Assert.False(string.IsNullOrEmpty(listener.Token));
        Assert.NotEqual(0, listener.Port);

        var info = JsonSerializer.Deserialize<ControlConnectionInfo>(File.ReadAllText(ConnectionFilePath));
        Assert.NotNull(info);
        Assert.Equal(listener.Port, info!.Port);
        Assert.Equal(listener.Token, info.Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}

/// <summary>
/// Proves Program.cs actually wires ControlListener in - started once the host has genuinely
/// finished starting (see Program.cs's own comment on why this is no longer unconditional-at-the-
/// top, the Plan 13 Task 8 fix), reachable on its own real port with its own real token. TestServer
/// always finishes starting quickly (no real socket, no real bind conflict is possible), so that
/// gating is invisible here - this fixture still observes the listener already up by the time it
/// runs. The direct-construction tests above cover every behaviour in isolation; these cover the
/// wiring itself, the same division of labour OriginValidationTests/CharonStatusTests already use
/// for proving the editor link and MCP Origin validation are really connected in the real app.
/// </summary>
public sealed class ControlListenerProgramWiringTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ControlListenerProgramWiringTests(WebApplicationFactory<Program> factory)
    {
        // Isolates AppPaths to a throwaway directory - without this, ControlListener (started by
        // Program.cs once the host finishes starting, same as EditorListener starts unconditionally
        // above that) would write a real discovery file into this machine's actual
        // ~/Library/Application Support/Hades/. Same pattern every other WebApplicationFactory-based
        // fixture in this project uses.
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));
            }));
    }

    [Fact]
    public async Task Program_StartsARealControlListener_ReachableWithItsOwnToken()
    {
        var listener = _factory.Services.GetRequiredService<ControlListener>();
        var port = await ProgramWiringPort.WaitAsync(listener);
        Assert.False(string.IsNullOrEmpty(listener.Token));

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var request = new HttpRequestMessage(HttpMethod.Get, "/control/ping");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", listener.Token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EditorListenersToken_DoesNotAuthenticateAgainstTheControlListener()
    {
        // The two listeners are separate trust boundaries with independently generated secrets -
        // see ControlConnectionInfo's own doc comment for why they are not even the same wire
        // type. This proves it empirically: the editor link's real token, presented to the
        // control API, is rejected exactly like any other wrong token - it is impossible to
        // present one listener's token to the other and have it accepted.
        var editorListener = _factory.Services.GetRequiredService<EditorListener>();
        var controlListener = _factory.Services.GetRequiredService<ControlListener>();
        Assert.NotEqual(editorListener.Token, controlListener.Token);

        var controlPort = await ProgramWiringPort.WaitAsync(controlListener);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{controlPort}") };
        var request = new HttpRequestMessage(HttpMethod.Get, "/control/ping");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", editorListener.Token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public void Dispose()
    {
        // See EditorToolTestBase.Dispose's own comment: _factory is a fresh per-test
        // WebApplicationFactory whose own background services can still be touching _appRoot
        // until the host itself is disposed - which must happen before the delete below.
        _factory.Dispose();

        if (Directory.Exists(_appRoot)) Directory.Delete(_appRoot, recursive: true);
    }
}
