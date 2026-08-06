import Foundation

/// Whether first-run onboarding has already completed - an app-owned preference with no `.NET`
/// counterpart at all: the control API has no notion of "has this installation of Hades.app been
/// onboarded," and spec #3 §3.6's flow is entirely a Swift-shell concern. Behind a protocol for
/// exactly the same reason `LaunchAtLoginReading`/`ResourceGuardReading` are: so
/// `OnboardingViewModelTests` never has to touch real, persistent `UserDefaults` state (which would
/// leak between test runs on the same machine) - see `UserDefaultsOnboardingStore`'s own doc comment.
///
/// "Onboarding step state - which step you're on, whether one is complete - is view state and is
/// fine" (Plan 14 Task 6's own instruction). This protocol is that allowance, applied.
@MainActor
public protocol OnboardingCompletionTracking {
    /// `true` once `markCompleted()` has ever been called. Read once at launch by `AppDelegate` -
    /// see that type's own doc comment on "the caller" - to decide whether onboarding should appear
    /// at all.
    var hasCompletedOnboarding: Bool { get }

    /// Records that onboarding finished. Called exactly once, by `OnboardingViewModel.advance()`
    /// when it moves past the last step - see that method's own doc comment.
    func markCompleted()
}

/// The real `OnboardingCompletionTracking`, backed by `UserDefaults.standard` under this app's own
/// bundle domain (`com.arcforge.hades.shell` - see `scripts/build-app.sh`'s own `Info.plist`), never
/// shared with, or read by, any other program. Not unit tested itself - one line each way, the same
/// "nothing to unit test" allowance `LaunchAtLoginService`/`NSOpenPanelDirectoryPicker` already have;
/// `OnboardingViewModelTests` fakes the protocol instead (`FakeOnboardingCompletionTracking` in
/// `Tests/HadesAppTests/Support/TestSupport.swift`).
@MainActor
public struct UserDefaultsOnboardingStore: OnboardingCompletionTracking {
    private static let key = "HadesOnboardingCompleted"
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    public var hasCompletedOnboarding: Bool {
        defaults.bool(forKey: Self.key)
    }

    public func markCompleted() {
        defaults.set(true, forKey: Self.key)
    }
}
