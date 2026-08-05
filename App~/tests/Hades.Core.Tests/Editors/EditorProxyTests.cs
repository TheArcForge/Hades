using System.Net;
using System.Net.Sockets;
using System.Text;
using Hades.Contract.Wire;
using Hades.Core.Editors;
using Hades.Core.Storage;
using ModelContextProtocol;

namespace Hades.Core.Tests.Editors;

/// <summary>
/// EditorProxy end to end: a real loopback socket pair standing in for the plugin (same pattern
/// as EditorSessionTests), registered into a real EditorRegistry a real ProjectService shares -
/// exactly the wiring hades_charon_status itself uses (see CharonStatusTests, the Server-side
/// analogue this mirrors at the Core level, without any ASP.NET Core involved).
/// </summary>
public sealed class EditorProxyTests : IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff00001111";

    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly EditorRegistry _registry = new();
    readonly ProjectService _projects;
    readonly EditorProxy _proxy;
    readonly List<IDisposable> _toDispose = [];

    public EditorProxyTests()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {ProjectGuid}\n");

        _projects = new ProjectService(new AppPaths(_appRoot), _registry)
        {
            // Keeps the busy-probe tests fast without weakening what they prove - same tunable
            // CharonStatusTests shrinks for the same reason.
            CharonProbeTimeout = TimeSpan.FromMilliseconds(300),
        };
        _projects.AdoptAndIndex(_projectRoot);

        _proxy = new EditorProxy(_projects, _registry) { CommandTimeout = TimeSpan.FromSeconds(2) };

        _listener.Start();
    }

    static Hello MakeHello(long processId = 4242) => new()
    {
        ProjectGuid = ProjectGuid,
        ProjectPath = "/tmp/fake-unity-project",
        UnityVersion = "6000.3.2f1",
        PluginVersion = "1.2.0",
        ProcessId = processId,
    };

    /// <summary>Connects a real loopback socket pair and registers the server end as this
    /// project's attached editor - EditorSessionTests.ConnectAsync's pattern, plus the
    /// EditorRegistry registration both ProjectService.GetCharonStatus and EditorProxy itself key
    /// off.</summary>
    async Task<(StreamReader UnityReads, StreamWriter UnityWrites)> AttachFakeUnityAsync(Hello? hello = null)
    {
        var effectiveHello = hello ?? MakeHello();
        var acceptTask = _listener.AcceptTcpClientAsync();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        var server = await acceptTask;

        _toDispose.Add(client);
        _toDispose.Add(server);

        var session = new EditorSession(server.GetStream(), effectiveHello);
        _toDispose.Add(session);
        session.Start();

        _registry.Register(new AttachedEditor
        {
            Hello = effectiveHello,
            ConnectedAtUtc = DateTimeOffset.UtcNow,
            Session = session,
        });

        var unityReads = new StreamReader(client.GetStream(), new UTF8Encoding(false));
        var unityWrites = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        return (unityReads, unityWrites);
    }

    /// <summary>Answers exactly one pending request - whichever line is next - with a plain
    /// success. Used to answer the busy probe GetCharonStatus sends before EditorProxy ever
    /// touches the real command.</summary>
    static async Task AnswerOneAsync(StreamReader reads, StreamWriter writes)
    {
        var line = await reads.ReadLineAsync();
        Assert.True(JsonRpcRequest.TryParse(line, out var request, out _));
        await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request!.Id!, JsonValue.Bool(true)).ToJson()));
    }

    // ---------------------------------------------------------------- not attached

    [Fact]
    public async Task NoEditorAttached_NamesHadesCharonStatus_NotATimeoutOrGenericFailure()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync(ProjectGuid, "assets.refresh"));

        Assert.Contains("hades_charon_status", ex.Message);
        Assert.DoesNotContain("busy", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- busy

    [Fact]
    public async Task EditorAttachedButBusy_SaysBusy_DistinctFromNotAttached()
    {
        // Registered, but never answers the probe GetCharonStatus sends - a blocked main thread:
        // the connection itself is alive, nothing is draining it. Same setup as
        // CharonStatusTests.EditorAttachedButMainThreadBlocked_ReportsBusyNotGone.
        await AttachFakeUnityAsync();

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync(ProjectGuid, "assets.refresh"));

        Assert.Contains("busy", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("none is attached", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- plugin-side exception

    [Fact]
    public async Task PluginError_BecomesMcpExceptionCarryingThePluginsOwnMessage()
    {
        var (reads, writes) = await AttachFakeUnityAsync();

        var responder = Task.Run(async () =>
        {
            await AnswerOneAsync(reads, writes); // the busy probe - responsive

            // The real command: a JSON-RPC error exactly as HadesClient reports a plugin
            // exception - the thrown exception's own Message, not a wrapped stack trace (see
            // HadesClient.DescribeFailure on the plugin side).
            var line = await reads.ReadLineAsync();
            JsonRpcRequest.TryParse(line, out var request, out _);
            await writes.WriteLineAsync(MiniJson.Write(
                JsonRpcResponse.Failure(request!.Id!, -32603, "GameObject 'Foo' does not exist.").ToJson()));
        });

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync(ProjectGuid, "scene.delete_gameobject"));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("GameObject 'Foo' does not exist.", ex.Message);
    }

    // ---------------------------------------------------------------- timeout

    [Fact]
    public async Task CommandTimesOut_ReportsWhichCommandAndHowLong()
    {
        var (reads, writes) = await AttachFakeUnityAsync();

        // Answers the busy probe (responsive - this is a genuine timeout, not busy) but never
        // answers the real command at all.
        var responder = AnswerOneAsync(reads, writes);

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync(ProjectGuid, "project_run_tests"));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("project_run_tests", ex.Message);
        Assert.Contains("2", ex.Message); // CommandTimeout is 2s in this fixture
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- success path (sanity)

    [Fact]
    public async Task Success_ReturnsThePluginsResult()
    {
        var (reads, writes) = await AttachFakeUnityAsync();

        var responder = Task.Run(async () =>
        {
            await AnswerOneAsync(reads, writes); // the busy probe

            var line = await reads.ReadLineAsync();
            JsonRpcRequest.TryParse(line, out var request, out _);
            var result = JsonValue.NewObject().SetProperty("refreshed", JsonValue.Bool(true));
            await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request!.Id!, result).ToJson()));
        });

        var result = await _proxy.SendCommandAsync(ProjectGuid, "assets.refresh");
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.TryGetProperty("refreshed", out var refreshed));
        Assert.True(refreshed!.AsBoolean());
    }

    // ---------------------------------------------------------------- project resolution

    [Fact]
    public async Task UnknownProjectHandle_ResolvesExactlyLikeEveryOtherTool()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync("not-a-real-project", "assets.refresh"));

        Assert.Contains("Unknown project 'not-a-real-project'", ex.Message);
    }

    [Fact]
    public async Task NoHandle_FallsBackToTheSoleKnownProject()
    {
        // No editor attached, but reaching the "not attached" error at all - rather than a
        // project-resolution error - proves resolution succeeded with no handle supplied.
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _proxy.SendCommandAsync(null, "assets.refresh"));

        Assert.Contains("hades_charon_status", ex.Message);
    }

    public void Dispose()
    {
        _listener.Stop();
        foreach (var disposable in _toDispose) disposable.Dispose();

        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
