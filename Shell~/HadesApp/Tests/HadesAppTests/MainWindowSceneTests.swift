import AppKit
import HadesSupervision
import Testing

@testable import HadesApp

/// `MainWindowScene` is the AppKit controller that vends the single main window - the direct
/// counterpart to `MenuBarController`'s (deliberately untested, per that type's own doc comment)
/// `NSStatusItem`/`NSPopover` wiring. Unlike `MenuBarController`, the specific behaviour this plan
/// asks to prove - "created once and reused" - IS verifiable on a controller that vends windows
/// without needing a rendered UI: every AppKit side effect (`NSWindow` creation, focusing it) is
/// injected as a closure with a real default, and activation policy goes through an injected
/// `ActivationPolicyCoordinator` (shared with `SettingsWindowController` in the real composition
/// root, `HadesMenuBarApp.main()` - see that type's own doc comment for why), so these tests observe
/// CALL COUNTS and ARGUMENTS against the real `MainWindowScene` + real `MainWindowViewModel`, never a
/// mock of the type under test.
///
/// What these tests do NOT prove, because no unit test can without a rendered UI and a real
/// WindowServer session: that AppKit actually calls `windowWillClose(_:)` when a user clicks the
/// window's own close button, and that the OS actually hides the Dock icon when activation policy
/// flips to `.accessory`. Both are standard, documented AppKit contracts this code relies on but
/// does not reimplement; Task 8's hand-run pass is what confirms the OS honours them for real.
@Suite("MainWindowScene")
@MainActor
struct MainWindowSceneTests {

    @Test("show() creates the window on the first call only - every later call reuses the SAME instance, never a second window")
    func showCreatesOnceAndReuses() {
        var createCount = 0
        let viewModel = MainWindowViewModel(supervisor: FakeCoreSupervisor(state: .notStarted))
        let scene = MainWindowScene(
            viewModel: viewModel,
            makeWindow: { createCount += 1; return NSWindow() },
            focusWindow: { _ in }
        )

        scene.show()
        scene.show()
        scene.show()

        #expect(createCount == 1)
    }

    @Test("show() always focuses the one cached window, never a newly created one")
    func showAlwaysFocusesTheSameWindowInstance() {
        var focused: [ObjectIdentifier] = []
        let viewModel = MainWindowViewModel(supervisor: FakeCoreSupervisor(state: .notStarted))
        let scene = MainWindowScene(
            viewModel: viewModel,
            makeWindow: { NSWindow() },
            focusWindow: { focused.append(ObjectIdentifier($0)) }
        )

        scene.show()
        scene.show()

        #expect(focused.count == 2)
        #expect(Set(focused).count == 1, "both calls must focus the identical NSWindow instance")
    }

    @Test("show() selects the Projects section, overriding whatever was selected before")
    func showSelectsProjects() {
        let viewModel = MainWindowViewModel(supervisor: FakeCoreSupervisor(state: .notStarted))
        viewModel.select(.memory)
        let scene = MainWindowScene(
            viewModel: viewModel,
            makeWindow: { NSWindow() },
            focusWindow: { _ in }
        )

        scene.show()

        #expect(viewModel.selectedSection == .projects)
    }

    @Test("show() switches activation policy to regular; windowWillClose returns it to accessory")
    func activationPolicyFollowsWindowLifecycle() {
        var policies: [NSApplication.ActivationPolicy] = []
        let viewModel = MainWindowViewModel(supervisor: FakeCoreSupervisor(state: .notStarted))
        let scene = MainWindowScene(
            viewModel: viewModel,
            makeWindow: { NSWindow() },
            focusWindow: { _ in },
            activationCoordinator: ActivationPolicyCoordinator(setActivationPolicy: { policies.append($0) })
        )

        scene.show()
        #expect(policies == [.regular])

        scene.windowWillClose(Notification(name: NSWindow.willCloseNotification))
        #expect(policies == [.regular, .accessory])
    }

    @Test(
        "closing the main window while Settings (sharing the SAME coordinator) is still open does NOT revert to accessory - the Task 2 edge case Task 7 fixes"
    )
    func activationPolicyStaysRegularWhileASharedWindowIsStillOpen() {
        var policies: [NSApplication.ActivationPolicy] = []
        let coordinator = ActivationPolicyCoordinator(setActivationPolicy: { policies.append($0) })
        let viewModel = MainWindowViewModel(supervisor: FakeCoreSupervisor(state: .notStarted))
        let scene = MainWindowScene(
            viewModel: viewModel,
            makeWindow: { NSWindow() },
            focusWindow: { _ in },
            activationCoordinator: coordinator
        )

        scene.show() // the main window opens
        coordinator.windowOpened() // Settings opens too, sharing the same coordinator - see HadesMenuBarApp.main()
        #expect(policies == [.regular, .regular])

        scene.windowWillClose(Notification(name: NSWindow.willCloseNotification)) // the main window closes
        #expect(policies == [.regular, .regular], "Settings is still open - the Dock icon must not disappear out from under it")

        coordinator.windowClosed() // Settings closes too
        #expect(policies == [.regular, .regular, .accessory], "now both are closed - reverts")
    }

    @Test("polling follows window visibility: show() starts it, windowWillClose stops it, reopening resumes it")
    func pollingFollowsVisibility() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let viewModel = MainWindowViewModel(supervisor: supervisor, pollInterval: .milliseconds(20))
        let scene = MainWindowScene(
            viewModel: viewModel,
            makeWindow: { NSWindow() },
            focusWindow: { _ in }
        )

        scene.show()
        let started = await waitUntil(timeout: .seconds(3)) { await supervisor.refreshCallCount >= 2 }
        #expect(started)

        scene.windowWillClose(Notification(name: NSWindow.willCloseNotification))
        let countAtClose = await supervisor.refreshCallCount
        try? await Task.sleep(for: .milliseconds(200)) // several would-be intervals if still ticking
        #expect(await supervisor.refreshCallCount == countAtClose, "no further ticks after the window closes")

        scene.show() // reopen - reuses the same cached window
        let resumed = await waitUntil(timeout: .seconds(3)) { await supervisor.refreshCallCount > countAtClose }
        #expect(resumed)
    }
}
