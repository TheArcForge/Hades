import AppKit

/// Coordinates `NSApp`'s activation policy across every window that needs `.regular` while open -
/// today the main window (`MainWindowScene`) and Settings (`SettingsWindowController`). Fixes the
/// edge case Task 2 left open: with each window independently flipping `.accessory` on its own
/// close, opening Settings and then closing the main window dropped the Dock icon out from under a
/// still-visible Settings window (Settings never touched activation policy at all before this type
/// existed - `MainWindowScene` was the sole, and wrongly sole, owner). A plain reference count is
/// enough - `.regular` the instant any owned window opens, `.accessory` only once every owned window
/// this coordinator was told about has also closed.
///
/// One shared instance, constructed once in `HadesMenuBarApp.main()` and passed to BOTH
/// `MainWindowScene` and `SettingsWindowController` - see that composition root for the actual
/// wiring. Neither window type calls `NSApp.setActivationPolicy` directly anymore; both go through
/// this type, so the accessory/regular transition has exactly one owner, auditable in one place -
/// the same "isolated so it is auditable in one place" reasoning this plan already applies to
/// `ShellFacts/`.
@MainActor
public final class ActivationPolicyCoordinator {
    private var openWindowCount = 0
    private let setActivationPolicy: @MainActor (NSApplication.ActivationPolicy) -> Void

    public init(
        setActivationPolicy: @escaping @MainActor (NSApplication.ActivationPolicy) -> Void = {
            NSApp.setActivationPolicy($0)
        }
    ) {
        self.setActivationPolicy = setActivationPolicy
    }

    /// Call when an owned window opens (becomes visible). Always ensures `.regular` - safe to call
    /// even when already regular (e.g. a second owned window opening while the first is still up).
    public func windowOpened() {
        openWindowCount += 1
        setActivationPolicy(.regular)
    }

    /// Call when an owned window closes. Reverts to `.accessory` only once EVERY owned window this
    /// coordinator was told about has also closed - never on the first of several to close. A stray
    /// extra call (more closes than opens) is a true no-op - it neither goes negative nor re-fires
    /// `.accessory` a second time.
    public func windowClosed() {
        guard openWindowCount > 0 else { return }
        openWindowCount -= 1
        if openWindowCount == 0 {
            setActivationPolicy(.accessory)
        }
    }
}
