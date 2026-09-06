using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Hades.Control.Client;
using Hades.Shell.ViewModels;

namespace Hades.Shell.Onboarding;

/// <summary>
/// Whether first-run onboarding has already completed. An app-owned preference with no counterpart
/// in the control API - the core has no notion of "has this installation been onboarded". Behind an
/// interface so tests never touch persistent per-user state, which would leak between runs on the
/// same machine.
/// </summary>
public interface IOnboardingCompletionStore
{
    bool HasCompletedOnboarding { get; }
    void MarkCompleted();
}

/// <summary>
/// The real store: a small JSON file under the application-data root. Not unit tested - one line
/// each way, the same allowance the other OS-touching seams have.
///
/// A file rather than the registry, deliberately: this is app state, it belongs beside the rest of
/// Hades' per-user data, and uninstalling by deleting the folder should take it with it.
/// </summary>
public sealed class FileOnboardingCompletionStore(string? root = null) : IOnboardingCompletionStore
{
    readonly string _path = Path.Combine(root ?? ClientPaths.DefaultRoot(), "onboarding.json");

    public bool HasCompletedOnboarding
    {
        get
        {
            try
            {
                if (!File.Exists(_path)) return false;

                using var document = JsonDocument.Parse(File.ReadAllText(_path));
                return document.RootElement.TryGetProperty("completed", out var completed) && completed.GetBoolean();
            }
            catch (Exception)
            {
                // Missing, unreadable or malformed all mean the same thing: show onboarding. Being
                // shown it twice is a far smaller cost than never being shown it at all.
                return false;
            }
        }
    }

    public void MarkCompleted()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, """{"completed":true}""");
        }
        catch (Exception)
        {
            // Failing to record it means onboarding appears again next launch - mildly annoying,
            // and not worth interrupting a first run over.
        }
    }
}

/// <summary>
/// First-run onboarding: which step you are on, and the one live check the flow can honestly make.
/// Step state is view state, which is why this type is allowed to own it.
/// </summary>
public sealed class OnboardingViewModel(
    IClaudeCodeVerifier verifier,
    IOnboardingCompletionStore store,
    ProjectsViewModel projects)
    : INotifyPropertyChanged
{
    /// <summary>
    /// The Projects and Unity-plugin steps' actions, exposed rather than wrapped — the same call the
    /// Projects section makes, so onboarding cannot drift into a second, subtly different way to add
    /// a project. <see cref="ProjectsViewModel.AddProjectAsync"/> already refreshes explicitly for
    /// this exact caller: its own doc comment notes that onboarding drives no poll tick, so an add
    /// here would otherwise succeed server-side and show nothing.
    /// </summary>
    public ProjectsViewModel Projects { get; } = projects;

    /// <summary>What <paramref name="step"/> lets the user do. See <see cref="OnboardingAction"/>
    /// for why this is data rather than markup.</summary>
    public static OnboardingAction ActionFor(OnboardingStep step) => step switch
    {
        OnboardingStep.ClaudeCode => OnboardingAction.VerifyClaudeCode,
        OnboardingStep.Projects => OnboardingAction.AddProject,
        OnboardingStep.UnityPlugin => OnboardingAction.InstallPlugin,
        _ => OnboardingAction.None,
    };

    /// <summary>The action for the step being shown.</summary>
    public OnboardingAction CurrentAction => ActionFor(CurrentStep);

    /// <summary>
    /// The four Windows steps, in fixed order. <see cref="OnboardingStep.Permissions"/> is absent on
    /// purpose - see that type's own doc comment.
    /// </summary>
    public static readonly OnboardingStep[] AllSteps =
    [
        OnboardingStep.Install,
        OnboardingStep.ClaudeCode,
        OnboardingStep.Projects,
        OnboardingStep.UnityPlugin,
    ];

    int _index;
    bool _isFinished;
    ClaudeCodeVerification _claudeCodeVerification = ClaudeCodeVerification.NotVerified;

    public event PropertyChangedEventHandler? PropertyChanged;

    public OnboardingStep CurrentStep => AllSteps[Math.Min(_index, AllSteps.Length - 1)];

    public bool IsFinished
    {
        get => _isFinished;
        private set { _isFinished = value; OnPropertyChanged(); }
    }

    public ClaudeCodeVerification ClaudeCodeVerification
    {
        get => _claudeCodeVerification;
        private set { _claudeCodeVerification = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Moves to the next step, completing once it moves past the last one.
    ///
    /// Completion is never gated on anything the Unity plugin step does: that step is an upgrade,
    /// and a user who stops before it already has a working Hades.
    /// </summary>
    public void Advance()
    {
        if (_index >= AllSteps.Length - 1)
        {
            Finish();
            return;
        }

        _index++;
        OnPropertyChanged(nameof(CurrentStep));

        // CurrentAction is derived from CurrentStep, so it changes with it. A binding on a derived
        // property that never raises is the classic way a view stops updating for one control only.
        OnPropertyChanged(nameof(CurrentAction));
    }

    /// <summary>Leaves onboarding early. Recorded as completed - a user who chose to skip should not
    /// be asked again on every launch.</summary>
    public void Skip() => Finish();

    public async Task VerifyClaudeCodeAsync()
    {
        ClaudeCodeVerification = ClaudeCodeVerification.Verifying;
        ClaudeCodeVerification = await verifier.VerifyAsync().ConfigureAwait(false);
    }

    void Finish()
    {
        _index = AllSteps.Length - 1;
        store.MarkCompleted();
        IsFinished = true;
        OnPropertyChanged(nameof(CurrentStep));
    }

    /// <summary>
    /// Each step's body text.
    ///
    /// AUTHORED COPY, NOT PORTED. The Mac's install step says "…five steps, and you can stop after
    /// the fourth with a fully working setup", which is Swift-authored text rather than anything the
    /// API serves - and it is wrong here, because Windows has four. Copying it across would have the
    /// app confidently state a number that does not match what the user is looking at. The counts
    /// below are pinned by tests for exactly that reason.
    /// </summary>
    public static string CopyFor(OnboardingStep step) => step switch
    {
        OnboardingStep.Install =>
            "Hades is installed and running in your notification area. There are four steps, and you "
            + "can stop after the third with a fully working setup - the last one is an upgrade.",

        OnboardingStep.ClaudeCode =>
            "Hades serves its tools over MCP on your machine. Checking asks the core directly and "
            + "reports how many tools it is serving. It does not check whether Claude Code has "
            + "connected - that is a separate thing, and this screen will not claim to know it.",

        OnboardingStep.Projects =>
            "Add a Unity project so Hades can index it. You can add more at any time from the "
            + "Projects section.",

        OnboardingStep.UnityPlugin =>
            "Installing the Unity plugin lets Hades see the editor live - what is attached, and when "
            + "a reload is blocked. This step is optional: everything above already works without it.",

        _ => string.Empty,
    };

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
