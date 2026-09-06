using System.IO;
using System.Threading;
using System.Windows;
using Hades.Control.Client;
using Hades.Shell.Onboarding;
using Hades.Shell.ShellFacts;
using Hades.Shell.Tray;
using Hades.Shell.ViewModels;
using Hades.Supervision;

namespace Hades.Shell;

/// <summary>
/// The shell's entry point. It renders; the core decides - see Windows/Directory.Build.props for
/// the build-time guard that keeps that true, and Spec #5 §2 for why the rule needs a mechanism
/// here when the shell happens to be written in the same language as the core.
/// </summary>
public partial class App : Application
{
    // macOS gets single-instance free from the bundle model; Windows does not. A second launch
    // ACTIVATES the existing instance rather than exiting silently - a user who double-clicks the
    // installed app expecting it to appear must not be met with nothing happening.
    //
    // Local\ rather than Global\: one running shell PER LOGON SESSION, not per machine. Two users
    // switched between on the same box each get their own core and their own tray icon, and a
    // Global\ name would let whichever logged in first lock the other out of their own app.
    const string InstanceMutexName = @"Local\Hades.Shell.SingleInstance";
    const string ActivationEventName = @"Local\Hades.Shell.Activate";

    Mutex? _instanceMutex;
    EventWaitHandle? _activationEvent;
    RegisteredWaitHandle? _activationWait;
    TrayIcon? _tray;
    CoreSupervisor? _supervisor;
    TrayViewModel? _viewModel;
    readonly LeaseToast _leaseToast = new();
    MainWindow? _mainWindow;
    MainWindowViewModel? _mainWindowViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // BEFORE the tray icon is created, further down. Windows synthesises an AppUserModelID for
        // a NotifyIcon whose process has not declared one, and a synthesised id has no name and no
        // icon to show - which is exactly how this app came to appear in Settings > Notifications
        // as "Hades.Shell" behind a blank blue square. See NotificationIdentity for the other half
        // of the fix, which lives in the installer.
        ShellFacts.NotificationIdentity.Declare();

        // Fluent is garnish, not identity (Spec #5 §5.5): the shell must look acceptable under the
        // default WPF theme, and ThemeMode is applied where it helps without anything depending on
        // it. ThemeMode is [Experimental] in .NET 10 and therefore exempt from .NET's
        // breaking-change policy, with incomplete control coverage - a half-Fluent window is the
        // realistic failure mode, not a broken one.
        //
        // Suppressed HERE, at the single call site, never project-wide: a <NoWarn>WPF0001</NoWarn>
        // would also silence future experimental APIs nobody chose to adopt. And set in code rather
        // than as a XAML attribute on Application, because from XAML the diagnostic is raised inside
        // the generated .g.cs, where no #pragma of ours can live - which would force exactly the
        // project-wide NoWarn this avoids.
#pragma warning disable WPF0001 // ThemeMode is evaluation-only in .NET 10; see Spec #5 §5.5.
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001

        // A named EventWaitHandle rather than a broadcast window message, of the two the plan
        // allows. The message route needs an HWND to broadcast to, and this app deliberately owns
        // no window at startup; the message-only window that would have to be created for it is
        // exactly the kind HWND_BROADCAST does not reach, so the fiddly option is also the one that
        // silently would not work here. The event needs no window and no P/Invoke.
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(OnActivationRequested),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        _tray = new TrayIcon();
        _tray.QuitRequested += (_, _) => Shutdown();
        _tray.OpenRequested += (_, _) => ShowMainWindow();
        _tray.Show();

        StartSupervision();
        ShowOnboardingOnFirstRunOnly();
    }

    /// <summary>
    /// First run only. Shown after supervision has started, not before: the Claude Code step checks
    /// a running core, and putting the window up first would have it fail a check that was only
    /// going to succeed a second later.
    ///
    /// Non-modal, and it does not gate the tray. A user who ignores the window entirely still has a
    /// working Hades sitting in the notification area - which is the same thing the copy promises.
    /// </summary>
    void ShowOnboardingOnFirstRunOnly()
    {
        var store = new FileOnboardingCompletionStore();
        if (store.HasCompletedOnboarding) return;

        // Its own ProjectsViewModel rather than the main window's: that one is built lazily, and
        // onboarding runs before any window has been asked for. They talk to the same core over the
        // same discovery file, so a project added here is simply there when the section opens.
        var projects = new ProjectsViewModel(() => Discovery.Read(ClientPaths.DefaultRoot()));

        new OnboardingWindow(new OnboardingViewModel(new LiveClaudeCodeVerifier(), store, projects)).Show();
    }

    /// <summary>
    /// A second launch signalled us, having found this instance already holding the mutex. It means
    /// the same thing as clicking the tray icon - show me the app - so it raises the window, and the
    /// window appearing IS the feedback. The "Hades is already running" balloon this used to show
    /// existed only while there was no window to raise.
    /// </summary>
    void OnActivationRequested() => ShowMainWindow();

    /// <summary>
    /// The user asked for the main window - from the tray menu, by double-clicking the icon, or by
    /// launching the app a second time. All three mean "show me the app", which is why
    /// <see cref="OnActivationRequested"/> now routes here rather than showing a balloon of its own.
    ///
    /// Created lazily: the app starts with no window at all, matching the Mac's LSUIElement
    /// behaviour, and building one at startup would put a taskbar button there before anyone asked.
    /// </summary>
    void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            var home = ClientPaths.DefaultRoot();

            _mainWindowViewModel = new MainWindowViewModel(_supervisor!);
            var projects = new ProjectsViewModel(() => Discovery.Read(home));
            var traces = new TracesViewModel(() => Discovery.Read(home));
            var memory = new MemoryViewModel(() => Discovery.Read(home));

            // The Run value points at this executable, so a shell moved or reinstalled elsewhere
            // registers its own path rather than inheriting a stale one.
            var settings = new SettingsViewModel(
                () => Discovery.Read(home),
                makeClient: null,
                new LaunchAtLogin(new WindowsStartupRegistry(), Environment.ProcessPath ?? string.Empty),
                new WindowsPowerStatus());

            // The composition root wires which section gets refreshed, so MainWindowViewModel never
            // needs to know what a section IS - only that one is selected. Only the SELECTED
            // section refreshes: an unselected one gets exactly as much polling as a closed window.
            _mainWindowViewModel.RefreshSelectedSection = section => section switch
            {
                Section.Projects => projects.RefreshAsync(),
                Section.Traces => traces.RefreshAsync(),
                Section.Memory => memory.RefreshAsync(),
                Section.Settings => settings.RefreshAsync(),
                _ => Task.CompletedTask,
            };

            _mainWindow = new MainWindow(_mainWindowViewModel, projects, traces, memory, settings);
        }

        _mainWindow.ShowOrActivate();
    }

    /// <summary>
    /// Adopt-or-spawn a core, then keep the tray current from it. The supervisor runs no timer of
    /// its own by design - <see cref="TrayViewModel"/> drives the cadence.
    /// </summary>
    void StartSupervision()
    {
        var home = ClientPaths.DefaultRoot();

        // Installed core beside the shell, else the development `dotnet run` fallback - and it says
        // loudly which one it took, because the fallback silently working is how a broken install
        // gets mistaken for a working one.
        var coreLaunch = CoreLifetime.ResolveForThisBuild();

        _supervisor = new CoreSupervisor(
            new CoreSupervisor.Configuration
            {
                Home = home,
                CoreExecutable = coreLaunch.Executable,
                CoreArguments = coreLaunch.Arguments,
            },
            new Win32CoreProcessHost());

        _viewModel = new TrayViewModel(_supervisor, () => Discovery.Read(home));

        // Marshalled onto the Dispatcher: the poll loop runs on a thread-pool thread, and NotifyIcon
        // is a WinForms component that must be touched from the thread that created it.
        _viewModel.ContentChanged += (_, content) => Dispatcher.BeginInvoke(() =>
        {
            _tray?.Update(_supervisor.State, content.Summary);

            // Evaluated on every tick, but LeaseToast fires at most once per continuous hold. The
            // toast is what makes the lease prominent on Windows: the tray icon is showing
            // `leaseHeld`, but Windows hides tray icons behind the overflow chevron by default, so
            // the icon alone guarantees nothing.
            if (_leaseToast.Evaluate(content.Summary?.Lease) is { } message)
            {
                _tray?.ShowBalloon(message);
            }
        });

        _tray!.MenuOpened += (_, _) => _viewModel?.MenuOpened();
        _tray!.MenuClosed += (_, _) => _viewModel?.MenuClosed();
        _tray!.ReleaseRequested += (_, leaseId) => _ = _viewModel?.ReleaseAsync(leaseId);

        _ = Task.Run(async () =>
        {
            try
            {
                await _supervisor.StartAsync();
            }
            catch (Exception)
            {
                // Adopt-or-spawn failing is a state, not a crash: the supervisor reports it and the
                // tray renders it. A missing core\ directory - which is every developer build, since
                // §8.1 only ships one inside the installed layout - lands here, and the shell must
                // still come up and still adopt a core someone started by hand.
            }

            await _viewModel!.BootstrapAsync();
            _viewModel.StartPolling();
        });
    }

    static void ActivateExistingInstance()
    {
        // TryOpenExisting rather than a plain open: the first instance can die between our failed
        // mutex acquisition and this line, and a second launch racing a quitting one must not
        // crash on the way out.
        if (!EventWaitHandle.TryOpenExisting(ActivationEventName, out var handle)) return;

        using (handle) handle.Set();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Unregister BEFORE disposing the event: the callback marshals onto the Dispatcher, which
        // is shutting down, and a wait still armed against a disposed handle is the other half of
        // the same race.
        _activationWait?.Unregister(waitObject: null);
        _activationEvent?.Dispose();

        // Stop polling before the tray goes, so an in-flight tick cannot publish onto a disposed
        // NotifyIcon on its way out.
        _viewModel?.Dispose();
        _mainWindowViewModel?.Dispose();

        // StopAsync, not fire-and-forget: for a SPAWNED core this is what takes it down with us,
        // and the ownership footer promised the user exactly that. An adopted core is deliberately
        // left running by StopAsync itself.
        try
        {
            _supervisor?.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Quitting must not fail. The Job Object is the backstop if this did not get there.
        }

        _tray?.Dispose();
        _instanceMutex?.Dispose();

        base.OnExit(e);
    }
}
