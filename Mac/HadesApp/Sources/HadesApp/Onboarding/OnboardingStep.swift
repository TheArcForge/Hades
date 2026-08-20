/// The five first-run onboarding steps, in the fixed order spec #4 §4 and spec #3 §3.6 both give:
/// install, permissions, Claude Code, projects, Unity plugin. `rawValue` is the step's 0-based
/// position, deliberately sequential so `OnboardingViewModel.advance()` can move to the next step
/// with plain arithmetic rather than a hand-written table.
///
/// **Unity plugin is the last step, and only an upgrade.** Spec #4 §4's own success criterion: "a
/// user who stops after step 4 has a working, useful Hades... Step 5 is an upgrade, not a
/// requirement." `OnboardingViewModel.advance()` never gates completion on anything this step does -
/// see that method's own doc comment.
public enum OnboardingStep: Int, CaseIterable, Sendable {
    case install
    case permissions
    case claudeCode
    case projects
    case unityPlugin

    /// Fixed sidebar/step chrome, not control-API data - same allowance `Section.title` already
    /// documents (spec #3 §1: literal Swift copy is fine for concepts with no API equivalent; a
    /// step's name is UI navigation, not a rendered DTO field).
    public var title: String {
        switch self {
        case .install: return "Install"
        case .permissions: return "Permissions"
        case .claudeCode: return "Claude Code"
        case .projects: return "Projects"
        case .unityPlugin: return "Unity Plugin"
        }
    }
}
