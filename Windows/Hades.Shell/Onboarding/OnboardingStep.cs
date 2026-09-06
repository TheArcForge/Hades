namespace Hades.Shell.Onboarding;

/// <summary>
/// The first-run onboarding steps. The port of
/// <c>Mac/HadesApp/Sources/HadesApp/Onboarding/OnboardingStep.swift</c>, which has five.
///
/// <b><see cref="Permissions"/> IS DELIBERATELY NOT ONE OF THE WINDOWS STEPS.</b> It is macOS TCC
/// folder access, and Windows shows no equivalent prompt - walking a user through a permission grant
/// that never fires would be explaining something that does not happen. The member is kept here, and
/// excluded from <see cref="OnboardingViewModel.AllSteps"/>, so the exclusion is explicit and
/// testable rather than a silent omission someone later "restores" by accident.
///
/// <b><see cref="UnityPlugin"/> is last, and only an upgrade.</b> A user who stops before it has a
/// working, useful Hades - so completion is never gated on anything that step does.
/// </summary>
public enum OnboardingStep
{
    Install,

    /// <summary>macOS only. See this type's own doc comment - never in the Windows sequence.</summary>
    Permissions,

    ClaudeCode,
    Projects,
    UnityPlugin,
}

public static class OnboardingStepExtensions
{
    /// <summary>Fixed step chrome, not control-API data - the same allowance section titles use.</summary>
    public static string Title(this OnboardingStep step) => step switch
    {
        OnboardingStep.Install => "Install",
        OnboardingStep.Permissions => "Permissions",
        OnboardingStep.ClaudeCode => "Claude Code",
        OnboardingStep.Projects => "Projects",
        OnboardingStep.UnityPlugin => "Unity Plugin",
        _ => step.ToString(),
    };
}

/// <summary>
/// What a step lets the user actually DO, as opposed to what its copy tells them.
///
/// <para><b>This exists because those two drifted.</b> The Projects and Unity-plugin steps shipped
/// with copy saying "Add a Unity project…" and "Installing the Unity plugin…" and no control to do
/// either — the window had exactly one action panel, for the Claude Code check. Thirteen onboarding
/// tests passed throughout: they pinned the step count, the order and the copy, and not one asked
/// whether a step could do anything. A hand-run found it on the first walk-through.</para>
///
/// <para>Modelling the action as data rather than as XAML is what makes the invariant checkable
/// without a Window: the view switches panels on this, and a headless test asserts that the only
/// purely informational step is <see cref="OnboardingStep.Install"/>.</para>
/// </summary>
public enum OnboardingAction
{
    /// <summary>Nothing to do — the step is informational. Only Install may be this.</summary>
    None,

    /// <summary>Ask the core how many tools it is serving.</summary>
    VerifyClaudeCode,

    /// <summary>Pick a folder and adopt it as a project.</summary>
    AddProject,

    /// <summary>Install the Unity plugin into the project added a step earlier.</summary>
    InstallPlugin,
}
