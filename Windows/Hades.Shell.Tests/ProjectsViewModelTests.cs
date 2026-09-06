using Hades.Control.Client;
using Hades.Control.Client.Dtos;
using Hades.Shell.ViewModels;

namespace Hades.Shell.Tests;

/// <summary>
/// The Projects section's behaviour, behind a fake client - mirroring how the Swift side fakes
/// ControlProjectsFetching. The interesting cases are not "does it fetch" but what it does when a
/// call FAILS, because those are the paths a user hits during a core restart or a pruned operation.
/// </summary>
public class ProjectsViewModelTests
{
    sealed class FakeProjectsClient : IProjectsClient
    {
        public Func<Task<ProjectsResult>> OnProjects { get; set; } =
            () => Task.FromResult(new ProjectsResult { Projects = [] });

        public Func<string, Task<ProjectRow>>? OnAdd { get; set; }
        public Func<string, Task<ActionResult>>? OnRemove { get; set; }
        public Func<string, Task<RebuildStartedResult>>? OnRebuild { get; set; }
        public Func<string, Task<InstallPluginResult>>? OnInstallPlugin { get; set; }
        public Func<string, Task<ActionResult>>? OnReveal { get; set; }
        public Func<string, Task<ActionResult>>? OnOpenInUnity { get; set; }
        public Func<string, Task<OperationResult>>? OnOperation { get; set; }

        public int RemoveCalls { get; private set; }
        public int OperationCalls { get; private set; }
        public string? LastAddedPath { get; private set; }

        public Task<ProjectsResult> ProjectsAsync() => OnProjects();

        public Task<ProjectRow> AddProjectAsync(string path)
        {
            LastAddedPath = path;
            return OnAdd?.Invoke(path) ?? Task.FromResult(Row("added", "abc"));
        }

        public Task<ActionResult> RemoveProjectAsync(string productGuid)
        {
            RemoveCalls++;
            return OnRemove?.Invoke(productGuid) ?? Task.FromResult(Action("removed"));
        }

        public Task<RebuildStartedResult> RebuildProjectAsync(string productGuid) =>
            OnRebuild?.Invoke(productGuid) ?? Task.FromResult(new RebuildStartedResult { OperationId = "op-1" });

        public Task<InstallPluginResult> InstallPluginAsync(string productGuid) =>
            OnInstallPlugin?.Invoke(productGuid)
            ?? Task.FromResult(new InstallPluginResult { Success = true, NeedsRestart = false, Message = "installed" });

        public Task<ActionResult> RevealInFinderAsync(string productGuid) =>
            OnReveal?.Invoke(productGuid) ?? Task.FromResult(Action("revealed"));

        public Task<ActionResult> OpenInUnityAsync(string productGuid) =>
            OnOpenInUnity?.Invoke(productGuid) ?? Task.FromResult(Action("opened"));

        public Task<OperationResult> OperationAsync(string id)
        {
            OperationCalls++;
            return OnOperation?.Invoke(id) ?? Task.FromResult(Operation(OperationState.Running));
        }
    }

    static ActionResult Action(string message) => new() { Success = true, Message = message };

    static ProjectRow Row(string name, string productGuid) => new()
    {
        Name = name,
        Path = @"C:\Projects\" + name,
        ProductGuid = productGuid,
        UnityVersion = "2022.3.10f1",
        IndexState = ProjectIndexState.Indexed,
        IndexStatus = "Indexed, 1204 nodes",
        NodeCount = 1204,
        EdgeCount = 3820,
        Editor = new ProjectEditorInfo { State = ProjectEditorState.Attached, Status = "Editor attached" },
        Warnings = [],
    };

    static OperationResult Operation(OperationState state) => new()
    {
        Id = "op-1",
        Kind = "rebuild",
        State = state,
        StartedAtUtc = DateTimeOffset.UnixEpoch,
        ElapsedSeconds = 5,
    };

    static readonly ControlConnection AConnection = new() { Port = 1234, Token = "t" };

    static (ProjectsViewModel Vm, FakeProjectsClient Client) NewSubject()
    {
        var client = new FakeProjectsClient();
        return (new ProjectsViewModel(() => AConnection, _ => client), client);
    }

    // ---- refresh ------------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_PopulatesRows()
    {
        var (vm, client) = NewSubject();
        client.OnProjects = () => Task.FromResult(new ProjectsResult { Projects = [Row("MyGame", "abc")] });

        await vm.RefreshAsync();

        Assert.Single(vm.Projects);
        Assert.Equal("MyGame", vm.Projects[0].Name);
    }

    /// <summary>
    /// One unlucky poll must not flash a project list already on screen back to empty. Self-heals
    /// on the next tick, exactly as the tray's own loop does.
    /// </summary>
    [Fact]
    public async Task Refresh_FailureKeepsTheRowsAlreadyOnScreen()
    {
        var (vm, client) = NewSubject();
        client.OnProjects = () => Task.FromResult(new ProjectsResult { Projects = [Row("MyGame", "abc")] });
        await vm.RefreshAsync();

        client.OnProjects = () => throw new ControlClientException(ControlClientError.Transport, "gone");
        await vm.RefreshAsync();

        Assert.Single(vm.Projects);
    }

    [Fact]
    public async Task Refresh_WithNoConnection_DoesNothing()
    {
        var client = new FakeProjectsClient();
        var vm = new ProjectsViewModel(() => null, _ => client);

        await vm.RefreshAsync();

        Assert.Empty(vm.Projects);
    }

    // ---- action messages ----------------------------------------------------------------------

    /// <summary>
    /// A failed action records THE SERVER'S OWN message, never text invented here.
    /// </summary>
    [Fact]
    public async Task AFailedAction_RecordsTheServersOwnMessage()
    {
        var (vm, client) = NewSubject();
        client.OnRemove = _ => throw new ControlClientException(
            ControlClientError.Server, "Project is still indexing; try again shortly.", statusCode: 409);

        await vm.RemoveProjectAsync("abc", confirmed: true);

        Assert.Equal("Project is still indexing; try again shortly.", vm.LastActionMessage);
    }

    /// <summary>
    /// A transport or stale-token failure has no server text to show, so the previous message is
    /// left exactly as it was rather than cleared or replaced with something made up here.
    /// </summary>
    [Theory]
    [InlineData(ControlClientError.Transport)]
    [InlineData(ControlClientError.StaleToken)]
    [InlineData(ControlClientError.Decoding)]
    public async Task AFailureWithNoServerText_LeavesTheLastMessageAlone(ControlClientError error)
    {
        var (vm, client) = NewSubject();
        client.OnReveal = _ => Task.FromResult(Action("Revealed in Explorer."));
        await vm.RevealInExplorerAsync("abc");

        client.OnRemove = _ => throw new ControlClientException(error, "some client-side detail");
        await vm.RemoveProjectAsync("abc", confirmed: true);

        Assert.Equal("Revealed in Explorer.", vm.LastActionMessage);
    }

    [Fact]
    public async Task SuccessfulActions_RecordTheServersMessageVerbatim()
    {
        var (vm, client) = NewSubject();
        client.OnInstallPlugin = _ => Task.FromResult(new InstallPluginResult
        {
            Success = true,
            NeedsRestart = true,
            Message = "Plugin installed. Restart Unity to pick it up.",
        });

        await vm.InstallPluginAsync("abc");

        Assert.Equal("Plugin installed. Restart Unity to pick it up.", vm.LastActionMessage);
    }

    // ---- add ----------------------------------------------------------------------------------

    /// <summary>
    /// Add answers a bare ProjectRow with no message field: the row appearing IS the feedback, so
    /// no success text is invented. And it refreshes EXPLICITLY rather than waiting for a tick -
    /// onboarding drives no tick at all, so an add there would otherwise complete server-side and
    /// leave "No projects yet" on screen, the success real and entirely invisible.
    /// </summary>
    [Fact]
    public async Task AddProject_RefreshesImmediately_AndInventsNoSuccessText()
    {
        var (vm, client) = NewSubject();
        client.OnProjects = () => Task.FromResult(new ProjectsResult { Projects = [Row("MyGame", "abc")] });

        await vm.AddProjectAsync(@"C:\Projects\MyGame");

        Assert.Equal(@"C:\Projects\MyGame", client.LastAddedPath);
        Assert.Single(vm.Projects);
        Assert.Null(vm.LastActionMessage);
    }

    /// <summary>
    /// A previous failure's text must not outlive the success that replaced it - otherwise "not a
    /// Unity project" stays on screen above the row that just added fine.
    /// </summary>
    [Fact]
    public async Task AddProject_ClearsAnEarlierFailureMessage()
    {
        var (vm, client) = NewSubject();
        client.OnAdd = _ => throw new ControlClientException(
            ControlClientError.Server, "That folder is not a Unity project.", statusCode: 400);
        await vm.AddProjectAsync(@"C:\not-unity");
        Assert.Equal("That folder is not a Unity project.", vm.LastActionMessage);

        client.OnAdd = null;
        await vm.AddProjectAsync(@"C:\Projects\MyGame");

        Assert.Null(vm.LastActionMessage);
    }

    // ---- remove -------------------------------------------------------------------------------

    /// <summary>
    /// confirmed is the gate itself, not a hint: false never reaches the network at all. This is
    /// what makes "never remove without confirming" provable here rather than merely trusted of
    /// whatever calls it.
    /// </summary>
    [Fact]
    public async Task RemoveProject_WithoutConfirmation_NeverReachesTheNetwork()
    {
        var (vm, client) = NewSubject();

        await vm.RemoveProjectAsync("abc", confirmed: false);

        Assert.Equal(0, client.RemoveCalls);
        Assert.Null(vm.LastActionMessage);
    }

    [Fact]
    public async Task RemoveProject_WhenConfirmed_CallsTheRouteAndRecordsTheMessage()
    {
        var (vm, client) = NewSubject();
        client.OnRemove = _ => Task.FromResult(Action("Removed 'MyGame'."));

        await vm.RemoveProjectAsync("abc", confirmed: true);

        Assert.Equal(1, client.RemoveCalls);
        Assert.Equal("Removed 'MyGame'.", vm.LastActionMessage);
    }

    // ---- rebuild and operation polling --------------------------------------------------------

    /// <summary>
    /// Rebuild registers the operation id for refresh to poll; it does not poll it itself, matching
    /// the "never starts a timer of its own" discipline every view model here holds to.
    /// </summary>
    [Fact]
    public async Task RebuildProject_TracksTheOperation_ButDoesNotPollItItself()
    {
        var (vm, client) = NewSubject();

        await vm.RebuildProjectAsync("abc");

        Assert.Equal(0, client.OperationCalls);
        Assert.Empty(vm.RebuildProgress);
    }

    [Fact]
    public async Task Refresh_PollsATrackedRebuildAndPublishesItsState()
    {
        var (vm, client) = NewSubject();
        client.OnOperation = _ => Task.FromResult(Operation(OperationState.Running));
        await vm.RebuildProjectAsync("abc");

        await vm.RefreshAsync();

        var progress = vm.RebuildProgress["abc"];
        Assert.Equal(OperationProgressKind.Tracked, progress.Kind);
        Assert.Equal(OperationState.Running, progress.Result!.State);
    }

    /// <summary>
    /// A terminal state stops the tracking. Otherwise every finished rebuild would be re-polled
    /// forever, and eventually answer 404 once the registry pruned it.
    /// </summary>
    [Fact]
    public async Task Refresh_StopsPollingOnceTheOperationIsDone()
    {
        var (vm, client) = NewSubject();
        client.OnOperation = _ => Task.FromResult(Operation(OperationState.Done));
        await vm.RebuildProjectAsync("abc");

        await vm.RefreshAsync();
        var callsAfterFirst = client.OperationCalls;
        await vm.RefreshAsync();

        Assert.Equal(1, callsAfterFirst);
        Assert.Equal(callsAfterFirst, client.OperationCalls);
        Assert.Equal(OperationState.Done, vm.RebuildProgress["abc"].Result!.State);
    }

    /// <summary>
    /// A 404 is an ORDINARY outcome, not a failure: the registry keeps completed operations for
    /// five minutes, so a rebuild that finished a while ago is simply gone. The server's own
    /// explanation is carried verbatim rather than papered over.
    /// </summary>
    [Fact]
    public async Task Refresh_TreatsAPrunedOperationAsAnOrdinaryOutcome()
    {
        var (vm, client) = NewSubject();
        client.OnOperation = _ => throw new ControlClientException(
            ControlClientError.Server,
            "Unknown operation 'op-1'. It may have completed and been pruned, or the id is wrong.",
            statusCode: 404);
        await vm.RebuildProjectAsync("abc");

        await vm.RefreshAsync();
        var callsAfterFirst = client.OperationCalls;
        await vm.RefreshAsync();

        var progress = vm.RebuildProgress["abc"];
        Assert.Equal(OperationProgressKind.Pruned, progress.Kind);
        Assert.StartsWith("Unknown operation 'op-1'.", progress.Message);
        Assert.Equal(callsAfterFirst, client.OperationCalls); // stopped tracking
    }

    /// <summary>
    /// A transient failure keeps tracking and leaves the last known progress alone - the next tick
    /// retries. Only a terminal state or a 404 stops it.
    /// </summary>
    [Fact]
    public async Task Refresh_KeepsTrackingThroughATransientOperationFailure()
    {
        var (vm, client) = NewSubject();
        client.OnOperation = _ => Task.FromResult(Operation(OperationState.Running));
        await vm.RebuildProjectAsync("abc");
        await vm.RefreshAsync();

        client.OnOperation = _ => throw new ControlClientException(ControlClientError.Transport, "blip");
        await vm.RefreshAsync();

        Assert.Equal(OperationProgressKind.Tracked, vm.RebuildProgress["abc"].Kind);

        client.OnOperation = _ => Task.FromResult(Operation(OperationState.Done));
        await vm.RefreshAsync();

        Assert.Equal(OperationState.Done, vm.RebuildProgress["abc"].Result!.State);
    }

    /// <summary>
    /// A new rebuild must not show the previous one's frozen state while it is still unpolled.
    /// </summary>
    [Fact]
    public async Task RebuildProject_ClearsAStaleProgressEntryForThatProject()
    {
        var (vm, client) = NewSubject();
        client.OnOperation = _ => Task.FromResult(Operation(OperationState.Done));
        await vm.RebuildProjectAsync("abc");
        await vm.RefreshAsync();
        Assert.NotEmpty(vm.RebuildProgress);

        await vm.RebuildProjectAsync("abc");

        Assert.Empty(vm.RebuildProgress);
    }
}
