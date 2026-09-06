using System.Windows;
using System.Windows.Controls;
using Hades.Control.Client.Dtos;
using Hades.Shell.ViewModels;

namespace Hades.Shell.Sections;

/// <summary>
/// The Asphodel (memory) section.
///
/// The two confirmation dialogs live here rather than in the view model, for the same reason the
/// remove-project dialog does: a view model that opened dialogs could not be tested headlessly, and
/// the `confirmed` flag it takes instead is what makes "never overwrite or delete without asking"
/// provable. Accept and Defer deliberately do not ask - accepting only appends, deferring is
/// bookkeeping - and adding confirmations there would train the user to click through the two that
/// actually matter.
/// </summary>
public partial class MemoryView : UserControl
{
    public MemoryView() => InitializeComponent();

    MemoryViewModel? ViewModel => DataContext as MemoryViewModel;

    static string? FileNameFrom(object sender) => (sender as FrameworkElement)?.Tag as string;

    async void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ProjectRow row) return;
        if (row.ProductGuid == vm.ProjectFilter) return;

        await vm.SelectProjectAsync(row.ProductGuid);
    }

    async void OnDocumentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not MemoryDocumentRow row) return;

        await vm.SelectDocumentAsync(row.Name);
    }

    async void OnAccept(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && FileNameFrom(sender) is { } fileName) await vm.AcceptProposalAsync(fileName);
    }

    async void OnDefer(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && FileNameFrom(sender) is { } fileName) await vm.DeferProposalAsync(fileName);
    }

    async void OnDismiss(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || FileNameFrom(sender) is not { } fileName) return;

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            "Dismiss this proposal? The proposal file is deleted. Your memory documents are not touched.",
            "Dismiss proposal",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        await vm.DismissProposalAsync(fileName, confirmed: answer == MessageBoxResult.OK);
    }

    async void OnSave(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (vm.SelectedDocument.Document is not { } document) return;

        // Named plainly, because this is the one irreversible thing in the section: memory is
        // authored and has no other copy, and the core writes atomically over the old content with
        // no merge and no history.
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Overwrite '{document.Name}'? There is no version history — the current contents are replaced.",
            "Save document",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        await vm.SaveDocumentAsync(document.Name, DocumentEditor.Text, confirmed: answer == MessageBoxResult.OK);
    }
}
