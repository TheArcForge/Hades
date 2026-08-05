import AppKit
import SwiftUI

/// AppKit wiring: the `NSStatusItem` and the `NSPopover` that hosts `MenuBarRootView`. Per this
/// plan's own STANDING RULES ("NSStatusItem wiring may not be [testable]"), this type is not unit
/// tested - everything it does is either a direct AppKit call or a call into the already-tested
/// `MenuBarViewModel`/`MainWindowScene`. Three responsibilities:
///
/// 1. Keep the status item's icon in sync with `viewModel.content` (via `onContentChange`, so this
///    works even while the popover is closed and SwiftUI's own view diffing is not running).
/// 2. Start and stop `viewModel`'s poll loop exactly when the dropdown opens and closes - a
///    background app has no business polling continuously. (Plan 12 Task 3.)
/// 3. Offer "Open Hades" - spec #3 §3.1, verbatim: "The dropdown gives per-project one-line status,
///    attached Editors, **a jump to the main window**, and quit." The dropdown IS the popover, so
///    the primary surface is the "Open Hades" button `MenuBarRootView`/`MenuBarContentView` render
///    next to the existing Quit button. The right-click/secondary-click context menu below
///    (`showOpenHadesMenu`, via `NSMenu.popUp(positioning:at:in:)` rather than assigning
///    `statusItem.menu`, which would hijack ALL clicks including the left-click popover toggle) is
///    kept as a second, harmless path to the same `onOpenHades` closure - not the specified one.
@MainActor
final class MenuBarController: NSObject, NSPopoverDelegate {
    private let statusItem: NSStatusItem
    private let popover: NSPopover
    private let viewModel: MenuBarViewModel
    private let onOpenHades: () -> Void

    init(viewModel: MenuBarViewModel, onQuit: @escaping () -> Void, onOpenHades: @escaping () -> Void) {
        self.viewModel = viewModel
        self.onOpenHades = onOpenHades
        self.statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        self.popover = NSPopover()
        super.init()

        popover.behavior = .transient // closes on outside click, standard menu-bar-dropdown UX
        popover.delegate = self
        popover.contentViewController = NSHostingController(
            rootView: MenuBarRootView(viewModel: viewModel, onOpenHades: onOpenHades, onQuit: onQuit)
        )

        if let button = statusItem.button {
            button.image = statusIcon(for: viewModel.content)
            button.action = #selector(handleStatusItemClick)
            button.target = self
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }

        // Keeps the glyph current even while the popover is closed and polling is deliberately
        // stopped - e.g. the one bootstrap() fetch at launch, or the result of a Release tap.
        viewModel.onContentChange = { [weak self] content in
            self?.statusItem.button?.image = self?.statusIcon(for: content)
        }
    }

    /// Left-click toggles the popover (unchanged from Plan 12 Task 3, and the popover's own "Open
    /// Hades" button is the specified way to reach the main window - spec #3 §3.1). Right-click
    /// shows a second, redundant "Open Hades" context menu - harmless to keep, not the specified
    /// surface; see this type's own doc comment.
    @objc private func handleStatusItemClick() {
        if NSApp.currentEvent?.type == .rightMouseUp {
            showOpenHadesMenu()
            return
        }
        guard let button = statusItem.button else { return }
        if popover.isShown {
            popover.performClose(nil)
        } else {
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            popover.contentViewController?.view.window?.makeKey()
        }
    }

    private func showOpenHadesMenu() {
        guard let button = statusItem.button else { return }
        let menu = NSMenu()
        let openItem = NSMenuItem(title: "Open Hades", action: #selector(handleOpenHades), keyEquivalent: "")
        openItem.target = self
        menu.addItem(openItem)
        menu.popUp(positioning: nil, at: NSPoint(x: 0, y: button.bounds.maxY + 4), in: button)
    }

    @objc private func handleOpenHades() {
        onOpenHades()
    }

    private func statusIcon(for content: MenuBarContent) -> NSImage? {
        NSImage(
            systemSymbolName: StatusIcon.symbolName(for: content),
            accessibilityDescription: "Hades"
        )
    }

    // MARK: - NSPopoverDelegate

    /// Required behaviour: "Polling is ~1 Hz and stops when the menu is closed." `CoreSupervisor`
    /// runs no timer of its own by design - this popover's open/closed lifecycle is the only thing
    /// that starts and stops `viewModel`'s loop.
    func popoverWillShow(_ notification: Notification) {
        viewModel.startPolling()
    }

    func popoverDidClose(_ notification: Notification) {
        viewModel.stopPolling()
    }
}
