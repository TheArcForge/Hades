import Foundation
import HadesControl

/// Decides whether the Hades core is already running, adopts it if so, spawns it if not, restarts
/// it if a spawned instance dies, and guarantees a spawned instance never outlives the process
/// that spawned it (see `HadesCoreReaper`'s own doc comment for exactly how).
///
/// This type is the one place in the shell that is NOT a thin renderer of the control API (spec
/// #3 §1, "Swift renders, .NET decides"): there is no .NET equivalent for "is a local process
/// running", so supervision genuinely has to live in Swift. Everything it decides is exposed
/// through `state` and `currentOwnership` - it holds no other state, and formats no strings.
public actor CoreSupervisor {

    /// Whether the app itself started the current core, or found one already running.
    /// `.adopted` is the load-bearing case: the app does not own that core's lifecycle, so
    /// quitting - even after exhausting restart attempts on some OTHER, later-spawned core - must
    /// never kill it. See `stop()`.
    public enum Ownership: Equatable, Sendable {
        case adopted
        case spawned
    }

    public enum State: Equatable, Sendable {
        /// `start()` has not been called yet, or `refresh()` noticed an adopted core is gone (see
        /// `refresh()`'s own doc comment for why that reuses this case rather than a new one).
        case notStarted
        case starting
        case running(Ownership)
        /// A spawned core died and a bounded restart-with-backoff sequence is in progress.
        /// `attempt` is 1-based and counts the spawn attempt currently being waited on.
        case restarting(attempt: Int)
        /// `maxRestartAttempts` spawn attempts were made and none produced a core that answered
        /// `/control/ping` in time. Terminal until `start()` is called again. Never reachable from
        /// a failed ADOPT attempt - adoption failing just means "not running", which spawns
        /// instead of failing (see `start()`).
        case failed(attempts: Int)
    }

    public struct Configuration: Sendable {
        /// `HADES_HOME` override, matching `Discovery.read`'s own default of reading the real
        /// environment variable. Threaded explicitly through every `Discovery.read` call this type
        /// makes (rather than relying on THAT call's own default) so a spawned core is always
        /// pointed at the exact same root this supervisor itself reads back.
        public var home: String?

        /// The core's executable and arguments - e.g. `dotnet` and
        /// `["run", "--project", "<repo>/App~/src/Hades.Server", "--no-launch-profile", "--",
        /// <projectPaths>...]` for a real launch. Deliberately not hardcoded here - see the Task 2
        /// report for what phase one assumes about launching the core, and why this is a plain,
        /// swappable `URL`/`[String]` pair rather than a baked-in constant.
        public var coreExecutable: URL
        public var coreArguments: [String]

        /// Extra environment variables merged over the current process's own environment before
        /// spawning. `HADES_HOME` is set automatically from `home` when `home` is non-nil -
        /// callers do not need to duplicate it here.
        public var extraEnvironment: [String: String]

        /// The built `HadesCoreReaper` binary - see `BuildProducts.executable(named:)` for how
        /// tests locate it, and that type's own doc comment for why a real app needs a different
        /// mechanism (an app bundle has its own, unrelated layout).
        public var reaperExecutable: URL

        public var maxRestartAttempts: Int
        public var backoff: @Sendable (_ attempt: Int) -> Duration
        public var pingTimeout: Duration
        public var pingPollInterval: Duration
        public var adoptionProbeTimeout: Duration

        /// How long a spawned core must stay `.running` before its death is treated as a FRESH
        /// problem (attempt budget resets) rather than a continuation of whatever already-diagnosed
        /// problem produced the previous death (attempt budget keeps depleting). See
        /// `handleCoreProcessExit`'s own doc comment for why this exists: without it, a core that
        /// answers one ping and then dies moments later - the Plan 13 Task 8 spawn-loop bug, and not
        /// only from the specific race that bug happened to be caused by - gets a brand new 5-attempt
        /// budget on every single death, and `maxRestartAttempts` never actually binds. Three seconds
        /// is comfortably past any plausible "answered ping while already doomed" window (measured at
        /// ~100ms) while still being short enough that a core which genuinely recovers is not kept
        /// on a depleting budget for long.
        public var minimumStableUptime: Duration

        public init(
            home: String? = ProcessInfo.processInfo.environment["HADES_HOME"],
            coreExecutable: URL,
            coreArguments: [String],
            extraEnvironment: [String: String] = [:],
            reaperExecutable: URL,
            maxRestartAttempts: Int = 5,
            backoff: @escaping @Sendable (_ attempt: Int) -> Duration = Configuration.defaultBackoff,
            pingTimeout: Duration = .seconds(15),
            pingPollInterval: Duration = .milliseconds(200),
            adoptionProbeTimeout: Duration = .seconds(2),
            minimumStableUptime: Duration = .seconds(3)
        ) {
            self.home = home
            self.coreExecutable = coreExecutable
            self.coreArguments = coreArguments
            self.extraEnvironment = extraEnvironment
            self.reaperExecutable = reaperExecutable
            self.maxRestartAttempts = maxRestartAttempts
            self.backoff = backoff
            self.pingTimeout = pingTimeout
            self.pingPollInterval = pingPollInterval
            self.adoptionProbeTimeout = adoptionProbeTimeout
            self.minimumStableUptime = minimumStableUptime
        }

        /// 1s, 2s, 4s, 8s, 16s - doubling, capped at 16s. The default `maxRestartAttempts` (5)
        /// with this backoff spends about 15s sleeping between attempts (plus whatever each
        /// attempt itself takes to fail) before `state` becomes `.failed` - long enough to ride
        /// out a slow one-off hiccup (e.g. a cold JIT warm-up), short enough that a genuinely
        /// broken core does not leave the menu bar looking alive for minutes.
        public static func defaultBackoff(attempt: Int) -> Duration {
            .seconds(min(16, 1 << max(0, attempt - 1)))
        }
    }

    private let configuration: Configuration
    private let probeSession: URLSession

    public private(set) var state: State = .notStarted
    private var ownership: Ownership?
    private var connection: ControlConnection?
    private var reaperProcess: Process?
    private var isStopping = false

    /// How many spawn attempts have been used in the restart cycle currently in progress.
    /// Persists ACROSS calls to `spawnWithRetries()` - see that method and
    /// `handleCoreProcessExit()`'s own doc comments for why: resetting this to zero on every
    /// death (the Plan 13 Task 8 bug) means `maxRestartAttempts` never actually binds. Reset to
    /// zero only by `start()` (a fresh, user/app-initiated attempt) or by `handleCoreProcessExit()`
    /// noticing the core that just died had proven itself stable first.
    private var attemptsUsedInCurrentCycle = 0

    /// When the most recent spawn was last confirmed `.running(.spawned)` - `nil` whenever there
    /// is no live spawned core to measure. Read by `handleCoreProcessExit()` against
    /// `configuration.minimumStableUptime` to decide whether a death earned a fresh budget.
    private var lastSpawnBecameRunningAt: ContinuousClock.Instant?

    public init(configuration: Configuration) {
        self.configuration = configuration
        let sessionConfiguration = URLSessionConfiguration.ephemeral
        let seconds = configuration.adoptionProbeTimeout.timeInterval
        sessionConfiguration.timeoutIntervalForRequest = seconds
        sessionConfiguration.timeoutIntervalForResource = seconds
        self.probeSession = URLSession(configuration: sessionConfiguration)
    }

    /// Convenience for callers (the menu bar) that only care whether quitting is safe right now,
    /// without switching over the full `state` machine.
    public var currentOwnership: Ownership? { ownership }

    /// Adopt-or-spawn. If a core is already reachable via the discovery file, attaches to it
    /// without spawning anything. Otherwise spawns one (through the reaper - see its own doc
    /// comment) and waits, with bounded retries and backoff, for it to come up. Idempotent:
    /// calling this while already starting, running, or restarting is a no-op.
    public func start() async {
        switch state {
        case .starting, .running, .restarting:
            return
        case .notStarted, .failed:
            break
        }

        state = .starting
        // A fresh, user/app-initiated start always gets the full budget - see
        // `attemptsUsedInCurrentCycle`'s own doc comment. Harmless when this ends up adopting
        // instead of spawning (the adopt branch below never reads it).
        attemptsUsedInCurrentCycle = 0

        if let existing = Discovery.read(home: configuration.home), await canPing(existing) {
            connection = existing
            ownership = .adopted
            state = .running(.adopted)
            return
        }

        ownership = .spawned
        await spawnWithRetries()
    }

    /// Re-validates the current core without spawning or restarting anything. A spawned core's
    /// death is already detected event-drivenly (the reaper `Process`'s termination handler, wired
    /// up in `spawnOnce`), so this exists specifically for the ADOPTED case, which has no such
    /// signal: CoreSupervisor does not own that process, so the only way to know it is gone is to
    /// ask. Deliberately takes no action beyond reflecting reality - it drops `state` back to
    /// `.notStarted` so a later `start()` re-runs the normal adopt-or-spawn decision, but never
    /// spawns anything itself: restarting a core the app never started is exactly what adoption
    /// promises not to do.
    ///
    /// Callers drive the cadence (e.g. the menu bar's own ~1Hz poll while its window is open) -
    /// this type runs no background timer of its own, so supervision never polls when nothing is
    /// watching.
    public func refresh() async {
        guard case .running(.adopted) = state, let connection else { return }
        if await canPing(connection) == false {
            self.connection = nil
            self.ownership = nil
            state = .notStarted
        }
    }

    /// Graceful, app-initiated shutdown. If the current core is `.spawned`, terminates it (via the
    /// reaper) and waits for that to finish. If `.adopted`, does nothing to the core at all: it
    /// outlives this call. This is the entire "quit stops Hades in one case and not the other"
    /// trade the adopt-or-spawn decision makes.
    public func stop() async {
        guard ownership == .spawned, let process = reaperProcess, process.isRunning else { return }
        isStopping = true
        defer { isStopping = false }
        process.terminate() // SIGTERM to the reaper; it kills the core's process group, then exits.
        await waitUntilExit(process, timeout: .seconds(5))
    }

    // MARK: - Spawn / restart

    /// Resumes from `attemptsUsedInCurrentCycle`, NOT zero - the fix for the Plan 13 Task 8 bug.
    /// The original version of this method always started `attempt` at zero, so every call gave
    /// the budget a fresh start; `handleCoreProcessExit()` called this on every death, including
    /// a core that answered one ping and died moments later, so `maxRestartAttempts` never
    /// actually bound (measured live: 49 spawn attempts in 75 seconds, still going). Continuing
    /// from wherever the cycle left off is what makes the budget actually deplete across
    /// repeated fast deaths, while a single call from a fresh `start()` (where
    /// `attemptsUsedInCurrentCycle` is always reset to 0 first) behaves exactly as before.
    private func spawnWithRetries() async {
        var attempt = attemptsUsedInCurrentCycle
        while attempt < configuration.maxRestartAttempts {
            attempt += 1
            if attempt > 1 {
                state = .restarting(attempt: attempt)
                try? await Task.sleep(for: configuration.backoff(attempt - 1))
            }
            if await spawnOnce() {
                state = .running(.spawned)
                attemptsUsedInCurrentCycle = attempt
                lastSpawnBecameRunningAt = .now
                return
            }
        }
        attemptsUsedInCurrentCycle = attempt
        state = .failed(attempts: attempt)
    }

    /// One spawn attempt: launches the reaper (which launches the core underneath it), then polls
    /// the discovery file + `/control/ping` until it answers or `pingTimeout` elapses. Returns
    /// whether THIS attempt produced a live, reachable core.
    private func spawnOnce() async -> Bool {
        let process = Process()
        process.executableURL = configuration.reaperExecutable
        process.arguments = [configuration.coreExecutable.path] + configuration.coreArguments

        var environment = ProcessInfo.processInfo.environment
        for (key, value) in configuration.extraEnvironment { environment[key] = value }
        if let home = configuration.home { environment["HADES_HOME"] = home }
        process.environment = environment

        process.terminationHandler = { [weak self] _ in
            Task { await self?.handleCoreProcessExit() }
        }

        do {
            try process.run()
        } catch {
            return false
        }
        reaperProcess = process

        let deadline = ContinuousClock.now.advanced(by: configuration.pingTimeout)
        while ContinuousClock.now < deadline {
            if !process.isRunning {
                return false // died (or was refused) before ever answering ping
            }
            if let discovered = Discovery.read(home: configuration.home), await canPing(discovered) {
                // Re-check liveness synchronously (no further `await` before committing below):
                // `canPing` suspended on a real network call, and the process could in principle
                // have died in that exact window. This closes the gap cheaply rather than
                // reporting success for a core that is already gone.
                if process.isRunning {
                    connection = discovered
                    return true
                }
                return false
            }
            try? await Task.sleep(for: configuration.pingPollInterval)
        }

        // Timed out waiting for a ping response; this attempt failed. Stop it before trying again
        // so a hung attempt does not linger alongside the next one.
        process.terminate()
        return false
    }

    /// Fires when the reaper process (and thus, by construction, the core underneath it - see
    /// `HadesCoreReaper`) exits, for any reason, at any point after a successful spawn. Ignored
    /// while `stop()` is deliberately causing exactly this exit, and never wired up at all for an
    /// adopted core (which has no `reaperProcess`).
    ///
    /// Whether this death gets a fresh attempt budget depends on how long the core had been
    /// `.running` - see `attemptsUsedInCurrentCycle`'s own doc comment for the bug this closes.
    /// A core that ran for at least `configuration.minimumStableUptime` proved itself healthy:
    /// whatever killed it is a NEW problem, so it earns a full budget, same as `start()` gives a
    /// user-initiated attempt. A core that died sooner than that - including, but not limited to,
    /// the specific false-positive-readiness race Defect A closes on the .NET side; this guard is
    /// what stops any OTHER fast-crash cause from reproducing the same runaway - never proved
    /// itself, so it keeps consuming the SAME budget `spawnWithRetries()` is already working
    /// through, rather than resetting it back to zero.
    private func handleCoreProcessExit() async {
        guard !isStopping, ownership == .spawned else { return }
        guard case .running = state else { return } // an in-progress spawnOnce() handles its own failure path

        let stableEnough = lastSpawnBecameRunningAt.map {
            ContinuousClock.now - $0 >= configuration.minimumStableUptime
        } ?? false
        if stableEnough {
            attemptsUsedInCurrentCycle = 0
        }

        await spawnWithRetries()
    }

    // MARK: - Helpers

    private func canPing(_ connection: ControlConnection) async -> Bool {
        let client = ControlClient(connection: connection, session: probeSession)
        do {
            _ = try await client.ping()
            return true
        } catch {
            return false
        }
    }

    private func waitUntilExit(_ process: Process, timeout: Duration) async {
        let deadline = ContinuousClock.now.advanced(by: timeout)
        while process.isRunning, ContinuousClock.now < deadline {
            try? await Task.sleep(for: .milliseconds(50))
        }
    }
}

extension Duration {
    fileprivate var timeInterval: TimeInterval {
        let components = self.components
        return TimeInterval(components.seconds) + TimeInterval(components.attoseconds) / 1e18
    }
}
