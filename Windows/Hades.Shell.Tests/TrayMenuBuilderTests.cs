using Hades.Shell.Tray;
using Hades.Supervision;

namespace Hades.Shell.Tests;

/// <summary>
/// The tray menu is the densest safety surface in the app, and these tests are the only thing that
/// keeps it carrying what the Mac popover carries. The reference is
/// Mac/HadesApp/Sources/HadesApp/Views/MenuBarContentView.swift, and the rule it obeys is that every
/// string the core can author is printed VERBATIM - the shell never re-words, re-filters or
/// re-orders it.
///
/// Note on construction: the plan's draft of this file wrote `new SupervisorState.Restarting(3)`.
/// SupervisorState is a readonly record struct with static factories, not a hierarchy of nested
/// types, so the calls below are `SupervisorState.Restarting(3)`. Same states, real API.
/// </summary>
public class TrayMenuBuilderTests
{
    [Fact]
    public void NotRunning_SaysSo_AndOffersNoProjectRows()
    {
        var items = TrayMenuBuilder.Build(SupervisorState.NotStarted, summary: null);

        Assert.Contains(items, i => i.Text.Contains("not running", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(items, i => i.Text == "Open Hades");
        Assert.Contains(items, i => i.Text == "Quit Hades");
    }

    [Fact]
    public void Restarting_ReportsTheAttemptNumber()
    {
        var items = TrayMenuBuilder.Build(SupervisorState.Restarting(3), summary: null);

        Assert.Contains(items, i => i.Text.Contains("3"));
    }

    [Fact]
    public void Failed_ReportsHowManyAttemptsWereMade()
    {
        var items = TrayMenuBuilder.Build(SupervisorState.Failed(5), summary: null);

        Assert.Contains(items, i => i.Text.Contains("5"));
    }

    // The ownership footer is a safety statement, not decoration: it is the only place the user
    // learns whether quitting will stop a core that something else may be using.
    [Fact]
    public void Adopted_SaysQuittingLeavesTheCoreRunning()
    {
        var items = TrayMenuBuilder.Build(
            SupervisorState.Running(Ownership.Adopted), SummaryFixture.Idle());

        Assert.Contains(items, i => i.Text == "Adopted — quitting Hades leaves it running");
    }

    [Fact]
    public void Spawned_SaysQuittingStopsTheCore()
    {
        var items = TrayMenuBuilder.Build(
            SupervisorState.Running(Ownership.Spawned), SummaryFixture.Idle());

        Assert.Contains(items, i => i.Text == "Started by this app — quitting stops it");
    }

    // "The shell renders, the core decides": every project row's status text comes from the core
    // verbatim. The shell never re-words it.
    [Fact]
    public void ProjectRowsRenderTheCoresOwnStatusTextVerbatim()
    {
        var summary = SummaryFixture.WithProject(name: "MyGame", status: "Indexed, 1204 nodes");
        var items = TrayMenuBuilder.Build(SupervisorState.Running(Ownership.Spawned), summary);

        Assert.Contains(items, i => i.Text.Contains("Indexed, 1204 nodes"));
    }

    [Fact]
    public void TheHeadlineIsRenderedVerbatim()
    {
        var summary = SummaryFixture.Idle();
        var items = TrayMenuBuilder.Build(SupervisorState.Running(Ownership.Spawned), summary);

        Assert.Contains(items, i => i.Text == summary.Headline);
    }

    /// <summary>
    /// A Running supervisor that has never had a successful /control/summary must render as
    /// not-running, not as an empty Running menu. This is MenuBarContent.resolve's `guard let
    /// lastSummary` branch, and getting it wrong shows the user a blank menu for a core that is
    /// merely still starting up.
    /// </summary>
    [Fact]
    public void RunningWithNoSummaryYet_RendersAsNotRunning()
    {
        var items = TrayMenuBuilder.Build(SupervisorState.Running(Ownership.Spawned), summary: null);

        Assert.Contains(items, i => i.Text.Contains("not running", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.Text.Contains("quitting", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Starting is a distinct supervisor state with no core to ask yet, and the Mac collapses it
    /// into notRunning rather than inventing a fifth thing to display.
    /// </summary>
    [Fact]
    public void Starting_RendersAsNotRunning()
    {
        var items = TrayMenuBuilder.Build(SupervisorState.Starting, summary: null);

        Assert.Contains(items, i => i.Text.Contains("not running", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Rows are keyed by productGuid, never by the display name: two checkouts of the same project
    /// share a name, and keying on name collided them into one row. Both must survive into the menu.
    /// </summary>
    [Fact]
    public void TwoProjectsSharingADisplayNameBothAppear()
    {
        var baseline = SummaryFixture.WithProject(name: "MyGame", status: "Indexed, 10 nodes");
        var second = baseline.Rows[0] with { ProductGuid = "0000ffff0000ffff0000ffff0000ffff", Status = "Indexed, 20 nodes" };
        var summary = baseline with { Rows = [baseline.Rows[0], second] };

        var items = TrayMenuBuilder.Build(SupervisorState.Running(Ownership.Spawned), summary);

        Assert.Contains(items, i => i.Text.Contains("Indexed, 10 nodes"));
        Assert.Contains(items, i => i.Text.Contains("Indexed, 20 nodes"));
    }

    /// <summary>
    /// Open Hades and Quit Hades are always available, in every state - the Mac draws them outside
    /// the switch entirely. Opening the window or quitting must not depend on a core being up.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryState))]
    public void OpenAndQuitAreAvailableInEveryState(SupervisorState state, bool withSummary)
    {
        var items = TrayMenuBuilder.Build(state, withSummary ? SummaryFixture.Idle() : null);

        Assert.Contains(items, i => i.Text == "Open Hades");
        Assert.Contains(items, i => i.Text == "Quit Hades");
    }

    // ---- Task 5: the lease line and Release -------------------------------------------------
    //
    // Spec #3 §3.1 makes the lease indicator "deliberately prominent" as net #7 of the
    // reload-safety design: a user must never be confused about why their code stopped compiling.
    //
    // The plan's draft asserted items[0] contains the LEASE ID. That is not what the Mac renders
    // and not what a user can use: MenuBarContentView draws summary.headline first (for a held
    // lease the core writes it about the lease) and puts Release directly beneath it, and a real
    // leaseId is a hex GUID - printing one as the first line of a tray menu is noise, not
    // prominence. What is pinned instead: the lease block sits immediately after the headline and
    // BEFORE any project rows, and it names the project holding the lease, verbatim.

    [Fact]
    public void AHeldLeaseIsSurfacedAboveTheProjectRows()
    {
        var summary = SummaryFixture.WithHeldLease(leaseId: "hades-script-editing", releasable: true);
        var items = TrayMenuBuilder.Build(SupervisorState.Running(Ownership.Spawned), summary).ToList();

        var release = items.FindIndex(i => i.Text == "Release");
        var firstRow = items.FindIndex(i => i.Text == summary.Rows[0].Status);

        Assert.True(release >= 0, "a held lease must offer Release");
        Assert.True(firstRow >= 0, "the fixture has a project row");
        Assert.True(release < firstRow, "the lease must be reachable before the project list");
    }

    [Fact]
    public void TheLeaseLineNamesTheProjectHoldingIt()
    {
        var summary = SummaryFixture.WithHeldLease("id", releasable: true, project: "Hades-Unity-Client");
        var items = TrayMenuBuilder.Build(SupervisorState.Running(Ownership.Spawned), summary);

        Assert.Contains(items, i => i.Text.Contains("Hades-Unity-Client"));
    }

    [Fact]
    public void ReleaseIsOfferedWhenTheLeaseIsReleasable()
    {
        var summary = SummaryFixture.WithHeldLease(leaseId: "hades-script-editing", releasable: true);
        var items = TrayMenuBuilder.Build(SupervisorState.Running(Ownership.Spawned), summary);

        var release = Assert.Single(items, i => i.Text == "Release");
        Assert.True(release.Enabled);
    }

    // Disabled, not hidden: the user still needs to see that a lease is held and that releasing it
    // is not currently possible - exactly what the Mac's .disabled(!lease.releasable) does.
    [Fact]
    public void ReleaseIsShownButDisabledWhenTheLeaseIsNotReleasable()
    {
        var summary = SummaryFixture.WithHeldLease(leaseId: "hades-script-editing", releasable: false);
        var items = TrayMenuBuilder.Build(SupervisorState.Running(Ownership.Spawned), summary);

        var release = Assert.Single(items, i => i.Text == "Release");
        Assert.False(release.Enabled);
    }

    [Fact]
    public void NoLeaseMeansNoReleaseItem()
    {
        var items = TrayMenuBuilder.Build(SupervisorState.Running(Ownership.Spawned), SummaryFixture.Idle());

        Assert.DoesNotContain(items, i => i.Text == "Release");
    }

    /// <summary>
    /// Release must act on the lease the menu was built from. Passing the wrong id releases someone
    /// else's lease, or nothing at all, and the user is left staring at an Editor that still will
    /// not compile.
    /// </summary>
    [Fact]
    public void ReleaseCarriesTheLeaseIdItWasBuiltWith()
    {
        string? released = null;
        var summary = SummaryFixture.WithHeldLease(leaseId: "lease-abc", releasable: true);

        var items = TrayMenuBuilder.Build(
            SupervisorState.Running(Ownership.Spawned),
            summary,
            new TrayMenuActions { OnRelease = id => released = id });

        Assert.Single(items, i => i.Text == "Release").Action!();

        Assert.Equal("lease-abc", released);
    }

    public static TheoryData<SupervisorState, bool> EveryState() => new()
    {
        { SupervisorState.NotStarted, false },
        { SupervisorState.Starting, false },
        { SupervisorState.Restarting(2), false },
        { SupervisorState.Failed(4), false },
        { SupervisorState.Running(Ownership.Adopted), true },
        { SupervisorState.Running(Ownership.Spawned), true },
    };
}
