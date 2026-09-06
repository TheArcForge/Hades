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
/// hades_charon_status end-to-end, over HTTP, covering all three states: not attached, attached
/// (main thread responsive), and busy (attached, main thread not draining). The attached/busy
/// cases play a fake Unity Editor plugin over a real loopback socket against the app's actual
/// EditorListener — started for real by Program.cs, exactly as it is for a live server — using
/// the same token+hello handshake EditorListenerTests plays, but driven from outside the process
/// through the full MCP/HTTP path rather than calling EditorRegistry directly.
/// </summary>
public sealed class CharonStatusTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff00001111";

    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly List<IDisposable> _toDispose = [];

    public CharonStatusTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {ProjectGuid}\n");

        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));

                // The busy case waits out a real probe timeout against a peer that deliberately
                // never answers - shrinking it keeps that test fast without weakening what it
                // proves. Rebuilt from the SAME EditorRegistry singleton Program.cs hands to the
                // real EditorListener, so this replacement observes exactly the editors that
                // listener registers.
                services.RemoveAll<ProjectService>();
                services.AddSingleton(sp => new ProjectService(
                    sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<EditorRegistry>())
                {
                    CharonProbeTimeout = TimeSpan.FromSeconds(5),
                });
            }));

        _factory.Services.GetRequiredService<ProjectService>().AdoptAndIndex(_projectRoot);
    }

    static JsonElement Structured(JsonElement envelope) =>
        envelope.GetProperty("result").GetProperty("structuredContent");

    const string RealAppPluginVersion = "1.4.0"; // UnityPlugin/Assets/Hades/Runtime/HadesBoot.cs's own PluginVersion constant - see ProjectsTests.cs's own identically-named constant for the same reasoning.

    static Hello MakeHello(long processId, string pluginVersion = RealAppPluginVersion) => new()
    {
        ProjectGuid = ProjectGuid,
        ProjectPath = "/tmp/fake-unity-project",
        UnityVersion = "6000.3.2f1",
        PluginVersion = pluginVersion,
        ProcessId = processId,
    };

    /// <summary>Polls rather than sleeping a fixed period - same shape as EditorListenerTests'
    /// own Eventually.</summary>
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

    /// <summary>
    /// Dials into the app's REAL EditorListener (started by Program.cs, same as a live server)
    /// and completes the token + hello handshake exactly as the real plugin does, reading the
    /// port and token the listener actually wrote to <see cref="AppPaths.EditorTokenFile"/>.
    /// Blocks until the editor is visibly registered before returning, so the caller never races
    /// EditorListener's own async accept/handshake handling. Leaves the connection open and
    /// registered for the caller to drive; the underlying socket is tracked for teardown.
    /// </summary>
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

    // ---------------------------------------------------------------- not attached

    [Fact]
    public async Task NoEditorAttached_ReportsAttachedFalseAndNotBusy()
    {
        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_charon_status"));

        Assert.False(structured.GetProperty("attached").GetBoolean());
        Assert.False(structured.GetProperty("busy").GetBoolean());
        var detail = structured.GetProperty("detail").GetString()!;
        Assert.Contains("Editor", detail);
    }

    [Fact]
    public async Task NoEditorAttached_ButPluginPresentOnDisk_SurfacesTheDiskVersionAsADistinctState()
    {
        // Medium finding: hades_charon_status could not distinguish "plugin installed but Unity
        // has not (re)connected yet" from "nothing installed at all" — both collapsed to the same
        // Attached:false answer with every plugin field null, because PluginVersion is
        // hello-derived and there is no hello without a connection. Simulate a plugin that was
        // written to disk (e.g. by installPlugin) but whose Editor has not attached in THIS
        // process — a real, on-disk HadesBoot.cs with its own PluginVersion constant, exactly what
        // PluginInstaller.Install itself would have produced.
        var hadesRuntimeDir = Path.Combine(_projectRoot, "Assets", "Hades", "Runtime");
        Directory.CreateDirectory(hadesRuntimeDir);
        File.WriteAllText(Path.Combine(hadesRuntimeDir, "HadesBoot.cs"),
            "public static class HadesBoot { public const string PluginVersion = \"1.4.0\"; }");

        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_charon_status"));

        Assert.False(structured.GetProperty("attached").GetBoolean());
        Assert.Equal("1.4.0", structured.GetProperty("pluginVersionOnDisk").GetString());

        var detail = structured.GetProperty("detail").GetString()!;
        Assert.Contains("1.4.0", detail);
        Assert.Contains("disk", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoEditorAttached_AndNoPluginOnDisk_ReportsNoPluginVersionOnDiskAtAll()
    {
        // The other half of the same distinction: truly nothing installed must NOT claim a disk
        // version, and the original "not attached" wording (no plugin-specific remedy implied)
        // stays exactly as it was — this is the control for the test above.
        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_charon_status"));

        Assert.False(structured.GetProperty("attached").GetBoolean());
        Assert.False(structured.TryGetProperty("pluginVersionOnDisk", out _));

        var detail = structured.GetProperty("detail").GetString()!;
        Assert.DoesNotContain("present on disk", detail);
    }

    [Fact]
    public async Task DescriptionExplainsWhyEditorToolsAreMissing()
    {
        // This tool's description is the thing that tells an agent WHY ~50 editor-action tools it
        // might expect (from Hades' Unity-plugin package) are simply absent here — not broken,
        // not hidden, not yet implemented in this standalone server.
        var tool = Assert.Single((await McpTestClient.ListTools(_factory))
            .GetProperty("result").GetProperty("tools").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "hades_charon_status");

        Assert.Contains("Editor", tool.GetProperty("description").GetString());
    }

    // ---------------------------------------------------------------- attached, main thread responsive

    [Fact]
    public async Task EditorAttachedAndResponsive_ReportsFullDetailsAndNotBusy()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync(MakeHello(processId: 9001));

        // Answers whatever probe request arrives immediately - a healthy, responsive main thread
        // (see MainThreadPump's class doc comment: everything but "keepalive" is dispatched
        // through the pump, so a prompt answer here is what "not busy" means on the wire).
        var responder = Task.Run(async () =>
        {
            var line = await reads.ReadLineAsync();
            if (line is not null && JsonRpcRequest.TryParse(line, out var request, out _) && request is not null)
            {
                await writes.WriteLineAsync(MiniJson.Write(
                    JsonRpcResponse.Success(request.Id!, JsonValue.Bool(true)).ToJson()));
            }
        });

        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_charon_status"));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(structured.GetProperty("attached").GetBoolean());
        Assert.False(structured.GetProperty("busy").GetBoolean());
        Assert.Equal("6000.3.2f1", structured.GetProperty("unityVersion").GetString());
        Assert.Equal("/tmp/fake-unity-project", structured.GetProperty("projectPath").GetString());
        Assert.Equal(9001, structured.GetProperty("processId").GetInt64());
        Assert.True(structured.GetProperty("connectionAgeSeconds").GetDouble() >= 0);

        Assert.Contains("6000.3.2f1", structured.GetProperty("detail").GetString());

        // Plugin version matches this app - reported (hello-derived, same as unityVersion above),
        // but no mismatch wording anywhere in detail.
        Assert.Equal(RealAppPluginVersion, structured.GetProperty("pluginVersion").GetString());
        Assert.DoesNotContain("does not match", structured.GetProperty("detail").GetString());
    }

    // ---------------------------------------------------------------- plugin version skew (spec #4 §6 - reported live, on connect)

    [Fact]
    public async Task EditorAttachedWithAnOlderPluginVersion_ReportsItLive_AndNamesTheGapInDetail()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync(MakeHello(processId: 9003, pluginVersion: "1.1.0"));
        var responder = Task.Run(async () =>
        {
            var line = await reads.ReadLineAsync();
            if (line is not null && JsonRpcRequest.TryParse(line, out var request, out _) && request is not null)
            {
                await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request.Id!, JsonValue.Bool(true)).ToJson()));
            }
        });

        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_charon_status"));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        // Still attached, not refused - degrade, never refuse (spec #4 §6). The connection and
        // every other Charon fact are exactly as healthy as the matching-version case above.
        Assert.True(structured.GetProperty("attached").GetBoolean());
        Assert.Equal("1.1.0", structured.GetProperty("pluginVersion").GetString());

        var detail = structured.GetProperty("detail").GetString()!;
        Assert.Contains("v1.1.0", detail);
        Assert.Contains($"v{RealAppPluginVersion}", detail);
        Assert.Contains("does not match", detail);
    }

    [Fact]
    public async Task EditorAttachedWithAMajorPluginVersionMismatch_DetailEscalatesTheWording_StillNotRefused()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync(MakeHello(processId: 9004, pluginVersion: "9.9.9"));
        var responder = Task.Run(async () =>
        {
            var line = await reads.ReadLineAsync();
            if (line is not null && JsonRpcRequest.TryParse(line, out var request, out _) && request is not null)
            {
                await writes.WriteLineAsync(MiniJson.Write(JsonRpcResponse.Success(request.Id!, JsonValue.Bool(true)).ToJson()));
            }
        });

        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_charon_status"));
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        // The refusal path is not taken even at major skew - see this file's own class doc comment
        // and EditorListenerTests' dedicated proof at the transport layer. Attached is still true.
        Assert.True(structured.GetProperty("attached").GetBoolean());
        Assert.Equal("9.9.9", structured.GetProperty("pluginVersion").GetString());

        var detail = structured.GetProperty("detail").GetString()!;
        Assert.Contains("major version", detail);
    }

    // ---------------------------------------------------------------- busy

    [Fact]
    public async Task EditorAttachedButMainThreadBlocked_ReportsBusyNotGone()
    {
        await ConnectAsFakeUnityAsync(MakeHello(processId: 9002));

        // Never answers - simulates a blocked main thread: the connection (and the probe request
        // itself) arrives fine, nothing ever drains it. Distinct from a dead connection, which is
        // exactly the property the background I/O thread exists to prove (see HadesClient's own
        // class doc comment and its keepalive-while-blocked test on the plugin side).

        var structured = Structured(await McpTestClient.CallTool(_factory, "hades_charon_status"));

        Assert.True(structured.GetProperty("attached").GetBoolean());
        Assert.True(structured.GetProperty("busy").GetBoolean());
        Assert.Equal("6000.3.2f1", structured.GetProperty("unityVersion").GetString());

        Assert.Contains("main thread", structured.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- tool count

    [Fact]
    public async Task ToolCount_Stays32()
    {
        // The tool-surface-consolidation plan's own target, reached: 90 pre-Plan-10 tools (38
        // pre-Plan-9, +31 Plan 9 Task 2 class-1, +15 Task 3 class-2, +3 Task 4 class-3, +3 Task 5
        // class-4) grew to 103 as Plan 10 Tasks 1-5 added 13 new consolidated tools (scene_apply,
        // prefab_apply/material_apply/animation_apply, inspect_asset/find_unset_references,
        // graph_query/project_settings/project_settings_apply, asset_manage/scene_manage/
        // script_editing_session/hades_regression) alongside every granular tool they would
        // eventually replace - deliberately, per the plan's hard-cutover rule: add the
        // replacements first, prove them, THEN delete the originals in one atomic pass, never an
        // interim deprecated-but-live surface. Plan 10 Task 6 is that pass: the capability audit
        // passed (every one of the 71 replaced tools' behaviour proven reachable through its
        // replacement - see the plan's own "Capability audit" section), so all 71 are gone -
        // 103 - 71 = 32, exactly the plan's own "90 -> 32" target.
        // This test (from plan 7) itself only changes hades_charon_status' own behaviour - a drift
        // here beyond 32 now means a tool was added or removed as a side effect, not on purpose.
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
