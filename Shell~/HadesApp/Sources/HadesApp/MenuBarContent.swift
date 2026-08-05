import HadesControl
import HadesSupervision

/// Everything the menu bar needs to render, collapsed into one value the views switch on. Per
/// spec #3 §1 ("Swift renders, .NET decides"), the ONLY decision made here is which of these four
/// CASES applies - driven entirely by `CoreSupervisor.State`, a Swift-native concept with no .NET
/// equivalent (see `CoreSupervisor`'s own doc comment: "there is no .NET equivalent for 'is a
/// local process running'", so supervision genuinely has to live in Swift). Once the case is
/// `.running`, the payload is the control API's own `SummaryResult`, carried through completely
/// unchanged - `resolve` never reads inside it, never reformats a field, never picks a row. Every
/// view downstream of this type prints fields off that `SummaryResult` verbatim. This is the
/// entire "state mapping" surface Plan 12 Task 3 requires to be unit-tested - see
/// `MenuBarContentTests` for a case-by-case proof.
public enum MenuBarContent: Equatable, Sendable {
    /// No core to ask: supervision has not started, is starting, or (see `resolve`'s own doc
    /// comment on the `.running` branch) a `.running` core has not yet answered
    /// `/control/summary` even once.
    case notRunning

    /// A spawned core died and `CoreSupervisor` is retrying with backoff. `attempt` is
    /// `CoreSupervisor.State.restarting`'s own 1-based counter, carried verbatim.
    case restarting(attempt: Int)

    /// Every restart attempt failed. Terminal until the app restarts supervision. `attempts` is
    /// `CoreSupervisor.State.failed`'s own count, carried verbatim.
    case failed(attempts: Int)

    /// A core is running and has answered `/control/summary` at least once. `ownership` says
    /// whether quitting the app would stop it (see `CoreSupervisor.Ownership`'s own doc comment);
    /// `summary` is that response, untouched.
    case running(ownership: CoreSupervisor.Ownership, summary: SummaryResult)

    /// The pure `supervisorState` + `lastSummary` -> `MenuBarContent` mapping: no I/O, no async,
    /// nothing but a switch. `lastSummary` is whatever the caller's most recent successful
    /// `/control/summary` fetch produced, or nil if there has never been one; this function does
    /// not fetch anything itself, so callers must clear it once `supervisorState` moves off
    /// `.running` - see `MenuBarViewModel.tick()` for the one real caller, which does exactly
    /// that (a stale summary from a now-dead core must not survive into a later `.running`).
    public static func resolve(supervisorState: CoreSupervisor.State, lastSummary: SummaryResult?) -> MenuBarContent {
        switch supervisorState {
        case .notStarted, .starting:
            return .notRunning
        case .restarting(let attempt):
            return .restarting(attempt: attempt)
        case .failed(let attempts):
            return .failed(attempts: attempts)
        case .running(let ownership):
            guard let lastSummary else { return .notRunning }
            return .running(ownership: ownership, summary: lastSummary)
        }
    }
}
