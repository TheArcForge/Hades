import AppKit
import Testing

@testable import HadesApp

/// `HadesMenuBarApp.main()` itself is not unit tested, for the same reason `AppDelegate`'s own doc
/// comment gives: it is pure AppKit bootstrap (`NSApplication.shared.run()` blocks forever - there
/// is nothing a test could call). `makeMainMenu`, though, is a pure function from
/// (target, selector) to an `NSMenu` structure - no rendering, no running app required - so it is
/// exactly the kind of thing this project's own standard asks NOT to skip just because the
/// surrounding area (AppKit menu wiring) is mostly untestable. This is the one proof that Spec #3
/// §3.5's "reachable by Cmd-," entry point is wired to the OS-standard shortcut at all. `SettingsView`
/// itself (Task 7's real content, replacing the placeholder this suite's doc comment used to note as
/// still outstanding) is proven at the `SettingsViewModel` layer instead (see
/// `SettingsViewModelTests`) - the view itself, like every other SwiftUI view in this app, has no
/// snapshot-test infrastructure; Task 8's hand-run pass is what confirms it actually renders.
@Suite("HadesMenuBarApp.makeMainMenu")
@MainActor
struct HadesMenuBarAppTests {

    @Test("the app menu contains a Settings item bound to the OS-standard Cmd-comma shortcut")
    func settingsItemHasStandardShortcutAndTarget() {
        let target = FakeMenuTarget()
        let menu = HadesMenuBarApp.makeMainMenu(
            target: target,
            settingsAction: #selector(FakeMenuTarget.handle)
        )

        let settingsItem = menu.items.first?.submenu?.items.first { $0.title == "Settings…" }

        #expect(settingsItem?.keyEquivalent == ",")
        #expect(settingsItem?.action == #selector(FakeMenuTarget.handle))
        #expect(settingsItem?.target === target)
    }

    /// The proof available at this level: the Quit item is bound to the exact same selector -
    /// `NSApplication.terminate(_:)` - that `AppDelegate`'s own `onQuit = { NSApp.terminate(nil) }`
    /// closure already invokes for the popover's Quit button. `NSApplication.terminate(_:)` always
    /// asks its delegate first (`AppDelegate.applicationShouldTerminate(_:)`), which is what keeps
    /// an adopted core alive and cleans up a spawned one (Plan 12 Task 4 check 7) - so the SAME
    /// selector here means the SAME path, without re-running a live quit sequence in-process (which
    /// would terminate the test runner itself).
    @Test("the app menu contains a Quit item bound to Cmd-Q, dispatched via NSApplication.terminate(_:) - the same shutdown path the popover's own Quit button uses")
    func quitItemUsesStandardTerminateActionViaResponderChain() {
        let target = FakeMenuTarget()
        let menu = HadesMenuBarApp.makeMainMenu(
            target: target,
            settingsAction: #selector(FakeMenuTarget.handle)
        )

        let quitItem = menu.items.first?.submenu?.items.first { $0.title == "Quit Hades" }

        #expect(quitItem?.keyEquivalent == "q")
        #expect(quitItem?.action == #selector(NSApplication.terminate(_:)))
        // target nil: dispatched via the standard responder chain, exactly like Xcode's own default
        // app-menu template - NOT bound to a fake/local target that could silently diverge from the
        // real quit path.
        #expect(quitItem?.target == nil)
    }
}

private final class FakeMenuTarget: NSObject {
    @objc func handle() {}
}
