using System.ComponentModel;
using System.Runtime.CompilerServices;
using Hades.Supervision;

namespace Hades.Shell.ViewModels;

/// <summary>
/// Owns navigation and the polling LIFECYCLE for the main window - nothing else. The port of
/// <c>Mac/HadesApp/Sources/HadesApp/MainWindow/MainWindowViewModel.swift</c>.
///
/// Two things: which sidebar <see cref="Section"/> is selected, and the poll loop that runs only
/// while the window is open. It deliberately holds no Projects/Traces/Memory data of its own and
/// never will - each section gets its own view model owning its own fetch (Task 7 onwards). Keeping
/// that split here, rather than letting this grow a property per section as those tasks land, is
/// what stops it becoming a god object.
///
/// NO DISPATCHER, deliberately: everything here is plain state and async, so the tests need no STA
/// apartment. Marshalling to the UI thread is the view layer's job - see App.xaml.cs, which is the
/// only place that does it.
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    readonly ICoreSupervisor _supervisor;
    readonly TimeSpan _pollInterval;

    CancellationTokenSource? _poll;
    Section _selectedSection = Section.Projects;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindowViewModel(ICoreSupervisor supervisor, TimeSpan? pollInterval = null)
    {
        _supervisor = supervisor;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>Sidebar order, and therefore the order the user sees. Projects first because the
    /// window opens there; Settings last because that is where a Windows nav pane puts it.</summary>
    public IReadOnlyList<Section> Sections { get; } =
        [Section.Projects, Section.Traces, Section.Memory, Section.Settings];

    /// <summary>
    /// Settable so the sidebar's SelectedItem can bind two-way. <see cref="Select"/> is the same
    /// thing by another name, kept because the Mac reference has it and reads better from code.
    /// </summary>
    public Section SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (_selectedSection == value) return;

            _selectedSection = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Called once per tick with whichever section is CURRENTLY selected - never all four, so an
    /// unselected section gets exactly as much background polling as a closed window does: none.
    /// Null until a section view model wires it (Task 7 onwards); not a bindable property, because
    /// nothing renders it.
    /// </summary>
    public Func<Section, Task>? RefreshSelectedSection { get; set; }

    public void Select(Section section) => SelectedSection = section;

    /// <summary>
    /// Opening the window always lands on Projects, however the user left it last time. Mirrors
    /// MainWindowScene.show()'s own behaviour.
    /// </summary>
    public void PrepareToShow() => Select(Section.Projects);

    /// <summary>One tick: re-validate the supervisor, then refresh the selected section.</summary>
    public async Task RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        await _supervisor.RefreshAsync(cancellationToken).ConfigureAwait(false);

        if (RefreshSelectedSection is { } refresh)
        {
            await refresh(SelectedSection).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts the poll loop. Idempotent - the window can be shown while already shown. A closed
    /// window has no business polling, which is why this is tied to visibility rather than started
    /// once at launch; the supervisor runs no timer of its own, so callers drive the cadence.
    /// </summary>
    public void StartPolling()
    {
        if (_poll is not null) return;

        _poll = new CancellationTokenSource();
        var token = _poll.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RefreshOnceAsync(token).ConfigureAwait(false);
                    await Task.Delay(_pollInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // A section's own fetch failing is that section's business to surface, not a
                    // reason to stop the window polling. Same swallow-and-retry contract the tray's
                    // loop already follows.
                }
            }
        }, token);
    }

    public void StopPolling()
    {
        _poll?.Cancel();
        _poll?.Dispose();
        _poll = null;
    }

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose() => StopPolling();
}
