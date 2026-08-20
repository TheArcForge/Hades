using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hades.Contract.Wire;
using Hades.Core;
using Hades.Core.Editors;
using Hades.Core.Storage;
using Hades.Server.Control;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Hades.Server.Tests.Control;

/// <summary>
/// Pure, deterministic tests of <see cref="EditorsEndpoint.Resolve"/> - the busy/attached
/// resolution logic behind <c>GET /control/editors</c>, exercised directly against hand-built
/// <see cref="EditorStateSnapshot"/> inputs, no clock, no sockets, no HTTP. Same "verbatim"
/// discipline as SummaryTests.cs/ProjectsTests.cs's own Resolve suites: every expected display
/// string below is a hand-typed literal. See <see cref="EditorsBuildAsyncTests"/> for proof this
/// logic is actually fed real <see cref="ProjectService"/>/<see cref="EditorRegistry"/> state.
/// </summary>
public sealed class EditorsResolveTests
{
    static EditorStateSnapshot Snapshot(string name, bool attached, bool busy) => new()
    {
        Name = name,
        ProductGuid = "guid-" + name,
        Attached = attached,
        Busy = busy,
        UnityVersion = attached ? "6000.3.2f1" : null,
        ProcessId = attached ? 4321 : null,
        ConnectionAge = attached ? TimeSpan.FromSeconds(90) : null,
    };

    [Fact]
    public void NoProjects_IsAWellFormedEmptyResponse()
    {
        var result = EditorsEndpoint.Resolve([]);

        Assert.Empty(result.Editors);
    }

    [Fact]
    public void NoEditorsAttached_ProducesAnEmptyList_AbsentProjectsAreNotRows()
    {
        var result = EditorsEndpoint.Resolve([Snapshot("Idle", attached: false, busy: false)]);

        // "Absent" is exclusion from this list, not a value it can take - unlike
        // ProjectsEndpoint's per-project Editor field, which represents the SAME project row
        // either way. See EditorsEndpoint's own class doc comment.
        Assert.Empty(result.Editors);
    }

    [Fact]
    public void AttachedNotBusy_StateAndStatusAreExactLiterals()
    {
        var result = EditorsEndpoint.Resolve([Snapshot("Hades-Unity-Client", attached: true, busy: false)]);

        var row = Assert.Single(result.Editors);
        Assert.Equal("Hades-Unity-Client", row.Project);
        Assert.Equal("guid-Hades-Unity-Client", row.ProductGuid);
        Assert.Equal(ProjectEditorState.Attached, row.State);
        Assert.Equal("Editor attached", row.Status);
        Assert.Equal("6000.3.2f1", row.UnityVersion);
        Assert.Equal(4321, row.ProcessId);
        Assert.Equal(90, row.ConnectionAgeSeconds);
    }

    [Fact]
    public void AttachedAndBusy_StateAndStatusAreExactLiterals_NeverReadsAsPlainAttached()
    {
        var result = EditorsEndpoint.Resolve([Snapshot("Hades-Unity-Client", attached: true, busy: true)]);

        var row = Assert.Single(result.Editors);
        Assert.Equal(ProjectEditorState.Busy, row.State);
        Assert.Equal("Editor attached (busy)", row.Status);
    }

    [Fact]
    public void MultipleProjects_OnlyAttachedOnesAppear_EachAsItsOwnRow()
    {
        var result = EditorsEndpoint.Resolve([
            Snapshot("Absent", attached: false, busy: false),
            Snapshot("Attached", attached: true, busy: false),
            Snapshot("Busy", attached: true, busy: true),
        ]);

        Assert.Equal(2, result.Editors.Count);
        Assert.Contains(result.Editors, r => r.Project == "Attached" && r.State == ProjectEditorState.Attached);
        Assert.Contains(result.Editors, r => r.Project == "Busy" && r.State == ProjectEditorState.Busy);
        Assert.DoesNotContain(result.Editors, r => r.Project == "Absent");
    }
}

/// <summary>
/// Proves <see cref="EditorsEndpoint.BuildAsync"/> actually reuses
/// <see cref="ProjectService.GetCharonStatus"/> - the exact same probe hades_charon_status and
/// SummaryEndpoint/ProjectsEndpoint already use - rather than a second busy-detector. Same
/// real-loopback-socket construction technique as SummaryTests.cs's own SummaryBuildAsyncTests and
/// ProjectsTests.cs's own ProjectsBuildAsyncTests (duplicated locally rather than shared - same
/// convention those two files already established between each other).
/// </summary>
public sealed class EditorsBuildAsyncTests : IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff11100001";

    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly List<IDisposable> _toDispose = [];
    readonly List<TcpListener> _listeners = [];
    readonly EditorRegistry _editorRegistry = new();
    readonly ProjectService _projects;

    public EditorsBuildAsyncTests()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {ProjectGuid}\n");

        _projects = new ProjectService(new AppPaths(_appRoot), _editorRegistry)
        {
            CharonProbeTimeout = TimeSpan.FromMilliseconds(300),
        };
    }

    static Hello MakeHello() => new()
    {
        ProjectGuid = ProjectGuid,
        ProjectPath = "/tmp/fake-unity-project",
        UnityVersion = "6000.3.2f1",
        PluginVersion = "1.2.0",
        ProcessId = 4321,
    };

    async Task<(StreamReader UnityReads, StreamWriter UnityWrites)> RegisterFakeEditorAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);

        var acceptTask = listener.AcceptTcpClientAsync();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await acceptTask;
        _toDispose.Add(client);
        _toDispose.Add(server);

        var session = new EditorSession(server.GetStream(), MakeHello());
        _toDispose.Add(session);
        session.Start();

        var unityReads = new StreamReader(client.GetStream(), new UTF8Encoding(false));
        var unityWrites = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        _editorRegistry.Register(new AttachedEditor { Hello = session.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = session });

        return (unityReads, unityWrites);
    }

    static Task RespondToNextProbeAsync(StreamReader reads, StreamWriter writes) => Task.Run(async () =>
    {
        var line = await reads.ReadLineAsync();
        if (line is not null && JsonRpcRequest.TryParse(line, out var request, out _) && request is not null)
        {
            await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request.Id!, JsonValue.Bool(true)).ToJson()));
        }
    });

    [Fact]
    public async Task NoEditorAttached_EmptyList()
    {
        _projects.AdoptAndIndex(_projectRoot);

        var result = await EditorsEndpoint.BuildAsync(_projects);

        Assert.Empty(result.Editors);
    }

    [Fact]
    public async Task AttachedAndResponsive_ReusesGetCharonStatus_RowReflectsAttached()
    {
        _projects.AdoptAndIndex(_projectRoot);
        var (unityReads, unityWrites) = await RegisterFakeEditorAsync();
        var responder = RespondToNextProbeAsync(unityReads, unityWrites);

        var result = await EditorsEndpoint.BuildAsync(_projects);
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        var row = Assert.Single(result.Editors);
        Assert.Equal(ProjectEditorState.Attached, row.State);
        Assert.Equal("Editor attached", row.Status);
        Assert.Equal("6000.3.2f1", row.UnityVersion);
        Assert.Equal(4321, row.ProcessId);
        Assert.NotNull(row.ConnectionAgeSeconds);
    }

    [Fact]
    public async Task AttachedButBusy_ReusesGetCharonStatus_RowIsBusy()
    {
        _projects.AdoptAndIndex(_projectRoot);
        await RegisterFakeEditorAsync();
        // Deliberately never answers the probe - the same "busy" condition SummaryTests.cs's own
        // AttachedButBusy_... test and CharonStatusTests prove at their own layers.

        var result = await EditorsEndpoint.BuildAsync(_projects);

        var row = Assert.Single(result.Editors);
        Assert.Equal(ProjectEditorState.Busy, row.State);
        Assert.Equal("Editor attached (busy)", row.Status);
    }

    public void Dispose()
    {
        foreach (var d in _toDispose) d.Dispose();
        foreach (var l in _listeners) l.Stop();

        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

/// <summary>
/// <see cref="EditorsEndpoint.ReleaseAsync"/> - the force-release action - called directly (no
/// HTTP), same division of labour as ProjectsTests.cs's own ProjectsBuildAsyncTests for the action
/// methods it tests directly. This is where every one of Task 4's required test properties lives:
/// idempotent release, no-editor-attached fails informatively and fast (not a timeout), and the
/// release path actually sends "lease.release" over the wire.
/// </summary>
public sealed class EditorsReleaseTests : IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff22200002";

    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly List<IDisposable> _toDispose = [];
    readonly List<TcpListener> _listeners = [];
    readonly EditorRegistry _editorRegistry = new();
    readonly ProjectService _projects;
    readonly LeaseRegistry _leases = new();
    readonly EditorProxy _editorProxy;

    public EditorsReleaseTests()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {ProjectGuid}\n");

        _projects = new ProjectService(new AppPaths(_appRoot), _editorRegistry)
        {
            CharonProbeTimeout = TimeSpan.FromMilliseconds(300),
        };
        _editorProxy = new EditorProxy(_projects, _editorRegistry);

        _projects.Adopt(_projectRoot);
    }

    static Hello MakeHello() => new()
    {
        ProjectGuid = ProjectGuid,
        ProjectPath = "/tmp/fake-unity-project",
        UnityVersion = "6000.3.2f1",
        PluginVersion = "1.2.0",
        ProcessId = 4321,
    };

    async Task<(StreamReader UnityReads, StreamWriter UnityWrites)> RegisterFakeEditorAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);

        var acceptTask = listener.AcceptTcpClientAsync();
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await acceptTask;
        _toDispose.Add(client);
        _toDispose.Add(server);

        var session = new EditorSession(server.GetStream(), MakeHello());
        _toDispose.Add(session);
        session.Start();

        var unityReads = new StreamReader(client.GetStream(), new UTF8Encoding(false));
        var unityWrites = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        _editorRegistry.Register(new AttachedEditor { Hello = session.Hello, ConnectedAtUtc = DateTimeOffset.UtcNow, Session = session });

        return (unityReads, unityWrites);
    }

    /// <summary>Answers the busy probe with a plain success, then the real command (expected to be
    /// "lease.release") with <paramref name="result"/> - the exact two-step cadence
    /// EditorProxy.SendCommandAsync always uses (busy probe, then the real command). Returns the
    /// parsed real request so a test can assert the method and params actually sent.</summary>
    static async Task<JsonRpcRequest> AnswerProbeThenRespondAsync(StreamReader reads, StreamWriter writes, JsonValue result)
    {
        var probeLine = await reads.ReadLineAsync();
        Assert.True(JsonRpcRequest.TryParse(probeLine, out var probe, out var probeError), probeError);
        await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(probe!.Id!, JsonValue.Bool(true)).ToJson()));

        var realLine = await reads.ReadLineAsync();
        Assert.True(JsonRpcRequest.TryParse(realLine, out var real, out var realError), realError);
        await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(real!.Id!, result).ToJson()));
        return real;
    }

    static JsonValue LeaseResult(bool success, string? leaseId = null, long? expiresAtUtcMs = null)
    {
        var o = JsonValue.NewObject();
        o.SetProperty("success", JsonValue.Bool(success));
        o.SetProperty("leaseId", leaseId is null ? JsonValue.Null : JsonValue.String(leaseId));
        o.SetProperty("expiresAtUtcMs", expiresAtUtcMs is null ? JsonValue.Null : JsonValue.Integer(expiresAtUtcMs.Value));
        return o;
    }

    // ---------------------------------------------------------------- unknown project

    [Fact]
    public async Task UnknownProject_Returns404()
    {
        var response = await EditorsEndpoint.ReleaseAsync(_projects, _leases, _editorProxy, "not-a-known-guid");

        Assert.Equal(StatusCodes.Status404NotFound, await StatusCodeOf(response));
    }

    // ---------------------------------------------------------------- idempotent: already gone

    [Fact]
    public async Task NoLeaseHeld_SucceedsIdempotently_TheCommonTtlFiredCase()
    {
        // No RecordHeld call at all - by the time a user clicks, the TTL may already have fired
        // (plan 8 proved TTL release works) and this app never heard about it going stale. Failing
        // here would be user-hostile and wrong - see EditorsEndpoint's own class doc comment.
        var response = await EditorsEndpoint.ReleaseAsync(_projects, _leases, _editorProxy, ProjectGuid);
        var json = await ResultBodyAsync(response);

        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Contains("nothing to release", json.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoLeaseHeld_NeverTouchesTheWire_NoFakeEditorNeededAtAll()
    {
        // Proves the idempotent-no-op path is genuinely local: register NO fake editor at all (if
        // this tried to reach one, the call would hang until EditorProxy's own timeout).
        var response = await EditorsEndpoint.ReleaseAsync(_projects, _leases, _editorProxy, ProjectGuid);

        Assert.Equal(StatusCodes.Status200OK, await StatusCodeOf(response));
    }

    // ---------------------------------------------------------------- no editor attached

    [Fact]
    public async Task NoEditorAttached_FailsInformatively_NamesTheFact_NotATimeout()
    {
        _leases.RecordHeld(ProjectGuid, "hades-script-editing", DateTimeOffset.UtcNow.AddSeconds(30));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await EditorsEndpoint.ReleaseAsync(_projects, _leases, _editorProxy, ProjectGuid);
        stopwatch.Stop();

        var json = await ResultBodyAsync(response);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Contains("no", json.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attached", json.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

        // "Not a timeout": EditorProxy.SendCommandAsync's own CommandTimeout defaults to 30s, and
        // GetCharonStatus's own probe timeout defaults to 1.5s - neither is ever reached here,
        // because no registration at all means ProjectService.GetCharonStatus answers "not
        // attached" synchronously, no wire round trip needed. This must return in well under
        // either timeout, proving the failure is immediate and informative, not a stall.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"expected an immediate failure, took {stopwatch.Elapsed}");
    }

    // ---------------------------------------------------------------- busy

    [Fact]
    public async Task EditorBusy_FailsInformatively_ViaEditorProxysOwnBusyDetection()
    {
        _leases.RecordHeld(ProjectGuid, "hades-script-editing", DateTimeOffset.UtcNow.AddSeconds(30));
        await RegisterFakeEditorAsync();
        // Deliberately never answers the probe - CharonProbeTimeout is 300ms here, so this fails
        // fast rather than waiting for EditorProxy's own 30s command timeout.

        var response = await EditorsEndpoint.ReleaseAsync(_projects, _leases, _editorProxy, ProjectGuid);
        var json = await ResultBodyAsync(response);

        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Contains("busy", json.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- the primary success path

    [Fact]
    public async Task EditorAttachedAndResponsive_SendsLeaseReleaseWireMethod_WithTheBelievedLeaseId()
    {
        _leases.RecordHeld(ProjectGuid, "hades-script-editing", DateTimeOffset.UtcNow.AddSeconds(30));
        var (reads, writes) = await RegisterFakeEditorAsync();
        var responderTask = AnswerProbeThenRespondAsync(reads, writes, LeaseResult(success: true));

        var response = await EditorsEndpoint.ReleaseAsync(_projects, _leases, _editorProxy, ProjectGuid);
        var request = await responderTask.WaitAsync(TimeSpan.FromSeconds(5));
        var json = await ResultBodyAsync(response);

        // The release path goes through the plugin's existing lease.release - not a second path.
        Assert.Equal("lease.release", request.Method);
        Assert.True(request.Params!.TryGetProperty("leaseId", out var leaseId) && leaseId!.AsString() == "hades-script-editing");

        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Contains("Released", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task EditorAttachedAndResponsive_Success_ClearsLeaseRegistry_SoSummaryStopsShowingTheRow()
    {
        _leases.RecordHeld(ProjectGuid, "hades-script-editing", DateTimeOffset.UtcNow.AddSeconds(30));
        var (reads, writes) = await RegisterFakeEditorAsync();
        var responderTask = AnswerProbeThenRespondAsync(reads, writes, LeaseResult(success: true));

        await EditorsEndpoint.ReleaseAsync(_projects, _leases, _editorProxy, ProjectGuid);
        await responderTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(_leases.Get(ProjectGuid));
    }

    // ---------------------------------------------------------------- races a concurrent RecordHeld

    [Fact]
    public async Task Release_ANewLeaseIsRecordedDuringTheRoundTrip_DoesNotClobberTheFreshBelief()
    {
        // Same race as LeaseRegistry.ReconcileAsync (see LeaseRegistryTests.cs's own tests for the
        // identical shape one layer down): this reads the believed lease, awaits the wire round
        // trip to release THAT lease specifically, and only then clears - but a concurrent 'begin'
        // can record a genuinely different, fresh lease while that round trip is still in flight.
        // Unconditionally clearing on the way back out would wipe the fresh belief too, even though
        // it has nothing to do with the stale lease this call actually asked the plugin to release.
        _leases.RecordHeld(ProjectGuid, "hades-script-editing", DateTimeOffset.UtcNow.AddSeconds(30));
        var (reads, writes) = await RegisterFakeEditorAsync();

        var releaseTask = EditorsEndpoint.ReleaseAsync(_projects, _leases, _editorProxy, ProjectGuid);

        // EditorProxy.SendCommandAsync's own two-step cadence: answer the busy probe first.
        var probeLine = await reads.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(JsonRpcRequest.TryParse(probeLine, out var probe, out var probeError), probeError);
        await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(probe!.Id!, JsonValue.Bool(true)).ToJson()));

        var realLine = await reads.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(JsonRpcRequest.TryParse(realLine, out var real, out var realError), realError);
        Assert.Equal("lease.release", real!.Method);

        // The race: a concurrent 'begin' acquires a brand-new, unrelated lease while this
        // release's own "lease.release" for the OLD lease is still in flight.
        _leases.RecordHeld(ProjectGuid, "fresh-lease-from-a-concurrent-begin", DateTimeOffset.UtcNow.AddSeconds(30));

        await writes.WriteLineAsync(MiniJson.Write(
            JsonRpcResponse.Success(real.Id!, LeaseResult(success: true)).ToJson()));

        await releaseTask.WaitAsync(TimeSpan.FromSeconds(5));

        var stillBelieved = _leases.Get(ProjectGuid);
        Assert.NotNull(stillBelieved);
        Assert.Equal("fresh-lease-from-a-concurrent-begin", stillBelieved!.LeaseId);
    }

    // ---------------------------------------------------------------- idempotent: release twice

    [Fact]
    public async Task ReleaseTwice_SecondCallStillSucceeds_WithoutAnyFurtherWireInteraction()
    {
        _leases.RecordHeld(ProjectGuid, "hades-script-editing", DateTimeOffset.UtcNow.AddSeconds(30));
        var (reads, writes) = await RegisterFakeEditorAsync();
        var responderTask = AnswerProbeThenRespondAsync(reads, writes, LeaseResult(success: true));

        var first = await EditorsEndpoint.ReleaseAsync(_projects, _leases, _editorProxy, ProjectGuid);
        await responderTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StatusCodes.Status200OK, await StatusCodeOf(first));
        Assert.True((await ResultBodyAsync(first)).GetProperty("success").GetBoolean());

        // Second call: LeaseRegistry no longer believes anything is held (cleared by the first
        // call above), so this must succeed WITHOUT touching the fake editor again - if it tried,
        // it would hang, since nothing is left to answer a second wire request.
        var second = await EditorsEndpoint.ReleaseAsync(_projects, _leases, _editorProxy, ProjectGuid);
        Assert.Equal(StatusCodes.Status200OK, await StatusCodeOf(second));
        Assert.True((await ResultBodyAsync(second)).GetProperty("success").GetBoolean());
    }

    // ---------------------------------------------------------------- edge case: a different lease now holds it

    [Fact]
    public async Task PluginReportsADifferentLeaseNowHeld_FailsInformatively_ClearsTheStaleBelief()
    {
        // Structurally shouldn't happen while ReloadGate only ever hands out one constant lease id
        // (see EditorsEndpoint's own class doc comment) - exercised anyway via a canned plugin
        // response, since this app must stay honest about ReloadGate.Release's own real contract
        // (false when a DIFFERENT lease currently holds the gate) rather than assuming it away.
        _leases.RecordHeld(ProjectGuid, "hades-script-editing", DateTimeOffset.UtcNow.AddSeconds(30));
        var (reads, writes) = await RegisterFakeEditorAsync();
        var responderTask = AnswerProbeThenRespondAsync(reads, writes,
            LeaseResult(success: false, leaseId: "some-other-lease", expiresAtUtcMs: DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds()));

        var response = await EditorsEndpoint.ReleaseAsync(_projects, _leases, _editorProxy, ProjectGuid);
        await responderTask.WaitAsync(TimeSpan.FromSeconds(5));
        var json = await ResultBodyAsync(response);

        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Null(_leases.Get(ProjectGuid));
    }

    // ---------------------------------------------------------------- helpers

    static DefaultHttpContext NewContext()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        return context;
    }

    static async Task<JsonElement> ResultBodyAsync(IResult result)
    {
        var context = NewContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();
    }

    static async Task<int> StatusCodeOf(IResult result)
    {
        var context = NewContext();
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    public void Dispose()
    {
        foreach (var d in _toDispose) d.Dispose();
        foreach (var l in _listeners) l.Stop();

        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

/// <summary>
/// <c>GET /control/editors</c> and <c>POST /control/leases/{id}/release</c> over real HTTP against
/// a directly-constructed <see cref="ControlListener"/> - same style as SummaryTests.cs/
/// ProjectsTests.cs's own HTTP test classes: proving auth/Origin/routing, not re-proving the
/// resolution/release logic already covered above.
/// </summary>
public sealed class EditorsEndpointHttpTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");

    static HttpRequestMessage Request(HttpMethod method, string path, string? token, string? origin = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    static HttpClient ClientFor(ControlListener listener) => new() { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };

    [Fact]
    public async Task GetEditors_NoToken_IsRefused()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/editors", token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetEditors_ForeignOrigin_IsRejectedWith403_EvenWithAValidToken()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/editors", listener.Token, origin: "https://evil.example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetEditors_ValidToken_NoProjects_ReturnsAnEmptyArray()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/editors", listener.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("editors").GetArrayLength());
    }

    [Fact]
    public async Task ReleaseAction_NoToken_IsRefused()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/control/leases/some-guid/release", token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReleaseAction_ForeignOrigin_IsRejectedWith403_EvenWithAValidToken()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/control/leases/some-guid/release", listener.Token, origin: "https://evil.example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReleaseAction_ValidToken_UnknownProject_Returns404()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/control/leases/not-a-known-guid/release", listener.Token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReleaseAction_ValidToken_NoLeaseHeld_SucceedsIdempotentlyOverRealHttp()
    {
        var projectRoot = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ProjectSettings"));
        const string guid = "aaaabbbbccccddddeeeeffff33300003";
        File.WriteAllText(Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {guid}\n");

        var projectService = new ProjectService(new AppPaths(Path.Combine(_tempDir, "app")));
        projectService.Adopt(projectRoot);

        using var listener = new ControlListener(ConnectionFilePath, projects: projectService);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/leases/{guid}/release", listener.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}

/// <summary>
/// Proves Program.cs's ControlListener registration threads the app's real, shared
/// <see cref="EditorProxy"/>/<see cref="EditorRegistry"/> into the new editors/release routes -
/// the same "sees the same singletons everything else uses" property SummaryProgramWiringTests/
/// ProjectsProgramWiringTests already prove for their own endpoints, and the ONE property that
/// direct-construction tests above cannot: that the release action actually reaches a REAL Editor
/// attached through the app's REAL EditorListener, not an isolated default EditorProxy that would
/// silently see nothing. Reuses EditorToolTestBase for its fake-Unity-over-a-real-socket dial-in
/// (ConnectAsFakeUnityAsync/AnswerOneAsync/AnswerBusyProbeThenRespondAsync) rather than duplicating
/// it a third time - that base class already exists for exactly this "drive the app's real
/// EditorListener" scenario.
/// </summary>
public sealed class EditorsProgramWiringTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
{
    static JsonValue LeaseResult(bool success) =>
        JsonValue.NewObject().SetProperty("success", JsonValue.Bool(success))
            .SetProperty("leaseId", JsonValue.Null).SetProperty("expiresAtUtcMs", JsonValue.Null);

    [Fact]
    public async Task ControlListener_EditorsEndpoint_SeesTheSameEditorRegistryEverythingElseUses()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        // GET /control/editors sends exactly ONE wire request (GetCharonStatus's own busy probe,
        // no follow-up command) - AnswerOneAsync, not the two-step AnswerBusyProbeThenRespondAsync
        // every Editor-dependent MCP TOOL test uses (those always send a probe THEN a real command).
        var responder = AnswerOneAsync(reads, writes, JsonValue.Bool(true));

        var listener = Factory.Services.GetRequiredService<ControlListener>();
        var port = await ProgramWiringPort.WaitAsync(listener);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var request = new HttpRequestMessage(HttpMethod.Get, "/control/editors");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", listener.Token);

        var response = await client.SendAsync(request);
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = Assert.Single(body.GetProperty("editors").EnumerateArray());
        Assert.Equal(ProjectGuid, row.GetProperty("productGuid").GetString());
        Assert.Equal("attached", row.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ControlListener_ReleaseAction_ReachesTheRealAttachedEditor_ThroughTheSharedEditorProxy()
    {
        // Connect FIRST, record the believed lease SECOND - the realistic order (a lease can only
        // ever be recorded via script_editing_session's 'begin', which itself needs an already-
        // attached Editor to send project.begin_script_editing to). Doing it the other way around
        // would make EditorListener.Register's own reconnect reconciliation (LeaseRegistry.ReconcileAsync -
        // fire-and-forget the moment a session registers with an already-believed-held lease) race
        // an extra lease.renew probe onto the wire ahead of this test's own expected [probe,
        // lease.release] sequence.
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        Factory.Services.GetRequiredService<LeaseRegistry>()
            .RecordHeld(ProjectGuid, "hades-script-editing", DateTimeOffset.UtcNow.AddSeconds(30));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, LeaseResult(success: true));

        var listener = Factory.Services.GetRequiredService<ControlListener>();
        var port = await ProgramWiringPort.WaitAsync(listener);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/control/leases/{ProjectGuid}/release");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", listener.Token);

        var response = await client.SendAsync(request);
        var wireRequest = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("lease.release", wireRequest.Method);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());

        Assert.Null(Factory.Services.GetRequiredService<LeaseRegistry>().Get(ProjectGuid));
    }
}

/// <summary>
/// Task 4's required regression guard: "no new Lock/Unlock call site was introduced anywhere".
/// Core can never call EditorApplication.Lock/UnlockReloadAssemblies at all (this assembly has no
/// UnityEditor reference), so the first test here is trivially, permanently true - it exists to
/// PIN that, not to catch a currently-real risk. The second test is UnityPlugin's own
/// ReloadGateCriticalSuite.SourceScan_NoCallSitesForTheNativeLockApi_OutsideIEditorLockApi,
/// re-run here against the SAME real UnityPlugin source tree (never modified - see this class's own
/// doc comment) - that suite only runs inside a live Unity Editor batchmode run, so it is NOT part
/// of the "1076 green" `dotnet test` count Task 4 must not regress; this is what makes "no new
/// Lock/Unlock call site" actually checkable from the ONE suite this task's tests run under.
/// </summary>
public sealed class EditorsSourceScanTests
{
    static readonly Regex RealCallSyntax = new(@"\b(Lock|Unlock)ReloadAssemblies\s*\(");

    [Fact]
    public void AppSourceTree_NeverCallsTheNativeLockApi()
    {
        var appSrcDir = FindAppSrcDirectory();

        var violations = Directory.GetFiles(appSrcDir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => RealCallSyntax.Matches(File.ReadAllText(file))
                .Select(m => $"{file}: '{m.Value.TrimEnd()}'"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void PluginSourceTree_StillHasExactlyTheTwoCallSitesInsideIEditorLockApi_NothingElse()
    {
        var toolsDir = PluginRequiredFields.FindPluginToolsDirectory() ?? throw new InvalidOperationException(
            "Could not locate UnityPlugin/Assets/Hades/Tools - see PluginRequiredFields' own doc comment. "
            + "Is UnityPlugin present alongside Core in this checkout?");
        var pluginRoot = Path.GetDirectoryName(toolsDir)!; // .../UnityPlugin/Assets/Hades

        var violationsOutsideAllowedFile = new List<string>();
        var callSitesInsideAllowedFile = 0;

        foreach (var file in Directory.GetFiles(pluginRoot, "*.cs", SearchOption.AllDirectories))
        {
            var matches = RealCallSyntax.Matches(File.ReadAllText(file));
            if (matches.Count == 0) continue;

            if (string.Equals(Path.GetFileName(file), "IEditorLockApi.cs", StringComparison.Ordinal))
            {
                callSitesInsideAllowedFile += matches.Count;
                continue;
            }

            violationsOutsideAllowedFile.Add($"{file}: {matches.Count} call site(s)");
        }

        Assert.Empty(violationsOutsideAllowedFile);
        Assert.Equal(2, callSitesInsideAllowedFile);
    }

    static string FindAppSrcDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Core", "src");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(dir.FullName, "UnityPlugin")))
                return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate Core/src by walking up from " + AppContext.BaseDirectory);
    }
}
