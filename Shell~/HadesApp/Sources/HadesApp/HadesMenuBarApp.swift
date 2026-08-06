import AppKit
import SwiftUI

/// Entry point. Deliberately a `@main` TYPE, not a file literally named `main.swift` - see
/// `Package.swift`'s own doc comment for why that distinction is load-bearing: SwiftPM refuses to
/// let a test target `@testable import` an executable target whose entry point is `main.swift`
/// (top-level-code linking semantics), which would have blocked every test in this package.
@main
enum HadesMenuBarApp {
    static func main() {
        let app = NSApplication.shared
        // No Dock icon, no Cmd+Tab entry - this is a menu-bar-only app. Also declared via
        // LSUIElement in the bundled Info.plist (see scripts/build-app.sh); setting it here too
        // means the app behaves correctly even when launched unbundled during development, when
        // there is no Info.plist for AppKit to read it from.
        app.setActivationPolicy(.accessory)

        // One shared coordinator between the main window and Settings - see its own doc comment for
        // the Task 2 edge case this fixes (closing the main window while Settings was still open used
        // to drop the Dock icon out from under it).
        let activationCoordinator = ActivationPolicyCoordinator()

        let settingsController = SettingsWindowController(
            viewModel: SettingsViewModel(),
            activationCoordinator: activationCoordinator
        )
        app.mainMenu = makeMainMenu(
            target: settingsController,
            settingsAction: #selector(SettingsWindowController.show)
        )

        let delegate = AppDelegate(activationCoordinator: activationCoordinator)
        app.delegate = delegate
        app.run()
    }

    /// Establishes the Settings scene's entry point - Spec #3 §3.5: "a standard macOS Settings
    /// scene, reachable by Cmd-,, like every other Mac app." **Settings is deliberately not a
    /// `Section`** (see that type's own doc comment) - it lives on the app's own main menu instead,
    /// the same place every other Mac app puts it. Also carries the standard "Quit Hades" (Cmd-Q)
    /// item every Mac app has: without it, Cmd-Q does nothing while the main window is frontmost -
    /// the popover's own Quit button only helps when the popover itself is open.
    ///
    /// Harmless to install unconditionally at launch even though this app is `.accessory` most of
    /// the time: an accessory app's main menu is inert until the app is actually active, which by
    /// construction only happens once some window - normally the main window, see
    /// `MainWindowScene.show()` - has already made it `.regular`. Task 7 replaces
    /// `SettingsWindowController`'s placeholder content with the real `SettingsView`; this only
    /// wires the OS-standard shortcut to a real, showable window so the capability is not dead in
    /// the meantime - see this project's own "name the caller" standard, and
    /// `HadesMenuBarAppTests` for the proof this structure is correct.
    ///
    /// The Quit item is bound directly to `NSApplication.terminate(_:)` - the exact selector
    /// `AppDelegate`'s `onQuit = { NSApp.terminate(nil) }` closure already invokes for the popover's
    /// own Quit button - via `target: nil` (standard responder-chain dispatch, same as Xcode's own
    /// default app-menu template). `terminate(_:)` always asks the app delegate first
    /// (`AppDelegate.applicationShouldTerminate(_:)`), so both paths converge on the SAME shutdown
    /// sequence: an adopted core survives, a spawned one is stopped (Plan 12 Task 4 check 7). Not
    /// routed through `MenuBarController`'s own `onQuit` closure - `NSApplication.terminate(_:)` IS
    /// that closure's entire body, so referencing it directly here is the same call, not a
    /// duplicate path to keep in sync.
    static func makeMainMenu(target: AnyObject, settingsAction: Selector) -> NSMenu {
        let mainMenu = NSMenu()

        let appMenuItem = NSMenuItem()
        mainMenu.addItem(appMenuItem)

        let appMenu = NSMenu()
        appMenuItem.submenu = appMenu

        let settingsItem = NSMenuItem(title: "Settings…", action: settingsAction, keyEquivalent: ",")
        settingsItem.target = target
        appMenu.addItem(settingsItem)

        appMenu.addItem(.separator())

        let quitItem = NSMenuItem(title: "Quit Hades", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        quitItem.target = nil
        appMenu.addItem(quitItem)

        return mainMenu
    }
}

/// Vends the single Settings window, the same "create once, reuse, never a second instance" shape
/// `MainWindowScene` establishes for the main window - but deliberately much smaller: no polling, no
/// section state. Settings is only ever reachable once the app is already active (see
/// `makeMainMenu`'s own doc comment), so by the time `show()` can be invoked at all, something else
/// has already made the app `.regular` - but this window is now an equal, not subordinate,
/// participant in THAT decision: it calls `activationCoordinator.windowOpened()`/`.windowClosed()`
/// itself (see `ActivationPolicyCoordinator`'s own doc comment for the Task 2 edge case this fixes -
/// `MainWindowScene` used to be the sole owner of the accessory/regular transition, which was wrong
/// the moment a second window could need `.regular` too).
///
/// Not unit tested, same allowance `MenuBarController` already has: everything below is a direct
/// AppKit call, or a call into the already-tested `SettingsViewModel`/`ActivationPolicyCoordinator`.
@MainActor
private final class SettingsWindowController: NSObject, NSWindowDelegate {
    private let viewModel: SettingsViewModel
    private let activationCoordinator: ActivationPolicyCoordinator
    private var window: NSWindow?

    /// Whether THIS type currently has an outstanding `activationCoordinator.windowOpened()` call -
    /// see `MainWindowScene.isWindowOpen`'s own doc comment for why this, not `window == nil`, is
    /// the right gate: `show()` refreshes and refocuses an already-open Settings window on every
    /// call (see this type's own doc comment on why), and without this flag that second call pushed
    /// `.regular` again with only one eventual `windowWillClose(_:)` to undo it - the exact defect
    /// `MainWindowScene` had, reproduced here too since both types used to call `windowOpened()`
    /// unconditionally.
    private var isWindowOpen = false

    init(viewModel: SettingsViewModel, activationCoordinator: ActivationPolicyCoordinator) {
        self.viewModel = viewModel
        self.activationCoordinator = activationCoordinator
    }

    /// Creates the window once and reuses it thereafter, exactly like `MainWindowScene.show()`.
    /// Refreshes `viewModel` on EVERY call, not just the first - reopening Settings after, say,
    /// occupying port 7823 and relaunching Hades must show the current conflict, not a stale snapshot
    /// from whenever the window was first created.
    @objc func show() {
        if let window {
            if !isWindowOpen {
                isWindowOpen = true
                activationCoordinator.windowOpened()
            }
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
            Task { await viewModel.refresh() }
            return
        }

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 420, height: 340),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false
        )
        window.title = "Settings"
        window.isReleasedWhenClosed = false // reused across closes, exactly like MainWindowScene's window
        window.delegate = self
        window.center()
        window.contentViewController = NSHostingController(rootView: SettingsView(viewModel: viewModel))
        self.window = window

        isWindowOpen = true
        activationCoordinator.windowOpened()
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        Task { await viewModel.refresh() }
    }

    // MARK: - NSWindowDelegate

    /// Mirrors `MainWindowScene.windowWillClose(_:)`: tells the shared coordinator this window
    /// closed. Whether the Dock icon actually disappears now depends on whether the main window is
    /// ALSO closed - see `ActivationPolicyCoordinator`'s own doc comment.
    func windowWillClose(_ notification: Notification) {
        if isWindowOpen {
            isWindowOpen = false
            activationCoordinator.windowClosed()
        }
    }
}
