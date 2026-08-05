import AppKit
import HadesControl
import HadesSupervision

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
            viewModel: mainWindowViewModel, projectsViewModel: projectsViewModel, tracesViewModel: tracesViewModel,
            memoryViewModel: memoryViewModel, activationCoordinator: activationCoordinator)
        self.mainWindowScene = mainWindowScene

        menuBarController = MenuBarController(
            viewModel: viewModel,
            onQuit: { NSApp.terminate(nil) },
            onOpenHades: { mainWindowScene.show() }
        )

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

    /// Phase-one placeholder for how the core gets launched. Mirrors
    /// `App~/scripts/e2e-editor-attach.sh`'s own established convention (`dotnet run --project
    /// src/Hades.Server --no-launch-profile`) and `CoreSupervisor.Configuration`'s own doc comment
    /// example - `/usr/bin/env` plus `["dotnet", "run", ...]` because `HadesCoreReaper` spawns the
    /// core via `posix_spawn`, which (unlike `posix_spawnp`) never searches `PATH` itself; `env`
    /// does that PATH search on `posix_spawn`'s behalf. The repo root is located from this SOURCE
    /// FILE's own compile-time path (the same technique, and the same justification, as
    /// `HadesSupervision`'s `BuildProducts.packageRoot`: a running process's own location is not a
    /// reliable way to find "the repo" once toolchain-internal host processes are involved).
    ///
    /// This is a deliberate, DOCUMENTED simplification the plan did not specify - see the Plan 12
    /// Task 3 report. Spec #4 (distribution) replaces `dotnet run` against source with a
    /// self-contained published binary embedded in the app bundle; nothing here assumes today's
    /// dev-time invocation survives that change.
    private static func makeConfiguration() -> CoreSupervisor.Configuration {
        let repoRoot =
            URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // AppDelegate.swift -> Sources/HadesApp/
            .deletingLastPathComponent()  // -> Sources/
            .deletingLastPathComponent()  // -> HadesApp/
            .deletingLastPathComponent()  // -> Shell~/
            .deletingLastPathComponent()  // -> repo root
        let serverProject = repoRoot.appendingPathComponent("App~/src/Hades.Server").path

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

        return CoreSupervisor.Configuration(
            coreExecutable: URL(fileURLWithPath: "/usr/bin/env"),
            coreArguments: ["dotnet", "run", "--project", serverProject, "--no-launch-profile"],
            reaperExecutable: reaperExecutable
        )
    }
}
