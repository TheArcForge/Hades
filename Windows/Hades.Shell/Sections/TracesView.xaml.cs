using System.Windows;
using System.Windows.Controls;
using Hades.Control.Client.Dtos;
using Hades.Shell.ViewModels;

namespace Hades.Shell.Sections;

/// <summary>
/// The Charon (traces) section. Like ProjectsView, every handler here is one line: read what was
/// picked and hand it to <see cref="TracesViewModel"/>. No filtering, sorting or re-wording happens
/// in this file - the server owns all three, and re-filtering the sequences list client-side would
/// corrupt the grouping the server did before filtering.
/// </summary>
public partial class TracesView : UserControl
{
    public TracesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    TracesViewModel? ViewModel => DataContext as TracesViewModel;

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TracesViewModel previous) previous.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is TracesViewModel next) next.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Re-selects the sequence the user picked, after a refresh replaced the list under it.
    ///
    /// <para>The view model hands over a brand-new list of rows on every poll, so the selected
    /// instance stops being in the collection and WPF clears the selection - measured at under three
    /// seconds, which is short enough that the list deselects itself while you are still reading it.
    /// Matching by id restores it. THIS BELONGS IN THE VIEW: the fix needs the Dispatcher (the poll
    /// runs on a background thread), and this shell's rule is that view models never touch it, so
    /// tests need no STA apartment. Reconciling the collection inside the view model was tried and
    /// threw from the wrong thread, emptying the whole section.</para>
    ///
    /// <para>Setting SelectedItem re-raises SelectionChanged, which calls back into
    /// <see cref="TracesViewModel.SelectSequenceAsync"/> - harmless, because that short-circuits on
    /// an unchanged id rather than re-resolving every call in the sequence again.</para>
    /// </summary>
    void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TracesViewModel.Sequences)) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (ViewModel is not { SelectedSequenceId: { } id } vm) return;
            if (SequencesList.SelectedItem is TraceSequenceRow current && current.Id == id) return;

            foreach (var row in vm.Sequences)
            {
                if (row.Id != id) continue;

                SequencesList.SelectedItem = row;
                return;
            }
        });
    }

    async void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ProjectRow row) return;

        // Guard against the ComboBox raising this while the binding is settling on the value the
        // view model already holds - that would clear the user's selected trace for no reason.
        if (row.ProductGuid == vm.ProjectFilter) return;

        await vm.SelectProjectAsync(row.ProductGuid);
    }

    async void OnApplyFilters(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var outcome = (OutcomeFilterBox.SelectedItem as ComboBoxItem)?.Tag as string;

        await vm.ApplyFiltersAsync(
            tool: ToolFilterBox.Text,
            // Empty means "any", which the API expresses by the parameter being absent.
            outcome: string.IsNullOrWhiteSpace(outcome) ? null : outcome,
            minDurationMs: null,
            maxDurationMs: null);
    }

    /// <summary>
    /// Selecting a SEQUENCE shows no span detail, deliberately - there is none to show, because a
    /// sequence is several calls and the detail pane describes one.
    ///
    /// <para>This used to drill into <c>TraceIds[0]</c>. A sequence is marked failed when ANY call
    /// in it failed, so the normal case was a row carrying the error glyph whose detail pane then
    /// reported <c>ok</c> - the first call having succeeded - while the calls that actually failed
    /// could not be reached from this tab at all. The pane is cleared instead, and every call is
    /// reachable by expanding the row (see OnSequenceCallClicked). Same shape as the Mac, whose
    /// sequences list binds no selection and expands into per-call buttons.</para>
    /// </summary>
    async void OnSequenceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not TraceSequenceRow row) return;

        await vm.SelectSequenceAsync(row);
    }

    /// <summary>One call in the selected sequence's breakdown. The id rides on the button's Tag,
    /// carried there by the SequenceCallRow the pane is bound to, so this handler does no index
    /// arithmetic against the parallel arrays - that pairing is made once, in
    /// TracesViewModel.SelectSequenceAsync, where the calls are resolved.</summary>
    async void OnSequenceCallClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (sender is not Button { Tag: string traceId } || string.IsNullOrEmpty(traceId)) return;

        await vm.SelectTraceAsync(traceId);
    }

    async void OnFailureSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not FailedCallRow row) return;

        await vm.SelectTraceAsync(row.TraceId);
    }
}
