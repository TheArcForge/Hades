using System.Windows;
using System.Windows.Controls;
using Hades.Shell.ViewModels;

namespace Hades.Shell.Sections;

/// <summary>
/// The Projects section. Every action here is one line: read the productGuid off the button and hand
/// it to <see cref="ProjectsViewModel"/>. Nothing decides anything - eligibility, wording and
/// outcome all belong to the core, and the view model records the server's own message.
///
/// The folder picker and the remove confirmation live here rather than in the view model because
/// both are platform UI: a view model that opened dialogs could not be tested headlessly, which is
/// the whole reason removeProject takes a `confirmed` flag instead of asking the user itself.
/// </summary>
public partial class ProjectsView : UserControl
{
    public ProjectsView() => InitializeComponent();

    ProjectsViewModel? ViewModel => DataContext as ProjectsViewModel;

    static string? GuidFrom(object sender) => (sender as FrameworkElement)?.Tag as string;

    async void OnAddProject(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        // Microsoft.Win32.OpenFolderDialog, in-box since .NET 8 - no WinForms FolderBrowserDialog
        // and no third-party package. This is the one place the shell picks a path; the core decides
        // whether what was picked is actually a Unity project, and says so in its own words.
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a Unity project folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        await vm.AddProjectAsync(dialog.FolderName);
    }

    async void OnRebuild(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && GuidFrom(sender) is { } guid) await vm.RebuildProjectAsync(guid);
    }

    async void OnInstallPlugin(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && GuidFrom(sender) is { } guid) await vm.InstallPluginAsync(guid);
    }

    async void OnReveal(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && GuidFrom(sender) is { } guid) await vm.RevealInExplorerAsync(guid);
    }

    async void OnOpenInUnity(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && GuidFrom(sender) is { } guid) await vm.OpenInUnityAsync(guid);
    }

    async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || GuidFrom(sender) is not { } guid) return;

        // The dialog is what sets `confirmed`. The view model refuses to call the route without it,
        // so a removal can never happen because someone wired a button up carelessly.
        // "Its index is deleted" was WRONG, and measured so: after a remove, the project's graph.db
        // is still there byte for byte - only its project.json is flagged. The CLI's own help
        // carried the identical false claim and was corrected the same day. Promising a deletion
        // that does not happen is the worse direction of the two errors: someone removing a project
        // to reclaim disk space would believe they had.
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            "Remove this project from Hades? Nothing is deleted — the project folder, its indexed "
            + "graph and its authored memory all stay where they are. You can add it back at any time.",
            "Remove project",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        await vm.RemoveProjectAsync(guid, confirmed: answer == MessageBoxResult.OK);
    }
}
