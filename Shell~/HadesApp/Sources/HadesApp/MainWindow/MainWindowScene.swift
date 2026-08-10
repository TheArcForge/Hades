import AppKit
import SwiftUI

/// AppKit wiring: the single `NSWindow` "Open Hades" jumps to (Spec #3 §3.1's "a jump to the main
/// window" - phase one shipped the menu bar with nowhere for that jump to land). Parallel in spirit
/// to `MenuBarController`'s `NSStatusItem`/`NSPopover` wiring, but - unlike that type - structured
/// so its lifecycle rules ARE unit-testable without a rendered UI: every AppKit side effect
/// (creating the window, focusing it, switching activation policy) is injected as a closure or
/// collaborator with a real default, so `MainWindowSceneTests` can observe what THIS type decided to
/// do without depending on the WindowServer actually drawing anything. See `MainWindowSceneTests`'
/// own doc comment for exactly which of this type's contracts that proves, and which ones only Task
/// 8's hand-run pass can.
///
/// Three responsibilities, all required by this plan:
///
/// 1. **Vend one window, created once, reused forever after.** `window` is set on the first
///    `show()` and never cleared - `isReleasedWhenClosed = false` on the real default window means
///    closing it (the red traffic light, or Cmd-W) does not deallocate it, so a later `show()`
///    reuses and refocuses the exact same instance rather than building a second one.
/// 2. **Tie `MainWindowViewModel`'s poll loop to the window's own visibility** - polling while
///    shown, stopped the instant it closes. A background menu-bar app with no window open has no
///    business polling, exactly the discipline `MenuBarController` already applies to the popover.
/// 3. **Participate in shared activation-policy ownership, not sole ownership.** Task 2 made this
///    type the ONLY thing that ever called `NSApp.setActivationPolicy` - correct until Settings
///    (Task 7) became a second window that also needs `.regular` while open. This type now calls
///    `activationCoordinator.windowOpened()`/`.windowClosed()` instead of setting policy directly;
///    see `ActivationPolicyCoordinator`'s own doc comment for the edge case that fixes (closing the
///    main window while Settings was still open used to drop the Dock icon out from under it).
@MainActor
public final class MainWindowScene: NSObject, NSWindowDelegate {
    private let viewModel: MainWindowViewModel
    private let projectsViewModel: ProjectsViewModel
    private let migrationCleanupViewModel: MigrationCleanupViewModel
    private let tracesViewModel: TracesViewModel
    private let memoryViewModel: MemoryViewModel
    private let makeWindow: @MainActor () -> NSWindow
    private let focusWindow: @MainActor (NSWindow) -> Void
    private let activationCoordinator: ActivationPolicyCoordinator

    /// Reused across every `show()` call after the first - see this type's own doc comment. Not
    /// exposed outside this type: `MainWindowSceneTests` proves reuse via the injected `makeWindow`/
    /// `focusWindow` closures' own call counts, never by reading this property directly.
    private var window: NSWindow?

    /// Whether THIS type currently has an outstanding `activationCoordinator.windowOpened()` call -
    /// i.e. whether `windowClosed()` is still owed. `show()` is safely callable any number of times
    /// while the window is already open (that is the whole point of "created once and reused" -
    /// `MainWindowSceneTests.showCreatesOnceAndReuses` calls it three times in a row), but the
    /// coordinator's contract is one `windowOpened()` per eventual `windowClosed()` - see that type's
    /// own doc comment. Gating on THIS flag, not on `window == nil`, is what keeps those two facts
    /// compatible: without it, a second `show()` while already visible (e.g. "Open Hades" clicked
    /// again from the menu bar) pushes `.regular` a second time with only one `windowWillClose(_:)`
    /// ever coming to undo it, so the count never reaches zero and the Dock icon never goes away -
    /// this was reproduced live and is that defect's actual root cause.
    private var isWindowOpen = false

    /// `projectsViewModel`/`tracesViewModel`/`memoryViewModel` default to a fresh instance each so
    /// every existing `MainWindowSceneTests` call site - none of which cares about Projects/Traces/
    /// Memory data, only AppKit window lifecycle - keeps compiling unchanged. The composition root
    /// (`AppDelegate`) passes the SAME instances it also wires into `viewModel.refreshSelectedSection`,
    /// so the instance this view renders is the one polling keeps current. `activationCoordinator`
    /// defaults to a fresh instance so every existing test keeps compiling too; the composition root
    /// (`HadesMenuBarApp.main()`) passes the SAME instance it also gives `SettingsWindowController` -
    /// see `ActivationPolicyCoordinator`'s own doc comment for why that sharing is the whole fix.
    public init(
        viewModel: MainWindowViewModel,
        projectsViewModel: ProjectsViewModel = ProjectsViewModel(),
        migrationCleanupViewModel: MigrationCleanupViewModel = MigrationCleanupViewModel(),
        tracesViewModel: TracesViewModel = TracesViewModel(),
        memoryViewModel: MemoryViewModel = MemoryViewModel(),
        makeWindow: (@MainActor () -> NSWindow)? = nil,
        focusWindow: @escaping @MainActor (NSWindow) -> Void = MainWindowScene.focusDefaultWindow,
        activationCoordinator: ActivationPolicyCoordinator = ActivationPolicyCoordinator()
    ) {
        self.viewModel = viewModel
        self.projectsViewModel = projectsViewModel
        self.migrationCleanupViewModel = migrationCleanupViewModel
        self.tracesViewModel = tracesViewModel
        self.memoryViewModel = memoryViewModel
        self.makeWindow =
            makeWindow ?? {
                MainWindowScene.makeDefaultWindow(
                    viewModel: viewModel, projectsViewModel: projectsViewModel, migrationCleanupViewModel: migrationCleanupViewModel,
                    tracesViewModel: tracesViewModel, memoryViewModel: memoryViewModel)
            }
        self.focusWindow = focusWindow
        self.activationCoordinator = activationCoordinator
    }

    /// Called by "Open Hades" (`MenuBarController`). Creates the window once; every later call
    /// reuses and refocuses that SAME instance - see `MainWindowSceneTests.showCreatesOnceAndReuses`
    /// for the proof this does not depend on a rendered UI. Always resets the sidebar selection to
    /// Projects (this plan's own explicit requirement: "Opening the window from the menu bar selects
    /// Projects"), regardless of whichever section was showing when the window last closed.
    public func show() {
        let window: NSWindow
        if let existing = self.window {
            window = existing
        } else {
            window = makeWindow()
            window.delegate = self
            self.window = window
        }
        viewModel.select(.projects)
        if !isWindowOpen {
            isWindowOpen = true
            activationCoordinator.windowOpened()
        }
        focusWindow(window)
        viewModel.startPolling()
    }

    // MARK: - NSWindowDelegate

    /// A background menu-bar app with a closed window has no business polling - see this type's own
    /// doc comment. Whether the Dock icon also disappears now depends on whether Settings is ALSO
    /// closed - `activationCoordinator.windowClosed()` decides that, not this method. AppKit calls
    /// this for both a user-initiated close (the red traffic light, Cmd-W) and a programmatic one;
    /// this method itself is directly callable too (as `MainWindowSceneTests` does), since it is
    /// ordinary Swift dispatch, not a simulated AppKit event.
    public func windowWillClose(_ notification: Notification) {
        viewModel.stopPolling()
        if isWindowOpen {
            isWindowOpen = false
            activationCoordinator.windowClosed()
        }
    }

    // MARK: - Real defaults (composition-root path only; never exercised by tests, which inject fakes)

    private static func makeDefaultWindow(
        viewModel: MainWindowViewModel, projectsViewModel: ProjectsViewModel, migrationCleanupViewModel: MigrationCleanupViewModel,
        tracesViewModel: TracesViewModel, memoryViewModel: MemoryViewModel
    ) -> NSWindow {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 900, height: 600),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Hades"
        // Hard floor, enforced by AppKit itself on every frame-setting path (not just the ones this
        // type calls directly) - see this type's own doc comment for why this is the fix, not the
        // `NSHostingController`/`NSHostingView` timing race underneath it. Set BEFORE
        // `contentViewController` below: that assignment itself immediately asks the not-yet-laid-out
        // hosting view for a size and applies it synchronously (confirmed live - the window collapses
        // to a ~0pt content size in that exact call, every time, before this method even returns), so
        // the floor has to already be in place for AppKit to clamp that first attempt. Recovery from
        // the collapse then depends on however many asynchronous SwiftUI layout passes happen to run
        // before the window is considered "settled" - sometimes that lands back on 900x600, sometimes
        // it stalls partway (592x84 reproduced live, 10/10 fresh launches). `contentMinSize` does not
        // depend on that sequence completing correctly: every frame this window is ever given, on
        // every path that sets one, is clamped to at least this size, so the collapse - partial or
        // total - cannot produce a visible frame smaller than 900x600, regardless of which of those
        // asynchronous passes has or hasn't run yet.
        window.contentMinSize = NSSize(width: 900, height: 600)
        window.center()
        // Keeps the Swift NSWindow object alive across a close - see this type's own doc comment on
        // why that is exactly what "created once and reused" requires.
        window.isReleasedWhenClosed = false
        window.contentViewController = NSHostingController(
            rootView: MainWindowContentView(
                viewModel: viewModel, projectsViewModel: projectsViewModel, migrationCleanupViewModel: migrationCleanupViewModel,
                tracesViewModel: tracesViewModel, memoryViewModel: memoryViewModel)
        )
        return window
    }

    public static func focusDefaultWindow(_ window: NSWindow) {
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }
}

/// The window's own content: a sidebar listing the three `Section` destinations, and a detail area.
/// Projects (Task 3) renders the real `ProjectsView`; Traces (Task 5) renders the real `TracesView`;
/// Memory (this plan's Task 6) renders the real `MemoryView`, replacing the placeholder
/// (`Text(section.title)`) Task 2 left it as. Reads `viewModel.selectedSection` directly, same
/// no-property-wrapper-needed pattern `MenuBarRootView` already uses for `MenuBarViewModel` -
/// Observation tracks the read regardless.
private struct MainWindowContentView: View {
    let viewModel: MainWindowViewModel
    let projectsViewModel: ProjectsViewModel
    let migrationCleanupViewModel: MigrationCleanupViewModel
    let tracesViewModel: TracesViewModel
    let memoryViewModel: MemoryViewModel

    var body: some View {
        NavigationSplitView {
            List(Section.allCases, id: \.self, selection: selection) { section in
                Text(section.title)
            }
        } detail: {
            switch viewModel.selectedSection {
            case .projects:
                ProjectsView(viewModel: projectsViewModel, migrationCleanupViewModel: migrationCleanupViewModel)
            case .traces:
                TracesView(viewModel: tracesViewModel)
            case .memory:
                MemoryView(viewModel: memoryViewModel)
            }
        }
    }

    private var selection: Binding<Section?> {
        Binding(
            get: { viewModel.selectedSection },
            set: { newValue in
                if let newValue { viewModel.select(newValue) }
            }
        )
    }
}
