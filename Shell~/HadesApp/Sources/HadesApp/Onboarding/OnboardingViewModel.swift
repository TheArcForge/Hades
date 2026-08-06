import Observation

/// Owns first-run onboarding's own step sequencing (spec #3 §3.6, spec #4 §4), the Claude Code
/// reachability check, and the migration-offer gate. Project adding and Unity-plugin installing are
/// deliberately NOT reimplemented here - both already exist, fully built and fully tested, on
/// `ProjectsViewModel` (`addProject(path:)`, `installPlugin(productGuid:)`, and the `projects`
/// list itself, which already reports index state/status as it happens - spec #4 §4's "indexing
/// starts immediately and reports progress; nothing blocks" is exactly `ProjectsViewModel.refresh()`'s
/// existing contract). This type OWNS one `ProjectsViewModel` instance and exposes it directly so
/// the Projects and Unity Plugin step views call straight through to it - see `projectsViewModel`'s
/// own doc comment. `AppDelegate` hands onboarding the SAME instance the main window polls, so a
/// project added during onboarding is already there once the main window opens later.
///
/// **Step 5 (Unity plugin) is genuinely skippable.** `advance()` moves through every step, including
/// past `.unityPlugin` into completion, with NOTHING here ever reading `projectsViewModel`'s state
/// to decide whether completion is allowed - no plugin-installed flag, no per-project gate. Spec #4
/// §4's own success criterion: "a user who stops after step 4 has a working, useful Hades... Step 5
/// is an upgrade, not a requirement." See `completingOnboardingNeverRequiresInstallingTheUnityPlugin`
/// in `OnboardingViewModelTests` for the proof.
@MainActor
@Observable
public final class OnboardingViewModel {
    public private(set) var currentStep: OnboardingStep = .install
    public private(set) var isComplete = false

    /// The Claude Code step's own last check - `.notVerified` until `verifyClaudeCode()` is first
    /// called. See `ClaudeCodeVerification`'s own doc comment for exactly what a `.reachable` result
    /// proves and what it only assumes.
    public private(set) var claudeCodeVerification: ClaudeCodeVerification = .notVerified

    /// The one project `addProject(path:)` most recently found to look like a v1.2 install, offered
    /// but not yet acted on - `nil` whenever nothing is currently offered. Cleared by either
    /// `confirmMigration()` or `declineMigration()`, never by anything else, so the offer stays on
    /// screen until the user makes an explicit choice.
    public private(set) var migrationOfferedProjectPath: String?

    /// Owns Projects-step (and Unity-Plugin-step) state and every action on it - `addProject(path:)`,
    /// `installPlugin(productGuid:)`, `projects`, `lastActionMessage`. Exposed directly (not wrapped)
    /// so the step views call straight through to the one already-tested implementation - see this
    /// type's own class doc comment.
    public let projectsViewModel: ProjectsViewModel

    private let completionStore: any OnboardingCompletionTracking
    private let claudeCodeVerifier: any ClaudeCodeVerifying

    /// `nil` in production - see `MigrationOffering`'s own doc comment for why: there is no control
    /// API endpoint to back a real conformance with today. Tests inject a fake to prove the
    /// offered-never-silently-performed contract ahead of that endpoint existing.
    private let migrationOffering: (any MigrationOffering)?

    public init(
        projectsViewModel: ProjectsViewModel = ProjectsViewModel(),
        completionStore: any OnboardingCompletionTracking = UserDefaultsOnboardingStore(),
        claudeCodeVerifier: any ClaudeCodeVerifying = LiveClaudeCodeVerifier(),
        migrationOffering: (any MigrationOffering)? = nil
    ) {
        self.projectsViewModel = projectsViewModel
        self.completionStore = completionStore
        self.claudeCodeVerifier = claudeCodeVerifier
        self.migrationOffering = migrationOffering
    }

    /// Moves to the next step, or - from the last step (`.unityPlugin`) - completes onboarding and
    /// marks `completionStore`. A no-op once already complete (never double-marks the store); every
    /// step, including `.unityPlugin`, reaches this the same way, via the same "Continue"/"Finish"
    /// action in `OnboardingRootView` - there is no separate "skip" code path to keep in sync with
    /// this one, which is exactly what makes step 5 provably no different from any other step: see
    /// this type's own class doc comment.
    public func advance() {
        guard !isComplete else { return }
        if let next = OnboardingStep(rawValue: currentStep.rawValue + 1) {
            currentStep = next
        } else {
            completionStore.markCompleted()
            isComplete = true
        }
    }

    /// Runs the Claude Code step's live check - see `ClaudeCodeVerifying`'s own doc comment.
    /// Callable any number of times (e.g. retry after fixing something); each call replaces the
    /// previous result outright, never merges with it.
    public func verifyClaudeCode() async {
        claudeCodeVerification = .verifying
        claudeCodeVerification = await claudeCodeVerifier.verify()
    }

    /// Delegates to `projectsViewModel.addProject(path:)` - see this type's own class doc comment
    /// for why that, not a second implementation, is the whole of what this method does for adding
    /// itself. The ONE thing it adds on top: after the add attempt, if (and only if) a real
    /// `MigrationOffering` is wired, ask whether the just-added path looks like a v1.2 project, and
    /// if so surface the offer - never act on it. Production leaves `migrationOffering` `nil` (see
    /// that property's own doc comment), so this check is unreachable for a real user today; that is
    /// deliberate, not an oversight.
    public func addProject(path: String) async {
        await projectsViewModel.addProject(path: path)

        guard let migrationOffering else { return }
        if await migrationOffering.isV12Project(projectPath: path) {
            migrationOfferedProjectPath = path
        }
    }

    /// The ONLY path that ever calls `migrationOffering.performMigration` - reachable exclusively
    /// from an explicit user action (the Projects step's own "Migrate…" button), never automatically.
    /// A no-op when nothing is currently offered. Clears the offer immediately, before the (awaited)
    /// perform call returns, so a second tap cannot double-fire it.
    public func confirmMigration() async {
        guard let path = migrationOfferedProjectPath else { return }
        migrationOfferedProjectPath = nil
        await migrationOffering?.performMigration(projectPath: path)
    }

    /// Clears the current offer without ever calling `performMigration` - the Projects step's own
    /// "Not Now" button.
    public func declineMigration() {
        migrationOfferedProjectPath = nil
    }
}
