using System.Runtime.InteropServices;
using System.Text.Json;
using Hades.Control.Client;
using Hades.Core;
using Hades.Core.Editors;
using Hades.Core.Projects;
using Hades.Core.Storage;
using Hades.Server.Control;

namespace Hades.Cli.Tests;

/// <summary>
/// <c>hades diagnose</c> is the mitigation for the whole class of environmental failures CI cannot
/// reach - OneDrive placeholders, antivirus locking files, long paths, a Unity Hub somewhere
/// unexpected. For a maintainer who cannot reproduce any of that, one command a reporter can run is
/// worth more than more tests.
///
/// Which makes two properties non-negotiable, and both are pinned below: it must produce a USEFUL
/// report when no core is running (the most likely state when someone runs it in anger), and it must
/// never print a secret, because its output goes straight into bug reports.
/// </summary>
public sealed class DiagnoseTests : IDisposable
{
    const string ProjectGuid = "aaaabbbbccccddddeeeeffff60000006";

    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly List<IDisposable> _toDispose = [];
    readonly LeaseRegistry _leases = new();
    readonly ProjectService _projects;

    string ConnectionFilePath => Path.Combine(_tempDir, "control.token");

    public DiagnoseTests() => _projects = new ProjectService(new AppPaths(_appRoot));

    void AdoptFixtureProject()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "ProjectSettings"));
        File.WriteAllText(
            Path.Combine(_projectRoot, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {ProjectGuid}\n");
        _projects.AdoptAndIndex(_projectRoot);
    }

    (ControlListener Listener, ControlClient Client) StartListener()
    {
        var listener = new ControlListener(ConnectionFilePath, projects: _projects, leases: _leases);
        listener.Start();
        _toDispose.Add(listener);

        return (listener, new ControlClient(new ControlConnection { Port = listener.Port, Token = listener.Token }));
    }

    /// <summary>
    /// The state diagnose is most often run in. It must report the environment rather than refusing
    /// with "no core found" - a reporter whose core will not start is exactly who needs this.
    /// </summary>
    [Fact]
    public async Task Diagnose_ReportsTheEnvironmentEvenWhenNoCoreIsRunning()
    {
        var output = new StringWriter();

        await Commands.DiagnoseAsync(client: null, output, root: "/nonexistent-root");

        var text = output.ToString();
        Assert.Contains("Hades diagnostics", text);
        Assert.Contains("/nonexistent-root", text);
        Assert.Contains(RuntimeInformation.OSDescription, text);
        Assert.Contains("not running", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diagnose_ReportsProcessAndOsArchitecture()
    {
        var output = new StringWriter();

        await Commands.DiagnoseAsync(client: null, output, root: _appRoot);

        var text = output.ToString();
        Assert.Contains(RuntimeInformation.ProcessArchitecture.ToString(), text);
        Assert.Contains(RuntimeInformation.OSArchitecture.ToString(), text);
    }

    [Fact]
    public async Task Diagnose_SaysWhetherTheStorageRootExists()
    {
        Directory.CreateDirectory(_appRoot);
        var present = new StringWriter();
        await Commands.DiagnoseAsync(client: null, present, root: _appRoot);
        Assert.Contains("exists: True", present.ToString());

        var absent = new StringWriter();
        await Commands.DiagnoseAsync(client: null, absent, root: Path.Combine(_appRoot, "definitely-not-here"));
        Assert.Contains("exists: False", absent.ToString());
    }

    /// <summary>
    /// A token that exists and parses is a fact worth reporting. Its CONTENTS are not - see
    /// <see cref="Diagnose_NeverPrintsTheBearerToken"/>.
    /// </summary>
    [Fact]
    public async Task Diagnose_ReportsWhetherTheDiscoveryFileParses()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(ConnectionFilePath, """{"port":1234,"token":"super-secret"}""");

        var output = new StringWriter();
        await Commands.DiagnoseAsync(client: null, output, root: _tempDir);

        var text = output.ToString();
        Assert.Contains("control.token", text);
        Assert.Contains("parses: True", text);
    }

    [Fact]
    public async Task Diagnose_ReportsAMalformedDiscoveryFileAsUnparseable()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(ConnectionFilePath, "this is not json");

        var output = new StringWriter();
        await Commands.DiagnoseAsync(client: null, output, root: _tempDir);

        Assert.Contains("parses: False", output.ToString());
    }

    /// <summary>
    /// THE ONE THAT MATTERS MOST. This output goes into bug reports, pasted into issue trackers by
    /// people who will not read it first. The bearer token grants full control-API access on that
    /// machine, so it must never appear - only whether the file exists and parses.
    /// </summary>
    [Fact]
    public async Task Diagnose_NeverPrintsTheBearerToken()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(ConnectionFilePath, """{"port":1234,"token":"super-secret-token-value"}""");

        var output = new StringWriter();
        await Commands.DiagnoseAsync(client: null, output, root: _tempDir);

        Assert.DoesNotContain("super-secret-token-value", output.ToString());
    }

    [Fact]
    public async Task Diagnose_WithARunningCore_ReportsItsVersionAndUptime()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();
        var output = new StringWriter();

        await Commands.DiagnoseAsync(client, output, root: _tempDir);

        var text = output.ToString();
        Assert.Contains("core:", text);
        Assert.Contains("version:", text);
        Assert.Contains("uptimeSeconds:", text);
        Assert.DoesNotContain("not running", text);
    }

    [Fact]
    public async Task Diagnose_WithARunningCore_ListsProjectsWithTheirPathsAndCounts()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();
        var output = new StringWriter();

        await Commands.DiagnoseAsync(client, output, root: _tempDir);

        var text = output.ToString();
        Assert.Contains(ProjectGuid, text);
        Assert.Contains("nodeCount:", text);
        Assert.Contains("indexState:", text);
    }

    /// <summary>
    /// OneDrive is called out by name because its placeholder files are one of the environmental
    /// failures this command exists for: a path can look ordinary and still not have its contents on
    /// disk. A project not under OneDrive must not be flagged, or the signal is worthless.
    /// </summary>
    [Fact]
    public async Task Diagnose_DoesNotFlagAnOrdinaryPathAsOneDrive()
    {
        AdoptFixtureProject();
        var (_, client) = StartListener();
        var output = new StringWriter();

        await Commands.DiagnoseAsync(client, output, root: _tempDir);

        Assert.Contains("oneDrive:    False", output.ToString());
    }

    [Fact]
    public async Task Diagnose_FlagsAPathContainingOneDrive()
    {
        Assert.True(Commands.LooksLikeOneDrivePath(@"C:\Users\someone\OneDrive\Projects\MyGame"));
        Assert.False(Commands.LooksLikeOneDrivePath(@"C:\Projects\MyGame"));
    }

    /// <summary>Reported even with no core, since a reporter whose core will not start still needs
    /// to know whether the environment is the reason.</summary>
    [Fact]
    public async Task Diagnose_ReportsTheDotnetRuntimeVersion()
    {
        var output = new StringWriter();

        await Commands.DiagnoseAsync(client: null, output, root: _appRoot);

        Assert.Contains(RuntimeInformation.FrameworkDescription, output.ToString());
    }

    public void Dispose()
    {
        foreach (var d in _toDispose) d.Dispose();

        foreach (var dir in new[] { _tempDir, _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
