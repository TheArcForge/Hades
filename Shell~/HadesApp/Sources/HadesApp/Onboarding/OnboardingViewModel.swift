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

    /// The OS's own current launch-at-login registration, read once at construction - the Claude
    /// Code step's own opt-in (see that step's view for why THAT step, not any other: Claude Code
    /// does not retry an MCP server that was unreachable at session start, so this is offered at
    /// exactly the step where the user is setting up that connection). Same
    /// `LaunchAtLoginReading` seam `SettingsViewModel.launchAtLoginEnabled` already reads - never a
    /// second OS-facts abstraction - but read once here rather than on a repeating `refresh()`:
    /// unlike the Settings window, onboarding is never reopened once constructed (see
    /// `OnboardingWindowController`'s own doc comment), so there is no "reopened later, might be
    /// stale" case to guard against beyond `toggleLaunchAtLogin(to:)`'s own re-read.
    public private(set) var launchAtLoginEnabled: Bool

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

    /// `AppDelegate` passes a real `LiveMigrationOffering` here (Plan 14 Task 10) - see
    /// `MigrationOffering`'s own doc comment. Defaults to `nil` only for callers that do not care
    /// about migration at all (SwiftUI previews, and every existing test that predates this
    /// parameter); tests that DO exercise migration inject `FakeMigrationOffering` to prove the
    /// offered-never-silently-performed contract without a real control-API round trip.
    private let migrationOffering: (any MigrationOffering)?

    /// See `launchAtLoginEnabled`'s own doc comment. `AppDelegate` never passes this explicitly,
    /// relying on the same real-`LaunchAtLoginService()` default `SettingsViewModel` also falls
    /// back to; every test below injects `FakeLaunchAtLoginReading` instead - the real
    /// `LaunchAtLoginService` must never run as a side effect of the test suite (see that type's
    /// own doc comment for why).
    private let launchAtLogin: any LaunchAtLoginReading

    public init(
        projectsViewModel: ProjectsViewModel = ProjectsViewModel(),
        completionStore: any OnboardingCompletionTracking = UserDefaultsOnboardingStore(),
        claudeCodeVerifier: any ClaudeCodeVerifying = LiveClaudeCodeVerifier(),
        migrationOffering: (any MigrationOffering)? = nil,
        launchAtLogin: any LaunchAtLoginReading = LaunchAtLoginService()
    ) {
        self.projectsViewModel = projectsViewModel
        self.completionStore = completionStore
        self.claudeCodeVerifier = claudeCodeVerifier
        self.migrationOffering = migrationOffering
        self.launchAtLogin = launchAtLogin
        self.launchAtLoginEnabled = launchAtLogin.isEnabled
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
    /// if so surface the offer - never act on it. Production wires a real `LiveMigrationOffering`
    /// (see `migrationOffering`'s own doc comment), so this check IS reachable for a real user; a
    /// caller that omits `migrationOffering` (previews, most existing tests) simply never sees an
    /// offer, which is the same "nothing to check" short-circuit this method already needed for
    /// that case.
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

    /// Requests a launch-at-login change, then immediately re-reads `launchAtLoginEnabled` from
    /// the SAME OS source - never the requested value - so a request the OS refuses OR silently
    /// ignores can never display as on. Identical contract to, and built on the same
    /// `LaunchAtLoginReading.settingEnabled(to:)` seam as, `SettingsViewModel.toggleLaunchAtLogin` -
    /// see that method's own doc comment.
    public func toggleLaunchAtLogin(to requested: Bool) {
        launchAtLoginEnabled = launchAtLogin.settingEnabled(to: requested)
    }
}
