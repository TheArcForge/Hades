using System.Net;
using System.Net.Sockets;
using System.Text;
using Hades.Contract.Wire;
using Hades.Core.Editors;

namespace Hades.Core.Tests.Editors;

/// <summary>
/// LeaseRegistry in isolation: the pure believed-state store (RecordHeld/Clear/Get, and
/// RecordHeld's AcquiredAtUtc-preserving-across-a-renew behaviour), plus ReconcileAsync's
/// reconnect story - see the class doc comment on LeaseRegistry. The reconnect tests use a real
/// loopback socket pair with this test playing "Unity" directly, wrapping the server end in a
/// real EditorSession - same pattern as EditorSessionTests.
/// </summary>
public sealed class LeaseRegistryTests : IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff00001111";

    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly List<IDisposable> _toDispose = [];

    public LeaseRegistryTests() => _listener.Start();

    static Hello MakeHello() => new()
    {
        ProjectGuid = ProjectGuid,
        ProjectPath = "/tmp/some-project",
        UnityVersion = "6000.3.2f1",
        PluginVersion = "1.2.0",
        ProcessId = 4242,
    };

    /// <summary>Connects a real loopback socket pair: the returned session wraps the server end
    /// (exactly as EditorListener would after a handshake), and the reader/writer wrap the client
    /// end for the test to act as the "Unity" peer - same pattern as EditorSessionTests.</summary>
    async Task<(EditorSession Session, StreamReader UnityReads, StreamWriter UnityWrites)> ConnectAsync()
    {
        var acceptTask = _listener.AcceptTcpClientAsync();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        var server = await acceptTask;

        _toDispose.Add(client);
        _toDispose.Add(server);

        var session = new EditorSession(server.GetStream(), MakeHello());
        _toDispose.Add(session);
        session.Start();

        var unityReads = new StreamReader(client.GetStream(), new UTF8Encoding(false));
        var unityWrites = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        return (session, unityReads, unityWrites);
    }

    /// <summary>Builds a lease.* wire result exactly as HadesBoot's handlers do - see HadesBoot.cs
    /// (BuildLeaseResult) for the plugin side of this same shape.</summary>
    static JsonValue LeaseResult(bool success, string? leaseId, DateTimeOffset? expiresAtUtc)
    {
        var obj = JsonValue.NewObject();
        obj.SetProperty("success", JsonValue.Bool(success));
        obj.SetProperty("leaseId", leaseId is null ? JsonValue.Null : JsonValue.String(leaseId));
        obj.SetProperty("expiresAtUtcMs", expiresAtUtc is null ? JsonValue.Null : JsonValue.Integer(expiresAtUtc.Value.ToUnixTimeMilliseconds()));
        return obj;
    }

    // ---------------------------------------------------------------- pure store behaviour

    [Fact]
    public void Get_OnAnEmptyRegistry_ReturnsNull()
    {
        var registry = new LeaseRegistry();

        Assert.Null(registry.Get(ProjectGuid));
    }

    [Fact]
    public void RecordHeld_ThenGet_ReturnsWhatWasRecorded()
    {
        var now = DateTimeOffset.UtcNow;
        var registry = new LeaseRegistry(() => now);
        var expiry = now.AddSeconds(30);

        registry.RecordHeld(ProjectGuid, "lease-1", expiry);

        var found = registry.Get(ProjectGuid);
        Assert.NotNull(found);
        Assert.Equal(ProjectGuid, found!.ProductGuid);
        Assert.Equal("lease-1", found.LeaseId);
        Assert.Equal(expiry, found.ExpiresAtUtc);
        Assert.Equal(now, found.AcquiredAtUtc);
    }

    [Fact]
    public void RecordHeld_RenewingTheSameLeaseId_PreservesTheOriginalAcquiredAtUtc()
    {
        var t0 = DateTimeOffset.UtcNow;
        var clock = t0;
        var registry = new LeaseRegistry(() => clock);
        registry.RecordHeld(ProjectGuid, "lease-1", t0.AddSeconds(30));

        clock = t0.AddSeconds(10); // ten seconds later, a renew arrives for the SAME lease id
        registry.RecordHeld(ProjectGuid, "lease-1", clock.AddSeconds(30));

        var found = registry.Get(ProjectGuid);
        Assert.Equal(t0, found!.AcquiredAtUtc);
        Assert.Equal(clock.AddSeconds(30), found.ExpiresAtUtc);
    }

    [Fact]
    public void RecordHeld_WithADifferentLeaseId_ResetsAcquiredAtUtc()
    {
        var t0 = DateTimeOffset.UtcNow;
        var clock = t0;
        var registry = new LeaseRegistry(() => clock);
        registry.RecordHeld(ProjectGuid, "lease-1", t0.AddSeconds(30));

        clock = t0.AddSeconds(10); // a genuinely different lease, not a continuation
        registry.RecordHeld(ProjectGuid, "lease-2", clock.AddSeconds(30));

        var found = registry.Get(ProjectGuid);
        Assert.Equal("lease-2", found!.LeaseId);
        Assert.Equal(clock, found.AcquiredAtUtc);
    }

    [Fact]
    public void Clear_RemovesTheBelief()
    {
        var registry = new LeaseRegistry();
        registry.RecordHeld(ProjectGuid, "lease-1", DateTimeOffset.UtcNow.AddSeconds(30));

        registry.Clear(ProjectGuid);

        Assert.Null(registry.Get(ProjectGuid));
    }

    [Fact]
    public void Clear_OnAnUnknownProject_IsANoOp()
    {
        var registry = new LeaseRegistry();

        registry.Clear(ProjectGuid); // must not throw

        Assert.Null(registry.Get(ProjectGuid));
    }

    [Fact]
    public void All_ReturnsASnapshotAcrossProjects()
    {
        var registry = new LeaseRegistry();
        registry.RecordHeld("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "lease-a", DateTimeOffset.UtcNow.AddSeconds(30));
        registry.RecordHeld("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "lease-b", DateTimeOffset.UtcNow.AddSeconds(30));

        Assert.Equal(2, registry.All().Count);
    }

    // ---------------------------------------------------------------- TTL self-expiry (defect: the
    // lease indicator lying after the plugin's own TTL watchdog released the real lock with no
    // reconnect, and so no ReconcileAsync, to notice)

    [Fact]
    public void Get_PastItsOwnRecordedExpiry_ReturnsNullWithNoExplicitClear()
    {
        var t0 = DateTimeOffset.UtcNow;
        var clock = t0;
        var registry = new LeaseRegistry(() => clock);
        registry.RecordHeld(ProjectGuid, "lease-1", t0.AddSeconds(5));

        clock = t0.AddSeconds(220); // the exact shape of the live repro: ttlSeconds=5, no end, wait past it
        Assert.Null(registry.Get(ProjectGuid));
    }

    [Fact]
    public void Get_OneTickBeforeItsOwnExpiry_StillReturnsTheLease()
    {
        var t0 = DateTimeOffset.UtcNow;
        var clock = t0;
        var registry = new LeaseRegistry(() => clock);
        var expiry = t0.AddSeconds(5);
        registry.RecordHeld(ProjectGuid, "lease-1", expiry);

        clock = expiry.AddTicks(-1);
        Assert.NotNull(registry.Get(ProjectGuid));
    }

    [Fact]
    public void Get_AtExactlyItsOwnExpiry_ReturnsNull()
    {
        // Same "now >= expiry" boundary as the plugin's own ReloadLease.IsExpired - see the class
        // doc comment for why the two must agree.
        var t0 = DateTimeOffset.UtcNow;
        var clock = t0;
        var registry = new LeaseRegistry(() => clock);
        var expiry = t0.AddSeconds(5);
        registry.RecordHeld(ProjectGuid, "lease-1", expiry);

        clock = expiry;
        Assert.Null(registry.Get(ProjectGuid));
    }

    [Fact]
    public void All_ExcludesASelfExpiredLease_ButKeepsAnUnexpiredOneForAnotherProject()
    {
        const string otherProject = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var t0 = DateTimeOffset.UtcNow;
        var clock = t0;
        var registry = new LeaseRegistry(() => clock);
        registry.RecordHeld(ProjectGuid, "lease-expired", t0.AddSeconds(5));
        registry.RecordHeld(otherProject, "lease-still-good", t0.AddSeconds(60));

        clock = t0.AddSeconds(10);

        var all = registry.All();
        var remaining = Assert.Single(all);
        Assert.Equal(otherProject, remaining.ProductGuid);
        Assert.Equal("lease-still-good", remaining.LeaseId);
    }

    [Fact]
    public void Get_AfterSelfExpiring_EvictsSoAFreshAcquireOfTheSameLeaseIdResetsAcquiredAtUtc()
    {
        // Guards the interaction with RecordHeld's own "renewing the same lease id preserves
        // AcquiredAtUtc" rule (see RecordHeld_RenewingTheSameLeaseId_... above): once a lease has
        // genuinely self-expired and been evicted, a LATER RecordHeld that happens to reuse the
        // same textual id is a brand new hold, not a continuation, and must stamp a fresh
        // AcquiredAtUtc rather than resurrecting the expired one's.
        var t0 = DateTimeOffset.UtcNow;
        var clock = t0;
        var registry = new LeaseRegistry(() => clock);
        registry.RecordHeld(ProjectGuid, "lease-1", t0.AddSeconds(5));

        clock = t0.AddSeconds(10);
        Assert.Null(registry.Get(ProjectGuid)); // self-expires and evicts

        clock = t0.AddSeconds(11);
        registry.RecordHeld(ProjectGuid, "lease-1", clock.AddSeconds(30));

        var found = registry.Get(ProjectGuid);
        Assert.NotNull(found);
        Assert.Equal(clock, found!.AcquiredAtUtc); // fresh, not t0 - the expired hold does not linger
    }

    // ---------------------------------------------------------------- reconnect reconciliation

    [Fact]
    public async Task ReconcileAsync_NothingBelievedHeld_CompletesImmediatelyWithoutARequest()
    {
        var (session, _, _) = await ConnectAsync();
        var registry = new LeaseRegistry();

        // Must return promptly without waiting on any network round trip - there is nothing to
        // confirm, and nobody answers on the "Unity" side of this connection in this test. If
        // this incorrectly sent a request anyway, it would hang here until the timeout fires.
        await registry.ReconcileAsync(ProjectGuid, session).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(registry.Get(ProjectGuid));
    }

    [Fact]
    public async Task ReconcileAsync_WhenTheLocalBeliefHasAlreadySelfExpired_SkipsTheNetworkRoundTrip()
    {
        // TTL self-expiry (Get's own guard - see the class doc comment) already knows this belief
        // is gone before ReconcileAsync's internal Get(productGuid) call even runs - so there is
        // nothing to confirm, same as the nothing-recorded-at-all case above, and this must not
        // hang waiting for a lease.renew nobody on the "Unity" side of this test will ever answer.
        var (session, _, _) = await ConnectAsync();
        var t0 = DateTimeOffset.UtcNow;
        var clock = t0;
        var registry = new LeaseRegistry(() => clock);
        registry.RecordHeld(ProjectGuid, "lease-1", t0.AddSeconds(5));
        clock = t0.AddSeconds(10);

        await registry.ReconcileAsync(ProjectGuid, session).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(registry.Get(ProjectGuid));
    }

    [Fact]
    public async Task ReconcileAsync_PluginConfirmsTheSameLease_UpdatesTheExpiryAndKeepsHeldSince()
    {
        var (session, unityReads, unityWrites) = await ConnectAsync();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-1);
        var registry = new LeaseRegistry(() => t0);
        registry.RecordHeld(ProjectGuid, "lease-1", t0.AddSeconds(30));

        var reconcile = registry.ReconcileAsync(ProjectGuid, session);

        var line = await unityReads.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(JsonRpcRequest.TryParse(line, out var request, out _));
        Assert.Equal("lease.renew", request!.Method);
        Assert.True(request.Params!.TryGetProperty("leaseId", out var idValue));
        Assert.Equal("lease-1", idValue!.AsString());

        // Rounded to millisecond precision up front - the wire (expiresAtUtcMs, see LeaseResult)
        // only carries millisecond precision, so that is the precision a round trip can preserve.
        var newExpiry = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds());
        await unityWrites.WriteLineAsync(MiniJson.Write(
            JsonRpcResponse.Success(request.Id!, LeaseResult(true, "lease-1", newExpiry)).ToJson()));

        await reconcile.WaitAsync(TimeSpan.FromSeconds(5));

        var found = registry.Get(ProjectGuid);
        Assert.NotNull(found);
        Assert.Equal("lease-1", found!.LeaseId);
        Assert.Equal(newExpiry, found.ExpiresAtUtc);
        Assert.Equal(t0, found.AcquiredAtUtc); // confirming a survived lease must not reset "held since"
    }

    [Fact]
    public async Task ReconcileAsync_PluginReportsNoneHeld_ClearsTheBelief()
    {
        // The exact scenario the plan calls out: the app believes a lease is held, but only the
        // plugin knows for sure whether it actually survived - here it reports nothing held (e.g.
        // the Unity process itself restarted between the app's last knowledge and this reconnect,
        // so SessionState-based boot reconciliation cleared even the native lock, and the fresh
        // ReloadGate instance never heard of "lease-1" at all).
        var (session, unityReads, unityWrites) = await ConnectAsync();
        var registry = new LeaseRegistry();
        registry.RecordHeld(ProjectGuid, "lease-1", DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.NotNull(registry.Get(ProjectGuid)); // sanity: the belief exists before reconciling

        var reconcile = registry.ReconcileAsync(ProjectGuid, session);

        var line = await unityReads.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(JsonRpcRequest.TryParse(line, out var request, out _));

        await unityWrites.WriteLineAsync(MiniJson.Write(
            JsonRpcResponse.Success(request!.Id!, LeaseResult(false, null, null)).ToJson()));

        await reconcile.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(registry.Get(ProjectGuid));
    }

    [Fact]
    public async Task ReconcileAsync_ADifferentLeaseIsReported_ClearsTheOldBeliefRatherThanAdoptingIt()
    {
        var (session, unityReads, unityWrites) = await ConnectAsync();
        var registry = new LeaseRegistry();
        registry.RecordHeld(ProjectGuid, "lease-1", DateTimeOffset.UtcNow.AddSeconds(30));

        var reconcile = registry.ReconcileAsync(ProjectGuid, session);
        var line = await unityReads.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(JsonRpcRequest.TryParse(line, out var request, out _));

        // success=false because a DIFFERENT lease holds the gate (see ReloadGate.Renew): the
        // renew of "lease-1" was rejected, and the response names the actual current holder.
        await unityWrites.WriteLineAsync(MiniJson.Write(
            JsonRpcResponse.Success(request!.Id!, LeaseResult(false, "someone-elses-lease", DateTimeOffset.UtcNow.AddSeconds(15))).ToJson()));

        await reconcile.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(registry.Get(ProjectGuid)); // lease-1 is definitely gone - clear, don't silently adopt a stranger's lease
    }

    public void Dispose()
    {
        _listener.Stop();
        foreach (var disposable in _toDispose) disposable.Dispose();
    }
}
