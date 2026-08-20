using System.Net;
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

namespace Hades.Server.Tests;

/// <summary>
/// hades_charon_status' lease-visibility fields, end-to-end over HTTP - see the release-paths/
/// visibility plan: a held reload lock must never be silent. Same fake-Unity-plugin-over-a-real-
/// loopback-socket infrastructure as CharonStatusTests (this file does not extend that one, to
/// keep each file's fixture setup self-contained - same convention CharonStatusTests itself
/// follows relative to EditorListenerTests).
/// </summary>
public sealed class LeaseVisibilityTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff00001111";

    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly List<IDisposable> _toDispose = [];

    // Mutable so individual tests can backdate LeaseRegistry's notion of "now" for the instant a
    // lease is recorded, then let it move back to real time - lets "how long has it been held"
    // be asserted deterministically without a real sleep.
    DateTimeOffset _leaseClock = DateTimeOffset.UtcNow;

    public LeaseVisibilityTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {ProjectGuid}\n");

        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));

                services.RemoveAll<LeaseRegistry>();
                services.AddSingleton(new LeaseRegistry(() => _leaseClock));

                // Rebuilt from the SAME EditorRegistry singleton Program.cs hands to the real
                // EditorListener - same reason and same shape as CharonStatusTests' override -
                // so the "attached" tests below do not pay the real 1.5s default probe timeout.
                services.RemoveAll<ProjectService>();
                services.AddSingleton(sp => new ProjectService(
                    sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<EditorRegistry>())
                {
                    CharonProbeTimeout = TimeSpan.FromMilliseconds(300),
                });
            }));

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(_projectRoot);
    }

    static JsonElement Structured(JsonElement envelope) =>
        envelope.GetProperty("result").GetProperty("structuredContent");

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

    /// <summary>Dials into the app's REAL EditorListener and completes the token + hello
    /// handshake, exactly as CharonStatusTests' own helper does - see that file for the full
    /// rationale.</summary>
    async Task<(StreamReader Reads, StreamWriter Writes)> ConnectAsFakeUnityAsync(Hello hello)
    {
        var paths = _factory.Services.GetRequiredService<AppPaths>();
        var registry = _factory.Services.GetRequiredService<EditorRegistry>();

        Assert.True(await Eventually(() => File.Exists(paths.EditorTokenFile)),
            "EditorListener never wrote its connection file - is it started in Program.cs?");

        Assert.True(EditorConnectionInfo.TryParse(File.ReadAllText(paths.EditorTokenFile), out var info, out var error), error);

        var client = new TcpClient();
        _toDispose.Add(client);
        await client.ConnectAsync(IPAddress.Loopback, info!.Port);

        var stream = client.GetStream();
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(stream, new UTF8Encoding(false));

        await writer.WriteLineAsync(info.Token);
        await writer.WriteLineAsync(MiniJson.Write(hello.ToJson()));

        Assert.True(await Eventually(() => registry.Get(ProjectGuid)?.Hello.ProcessId == hello.ProcessId),
            "the fake Unity Editor never registered");

        return (reader, writer);
    }

    /// <summary>Answers whatever the next probe request is with a plain success, so the Editor
    /// reads as attached-and-not-busy - same shape as CharonStatusTests' own responder.</summary>
    static Task RespondToNextProbeAsync(StreamReader reads, StreamWriter writes) => Task.Run(async () =>
    {
        var line = await reads.ReadLineAsync();
        if (line is not null && JsonRpcRequest.TryParse(line, out var request, out _) && request is not null)
        {
            await writes.WriteLineAsync(MiniJson.Write(
                JsonRpcResponse.Success(request.Id!, JsonValue.Bool(true)).ToJson()));
        }
    });

    // ---------------------------------------------------------------- nothing held

    [Fact]
    public async Task NoLeaseHeld_ReportsLeaseHeldFalseAndNoLeaseFields()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync(MakeHello(processId: 9101));
        var responder = RespondToNextProbeAsync(reads, writes);

        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_charon_status"));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(structured.GetProperty("attached").GetBoolean());
        Assert.False(structured.GetProperty("leaseHeld").GetBoolean());

        // Absent, not null-valued - same WhenWritingNull convention CharonStatusResult already
        // uses for unityVersion/projectPath/etc. when Attached is false (see that record's own
        // doc comment).
        Assert.False(structured.TryGetProperty("leaseId", out _));
        Assert.False(structured.TryGetProperty("leaseHeldForSeconds", out _));
        Assert.False(structured.TryGetProperty("leaseExpiresAtUtc", out _));
    }

    // ---------------------------------------------------------------- held while attached

    [Fact]
    public async Task LeaseHeldWhileAttached_ReportsIdHeldForSecondsAndExpiresAt()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync(MakeHello(processId: 9102));
        var responder = RespondToNextProbeAsync(reads, writes);

        var leases = _factory.Services.GetRequiredService<LeaseRegistry>();
        _leaseClock = DateTimeOffset.UtcNow.AddSeconds(-12); // backdate "now" for this one recording
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(18);
        leases.RecordHeld(ProjectGuid, "lease-xyz", expiresAt);

        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_charon_status"));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(structured.GetProperty("attached").GetBoolean());
        Assert.True(structured.GetProperty("leaseHeld").GetBoolean());
        Assert.Equal("lease-xyz", structured.GetProperty("leaseId").GetString());

        // ~12s, generous window either side for test-runner scheduling jitter.
        Assert.InRange(structured.GetProperty("leaseHeldForSeconds").GetDouble(), 8, 25);
        Assert.Equal(expiresAt, structured.GetProperty("leaseExpiresAtUtc").GetDateTimeOffset());

        var detail = structured.GetProperty("detail").GetString()!;
        Assert.Contains("reload", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lease-xyz", detail);
    }

    // ---------------------------------------------------------------- TTL self-expiry (mutation-tool-defects.md #2)

    /// <summary>The exact live repro from mutation-tool-defects.md #2: ttlSeconds=5, no 'end' call,
    /// wait past expiry - the plugin's own TTL watchdog genuinely released Unity's real reload lock
    /// (proven separately, live, by an unrelated lease-requiring call succeeding - see this defect's
    /// UnityPlugin half), but with nothing pushing that fact back to the app and no reconnect to trigger
    /// ReconcileAsync, hades_charon_status kept reporting leaseHeld=true / leaseHeldForSeconds
    /// climbing / a negative "expires in" indefinitely. LeaseRegistry.Get's own TTL self-expiry (see
    /// its class doc comment) is what closes this without needing a plugin-to-app push: this reads
    /// leaseHeld=false the moment the BELIEVED lease's own recorded expiry has passed, Editor still
    /// attached and connected the whole time, no reconnect and no lease.release involved at all.</summary>
    [Fact]
    public async Task LeaseHeldWhileAttached_ButItsOwnTtlHasAlreadyPassed_ReportsLeaseHeldFalse()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync(MakeHello(processId: 9103));
        var responder = RespondToNextProbeAsync(reads, writes);

        var leases = _factory.Services.GetRequiredService<LeaseRegistry>();
        leases.RecordHeld(ProjectGuid, "lease-ttl-fired", DateTimeOffset.UtcNow.AddSeconds(-215)); // already expired

        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_charon_status"));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(structured.GetProperty("attached").GetBoolean()); // the connection itself never dropped
        Assert.False(structured.GetProperty("leaseHeld").GetBoolean());
        Assert.False(structured.TryGetProperty("leaseId", out _));
        Assert.False(structured.TryGetProperty("leaseHeldForSeconds", out _));
        Assert.False(structured.TryGetProperty("leaseExpiresAtUtc", out _));
        Assert.DoesNotContain("lease-ttl-fired", structured.GetProperty("detail").GetString()!);

        // The self-expiry is not merely hidden from the read side - it evicted the stale entry, so
        // a second, independent read (e.g. the /control/summary endpoint's own LeaseRegistry.Get)
        // sees exactly the same "nothing believed held" truth, not a lingering entry only this one
        // call happened to filter out.
        Assert.Null(leases.Get(ProjectGuid));
    }

    // ---------------------------------------------------------------- held while NOT attached

    [Fact]
    public async Task LeaseBelievedHeld_ButEditorNotCurrentlyAttached_StillReportsIt()
    {
        // The exact case the plan calls out: a held lock must never be silent, and "not attached"
        // (the Editor process crashed, or has simply not reconnected yet) is precisely when a
        // stale belief matters most - it must not be hidden just because there is nobody to ask
        // right now. No fake Unity connects in this test at all.
        var leases = _factory.Services.GetRequiredService<LeaseRegistry>();
        _leaseClock = DateTimeOffset.UtcNow.AddSeconds(-5);
        leases.RecordHeld(ProjectGuid, "lease-stale", DateTimeOffset.UtcNow.AddSeconds(25));

        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_charon_status"));

        Assert.False(structured.GetProperty("attached").GetBoolean());
        Assert.True(structured.GetProperty("leaseHeld").GetBoolean());
        Assert.Equal("lease-stale", structured.GetProperty("leaseId").GetString());
        Assert.Contains("lease-stale", structured.GetProperty("detail").GetString()!);
    }

    // ---------------------------------------------------------------- BeginScriptEditing wiring, end to end

    /// <summary>Answers exactly one pending request with a plain success (the busy probe every
    /// SendCommandAsync call makes first), then the NEXT pending request with <paramref name="result"/>
    /// - same two-step shape as EditorToolTestBase.AnswerBusyProbeThenRespondAsync, duplicated
    /// here (rather than shared) because this file's fixture is deliberately self-contained - see
    /// this class's own doc comment.</summary>
    static async Task AnswerProbeThenRespondAsync(StreamReader reads, StreamWriter writes, JsonValue result)
    {
        var probeLine = await reads.ReadLineAsync();
        Assert.True(JsonRpcRequest.TryParse(probeLine, out var probeRequest, out var probeError), probeError);
        await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(probeRequest!.Id!, JsonValue.Bool(true)).ToJson()));

        var line = await reads.ReadLineAsync();
        Assert.True(JsonRpcRequest.TryParse(line, out var request, out var error), error);
        Assert.Equal("project.begin_script_editing", request!.Method);
        await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request.Id!, result).ToJson()));
    }

    // ---------------------------------------------------------------- script_editing_session wiring, end to end (Plan 10 Task 5)

    /// <summary>The exact defect Task 7 found live: leaseHeld read false for the entire life of a
    /// genuinely-held script-editing lease, because nothing ever called LeaseRegistry.RecordHeld
    /// from the tool that begins a session (see EditorProjectTools' own doc comment). Unlike
    /// LeaseHeldWhileAttached_... above (which seeds LeaseRegistry directly - the READ side only),
    /// this drives the real tool end to end, then checks hades_charon_status sees it - the same
    /// two-call sequence Task 7 ran against a real Editor. Originally proven against the standalone
    /// BeginScriptEditing tool; re-run here through script_editing_session (action='begin'), its
    /// Plan 10 Task 6 replacement, since BeginScriptEditing itself is gone - re-proving the fifth of
    /// the five lease properties this consolidation must not regress: script_editing_session's
    /// 'begin' must be a real caller of LeaseRegistry.RecordHeld, not just a proxy that happens to
    /// return the right JSON.</summary>
    [Fact]
    public async Task ScriptEditingSessionBegin_ThenCharonStatus_ReportsLeaseHeldTrue()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync(MakeHello(processId: 9105));

        var beginResponder = AnswerProbeThenRespondAsync(reads, writes, JsonValue.NewObject()
            .SetProperty("leaseId", JsonValue.String("hades-script-editing"))
            .SetProperty("expiresAtUtcMs", JsonValue.Integer(DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds())));

        await McpTestClient.CallTool(_factory, "script_editing_session", new { action = "begin" });
        await beginResponder.WaitAsync(TimeSpan.FromSeconds(5));

        var statusResponder = RespondToNextProbeAsync(reads, writes);
        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_charon_status"));
        await statusResponder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(structured.GetProperty("leaseHeld").GetBoolean());
        Assert.Equal("hades-script-editing", structured.GetProperty("leaseId").GetString());
    }

    // ---------------------------------------------------------------- tool count

    [Fact]
    public async Task ToolCount_Stays32()
    {
        // Plan 10 Task 6's hard cutover landed: 103 tools (90 pre-Plan-10, +13 new consolidated
        // tools Tasks 1-5 added alongside every granular tool they would eventually replace) minus
        // the 71 granular tools the capability audit proved reachable through those replacements =
        // 32, the plan's own "90 -> 32" target - see CharonStatusTests.ToolCount_Stays32's own
        // comment for the fuller breakdown. This plan (8) itself adds no tools - only changes
        // hades_charon_status' own behaviour - so a drift here beyond 32 now means a tool was added
        // or removed as a side effect, not on purpose.
        var tools = (await McpTestClient.ListTools(_factory)).GetProperty("result").GetProperty("tools");

        Assert.Equal(32, tools.GetArrayLength());
    }

    public void Dispose()
    {
        foreach (var disposable in _toDispose) disposable.Dispose();

        // See EditorToolTestBase.Dispose's own comment: _factory is a fresh per-test
        // WebApplicationFactory whose own background services can still be touching
        // _appRoot/_projectRoot until the host itself is disposed - which must happen before
        // the recursive delete below.
        _factory.Dispose();

        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
