using System.Windows;
using System.Windows.Controls;
using Hades.Control.Client.Dtos;
using System.Windows.Threading;

namespace Hades.Shell.Onboarding;

/// <summary>
/// The first-run walkthrough. One window that re-renders per step rather than four separate views:
/// each step is a title, a paragraph, and its own action panel, so four XAML files would be four
/// copies of the same layout.
/// </summary>
public partial class OnboardingWindow : Window
{
    readonly OnboardingViewModel _viewModel;

    public OnboardingWindow(OnboardingViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Render();
    }

    void Render()
    {
        var step = _viewModel.CurrentStep;
        var position = Array.IndexOf(OnboardingViewModel.AllSteps, step) + 1;

        StepTitle.Text = step.Title();

        // Counted from the sequence itself. The Mac hardcoded its step count in prose and it went
        // stale the moment a platform had a different number of them.
        StepCounter.Text = $"Step {position} of {OnboardingViewModel.AllSteps.Length}";
        StepCopy.Text = OnboardingViewModel.CopyFor(step);

        // Switched on the ACTION rather than on the step, so a step that gains an action cannot be
        // given copy promising it and no panel to deliver it - which is exactly how the Projects and
        // Unity-plugin steps shipped with instructions and no controls.
        var action = OnboardingViewModel.ActionFor(step);
        ClaudeCodePanel.Visibility = Show(action, OnboardingAction.VerifyClaudeCode);
        ProjectsPanel.Visibility = Show(action, OnboardingAction.AddProject);
        UnityPluginPanel.Visibility = Show(action, OnboardingAction.InstallPlugin);

        if (action == OnboardingAction.InstallPlugin) ShowProjectsToInstallInto();

        NextButton.Content = position == OnboardingViewModel.AllSteps.Length ? "Finish" : "Next";
    }

    /// <summary>
    /// Lists every known project, each with its own button. One nameless "Install plugin" could not
    /// answer "into which?" - and with more than one project added, that is the only question that
    /// matters.
    ///
    /// <para><b>It FETCHES rather than reads what it happens to hold.</b> Onboarding's
    /// <see cref="ProjectsViewModel"/> starts empty and is only filled as a side effect of adding,
    /// so anyone who already had projects - or who added one, went back, and came forward again -
    /// was told "no projects yet" while two sat registered. There is no poll tick in this window to
    /// fill it later, which is the same gap that made the Projects step's own add invisible.</para>
    /// </summary>
    async void ShowProjectsToInstallInto()
    {
        RenderProjectList();
        await _viewModel.Projects.RefreshAsync();

        // Still the plugin step? Advancing during the fetch would otherwise repopulate a hidden list.
        if (_viewModel.CurrentAction == OnboardingAction.InstallPlugin) RenderProjectList();
    }

    void RenderProjectList()
    {
        var projects = _viewModel.Projects.Projects;

        // Rebuilt from the server rows, but carrying over what has already been installed in this
        // session - otherwise a refresh would quietly turn every "âœ“ Installed" back into a button.
        var installed = _pluginRows.Where(r => r.Installed).Select(r => r.ProductGuid).ToHashSet(StringComparer.Ordinal);

        _pluginRows = [.. projects.Select(p => new PluginRow(p) { Installed = installed.Contains(p.ProductGuid) })];

        PluginProjectsList.ItemsSource = _pluginRows;
        PluginProjectsList.Visibility = _pluginRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoProjectsToInstallInto.Visibility = _pluginRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    List<PluginRow> _pluginRows = [];

    static Visibility Show(OnboardingAction actual, OnboardingAction wanted) =>
        actual == wanted ? Visibility.Visible : Visibility.Collapsed;

    async void OnAddProject(object sender, RoutedEventArgs e)
    {
        // Microsoft.Win32.OpenFolderDialog, the same in-box picker the Projects section uses. The
        // core decides whether the folder is really a Unity project and says so in its own words.
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a Unity project folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true) return;

        var folderName = System.IO.Path.GetFileName(dialog.FolderName.TrimEnd('\\', '/'));

        AddProjectButton.IsEnabled = false;
        AddProjectProgress.Visibility = Visibility.Visible;
        SetNavigationEnabled(false);

        // Elapsed seconds only until the server has something better to say. The add itself now
        // returns as soon as the project is registered, so this covers the brief adopt call; the
        // real file counts come from polling the indexing operation below.
        var ticking = StartElapsedTicker(
            seconds => AddProjectResult.Text = $"Adding {folderName} — {seconds}s elapsed.");

        try
        {
            var operationId = await _viewModel.Projects.AddProjectAsync(dialog.FolderName);

            ticking.Dispose();

            if (_viewModel.Projects.LastActionMessage is { } refusal)
            {
                // The server's own refusal, verbatim. Never reworded here.
                AddProjectResult.Text = refusal;
                return;
            }

            AddedProjectsList.ItemsSource = _viewModel.Projects.Projects;

            if (operationId is null)
            {
                AddProjectResult.Text = "Added.";
                return;
            }

            await FollowIndexingAsync(operationId);
            await _viewModel.Projects.RefreshAsync();
            AddedProjectsList.ItemsSource = _viewModel.Projects.Projects;
        }
        finally
        {
            ticking.Dispose();
            AddProjectProgress.Visibility = Visibility.Collapsed;
            AddProjectButton.IsEnabled = true;
            SetNavigationEnabled(true);
        }
    }

    /// <summary>
    /// Polls the indexing operation and shows what it reports — "Scripts: 1,240 of 3,410 files".
    ///
    /// <para>The text is the server's own, formatted once in
    /// <c>Hades.Core.Indexing.IndexProgressUpdate.Format</c> and passed through untouched, so the
    /// shell never invents a number or a unit. Where the server has not reported yet, elapsed
    /// seconds stand in rather than a blank line.</para>
    /// </summary>
    async Task FollowIndexingAsync(string operationId)
    {
        var started = DateTime.UtcNow;

        while (true)
        {
            var operation = await _viewModel.Projects.OperationAsync(operationId);

            // Unknown or unreachable: the add already succeeded, so this stops quietly rather than
            // reporting a failure that did not happen.
            if (operation is null) { AddProjectResult.Text = "Added."; return; }

            if (operation.State != OperationState.Running)
            {
                AddProjectResult.Text = operation.State == OperationState.Failed
                    ? operation.Error ?? "Indexing failed."
                    : "Added and indexed.";
                return;
            }

            AddProjectResult.Text = operation.Progress
                ?? $"Indexing — {(int)(DateTime.UtcNow - started).TotalSeconds}s elapsed.";

            await Task.Delay(250);
        }
    }

    /// <summary>
    /// Skip and Next while work is in flight. Advancing mid-add would leave the step that owns the
    /// operation torn down while its await is still outstanding, and Skip would close the window
    /// out from under it.
    /// </summary>
    void SetNavigationEnabled(bool enabled)
    {
        SkipButton.IsEnabled = enabled;
        NextButton.IsEnabled = enabled;
    }

    /// <summary>Reports elapsed seconds once a second until disposed. On the UI thread by
    /// construction - <see cref="DispatcherTimer"/> ticks there - so handlers may touch controls.</summary>
    static IDisposable StartElapsedTicker(Action<int> report)
    {
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };

        timer.Tick += (_, _) => report((int)(DateTime.UtcNow - started).TotalSeconds);
        timer.Start();
        report(0);

        return new Stopper(timer);
    }

    sealed class Stopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }

    async void OnInstallPlugin(object sender, RoutedEventArgs e)
    {
        // The button carries its own row's productGuid, so which project this installs into is never
        // inferred from "the last one added".
        if (sender is not Button { Tag: string productGuid }) return;

        var button = (Button)sender;
        var row = _pluginRows.FirstOrDefault(r => r.ProductGuid == productGuid);

        button.IsEnabled = false;
        InstallPluginProgress.Visibility = Visibility.Visible;
        InstallPluginResult.Text = $"Installing into {row?.Name ?? "the project"}…";
        SetNavigationEnabled(false);

        try
        {
            var succeeded = await _viewModel.Projects.InstallPluginAsync(productGuid);

            InstallPluginResult.Text = _viewModel.Projects.LastActionMessage ?? "Plugin installed.";

            // Marked done only on a reported success. A row flipping to "âœ“ Installed" on a refusal
            // would be the worst of both - wrong, and reassuring.
            if (row is not null) row.Installed = succeeded;
        }
        finally
        {
            InstallPluginProgress.Visibility = Visibility.Collapsed;
            button.IsEnabled = true;
            SetNavigationEnabled(true);
        }
    }

    async void OnCheckClaudeCode(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        CheckResult.Text = "Checking…";

        await _viewModel.VerifyClaudeCodeAsync();

        var verification = _viewModel.ClaudeCodeVerification;

        // The wording is careful on purpose: the check proves the CORE is serving tools, not that
        // Claude Code has connected. Saying the latter would claim something never verified.
        CheckResult.Text = verification.Kind switch
        {
            ClaudeCodeVerificationKind.Reachable =>
                $"Hades is running and serving {verification.ToolCount} tools. "
                + "Whether Claude Code has picked them up is something only Claude Code can tell you.",
            _ => "Could not reach Hades. It may still be starting - try again in a moment.",
        };

        CheckButton.IsEnabled = true;
    }

    void OnNext(object sender, RoutedEventArgs e)
    {
        _viewModel.Advance();

        if (_viewModel.IsFinished) Close();
        else Render();
    }

    void OnSkip(object sender, RoutedEventArgs e)
    {
        _viewModel.Skip();
        Close();
    }
}

