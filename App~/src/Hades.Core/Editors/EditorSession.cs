using System.Text;
using Hades.Contract.Wire;

namespace Hades.Core.Editors;

/// <summary>One lease.acquire / lease.renew / lease.release outcome, parsed from the plugin's own
/// answer - see <see cref="EditorSession.AcquireLeaseAsync"/> and its siblings. <see cref="Success"/>
/// is that specific call's own gate-level result (ReloadGate.Acquire/Renew/Release's return
/// value); <see cref="LeaseId"/> and <see cref="ExpiresAtUtc"/> are always the CURRENT holder's
/// truth as the plugin sees it after the call, not an echo of what was requested - null/null when
/// nothing is held. That means a rejected acquire/release (a DIFFERENT lease holds the gate)
/// still reports a real <see cref="LeaseId"/>/<see cref="ExpiresAtUtc"/>: the other lease's, not
/// the caller's.</summary>
public sealed record LeaseOutcome
{
    public required bool Success { get; init; }
    public string? LeaseId { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}

/// <summary>
/// One connected Unity Editor's live socket: request/response correlation and keepalive tracking
/// for the connection <see cref="Hello"/> identified. The app is always the JSON-RPC client and
/// Unity is always the server (see the plan's transport section), so this class only ever
/// originates requests - it never answers one itself.
///
/// This is the one seam a later plan needs for per-request timeouts:
/// <see cref="SendRequestAsync"/> already returns a plain awaitable backed by a
/// <see cref="TaskCompletionSource{TResult}"/> rather than blocking synchronously, and already
/// accepts a <see cref="CancellationToken"/> - racing it against a timer (a
/// <see cref="CancellationTokenSource"/> with a timeout, or <c>Task.WhenAny</c> against
/// <c>Task.Delay</c>) drops straight in without touching this class.
///
/// Wire framing for everything this class reads or writes: one JSON-RPC message per line, over
/// the stream handed to the constructor. That stream must already be past the token + hello
/// handshake - see <see cref="EditorListener"/>, which owns that part and constructs this class
/// only once it succeeds.
/// </summary>
public sealed class EditorSession : IDisposable
{
    readonly Stream _stream;
    readonly StreamWriter _writer;
    readonly StreamReader _reader;
    readonly Dictionary<long, TaskCompletionSource<JsonRpcResponse>> _pending = [];
    readonly Lock _gate = new();

    long _nextId;
    CancellationTokenSource? _cts;
    Task? _receiveLoop;
    bool _disposed;

    public EditorSession(Stream stream, Hello hello)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(hello);

        _stream = stream;
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" };
        _reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Hello = hello;
    }

    /// <summary>The handshake payload this connection sent, exactly once, at connect time.</summary>
    public Hello Hello { get; }

    public DateTimeOffset ConnectedAtUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>UTC time the most recent keepalive round trip was acknowledged, or null before
    /// the first one completes. See <see cref="SendKeepaliveAsync"/>.</summary>
    public DateTimeOffset? LastKeepaliveAckUtc { get; private set; }

    /// <summary>
    /// Raised exactly once, when the connection ends for any reason - the peer closing the
    /// socket, a read/write failure, or <see cref="Dispose"/>. <see cref="EditorListener"/>
    /// subscribes to deregister this session from <see cref="EditorRegistry"/>; callers must
    /// subscribe before calling <see cref="Start"/>, since the read loop can observe EOF and
    /// raise this as soon as it starts running.
    /// </summary>
    public event Action? Disconnected;

    /// <summary>Starts the background read loop that receives responses. Plugin-initiated
    /// notifications (asset changes, console output, ...) are not handled yet - they land with
    /// the plan that needs them (see the editor-link plan's "Out, deliberately" section) - so a
    /// line that isn't a response to a pending request is simply ignored, not an error.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_receiveLoop is not null) throw new InvalidOperationException("EditorSession is already started.");

        _cts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Sends a JSON-RPC request and returns the correlated response once it arrives. The request
    /// id is minted here - an incrementing integer, unique per session - so callers never supply
    /// one. Cancelling <paramref name="cancellationToken"/> before a response arrives cancels the
    /// returned task and stops tracking the request; see the class doc comment for why this
    /// parameter exists even though nothing here enforces a timeout yet.
    /// </summary>
    public Task<JsonRpcResponse> SendRequestAsync(string method, JsonValue? @params = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (_receiveLoop is null) throw new InvalidOperationException("EditorSession.Start() must be called before sending requests.");

        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonRpcRequest { Id = JsonValue.Integer(id), Method = method, Params = @params };
        var tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            _pending[id] = tcs;
        }

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                lock (_gate) { _pending.Remove(id); }
                tcs.TrySetCanceled(cancellationToken);
            });
        }

        try
        {
            WriteLine(MiniJson.Write(request.ToJson()));
        }
        catch (Exception e)
        {
            lock (_gate) { _pending.Remove(id); }
            tcs.TrySetException(e);
        }

        return tcs.Task;
    }

    /// <summary>
    /// Sends a keepalive request and awaits the Editor's answer, recording
    /// <see cref="LastKeepaliveAckUtc"/> once it arrives - a thin wrapper over
    /// <see cref="SendRequestAsync"/>, sharing the exact same correlation and (future) timeout
    /// machinery rather than special-casing keepalive traffic.
    /// </summary>
    public async Task SendKeepaliveAsync(CancellationToken cancellationToken = default)
    {
        await SendRequestAsync("keepalive", null, cancellationToken).ConfigureAwait(false);
        LastKeepaliveAckUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Requests the Editor's reload gate under <paramref name="leaseId"/> - see
    /// HadesBoot's lease.acquire handler and ReloadGate.Acquire, which this thinly wraps over the
    /// wire. <paramref name="ttl"/> is a request, not a guarantee: read <see cref="LeaseOutcome.ExpiresAtUtc"/>
    /// for what the plugin actually applied, never assume it matches what was asked for.</summary>
    public Task<LeaseOutcome> AcquireLeaseAsync(string leaseId, TimeSpan? ttl = null, CancellationToken cancellationToken = default) =>
        SendLeaseRequestAsync("lease.acquire", leaseId, ttl, cancellationToken);

    /// <summary>Extends the held lease's TTL from now, IF <paramref name="leaseId"/> is still the
    /// current holder - see ReloadGate.Renew. Also used as <see cref="LeaseRegistry.ReconcileAsync"/>'s
    /// reconnect probe: a false <see cref="LeaseOutcome.Success"/> here is exactly "the plugin
    /// does not currently hold this lease".</summary>
    public Task<LeaseOutcome> RenewLeaseAsync(string leaseId, CancellationToken cancellationToken = default) =>
        SendLeaseRequestAsync("lease.renew", leaseId, null, cancellationToken);

    /// <summary>Releases <paramref name="leaseId"/> - see ReloadGate.Release. Succeeds
    /// (<see cref="LeaseOutcome.Success"/> true) even when nothing was held or a different lease
    /// already released it: releasing an unknown or already-released id is idempotent by design,
    /// not an error.</summary>
    public Task<LeaseOutcome> ReleaseLeaseAsync(string leaseId, CancellationToken cancellationToken = default) =>
        SendLeaseRequestAsync("lease.release", leaseId, null, cancellationToken);

    async Task<LeaseOutcome> SendLeaseRequestAsync(string method, string leaseId, TimeSpan? ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);

        var @params = JsonValue.NewObject().SetProperty("leaseId", JsonValue.String(leaseId));
        if (ttl is { } t) @params.SetProperty("ttlSeconds", JsonValue.Float(t.TotalSeconds));

        var response = await SendRequestAsync(method, @params, cancellationToken).ConfigureAwait(false);

        if (response.IsError)
            throw new InvalidOperationException($"{method} failed: {response.Error!.Message}");

        var result = response.Result;
        if (result is null || result.Kind != JsonValueKind.Object)
            throw new InvalidOperationException($"{method} returned an unexpected response shape.");

        var success = result.TryGetProperty("success", out var successValue) && successValue!.Kind == JsonValueKind.Boolean && successValue.AsBoolean();
        var resultLeaseId = result.TryGetProperty("leaseId", out var idValue) && idValue!.Kind == JsonValueKind.String ? idValue.AsString() : null;
        var expiresAtUtc = result.TryGetProperty("expiresAtUtcMs", out var expiresValue) && expiresValue!.Kind == JsonValueKind.Integer
            ? DateTimeOffset.FromUnixTimeMilliseconds(expiresValue.AsInteger())
            : (DateTimeOffset?)null;

        return new LeaseOutcome { Success = success, LeaseId = resultLeaseId, ExpiresAtUtc = expiresAtUtc };
    }

    async Task ReceiveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await _reader.ReadLineAsync(token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    break;
                }

                if (line is null) break; // EOF: the peer closed the connection.

                // A line that doesn't parse as a response - or whose id matches nothing pending -
                // is not this loop's problem: a malformed line from a hostile or buggy peer must
                // not kill the connection (mirrors MiniJson's own never-throw contract), and a
                // plugin-initiated notification is simply not handled yet - see Start()'s doc
                // comment.
                if (JsonRpcResponse.TryParse(line, out var response, out _) && response is not null)
                {
                    CompletePending(response);
                }
            }
        }
        finally
        {
            FailAllPending();
            Disconnected?.Invoke();
        }
    }

    void CompletePending(JsonRpcResponse response)
    {
        if (response.Id is not { Kind: JsonValueKind.Integer } id) return;

        TaskCompletionSource<JsonRpcResponse>? tcs;
        lock (_gate)
        {
            _pending.Remove(id.AsInteger(), out tcs);
        }

        tcs?.TrySetResult(response);
    }

    void FailAllPending()
    {
        List<TaskCompletionSource<JsonRpcResponse>> pending;
        lock (_gate)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var tcs in pending)
        {
            tcs.TrySetException(new IOException("The editor connection closed before a response arrived."));
        }
    }

    void WriteLine(string text)
    {
        // Serializes writes: two concurrent SendRequestAsync calls must not interleave partial
        // lines onto the wire. Low contention, tiny payloads (tool calls, not bulk data) - a
        // plain lock is not a bottleneck here.
        lock (_gate)
        {
            _writer.WriteLine(text);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _cts?.Cancel();
        _stream.Dispose();
        // The receive loop's own finally block fails pending requests and raises Disconnected
        // once ReadLineAsync unblocks (with an exception, caught there) as a result of the
        // stream closing above - Dispose does not duplicate that here.
        _cts?.Dispose();
    }
}
