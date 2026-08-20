import AppKit
import Testing

@testable import HadesApp

/// Pure tests of `ActivationPolicyCoordinator`'s reference-counting logic - the fix for the edge
/// case Task 2 left open: `MainWindowScene.windowWillClose(_:)` used to call
/// `NSApp.setActivationPolicy(.accessory)` unconditionally, so opening Settings and then closing the
/// main window dropped the Dock icon out from under a still-visible Settings window. A shared
/// instance between `MainWindowScene` and `SettingsWindowController` (see that type's own doc
/// comment and `HadesMenuBarApp.main()`'s wiring) fixes it with a plain reference count - no AppKit
/// rendering needed to prove the count logic itself, only the injected `setActivationPolicy` closure
/// this type already exposes for exactly that reason.
@Suite("ActivationPolicyCoordinator")
@MainActor
struct ActivationPolicyCoordinatorTests {

    @Test("the first window to open switches to .regular")
    func firstOpenSwitchesToRegular() {
        var policies: [NSApplication.ActivationPolicy] = []
        let coordinator = ActivationPolicyCoordinator(setActivationPolicy: { policies.append($0) })

        coordinator.windowOpened()

        #expect(policies == [.regular])
    }

    @Test("the last window to close switches back to .accessory")
    func lastCloseSwitchesToAccessory() {
        var policies: [NSApplication.ActivationPolicy] = []
        let coordinator = ActivationPolicyCoordinator(setActivationPolicy: { policies.append($0) })

        coordinator.windowOpened()
        coordinator.windowClosed()

        #expect(policies == [.regular, .accessory])
    }

    @Test("closing one of two owned windows does not revert to .accessory - only closing BOTH does (the edge case this type fixes)")
    func closingOneOfTwoOwnedWindowsDoesNotRevertYet() {
        var policies: [NSApplication.ActivationPolicy] = []
        let coordinator = ActivationPolicyCoordinator(setActivationPolicy: { policies.append($0) })

        coordinator.windowOpened() // e.g. the main window opens
        coordinator.windowOpened() // e.g. Settings opens too, while the main window is still up
        coordinator.windowClosed() // the main window closes

        #expect(policies == [.regular, .regular], "Settings is still open - must stay regular, not drop to accessory")

        coordinator.windowClosed() // Settings closes too

        #expect(policies == [.regular, .regular, .accessory], "now both have closed - reverts")
    }

    @Test("windowClosed() called more times than windowOpened() never underflows or double-fires .accessory")
    func closeIsClampedAtZero() {
        var policies: [NSApplication.ActivationPolicy] = []
        let coordinator = ActivationPolicyCoordinator(setActivationPolicy: { policies.append($0) })

        coordinator.windowOpened()
        coordinator.windowClosed()
        coordinator.windowClosed() // a stray extra close

        #expect(policies == [.regular, .accessory])
    }
}
