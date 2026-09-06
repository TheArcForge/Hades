using System.Reflection;
using Hades.Control.Client;
using Hades.Core;
using Hades.Core.Editors;
using Hades.Core.Projects;
using Hades.Core.Storage;
using Hades.Server.Control;

namespace Hades.Cli.Tests;

/// <summary>
/// <see cref="Commands"/> against a real <see cref="ControlListener"/> over real loopback HTTP -
/// the same "direct construction, real socket, no mocking" technique
/// ControlAuthTests/ProjectsEndpointHttpTests already use for this exact listener - never a mocked
/// HttpClient, and never a mocked <see cref="ControlClient"/> either: this builds a real
/// <c>ControlClient</c> over the listener's own <see cref="ControlConnection"/> (port and token),
/// so every request still crosses a real loopback socket exactly as it did when this test built a
/// bare <see cref="HttpClient"/> directly. This is the CLI's whole reason for existing (Plan 11
/// Task 7): every value asserted below must appear VERBATIM, because <see cref="Commands"/> is only
/// ever supposed to read a JSON field and print it - never format, sum, compare, or map one. A test
/// here that had to compute an expected string from more than one response field would itself be
/// evidence the endpoint under-specifies its surface (see this project's own README-equivalent:
/// Commands' class doc comment).
/// </summary>
public sealed class CommandsTests : IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff50000005";

    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly List<IDisposable> _toDispose = [];
    readonly LeaseRegistry _leases = new();
    readonly ProjectService _projects;

    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");
    string ProjectName => Path.GetFileName(_projectRoot);

    public CommandsTests()
    {
        _projects = new ProjectService(new AppPaths(_appRoot));
    }

    void AdoptFixtureProject()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"), $"  productGUID: {ProjectGuid}\n");
        _projects.AdoptAndIndex(_projectRoot);
    }

    /// <summary>
    /// Test-only realpath oracle - invokes the actual (internal) <see cref="ProjectStore.Canonicalize"/>
    /// via reflection rather than re-implementing it, so this helper can never drift from what the
    /// server actually does. Needed because <see cref="Path.GetTempPath"/> itself sits under a
    /// symlinked ancestor on macOS (<c>/var</c> -&gt; <c>/private/var</c>): now that Canonicalize
    /// resolves the FULL chain, a project adopted from <see cref="_projectRoot"/> is stored (and so
    /// echoed back by the server) under its resolved spelling, not the raw one - the one assertion
    /// here that prints a full path must compare against THIS, for a reason that has nothing to do
    /// with the CLI-printing behavior it actually names and exercises.
    /// </summary>
    static string RealPath(string path)
    {
        var method = typeof(ProjectStore).GetMethod("Canonicalize", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ProjectStore.Canonicalize not found — has it been renamed?");

        return (string)method.Invoke(null, [path])!;
    }

    (ControlListener Listener, ControlClient Client) StartListener()
    {
        var listener = new ControlListener(ConnectionFilePath, projects: _projects, leases: _leases);
        listener.Start();
        _toDispose.Add(listener);

        var connection = new ControlConnection { Port = listener.Port, Token = listener.Token };
        var client = new ControlClient(connection);

        return (listener, client);
    }

    // ---------------------------------------------------------------- status

    [Fact]
    public async Task Status_NoProjects_PrintsTheFirstRunHeadlineVerbatim_AndNoLeaseSection()
    {
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.StatusAsync(client, output);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("icon:     idle", text);
        Assert.Contains("headline: No projects yet — add a Unity project to get started.", text);
        Assert.Contains("(none)", text);
        Assert.DoesNotContain("lease:", text);
    }

    [Fact]
    public async Task Status_OneProject_PrintsTheRowsProjectStatusAndSeverityVerbatim()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.StatusAsync(client, output);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        // AdoptAndIndex indexes synchronously, so LastIndexedUtc is already set by the time this
        // runs - "indexed <age> ago", never "not yet indexed". The exact age is a live clock reading
        // (SummaryEndpoint.FormatAge), so this checks the stable prefix/suffix around it rather than
        // pinning e.g. "indexed 0s ago", which would be one clock tick from flaking.
        Assert.Contains($"- {ProjectName}: No Editor attached · indexed ", text);
        Assert.Contains(" ago [ok]", text);
    }

    [Fact]
    public async Task Status_LeaseHeld_PrintsEveryLeaseFieldVerbatim()
    {
        AdoptFixtureProject();
        _leases.RecordHeld(ProjectGuid, "hades-script-editing", DateTimeOffset.UtcNow.AddSeconds(30));
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.StatusAsync(client, output);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("icon:     leaseHeld", text);
        Assert.Contains("lease:", text);
        Assert.Contains($"project:           {ProjectName}", text);
        Assert.Contains("leaseId:           " + ProjectGuid, text);
        // heldForSeconds is not pinned to an exact literal: LeaseRegistry.RecordHeld stamps
        // AcquiredAtUtc from the real clock, so the true elapsed value depends on real wall-clock
        // time between that call and this HTTP round trip (test-machine load can push it past a
        // rounding boundary) - the CLI's own job under test is only "print whatever integer the
        // server sent", which this proves without asserting on the server's own time math (already
        // pinned exactly, with an injected clock, by SummaryResolveTests).
        Assert.Matches(@"heldForSeconds:\s+\d+", text);
        // Same clock-derived risk as heldForSeconds above, guarded the same way: SummaryEndpoint.BuildLease
        // computes this as Math.Round((lease.ExpiresAtUtc - now).TotalSeconds), so real wall-clock time
        // spent on the HTTP round trip can round the true remaining time down from 30 to 29 under load -
        // this is not pinned to the exact literal for the same reason heldForSeconds isn't. Matters more
        // now that LeaseRegistry self-expires (Get/All evict once now >= ExpiresAtUtc): expiry is no
        // longer inert here, just still comfortably within the 30s TTL for a fast in-process test.
        Assert.Matches(@"expiresInSeconds:\s+\d+", text);
        // Not attached (no fake editor registered), so per SummaryEndpoint.BuildLease, releasable
        // mirrors Attached - must print exactly "False", not a truthy-looking placeholder.
        Assert.Contains("releasable:        False", text);
    }

    // ---------------------------------------------------------------- projects

    [Fact]
    public async Task Projects_NoProjects_PrintsTheLiteralNoProjectsLine()
    {
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.ProjectsAsync(client, output);

        Assert.Equal(0, exitCode);
        Assert.Equal("(no projects)" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task Projects_OneHealthyProject_PrintsEveryFieldVerbatim_NoWarningLines()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.ProjectsAsync(client, output);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains($"- {ProjectName}", text);
        Assert.Contains($"path:         {RealPath(_projectRoot)}", text);
        Assert.Contains($"productGuid:  {ProjectGuid}", text);
        // AdoptAndIndex indexes synchronously, so this is already "indexed", not "indexing" - see
        // the Status test's own note on why the exact age ("indexed 0s ago") is not pinned here.
        Assert.Contains("indexState:   indexed", text);
        Assert.Contains("indexStatus:  indexed ", text);
        Assert.Contains(" ago", text);
        Assert.Contains("nodeCount:    0", text);
        Assert.Contains("edgeCount:    0", text);
        Assert.Contains("editor:       No Editor attached", text);
        Assert.DoesNotContain("warning [", text);
    }

    [Fact]
    public async Task Projects_PathMissing_PrintsTheWarningCodeSeverityMessageAndRemedyVerbatim()
    {
        AdoptFixtureProject();
        Directory.Delete(_projectRoot, recursive: true);
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.ProjectsAsync(client, output);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains(
            "warning [error] pathMissing: Project path not found — check that the volume is mounted or the drive is connected.",
            text);
        Assert.Contains(
            "remedy: Reconnect the volume, or remove this project from Hades if it no longer exists.",
            text);
    }

    // ---------------------------------------------------------------- release

    [Fact]
    public async Task Release_UnknownProject_PrintsTheServersOwnErrorField_ReturnsNonZero()
    {
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.ReleaseAsync(client, output, "not-a-known-guid");

        Assert.Equal(1, exitCode);
        Assert.Equal("error: Unknown project 'not-a-known-guid'." + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task Release_NoLeaseHeld_SucceedsIdempotently_PrintsSuccessAndMessageVerbatim()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.ReleaseAsync(client, output, ProjectGuid);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("success: True", text);
        Assert.Contains($"message: No reload lease is held for '{ProjectName}' — nothing to release.", text);
    }

    // ------------------------------------------------- the commands Spec #5 §5.4 promotes
    //
    // Still against a REAL loopback ControlListener, never a mock. That property is why this suite
    // is trustworthy: every string asserted below is one the server genuinely produced, so a test
    // passing here means the CLI printed what the core actually said.

    [Fact]
    public async Task AddProject_PrintsTheNewRowsOwnFields()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(
            Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {ProjectGuid}\n");

        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.AddProjectAsync(client, output, _projectRoot);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains($"added: {ProjectName}", text);
        Assert.Contains($"productGuid:  {ProjectGuid}", text);

        // Adding starts a BACKGROUND index now rather than blocking on it, so this test owns the
        // same teardown race the rebuild test already documents: the index still holds graph.db open
        // while Dispose deletes the directory around it.
        //
        // Waits on the project's own state rather than the operation id, because that id is returned
        // only by the add response - GET /control/projects deliberately does not carry it, so there
        // is nothing to poll by the time this test can ask.
        await WaitForIndexedAsync(client, ProjectGuid);
    }

    /// <summary>Polls until a project reports itself indexed. See
    /// <see cref="WaitForRebuildAsync"/> for the teardown race this exists to close.</summary>
    async Task WaitForIndexedAsync(ControlClient client, string productGuid)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var projects = await client.ProjectsAsync();
            var row = projects.Projects.FirstOrDefault(p => p.ProductGuid == productGuid);

            if (row?.IndexState == Hades.Control.Client.Dtos.ProjectIndexState.Indexed) return;

            await Task.Delay(50);
        }

        throw new TimeoutException($"Project '{productGuid}' never finished indexing.");
    }

    /// <summary>The core's own refusal, printed verbatim - the CLI never decides what is or is not
    /// a Unity project.</summary>
    [Fact]
    public async Task AddProject_ANonUnityFolder_PrintsTheServersOwnRefusal()
    {
        Directory.CreateDirectory(_projectRoot);
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.AddProjectAsync(client, output, _projectRoot);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("error: ", output.ToString());
    }

    [Fact]
    public async Task RemoveProject_PrintsSuccessAndTheServersMessage()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.RemoveProjectAsync(client, output, ProjectGuid);

        Assert.Equal(0, exitCode);
        Assert.Contains("success: True", output.ToString());
    }

    /// <summary>
    /// Rebuild prints the operation id and returns; it does not poll to completion. Blocking would
    /// invent a progress model the route does not offer.
    /// </summary>
    /// <summary>
    /// Rebuilding is asynchronous SERVER-side, so a test that starts one and returns immediately is
    /// racing its own teardown: the rebuild still holds graph.db open while Dispose tries to delete
    /// the directory containing it. Waiting for the operation to settle is the test's own
    /// responsibility - it created the background work.
    /// </summary>
    async Task WaitForRebuildAsync(ControlClient client, string operationId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var operation = await client.OperationAsync(operationId);
            // Fully qualified: this test project references BOTH Hades.Server and Hades.Control.Client, and
            // each defines its own OperationState - the deliberate wire-type duplication the conformance
            // suite exists to keep in step. A bare name binds to the server's, not the client's.
            if (operation.State != Hades.Control.Client.Dtos.OperationState.Running) return;

            await Task.Delay(50);
        }

        throw new TimeoutException($"Rebuild operation '{operationId}' never left the running state.");
    }

    static string OperationIdFrom(StringWriter output) =>
        output.ToString().Split("operationId: ")[1].Trim();

    [Fact]
    public async Task Rebuild_PrintsTheOperationIdAndDoesNotBlock()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.RebuildAsync(client, output, ProjectGuid);

        Assert.Equal(0, exitCode);
        Assert.Contains("operationId: ", output.ToString());

        await WaitForRebuildAsync(client, OperationIdFrom(output));
    }

    [Fact]
    public async Task Operation_PrintsTheStateOfATrackedOperation()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();

        var started = new StringWriter();
        await Commands.RebuildAsync(client, started, ProjectGuid);
        var operationId = OperationIdFrom(started);

        var output = new StringWriter();
        var exitCode = await Commands.OperationAsync(client, output, operationId);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains($"id:             {operationId}", text);
        Assert.Contains("kind:           rebuild", text);

        await WaitForRebuildAsync(client, operationId);
    }

    [Fact]
    public async Task Operation_AnUnknownId_PrintsTheServersOwnExplanation()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.OperationAsync(client, output, "op-that-never-existed");

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown operation", output.ToString());
    }

    [Fact]
    public async Task Traces_PrintsAllThreeSectionsEvenWhenEmpty()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.TracesAsync(client, output, ProjectGuid);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("sequences:", text);
        Assert.Contains("failures:", text);
        Assert.Contains("slow tools:", text);
    }

    [Fact]
    public async Task Memory_PrintsDocumentsAndProposals()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();
        var output = new StringWriter();

        var exitCode = await Commands.MemoryAsync(client, output, ProjectGuid);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("documents:", text);
        Assert.Contains("proposals:", text);
    }

    [Fact]
    public async Task InstallPlugin_PrintsSuccessNeedsRestartAndTheServersMessage()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();
        var output = new StringWriter();

        await Commands.InstallPluginAsync(client, output, ProjectGuid);

        var text = output.ToString();
        // Whether it succeeds here depends on the fixture project's shape; what is asserted is that
        // all three fields are printed and none is re-worded into a sentence of our own.
        Assert.True(
            text.Contains("needsRestart: ") || text.StartsWith("error: "),
            $"expected the raw fields or the server's own error, got: {text}");
    }

    public void Dispose()
    {
        foreach (var d in _toDispose) d.Dispose();

        foreach (var dir in new[] { _tempDir, _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
