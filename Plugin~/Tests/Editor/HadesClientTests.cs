// C# 9 only in this file - Unity 6000.3's compiler caps there. Block-scoped namespace and
// ordinary mutable fields are deliberate, matching Contract/'s dialect (see MiniJson.cs's file
// banner) even though this file never leaves the plugin.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Transport;
using NUnit.Framework;
using UnityEngine;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// Exercises <see cref="HadesClient"/> against an in-process fake app: a raw
    /// <see cref="TcpListener"/> playing the app's side of the handshake and JSON-RPC framing
    /// (see <see cref="Hades.Core.Editors.EditorListener"/> on the app side for the protocol this
    /// mirrors), so no real app process is needed to prove dial-out, framing, and reconnect.
    /// </summary>
    [TestFixture]
    public sealed class HadesClientTests
    {
        static Hello MakeHello(string projectGuid = "aaaabbbbccccddddeeeeffff00001111", string unityVersion = "6000.3.2f1",
            string projectPath = "/tmp/project", string pluginVersion = "1.2.0", long processId = 1234)
        {
            return new Hello
            {
                ProjectGuid = projectGuid,
                ProjectPath = projectPath,
                UnityVersion = unityVersion,
                PluginVersion = pluginVersion,
                ProcessId = processId,
            };
        }

        /// <summary>A pump this test suite owns but (deliberately, in most tests) never starts
        /// or ticks - Task 3's behaviours (hello, framing, reconnect, silence, malformed lines,
        /// shutdown) do not depend on the pump draining anything, since none of these tests send
        /// a non-keepalive request.</summary>
        static MainThreadPump MakeIdlePump() => new MainThreadPump();

        static HadesClient MakeClient(FakeApp app, Hello hello, MainThreadPump pump = null,
            Func<JsonRpcRequest, JsonValue> handleRequest = null)
        {
            // Tiny backoff bounds so reconnect tests run in milliseconds, not seconds - the
            // schedule itself (exponential + jitter) is a HadesClient implementation detail, not
            // something a caller needs realistic values for in a test.
            return new HadesClient(() => app.ConnectionInfo, hello,
                pump ?? MakeIdlePump(),
                handleRequest ?? (request => JsonValue.Bool(true)),
                TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(80));
        }

        [Test]
        public async Task Connect_SendsHelloCarryingProjectGuidPathVersionsAndPid()
        {
            using var app = new FakeApp();
            var hello = MakeHello(projectGuid: "11112222333344445555666677778888", unityVersion: "6000.3.2f1",
                projectPath: "/tmp/my-project", pluginVersion: "1.2.0", processId: 4321);

            using var client = MakeClient(app, hello);
            client.Start();

            using var conn = await app.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(app.Token, conn.Token, "the token line must match exactly what the connection file carried");
            Assert.IsNull(conn.HelloParseError, conn.HelloParseError);
            Assert.IsNotNull(conn.Hello);
            Assert.AreEqual("11112222333344445555666677778888", conn.Hello.ProjectGuid);
            Assert.AreEqual("/tmp/my-project", conn.Hello.ProjectPath);
            Assert.AreEqual("6000.3.2f1", conn.Hello.UnityVersion);
            Assert.AreEqual("1.2.0", conn.Hello.PluginVersion);
            Assert.AreEqual(4321, conn.Hello.ProcessId);
        }

        [Test]
        public async Task Request_GetsAResponse_OneJsonRpcMessagePerLine()
        {
            using var app = new FakeApp();
            using var client = MakeClient(app, MakeHello());
            client.Start();

            using var conn = await app.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

            await conn.SendRequestAsync("keepalive", 42);
            var response = await conn.ReadResponseAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(42, response.Id.AsInteger());
            Assert.IsFalse(response.IsError, "keepalive must not error");
        }

        [Test]
        public async Task DroppedConnection_ReconnectsAndSendsExactlyOneHelloPerConnection()
        {
            using var app = new FakeApp();
            using var client = MakeClient(app, MakeHello(processId: 111));
            client.Start();

            using (var first = await app.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.AreEqual(111, first.Hello.ProcessId);
            } // Dispose() closes the socket - simulates the domain-reload drop.

            using var second = await app.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(111, second.Hello.ProcessId);

            // Prove the second connection's handshake was exactly token+hello and nothing more:
            // the very next line off the wire answers a request we send now. If a duplicate
            // hello had snuck onto this connection, this read would return that stray line
            // instead of a well-formed response and the parse/id assertion below would fail.
            await second.SendRequestAsync("keepalive", 99);
            var response = await second.ReadResponseAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(99, response.Id.AsInteger());
        }

        [Test]
        public async Task ConnectionDrop_InvokesOnDisconnectedCallback()
        {
            // The release-paths plan's socket-disconnect net: ReloadGate needs to know the
            // instant a connection that was serving requests ends, from any cause, so it can
            // release whatever lock that connection's app might have been holding. This test
            // proves HadesClient actually calls the hook - the "does it jump the pump's queue"
            // and "does it release exactly once" behaviours live in ReloadGate's own suite
            // (ReloadReleasePathTests.cs), against the hook directly, not against a real socket.
            using var app = new FakeApp();
            var disconnectedCount = 0;
            using var client = new HadesClient(() => app.ConnectionInfo, MakeHello(), MakeIdlePump(),
                request => JsonValue.Bool(true),
                TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(80),
                onDisconnected: () => Interlocked.Increment(ref disconnectedCount));
            client.Start();

            using (await app.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5)))
            {
                // just handshake, then drop - the disconnect every script save causes.
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Volatile.Read(ref disconnectedCount) < 1 && DateTime.UtcNow < deadline)
                await Task.Delay(10);

            Assert.AreEqual(1, Volatile.Read(ref disconnectedCount),
                "onDisconnected must fire exactly once for the one connection that dropped");
        }

        [Test]
        public async Task FailedConnectionAttempt_DoesNotInvokeOnDisconnectedCallback()
        {
            // A refused connection never completed a handshake, so nothing was ever holding
            // anything over it - it must not be confused with a real disconnect.
            var deadListener = new TcpListener(IPAddress.Loopback, 0);
            deadListener.Start();
            var deadPort = ((IPEndPoint)deadListener.LocalEndpoint).Port;
            deadListener.Stop();

            var connectionInfo = new EditorConnectionInfo { Port = deadPort, Token = "irrelevant" };
            var disconnectedCount = 0;

            using var client = new HadesClient(() => connectionInfo, MakeHello(), MakeIdlePump(),
                request => JsonValue.Bool(true),
                TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(200),
                onDisconnected: () => Interlocked.Increment(ref disconnectedCount));
            client.Start();

            await Task.Delay(250);

            Assert.AreEqual(0, Volatile.Read(ref disconnectedCount),
                "a connection that never completed a handshake is not a disconnect");
        }

        [Test]
        public async Task RepeatedConnectionFailures_BackOffInsteadOfBusyLooping()
        {
            // A port nothing is listening on: every connection attempt fails immediately with
            // "connection refused", the same shape of failure as "the app has not started yet".
            var deadListener = new TcpListener(IPAddress.Loopback, 0);
            deadListener.Start();
            var deadPort = ((IPEndPoint)deadListener.LocalEndpoint).Port;
            deadListener.Stop();

            var connectionInfo = new EditorConnectionInfo { Port = deadPort, Token = "irrelevant" };
            var attemptCount = 0;
            EditorConnectionInfo Resolve()
            {
                Interlocked.Increment(ref attemptCount);
                return connectionInfo;
            }

            using var client = new HadesClient(Resolve, MakeHello(), MakeIdlePump(),
                request => JsonValue.Bool(true),
                TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(200));
            client.Start();

            await Task.Delay(250);
            var attempts = Volatile.Read(ref attemptCount);

            // A busy loop with no backoff would rack up hundreds of attempts against an
            // instantly-refused loopback port in 250ms; a real schedule starting at 30ms keeps
            // this to a handful - the exact count depends on jitter, so the bound is generous.
            Assert.Greater(attempts, 0, "sanity: it should have tried at least once");
            Assert.Less(attempts, 20, "connection failures must back off between attempts, not busy-loop");
        }

        [Test]
        public async Task Reconnect_ProducesNoUnityConsoleOutput()
        {
            using var app = new FakeApp();
            using var client = MakeClient(app, MakeHello());

            var logs = new List<string>();
            void OnLog(string condition, string stackTrace, LogType type) => logs.Add("[" + type + "] " + condition);

            Application.logMessageReceivedThreaded += OnLog;
            try
            {
                client.Start();

                using (var first = await app.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5)))
                {
                    // just handshake, then drop - the disconnect every script save causes.
                }

                using (await app.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5)))
                {
                    // Give any spurious log a moment to arrive before we assert its absence.
                    await Task.Delay(200);
                }
            }
            finally
            {
                Application.logMessageReceivedThreaded -= OnLog;
            }

            Assert.IsEmpty(logs, "reconnect must be silent, but logged: " + string.Join(" | ", logs));
        }

        [Test]
        public async Task MalformedLineFromApp_IsSkippedWithoutKillingTheConnection()
        {
            using var app = new FakeApp();
            using var client = MakeClient(app, MakeHello());
            client.Start();

            using var conn = await app.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

            await conn.WriteRawLineAsync("{ this is not valid json and has no closing brace");
            await conn.SendRequestAsync("keepalive", 7);

            var response = await conn.ReadResponseAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(7, response.Id.AsInteger());
            Assert.IsFalse(response.IsError);
        }

        [Test]
        public async Task Dispose_EndsTheIoThread()
        {
            using var app = new FakeApp();
            var client = MakeClient(app, MakeHello());
            client.Start();

            using var conn = await app.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(client.IsIoThreadRunning, "sanity: the thread should be alive once connected");

            client.Dispose();

            Assert.IsFalse(client.IsIoThreadRunning, "no thread may survive Dispose()");
        }

        [Test]
        public async Task Keepalive_IsAnsweredByTheIoThread_EvenWhileThePumpNeverDrains()
        {
            // This is the property that lets the app report editor_busy instead of concluding
            // Unity died: keepalive must come back fast even when whatever would service other
            // requests - the main thread, standing in for it here as a pump nobody ever ticks -
            // is completely unavailable.
            using var app = new FakeApp();
            using var pump = MakeIdlePump(); // Start()/Tick() deliberately never called below.
            using var client = new HadesClient(() => app.ConnectionInfo, MakeHello(), pump,
                request => JsonValue.Bool(true),
                TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(80));
            client.Start();

            using var conn = await app.AcceptAndHandshakeAsync(TimeSpan.FromSeconds(5));

            // Clogs the pump forever (nothing ever calls Tick()) - proves the fast path below
            // isn't fast merely because there was nothing else going on.
            await conn.SendRequestAsync("some_slow_tool", 1);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await conn.SendRequestAsync("keepalive", 2);
            var response = await conn.ReadResponseAsync(TimeSpan.FromSeconds(5));
            stopwatch.Stop();

            Assert.AreEqual(2, response.Id.AsInteger());
            Assert.IsFalse(response.IsError);
            Assert.Less(stopwatch.ElapsedMilliseconds, 2000,
                "keepalive must not wait on a pump that never drains");
        }

        /// <summary>Plays the app's side of the wire: accepts a socket, reads the raw token line
        /// then the hello line (exactly what <c>Hades.Core.Editors.EditorListener</c> does), and
        /// thereafter sends JSON-RPC request lines / reads response lines.</summary>
        sealed class FakeApp : IDisposable
        {
            readonly TcpListener _listener;

            public string Token { get; }
            public EditorConnectionInfo ConnectionInfo { get; }

            public FakeApp()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Token = "test-token-" + Guid.NewGuid().ToString("N");
                ConnectionInfo = new EditorConnectionInfo
                {
                    Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
                    Token = Token,
                };
            }

            public async Task<FakeConnection> AcceptAndHandshakeAsync(TimeSpan timeout)
            {
                var client = await WithTimeout(_listener.AcceptTcpClientAsync(), timeout, "accept");
                client.NoDelay = true;
                var stream = client.GetStream();
                var reader = new StreamReader(stream, new UTF8Encoding(false));
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

                var tokenLine = await WithTimeout(reader.ReadLineAsync(), timeout, "token line");
                var helloLine = await WithTimeout(reader.ReadLineAsync(), timeout, "hello line");
                Hello.TryParse(helloLine, out var hello, out var error);

                return new FakeConnection(client, reader, writer, tokenLine, hello, error);
            }

            public static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, string what)
            {
                var winner = await Task.WhenAny(task, Task.Delay(timeout));
                if (!ReferenceEquals(winner, task)) throw new TimeoutException("Timed out waiting for " + what);
                return await task;
            }

            public void Dispose() => _listener.Stop();
        }

        sealed class FakeConnection : IDisposable
        {
            readonly TcpClient _client;
            readonly StreamReader _reader;
            readonly StreamWriter _writer;

            public string Token { get; }
            public Hello Hello { get; }
            public string HelloParseError { get; }

            public FakeConnection(TcpClient client, StreamReader reader, StreamWriter writer,
                string token, Hello hello, string helloParseError)
            {
                _client = client;
                _reader = reader;
                _writer = writer;
                Token = token;
                Hello = hello;
                HelloParseError = helloParseError;
            }

            public Task WriteRawLineAsync(string line) => _writer.WriteLineAsync(line);

            public Task SendRequestAsync(string method, long id)
            {
                var request = new JsonRpcRequest { Id = JsonValue.Integer(id), Method = method };
                return _writer.WriteLineAsync(MiniJson.Write(request.ToJson()));
            }

            public async Task<JsonRpcResponse> ReadResponseAsync(TimeSpan timeout)
            {
                var line = await FakeApp.WithTimeout(_reader.ReadLineAsync(), timeout, "response line");
                Assert.IsTrue(JsonRpcResponse.TryParse(line, out var response, out var error),
                    "response failed to parse: " + error + " raw='" + line + "'");
                return response;
            }

            public void Dispose() => _client.Close();
        }
    }
}
