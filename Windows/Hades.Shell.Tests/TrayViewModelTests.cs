using Hades.Control.Client;
using Hades.Control.Client.Dtos;
using Hades.Shell.Tray;
using Hades.Supervision;

namespace Hades.Shell.Tests;

/// <summary>
/// Drives every behaviour MenuBarViewModel.swift's doc comments name as load-bearing, without a real
/// process or a real network call. The ones that matter are not "does it fetch" but what it does
/// when fetching FAILS - those are the paths a user hits during a core restart.
/// </summary>
public class TrayViewModelTests
{
    sealed class FakeClient : ISummaryClient
    {
        public Func<Task<SummaryResult>> OnSummary { get; set; } = () => Task.FromResult(SummaryFixture.Idle());
        public int ReleaseCount { get; private set; }
        public string? LastReleasedLeaseId { get; private set; }
        public Func<Task<ActionResult>>? OnRelease { get; set; }

        public Task<SummaryResult> SummaryAsync() => OnSummary();

        public Task<ActionResult> ReleaseLeaseAsync(string leaseId)
        {
            ReleaseCount++;
            LastReleasedLeaseId = leaseId;
            return OnRelease?.Invoke()
                   ?? Task.FromResult(new ActionResult { Success = true, Message = "released" });
        }
    }

    static readonly ControlConnection AConnection = new() { Port = 9999, Token = "t" };

    static (TrayViewModel Vm, FakeSupervisor Sup, FakeClient Client) NewSubject(
        ControlConnection? connection = null)
    {
        var supervisor = new FakeSupervisor();
        var client = new FakeClient();
        var vm = new TrayViewModel(
            supervisor,
            discover: () => connection ?? AConnection,
            makeClient: _ => client);

        return (vm, supervisor, client);
    }

    [Fact]
    public async Task NotRunningSupervisor_ResolvesToNotRunning_AndNeverFetches()
    {
        var (vm, sup, client) = NewSubject();
        sup.State = SupervisorState.NotStarted;
        var fetched = false;
        client.OnSummary = () => { fetched = true; return Task.FromResult(SummaryFixture.Idle()); };

        await vm.BootstrapAsync();

        Assert.Equal(MenuContentKind.NotRunning, vm.Content.Kind);
        Assert.False(fetched, "there is no core to ask when the supervisor is not running");
    }

    [Fact]
    public async Task RunningSupervisor_PublishesTheFetchedSummary()
    {
        var (vm, sup, client) = NewSubject();
        sup.State = SupervisorState.Running(Ownership.Spawned);
        client.OnSummary = () => Task.FromResult(SummaryFixture.WithProject("MyGame", "Indexed, 7 nodes"));

        await vm.BootstrapAsync();

        Assert.Equal(MenuContentKind.Running, vm.Content.Kind);
        Assert.Equal("Indexed, 7 nodes", vm.Content.Summary!.Rows[0].Status);
        Assert.Equal(Ownership.Spawned, vm.Content.Ownership);
    }

    /// <summary>
    /// Every fetch failure is swallowed and leaves the last good summary in place. A core that
    /// blips for one tick must not flash the tray back to "not running" while the supervisor still
    /// says it is up.
    /// </summary>
    [Fact]
    public async Task AFailedFetch_KeepsTheLastGoodSummary()
    {
        var (vm, sup, client) = NewSubject();
        sup.State = SupervisorState.Running(Ownership.Spawned);
        client.OnSummary = () => Task.FromResult(SummaryFixture.WithProject("MyGame", "Indexed, 7 nodes"));
        await vm.BootstrapAsync();

        client.OnSummary = () => throw new HttpRequestException("core went away mid-poll");
        await vm.BootstrapAsync();

        Assert.Equal(MenuContentKind.Running, vm.Content.Kind);
        Assert.Equal("Indexed, 7 nodes", vm.Content.Summary!.Rows[0].Status);
    }

    /// <summary>
    /// The precondition MenuContent.Resolve documents: a summary from a core that has since died
    /// must never survive into a later Running. Without the clear, the tray would show a dead
    /// core's projects the moment a new one came up, before it had answered anything.
    /// </summary>
    [Fact]
    public async Task LeavingRunning_ClearsTheSummary_SoItCannotReappearLater()
    {
        var (vm, sup, client) = NewSubject();
        sup.State = SupervisorState.Running(Ownership.Spawned);
        client.OnSummary = () => Task.FromResult(SummaryFixture.WithProject("Old", "stale"));
        await vm.BootstrapAsync();

        sup.State = SupervisorState.Failed(3);
        await vm.BootstrapAsync();
        Assert.Equal(MenuContentKind.Failed, vm.Content.Kind);

        // Back to Running, but the fetch fails, so nothing fresh arrives.
        sup.State = SupervisorState.Running(Ownership.Spawned);
        client.OnSummary = () => throw new HttpRequestException("not up yet");
        await vm.BootstrapAsync();

        Assert.Equal(MenuContentKind.NotRunning, vm.Content.Kind);
        Assert.Null(vm.Content.Summary);
    }

    /// <summary>
    /// An unreadable discovery file while the supervisor still reports Running keeps the last
    /// summary, rather than clearing it. The core writes that file, and catching it mid-write is
    /// ordinary.
    /// </summary>
    [Fact]
    public async Task AnUnreadableDiscoveryFile_DoesNotClearTheSummary()
    {
        var supervisor = new FakeSupervisor { State = SupervisorState.Running(Ownership.Spawned) };
        var client = new FakeClient
        {
            OnSummary = () => Task.FromResult(SummaryFixture.WithProject("MyGame", "Indexed, 7 nodes")),
        };
        ControlConnection? connection = AConnection;
        var vm = new TrayViewModel(supervisor, discover: () => connection, makeClient: _ => client);

        await vm.BootstrapAsync();
        connection = null;
        await vm.BootstrapAsync();

        Assert.Equal(MenuContentKind.Running, vm.Content.Kind);
        Assert.Equal("Indexed, 7 nodes", vm.Content.Summary!.Rows[0].Status);
    }

    /// <summary>
    /// Discovery is re-read on EVERY tick and never cached. That alone is the whole stale-token
    /// recovery story: a core that restarts on a new port rewrites the file, and the next tick
    /// picks it up with no special-case code.
    /// </summary>
    [Fact]
    public async Task DiscoveryIsReReadEveryTick()
    {
        var supervisor = new FakeSupervisor { State = SupervisorState.Running(Ownership.Spawned) };
        var reads = 0;
        var vm = new TrayViewModel(
            supervisor,
            discover: () => { reads++; return AConnection; },
            makeClient: _ => new FakeClient());

        await vm.BootstrapAsync();
        await vm.BootstrapAsync();
        await vm.BootstrapAsync();

        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task Release_CallsTheRouteThenRefreshes()
    {
        var (vm, sup, client) = NewSubject();
        sup.State = SupervisorState.Running(Ownership.Spawned);

        await vm.ReleaseAsync("lease-1");

        Assert.Equal(1, client.ReleaseCount);
        Assert.Equal("lease-1", client.LastReleasedLeaseId);
        Assert.Equal(MenuContentKind.Running, vm.Content.Kind);
    }

    /// <summary>
    /// A failing Release is never surfaced: it is idempotent and safe to call late, so a lease whose
    /// TTL already fired is an ordinary outcome, not an error worth showing anyone.
    /// </summary>
    [Fact]
    public async Task Release_SwallowsErrors_AndStillRefreshes()
    {
        var (vm, sup, client) = NewSubject();
        sup.State = SupervisorState.Running(Ownership.Spawned);
        client.OnRelease = () => throw new HttpRequestException("gone");

        await vm.ReleaseAsync("lease-1");

        Assert.Equal(MenuContentKind.Running, vm.Content.Kind);
        Assert.Equal(1, sup.RefreshCount); // the tick after Release still ran
    }

    [Fact]
    public async Task ContentChanged_FiresWithWhatWasPublished()
    {
        var (vm, sup, _) = NewSubject();
        sup.State = SupervisorState.Running(Ownership.Adopted);
        MenuContent? seen = null;
        vm.ContentChanged += (_, c) => seen = c;

        await vm.BootstrapAsync();

        Assert.NotNull(seen);
        Assert.Equal(vm.Content, seen!.Value);
    }

    [Fact]
    public void StartPollingIsIdempotent_AndStopIsSafeWhenNotPolling()
    {
        var (vm, _, _) = NewSubject();

        vm.StopPolling();
        vm.StartPolling();
        vm.StartPolling();
        vm.StopPolling();
        vm.StopPolling();

        vm.Dispose();
    }
}
