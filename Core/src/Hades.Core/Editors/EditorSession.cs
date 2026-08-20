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
    /// <summary>
    /// Default bound on one line read by <see cref="ReceiveLoopAsync"/>, once the connection has
    /// graduated past the handshake and this class owns the read loop - see
    /// <see cref="ReadBoundedLineAsync"/>'s own doc comment for the read mechanism this bounds.
    /// Mirrors <see cref="EditorListener"/>'s own <c>MaxHandshakeLineBytes</c> (8KB) for the two
    /// raw handshake lines, at a far more generous size: a session message can legitimately carry
    /// a serialized scene/component result (e.g. project_get_console_log's up to 200 buffered
    /// entries - see UnityPlugin's ConsoleLogBuffer.Capacity - each with a message and stack trace,
    /// or a batched scene_apply/prefab_apply request), which the tiny handshake bound was never
    /// sized for. No hard limit exists anywhere else on this wire today (checked: no
    /// max-message-size concept in the MCP/editor layer), so 16 MiB is a deliberately chosen,
    /// generous multiple of the largest plausible legitimate payload - comfortably above it, while
    /// still finite: a connected buggy/hostile/skewed peer sending a huge payload with no '\n' now
    /// hits this bound and faults the session instead of growing this reader's buffer without
    /// limit. Overridable per-instance via the constructor below purely so tests can prove the
    /// bound is enforced without moving megabytes of data; production code always gets this
    /// default.
    /// </summary>
    public const int DefaultMaxSessionLineChars = 16 * 1024 * 1024;

    readonly Stream _stream;
    readonly StreamWriter _writer;
    readonly StreamReader _reader;
    readonly int _maxLineChars;
    readonly Dictionary<long, TaskCompletionSource<JsonRpcResponse>> _pending = [];
    readonly Lock _gate = new();

    long _nextId;
    CancellationTokenSource? _cts;
    Task? _receiveLoop;
    bool _disposed;

    public EditorSession(Stream stream, Hello hello, int maxLineChars = DefaultMaxSessionLineChars)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(hello);

        _stream = stream;
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true, NewLine = "\n" };
        _reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _maxLineChars = maxLineChars;
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
            ? ClampToUnixTimeMilliseconds(expiresValue.AsInteger())
            : (DateTimeOffset?)null;

        return new LeaseOutcome { Success = success, LeaseId = resultLeaseId, ExpiresAtUtc = expiresAtUtc };
    }

    static readonly long MinUnixTimeMilliseconds = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    static readonly long MaxUnixTimeMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>Converts an untrusted Unix-milliseconds value - the plugin's own reported
    /// expiresAtUtcMs - into a <see cref="DateTimeOffset"/> by CLAMPING into the range
    /// <see cref="DateTimeOffset.FromUnixTimeMilliseconds"/> can represent, rather than calling it
    /// directly, which throws <see cref="ArgumentOutOfRangeException"/> outside that range. A
    /// garbage or out-of-range value from a peer becomes "as far future/past as DateTimeOffset can
    /// represent" instead of an unhandled exception out of this JSON-RPC response parse - see
    /// EditorProjectTools.ScriptEditingSession's identical helper (Hades.Server.Mcp) for the
    /// caller where an equivalent throw is NOT already contained.</summary>
    static DateTimeOffset ClampToUnixTimeMilliseconds(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(milliseconds, MinUnixTimeMilliseconds, MaxUnixTimeMilliseconds));

    async Task ReceiveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await ReadBoundedLineAsync(token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    break;
                }

                if (line is null) break; // EOF, or a line over _maxLineChars - see ReadBoundedLineAsync.

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

    /// <summary>
    /// Reads one line, same observable contract as <see cref="StreamReader.ReadLineAsync(CancellationToken)"/>
    /// (a trailing partial line with no terminating '\n' is returned as-is at end of stream, same
    /// as that method) EXCEPT bounded: once accumulating a line would exceed
    /// <see cref="_maxLineChars"/> with no '\n' yet found, this returns null - exactly what "end of
    /// stream with nothing read" already means to <see cref="ReceiveLoopAsync"/>'s own null check,
    /// so an over-limit line ends the receive loop (and, via its own <c>finally</c> block,
    /// disconnects this session) the same way a dropped connection already does. Mirrors
    /// <see cref="EditorListener.ReadRawLineAsync"/>'s identical "over-limit treated the same as no
    /// data at all" contract for the two raw handshake lines - see
    /// <see cref="DefaultMaxSessionLineChars"/>'s own doc comment for why the bound itself is so
    /// much larger here.
    ///
    /// Reads one character at a time off the SAME buffering <see cref="_reader"/> every other read
    /// on this connection already uses, rather than a second, competing buffer over the raw
    /// <see cref="_stream"/> - see ReadRawLineAsync's own doc comment for why a second reader over
    /// the same stream is the thing to avoid. The per-character call overhead this trades away only
    /// matters for a message that approaches the bound; an ordinary few-KB response notices nothing.
    /// </summary>
    async Task<string?> ReadBoundedLineAsync(CancellationToken token)
    {
        var sb = new StringBuilder();
        var single = new char[1];

        while (sb.Length <= _maxLineChars)
        {
            var read = await _reader.ReadAsync(single.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0) return sb.Length > 0 ? sb.ToString() : null;
            if (single[0] == '\n') return sb.ToString();
            sb.Append(single[0]);
        }

        // Over the bound with no terminating '\n' in sight - signal exactly like EOF (see this
        // method's own doc comment) so the caller's existing null check faults the session.
        return null;
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
