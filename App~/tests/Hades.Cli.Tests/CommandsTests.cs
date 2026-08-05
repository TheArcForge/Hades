using System.Net.Http.Headers;
using Hades.Core;
using Hades.Core.Editors;
using Hades.Core.Storage;
using Hades.Server.Control;

namespace Hades.Cli.Tests;

/// <summary>
/// <see cref="Commands"/> against a real <see cref="ControlListener"/> over real loopback HTTP -
/// the same "direct construction, real socket, no mocking" technique
/// ControlAuthTests/ProjectsEndpointHttpTests already use for this exact listener - never a mocked
/// HttpClient. This is the CLI's whole reason for existing (Plan 11 Task 7): every value asserted
/// below must appear VERBATIM, because <see cref="Commands"/> is only ever supposed to read a JSON
/// field and print it - never format, sum, compare, or map one. A test here that had to compute an
/// expected string from more than one response field would itself be evidence the endpoint under-
/// specifies its surface (see this project's own README-equivalent: Commands' class doc comment).
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

    (ControlListener Listener, HttpClient Client) StartListener()
    {
        var listener = new ControlListener(ConnectionFilePath, projects: _projects, leases: _leases);
        listener.Start();
        _toDispose.Add(listener);

        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{listener.Port}") };
        _toDispose.Add(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", listener.Token);

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
        Assert.Contains("expiresInSeconds:  30", text);
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
        Assert.Contains($"path:         {_projectRoot}", text);
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

    public void Dispose()
    {
        foreach (var d in _toDispose) d.Dispose();

        foreach (var dir in new[] { _tempDir, _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
