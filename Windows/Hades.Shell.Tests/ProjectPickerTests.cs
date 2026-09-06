using Hades.Control.Client.Dtos;
using Hades.Shell.ViewModels;

namespace Hades.Shell.Tests;

/// <summary>
/// Guards the Charon and Asphodel project pickers, which rendered EMPTY.
///
/// Both view models republished <c>KnownProjects</c> every poll with freshly deserialised rows.
/// Replacing a ComboBox's ItemsSource clears its SelectedValue, and the binding is OneWay, so
/// nothing ever pushed it back - the picker went blank on the first refresh and stayed blank. The
/// defaulting logic was never at fault; both view models set ProjectFilter to the first known
/// project, matching the Mac.
/// </summary>
public class ProjectPickerTests
{
    static ProjectRow Project(string guid, string name, string indexStatus = "indexed 5s ago") => new()
    {
        Name = name,
        Path = "/tmp/" + name,
        ProductGuid = guid,
        IndexState = ProjectIndexState.Indexed,
        IndexStatus = indexStatus,
        NodeCount = 1,
        EdgeCount = 1,
        Editor = new ProjectEditorInfo { State = ProjectEditorState.Absent, Status = "No editor" },
        Warnings = [],
    };

    [Fact]
    public void IdenticalListsAreTheSame()
    {
        Assert.True(ProjectPicker.SameProjects(
            [Project("g-1", "Alpha"), Project("g-2", "Beta")],
            [Project("g-1", "Alpha"), Project("g-2", "Beta")]));
    }

    /// <summary>
    /// THE CASE THAT MAKES THE FIX WORK AT ALL. Most of ProjectRow is volatile by design -
    /// IndexStatus is a relative-time sentence that differs on almost every tick. A whole-record
    /// comparison would therefore report "changed" continuously and republish anyway, leaving the
    /// picker exactly as empty as before while looking like a fix.
    /// </summary>
    [Fact]
    public void AChangedIndexStatusIsNotAPickerChange()
    {
        Assert.True(ProjectPicker.SameProjects(
            [Project("g-1", "Alpha", "indexed 5s ago")],
            [Project("g-1", "Alpha", "indexed 6s ago")]));
    }

    [Fact]
    public void ARenamedProjectIsAChange_BecauseThePickerShowsTheName()
    {
        Assert.False(ProjectPicker.SameProjects(
            [Project("g-1", "Alpha")],
            [Project("g-1", "Renamed")]));
    }

    [Fact]
    public void ADifferentProjectIsAChange()
    {
        Assert.False(ProjectPicker.SameProjects([Project("g-1", "Alpha")], [Project("g-9", "Alpha")]));
    }

    [Fact]
    public void AnAddedOrRemovedProjectIsAChange()
    {
        Assert.False(ProjectPicker.SameProjects(
            [Project("g-1", "Alpha")],
            [Project("g-1", "Alpha"), Project("g-2", "Beta")]));

        Assert.False(ProjectPicker.SameProjects(
            [Project("g-1", "Alpha"), Project("g-2", "Beta")],
            [Project("g-1", "Alpha")]));
    }

    /// <summary>Order is part of it: the picker defaults to the FIRST project, so a reorder changes
    /// which one it lands on.</summary>
    [Fact]
    public void AReorderIsAChange()
    {
        Assert.False(ProjectPicker.SameProjects(
            [Project("g-1", "Alpha"), Project("g-2", "Beta")],
            [Project("g-2", "Beta"), Project("g-1", "Alpha")]));
    }

    [Fact]
    public void TwoEmptyListsAreTheSame() => Assert.True(ProjectPicker.SameProjects([], []));
}
