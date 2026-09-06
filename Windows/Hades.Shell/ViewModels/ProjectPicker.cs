using Hades.Control.Client.Dtos;

namespace Hades.Shell.ViewModels;

/// <summary>
/// Shared by the Charon and Asphodel project pickers, which have the same list and the same bug.
/// </summary>
internal static class ProjectPicker
{
    /// <summary>
    /// Whether two project lists are the same AS A PICKER SEES THEM - same projects, same order,
    /// same displayed names.
    ///
    /// <para><b>Why this exists.</b> Both view models republished <c>KnownProjects</c> on every poll
    /// with freshly deserialised rows. Replacing a ComboBox's ItemsSource clears its
    /// <c>SelectedValue</c>, and the binding is <c>OneWay</c>, so nothing ever pushed the value back
    /// - the picker rendered EMPTY from the first refresh onward, in both sections. The view models
    /// were doing their part correctly all along: each defaults <c>ProjectFilter</c> to the first
    /// known project, matching the Mac. The selection was being thrown away downstream.</para>
    ///
    /// <para><b>Why only two fields, and why comparing them all would be a bug.</b> The picker binds
    /// <c>ProductGuid</c> as the value and <c>Name</c> as the label; nothing else it shows can
    /// change. Most of the rest of <see cref="ProjectRow"/> is volatile by design -
    /// <c>IndexStatus</c> is a relative-time sentence ("indexed 5s ago") that differs on almost every
    /// tick - so a whole-record comparison would report "changed" continuously and republish anyway,
    /// leaving the picker exactly as empty as before while looking like a fix.</para>
    /// </summary>
    internal static bool SameProjects(IReadOnlyList<ProjectRow> current, IReadOnlyList<ProjectRow> incoming)
    {
        if (current.Count != incoming.Count) return false;

        for (var i = 0; i < current.Count; i++)
        {
            if (current[i].ProductGuid != incoming[i].ProductGuid || current[i].Name != incoming[i].Name)
            {
                return false;
            }
        }

        return true;
    }
}
