using System.Net;
using System.Runtime.ExceptionServices;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hades.Contract.Wire;
using Hades.Core;
using Hades.Core.Editors;
using Hades.Core.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Sdk;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Tests;

/// <summary>
/// Shared fixture for every class-1 editor-tool test (EditorSceneTools, EditorComponentTools):
/// the app's real EditorListener, attached to by a fake Unity Editor over a real loopback socket,
/// driven through the full MCP/HTTP path - the same wiring CharonStatusTests uses (see that
/// class's own doc comment), reused here rather than duplicated across two more test files.
///
/// Every one of these tools' own SendCommandAsync call first goes through
/// EditorProxy.GetCharonStatus's busy probe before the real command - see EditorProxyTests, which
/// already proves that dance in isolation. AnswerBusyProbeThenRespond plays both steps in the
/// order EditorProxy actually sends them, so each tool's own test only has to supply the ONE
/// canned result specific to its own command.
/// </summary>
public abstract class EditorToolTestBase : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    protected const string ProjectGuid = "aaaabbbbccccddddeeeeffff00001111";

    /// <summary>The most recently started fake-Editor responder, so <see cref="Structured"/> can
    /// report why it failed instead of the symptom it caused. Every test here reads
    /// <c>Structured(await CallTool(...))</c> BEFORE <c>await responder.WaitAsync(...)</c>, so a
    /// responder that throws loses its exception entirely: Structured fails first, the responder
    /// Task is never awaited, and its exception is discarded unobserved. What the test reports is
    /// then a tool error caused by the responder's failure - most often "busy - its main thread has
    /// not answered within the probe window", because a responder that threw never wrote a reply
    /// and the app really did wait out the whole probe window.</summary>
    Task? _responder;

    protected readonly WebApplicationFactory<Program> Factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly List<IDisposable> _toDispose = [];

    /// <param name="charonProbeTimeout">Overrides <see cref="ProjectService.CharonProbeTimeout"/>
    /// for classes that WANT the probe to time out. Everything else takes the default - see the
    /// assignment below for why it is so generous.</param>
    protected EditorToolTestBase(WebApplicationFactory<Program> factory, TimeSpan? charonProbeTimeout = null)
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {ProjectGuid}\n");

        Factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));

                services.RemoveAll<ProjectService>();
                services.AddSingleton(sp => new ProjectService(
                    sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<EditorRegistry>())
                {
                    // Deliberately far above production's 1.5s, and raised again from 5s. NO test
                    // built on this base class exercises the probe TIMING OUT - every one of them
                    // answers the probe through AnswerBusyProbeThenRespondAsync - so the only thing
                    // this budget can do here is turn scheduling latency into a spurious "busy".
                    //
                    // It did. Two tests failed on a Windows CI runner reporting "its main thread
                    // has not answered within the probe window"; the fake editor was fine, its
                    // responder just had not been scheduled yet. xUnit runs collections in
                    // parallel and the thread pool grows roughly one thread at a time, so on a
                    // 2-core runner a burst of parallel hosts can queue a reply for seconds. The
                    // earlier 300ms -> 5s move fixed the same failure for the same reason; 5s was
                    // simply still inside what a loaded runner can miss.
                    //
                    // Costs nothing when things work - a localhost round trip answers in
                    // milliseconds - and a fake editor that genuinely never answers still fails,
                    // via the test's own 30s responder guard.
                    // Deliberately far above production's 1.5s, and raised again from 5s. Almost
                    // no test built on this base class exercises the probe TIMING OUT - they answer
                    // it through AnswerBusyProbeThenRespondAsync - so for those the only thing this
                    // budget can do is turn scheduling latency into a spurious "busy".
                    //
                    // It did. Two tests failed on a Windows CI runner reporting "its main thread
                    // has not answered within the probe window"; the fake editor was fine, its
                    // responder just had not been scheduled yet. xUnit runs collections in parallel
                    // and the thread pool grows roughly one thread at a time, so on a 2-core runner
                    // a burst of parallel hosts can queue a reply for seconds. The earlier
                    // 300ms -> 5s move fixed the same failure for the same reason; 5s was simply
                    // still inside what a loaded runner can miss.
                    //
                    // Costs nothing when things work - a localhost round trip answers in
                    // milliseconds - and a fake editor that genuinely never answers still fails,
                    // via the test's own 30s responder guard. The exception is a class that never
                    // answers the probe ON PURPOSE, which would now wait the full 30s; those pass
                    // their own short value to this constructor.
                    CharonProbeTimeout = charonProbeTimeout ?? TimeSpan.FromSeconds(30),
                });
            }));

        Factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(_projectRoot);
    }

    /// <summary>
    /// The structured payload of a successful tool call - and, when the call was NOT successful, a
    /// failure that says so.
    ///
    /// <para>This used to be a bare
    /// <c>envelope.GetProperty("result").GetProperty("structuredContent")</c>, which turned every
    /// failed tool call in every test using it into <c>KeyNotFoundException: The given key was not
    /// present in the dictionary</c> - naming neither the missing key nor the error the tool
    /// actually returned. Two Windows CI failures reported exactly that and nothing else, which is
    /// what prompted this: an error envelope carries <c>isError</c> and a human-readable
    /// <c>content</c> block, and throwing it away is throwing away the entire diagnosis.</para>
    /// </summary>
    protected JsonElement Structured(JsonElement envelope)
    {
        // The responder's failure IS the diagnosis; whatever the tool returned is just its
        // consequence. Rethrown with its original stack, so the failure points at the line in the
        // fake Editor that actually broke rather than at this method. See _responder.
        if (_responder is { IsFaulted: true, Exception: { } fault })
            ExceptionDispatchInfo.Capture(fault.InnerExceptions.Count == 1 ? fault.InnerExceptions[0] : fault).Throw();

        if (!envelope.TryGetProperty("result", out var result))
            throw new XunitException($"Tool call returned no 'result'. Envelope:\n{Pretty(envelope)}");

        if (!result.TryGetProperty("structuredContent", out var structured))
            throw new XunitException(
                "Tool call returned a 'result' with no 'structuredContent', which is what an error " +
                $"response looks like. Envelope:\n{Pretty(envelope)}");

        return structured;
    }

    static string Pretty(JsonElement element) =>
        JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });

    static Hello MakeHello(long processId) => new()
    {
        ProjectGuid = ProjectGuid,
        ProjectPath = "/tmp/fake-unity-project",
        UnityVersion = "6000.3.2f1",
        PluginVersion = "1.2.0",
        ProcessId = processId,
    };

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

    /// <summary>Dials into the app's real EditorListener and completes the token+hello handshake,
    /// exactly as CharonStatusTests.ConnectAsFakeUnityAsync does - see that method's own doc
    /// comment for the details this mirrors.</summary>
    protected async Task<(StreamReader Reads, StreamWriter Writes)> ConnectAsFakeUnityAsync(long processId = 9101)
    {
        var paths = Factory.Services.GetRequiredService<AppPaths>();
        var registry = Factory.Services.GetRequiredService<EditorRegistry>();

        Assert.True(await Eventually(() => File.Exists(paths.EditorTokenFile)),
            "EditorListener never wrote its connection file - is it started in Program.cs?");
        Assert.True(EditorConnectionInfo.TryParse(File.ReadAllText(paths.EditorTokenFile), out var info, out var error), error);

        var client = new TcpClient();
        _toDispose.Add(client);
        await client.ConnectAsync(IPAddress.Loopback, info!.Port);

        var stream = client.GetStream();
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(stream, new UTF8Encoding(false));

        var hello = MakeHello(processId);
        await writer.WriteLineAsync(info.Token);
        await writer.WriteLineAsync(MiniJson.Write(hello.ToJson()));

        Assert.True(await Eventually(() => registry.Get(ProjectGuid)?.Hello.ProcessId == hello.ProcessId),
            "the fake Unity Editor never registered");

        return (reader, writer);
    }

    /// <summary>Answers exactly one pending request with a plain JSON-RPC success (used for the
    /// busy probe GetCharonStatus sends before EditorProxy ever touches the real command).
    ///
    /// Also enforces <see cref="PluginWireContract"/> against whatever was actually sent - see that
    /// class's own doc comment. This is the systemic fix for the defect class where an app tool
    /// builds a wire payload the plugin's JsonParams.RequireString/RequireInt would reject: because
    /// EVERY *_apply/*_manage test in this project answers its wire call through this method (or
    /// the two below, which both delegate the real command's response through the same check),
    /// enforcement applies to every test already written with no per-test change, and fails loudly,
    /// pointing at the exact missing field, the moment a tool's BuildOperation stops matching the
    /// plugin's actual requirement - in either direction, since PluginRequiredFields reads the
    /// plugin's requirement live from source rather than from a hand-copied snapshot.
    ///
    /// <para><b>The response is always written before a violation is thrown.</b> A contract
    /// violation must not stop this method from writing its JSON-RPC response back onto the socket:
    /// the app's own EditorProxy.SendCommandAsync is, at this moment, still awaiting exactly that
    /// response, with its own real 30s timeout - if this method threw BEFORE writing (the first cut
    /// of this mechanism did exactly that), the test would not fail fast with an actionable message,
    /// it would hang for 30 real seconds and then fail with an unrelated-looking McpException/
    /// KeyNotFoundException from deep inside the HTTP call, nowhere near this check. Writing first,
    /// then throwing, lets the awaited HTTP call complete normally and the violation surface at the
    /// test's own <c>responder.WaitAsync(...)</c> - fast, and pointing at the exact field.</para></summary>
    protected Task AnswerOneAsync(StreamReader reads, StreamWriter writes, JsonValue? result = null) =>
        _responder = AnswerOneCoreAsync(reads, writes, result);

    static async Task AnswerOneCoreAsync(StreamReader reads, StreamWriter writes, JsonValue? result = null)
    {
        var line = await reads.ReadLineAsync();
        // Names what actually arrived. Unlike the contract violation below, there is no reply this
        // can still write - without a request id there is nothing to correlate one to - so the app
        // WILL wait out its full probe window and report the Editor busy. Saying so here is the
        // difference between diagnosing this and re-deriving it.
        if (!JsonRpcRequest.TryParse(line, out var request, out var parseError))
            throw new XunitException(
                $"The fake Editor could not parse what the app sent it, so it answered nothing and " +
                $"the app will report the Editor busy. Parse error: {parseError}. Line: {line ?? "<null - the stream closed>"}");
        var violation = PluginWireContractViolation(request!);
        await writes.WriteLineAsync(MiniJson.Write(
            JsonRpcResponse.Success(request!.Id!, result ?? JsonValue.Bool(true)).ToJson()));
        if (violation != null) throw violation;
    }

    /// <summary>Plays the busy probe (plain success) followed by the real command, answered with
    /// <paramref name="result"/>. Returns the parsed real request so a test can assert the method
    /// name and params the tool actually sent, alongside the mapped result it gets back.</summary>
    protected Task<JsonRpcRequest> AnswerBusyProbeThenRespondAsync(StreamReader reads, StreamWriter writes, JsonValue result)
    {
        var task = AnswerBusyProbeThenRespondCoreAsync(reads, writes, result);
        _responder = task;
        return task;
    }

    static async Task<JsonRpcRequest> AnswerBusyProbeThenRespondCoreAsync(StreamReader reads, StreamWriter writes, JsonValue result)
    {
        await AnswerOneCoreAsync(reads, writes); // the busy probe

        var line = await reads.ReadLineAsync();
        Assert.True(JsonRpcRequest.TryParse(line, out var request, out var error), error);
        var violation = PluginWireContractViolation(request!);
        await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request!.Id!, result).ToJson()));
        if (violation != null) throw violation;
        return request;
    }

    /// <summary>Same as <see cref="AnswerBusyProbeThenRespondAsync"/>, but the real command comes
    /// back as a JSON-RPC error - exactly how a plugin-side exception's Message reaches the wire
    /// (see HadesClient.DescribeFailure on the plugin side).</summary>
    protected Task<JsonRpcRequest> AnswerBusyProbeThenFailAsync(StreamReader reads, StreamWriter writes, string message)
    {
        var task = AnswerBusyProbeThenFailCoreAsync(reads, writes, message);
        _responder = task;
        return task;
    }

    static async Task<JsonRpcRequest> AnswerBusyProbeThenFailCoreAsync(StreamReader reads, StreamWriter writes, string message)
    {
        await AnswerOneCoreAsync(reads, writes); // the busy probe

        var line = await reads.ReadLineAsync();
        Assert.True(JsonRpcRequest.TryParse(line, out var request, out var error), error);
        var violation = PluginWireContractViolation(request!);
        await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Failure(request!.Id!, -32603, message).ToJson()));
        if (violation != null) throw violation;
        return request;
    }

    /// <summary>Walks 'operations' (present only on the 7 consolidated batch wire calls -
    /// PluginWireContract.AssertOperationSatisfiesPluginContract itself no-ops for any other wire
    /// method, e.g. the busy probe's own hades.charon_status) and checks every operation object
    /// against the plugin's actual field requirements, returning the FIRST violation found rather
    /// than throwing directly - see AnswerOneAsync's own doc comment for why the caller must write
    /// its response before (maybe) throwing this. A malformed/absent 'operations' array is not this
    /// method's concern - SceneApplyTool/etc. already reject that locally before any wire call, and
    /// the plugin's own "requires an 'operations' array" error is exercised directly by each tool's
    /// own *_PluginLevelError_PropagatesAsToolError test.</summary>
    static Exception? PluginWireContractViolation(JsonRpcRequest request)
    {
        if (request.Params is not { Kind: WireKind.Object } @params) return null;
        if (!@params.TryGetProperty("operations", out var ops) || ops is not { Kind: WireKind.Array }) return null;

        foreach (var opJson in ops.Items)
        {
            if (opJson is null) continue;
            if (!opJson.TryGetProperty("op", out var opNameJson) || opNameJson is not { Kind: WireKind.String }) continue;

            try
            {
                PluginWireContract.AssertOperationSatisfiesPluginContract(request.Method, opNameJson.AsString(), opJson);
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var disposable in _toDispose) disposable.Dispose();

        // Factory is a fresh derived WebApplicationFactory built per test (see the constructor's
        // own WithWebHostBuilder call) and was never disposed until now. Left running, its own
        // real background services - EditorListener's live accept loop and ControlListener's,
        // both started unconditionally during Program.cs's own startup, plus ObservationService's
        // periodic sweep - keep touching _appRoot/_projectRoot after the test body returns, since
        // nothing here ever stopped them. Disposing the host BEFORE deleting the directories below
        // is what actually stops that traffic; deleting first (the previous order) left it free to
        // still be mid-write under either directory when the recursive delete ran, racing it
        // (IOException: "Directory not empty" - a different test each time, depending on which
        // leaked host's background work and which test's own teardown happened to collide).
        Factory.Dispose();

        TeardownDiagnostics.Delete(_appRoot, _projectRoot);
    }
}
