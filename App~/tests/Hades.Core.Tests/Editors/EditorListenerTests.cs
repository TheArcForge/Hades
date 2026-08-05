using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Hades.Contract.Wire;
using Hades.Core.Editors;

namespace Hades.Core.Tests.Editors;

public sealed class EditorListenerTests : IDisposable
{
    readonly string _tokenPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "editor.token");

    /// <summary>Polls rather than sleeping a fixed period - a timing-sensitive test that passes
    /// on a fast machine and fails in CI is worse than no test. Same shape as
    /// ObservationServiceTests.Eventually.</summary>
    static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }

    static Hello MakeHello(string projectGuid, string unityVersion = "6000.3.2f1",
        string projectPath = "/tmp/project", string pluginVersion = "0.1.0", long processId = 1000) => new()
    {
        ProjectGuid = projectGuid,
        ProjectPath = projectPath,
        UnityVersion = unityVersion,
        PluginVersion = pluginVersion,
        ProcessId = processId,
    };

    /// <summary>Connects a real socket and plays the client (Unity plugin) side of the
    /// handshake: token line, then hello line. Leaves the connection open for the caller.</summary>
    static async Task<TcpClient> HandshakeAsync(EditorListener listener, Hello hello)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.Port);

        var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        await writer.WriteLineAsync(listener.Token);
        await writer.WriteLineAsync(MiniJson.Write(hello.ToJson()));

        return client;
    }

    /// <summary>A real, routable, non-loopback IPv4 address for this machine, if one is up - what
    /// an attacker elsewhere on the LAN would actually connect from. Falls back to a loopback
    /// alias (127.0.0.2 - still not 127.0.0.1, so still proves a specific-address bind) for a
    /// fully offline sandbox with no such interface. Empirically, macOS refuses a connection to
    /// the real address immediately but merely times out connecting to an unbound loopback alias
    /// - both prove unreachability, so the caller accepts either outcome.</summary>
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

    [Fact]
    public async Task BindsLoopbackOnly_IsNotReachableViaAnyOtherLocalAddress()
    {
        using var listener = new EditorListener(_tokenPath, new EditorRegistry());
        listener.Start();

        // Sanity: the listener really is up on loopback, so the refusal below is meaningful and
        // not just "nothing is listening at all".
        using (var sanity = new TcpClient())
        {
            await sanity.ConnectAsync(IPAddress.Loopback, listener.Port);
            Assert.True(sanity.Connected);
        }

        // The listener is bound to the single specific address 127.0.0.1 (IPAddress.Loopback), so
        // the OS never accepts a connection addressed anywhere else - proven here against a real
        // non-loopback address where possible. See FindProbeAddress for why either a refusal or a
        // timeout counts as proof: which one happens is a platform detail, not the property under
        // test.
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<Exception>(
            () => client.ConnectAsync(FindProbeAddress(), listener.Port, cts.Token).AsTask());
    }

    [Fact]
    public async Task NoToken_ConnectionIsClosedWithoutRegisteringAnything()
    {
        var registry = new EditorRegistry();
        using var listener = new EditorListener(_tokenPath, registry);
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.Port);
        var stream = client.GetStream();
        client.Client.Shutdown(SocketShutdown.Send); // "no token": disconnect without sending a byte

        var buffer = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = await stream.ReadAsync(buffer, cts.Token);

        Assert.Equal(0, read); // the server closed its side too, not just went quiet
        Assert.Empty(registry.All());
    }

    [Fact]
    public async Task WrongToken_ConnectionIsClosedWithoutProcessingTheHelloThatFollows()
    {
        var registry = new EditorRegistry();
        using var listener = new EditorListener(_tokenPath, registry);
        listener.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.Port);
        var stream = client.GetStream();
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        await writer.WriteLineAsync("definitely-the-wrong-token");
        // A well-formed hello follows anyway - it must never be parsed or registered, because the
        // mismatch above must already have closed the connection first.
        await writer.WriteLineAsync(MiniJson.Write(MakeHello("aaaabbbbccccddddeeeeffff00001111").ToJson()));

        var buffer = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = await stream.ReadAsync(buffer, cts.Token);

        Assert.Equal(0, read);
        Assert.Empty(registry.All());
    }

    [Fact]
    public void Start_WritesTheTokenFileAtMode0600()
    {
        // Hades targets macOS; Unix file mode has no meaning on Windows, and File.GetUnixFileMode
        // is unsupported there. OperatingSystem.IsWindows() is the analyzer-recognised guard.
        if (OperatingSystem.IsWindows()) return;

        using var listener = new EditorListener(_tokenPath, new EditorRegistry());
        listener.Start();

        var mode = File.GetUnixFileMode(_tokenPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Start_WritesTheConnectionFileContainingThePortAndToken()
    {
        // The plugin dials out, so it needs to learn which port to connect to as well as the
        // token to present - see EditorConnectionInfo. The listener binds an OS-assigned
        // ephemeral port (port 0 requested), so the file must carry the ACTUAL bound port, not
        // the requested one.
        using var listener = new EditorListener(_tokenPath, new EditorRegistry());
        listener.Start();

        Assert.False(string.IsNullOrEmpty(listener.Token));
        Assert.NotEqual(0, listener.Port);

        Assert.True(EditorConnectionInfo.TryParse(File.ReadAllText(_tokenPath), out var info, out var error), error);
        Assert.Equal(listener.Port, info!.Port);
        Assert.Equal(listener.Token, info.Token);
    }

    [Fact]
    public async Task ValidHello_RegistersTheEditorUnderItsProjectGuidWithFullDetails()
    {
        var registry = new EditorRegistry();
        using var listener = new EditorListener(_tokenPath, registry);
        listener.Start();
        const string guid = "aaaabbbbccccddddeeeeffff00001111";

        using var client = await HandshakeAsync(listener,
            MakeHello(guid, unityVersion: "6000.3.2f1", projectPath: "/tmp/my-project", pluginVersion: "0.9.1", processId: 4321));

        Assert.True(await Eventually(() => registry.Get(guid) is not null), "the editor never registered");

        var editor = registry.Get(guid);
        Assert.Equal("6000.3.2f1", editor!.Hello.UnityVersion);
        Assert.Equal("/tmp/my-project", editor.Hello.ProjectPath);
        Assert.Equal("0.9.1", editor.Hello.PluginVersion);
        Assert.Equal(4321, editor.Hello.ProcessId);
    }

    [Fact]
    public async Task Disconnect_DeregistersTheEditor()
    {
        var registry = new EditorRegistry();
        using var listener = new EditorListener(_tokenPath, registry);
        listener.Start();
        const string guid = "aaaabbbbccccddddeeeeffff00001111";

        var client = await HandshakeAsync(listener, MakeHello(guid));
        Assert.True(await Eventually(() => registry.Get(guid) is not null));

        client.Close();

        Assert.True(await Eventually(() => registry.Get(guid) is null), "disconnect never deregistered the editor");
    }

    [Fact]
    public async Task TwoEditorsSameProject_NewestWins_AndTheOldOnesLateDisconnectDoesNotEvictIt()
    {
        // The exact race the plan calls out: a user reopens Unity while the old connection is
        // still dying. Both connections are real sockets here, not the registry exercised
        // directly - this proves the LISTENER wires token+hello+registration+deregistration
        // together correctly under the race, not just the registry's own bookkeeping.
        var registry = new EditorRegistry();
        using var listener = new EditorListener(_tokenPath, registry);
        listener.Start();
        const string guid = "aaaabbbbccccddddeeeeffff00001111";

        var oldClient = await HandshakeAsync(listener, MakeHello(guid, processId: 1));
        Assert.True(await Eventually(() => registry.Get(guid)?.Hello.ProcessId == 1));

        var newClient = await HandshakeAsync(listener, MakeHello(guid, processId: 2));
        Assert.True(await Eventually(() => registry.Get(guid)?.Hello.ProcessId == 2));
        Assert.Single(registry.All());

        oldClient.Close(); // the old connection finally notices it is dead

        // Give the old session's belated deregistration a real chance to run, then assert it did
        // NOT evict the newer registration - the specific defect this policy exists to prevent.
        Assert.True(await Eventually(() => registry.Get(guid)?.Hello.ProcessId == 2 && registry.All().Count == 1),
            "the old connection's deregistration evicted (or duplicated) the newer registration");

        newClient.Close();
    }

    [Fact]
    public async Task ConcurrentConnectDisconnectAndQuery_NeverThrowsAndEndsWithNothingRegistered()
    {
        var registry = new EditorRegistry();
        using var listener = new EditorListener(_tokenPath, registry);
        listener.Start();

        const int clientCount = 16;
        const int cyclesPerClient = 10;

        var readerCts = new CancellationTokenSource();
        var readerTask = Task.Run(async () =>
        {
            // Concurrent queries, running the whole time connections are churning - must never
            // throw or observe a torn collection.
            while (!readerCts.IsCancellationRequested)
            {
                _ = registry.All().Count;
                await Task.Yield();
            }
        });

        var workers = Enumerable.Range(0, clientCount).Select(async clientIndex =>
        {
            var guid = $"client-{clientIndex}";
            for (var cycle = 0; cycle < cyclesPerClient; cycle++)
            {
                using var client = await HandshakeAsync(listener, MakeHello(guid, processId: cycle));
                Assert.True(await Eventually(() => registry.Get(guid)?.Hello.ProcessId == cycle, timeoutMs: 5000));
                client.Close();
            }
        });

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));
        readerCts.Cancel();
        await readerTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(await Eventually(() => registry.All().Count == 0, timeoutMs: 10000),
            "some editor's deregistration was lost under concurrent load");
    }

    // ---------------------------------------------------------------- reconnect reconciliation wiring
    //
    // EditorListener.Register is where a "reconnect" concretely happens - see LeaseRegistry's own
    // class doc comment on why ReconcileAsync must run right there. These tests prove the WIRING
    // (constructor plumbing, fire-and-forget dispatch, exception containment); LeaseRegistryTests
    // already covers ReconcileAsync's own reconciliation logic exhaustively against a raw
    // EditorSession, so these stay focused on what only EditorListener itself can prove.

    [Fact]
    public async Task Registering_WithABelievedLeaseForItsProject_SendsAReconcilingLeaseRenew_AndUpdatesTheBelief()
    {
        var registry = new EditorRegistry();
        var leases = new LeaseRegistry();
        const string guid = "aaaabbbbccccddddeeeeffff00001111";
        leases.RecordHeld(guid, "lease-1", DateTimeOffset.UtcNow.AddSeconds(30));

        using var listener = new EditorListener(_tokenPath, registry, leases);
        listener.Start();

        using var client = await HandshakeAsync(listener, MakeHello(guid));

        // Registration itself must already be visible even though the reconcile round trip below
        // has not completed yet - reconciliation must never gate registration.
        Assert.True(await Eventually(() => registry.Get(guid) is not null), "the editor never registered");

        var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
        var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(JsonRpcRequest.TryParse(line, out var request, out _));
        Assert.Equal("lease.renew", request!.Method);
        Assert.True(request.Params!.TryGetProperty("leaseId", out var idValue));
        Assert.Equal("lease-1", idValue!.AsString());

        var newExpiry = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddSeconds(45).ToUnixTimeMilliseconds());
        var result = JsonValue.NewObject();
        result.SetProperty("success", JsonValue.Bool(true));
        result.SetProperty("leaseId", JsonValue.String("lease-1"));
        result.SetProperty("expiresAtUtcMs", JsonValue.Integer(newExpiry.ToUnixTimeMilliseconds()));
        await writer.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request.Id!, result).ToJson()));

        Assert.True(await Eventually(() => leases.Get(guid)?.ExpiresAtUtc == newExpiry),
            "the believed lease was never updated from the reconciling lease.renew's response");
    }

    [Fact]
    public async Task Registering_WithNothingBelievedHeldForItsProject_RegistersNormally_BeliefStaysEmpty()
    {
        var registry = new EditorRegistry();
        var leases = new LeaseRegistry();
        const string guid = "aaaabbbbccccddddeeeeffff00001111";

        using var listener = new EditorListener(_tokenPath, registry, leases);
        listener.Start();

        using var client = await HandshakeAsync(listener, MakeHello(guid));

        Assert.True(await Eventually(() => registry.Get(guid) is not null), "the editor never registered");
        Assert.Null(leases.Get(guid)); // nothing was ever believed held, so there is nothing to reconcile
    }

    [Fact]
    public async Task Registering_WhenReconciliationGetsAnErrorResponse_EditorStaysAttached_BeliefLeftUntouched()
    {
        // The exact case Part A's wiring exists for: a plugin that CANNOT answer lease.renew (here,
        // a real JSON-RPC error - e.g. an older plugin build with no lease.* handler at all) must
        // leave the editor attached, not un-register it - and must leave the believed lease exactly
        // as it was, neither confirmed nor cleared, since an inconclusive attempt is not evidence
        // either way (see LeaseRegistry.ReconcileAsync's own doc comment).
        var registry = new EditorRegistry();
        var leases = new LeaseRegistry();
        const string guid = "aaaabbbbccccddddeeeeffff00001111";
        var originalExpiry = DateTimeOffset.UtcNow.AddSeconds(30);
        leases.RecordHeld(guid, "lease-1", originalExpiry);

        using var listener = new EditorListener(_tokenPath, registry, leases);
        listener.Start();

        using var client = await HandshakeAsync(listener, MakeHello(guid));
        Assert.True(await Eventually(() => registry.Get(guid) is not null), "the editor never registered");

        var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
        var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(JsonRpcRequest.TryParse(line, out var request, out _));
        Assert.Equal("lease.renew", request!.Method);

        await writer.WriteLineAsync(MiniJson.Write(
            JsonRpcResponse.Failure(request!.Id!, -32601, "Method not found").ToJson()));

        // Bounded settle: there is no state transition to poll for (nothing is SUPPOSED to change),
        // so this gives the fire-and-forget reconcile - and its swallowed exception - a generous,
        // fixed window to actually run before asserting nothing happened.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.NotNull(registry.Get(guid)); // still attached - reconciliation failing must never detach it
        var stillBelieved = leases.Get(guid);
        Assert.NotNull(stillBelieved);
        Assert.Equal("lease-1", stillBelieved!.LeaseId);
        Assert.Equal(originalExpiry, stillBelieved.ExpiresAtUtc); // untouched - not cleared, not updated
    }

    // ---------------------------------------------------------------- disconnect clears the believed lease
    //
    // The gap this fixes: Register's Disconnected handler (above) already deregisters the editor,
    // but until now never told LeaseRegistry the Editor was gone - so the app kept believing a
    // lease was held long after the plugin's own ReloadGate.ReleaseOnDisconnect (plan 8) had
    // already freed the real lock. These tests drive the SAME newest-wins guard
    // EditorRegistry.Deregister already applies to itself (see
    // TwoEditorsSameProject_NewestWins_AndTheOldOnesLateDisconnectDoesNotEvictIt above), extended
    // to LeaseRegistry.

    [Fact]
    public async Task Disconnect_ClearsABelievedHeldLeaseForItsProject()
    {
        // Connect first, record the believed lease SECOND - the realistic order (a lease is only
        // ever recorded once an Editor is already attached - see EditorsProgramWiringTests'
        // ControlListener_ReleaseAction_... test in Hades.Server.Tests for the same reasoning).
        // This also means registration's own reconcile-on-connect (see the section above) finds
        // nothing believed held yet and sends no lease.renew, so it cannot confound this test's
        // own disconnect-triggered clear.
        var registry = new EditorRegistry();
        var leases = new LeaseRegistry();
        const string guid = "aaaabbbbccccddddeeeeffff00001111";

        using var listener = new EditorListener(_tokenPath, registry, leases);
        listener.Start();

        var client = await HandshakeAsync(listener, MakeHello(guid));
        Assert.True(await Eventually(() => registry.Get(guid) is not null), "the editor never registered");

        leases.RecordHeld(guid, "lease-1", DateTimeOffset.UtcNow.AddSeconds(30));

        client.Close();

        Assert.True(await Eventually(() => registry.Get(guid) is null), "disconnect never deregistered the editor");
        Assert.True(await Eventually(() => leases.Get(guid) is null),
            "disconnect never cleared the believed-held lease - the app would keep showing a lease that no longer exists");
    }

    [Fact]
    public async Task TwoEditorsSameProject_OldConnectionsLateDisconnect_DoesNotClearTheNewerConnectionsLeaseBelief()
    {
        // The exact race TwoEditorsSameProject_NewestWins_AndTheOldOnesLateDisconnectDoesNotEvictIt
        // proves for EditorRegistry itself, extended to LeaseRegistry: a user reopens Unity while
        // the old connection is still dying. By the time the old connection's belated disconnect
        // fires, a lease is believed held for this project - one that legitimately belongs to the
        // NEW, still-live Editor (recorded only once the new connection is the current
        // registration, the realistic order a real lease.acquire through the now-attached new
        // Editor would follow). The old connection's cleanup must not clear it.
        var registry = new EditorRegistry();
        var leases = new LeaseRegistry();
        const string guid = "aaaabbbbccccddddeeeeffff00001111";

        using var listener = new EditorListener(_tokenPath, registry, leases);
        listener.Start();

        var oldClient = await HandshakeAsync(listener, MakeHello(guid, processId: 1));
        Assert.True(await Eventually(() => registry.Get(guid)?.Hello.ProcessId == 1));

        var newClient = await HandshakeAsync(listener, MakeHello(guid, processId: 2));
        Assert.True(await Eventually(() => registry.Get(guid)?.Hello.ProcessId == 2));

        leases.RecordHeld(guid, "lease-new", DateTimeOffset.UtcNow.AddSeconds(30));

        oldClient.Close(); // the old connection finally notices it is dead

        // Bounded settle, not Eventually: there is no state transition to poll FOR here - the
        // whole point under test is that nothing is SUPPOSED to change - so this gives the old
        // session's belated disconnect handler a generous, fixed window to actually run (and, if
        // the guard were missing, to wrongly clear the lease) before asserting nothing happened.
        // Same reasoning as
        // Registering_WhenReconciliationGetsAnErrorResponse_EditorStaysAttached_BeliefLeftUntouched's
        // own "Bounded settle" comment above.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.Equal(2, registry.Get(guid)?.Hello.ProcessId); // still the newer editor, not evicted
        var stillBelieved = leases.Get(guid);
        Assert.NotNull(stillBelieved);
        Assert.Equal("lease-new", stillBelieved!.LeaseId); // untouched by the old connection's cleanup

        newClient.Close();
    }

    [Fact]
    public void Dispose_IsSafeToCallTwice()
    {
        var listener = new EditorListener(_tokenPath, new EditorRegistry());
        listener.Start();

        listener.Dispose();
        listener.Dispose();
    }

    [Fact]
    public async Task Dispose_StopsAcceptingNewConnections()
    {
        var listener = new EditorListener(_tokenPath, new EditorRegistry());
        listener.Start();
        var port = listener.Port;

        listener.Dispose();

        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port, cts.Token).AsTask());
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_tokenPath);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
