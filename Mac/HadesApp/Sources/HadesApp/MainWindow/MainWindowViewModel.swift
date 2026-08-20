import HadesSupervision
import Observation

/// Owns navigation and the polling LIFECYCLE for the main window - nothing else. Two things:
/// which sidebar `Section` is selected, and the shell-level poll loop that runs only while the
/// window is open.
///
/// Deliberately holds no Projects/Traces/Memory business data of its own, and never will - per spec
/// #3 §1 ("Swift renders, .NET decides") and this project's own settled decision on data ownership:
/// each section gets its own view model that owns its own fetch (`ProjectsViewModel` in Task 3, the
/// same shape for Traces and Memory later). This type's only job is deciding WHEN a refresh happens
/// (tied to window visibility, via `startPolling`/`stopPolling`) and WHICH section's view model gets
/// it - see `refreshSelectedSection` below. Keeping that split here, instead of letting this type
/// grow a `project`/`traces`/`memory` property per section as those tasks land, is what stops it
/// becoming a god object.
///
/// The supervisor half of polling - `CoreSupervisor.refresh()` - is the exact same "callers drive
/// the cadence" contract `MenuBarViewModel` already established (see `CoreSupervisor.refresh()`'s
/// own doc comment, which anticipates exactly this: "Callers drive the cadence (e.g. the menu bar's
/// own ~1Hz poll while its window is open)" - the main window is a second such caller, independent
/// of the popover's own loop). `CoreSupervisor` itself runs no timer - a closed window, like a
/// closed popover, has no business polling.
@MainActor
@Observable
public final class MainWindowViewModel {
    public private(set) var selectedSection: Section = .projects

    /// Called once per tick, right after `supervisor.refresh()`, with whichever `Section` is
    /// CURRENTLY selected - never all three, so an unselected section gets exactly as much
    /// background polling as a closed window does: none. `nil` (the default) until Task 3
    /// (`ProjectsViewModel`), 5, or 6 sets it - deliberately not implemented here, since
    /// `ProjectsViewModel`'s own shape is that task's decision, not this one's. Not `@Observable`
    /// state (nothing renders it) - it is a dependency, wired once by whatever constructs a section's
    /// view model, the same way `onContentChange` is wired once by `MenuBarController`.
    public var refreshSelectedSection: (@MainActor (Section) async -> Void)?

    private let supervisor: any CoreSupervising
    private let pollInterval: Duration
    private var pollTask: Task<Void, Never>?

    public init(supervisor: any CoreSupervising, pollInterval: Duration = .seconds(1)) {
        self.supervisor = supervisor
        self.pollInterval = pollInterval
    }

    /// Changes the sidebar selection. Called by the sidebar's own selection binding, and by
    /// `MainWindowScene.show()` to enforce "opening the window always selects Projects."
    public func select(_ section: Section) {
        selectedSection = section
    }

    /// Starts the poll loop. Idempotent - calling this while already polling is a no-op, exactly
    /// mirroring `MenuBarViewModel.startPolling()`'s own guard. `MainWindowScene.show()` is the one
    /// real caller.
    public func startPolling() {
        guard pollTask == nil else { return }
        pollTask = Task { [weak self] in
            guard let self else { return }
            while !Task.isCancelled {
                await self.supervisor.refresh()
                if let refreshSelectedSection = self.refreshSelectedSection {
                    await refreshSelectedSection(self.selectedSection)
                }
                try? await Task.sleep(for: self.pollInterval)
            }
        }
    }

    /// Stops the poll loop immediately. Safe to call whether or not polling is currently running.
    /// `MainWindowScene.windowWillClose(_:)` is the one real caller.
    public func stopPolling() {
        pollTask?.cancel()
        pollTask = nil
    }
}
