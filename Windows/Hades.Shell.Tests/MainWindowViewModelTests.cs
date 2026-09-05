using Hades.Shell.ViewModels;

namespace Hades.Shell.Tests;

public class MainWindowViewModelTests
{
    static MainWindowViewModel NewSubject(FakeSupervisor? supervisor = null) =>
        new(supervisor ?? new FakeSupervisor());

    [Fact]
    public void OpensOnProjects()
    {
        Assert.Equal(Section.Projects, NewSubject().SelectedSection);
    }

    /// <summary>
    /// Charon and Asphodel are product names, not decoration, and are deliberately NOT to be
    /// renamed to generic labels. The enum cases stay Traces/Memory because that is what the
    /// control API speaks; these titles are what users read.
    /// </summary>
    [Fact]
    public void SectionTitlesMatchTheProductVocabulary()
    {
        Assert.Equal("Projects", Section.Projects.Title());
        Assert.Equal("Charon", Section.Traces.Title());
        Assert.Equal("Asphodel", Section.Memory.Title());
        Assert.Equal("Settings", Section.Settings.Title());
    }

    [Fact]
    public void EverySectionHasATitle()
    {
        foreach (var section in Enum.GetValues<Section>())
        {
            Assert.False(string.IsNullOrWhiteSpace(section.Title()));
        }
    }

    [Fact]
    public void SelectingASectionChangesTheSelection()
    {
        var vm = NewSubject();

        vm.Select(Section.Memory);

        Assert.Equal(Section.Memory, vm.SelectedSection);
    }

    /// <summary>
    /// The sidebar binds to this, so the order is what the user sees. Projects first because the
    /// window opens there; Settings last because Windows puts it at the bottom of a nav pane.
    /// </summary>
    [Fact]
    public void SectionsAreOfferedInSidebarOrder()
    {
        Assert.Equal(
            new[] { Section.Projects, Section.Traces, Section.Memory, Section.Settings },
            NewSubject().Sections);
    }

    [Fact]
    public void ChangingSelectionRaisesPropertyChanged()
    {
        var vm = NewSubject();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Select(Section.Traces);

        Assert.Contains(nameof(MainWindowViewModel.SelectedSection), raised);
    }

    [Fact]
    public void SelectingTheSameSectionAgainRaisesNothing()
    {
        var vm = NewSubject();
        vm.Select(Section.Traces);

        var raised = 0;
        vm.PropertyChanged += (_, _) => raised++;
        vm.Select(Section.Traces);

        Assert.Equal(0, raised);
    }

    /// <summary>
    /// Opening the window always lands on Projects, however the user left it. Mirrors
    /// MainWindowScene.show()'s own behaviour.
    /// </summary>
    [Fact]
    public void PreparingToShowResetsToProjects()
    {
        var vm = NewSubject();
        vm.Select(Section.Settings);

        vm.PrepareToShow();

        Assert.Equal(Section.Projects, vm.SelectedSection);
    }

    /// <summary>
    /// Only the SELECTED section is refreshed - never all four. An unselected section gets exactly
    /// as much background polling as a closed window does: none.
    /// </summary>
    [Fact]
    public async Task RefreshingRefreshesOnlyTheSelectedSection()
    {
        var supervisor = new FakeSupervisor();
        var vm = new MainWindowViewModel(supervisor);
        var refreshed = new List<Section>();
        vm.RefreshSelectedSection = section => { refreshed.Add(section); return Task.CompletedTask; };

        vm.Select(Section.Memory);
        await vm.RefreshOnceAsync();

        Assert.Equal([Section.Memory], refreshed);
        Assert.Equal(1, supervisor.RefreshCount);
    }

    /// <summary>
    /// A closed window has no business polling, so the loop is started and stopped with visibility.
    /// Both are idempotent, because the window can be shown while already shown.
    /// </summary>
    [Fact]
    public void StartAndStopPollingAreIdempotent()
    {
        var vm = NewSubject();

        vm.StopPolling();
        vm.StartPolling();
        vm.StartPolling();
        vm.StopPolling();
        vm.StopPolling();

        vm.Dispose();
    }

    [Fact]
    public async Task RefreshWithNoSectionHandlerStillRefreshesTheSupervisor()
    {
        var supervisor = new FakeSupervisor();
        var vm = new MainWindowViewModel(supervisor);

        await vm.RefreshOnceAsync();

        Assert.Equal(1, supervisor.RefreshCount);
    }
}
