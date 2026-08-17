// C# 9 only in this file - see the file banner in Contract/MiniJson.cs. Block-scoped namespace
// and ordinary mutable fields are deliberate, not an oversight.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Hades.Contract.Wire;
using Hades.Runtime;

namespace Hades.Transport
{
    /// <summary>
    /// The plugin's half of the Unity Editor link: dials out to the app's loopback listener,
    /// performs the token + hello handshake, then answers JSON-RPC requests one per line until
    /// the connection drops - at which point it reconnects. See
    /// <c>Hades.Core.Editors.EditorListener</c> (app side) for the exact wire handshake this
    /// mirrors, and the editor-link plan's architecture section for why Unity dials out instead
    /// of listening.
    ///
    /// Runs entirely on one dedicated background thread, started by <see cref="Start"/> and
    /// joined by <see cref="Dispose"/>. "keepalive" is answered right there on that thread,
    /// never touching <see cref="MainThreadPump"/> - which is what lets a keepalive return
    /// instantly even when the main thread is busy or the pump is backed up. Every other request
    /// is dispatched through the pump, since it may need a Unity API. See the pump's own class
    /// doc comment for the deadline and per-tick-budget behaviour that governs that path.
    ///
    /// Reconnect is the normal path (a domain reload drops the socket on every script save), so
    /// every expected failure - connect refused, socket reset, malformed line - is handled
    /// silently: this class never logs anything itself. That is deliberate, not missing
    /// diagnostics; see the class's test suite for how "silent" is verified.
    ///
    /// A connection ending after it completed its handshake also invokes the optional
    /// <c>onDisconnected</c> callback, right here on this I/O thread - see the constructor's doc
    /// comment. This is the release-paths plan's socket-disconnect net: the callback is what lets
    /// <c>ReloadGate</c> release Unity's reload lock when the app that was holding it vanishes,
    /// even though a routine reconnect-on-drop (the common case, once per script save) invokes the
    /// exact same callback and simply finds nothing held to release.
    /// </summary>
    public sealed class HadesClient : IDisposable
    {
        static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

        // Default bound on one request line read by ServeRequests, once a connection is past the
        // handshake - see Hades.Core.Editors.EditorSession.DefaultMaxSessionLineChars (App~ side)
        // for the identical reasoning applied symmetrically to this direction of the same wire: a
        // connected buggy/hostile/skewed app peer sending a huge payload with no trailing '\n'
        // must not grow this reader's buffer without bound. Overridable via the constructor purely
        // so tests can prove the bound is enforced without moving megabytes of data; production
        // always gets this default.
        const int DefaultMaxRequestLineChars = 16 * 1024 * 1024;

        readonly Func<EditorConnectionInfo> _resolveConnection;
        readonly Hello _hello;
        readonly MainThreadPump _pump;
        readonly Func<JsonRpcRequest, JsonValue> _handleRequest;
        readonly TimeSpan _minBackoff;
        readonly TimeSpan _maxBackoff;
        readonly TimeSpan _requestTimeout;
        readonly Action _onDisconnected;
        readonly int _maxRequestLineChars;

        readonly ManualResetEventSlim _shutdown = new ManualResetEventSlim(false);
        readonly object _clientGate = new object();

        TcpClient _currentClient;
        Thread _ioThread;
        volatile bool _disposed;

        /// <param name="resolveConnection">Returns the port to dial and the token to present,
        /// or null when neither is known yet (the app has not started, or its connection file is
        /// unreadable). Called fresh before every connection attempt - not just once - because a
        /// restarted app hands out a new port and token on every listener <c>Start()</c>.</param>
        /// <param name="hello">Sent once per connection, immediately after the token line. Pid,
        /// project identity, and versions are fixed for this Editor process's lifetime, so a
        /// plain value (not a factory) is enough.</param>
        /// <param name="pump">Where every request except "keepalive" is dispatched to run on the
        /// main thread. Owned and started/disposed by the caller - this class only ever calls
        /// <see cref="MainThreadPump.EnqueueAsync{T}"/> on it, never its lifecycle methods, so a
        /// test can control exactly when (or whether) queued work ever drains.</param>
        /// <param name="handleRequest">Computes the result for any request other than
        /// "keepalive", called on the main thread via <paramref name="pump"/>. Exceptions become
        /// a JSON-RPC error response; no real dispatch table exists yet (that is a later plan;
        /// see the editor-link plan's scope), so this is the seam it plugs into.</param>
        /// <param name="minBackoff">Reconnect delay after the first failed connection attempt in
        /// a row. Defaults to 500ms.</param>
        /// <param name="maxBackoff">Ceiling the exponential reconnect delay never exceeds.
        /// Defaults to 30s.</param>
        /// <param name="requestTimeout">How long a non-keepalive request may sit queued before it
        /// is skipped instead of applied late. Defaults to 30s.</param>
        /// <param name="onDisconnected">Invoked on this I/O thread whenever a connection that
        /// completed its handshake ends, for any reason - the socket reset, the app closed it, or
        /// <see cref="Dispose"/> tore this client down. Used by the release-paths plan so
        /// whatever held Unity's reload lock gets released when the connection that owned it
        /// drops, even though nobody is left to send an explicit release - see
        /// <c>ReloadGate.ReleaseOnDisconnect</c>. Optional; null (the default) means nobody is
        /// listening. A connection that never completed its handshake (e.g. connection refused)
        /// does not count as a disconnect - there was never a live connection to have been holding
        /// anything.</param>
        /// <param name="maxRequestLineChars">Bound on one request line read from the app - see
        /// <see cref="DefaultMaxRequestLineChars"/>'s own doc comment. Defaults to it; overridden
        /// only by tests.</param>
        public HadesClient(Func<EditorConnectionInfo> resolveConnection, Hello hello, MainThreadPump pump,
            Func<JsonRpcRequest, JsonValue> handleRequest,
            TimeSpan? minBackoff = null, TimeSpan? maxBackoff = null, TimeSpan? requestTimeout = null,
            Action onDisconnected = null, int? maxRequestLineChars = null)
        {
            _resolveConnection = resolveConnection ?? throw new ArgumentNullException(nameof(resolveConnection));
            _hello = hello ?? throw new ArgumentNullException(nameof(hello));
            _pump = pump ?? throw new ArgumentNullException(nameof(pump));
            _handleRequest = handleRequest ?? throw new ArgumentNullException(nameof(handleRequest));
            _minBackoff = minBackoff ?? TimeSpan.FromMilliseconds(500);
            _maxBackoff = maxBackoff ?? TimeSpan.FromSeconds(30);
            _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
            _onDisconnected = onDisconnected;
            _maxRequestLineChars = maxRequestLineChars ?? DefaultMaxRequestLineChars;
        }

        /// <summary>True while the background I/O thread is alive. Used by tests to prove
        /// <see cref="Dispose"/> leaves no thread behind; not meant as a "connected" flag (the
        /// thread is alive while backing off between attempts too).</summary>
        public bool IsIoThreadRunning => _ioThread != null && _ioThread.IsAlive;

        /// <summary>Starts the background thread that dials out, handshakes, serves requests,
        /// and reconnects on drop. Call once.</summary>
        public void Start()
        {
            // Not ObjectDisposedException.ThrowIf: that static helper is .NET 7+, unavailable
            // under Unity's netstandard2.1/Mono surface.
            if (_disposed) throw new ObjectDisposedException(nameof(HadesClient));
            if (_ioThread != null) throw new InvalidOperationException("HadesClient is already started.");

            _ioThread = new Thread(IoThreadMain) { IsBackground = true, Name = "Hades-IO" };
            _ioThread.Start();
        }

        void IoThreadMain()
        {
            var attempt = 0;
            var random = new Random();

            while (!_disposed)
            {
                var info = SafeResolveConnection();
                if (info == null)
                {
                    // Nothing to dial yet - the app has not started, or has not written its
                    // connection file yet. Not a failed attempt, just "not ready": wait one
                    // minimum tick and check again, without escalating the backoff.
                    if (WaitForShutdown(_minBackoff)) return;
                    continue;
                }

                bool handshakeCompleted;
                try
                {
                    handshakeCompleted = RunOneConnection(info);
                }
                catch
                {
                    // A connection that fails for any reason not already handled inside
                    // RunOneConnection (e.g. an unanticipated exception) must not take the I/O
                    // thread down with it - reconnect is the normal path, so the outer loop
                    // simply tries again after backing off, same as an ordinary connect failure.
                    handshakeCompleted = false;
                }

                if (_disposed) return;

                if (handshakeCompleted)
                {
                    // A connection that got at least as far as a completed handshake proves the
                    // app is up and reachable right now - retry immediately (a domain reload's
                    // drop-and-reconnect should feel instantaneous) and reset the backoff so a
                    // LATER run of real failures starts counting from zero again.
                    attempt = 0;
                    continue;
                }

                var delay = ComputeBackoff(attempt, _minBackoff, _maxBackoff, random);
                attempt++;
                if (WaitForShutdown(delay)) return;
            }
        }

        EditorConnectionInfo SafeResolveConnection()
        {
            try
            {
                return _resolveConnection();
            }
            catch
            {
                // The resolver reads a file written by another process - transient I/O errors
                // (mid-write, permissions, not-yet-created) are exactly "not ready yet", not a
                // fault worth surfacing.
                return null;
            }
        }

        bool WaitForShutdown(TimeSpan delay) => _shutdown.Wait(delay);

        /// <summary>Connects, handshakes, then serves requests until the connection ends. Returns
        /// true once the handshake (token + hello) was sent successfully, regardless of how the
        /// connection later ended - the caller uses this to decide whether to reset the reconnect
        /// backoff. Never throws: every failure path below is either expected (and swallowed
        /// locally) or would have already been swallowed by the caller's own try/catch.</summary>
        bool RunOneConnection(EditorConnectionInfo info)
        {
            var client = new TcpClient();
            try
            {
                client.Connect(IPAddress.Loopback, info.Port);
            }
            catch
            {
                client.Dispose();
                return false;
            }

            lock (_clientGate)
            {
                if (_disposed)
                {
                    client.Dispose();
                    return false;
                }
                _currentClient = client;
            }

            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                var reader = new StreamReader(stream, new UTF8Encoding(false));

                try
                {
                    writer.WriteLine(info.Token);
                    writer.WriteLine(MiniJson.Write(_hello.ToJson()));
                }
                catch
                {
                    return false;
                }

                ServeRequests(reader, writer);
                _onDisconnected?.Invoke();
                return true;
            }
            finally
            {
                lock (_clientGate)
                {
                    if (ReferenceEquals(_currentClient, client)) _currentClient = null;
                }
                client.Dispose();
            }
        }

        /// <summary>
        /// One JSON-RPC request per line in. "keepalive" is answered synchronously, right here,
        /// before this loop even looks at the next line - the fast path that stays fast
        /// regardless of the main thread or the pump. Everything else is dispatched to
        /// <see cref="_pump"/> and this loop moves straight on to the next line WITHOUT waiting
        /// for it: blocking here would starve keepalive behind whatever else is in flight, which
        /// is exactly the coupling this class exists to avoid. Each dispatched request's response
        /// is written whenever its pump work completes (skipped items - past their deadline -
        /// write nothing at all). Concurrent writers are serialized with a local lock, since a
        /// keepalive response and a pump completion can land at the same time.
        ///
        /// A line that fails to parse is skipped, not fatal - a malformed or hostile line must
        /// not kill an otherwise-healthy connection (mirrors MiniJson's own never-throw contract
        /// at the wire boundary).
        /// </summary>
        void ServeRequests(StreamReader reader, StreamWriter writer)
        {
            var writeGate = new object();

            void WriteResponse(JsonRpcResponse response)
            {
                try
                {
                    var text = MiniJson.Write(response.ToJson());
                    lock (writeGate) { writer.WriteLine(text); }
                }
                catch
                {
                    // The socket is gone - ServeRequests' own read loop will notice on its next
                    // ReadLine() and return; nothing further to do from a completion callback.
                }
            }

            while (!_disposed)
            {
                string line;
                try
                {
                    line = ReadBoundedLine(reader);
                }
                catch
                {
                    return; // socket died - normal end of this connection, not a fault.
                }

                if (line == null) return; // EOF, or a line over _maxRequestLineChars - see ReadBoundedLine.
                if (line.Length == 0) continue;

                if (!JsonRpcRequest.TryParse(line, out var request, out _) || request?.Method == null)
                {
                    continue; // malformed line - skip it, keep serving this connection.
                }

                var id = request.Id ?? JsonValue.Null;

                if (request.Method == "keepalive")
                {
                    WriteResponse(JsonRpcResponse.Success(id, JsonValue.Bool(true)));
                    continue;
                }

                var deadlineUtc = DateTime.UtcNow + _requestTimeout;
                _pump.EnqueueAsync(() => _handleRequest(request), deadlineUtc).ContinueWith(task =>
                {
                    if (task.IsCanceled) return; // past its deadline - skipped, no late response.
                    var response = task.IsFaulted
                        ? JsonRpcResponse.Failure(id, -32603, DescribeFailure(task.Exception))
                        : JsonRpcResponse.Success(id, task.Result);
                    WriteResponse(response);
                });
            }
        }

        /// <summary>
        /// Reads one line, same observable contract as <see cref="StreamReader.ReadLine()"/> (a
        /// trailing partial line with no terminating '\n' is returned as-is at end of stream, same
        /// as that method) EXCEPT bounded: once accumulating a line would exceed
        /// <see cref="_maxRequestLineChars"/> with no '\n' yet found, this returns null - exactly
        /// what "end of stream with nothing read" already means to <see cref="ServeRequests"/>'s
        /// own null check, so an over-limit line ends this connection (the I/O thread simply
        /// reconnects - see <see cref="RunOneConnection"/>) the same way a dropped socket already
        /// does. Mirrors <c>Hades.Core.Editors.EditorListener.ReadRawLineAsync</c>'s identical
        /// "over-limit treated the same as no data at all" contract on the app side of this same
        /// wire - see <see cref="DefaultMaxRequestLineChars"/>'s own doc comment for why the bound
        /// itself is so much larger than the app's own (8KB) handshake-line bound.
        /// </summary>
        string ReadBoundedLine(StreamReader reader)
        {
            var sb = new StringBuilder();

            while (sb.Length <= _maxRequestLineChars)
            {
                var next = reader.Read();
                if (next == -1) return sb.Length > 0 ? sb.ToString() : null;
                if (next == '\n') return sb.ToString();
                sb.Append((char)next);
            }

            // Over the bound with no terminating '\n' in sight - signal exactly like end of
            // stream (see this method's own doc comment) so the caller's existing null check ends
            // this connection.
            return null;
        }

        static string DescribeFailure(AggregateException exception)
        {
            var inner = exception?.InnerException;
            return inner != null ? inner.Message : "Unknown error.";
        }

        /// <summary>Full-jitter-ish exponential backoff: <c>min(max, base * 2^attempt)</c>, then a
        /// random point in the top half of that range (equal jitter) - spreads reconnect attempts
        /// out without ever waiting less than half the computed delay.</summary>
        static TimeSpan ComputeBackoff(int attempt, TimeSpan min, TimeSpan max, Random random)
        {
            var exponential = min.TotalMilliseconds * Math.Pow(2, attempt);
            var capped = Math.Min(exponential, max.TotalMilliseconds);
            var jittered = (capped / 2) + (random.NextDouble() * (capped / 2));
            return TimeSpan.FromMilliseconds(Math.Max(jittered, min.TotalMilliseconds));
        }

        /// <summary>Signals shutdown, closes whatever connection is live right now (to unblock a
        /// thread parked in a blocking read), and joins the I/O thread - so by the time this
        /// returns, no thread survives. Safe to call more than once. Does not touch
        /// <see cref="_pump"/>'s lifecycle - the caller owns that.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Set();

            lock (_clientGate)
            {
                try { _currentClient?.Close(); }
                catch { /* already closing/closed - nothing to do */ }
            }

            _ioThread?.Join(TimeSpan.FromSeconds(5));

            // Deliberately NOT disposing _shutdown: if Join() above ever timed out (it should
            // not, in practice - the socket close and the Set() together unblock the thread
            // promptly), a still-running IoThreadMain could still be inside _shutdown.Wait(),
            // and disposing out from under it would throw ObjectDisposedException on that
            // thread. A ManualResetEventSlim that never had its WaitHandle materialized (this
            // one never does) holds no real OS handle, so leaving it for the GC costs nothing.
        }
    }
}
