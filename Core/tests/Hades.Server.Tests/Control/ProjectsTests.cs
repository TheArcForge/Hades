using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Hades.Contract.Wire;
using Hades.Core;
using Hades.Core.Editors;
using Hades.Core.Projects;
using Hades.Core.Storage;
using Hades.Server.Control;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hades.Server.Tests.Control;

/// <summary>
/// Pure, deterministic tests of <see cref="ProjectsEndpoint.Resolve"/> - the warning/status/index
/// resolution logic behind <c>GET /control/projects</c>, exercised directly against hand-built
/// <see cref="ProjectStateSnapshot"/> inputs with a fixed clock, no I/O. Same "verbatim" discipline
/// as SummaryTests.cs's own SummaryResolveTests: every expected string below is a hand-typed
/// literal, never built by formatting a field pulled from the same response under test. See
/// <see cref="ProjectsBuildAsyncTests"/> for proof each warning actually triggers from REAL state
/// (a real fixture file on disk), not merely from a hand-built snapshot.
/// </summary>
public sealed class ProjectsResolveTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    static ProjectStateSnapshot Healthy(string name = "P") => new()
    {
        Name = name,
        Path = "/tmp/" + name,
        ProductGuid = "guid-" + name,
        UnityVersion = "6000.3.2f1",
        PathExists = true,
        Attached = false,
        Busy = false,
        LastIndexedUtc = Now.AddMinutes(-2),
        NodeCount = 10,
        EdgeCount = 4,
        SerializationMode = 2, // Force Text - fully supported, no warning
        InstalledPluginVersion = "1.2.0",
        AppPluginVersion = "1.2.0",
    };

    // ---------------------------------------------------------------- the healthy baseline

    [Fact]
    public void EverythingHealthy_ProducesNoWarnings()
    {
        var result = ProjectsEndpoint.Resolve([Healthy()], Now);

        Assert.Empty(Assert.Single(result.Projects).Warnings);
    }

    [Fact]
    public void HealthyRow_EveryFieldIsAnExactLiteral()
    {
        var result = ProjectsEndpoint.Resolve([Healthy("Hades-Unity-Client") with { Path = "/Users/mike/Projects/Hades-Unity-Client", ProductGuid = "abc123" }], Now);

        var row = Assert.Single(result.Projects);
        Assert.Equal("Hades-Unity-Client", row.Name);
        Assert.Equal("/Users/mike/Projects/Hades-Unity-Client", row.Path);
        Assert.Equal("abc123", row.ProductGuid);
        Assert.Equal("6000.3.2f1", row.UnityVersion);
        Assert.Equal(ProjectIndexState.Indexed, row.IndexState);
        Assert.Equal("indexed 2m ago", row.IndexStatus);
        Assert.Equal(10, row.NodeCount);
        Assert.Equal(4, row.EdgeCount);
        Assert.Equal(ProjectEditorState.Absent, row.Editor.State);
        Assert.Equal("No Editor attached", row.Editor.Status);
        Assert.Null(row.Editor.UnityVersion);
        Assert.Null(row.Editor.ProcessId);
        Assert.Null(row.Editor.ConnectionAgeSeconds);
    }

    // ---------------------------------------------------------------- warning: serialization mode

    [Fact]
    public void ForceBinarySerialization_ProducesAnErrorWarning_WithTheExactLiteralMessage()
    {
        var result = ProjectsEndpoint.Resolve([Healthy() with { SerializationMode = 1 }], Now);

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings);
        Assert.Equal("serializationMode", warning.Code);
        Assert.Equal(ControlSeverity.Error, warning.Severity);
        Assert.Equal(
            "Asset serialization is set to Force Binary. Hades reads Unity's YAML directly from disk, so scenes, prefabs, and other serialized assets cannot be scanned at all — the graph is silently incomplete.",
            warning.Message);
        Assert.Equal("In Unity: Edit → Project Settings → Editor → Asset Serialization → Mode → Force Text.", warning.Remedy);
    }

    [Fact]
    public void MixedSerialization_ProducesAWarningSeverityWarning_WithTheExactLiteralMessage()
    {
        var result = ProjectsEndpoint.Resolve([Healthy() with { SerializationMode = 0 }], Now);

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings);
        Assert.Equal("serializationMode", warning.Code);
        Assert.Equal(ControlSeverity.Warning, warning.Severity);
        Assert.Equal(
            "Asset serialization is set to Mixed. Hades reads Unity's YAML directly from disk, so any asset serialized as binary under this mode is invisible to the graph — the graph may be silently incomplete.",
            warning.Message);
        Assert.Equal("In Unity: Edit → Project Settings → Editor → Asset Serialization → Mode → Force Text.", warning.Remedy);
    }

    [Fact]
    public void ForceTextSerialization_ProducesNoSerializationWarning()
    {
        var result = ProjectsEndpoint.Resolve([Healthy() with { SerializationMode = 2 }], Now);

        Assert.DoesNotContain(Assert.Single(result.Projects).Warnings, w => w.Code == "serializationMode");
    }

    [Fact]
    public void UnknownSerializationMode_ProducesNoWarning_RatherThanGuessing()
    {
        var result = ProjectsEndpoint.Resolve([Healthy() with { SerializationMode = null }], Now);

        Assert.DoesNotContain(Assert.Single(result.Projects).Warnings, w => w.Code == "serializationMode");
    }

    // ---------------------------------------------------------------- warning: plugin version mismatch

    [Fact]
    public void PluginVersionMismatch_ProducesAWarning_WithTheExactLiteralMessage()
    {
        var result = ProjectsEndpoint.Resolve(
            [Healthy() with { InstalledPluginVersion = "1.0.0", AppPluginVersion = "1.2.0" }], Now);

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings);
        Assert.Equal("pluginVersionMismatch", warning.Code);
        Assert.Equal(ControlSeverity.Warning, warning.Severity);
        Assert.Equal(
            "The installed Hades plugin (v1.0.0) does not match this app (v1.2.0). Editor-dependent tools may not work correctly until it is updated.",
            warning.Message);
        Assert.Equal("Use Install/Update Plugin for this project, then restart Unity if it is already running.", warning.Remedy);
    }

    [Fact]
    public void PluginVersionMatches_ProducesNoWarning()
    {
        var result = ProjectsEndpoint.Resolve(
            [Healthy() with { InstalledPluginVersion = "1.2.0", AppPluginVersion = "1.2.0" }], Now);

        Assert.Empty(Assert.Single(result.Projects).Warnings);
    }

    [Fact]
    public void PluginNotInstalled_ProducesNoMismatchWarning_ThereIsNothingToCompare()
    {
        var result = ProjectsEndpoint.Resolve(
            [Healthy() with { InstalledPluginVersion = null, AppPluginVersion = "1.2.0" }], Now);

        Assert.DoesNotContain(Assert.Single(result.Projects).Warnings, w => w.Code == "pluginVersionMismatch");
    }

    // ---------------------------------------------------------------- warning: plugin version skew (spec #4 §6 - degrade, never refuse)

    [Fact]
    public void PluginOneMinorBehind_DegradesWithTheOrdinaryWarning_NotEscalated_TheSpecsOwnLiteralExample()
    {
        var result = ProjectsEndpoint.Resolve(
            [Healthy() with { InstalledPluginVersion = "1.1.0", AppPluginVersion = "1.2.0" }], Now);

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings);
        Assert.Equal("pluginVersionMismatch", warning.Code);
        Assert.Equal(ControlSeverity.Warning, warning.Severity);
        Assert.Equal(
            "The installed Hades plugin (v1.1.0) does not match this app (v1.2.0). Editor-dependent tools may not work correctly until it is updated.",
            warning.Message);
        Assert.Equal("Use Install/Update Plugin for this project, then restart Unity if it is already running.", warning.Remedy);
    }

    [Fact]
    public void PluginNewerThanApp_SameMajor_StillJustTheOrdinaryWarning_NotEscalatedMerelyForBeingNewer()
    {
        // Explicit decision (see the plan report): "newer than app" alone does not escalate -
        // only a different MAJOR version does. A same-major newer plugin (e.g. a beta build
        // ahead of this app's own bundled copy) degrades exactly like an older one.
        var result = ProjectsEndpoint.Resolve(
            [Healthy() with { InstalledPluginVersion = "1.3.0", AppPluginVersion = "1.2.0" }], Now);

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings);
        Assert.Equal("pluginVersionMismatch", warning.Code);
        Assert.Equal(ControlSeverity.Warning, warning.Severity);
        Assert.Equal(
            "The installed Hades plugin (v1.3.0) does not match this app (v1.2.0). Editor-dependent tools may not work correctly until it is updated.",
            warning.Message);
    }

    [Fact]
    public void PluginMajorVersionBehind_ProducesTheEscalatedWarning_WithTheExactLiteralMessage()
    {
        var result = ProjectsEndpoint.Resolve(
            [Healthy() with { InstalledPluginVersion = "1.2.0", AppPluginVersion = "2.0.0" }], Now);

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings);
        Assert.Equal("pluginVersionMismatch", warning.Code);
        Assert.Equal(ControlSeverity.Warning, warning.Severity);
        Assert.Equal(
            "The installed Hades plugin (v1.2.0) is a different major version from this app (v2.0.0) — compatibility is not assured, and most Editor-dependent tools should be expected to fail until it is updated.",
            warning.Message);
        Assert.Equal("Use Install/Update Plugin for this project, then restart Unity if it is already running.", warning.Remedy);
    }

    [Fact]
    public void PluginMajorVersionAhead_ThePluginFromTheFutureCase_AlsoProducesTheEscalatedWarning_NeverSilent()
    {
        // Spec #4 §6's own caution: "a plugin from the future may genuinely speak a protocol the
        // app cannot" - the decision recorded here is that this STILL degrades rather than
        // refusing the connection, but the warning says so plainly rather than using the same
        // "may not work correctly" wording a same-major skew gets.
        var result = ProjectsEndpoint.Resolve(
            [Healthy() with { InstalledPluginVersion = "3.0.0", AppPluginVersion = "1.2.0" }], Now);

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings);
        Assert.Equal("pluginVersionMismatch", warning.Code);
        Assert.Equal(ControlSeverity.Warning, warning.Severity);
        Assert.Equal(
            "The installed Hades plugin (v3.0.0) is a different major version from this app (v1.2.0) — compatibility is not assured, and most Editor-dependent tools should be expected to fail until it is updated.",
            warning.Message);
        Assert.Equal("Use Install/Update Plugin for this project, then restart Unity if it is already running.", warning.Remedy);
    }

    [Fact]
    public void UnparseablePluginVersion_ProducesNoMismatchWarning_NothingTrustworthyToCompare()
    {
        var result = ProjectsEndpoint.Resolve(
            [Healthy() with { InstalledPluginVersion = "not-a-version", AppPluginVersion = "1.2.0" }], Now);

        Assert.DoesNotContain(Assert.Single(result.Projects).Warnings, w => w.Code == "pluginVersionMismatch");
    }

    // ---------------------------------------------------------------- warning: path missing

    [Fact]
    public void PathMissing_ProducesAnErrorWarning_WithTheExactLiteralMessage()
    {
        var result = ProjectsEndpoint.Resolve([Healthy() with { PathExists = false }], Now);

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings, w => w.Code == "pathMissing");
        Assert.Equal(ControlSeverity.Error, warning.Severity);
        Assert.Equal("Project path not found — check that the volume is mounted or the drive is connected.", warning.Message);
        Assert.Equal("Reconnect the volume, or remove this project from Hades if it no longer exists.", warning.Remedy);
    }

    // ---------------------------------------------------------------- warning: oracle conformance is reserved, never emitted

    [Fact]
    public void EveryWarningEverEmitted_IsOneOfTheThreeImplementedCodes_OracleConformanceNeverAppears()
    {
        // Every bad condition at once - the maximal case - still never produces a fourth code.
        // Plan 11 Task 3 reserves "oracleConformanceMismatch" (spec #1 §4.4) but does not
        // implement or fake its detection; there is no input on ProjectStateSnapshot that could
        // even ask for it.
        var worstCase = Healthy() with
        {
            PathExists = false,
            SerializationMode = 1,
            InstalledPluginVersion = "0.0.1",
            AppPluginVersion = "9.9.9",
        };

        var result = ProjectsEndpoint.Resolve([worstCase], Now);

        var codes = Assert.Single(result.Projects).Warnings.Select(w => w.Code).ToList();
        Assert.Equal(["pathMissing", "serializationMode", "pluginVersionMismatch"], codes);
        Assert.All(codes, code => Assert.NotEqual("oracleConformanceMismatch", code));
    }

    // ---------------------------------------------------------------- attached-editor state

    [Fact]
    public void EditorAbsent_StateAndStatusAreExactLiterals()
    {
        var result = ProjectsEndpoint.Resolve([Healthy() with { Attached = false, Busy = false }], Now);

        var editor = Assert.Single(result.Projects).Editor;
        Assert.Equal(ProjectEditorState.Absent, editor.State);
        Assert.Equal("No Editor attached", editor.Status);
    }

    [Fact]
    public void EditorAttachedNotBusy_StateAndStatusAreExactLiterals()
    {
        var result = ProjectsEndpoint.Resolve(
            [Healthy() with { Attached = true, Busy = false, EditorUnityVersion = "6000.3.2f1", EditorProcessId = 4321, ConnectionAge = TimeSpan.FromSeconds(90) }],
            Now);

        var editor = Assert.Single(result.Projects).Editor;
        Assert.Equal(ProjectEditorState.Attached, editor.State);
        Assert.Equal("Editor attached", editor.Status);
        Assert.Equal("6000.3.2f1", editor.UnityVersion);
        Assert.Equal(4321, editor.ProcessId);
        Assert.Equal(90, editor.ConnectionAgeSeconds);
    }

    [Fact]
    public void EditorAttachedAndBusy_StateAndStatusAreExactLiterals_NeverReadsAsPlainAttached()
    {
        var result = ProjectsEndpoint.Resolve([Healthy() with { Attached = true, Busy = true }], Now);

        var editor = Assert.Single(result.Projects).Editor;
        Assert.Equal(ProjectEditorState.Busy, editor.State);
        Assert.Equal("Editor attached (busy)", editor.Status);
    }

    // ---------------------------------------------------------------- index state and freshness

    [Fact]
    public void NeverIndexed_IndexStateIsIndexing_StatusIsExactLiteral()
    {
        var result = ProjectsEndpoint.Resolve([Healthy() with { LastIndexedUtc = null }], Now);

        var row = Assert.Single(result.Projects);
        Assert.Equal(ProjectIndexState.Indexing, row.IndexState);
        Assert.Equal("not yet indexed", row.IndexStatus);
    }

    [Fact]
    public void IndexedSecondsAgo_StatusIsExactLiteral()
    {
        var result = ProjectsEndpoint.Resolve([Healthy() with { LastIndexedUtc = Now.AddSeconds(-45) }], Now);

        Assert.Equal("indexed 45s ago", Assert.Single(result.Projects).IndexStatus);
    }

    // ---------------------------------------------------------------- node/edge counts and multiple projects

    [Fact]
    public void NodeAndEdgeCounts_PassThroughUnchanged()
    {
        var result = ProjectsEndpoint.Resolve([Healthy() with { NodeCount = 12345, EdgeCount = 6789 }], Now);

        var row = Assert.Single(result.Projects);
        Assert.Equal(12345, row.NodeCount);
        Assert.Equal(6789, row.EdgeCount);
    }

    [Fact]
    public void MultipleProjects_EachGetsItsOwnIndependentlyResolvedRow()
    {
        var result = ProjectsEndpoint.Resolve(
            [Healthy("Alpha") with { SerializationMode = 1 }, Healthy("Beta") with { PathExists = false }],
            Now);

        Assert.Equal(2, result.Projects.Count);
        Assert.Single(result.Projects.Single(p => p.Name == "Alpha").Warnings, w => w.Code == "serializationMode");
        Assert.Single(result.Projects.Single(p => p.Name == "Beta").Warnings, w => w.Code == "pathMissing");
    }

    [Fact]
    public void NoProjects_IsAWellFormedEmptyResponse()
    {
        var result = ProjectsEndpoint.Resolve([], Now);

        Assert.Empty(result.Projects);
    }
}

/// <summary>
/// Plan 11 Task 7 audit finding: <c>RebuildOperationResult</c> used to expose only
/// <c>nodesBefore</c>/<c>nodesAfter</c> as two raw counts - a shell showing "N nodes added" after a
/// rebuild would have had to subtract them itself, exactly the "counts the client must combine"
/// violation the audit looks for. <see cref="ProjectsEndpoint.BuildRebuildMessage"/> is the pure
/// fix: a fully resolved sentence, tested here the same "hand-typed literal" way every other Resolve
/// method in this file is - see <see cref="ProjectsBuildAsyncTests"/>'s own rebuild tests for proof
/// the real <see cref="ProjectsEndpoint.Rebuild"/> action actually uses it.
/// </summary>
public sealed class ProjectsBuildRebuildMessageTests
{
    [Fact]
    public void NodesIncreased_ReportsThePositiveDeltaWithAPlusSign()
    {
        Assert.Equal("Rebuild complete — 15 nodes (+5 from before).", ProjectsEndpoint.BuildRebuildMessage(nodesBefore: 10, nodesAfter: 15));
    }

    [Fact]
    public void NodesDecreased_ReportsTheNegativeDelta()
    {
        Assert.Equal("Rebuild complete — 18 nodes (-2 from before).", ProjectsEndpoint.BuildRebuildMessage(nodesBefore: 20, nodesAfter: 18));
    }

    [Fact]
    public void NodesUnchanged_ReportsAZeroDelta_NotAMinusSign()
    {
        Assert.Equal("Rebuild complete — 20 nodes (+0 from before).", ProjectsEndpoint.BuildRebuildMessage(nodesBefore: 20, nodesAfter: 20));
    }

    [Fact]
    public void FirstEverIndex_FromZero_ReportsTheFullCountAsTheDelta()
    {
        Assert.Equal("Rebuild complete — 1234 nodes (+1234 from before).", ProjectsEndpoint.BuildRebuildMessage(nodesBefore: 0, nodesAfter: 1234));
    }
}

/// <summary>
/// Proves <see cref="ProjectsEndpoint.BuildAsync"/> actually derives every field - especially the
/// three implemented warnings - from REAL on-disk state and a real <see cref="ProjectService"/>,
/// not from a hand-built snapshot. This is the required property from Plan 11 Task 3: "every
/// warning has a test that triggers it from real state... For serialization mode that means a
/// fixture ProjectSettings.asset with m_SerializationMode: 1" - corrected here to
/// EditorSettings.asset; see ProjectsEndpoint's own class doc comment for why.
/// </summary>
public sealed class ProjectsBuildAsyncTests : IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff40000004";
    const string RealAppPluginVersion = "1.4.0"; // UnityPlugin/Assets/Hades/Runtime/HadesBoot.cs's own PluginVersion constant, confirmed by reading it directly. Keep in sync if that constant ever changes (every other test in this codebase already pins this exact literal too).

    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly List<IDisposable> _toDispose = [];
    readonly List<TcpListener> _listeners = [];
    readonly EditorRegistry _editorRegistry = new();
    readonly ProjectService _projects;

    public ProjectsBuildAsyncTests()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {ProjectGuid}\n");

        _projects = new ProjectService(new AppPaths(_appRoot), _editorRegistry)
        {
            CharonProbeTimeout = TimeSpan.FromSeconds(5),
        };
    }

    /// <summary>
    /// Test-only realpath oracle - invokes the actual (internal) <see cref="ProjectStore.Canonicalize"/>
    /// via reflection rather than re-implementing it, so this helper can never drift from what the
    /// endpoints under test actually do. Needed because <see cref="Path.GetTempPath"/> itself sits
    /// under a symlinked ancestor on macOS (<c>/var</c> -&gt; <c>/private/var</c>): now that
    /// Canonicalize resolves the FULL chain, a path <see cref="ProjectService.Adopt"/>/<see
    /// cref="ProjectService.AdoptAndIndex"/> stores - and so a path <see cref="ProjectsEndpoint"/>
    /// echoes back or hands to a launcher - is the resolved spelling, not the raw fixture one. Only
    /// the two tests that assert a full path string need this; every other test in this class
    /// compares names, guids, or counts, none of which this affects.
    /// </summary>
    static string RealPath(string path)
    {
        var method = typeof(ProjectStore).GetMethod("Canonicalize", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ProjectStore.Canonicalize not found — has it been renamed?");

        return (string)method.Invoke(null, [path])!;
    }

    void WriteEditorSettings(int serializationMode)
    {
        var path = Path.Combine(_projectRoot, "ProjectSettings", "EditorSettings.asset");
        File.WriteAllText(path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!159 &1\n"
            + $"EditorSettings:\n  m_ObjectHideFlags: 0\n  serializedVersion: 15\n  m_SerializationMode: {serializationMode}\n");
    }

    void WriteInstalledPlugin(string version)
    {
        var dir = Path.Combine(_projectRoot, "Assets", "Hades", "Runtime");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "HadesBoot.cs"),
            $"namespace Hades.Runtime {{ public static class HadesBoot {{ public const string PluginVersion = \"{version}\"; }} }}\n");
    }

    void WriteProjectVersion(string version) =>
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectVersion.txt"),
            $"m_EditorVersion: {version}\nm_EditorVersionWithRevision: {version} (0000000000)\n");

    static Hello MakeHello(string unityVersion = "6000.3.2f1", string pluginVersion = "1.2.0") => new()
    {
        ProjectGuid = ProjectGuid,
        ProjectPath = "/tmp/fake-unity-project",
        UnityVersion = unityVersion,
        PluginVersion = pluginVersion,
        ProcessId = 4321,
    };

    /// <summary>Same construction technique as SummaryTests.cs's own SummaryBuildAsyncTests -
    /// a real loopback socket pair wrapped directly in an <see cref="EditorSession"/> and
    /// registered into <see cref="_editorRegistry"/>, skipping EditorListener's token+hello wire
    /// handshake since this is testing <see cref="ProjectsEndpoint.BuildAsync"/>, not the
    /// handshake.</summary>
    async Task<(StreamReader UnityReads, StreamWriter UnityWrites)> RegisterFakeEditorAsync(string unityVersion = "6000.3.2f1", string pluginVersion = "1.2.0")
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

        var session = new EditorSession(server.GetStream(), MakeHello(unityVersion, pluginVersion));
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

    static async Task<ProjectRow> WaitForRowAsync(ProjectService projects, Func<Task<ProjectsResult>> build, Func<ProjectRow, bool> ready, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        ProjectRow? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var result = await build();
            last = result.Projects.SingleOrDefault();
            if (last is not null && ready(last)) return last;
            await Task.Delay(25);
        }

        return last ?? throw new TimeoutException("Project never appeared.");
    }

    // ---------------------------------------------------------------- warning: serialization mode (real fixture)

    [Fact]
    public async Task ForceBinarySerialization_RealEditorSettingsFile_TriggersTheWarning()
    {
        _projects.AdoptAndIndex(_projectRoot);
        WriteEditorSettings(serializationMode: 1);

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings, w => w.Code == "serializationMode");
        Assert.Equal(ControlSeverity.Error, warning.Severity);
        Assert.Equal(
            "Asset serialization is set to Force Binary. Hades reads Unity's YAML directly from disk, so scenes, prefabs, and other serialized assets cannot be scanned at all — the graph is silently incomplete.",
            warning.Message);
    }

    [Fact]
    public async Task MixedSerialization_RealEditorSettingsFile_TriggersTheWarning()
    {
        _projects.AdoptAndIndex(_projectRoot);
        WriteEditorSettings(serializationMode: 0);

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings, w => w.Code == "serializationMode");
        Assert.Equal(ControlSeverity.Warning, warning.Severity);
    }

    [Fact]
    public async Task ForceTextSerialization_RealEditorSettingsFile_NoWarning()
    {
        _projects.AdoptAndIndex(_projectRoot);
        WriteEditorSettings(serializationMode: 2);

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);

        Assert.DoesNotContain(Assert.Single(result.Projects).Warnings, w => w.Code == "serializationMode");
    }

    [Fact]
    public async Task NoEditorSettingsFileAtAll_NoSerializationWarning_UnknownIsNotAssumedBad()
    {
        _projects.AdoptAndIndex(_projectRoot); // never wrote EditorSettings.asset

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);

        Assert.DoesNotContain(Assert.Single(result.Projects).Warnings, w => w.Code == "serializationMode");
    }

    // ---------------------------------------------------------------- warning: plugin version mismatch (real fixture)

    [Fact]
    public async Task PluginVersionMismatch_RealInstalledHadesBootFile_TriggersTheWarning()
    {
        _projects.AdoptAndIndex(_projectRoot);
        WriteInstalledPlugin("1.0.0");

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings, w => w.Code == "pluginVersionMismatch");
        Assert.Equal(
            $"The installed Hades plugin (v1.0.0) does not match this app (v{RealAppPluginVersion}). Editor-dependent tools may not work correctly until it is updated.",
            warning.Message);
    }

    [Fact]
    public async Task PluginVersionMatchesTheApp_RealInstalledHadesBootFile_NoWarning()
    {
        _projects.AdoptAndIndex(_projectRoot);
        WriteInstalledPlugin(RealAppPluginVersion);

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);

        Assert.DoesNotContain(Assert.Single(result.Projects).Warnings, w => w.Code == "pluginVersionMismatch");
    }

    [Fact]
    public async Task PluginNotInstalledAtAll_NoMismatchWarning()
    {
        _projects.AdoptAndIndex(_projectRoot); // never wrote Assets/Hades/

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);

        Assert.DoesNotContain(Assert.Single(result.Projects).Warnings, w => w.Code == "pluginVersionMismatch");
    }

    // ---------------------------------------------------------------- warning: plugin version - live Hello wins over the file scan

    [Fact]
    public async Task PluginVersionMismatch_LiveHelloWinsOverAFileThatNowMatches_AfterInstallWhileAttached()
    {
        // The exact staleness gap this task closes: installPlugin just wrote the APP's version to
        // disk (simulated directly here via WriteInstalledPlugin), but the already-attached Editor
        // is still running the OLD build until Unity restarts (InstallPluginAsync's own
        // NeedsRestart=true case - see that method's doc comment). A file-scan-only comparison
        // would wrongly read "matches" the instant the new bytes hit disk; the live Hello - what
        // is actually loaded right now - must keep the warning alive until the Editor actually
        // reconnects with a matching version.
        _projects.AdoptAndIndex(_projectRoot);
        WriteInstalledPlugin(RealAppPluginVersion); // file now matches - simulates a just-completed install
        var (reads, writes) = await RegisterFakeEditorAsync(pluginVersion: "1.0.0"); // still the old build, pre-restart
        var responder = RespondToNextProbeAsync(reads, writes);

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings, w => w.Code == "pluginVersionMismatch");
        Assert.Equal(
            $"The installed Hades plugin (v1.0.0) does not match this app (v{RealAppPluginVersion}). Editor-dependent tools may not work correctly until it is updated.",
            warning.Message);
    }

    [Fact]
    public async Task PluginVersionMatches_LiveHelloConfirmsIt_OverridingAStaleFileThatWouldDisagree()
    {
        // The mirror case: the file on disk happens to read as a different (stale) version, but
        // the live, connected Editor's own Hello - what is actually running right now - matches
        // the app. No warning: live truth wins over a stale file in either direction, not just
        // the "install just happened" one above.
        _projects.AdoptAndIndex(_projectRoot);
        WriteInstalledPlugin("1.0.0"); // stale/wrong file reading
        var (reads, writes) = await RegisterFakeEditorAsync(pluginVersion: RealAppPluginVersion); // actually running the current version
        var responder = RespondToNextProbeAsync(reads, writes);

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(Assert.Single(result.Projects).Warnings, w => w.Code == "pluginVersionMismatch");
    }

    [Fact]
    public async Task PluginVersionMajorMismatch_RealAttachedEditor_TriggersTheEscalatedWarning()
    {
        _projects.AdoptAndIndex(_projectRoot); // never wrote Assets/Hades/ at all - live Hello alone is enough to compare
        var (reads, writes) = await RegisterFakeEditorAsync(pluginVersion: "9.9.9");
        var responder = RespondToNextProbeAsync(reads, writes);

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings, w => w.Code == "pluginVersionMismatch");
        Assert.Equal(
            $"The installed Hades plugin (v9.9.9) is a different major version from this app (v{RealAppPluginVersion}) — compatibility is not assured, and most Editor-dependent tools should be expected to fail until it is updated.",
            warning.Message);
    }

    // ---------------------------------------------------------------- warning: path missing (real fixture)

    [Fact]
    public async Task PathMissing_RealDirectoryDeleted_TriggersTheWarning()
    {
        _projects.AdoptAndIndex(_projectRoot);
        Directory.Delete(_projectRoot, recursive: true);

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);

        var warning = Assert.Single(Assert.Single(result.Projects).Warnings, w => w.Code == "pathMissing");
        Assert.Equal(ControlSeverity.Error, warning.Severity);
        Assert.Equal("Project path not found — check that the volume is mounted or the drive is connected.", warning.Message);
    }

    // ---------------------------------------------------------------- unity version resolution

    [Fact]
    public async Task UnityVersion_ReadFromProjectVersionTxt_WhenNoEditorIsAttached()
    {
        _projects.AdoptAndIndex(_projectRoot);
        WriteProjectVersion("6000.3.2f1");

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);

        Assert.Equal("6000.3.2f1", Assert.Single(result.Projects).UnityVersion);
    }

    [Fact]
    public async Task UnityVersion_LiveAttachedEditorWins_OverADifferentProjectVersionTxt()
    {
        _projects.AdoptAndIndex(_projectRoot);
        WriteProjectVersion("2022.3.1f1"); // stale/different from what is actually attached right now
        var (reads, writes) = await RegisterFakeEditorAsync(unityVersion: "6000.3.2f1");
        var responder = RespondToNextProbeAsync(reads, writes);

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("6000.3.2f1", Assert.Single(result.Projects).UnityVersion);
    }

    // ---------------------------------------------------------------- attached-editor state (real charon status)

    [Fact]
    public async Task AttachedEditor_RowReflectsRealCharonStatus_NotAFabricatedValue()
    {
        _projects.AdoptAndIndex(_projectRoot);
        var (reads, writes) = await RegisterFakeEditorAsync();
        var responder = RespondToNextProbeAsync(reads, writes);

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);
        await responder.WaitAsync(TimeSpan.FromSeconds(30));

        var editor = Assert.Single(result.Projects).Editor;
        Assert.Equal(ProjectEditorState.Attached, editor.State);
        Assert.Equal("Editor attached", editor.Status);
        Assert.Equal("6000.3.2f1", editor.UnityVersion);
        Assert.Equal(4321, editor.ProcessId);
        Assert.NotNull(editor.ConnectionAgeSeconds);
    }

    [Fact]
    public async Task NoEditorAttached_RowSaysAbsent()
    {
        _projects.AdoptAndIndex(_projectRoot);

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);

        var editor = Assert.Single(result.Projects).Editor;
        Assert.Equal(ProjectEditorState.Absent, editor.State);
        Assert.Equal("No Editor attached", editor.Status);
    }

    // ---------------------------------------------------------------- node/edge counts reflect the real graph

    [Fact]
    public async Task NodeAndEdgeCounts_MatchWhatProjectServiceSummaryReports()
    {
        _projects.AdoptAndIndex(_projectRoot);
        var summary = _projects.Summary(ProjectGuid)!;

        var result = await ProjectsEndpoint.BuildAsync(_projects, () => DateTimeOffset.UtcNow);

        var row = Assert.Single(result.Projects);
        Assert.Equal(summary.TotalNodes, row.NodeCount);
        Assert.Equal(summary.TotalEdges, row.EdgeCount);
    }

    // ---------------------------------------------------------------- add

    [Fact]
    public async Task Add_ValidUnityProject_AdoptsIndexesAndReturnsAResolvedRow()
    {
        var freshRoot = Path.Combine(_appRoot + "-fresh-project");
        Directory.CreateDirectory(Path.Combine(freshRoot, "ProjectSettings"));
        const string freshGuid = "aaaabbbbccccddddeeeeffff50000005";
        File.WriteAllText(Path.Combine(freshRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {freshGuid}\n");

        try
        {
            var response = await ProjectsEndpoint.AddAsync(_projects, () => DateTimeOffset.UtcNow, new AddProjectRequest { Path = freshRoot });

            var json = await ResultBodyAsync(response);
            Assert.Equal(Path.GetFileName(freshRoot), json.GetProperty("name").GetString());
            Assert.Equal(RealPath(freshRoot), json.GetProperty("path").GetString());
            Assert.Equal(freshGuid, json.GetProperty("productGuid").GetString());
            Assert.Equal("indexed", json.GetProperty("indexState").GetString());

            Assert.Contains(_projects.KnownProjects(), p => p.ProductGuid == freshGuid);
        }
        finally
        {
            Directory.Delete(freshRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Add_NotAUnityProject_Returns400WithAClearMessage_DoesNotThrow()
    {
        var notAProject = Path.Combine(_appRoot + "-not-a-project");
        Directory.CreateDirectory(notAProject);

        try
        {
            var response = await ProjectsEndpoint.AddAsync(_projects, () => DateTimeOffset.UtcNow, new AddProjectRequest { Path = notAProject });

            Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(response));
        }
        finally
        {
            Directory.Delete(notAProject, recursive: true);
        }
    }

    [Fact]
    public async Task Add_BlankPath_Returns400()
    {
        var response = await ProjectsEndpoint.AddAsync(_projects, () => DateTimeOffset.UtcNow, new AddProjectRequest { Path = "   " });

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(response));
    }

    // ---------------------------------------------------------------- remove

    [Fact]
    public async Task Remove_KnownProject_DeregistersIt_AndReturnsASuccessMessage()
    {
        _projects.Adopt(_projectRoot);

        var response = ProjectsEndpoint.Remove(_projects, ProjectGuid);
        var json = await ResultBodyAsync(response);

        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Contains("removed from Hades", json.GetProperty("message").GetString());
        Assert.Contains("Nothing was deleted from disk", json.GetProperty("message").GetString());

        Assert.DoesNotContain(_projects.KnownProjects(), p => p.ProductGuid == ProjectGuid);
    }

    [Fact]
    public void Remove_UnknownGuid_Returns404()
    {
        var response = ProjectsEndpoint.Remove(_projects, "not-a-known-guid");

        Assert.Equal(StatusCodes.Status404NotFound, StatusCodeOf(response));
    }

    [Fact]
    public void Remove_CalledTwice_IsIdempotent_SecondCallStillSucceeds()
    {
        _projects.Adopt(_projectRoot);

        var first = ProjectsEndpoint.Remove(_projects, ProjectGuid);
        var second = ProjectsEndpoint.Remove(_projects, ProjectGuid);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(first));
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(second));
    }

    [Fact]
    public void Remove_LeavesAuthoredMemoryFilesOnDisk()
    {
        _projects.Adopt(_projectRoot);

        var memoryDir = _projects.Paths.MemoryDir(ProjectGuid);
        Directory.CreateDirectory(memoryDir);
        File.WriteAllText(Path.Combine(memoryDir, "decisions.md"), "# Authored decisions\nThis must survive remove.");
        File.WriteAllText(Path.Combine(memoryDir, "conventions.md"), "# Authored conventions\nThis must survive too.");

        ProjectsEndpoint.Remove(_projects, ProjectGuid);

        Assert.True(File.Exists(Path.Combine(memoryDir, "decisions.md")), "decisions.md must still exist after remove");
        Assert.True(File.Exists(Path.Combine(memoryDir, "conventions.md")), "conventions.md must still exist after remove");
        Assert.Equal("# Authored decisions\nThis must survive remove.", File.ReadAllText(Path.Combine(memoryDir, "decisions.md")));
    }

    [Fact]
    public void Remove_AlsoLeavesTheGraphDatabaseAndProjectJsonOnDisk_NotJustMemory()
    {
        _projects.AdoptAndIndex(_projectRoot);
        var graphDb = _projects.Paths.GraphDb(ProjectGuid);
        var projectJson = _projects.Paths.ProjectFile(ProjectGuid);
        Assert.True(File.Exists(graphDb));
        Assert.True(File.Exists(projectJson));

        ProjectsEndpoint.Remove(_projects, ProjectGuid);

        Assert.True(File.Exists(graphDb), "graph.db must still exist after remove - remove never deletes anything on disk");
        Assert.True(File.Exists(projectJson), "project.json must still exist after remove - it is rewritten (Removed=true), never deleted");
    }

    [Fact]
    public void Remove_ThenReAdd_MakesItKnownAgain()
    {
        _projects.Adopt(_projectRoot);
        ProjectsEndpoint.Remove(_projects, ProjectGuid);
        Assert.DoesNotContain(_projects.KnownProjects(), p => p.ProductGuid == ProjectGuid);

        _projects.Adopt(_projectRoot);

        Assert.Contains(_projects.KnownProjects(), p => p.ProductGuid == ProjectGuid);
    }

    // ---------------------------------------------------------------- rebuild

    [Fact]
    public async Task Rebuild_KnownProject_ReturnsAnOperationIdImmediately_AndTheRebuildEventuallyRuns()
    {
        _projects.Adopt(_projectRoot); // adopted but never indexed - LastIndexedUtc starts null
        Assert.Null(_projects.Summary(ProjectGuid)!.LastIndexedUtc);

        var operations = new OperationRegistry();
        var response = ProjectsEndpoint.Rebuild(_projects, operations, ProjectGuid);
        var json = await ResultBodyAsync(response);
        var operationId = json.GetProperty("operationId").GetString();

        Assert.False(string.IsNullOrWhiteSpace(operationId));
        Assert.True(Guid.TryParse(operationId, out _), "operationId should be a fresh guid-shaped string");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && _projects.Summary(ProjectGuid)!.LastIndexedUtc is null)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(_projects.Summary(ProjectGuid)!.LastIndexedUtc);

        // Plan 11 Task 5: the id must actually be pollable through the SAME registry, not just a
        // freshly-minted guid backed by nothing (Task 3's own former gap).
        await operations.WhenComplete(operationId!);
        var op = operations.Get(operationId!);
        Assert.NotNull(op);
        Assert.Equal(OperationState.Done, op!.State);
        var result = Assert.IsType<RebuildOperationResult>(op.Result);

        // Plan 11 Task 7 audit fix: Message must actually be built from THIS result's own
        // NodesBefore/NodesAfter (not a stray literal) - re-deriving the expected sentence from the
        // same numbers under test, rather than hardcoding a magic node count this test does not
        // control (real Roslyn scanning of an on-disk fixture project).
        Assert.Equal(ProjectsEndpoint.BuildRebuildMessage(result.NodesBefore, result.NodesAfter), result.Message);

        // I5 gap fix: the exact-equality assertion above already implies no warning suffix was
        // appended (this fixture project has no poison asset), but that is easy to miss reading
        // the assertion alone - made explicit here so the no-warning case's Message is pinned as
        // carrying NO suffix, not merely inferred. See Rebuild_OnePoisonAsset_... /
        // Rebuild_TwoPoisonAssets_... below for the warning-suffix cases themselves.
        Assert.DoesNotContain("could not be fully indexed", result.Message);
    }

    [Fact]
    public async Task Rebuild_OnePoisonAsset_MessageNamesTheSingleFileAndItsPath()
    {
        // I5's last wiring step (see ProjectsEndpoint.Rebuild's own comment): the per-file
        // diagnostics RebuildGraph carries must reach the operation's user-visible Message. One
        // poison asset - the same unparseable class-id header AssetIndexerTests' own poison
        // fixture uses (Assets/Poison.prefab) - alongside one healthy asset.
        _projects.Adopt(_projectRoot);
        Directory.CreateDirectory(Path.Combine(_projectRoot, "Assets"));
        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "Poison.prefab"),
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!4294967296 &1\nGameObject:\n  m_Name: Poison\n");
        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "Good.prefab"),
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1 &1\nGameObject:\n  m_Name: Good\n");

        var operations = new OperationRegistry();
        var response = ProjectsEndpoint.Rebuild(_projects, operations, ProjectGuid);
        var json = await ResultBodyAsync(response);
        var operationId = json.GetProperty("operationId").GetString()!;

        await operations.WhenComplete(operationId);
        var op = operations.Get(operationId);
        Assert.NotNull(op);
        var result = Assert.IsType<RebuildOperationResult>(op!.Result);

        Assert.Contains("1 file could not be fully indexed", result.Message);
        Assert.Contains("Assets/Poison.prefab", result.Message);
    }

    [Fact]
    public async Task Rebuild_TwoPoisonAssets_MessageNamesTheCountAndTheFirstFile()
    {
        _projects.Adopt(_projectRoot);
        Directory.CreateDirectory(Path.Combine(_projectRoot, "Assets"));
        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "PoisonA.prefab"),
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!4294967296 &1\nGameObject:\n  m_Name: PoisonA\n");
        File.WriteAllText(Path.Combine(_projectRoot, "Assets", "PoisonB.prefab"),
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!4294967296 &1\nGameObject:\n  m_Name: PoisonB\n");

        var operations = new OperationRegistry();
        var response = ProjectsEndpoint.Rebuild(_projects, operations, ProjectGuid);
        var json = await ResultBodyAsync(response);
        var operationId = json.GetProperty("operationId").GetString()!;

        await operations.WhenComplete(operationId);
        var op = operations.Get(operationId);
        Assert.NotNull(op);
        var result = Assert.IsType<RebuildOperationResult>(op!.Result);

        // Which of the two poison files sorts "first" is filesystem enumeration order, not
        // something this test controls - only the count and the "; first:" shape are pinned here.
        Assert.Contains("2 files could not be fully indexed; first:", result.Message);
    }

    [Fact]
    public void Rebuild_UnknownGuid_Returns404()
    {
        var response = ProjectsEndpoint.Rebuild(_projects, new OperationRegistry(), "not-a-known-guid");

        Assert.Equal(StatusCodes.Status404NotFound, StatusCodeOf(response));
    }

    // ---------------------------------------------------------------- installPlugin

    [Fact]
    public async Task InstallPlugin_NoEditorAttached_NeedsRestartIsFalse_WithTheExactLiteralMessage()
    {
        _projects.Adopt(_projectRoot);

        var response = await ProjectsEndpoint.InstallPluginAsync(_projects, ProjectGuid);
        var json = await ResultBodyAsync(response);

        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.False(json.GetProperty("needsRestart").GetBoolean());
        Assert.Equal("Plugin installed. It will load automatically the next time this project opens in Unity.",
            json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task InstallPlugin_EditorAlreadyAttached_NeedsRestartIsTrue_WithTheExactLiteralMessage()
    {
        _projects.Adopt(_projectRoot);
        var (reads, writes) = await RegisterFakeEditorAsync();
        var responder = RespondToNextProbeAsync(reads, writes);

        var response = await ProjectsEndpoint.InstallPluginAsync(_projects, ProjectGuid);
        await responder.WaitAsync(TimeSpan.FromSeconds(30));
        var json = await ResultBodyAsync(response);

        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.True(json.GetProperty("needsRestart").GetBoolean());
        Assert.Equal(
            "Plugin installed. Restart Unity to load it — an Editor already running when the plugin is installed will not pick it up until restart.",
            json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task InstallPlugin_ActuallyWritesThePluginFilesToDisk()
    {
        _projects.Adopt(_projectRoot);

        await ProjectsEndpoint.InstallPluginAsync(_projects, ProjectGuid);

        Assert.True(File.Exists(Path.Combine(_projectRoot, "Assets", "Hades", "Runtime", "HadesBoot.cs")));
        Assert.True(File.Exists(Path.Combine(_projectRoot, "Assets", "Hades", "Hades.asmdef")));
    }

    [Fact]
    public async Task InstallPlugin_UnknownGuid_Returns404()
    {
        var response = await ProjectsEndpoint.InstallPluginAsync(_projects, "not-a-known-guid");

        Assert.Equal(StatusCodes.Status404NotFound, StatusCodeOf(response));
    }

    [Fact]
    public async Task InstallPlugin_PathMissing_FailsCleanly_NeverThrows()
    {
        _projects.Adopt(_projectRoot);
        Directory.Delete(_projectRoot, recursive: true);

        var response = await ProjectsEndpoint.InstallPluginAsync(_projects, ProjectGuid);
        var json = await ResultBodyAsync(response);

        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal("Project path not found — check that the volume is mounted or the drive is connected.",
            json.GetProperty("message").GetString());
    }

    // ---------------------------------------------------------------- revealInFinder

    [Fact]
    public async Task RevealInFinder_PathExists_InvokesThePlatformFileManager()
    {
        _projects.Adopt(_projectRoot);
        string? capturedExecutable = null;
        IReadOnlyList<string>? capturedArgs = null;
        bool Fake(string exe, IReadOnlyList<string> args) { capturedExecutable = exe; capturedArgs = args; return true; }

        var response = ProjectsEndpoint.RevealInFinder(_projects, ProjectGuid, Fake);
        var json = await ResultBodyAsync(response);

        if (OperatingSystem.IsWindows())
        {
            // explorer.exe takes the selection as ONE comma-joined argument, not two.
            Assert.Equal("explorer.exe", capturedExecutable);
            Assert.Equal([$"/select,{RealPath(_projectRoot)}"], capturedArgs);
        }
        else
        {
            Assert.Equal("open", capturedExecutable);
            Assert.Equal(["-R", RealPath(_projectRoot)], capturedArgs);
        }
        Assert.True(json.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task RevealInFinder_PathMissing_FailsCleanly_NeverInvokesTheLauncher()
    {
        _projects.Adopt(_projectRoot);
        Directory.Delete(_projectRoot, recursive: true);
        var invoked = false;
        bool Fake(string exe, IReadOnlyList<string> args) { invoked = true; return true; }

        var response = ProjectsEndpoint.RevealInFinder(_projects, ProjectGuid, Fake);
        var json = await ResultBodyAsync(response);

        Assert.False(invoked);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal("Project path not found — check that the volume is mounted or the drive is connected.",
            json.GetProperty("message").GetString());
    }

    [Fact]
    public void RevealInFinder_UnknownGuid_Returns404()
    {
        var response = ProjectsEndpoint.RevealInFinder(_projects, "not-a-known-guid", (_, _) => true);

        Assert.Equal(StatusCodes.Status404NotFound, StatusCodeOf(response));
    }

    // ---------------------------------------------------------------- openInUnity

    [Fact]
    public async Task OpenInUnity_PathMissing_FailsCleanly_NeverInvokesTheLauncher()
    {
        _projects.Adopt(_projectRoot);
        Directory.Delete(_projectRoot, recursive: true);
        var invoked = false;
        bool Fake(string exe, IReadOnlyList<string> args) { invoked = true; return true; }

        var response = ProjectsEndpoint.OpenInUnity(_projects, ProjectGuid, Fake);
        var json = await ResultBodyAsync(response);

        Assert.False(invoked);
        Assert.False(json.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task OpenInUnity_NoProjectVersionFile_FailsCleanly_WithTheExactLiteralMessage()
    {
        _projects.Adopt(_projectRoot); // never wrote ProjectVersion.txt

        var response = ProjectsEndpoint.OpenInUnity(_projects, ProjectGuid, (_, _) => true);
        var json = await ResultBodyAsync(response);

        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal(
            "This project's Unity version is unknown — it has no ProjectSettings/ProjectVersion.txt yet. Open it once from Unity Hub, then try again.",
            json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OpenInUnity_VersionKnownButNoEditorInstalledAtTheDefaultLocation_FailsCleanly_NeverInvokesTheLauncher()
    {
        _projects.Adopt(_projectRoot);
        WriteProjectVersion("0000.0.0f1-hades-test-fixture-version-that-will-never-be-installed");
        var invoked = false;
        bool Fake(string exe, IReadOnlyList<string> args) { invoked = true; return true; }

        var response = ProjectsEndpoint.OpenInUnity(_projects, ProjectGuid, Fake);
        var json = await ResultBodyAsync(response);

        Assert.False(invoked);
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Contains("0000.0.0f1-hades-test-fixture-version-that-will-never-be-installed", json.GetProperty("message").GetString());
        Assert.Contains("Unity Hub", json.GetProperty("message").GetString());
    }

    [Fact]
    public void OpenInUnity_UnknownGuid_Returns404()
    {
        var response = ProjectsEndpoint.OpenInUnity(_projects, "not-a-known-guid", (_, _) => true);

        Assert.Equal(StatusCodes.Status404NotFound, StatusCodeOf(response));
    }

    [Fact]
    public void UnityHubPathFollowsThePlatformConvention()
    {
        var path = ProjectsEndpoint.UnityHubEditorExecutablePath("6000.0.30f1");

        if (OperatingSystem.IsWindows())
            Assert.Equal(@"C:\Program Files\Unity\Hub\Editor\6000.0.30f1\Editor\Unity.exe", path);
        else
            Assert.Equal("/Applications/Unity/Hub/Editor/6000.0.30f1/Unity.app/Contents/MacOS/Unity", path);
    }

    // ---------------------------------------------------------------- the real (non-faked) process launcher

    [Fact]
    public void DefaultProcessLauncher_NonExistentExecutable_ReturnsFalse_NeverThrows()
    {
        var result = ProjectsEndpoint.DefaultProcessLauncher(
            "/nonexistent/hades-test-fixture/does-not-exist", ["-arg"]);

        Assert.False(result);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Executes an <see cref="IResult"/> exactly as ASP.NET Core's own pipeline would -
    /// including resolving <c>Results.Json</c>'s JSON options via <c>HttpContext.RequestServices</c>
    /// - against a minimal, otherwise-empty real <see cref="IServiceProvider"/> rather than an
    /// uninitialised one, so this never depends on undocumented null-provider fallback behaviour.</summary>
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

    static int StatusCodeOf(IResult result)
    {
        var context = NewContext();
        result.ExecuteAsync(context).GetAwaiter().GetResult();
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
/// <c>GET /control/projects</c> and its actions over real HTTP against a directly-constructed
/// <see cref="ControlListener"/> - same style as SummaryTests.cs's own SummaryEndpointHttpTests:
/// proving auth/Origin/routing, not re-proving the resolution logic already covered above.
/// </summary>
public sealed class ProjectsEndpointHttpTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");

    static HttpRequestMessage Request(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    static HttpClient ClientFor(ControlListener listener) => new() { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };

    [Fact]
    public async Task GetProjects_NoToken_IsRefused()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/projects", token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetProjects_ValidToken_NoProjectsAdopted_ReturnsAnEmptyArray()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/projects", listener.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("projects").GetArrayLength());
    }

    [Fact]
    public async Task GetProjects_ValidToken_ReflectsARealAdoptedProject_OverRealHttp()
    {
        var projectRoot = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ProjectSettings"));
        const string guid = "aaaabbbbccccddddeeeeffff60000006";
        File.WriteAllText(Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {guid}\n");

        var projectService = new ProjectService(new AppPaths(Path.Combine(_tempDir, "app")));
        projectService.AdoptAndIndex(projectRoot);

        using var listener = new ControlListener(ConnectionFilePath, projects: projectService);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/control/projects", listener.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = Assert.Single(body.GetProperty("projects").EnumerateArray());
        Assert.Equal(Path.GetFileName(projectRoot), row.GetProperty("name").GetString());
        Assert.Equal(guid, row.GetProperty("productGuid").GetString());
        Assert.Equal("indexed", row.GetProperty("indexState").GetString());
        Assert.Equal(0, row.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task RemoveAction_NoToken_IsRefused()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, "/control/projects/some-guid/remove", token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RemoveAction_ValidToken_KnownProject_DeregistersOverRealHttp()
    {
        var projectRoot = Path.Combine(_tempDir, "proj2");
        Directory.CreateDirectory(Path.Combine(projectRoot, "ProjectSettings"));
        const string guid = "aaaabbbbccccddddeeeeffff70000007";
        File.WriteAllText(Path.Combine(projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {guid}\n");

        var projectService = new ProjectService(new AppPaths(Path.Combine(_tempDir, "app2")));
        projectService.Adopt(projectRoot);

        using var listener = new ControlListener(ConnectionFilePath, projects: projectService);
        listener.Start();
        using var client = ClientFor(listener);

        var response = await client.SendAsync(Request(HttpMethod.Post, $"/control/projects/{guid}/remove", listener.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.DoesNotContain(projectService.KnownProjects(), p => p.ProductGuid == guid);
    }

    [Fact]
    public async Task AddAction_ValidToken_RealJsonBody_AdoptsAndReturnsARow_ProvesTheRequestBodyBindsCorrectly()
    {
        var freshRoot = Path.Combine(_tempDir, "fresh-add-target");
        Directory.CreateDirectory(Path.Combine(freshRoot, "ProjectSettings"));
        const string guid = "aaaabbbbccccddddeeeeffff90000009";
        File.WriteAllText(Path.Combine(freshRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {guid}\n");

        var projectService = new ProjectService(new AppPaths(Path.Combine(_tempDir, "app3")));
        using var listener = new ControlListener(ConnectionFilePath, projects: projectService);
        listener.Start();
        using var client = ClientFor(listener);

        var request = Request(HttpMethod.Post, "/control/projects/add", listener.Token);
        request.Content = JsonContent.Create(new { path = freshRoot });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(guid, body.GetProperty("productGuid").GetString());
        Assert.Equal("indexed", body.GetProperty("indexState").GetString());
        Assert.Contains(projectService.KnownProjects(), p => p.ProductGuid == guid);
    }

    [Fact]
    public async Task AddAction_ForeignOrigin_IsRejectedWith403_EvenWithAValidToken()
    {
        using var listener = new ControlListener(ConnectionFilePath);
        listener.Start();
        using var client = ClientFor(listener);

        var request = Request(HttpMethod.Post, "/control/projects/add", listener.Token);
        request.Headers.Add("Origin", "https://evil.example.com");
        request.Content = JsonContent.Create(new { path = "/tmp/whatever" });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}

/// <summary>
/// Proves Program.cs's existing ControlListener registration (built for Task 2, unchanged by Task
/// 3 beyond ControlListener gaining an optional constructor parameter with a safe default) also
/// serves <c>/control/projects</c> against the app's real, shared <see cref="ProjectService"/>
/// singleton - same proof shape as SummaryTests.cs's own SummaryProgramWiringTests.
/// </summary>
public sealed class ProjectsProgramWiringTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff80000008";

    readonly WebApplicationFactory<Program> _factory;
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ProjectsProgramWiringTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {ProjectGuid}\n");

        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<AppPaths>();
                services.AddSingleton(new AppPaths(_appRoot));
            }));
    }

    [Fact]
    public async Task ControlListener_SeesTheSameProjectServiceEverythingElseInTheAppUses()
    {
        var projects = _factory.Services.GetRequiredService<ProjectService>();
        projects.AdoptAndIndex(_projectRoot);

        var listener = _factory.Services.GetRequiredService<ControlListener>();
        var port = await ProgramWiringPort.WaitAsync(listener);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var request = new HttpRequestMessage(HttpMethod.Get, "/control/projects");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", listener.Token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = Assert.Single(body.GetProperty("projects").EnumerateArray());
        Assert.Equal(ProjectGuid, row.GetProperty("productGuid").GetString());
    }

    public void Dispose()
    {
        // See EditorToolTestBase.Dispose's own comment: _factory is a fresh per-test
        // WebApplicationFactory whose own background services can still be touching
        // _appRoot/_projectRoot until the host itself is disposed - which must happen before
        // the recursive delete below.
        _factory.Dispose();

        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
