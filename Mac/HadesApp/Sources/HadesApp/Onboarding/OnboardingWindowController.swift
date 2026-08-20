import AppKit
import SwiftUI

/// Vends the one first-run onboarding window - the same "create once, reuse, never a second
/// instance" shape `MainWindowScene`/`SettingsWindowController` already establish, including the
/// exact `isWindowOpen` gating and `activationCoordinator.windowOpened()`/`windowClosed()` calls
/// those two hold to (Plan 14 Task 6's own baseline: "do not undo verified fixes" names
/// `isWindowOpen` gating and `window.contentMinSize` specifically). Unlike those two, this window is
/// never reopened once onboarding completes: `AppDelegate` is `OnboardingWindowController`'s only
/// caller, invoked once, at launch, gated on `UserDefaultsOnboardingStore().hasCompletedOnboarding`
/// being false - see `AppDelegate`'s own doc comment on "the caller."
///
/// Not unit tested, the same allowance `MenuBarController`/`SettingsWindowController` already have:
/// everything below is a direct AppKit call, or a call into the already-tested `OnboardingViewModel`.
@MainActor
final class OnboardingWindowController: NSObject, NSWindowDelegate {
    private let viewModel: OnboardingViewModel
    private let activationCoordinator: ActivationPolicyCoordinator
    private var window: NSWindow?

    /// See `MainWindowScene.isWindowOpen`'s own doc comment for why this flag, not `window == nil`,
    /// is the right gate against double-counting `activationCoordinator.windowOpened()`.
    private var isWindowOpen = false

    init(viewModel: OnboardingViewModel, activationCoordinator: ActivationPolicyCoordinator) {
        self.viewModel = viewModel
        self.activationCoordinator = activationCoordinator
    }

    func show() {
        if let window {
            if !isWindowOpen {
                isWindowOpen = true
                activationCoordinator.windowOpened()
            }
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
            return
        }

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 640, height: 520),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false
        )
        window.title = "Welcome to Hades"
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.center()
        // Same floor-clamp fix `MainWindowScene`'s own doc comment documents at length, applied
        // here for the same reason: an `NSHostingController` assigned before any SwiftUI layout
        // pass has run can otherwise collapse this window's frame well below anything usable.
        window.contentMinSize = NSSize(width: 640, height: 520)
        window.contentViewController = NSHostingController(
            rootView: OnboardingRootView(viewModel: viewModel, onFinished: { [weak self] in self?.close() })
        )
        self.window = window

        isWindowOpen = true
        activationCoordinator.windowOpened()
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    private func close() {
        window?.close()
    }

    // MARK: - NSWindowDelegate

    func windowWillClose(_ notification: Notification) {
        if isWindowOpen {
            isWindowOpen = false
            activationCoordinator.windowClosed()
        }
    }
}

/// The window's whole content: the current step's view, plus a shared footer (step indicator +
/// "Continue"/"Finish" button) every step advances through identically - see `OnboardingStep`'s own
/// doc comment for why the last step's button says "Finish" but calls the exact same
/// `viewModel.advance()` every other step's "Continue" does. `onFinished` is called once, the
/// instant `viewModel.isComplete` flips true, so `OnboardingWindowController` can close the window
/// without this view needing to know anything about `NSWindow`.
struct OnboardingRootView: View {
    let viewModel: OnboardingViewModel
    let onFinished: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                stepContent
                    .padding(24)
            }
            Divider()
            HStack {
                Text("Step \(viewModel.currentStep.rawValue + 1) of \(OnboardingStep.allCases.count) — \(viewModel.currentStep.title)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer()
                Button(viewModel.currentStep == .unityPlugin ? "Finish" : "Continue") {
                    viewModel.advance()
                }
                .keyboardShortcut(.defaultAction)
            }
            .padding()
        }
        .frame(minWidth: 640, minHeight: 520)
        .onChange(of: viewModel.isComplete) { _, isComplete in
            if isComplete { onFinished() }
        }
    }

    @ViewBuilder
    private var stepContent: some View {
        switch viewModel.currentStep {
        case .install:
            OnboardingInstallStepView()
        case .permissions:
            OnboardingPermissionsStepView()
        case .claudeCode:
            OnboardingClaudeCodeStepView(viewModel: viewModel)
        case .projects:
            OnboardingProjectsStepView(viewModel: viewModel)
        case .unityPlugin:
            OnboardingUnityPluginStepView(viewModel: viewModel)
        }
    }
}
