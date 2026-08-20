import Foundation
import HadesControl
import Testing

@testable import HadesApp

/// `OnboardingViewModel` owns first-run onboarding's own step sequencing, the Claude Code
/// reachability check, project adding (delegated to a real `ProjectsViewModel` - never
/// reimplemented, see `addProjectDelegatesToProjectsViewModel` below), and the migration-offer
/// gate. Migration's DECISION logic (`V12Detector`/`V12Importer`/`V12Cleanup`, all `.NET`,
/// `Core/src/Hades.Core/Migration/`) is never re-implemented here - `OnboardingViewModel` only ever
/// calls through the thin `MigrationOffering` seam (see that protocol's own doc comment), which
/// `AppDelegate` backs with a real `LiveMigrationOffering` in production as of Plan 14 Task 10.
/// These tests use `FakeMigrationOffering` throughout, so they prove the one contract that is
/// legitimately `OnboardingViewModel`'s OWN regardless of what backs the seam: an offer is
/// surfaced, never auto-performed, and performing requires an explicit, separate confirmation -
/// spec #4 §10, "Migration is always offered, never performed silently."
@Suite("OnboardingViewModel")
@MainActor
struct OnboardingViewModelTests {

    // MARK: - Initial state

    @Test("starts at the Install step, not complete, nothing verified, no migration offered")
    func startsAtInstallStepNotComplete() {
        let viewModel = OnboardingViewModel(
            projectsViewModel: ProjectsViewModel(discover: { nil }),
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )

        #expect(viewModel.currentStep == .install)
        #expect(viewModel.isComplete == false)
        #expect(viewModel.claudeCodeVerification == .notVerified)
        #expect(viewModel.migrationOfferedProjectPath == nil)
    }

    // MARK: - advance() - step progression

    @Test("advance() moves through install -> permissions -> claudeCode -> projects -> unityPlugin in order")
    func advanceMovesThroughEachStepInOrder() {
        let viewModel = OnboardingViewModel(
            projectsViewModel: ProjectsViewModel(discover: { nil }),
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )

        viewModel.advance()
        #expect(viewModel.currentStep == .permissions)
        viewModel.advance()
        #expect(viewModel.currentStep == .claudeCode)
        viewModel.advance()
        #expect(viewModel.currentStep == .projects)
        viewModel.advance()
        #expect(viewModel.currentStep == .unityPlugin)
        #expect(viewModel.isComplete == false, "reaching the last step is not itself completion")
    }

    @Test("advancing past the last step (unityPlugin) completes onboarding and marks the completion store")
    func advancingPastTheLastStepCompletesOnboarding() {
        let completionStore = FakeOnboardingCompletionTracking()
        let viewModel = OnboardingViewModel(
            projectsViewModel: ProjectsViewModel(discover: { nil }),
            completionStore: completionStore,
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )
        for _ in 0..<4 { viewModel.advance() }
        #expect(viewModel.currentStep == .unityPlugin)

        viewModel.advance()

        #expect(viewModel.isComplete == true)
        #expect(completionStore.markCompletedCallCount == 1)
    }

    @Test("advancing again after completion is a no-op - never double-marks the completion store")
    func advanceAfterCompletionIsANoOp() {
        let completionStore = FakeOnboardingCompletionTracking()
        let viewModel = OnboardingViewModel(
            projectsViewModel: ProjectsViewModel(discover: { nil }),
            completionStore: completionStore,
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )
        for _ in 0..<5 { viewModel.advance() }
        #expect(viewModel.isComplete == true)

        viewModel.advance()

        #expect(completionStore.markCompletedCallCount == 1)
        #expect(viewModel.currentStep == .unityPlugin)
    }

    // MARK: - Skip behaviour - the structural claim: a user who stops after step 4 has a working
    // Hades, because completion never depends on the Unity plugin step doing anything at all.

    @Test("completing onboarding never requires installing the Unity plugin for any project")
    func completingOnboardingNeverRequiresInstallingTheUnityPlugin() async {
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: []))])
        let projectsViewModel = ProjectsViewModel(
            discover: { ControlConnection(port: 1, token: "t") }, makeClient: { _ in fetcher })
        let completionStore = FakeOnboardingCompletionTracking()
        let viewModel = OnboardingViewModel(
            projectsViewModel: projectsViewModel,
            completionStore: completionStore,
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )

        for _ in 0..<5 { viewModel.advance() }

        #expect(viewModel.isComplete == true)
        #expect(completionStore.hasCompletedOnboarding == true)
        #expect(await fetcher.installPluginCallCount == 0, "step 5 must be reachable and completable with zero plugin installs")
    }

    // MARK: - verifyClaudeCode()

    @Test("verifyClaudeCode() reports reachable with the tool count the verifier returned, verbatim")
    func verifyClaudeCodeReportsReachableWithToolCount() async {
        let viewModel = OnboardingViewModel(
            projectsViewModel: ProjectsViewModel(discover: { nil }),
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.reachable(toolCount: 32)]),
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )

        await viewModel.verifyClaudeCode()

        #expect(viewModel.claudeCodeVerification == .reachable(toolCount: 32))
    }

    @Test("verifyClaudeCode() reports unreachable when the verifier does")
    func verifyClaudeCodeReportsUnreachable() async {
        let viewModel = OnboardingViewModel(
            projectsViewModel: ProjectsViewModel(discover: { nil }),
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )

        await viewModel.verifyClaudeCode()

        #expect(viewModel.claudeCodeVerification == .unreachable)
    }

    @Test("verifyClaudeCode() can be retried after a failure and reflects the new outcome")
    func verifyClaudeCodeCanBeRetriedAfterFailure() async {
        let verifier = FakeClaudeCodeVerifying([.unreachable, .reachable(toolCount: 32)])
        let viewModel = OnboardingViewModel(
            projectsViewModel: ProjectsViewModel(discover: { nil }),
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: verifier,
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )

        await viewModel.verifyClaudeCode()
        #expect(viewModel.claudeCodeVerification == .unreachable)

        await viewModel.verifyClaudeCode()
        #expect(viewModel.claudeCodeVerification == .reachable(toolCount: 32))
        #expect(await verifier.verifyCallCount == 2)
    }

    // MARK: - addProject(path:) - delegates to ProjectsViewModel, never reimplements it

    @Test("addProject(path:) delegates to the injected ProjectsViewModel's own addProject - never a second implementation")
    func addProjectDelegatesToProjectsViewModel() async {
        let row = ProjectRow(
            name: "Demo", path: "/tmp/demo", productGuid: "guid-1", unityVersion: "6000.0.1f1",
            indexState: .indexing, indexStatus: "Indexing…", nodeCount: 0, edgeCount: 0,
            editor: ProjectEditorInfo(state: .absent, status: "Not attached", unityVersion: nil, processId: nil, connectionAgeSeconds: nil),
            warnings: [])
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: []))], addOutcome: .success(row))
        let projectsViewModel = ProjectsViewModel(
            discover: { ControlConnection(port: 1, token: "t") }, makeClient: { _ in fetcher })
        let viewModel = OnboardingViewModel(
            projectsViewModel: projectsViewModel,
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )

        await viewModel.addProject(path: "/tmp/demo")

        #expect(await fetcher.addCallCount == 1)
        #expect(await fetcher.lastAddedPath == "/tmp/demo")
    }

    @Test("addProject(path:) never offers migration when no MigrationOffering is wired (the init's own safe default)")
    func addProjectWithNoMigrationOfferingNeverOffersMigration() async {
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: []))], addOutcome: .failure(.staleToken))
        let projectsViewModel = ProjectsViewModel(
            discover: { ControlConnection(port: 1, token: "t") }, makeClient: { _ in fetcher })
        let viewModel = OnboardingViewModel(
            projectsViewModel: projectsViewModel,
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            // migrationOffering omitted - defaults to nil. AppDelegate itself always passes a real
            // LiveMigrationOffering (Plan 14 Task 10); this test proves the OTHER case still degrades
            // safely, e.g. for any future caller that does not care about migration at all.
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )

        await viewModel.addProject(path: "/tmp/v12-project")

        #expect(viewModel.migrationOfferedProjectPath == nil)
    }

    @Test("addProject(path:) offers migration when the injected detector reports a v1.2 project - but never performs it")
    func addProjectOffersMigrationWhenDetectorReportsV12() async {
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: []))], addOutcome: .failure(.staleToken))
        let projectsViewModel = ProjectsViewModel(
            discover: { ControlConnection(port: 1, token: "t") }, makeClient: { _ in fetcher })
        let migration = FakeMigrationOffering(isV12ProjectResult: true)
        let viewModel = OnboardingViewModel(
            projectsViewModel: projectsViewModel,
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            migrationOffering: migration,
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )

        await viewModel.addProject(path: "/tmp/v12-project")

        #expect(viewModel.migrationOfferedProjectPath == "/tmp/v12-project")
        #expect(await migration.performMigrationCallCount == 0, "an offer must never silently perform anything")
    }

    @Test("addProject(path:) does not offer migration when the injected detector reports the project is not v1.2")
    func addProjectDoesNotOfferMigrationWhenDetectorReportsNotV12() async {
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: []))], addOutcome: .failure(.staleToken))
        let projectsViewModel = ProjectsViewModel(
            discover: { ControlConnection(port: 1, token: "t") }, makeClient: { _ in fetcher })
        let migration = FakeMigrationOffering(isV12ProjectResult: false)
        let viewModel = OnboardingViewModel(
            projectsViewModel: projectsViewModel,
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            migrationOffering: migration,
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )

        await viewModel.addProject(path: "/tmp/ordinary-project")

        #expect(viewModel.migrationOfferedProjectPath == nil)
        #expect(await migration.isV12ProjectCallCount == 1)
    }

    // MARK: - confirmMigration() / declineMigration() - offered, never silently performed

    @Test("confirmMigration() performs exactly once, for the offered path, and clears the offer")
    func confirmMigrationPerformsExactlyOnceForTheOfferedPath() async {
        let projectsViewModel = ProjectsViewModel(discover: { nil })
        let migration = FakeMigrationOffering(isV12ProjectResult: true)
        let viewModel = OnboardingViewModel(
            projectsViewModel: projectsViewModel,
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            migrationOffering: migration,
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )
        await viewModel.addProject(path: "/tmp/v12-project")
        #expect(viewModel.migrationOfferedProjectPath == "/tmp/v12-project")

        await viewModel.confirmMigration()

        #expect(await migration.performMigrationCallCount == 1)
        #expect(await migration.lastPerformedPath == "/tmp/v12-project")
        #expect(viewModel.migrationOfferedProjectPath == nil, "confirming clears the offer")
    }

    @Test("declineMigration() never performs anything, and clears the offer")
    func declineMigrationNeverPerformsAndClearsTheOffer() async {
        let projectsViewModel = ProjectsViewModel(discover: { nil })
        let migration = FakeMigrationOffering(isV12ProjectResult: true)
        let viewModel = OnboardingViewModel(
            projectsViewModel: projectsViewModel,
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            migrationOffering: migration,
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )
        await viewModel.addProject(path: "/tmp/v12-project")
        #expect(viewModel.migrationOfferedProjectPath == "/tmp/v12-project")

        viewModel.declineMigration()

        #expect(await migration.performMigrationCallCount == 0)
        #expect(viewModel.migrationOfferedProjectPath == nil)
    }

    @Test("confirmMigration() with no offer pending does nothing")
    func confirmMigrationWithNoOfferPendingDoesNothing() async {
        let projectsViewModel = ProjectsViewModel(discover: { nil })
        let migration = FakeMigrationOffering(isV12ProjectResult: true)
        let viewModel = OnboardingViewModel(
            projectsViewModel: projectsViewModel,
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            migrationOffering: migration,
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: false)
        )
        #expect(viewModel.migrationOfferedProjectPath == nil)

        await viewModel.confirmMigration()

        #expect(await migration.performMigrationCallCount == 0)
    }

    // MARK: - launchAtLoginEnabled / toggleLaunchAtLogin(to:) - the Claude Code step's own
    // launch-at-login opt-in (spec #4 §4's connectivity step is where this matters most: Claude
    // Code does not retry an MCP server that was unreachable at session start). Same
    // `LaunchAtLoginReading` seam `SettingsViewModel` already established, and the identical
    // "always re-read the OS's own answer after writing, never trust the request" contract - see
    // `LaunchAtLoginReading.settingEnabled(to:)`'s own doc comment for exactly why a thrown error
    // alone is not the only failure mode this must guard against, and `SettingsViewModelTests`'
    // own three equivalent tests for the shape these mirror.

    @Test("launchAtLoginEnabled reflects the OS's current state as soon as the view model exists")
    func launchAtLoginEnabledReflectsOSStateAtConstruction() {
        let viewModel = OnboardingViewModel(
            projectsViewModel: ProjectsViewModel(discover: { nil }),
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            launchAtLogin: FakeLaunchAtLoginReading(isEnabled: true)
        )

        #expect(viewModel.launchAtLoginEnabled == true)
    }

    @Test("toggleLaunchAtLogin reflects the OS's real answer once the request succeeds")
    func toggleLaunchAtLoginReflectsRealSuccess() {
        let launchAtLogin = FakeLaunchAtLoginReading(isEnabled: false)
        let viewModel = OnboardingViewModel(
            projectsViewModel: ProjectsViewModel(discover: { nil }),
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            launchAtLogin: launchAtLogin
        )

        viewModel.toggleLaunchAtLogin(to: true)

        #expect(viewModel.launchAtLoginEnabled == true)
        #expect(launchAtLogin.lastRequestedValue == true)
    }

    @Test("a request the OS silently ignores (no throw, but isEnabled unchanged) must NOT display as on")
    func toggleLaunchAtLoginDoesNotDisplayOnWhenTheOSSilentlyIgnoresTheRequest() {
        let launchAtLogin = FakeLaunchAtLoginReading(isEnabled: false)
        launchAtLogin.applyRequestToIsEnabled = false // the OS accepts the call but never actually registers
        let viewModel = OnboardingViewModel(
            projectsViewModel: ProjectsViewModel(discover: { nil }),
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            launchAtLogin: launchAtLogin
        )

        viewModel.toggleLaunchAtLogin(to: true)

        #expect(viewModel.launchAtLoginEnabled == false, "a silently-ignored request must read back as still off, not the requested true")
    }

    @Test("a thrown setEnabled error is swallowed, and isEnabled is still re-read afterward - never left stale")
    func toggleLaunchAtLoginSwallowsAThrownErrorAndStillRereadsIsEnabled() {
        struct SomeError: Error {}
        let launchAtLogin = FakeLaunchAtLoginReading(isEnabled: false)
        launchAtLogin.errorToThrow = SomeError()
        let viewModel = OnboardingViewModel(
            projectsViewModel: ProjectsViewModel(discover: { nil }),
            completionStore: FakeOnboardingCompletionTracking(),
            claudeCodeVerifier: FakeClaudeCodeVerifying([.unreachable]),
            launchAtLogin: launchAtLogin
        )

        viewModel.toggleLaunchAtLogin(to: true)

        #expect(viewModel.launchAtLoginEnabled == false, "the OS refused - isEnabled is still false, not the requested true")
        #expect(launchAtLogin.setEnabledCallCount == 1)
    }
}
