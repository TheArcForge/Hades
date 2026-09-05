using System.Net;
using System.Net.Sockets;
using System.Text;
using Hades.Contract.Wire;
using Hades.Core.Editors;

namespace Hades.Core.Tests.Editors;

/// <summary>
/// EditorSession in isolation - a real loopback socket pair, with the test driving the "Unity"
/// side directly (raw request/response lines), and no EditorListener/token/hello handshake
/// involved. See EditorListenerTests for the end-to-end handshake-through-registration path.
/// </summary>
public sealed class EditorSessionTests : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly List<IDisposable> _toDispose = [];

    public EditorSessionTests() => _listener.Start();

    static Hello MakeHello() => new()
    {
        ProjectGuid = "aaaabbbbccccddddeeeeffff00001111",
        ProjectPath = "/tmp/some-project",
        UnityVersion = "6000.3.2f1",
        PluginVersion = "0.1.0",
        ProcessId = 4242,
    };

    /// <summary>Connects a real loopback socket pair: the returned session wraps the server end
    /// (exactly as EditorListener would after a handshake), and the reader/writer wrap the client
    /// end for the test to act as the "Unity" peer. <paramref name="maxLineChars"/> is EditorSession's
    /// own bounded-line-read cap (see <see cref="EditorSession.DefaultMaxSessionLineChars"/>'s own
    /// doc comment) - overridden to a tiny value only by the test that proves the bound itself is
    /// enforced, so that test moves bytes, not megabytes.</summary>
    async Task<(EditorSession Session, StreamReader UnityReads, StreamWriter UnityWrites)> ConnectAsync(
        Hello? hello = null, int? maxLineChars = null)
    {
        var acceptTask = _listener.AcceptTcpClientAsync();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        var server = await acceptTask;

        _toDispose.Add(client);
        _toDispose.Add(server);

        var session = new EditorSession(server.GetStream(), hello ?? MakeHello(),
            maxLineChars ?? EditorSession.DefaultMaxSessionLineChars);
        _toDispose.Add(session);
        session.Start();

        var unityReads = new StreamReader(client.GetStream(), new UTF8Encoding(false));
        var unityWrites = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        return (session, unityReads, unityWrites);
    }

    [Fact]
    public async Task SendRequestAsync_ReturnsTheCorrelatedResponse()
    {
        var (session, unityReads, unityWrites) = await ConnectAsync();

        var sendTask = session.SendRequestAsync("ping");

        var requestLine = await unityReads.ReadLineAsync();
        Assert.True(JsonRpcRequest.TryParse(requestLine, out var request, out _));
        Assert.Equal("ping", request!.Method);

        await unityWrites.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request.Id!, JsonValue.String("pong")).ToJson()));

        var result = await sendTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.IsError);
        Assert.Equal("pong", result.Result!.AsString());
    }

    [Fact]
    public async Task SendRequestAsync_MintsADistinctIdPerCall()
    {
        var (session, unityReads, unityWrites) = await ConnectAsync();

        var t1 = session.SendRequestAsync("a");
        JsonRpcRequest.TryParse(await unityReads.ReadLineAsync(), out var r1, out _);

        var t2 = session.SendRequestAsync("b");
        JsonRpcRequest.TryParse(await unityReads.ReadLineAsync(), out var r2, out _);

        Assert.NotEqual(r1!.Id!.AsInteger(), r2!.Id!.AsInteger());

        await unityWrites.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(r1.Id!, JsonValue.Null).ToJson()));
        await unityWrites.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(r2.Id!, JsonValue.Null).ToJson()));
        await t1.WaitAsync(TimeSpan.FromSeconds(30));
        await t2.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TwoConcurrentRequests_EachGetItsOwnResponseRegardlessOfAnswerOrder()
    {
        var (session, unityReads, unityWrites) = await ConnectAsync();

        var t1 = session.SendRequestAsync("first");
        JsonRpcRequest.TryParse(await unityReads.ReadLineAsync(), out var r1, out _);
        var t2 = session.SendRequestAsync("second");
        JsonRpcRequest.TryParse(await unityReads.ReadLineAsync(), out var r2, out _);

        // Answer out of order: second request's response arrives first.
        await unityWrites.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(r2!.Id!, JsonValue.String("2")).ToJson()));
        await unityWrites.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(r1!.Id!, JsonValue.String("1")).ToJson()));

        Assert.Equal("1", (await t1.WaitAsync(TimeSpan.FromSeconds(30))).Result!.AsString());
        Assert.Equal("2", (await t2.WaitAsync(TimeSpan.FromSeconds(30))).Result!.AsString());
    }

    [Fact]
    public async Task SendKeepaliveAsync_RecordsTheAckTimeOnceAnswered()
    {
        var (session, unityReads, unityWrites) = await ConnectAsync();
        Assert.Null(session.LastKeepaliveAckUtc);

        var keepaliveTask = session.SendKeepaliveAsync();

        JsonRpcRequest.TryParse(await unityReads.ReadLineAsync(), out var request, out _);
        Assert.Equal("keepalive", request!.Method);
        await unityWrites.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request.Id!, JsonValue.Null).ToJson()));

        await keepaliveTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(session.LastKeepaliveAckUtc);
    }

    [Fact]
    public async Task Disconnected_FiresWhenThePeerClosesTheSocket()
    {
        var (session, _, unityWrites) = await ConnectAsync();
        var disconnected = new TaskCompletionSource();
        session.Disconnected += () => disconnected.TrySetResult();

        unityWrites.Close();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Disconnect_FailsAnyStillPendingRequestRatherThanHangingForever()
    {
        var (session, unityReads, unityWrites) = await ConnectAsync();

        var pending = session.SendRequestAsync("neverAnswered");
        await unityReads.ReadLineAsync(); // confirm it was actually sent before killing the link

        unityWrites.Close();

        await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task AMalformedLineFromThePeerIsSkippedWithoutKillingTheSession()
    {
        var (session, unityReads, unityWrites) = await ConnectAsync();

        await unityWrites.WriteLineAsync("this is not json {{{");

        var pending = session.SendRequestAsync("ping");
        JsonRpcRequest.TryParse(await unityReads.ReadLineAsync(), out var request, out _);
        await unityWrites.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request!.Id!, JsonValue.Null).ToJson()));

        // Still answers correctly after the garbage line - the read loop kept going.
        await pending.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ---------------------------------------------------------------- bounded session line read (B-F1)
    //
    // EditorListener.ReadRawLineAsync bounds the two raw handshake lines to MaxHandshakeLineBytes
    // (8KB) - but once a connection graduates to a session, this read loop used a plain
    // StreamReader.ReadLineAsync with no cap at all: a connected buggy/hostile/skewed peer sending
    // a huge payload with no '\n' could grow this reader's buffer without bound. These prove the
    // fix - a tiny configured cap (see ConnectAsync's own maxLineChars parameter), so the test
    // moves bytes, not megabytes; production always uses the real, much larger default.

    [Fact]
    public async Task ReceiveLoop_LineOverTheConfiguredCapWithNoNewline_FaultsTheSessionInsteadOfGrowingUnbounded()
    {
        const int tinyCap = 64;
        var (session, _, unityWrites) = await ConnectAsync(maxLineChars: tinyCap);
        var disconnected = new TaskCompletionSource();
        session.Disconnected += () => disconnected.TrySetResult();

        // Well over tinyCap, and deliberately never newline-terminated - the exact flood shape a
        // connected peer could send with no handshake gate left to stop it.
        await unityWrites.WriteAsync(new string('x', tinyCap * 4));
        await unityWrites.FlushAsync();

        // The bound must convert this into a clean disconnect (mirrors ReadRawLineAsync's own
        // "over-limit treated the same as no data" contract) rather than hanging forever
        // accumulating - proven the same way Disconnected_FiresWhenThePeerClosesTheSocket proves
        // an ordinary close.
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ReceiveLoop_LineUnderTheConfiguredCap_IsUnaffectedByIt()
    {
        // Sanity companion to the test above: an ordinary line well under a (still tiny, for
        // speed) cap must keep working exactly as before - the bound must not break normal-sized
        // messages.
        const int tinyCap = 4096;
        var (session, unityReads, unityWrites) = await ConnectAsync(maxLineChars: tinyCap);

        var sendTask = session.SendRequestAsync("ping");
        var requestLine = await unityReads.ReadLineAsync();
        Assert.True(JsonRpcRequest.TryParse(requestLine, out var request, out _));

        await unityWrites.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request!.Id!, JsonValue.String("pong")).ToJson()));

        var result = await sendTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.IsError);
        Assert.Equal("pong", result.Result!.AsString());
    }

    // ---------------------------------------------------------------- clamped lease expiry (B-F2)
    //
    // DateTimeOffset.FromUnixTimeMilliseconds throws ArgumentOutOfRangeException outside its
    // representable range - a peer answering lease.acquire/renew/release with a garbage
    // expiresAtUtcMs must not turn into an unhandled exception here (contained by SendLeaseRequestAsync's
    // caller today, but see EditorProjectTools' own script_editing_session test for the case where
    // an equivalent throw is NOT contained). Clamping keeps this call returning a normal
    // LeaseOutcome instead.

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public async Task AcquireLeaseAsync_ExpiresAtUtcMsOutOfRange_ClampsInsteadOfThrowing(long outOfRangeMs)
    {
        var (session, unityReads, unityWrites) = await ConnectAsync();

        var acquireTask = session.AcquireLeaseAsync("lease-1");
        JsonRpcRequest.TryParse(await unityReads.ReadLineAsync(), out var request, out _);

        var result = JsonValue.NewObject();
        result.SetProperty("success", JsonValue.Bool(true));
        result.SetProperty("leaseId", JsonValue.String("lease-1"));
        result.SetProperty("expiresAtUtcMs", JsonValue.Integer(outOfRangeMs));
        await unityWrites.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request!.Id!, result).ToJson()));

        var outcome = await acquireTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(outcome.Success);
        // Not literally DateTimeOffset.MaxValue/MinValue: FromUnixTimeMilliseconds(ToUnixTimeMilliseconds(...))
        // loses MaxValue's sub-millisecond ticks on the way through milliseconds, so the true
        // clamp boundary (what production code actually computes) is a hair below MaxValue -
        // still the representable extreme, just precise about what "extreme" means here.
        var boundary = outOfRangeMs > 0 ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue;
        var expectedClamp = DateTimeOffset.FromUnixTimeMilliseconds(boundary.ToUnixTimeMilliseconds());
        Assert.Equal(expectedClamp, outcome.ExpiresAtUtc);
    }

    [Fact]
    public async Task SendRequestAsync_ObservesCancellation_TheSeamALaterTimeoutPlanUses()
    {
        // Not a timeout implementation - that is explicitly a later plan's job - but this proves
        // the seam it will hook into: SendRequestAsync already accepts a CancellationToken and
        // reacts to it exactly the way a Task.Delay-based timeout would, without any reshaping.
        var (session, unityReads, _) = await ConnectAsync();
        using var cts = new CancellationTokenSource();

        var pending = session.SendRequestAsync("neverAnswered", cancellationToken: cts.Token);
        await unityReads.ReadLineAsync();

        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => pending);
    }

    [Fact]
    public void Hello_IsExposedAsReceivedAtConstruction()
    {
        var hello = MakeHello();
        using var session = new EditorSession(new MemoryStream(), hello);

        Assert.Same(hello, session.Hello);
    }

    public void Dispose()
    {
        _listener.Stop();
        foreach (var disposable in _toDispose) disposable.Dispose();
    }
}
