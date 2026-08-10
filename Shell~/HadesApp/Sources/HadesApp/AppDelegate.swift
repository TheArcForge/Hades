import AppKit
import HadesControl
import HadesSupervision
import os

/// AppKit wiring: the composition root. Constructs the real `CoreSupervisor`, the real
/// `MenuBarViewModel` (wired to the real `Discovery.read`/`ControlClient` via
/// `MenuBarViewModel`'s own defaults), and `MenuBarController`. Not unit tested, per this plan's
/// own STANDING RULES - everything interesting it does is either an AppKit lifecycle call or a
/// direct call into an already-tested type.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var supervisor: CoreSupervisor!
    private var viewModel: MenuBarViewModel!
    private var menuBarController: MenuBarController!
    private var mainWindowScene: MainWindowScene!

    /// Non-nil only while first-run onboarding is showing - see `applicationDidFinishLaunching`'s
    /// own "the caller" doc comment for exactly when that is. Held here for the same reason
    /// `mainWindowScene`/`menuBarController` are: an unretained `NSWindowDelegate` would deallocate
    /// the instant this method returns, taking the window's delegate callbacks with it.
    private var onboardingWindowController: OnboardingWindowController?

    /// Shared with `SettingsWindowController` - see `ActivationPolicyCoordinator`'s own doc comment
    /// for why one instance must be passed to both. Set once in `init(activationCoordinator:)`,
    /// wired into `MainWindowScene` below.
    private let activationCoordinator: ActivationPolicyCoordinator

    init(activationCoordinator: ActivationPolicyCoordinator) {
        self.activationCoordinator = activationCoordinator
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let supervisor = CoreSupervisor(configuration: Self.makeConfiguration())
        self.supervisor = supervisor

        let viewModel = MenuBarViewModel(supervisor: supervisor)
        self.viewModel = viewModel

        // Shares the ONE real CoreSupervisor with the popover's own view model - one supervised
        // core process for the whole app, two independent surfaces (popover, main window) each
        // polling `refresh()` only while they are themselves visible. See `MainWindowViewModel`'s
        // own doc comment.
        let mainWindowViewModel = MainWindowViewModel(supervisor: supervisor)
        let projectsViewModel = ProjectsViewModel()
        // The per-item migration cleanup UI's own view model - the three {productGuid}-scoped
        // V12Cleanup actions, rendered inside ProjectDetailView's "v1.2 Cleanup" section. See
        // MigrationCleanupViewModel's own doc comment for why the fourth, global action lives on
        // SettingsViewModel instead (constructed with its own default below, in HadesMenuBarApp.main()).
        let migrationCleanupViewModel = MigrationCleanupViewModel()
        let tracesViewModel = TracesViewModel()
        let memoryViewModel = MemoryViewModel()

        // The seam Task 2 left for this: each tick, while the window is open, refreshes only
        // whichever section is currently selected - never all three. Projects, Traces and Memory are
        // all wired up now - see `MainWindowViewModelTests.
        // wiresProjectsViewModelIntoRefreshSelectedSection` / `wiresTracesViewModelIntoRefreshSelectedSection`
        // / `wiresMemoryViewModelIntoRefreshSelectedSection` for this exact pattern proven against
        // each real view model.
        mainWindowViewModel.refreshSelectedSection = { section in
            switch section {
            case .projects:
                await projectsViewModel.refresh()
            case .traces:
                await tracesViewModel.refresh()
            case .memory:
                await memoryViewModel.refresh()
            }
        }

        let mainWindowScene = MainWindowScene(
            viewModel: mainWindowViewModel, projectsViewModel: projectsViewModel, migrationCleanupViewModel: migrationCleanupViewModel,
            tracesViewModel: tracesViewModel, memoryViewModel: memoryViewModel, activationCoordinator: activationCoordinator)
        self.mainWindowScene = mainWindowScene

        menuBarController = MenuBarController(
            viewModel: viewModel,
            onQuit: { NSApp.terminate(nil) },
            onOpenHades: { mainWindowScene.show() }
        )

        // THE CALLER for first-run onboarding (Plan 14 Task 6, spec #3 §3.6): the one and only place
        // anything decides to show it, and the one and only place anything marks it done.
        // `UserDefaultsOnboardingStore.hasCompletedOnboarding` is the sole gate - `false` on a fresh
        // install (no key written yet) and on every launch until `OnboardingViewModel.advance()`
        // moves past the last step and calls `markCompleted()` (see that method's own doc comment),
        // after which this branch never runs again for this machine. Shares the SAME
        // `projectsViewModel` instance the main window polls (constructed above, captured into
        // `mainWindowViewModel.refreshSelectedSection` already) - a project added during onboarding
        // is already there once the main window opens later; nothing is fetched twice.
        let onboardingCompletionStore = UserDefaultsOnboardingStore()
        if !onboardingCompletionStore.hasCompletedOnboarding {
            // Plan 14 Task 10: the control API now has a real /control/migration/* surface (see
            // Hades.Server.Control.MigrationEndpoint) to back this seam, so this is no longer the
            // `nil` MigrationOffering's own doc comment describes Task 6 leaving it - see
            // LiveMigrationOffering's own doc comment for exactly what it does (imports memory and
            // traces) and does not do (any of V12Cleanup's four cleanup routes) under this offer.
            let onboardingViewModel = OnboardingViewModel(
                projectsViewModel: projectsViewModel, completionStore: onboardingCompletionStore,
                migrationOffering: LiveMigrationOffering())
            let onboardingWindowController = OnboardingWindowController(
                viewModel: onboardingViewModel, activationCoordinator: activationCoordinator)
            self.onboardingWindowController = onboardingWindowController
            onboardingWindowController.show()
        }

        // Adopt-or-spawn happens off the main run loop's synchronous startup path so the status
        // item appears immediately (showing `.notRunning` - the correct, honest state - until this
        // resolves) rather than blocking app launch on it. `bootstrap()` performs the one
        // immediate fetch that gets the icon out of the placeholder state without waiting for the
        // user's first click - see `MenuBarViewModel.bootstrap()`'s own doc comment.
        Task {
            await supervisor.start()
            await viewModel.bootstrap()
        }
    }

    /// Async-aware quit: if the current core is `.spawned`, `supervisor.stop()` terminates it
    /// (via the reaper) before the app actually exits, closing net #2 of the process model
    /// ("the core exits if the app dies") for the ORDINARY quit path - the SIGKILL path is
    /// `HadesCoreReaper`'s job (Plan 12 Task 2), not this method's. If the current core is
    /// `.adopted`, `stop()` is a no-op by construction (see `CoreSupervisor.stop()`'s own doc
    /// comment) - quitting never kills a core this app did not start.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let supervisor else { return .terminateNow }
        Task {
            await supervisor.stop()
            NSApp.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }

    /// Diagnostic channel for `makeConfiguration()`'s bundled-vs-fallback decision - visible in
    /// Console.app / `log show --predicate 'subsystem == "com.arcforge.hades.shell"'` regardless of
    /// how the app was launched (Finder, DMG, Homebrew, Terminal), unlike a bare `print` which goes
    /// nowhere useful for an `LSUIElement` app with no console window. Deliberately not `private`
    /// to `makeConfiguration` alone (it is `private` to the type instead): logging which core an
    /// already-running app picked must survive being read back independently of any one call.
    private static let launchLogger = Logger(subsystem: "com.arcforge.hades.shell", category: "CoreLaunch")

    /// How the core gets launched. Spec #4 (distribution): prefers the self-contained core
    /// published INTO the bundle at `Contents/Resources/HadesServer/Hades.Server` - see
    /// `scripts/build-app.sh`'s own `dotnet publish` step (Release configuration only - see that
    /// script's own comment for why Debug does not pay this cost) - a single native executable
    /// launched by its own absolute path: no `dotnet`, no `PATH` search, no .NET SDK on the
    /// recipient's machine at all. `HadesCoreReaper` still spawns it via `posix_spawn`, unchanged.
    ///
    /// Falls back to the ORIGINAL phase-one placeholder - `/usr/bin/env` plus `["dotnet", "run",
    /// "--project", <repo>/App~/src/Hades.Server, "--no-launch-profile"]`, mirroring
    /// `App~/scripts/e2e-editor-attach.sh`'s own established convention - whenever the bundled
    /// binary is not there. That is the ordinary shape of a `build-app.sh Debug` build (day-to-day
    /// iteration keeps working exactly as before) or an unbundled `swift run`; it is NOT what a
    /// distributed `Hades.app` should ever do, so it is never silent - see `launchLogger` above.
    /// The repo root for that fallback is located from this SOURCE FILE's own compile-time path
    /// (the same technique, and the same justification, as `HadesSupervision`'s
    /// `BuildProducts.packageRoot`: a running process's own location is not a reliable way to find
    /// "the repo" once toolchain-internal host processes are involved); `env` does the `PATH`
    /// search `posix_spawn` (unlike `posix_spawnp`) never does itself.
    ///
    /// See the Plan 12 Task 3 report for the placeholder this replaces, and
    /// Documentation/ReleasePipeline.md section 6 for the distribution story this is now part of.
    private static func makeConfiguration() -> CoreSupervisor.Configuration {
        // `Bundle.main.url(forAuxiliaryExecutable:)` finds a helper binary placed in
        // `Contents/MacOS/` alongside the app's own executable - see scripts/build-app.sh, which
        // copies the HadesSupervision-built HadesCoreReaper there. Deliberately NOT
        // HadesSupervision's own `BuildProducts.executable(named:)`: that type's doc comment is
        // explicit that it is for `swift test`'s `.build/` layout only, and that "an app bundle
        // has its own, unrelated layout" - a real app never needs `BuildProducts` at all. When
        // this returns nil (running unbundled, e.g. `swift run` during development), spawning is
        // simply not possible - adopting an already-running core still works, since adoption never
        // touches `reaperExecutable`.
        let reaperExecutable =
            Bundle.main.url(forAuxiliaryExecutable: "HadesCoreReaper")
            ?? URL(fileURLWithPath: "/nonexistent/HadesCoreReaper-not-bundled")

        // `Contents/Resources/`, not `Contents/MacOS/`: the published core is an entire runtime
        // tree (the apphost plus well over a hundred managed/native files), not one auxiliary
        // executable - Resources is the conventional bundle location for that kind of bulk embedded
        // content, leaving Contents/MacOS holding only the app's own executable plus the one small
        // HadesCoreReaper helper, exactly as before. Code signing is unaffected by the choice
        // either way - `codesign --deep` walks Resources and MacOS identically - see
        // scripts/build-app.sh's own comment on its codesign step.
        let bundledCore = Bundle.main.resourceURL?.appendingPathComponent("HadesServer/Hades.Server")
        if let bundledCore, FileManager.default.isExecutableFile(atPath: bundledCore.path) {
            launchLogger.info("Launching bundled self-contained core: \(bundledCore.path, privacy: .public)")
            return CoreSupervisor.Configuration(
                coreExecutable: bundledCore,
                coreArguments: [],
                reaperExecutable: reaperExecutable
            )
        }

        let repoRoot =
            URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // AppDelegate.swift -> Sources/HadesApp/
            .deletingLastPathComponent()  // -> Sources/
            .deletingLastPathComponent()  // -> HadesApp/
            .deletingLastPathComponent()  // -> Shell~/
            .deletingLastPathComponent()  // -> repo root
        let serverProject = repoRoot.appendingPathComponent("App~/src/Hades.Server").path

        launchLogger.warning(
            "No bundled core at Contents/Resources/HadesServer/Hades.Server (looked in \(bundledCore?.path ?? "<no resourceURL>", privacy: .public)) - falling back to `dotnet run --project \(serverProject, privacy: .public) --no-launch-profile`. This needs the .NET SDK and this exact source checkout on THIS machine; expected for a build-app.sh Debug build or an unbundled swift run, never for a distributed Hades.app."
        )
        return CoreSupervisor.Configuration(
            coreExecutable: URL(fileURLWithPath: "/usr/bin/env"),
            coreArguments: ["dotnet", "run", "--project", serverProject, "--no-launch-profile"],
            reaperExecutable: reaperExecutable
        )
    }
}
