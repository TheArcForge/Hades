import HadesControl
import HadesSupervision
import Observation

/// Reads a fresh `ControlConnection` (normally `Discovery.read()`), or nil if the discovery file
/// is not there / not readable right now. Always called again on every tick - see
/// `MenuBarViewModel.tick()`'s own doc comment for why that alone is the entire `.staleToken`
/// recovery mechanism.
public typealias ConnectionProvider = @Sendable () async -> ControlConnection?

/// Builds a `ControlSummaryFetching` for a given connection (normally `ControlClient.init`).
public typealias SummaryClientFactory = @Sendable (ControlConnection) -> any ControlSummaryFetching

/// Owns exactly one piece of published state - `content` - and the polling loop that keeps it
/// current. This is the testable orchestration layer Plan 12 Task 3's STANDING RULES call for
/// ("view models and state mapping should be [tested]"): every dependency (`CoreSupervising`,
/// `ConnectionProvider`, `SummaryClientFactory`) is injected so `MenuBarViewModelTests` can drive
/// every required behaviour - staleToken recovery, Release swallowing errors, polling start/stop -
/// without a real process or a real network call. `MenuBarController` (untested AppKit wiring, per
/// the plan's own allowance) is the only thing that constructs this with the REAL
/// `CoreSupervisor`/`Discovery.read`/`ControlClient`.
///
/// Holds no state a view could turn into new display text: `content` is produced entirely by
/// `MenuBarContent.resolve`, which combines nothing - see that type's own doc comment. This type's
/// only job is deciding WHEN to call `resolve` again (on a timer while open, once at launch, and
/// once more after every Release tap) and WHAT to pass it (the supervisor's current state, and the
/// most recent successful `/control/summary` response, verbatim).
@MainActor
@Observable
public final class MenuBarViewModel {
    public private(set) var content: MenuBarContent = .notRunning

    /// Fires with the same value `content` was just set to - `MenuBarController` uses this to keep
    /// the `NSStatusItem` image in sync without needing to hand-roll Observation-framework change
    /// tracking from AppKit code.
    public var onContentChange: ((MenuBarContent) -> Void)?

    private let supervisor: any CoreSupervising
    private let discover: ConnectionProvider
    private let makeClient: SummaryClientFactory
    private let pollInterval: Duration

    /// The most recent successful `/control/summary` response. Cleared the instant the supervisor
    /// is observed NOT running - see `tick()` - so a later return to `.running` never shows a
    /// summary left over from a core that has since died.
    private var lastSummary: SummaryResult?
    private var pollTask: Task<Void, Never>?

    public init(
        supervisor: any CoreSupervising,
        discover: @escaping ConnectionProvider = { Discovery.read() },
        makeClient: @escaping SummaryClientFactory = { ControlClient(connection: $0) },
        pollInterval: Duration = .seconds(1)
    ) {
        self.supervisor = supervisor
        self.discover = discover
        self.makeClient = makeClient
        self.pollInterval = pollInterval
    }

    /// One immediate fetch, independent of the open/closed poll loop below - so the status item
    /// icon reflects reality as soon as `CoreSupervisor.start()` resolves at launch, rather than
    /// sitting on a placeholder glyph until the user's first click opens the dropdown.
    public func bootstrap() async {
        await tick()
    }

    /// Starts the ~1Hz poll loop. Idempotent - calling this while already polling is a no-op.
    /// `CoreSupervisor` runs no timer of its own BY DESIGN (see that type's own `refresh()` doc
    /// comment: "Callers drive the cadence"); this loop, started and stopped by
    /// `MenuBarController` exactly when the dropdown opens and closes, is the only thing that
    /// drives it. A background app has no business polling continuously.
    public func startPolling() {
        guard pollTask == nil else { return }
        pollTask = Task { [weak self] in
            guard let self else { return }
            while !Task.isCancelled {
                await self.tick()
                try? await Task.sleep(for: self.pollInterval)
            }
        }
    }

    /// Stops the poll loop immediately. Safe to call whether or not polling is currently running.
    public func stopPolling() {
        pollTask?.cancel()
        pollTask = nil
    }

    /// `POST /control/leases/{id}/release`. Per `ControlClient.releaseLease(id:)`'s own doc
    /// comment, idempotent and safe to call late (the TTL may already have fired), and a
    /// `success: false` result is never a client-side error to raise either - `message` already
    /// names what happened, and this type does not even look at it. `content` is the only
    /// published state this type has, so "never show an error for it" means exactly this: the
    /// result of the release call, success or thrown `ControlClientError`, is discarded, and the
    /// UI is brought current with one immediate `tick()` afterward rather than waiting for the
    /// next scheduled poll.
    public func release(leaseId: String) async {
        if let connection = await discover() {
            _ = try? await makeClient(connection).releaseLease(id: leaseId)
        }
        await tick()
    }

    /// Re-validates the supervisor, then resolves `content` from its current state plus the most
    /// recent summary. This is the ENTIRE `.staleToken` recovery mechanism: `discover()` is called
    /// fresh on every tick, never cached across ticks, so a token going stale because the core
    /// restarted needs no special-case code at all - the very next tick re-reads the (by then
    /// rewritten) discovery file on its own. Every fetch failure - `.staleToken`, `.transport`,
    /// `.server`, `.decoding` alike - is treated the same way: swallowed, never surfaced as an
    /// error case, leaving `lastSummary` (and therefore `content`) exactly as it was until either
    /// a later tick succeeds or the supervisor itself moves off `.running`.
    private func tick() async {
        await supervisor.refresh()
        let state = await supervisor.state

        guard case .running = state else {
            lastSummary = nil
            update(.resolve(supervisorState: state, lastSummary: nil))
            return
        }

        if let connection = await discover() {
            do {
                lastSummary = try await makeClient(connection).summary()
            } catch {
                // Self-heals next tick - see this method's own doc comment. Nothing to do here.
            }
        }
        // No `else`: a momentarily-unreadable discovery file, while the supervisor still reports
        // .running, keeps whatever `lastSummary` already holds rather than clearing it - one
        // unlucky file read should not flash the UI back to "not running".

        update(.resolve(supervisorState: state, lastSummary: lastSummary))
    }

    private func update(_ newContent: MenuBarContent) {
        content = newContent
        onContentChange?(newContent)
    }
}
