import HadesSupervision

/// The narrow slice of `CoreSupervisor` that `MenuBarViewModel` needs: read the current state, and
/// ask it to re-validate an adopted core (see `CoreSupervisor.refresh()`'s own doc comment - "the
/// menu bar's own ~1Hz poll while its window is open" is written as if this exact protocol already
/// existed). Deliberately excludes `start()`/`stop()`: those are app-lifecycle calls the
/// composition root (`AppDelegate`) makes directly on the concrete `CoreSupervisor`, never through
/// the view model.
///
/// Existing purely so tests can fake `CoreSupervisor` without spawning a real process - see
/// `FakeCoreSupervisor` in `Tests/HadesAppTests/Support/TestSupport.swift`. `CoreSupervisor`
/// itself needed no changes to conform (empty extension below): its `state`/`refresh()` already
/// match this signature exactly.
public protocol CoreSupervising: Sendable {
    var state: CoreSupervisor.State { get async }
    func refresh() async
}

extension CoreSupervisor: CoreSupervising {}
