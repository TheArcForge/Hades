using System.ComponentModel;
using System.Windows;
using Hades.Shell.ViewModels;

namespace Hades.Shell;

/// <summary>
/// The main window. Owns no state: everything it renders comes from
/// <see cref="MainWindowViewModel"/>, and everything that view model renders comes from the core.
/// </summary>
public partial class MainWindow : Window
{
    readonly MainWindowViewModel _viewModel;

    /// <summary>
    /// The Projects section's own view model. Held on the window rather than on
    /// <see cref="MainWindowViewModel"/>, which owns navigation and the poll lifecycle and
    /// deliberately holds no section data - composing the sections is the view layer's job.
    /// </summary>
    public ProjectsViewModel Projects { get; }

    /// <summary>The Charon (traces) section's own view model, held here for the same reason.</summary>
    public TracesViewModel Traces { get; }

    /// <summary>The Asphodel (memory) section's own view model, likewise.</summary>
    public MemoryViewModel Memory { get; }

    /// <summary>The Settings section's own view model, likewise.</summary>
    public SettingsViewModel Settings { get; }

    public MainWindow(
        MainWindowViewModel viewModel,
        ProjectsViewModel projects,
        TracesViewModel traces,
        MemoryViewModel memory,
        SettingsViewModel settings)
    {
        _viewModel = viewModel;
        Projects = projects;
        Traces = traces;
        Memory = memory;
        Settings = settings;
        DataContext = viewModel;

        InitializeComponent();
    }

    /// <summary>
    /// Shows the window, or raises it if it is already open. Always lands on Projects, and starts
    /// the poll loop - a closed window does not poll.
    /// </summary>
    public void ShowOrActivate()
    {
        _viewModel.PrepareToShow();

        Show();

        // A window hidden while minimised comes back minimised otherwise, which reads as the app
        // ignoring the click that asked for it.
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
        _viewModel.StartPolling();
    }

    /// <summary>
    /// Closing HIDES; only the tray's Quit ends the process.
    ///
    /// This is not merely a convenience. The app owns the Job Object handle that a SPAWNED core is
    /// assigned to, and kill-on-close means the core dies when the last handle closes - so letting
    /// WPF treat the window's close button as "exit the application" would kill a core mid-index
    /// because someone dismissed a window. On macOS the LSUIElement model gives this for free; here
    /// it has to be said out loud. App.xaml's ShutdownMode=OnExplicitShutdown is the other half.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();

        // Stop polling on the way down for the same reason ShowOrActivate starts it: a hidden
        // window is a closed window as far as the core is concerned.
        _viewModel.StopPolling();

        base.OnClosing(e);
    }
}
