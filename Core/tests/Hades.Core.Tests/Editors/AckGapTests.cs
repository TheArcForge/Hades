using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Hades.Contract.Wire;
using Hades.Core.Editors;
using Hades.Core.Reading;
using Hades.Core.Storage;
using ModelContextProtocol;

namespace Hades.Core.Tests.Editors;

/// <summary>
/// The ack gap (spec #2 §5.4): the plugin can execute a mutation, write its response, and have the
/// socket die before the app reads it - a domain reload's exact failure shape. EditorSession
/// reproduces this two ways, both exercised below: <see cref="EditorSession.SendRequestAsync"/>'s
/// pending request fails with an IOException once the receive loop notices the peer is gone
/// (EditorSession.FailAllPending), or - the shape "measured directly, observed live" - a session
/// already torn down by the time a send is attempted throws ObjectDisposedException straight out
/// of EditorSession.WriteLine.
///
/// The fix is verification, not bookkeeping: <see cref="EditorProxy.SendCommandAsync"/> re-checks
/// project state through the SAME <see cref="ProjectService"/> every other tool already uses,
/// rather than remembering what it sent. Same real-loopback-socket harness as EditorProxyTests - a
/// fake Unity standing in over an actual TCP pair, not a mocked EditorSession, because the failure
/// shape being reproduced IS a transport failure.
/// </summary>
public sealed class AckGapTests : IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff00002222";
    const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly EditorRegistry _registry = new();
    readonly ProjectService _projects;
    readonly EditorProxy _proxy;
    readonly List<IDisposable> _toDispose = [];

    public AckGapTests()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {ProjectGuid}\n");
        Directory.CreateDirectory(Path.Combine(_projectRoot, "Assets"));

        _projects = new ProjectService(new AppPaths(_appRoot), _registry)
        {
            // Keeps the busy-probe fast, same tunable EditorProxyTests/CharonStatusTests shrink
            // for the same reason - none of these tests want to spend real time proving "busy".
            CharonProbeTimeout = TimeSpan.FromSeconds(5),
        };
        _projects.AdoptAndIndex(_projectRoot);

        _proxy = new EditorProxy(_projects, _registry) { CommandTimeout = TimeSpan.FromSeconds(2) };

        _listener.Start();
    }

    static Hello MakeHello() => new()
    {
        ProjectGuid = ProjectGuid,
        ProjectPath = "/tmp/fake-unity-project",
        UnityVersion = "6000.3.2f1",
        PluginVersion = "1.2.0",
        ProcessId = 4242,
    };

    void WriteAsset(string relative, string body)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
    }

    /// <summary>Same connect pattern as EditorProxyTests.AttachFakeUnityAsync, plus the raw
    /// TcpClient/EditorSession handles this file's tests need in order to simulate the connection
    /// dying from either side - EditorProxyTests never needed those, since none of its scenarios
    /// simulate a drop mid-flight.</summary>
    async Task<(StreamReader Reads, StreamWriter Writes, TcpClient Client, EditorSession Session)> AttachFakeUnityAsync()
    {
        var hello = MakeHello();
        var acceptTask = _listener.AcceptTcpClientAsync();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        var server = await acceptTask;

        _toDispose.Add(client);
        _toDispose.Add(server);

        var session = new EditorSession(server.GetStream(), hello);
        _toDispose.Add(session);
        session.Start();

        _registry.Register(new AttachedEditor { Hello = hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = session });

        var reads = new StreamReader(client.GetStream(), new UTF8Encoding(false));
        var writes = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        return (reads, writes, client, session);
    }

    /// <summary>Answers exactly one pending request - the busy probe GetCharonStatus sends before
    /// EditorProxy ever touches the real command - with a plain success.</summary>
    static async Task AnswerBusyProbeAsync(StreamReader reads, StreamWriter writes)
    {
        var line = await reads.ReadLineAsync();
        Assert.True(JsonRpcRequest.TryParse(line, out var request, out _));
        await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request!.Id!, JsonValue.Bool(true)).ToJson()));
    }

    // ---------------------------------------------------------------- no verifier: the honest default

    [Fact]
    public async Task RemoteDisconnectMidFlight_NoVerifierSupplied_ReturnsInterruptedStateUnverified()
    {
        var (reads, writes, client, _) = await AttachFakeUnityAsync();

        var responder = Task.Run(async () =>
        {
            await AnswerBusyProbeAsync(reads, writes);
            var line = await reads.ReadLineAsync(); // reads the real command, never answers it
            Assert.True(JsonRpcRequest.TryParse(line, out _, out _));
            client.Close(); // the plugin may already have executed this - the ack gap itself
        });

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync(ProjectGuid, "scene.create_gameobject"));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("interrupted, state unverified, re-query before retrying", ex.Message);
    }

    [Fact]
    public async Task SessionAlreadyDisposedAtSendTime_ObjectDisposedException_IsTreatedAsAckGapToo()
    {
        // The OTHER real shape - "the existing ObjectDisposedException on a dead NetworkStream...
        // observed live": the session that answered GetCharonStatus's busy probe is gone by the
        // time SendCommandAsync goes to send the REAL command - the exact narrow race
        // EditorProxy.SendCommandAsync's own doc comment describes ("a disconnect can still land
        // in the gap between that call returning and this one running"). Made deterministic
        // instead of timing-dependent: the healthy connection answers the probe, and BEFORE that
        // answer is even written back, a second, already-torn-down session is registered in its
        // place - so by the time SendCommandAsync's own registry.Get runs (which cannot happen
        // before it has received and processed the probe reply), it is guaranteed to find the
        // dead one.
        var (reads, writes, _, _) = await AttachFakeUnityAsync();

        var responder = Task.Run(async () =>
        {
            var line = await reads.ReadLineAsync();
            Assert.True(JsonRpcRequest.TryParse(line, out var request, out _));

            var deadSession = await MakeAlreadyDeadSessionAsync();
            _registry.Register(new AttachedEditor { Hello = MakeHello(), ConnectedAtUtc = DateTimeOffset.UtcNow, Session = deadSession });

            await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request!.Id!, JsonValue.Bool(true)).ToJson()));
        });

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync(ProjectGuid, "scene.create_gameobject"));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("interrupted, state unverified, re-query before retrying", ex.Message);
    }

    /// <summary>A second real loopback connection, started and immediately disposed - a valid
    /// EditorSession whose underlying stream is already gone, so the next attempt to send through
    /// it hits ObjectDisposedException in EditorSession.WriteLine rather than the "Start() was
    /// never called" guard in SendRequestAsync.</summary>
    async Task<EditorSession> MakeAlreadyDeadSessionAsync()
    {
        var acceptTask = _listener.AcceptTcpClientAsync();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        var server = await acceptTask;
        _toDispose.Add(client);
        _toDispose.Add(server);

        var deadSession = new EditorSession(server.GetStream(), MakeHello());
        deadSession.Start();
        deadSession.Dispose();
        _toDispose.Add(deadSession);

        return deadSession;
    }

    // ---------------------------------------------------------------- verifier: applied

    [Fact]
    public async Task VerifierConfirmsApplied_ReportsSuccess_NotedAsVerifiedNotAcknowledged()
    {
        // The GameObject the "lost" scene.create_gameobject response would have described,
        // already on disk - standing in for a scene that happened to be saved before this check
        // ran. Found by NAME via ReadThrough (an on-demand read of the one file that matters, no
        // reindex needed), deliberately never by fileId - see GameObjectMutationResult's own doc
        // comment (Core/src/Hades.Server/Mcp/EditorSceneTools.cs) for why a freshly created
        // object's fileId legitimately reads 0 until the containing scene is saved AND next
        // reloaded, which makes it useless as a verification key immediately after creation.
        WriteAsset("Assets/Scenes/Sample.unity", Header
            + "--- !u!1 &1\nGameObject:\n  m_Component:\n  - component: {fileID: 2}\n  m_Name: NewlyCreated\n"
            + "--- !u!4 &2\nTransform:\n  m_GameObject: {fileID: 1}\n  m_Father: {fileID: 0}\n");

        var (reads, writes, client, _) = await AttachFakeUnityAsync();
        var responder = Task.Run(async () =>
        {
            await AnswerBusyProbeAsync(reads, writes);
            await reads.ReadLineAsync();
            client.Close();
        });

        var verifyCalls = 0;

        Task<AckGapVerification> Verify(ProjectService projects, string productGuid, CancellationToken ct)
        {
            verifyCalls++;
            var hierarchy = ReadThrough.GetHierarchy(_projectRoot, "Assets/Scenes/Sample.unity");
            var found = hierarchy.Roots.Any(n => n.Name == "NewlyCreated"); // by NAME, never fileId
            return Task.FromResult(new AckGapVerification
            {
                Outcome = found ? AckGapOutcome.Applied : AckGapOutcome.Ambiguous,
            });
        }

        var result = await _proxy.SendCommandAsync(ProjectGuid, "scene.create_gameobject", verifyIfInterrupted: Verify);
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, verifyCalls);
        Assert.True(result.TryGetProperty("hadesVerifiedNotAcknowledged", out var flag) && flag!.AsBoolean());
        Assert.True(result.TryGetProperty("hadesVerificationNote", out var note));
        Assert.Contains("verified", note!.AsString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("acknowledg", note.AsString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- verifier: not applied

    [Fact]
    public async Task VerifierConfirmsNotApplied_ThrowsClearRetrySafeError()
    {
        var (reads, writes, client, _) = await AttachFakeUnityAsync();
        var responder = Task.Run(async () =>
        {
            await AnswerBusyProbeAsync(reads, writes);
            await reads.ReadLineAsync();
            client.Close();
        });

        // Real graph-based verification this time - exactly "re-index the affected asset and
        // look" (spec #2 §5.4): SyncChanges brings the graph up to date, then Search asks it
        // directly. Nothing was ever written under this name, so this is a genuine confirmed
        // negative, not silence mistaken for one.
        Task<AckGapVerification> Verify(ProjectService projects, string productGuid, CancellationToken ct)
        {
            projects.SyncChanges(productGuid);
            var found = projects.Search(productGuid, "GameObjectThatWasNeverWritten");
            return Task.FromResult(new AckGapVerification
            {
                Outcome = found.Count == 0 ? AckGapOutcome.NotApplied : AckGapOutcome.Applied,
            });
        }

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync(ProjectGuid, "scene.create_gameobject", verifyIfInterrupted: Verify));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("did not", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- verifier: ambiguous / throws

    [Fact]
    public async Task VerifierItselfReportsAmbiguous_ReturnsInterruptedStateUnverified()
    {
        var (reads, writes, client, _) = await AttachFakeUnityAsync();
        var responder = Task.Run(async () =>
        {
            await AnswerBusyProbeAsync(reads, writes);
            await reads.ReadLineAsync();
            client.Close();
        });

        Task<AckGapVerification> Verify(ProjectService projects, string productGuid, CancellationToken ct) =>
            Task.FromResult(new AckGapVerification { Outcome = AckGapOutcome.Ambiguous });

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync(ProjectGuid, "scene.create_gameobject", verifyIfInterrupted: Verify));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("interrupted, state unverified, re-query before retrying", ex.Message);
    }

    [Fact]
    public async Task VerifierThrows_StillFoldsIntoInterruptedStateUnverified_NotACrash()
    {
        var (reads, writes, client, _) = await AttachFakeUnityAsync();
        var responder = Task.Run(async () =>
        {
            await AnswerBusyProbeAsync(reads, writes);
            await reads.ReadLineAsync();
            client.Close();
        });

        static Task<AckGapVerification> Verify(ProjectService projects, string productGuid, CancellationToken ct) =>
            throw new InvalidOperationException("the verifier's own graph query blew up");

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync(ProjectGuid, "scene.create_gameobject", verifyIfInterrupted: Verify));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("interrupted, state unverified, re-query before retrying", ex.Message);
    }

    // ---------------------------------------------------------------- must not overreach

    [Fact]
    public async Task GenuineTimeout_ConnectionStillAlive_VerifierIsNeverInvoked()
    {
        var (reads, writes, _, _) = await AttachFakeUnityAsync();
        // Answers the busy probe (genuinely responsive) but never answers the real command, and
        // never drops the connection either - a plain slow main thread, not an ack gap.
        var responder = AnswerBusyProbeAsync(reads, writes);

        var verifyCalls = 0;

        Task<AckGapVerification> Verify(ProjectService projects, string productGuid, CancellationToken ct)
        {
            verifyCalls++;
            return Task.FromResult(new AckGapVerification { Outcome = AckGapOutcome.Applied });
        }

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync(ProjectGuid, "project_run_tests", verifyIfInterrupted: Verify));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, verifyCalls);
    }

    [Fact]
    public async Task OrdinaryPluginError_VerifierIsNeverInvoked_MessageUnchanged()
    {
        var (reads, writes, _, _) = await AttachFakeUnityAsync();
        var responder = Task.Run(async () =>
        {
            await AnswerBusyProbeAsync(reads, writes);
            var line = await reads.ReadLineAsync();
            JsonRpcRequest.TryParse(line, out var request, out _);
            await writes.WriteLineAsync(MiniJson.Write(
                JsonRpcResponse.Failure(request!.Id!, -32603, "GameObject 'Foo' does not exist.").ToJson()));
        });

        var verifyCalls = 0;

        Task<AckGapVerification> Verify(ProjectService projects, string productGuid, CancellationToken ct)
        {
            verifyCalls++;
            return Task.FromResult(new AckGapVerification { Outcome = AckGapOutcome.Applied });
        }

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync(ProjectGuid, "scene.delete_gameobject", verifyIfInterrupted: Verify));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("GameObject 'Foo' does not exist.", ex.Message);
        Assert.Equal(0, verifyCalls);
    }

    // ---------------------------------------------------------------- assert the DESIGN, not only the behaviour

    [Fact]
    public void NoIdempotencyLedgerReplayTableOrPendingMutationJournalExistsOnEditorProxy()
    {
        // The instinct a future contributor will have on meeting this ack gap is to add exactly
        // one of these three things - a table remembering which request ids were already applied.
        // The whole design bet (spec #2 §5.4) is that none of them are needed, because the app can
        // always just LOOK at project state instead of remembering what it sent. This is not a
        // behavioural test - it is a structural assertion on EditorProxy itself, so it fails
        // loudly the moment such a field is introduced, however the surrounding behaviour reads.
        var bannedTerms = new[] { "ledger", "journal", "replay", "idempot" };

        var fields = typeof(EditorProxy).GetFields(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var field in fields)
        {
            foreach (var term in bannedTerms)
            {
                Assert.False(
                    field.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || field.FieldType.Name.Contains(term, StringComparison.OrdinalIgnoreCase),
                    $"EditorProxy.{field.Name} ({field.FieldType.Name}) looks like exactly the "
                    + "idempotency-ledger/replay-table/pending-mutation-journal bookkeeping the "
                    + "ack-gap design deliberately does NOT use (spec #2 §5.4) - verification "
                    + "replaces bookkeeping here, it does not sit alongside it. If a real, "
                    + "unrelated field ever legitimately needs one of these words, this test - not "
                    + "just this comment - is the thing that must change, deliberately.");
            }
        }
    }

    [Fact]
    public async Task RepeatedIdenticalAckGaps_EachIndependentlyReVerified_NoMemoizedShortcut()
    {
        // Two SEPARATE ack gaps for the exact same method and params, back to back. If EditorProxy
        // remembered the first outcome anywhere - a ledger in spirit, even without the name - the
        // second call could short-circuit without calling the verifier again. It must not: every
        // ack gap is independently looked at, never recalled.
        var sameParams = JsonValue.NewObject().SetProperty("name", JsonValue.String("Repeatable"));
        var verifyCalls = 0;

        Task<AckGapVerification> Verify(ProjectService projects, string productGuid, CancellationToken ct)
        {
            verifyCalls++;
            return Task.FromResult(new AckGapVerification { Outcome = AckGapOutcome.Applied });
        }

        for (var i = 0; i < 2; i++)
        {
            var (reads, writes, client, _) = await AttachFakeUnityAsync();
            var responder = Task.Run(async () =>
            {
                await AnswerBusyProbeAsync(reads, writes);
                await reads.ReadLineAsync();
                client.Close();
            });

            await _proxy.SendCommandAsync(ProjectGuid, "scene.create_gameobject", sameParams, verifyIfInterrupted: Verify);
            await responder.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(2, verifyCalls);
    }

    public void Dispose()
    {
        _listener.Stop();
        foreach (var disposable in _toDispose) disposable.Dispose();

        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
