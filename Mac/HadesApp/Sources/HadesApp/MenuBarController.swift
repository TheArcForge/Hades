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
        let hostingController = NSHostingController(
            rootView: MenuBarRootView(viewModel: viewModel, onOpenHades: onOpenHades, onQuit: onQuit)
        )
        // Without this, `NSHostingController.preferredContentSize` stays (0, 0) until its view has
        // actually been laid out inside a window - which does not happen until `show(relativeTo:
        // of:preferredEdge:)` itself inserts it. `show` reads whatever size is available RIGHT THEN
        // to anchor the popover to `button`, so with no size known yet it anchors using (0, 0) and
        // only resizes to the real SwiftUI content size (data already loaded from `bootstrap()`, not
        // a timing race with the network) a moment later - a resize that does not re-anchor to
        // `button`, leaving the window offset from it by roughly the content's own height every
        // single time (confirmed: identical on the first open AND a second open/close cycle in the
        // same run, so this is not a one-off launch race). `.preferredContentSize` makes
        // `NSHostingController` compute and keep that property current continuously, so `show` has
        // the real size up front and anchors correctly the first time.
        hostingController.sizingOptions = [.preferredContentSize]
        popover.contentViewController = hostingController

        if let button = statusItem.button {
            button.image = statusIcon()
            button.action = #selector(handleStatusItemClick)
            button.target = self
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }

        // The glyph itself never varies with content (see `statusIcon()`'s own doc comment), but
        // the button's image still needs setting at least once before `viewModel.content` exists -
        // reusing the same content-change hook the popover's data relies on is simpler than a
        // special-cased one-shot assignment.
        viewModel.onContentChange = { [weak self] _ in
            self?.statusItem.button?.image = self?.statusIcon()
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

    private func statusIcon() -> NSImage? {
        render(accessibilityDescription: "Hades")
    }

    /// Draws the menu-bar glyph: a bare "H", nothing else - no enclosing shape, no badge, no
    /// per-state variant. Per-project/per-state detail lives entirely in the popover this status
    /// item opens (see `MenuBarContentView`, which uses `StatusIcon.symbolName(for:)` for icons
    /// that sit next to their own text) - the icon itself must read as Hades regardless of state,
    /// so it no longer varies with it. SF Symbols has no unadorned single-letter glyph (only
    /// container variants like `h.square`/`h.circle` - verified empirically, neither `"h"` nor
    /// `"H"` resolves), so this draws the character directly instead of compositing an SF Symbol.
    /// `isTemplate = true` is what makes AppKit re-tint the drawn shape (using only this image's
    /// alpha, never the black drawn here) for light menu bars, dark menu bars, and the
    /// highlighted/selected state - there is no colour baked in anywhere in this image.
    private func render(accessibilityDescription: String) -> NSImage? {
        let canvas: CGFloat = 18 // standard menu-bar glyph size

        let image = NSImage(size: NSSize(width: canvas, height: canvas))
        image.lockFocus()

        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 13, weight: .semibold),
            .foregroundColor: NSColor.black,
        ]
        let glyph = "H" as NSString
        let glyphSize = glyph.size(withAttributes: attributes)
        glyph.draw(
            at: NSPoint(x: (canvas - glyphSize.width) / 2, y: (canvas - glyphSize.height) / 2),
            withAttributes: attributes)

        image.unlockFocus()
        image.isTemplate = true
        image.accessibilityDescription = accessibilityDescription
        return image
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
