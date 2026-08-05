import Darwin
import Foundation
import HadesControl
import Testing

@testable import HadesSupervision

/// Every test here spawns real child processes (`FakeCore`, `HadesCoreReaper`) and binds real
/// loopback ports - `.serialized` trades a bit of wall-clock time for not making timing-sensitive
/// process assertions flaky under parallel CPU contention.
@Suite("CoreSupervisor", .serialized)
struct CoreSupervisorTests {

    // MARK: - Adopt

    @Test("adopts an already-running core instead of spawning one")
    func adoptsExistingCore() async throws {
        let home = try makeTempHome()
        let existing = try startFakeCoreDirectly(home: home)
        defer { existing.terminate() }

        try await #require(waitUntil { readFakeCorePID(home: home) != nil })
        let originalPID = try #require(readFakeCorePID(home: home))
        let originalFileContents = try Data(contentsOf: home.appendingPathComponent("control.token"))

        let supervisor = CoreSupervisor(configuration: testConfiguration(home: home))
        try await withSupervisor(supervisor) {
            await supervisor.start()

            #expect(await supervisor.state == .running(.adopted))
            #expect(await supervisor.currentOwnership == .adopted)

            // Nothing new was spawned: the discovery file and the core's own pid are
            // byte-identical to what was there before `start()` was called.
            let fileContentsAfter = try Data(contentsOf: home.appendingPathComponent("control.token"))
            #expect(fileContentsAfter == originalFileContents)
            #expect(readFakeCorePID(home: home) == originalPID)
        }
    }

    // MARK: - Spawn

    @Test("spawns a core when none is running, and waits for it to answer ping")
    func spawnsWhenNoCoreRunning() async throws {
        let home = try makeTempHome()
        let supervisor = CoreSupervisor(configuration: testConfiguration(home: home))

        try await withSupervisor(supervisor) {
            await supervisor.start()

            #expect(await supervisor.state == .running(.spawned))
            #expect(await supervisor.currentOwnership == .spawned)

            // A core genuinely came up: the discovery file exists and its own ping answers for
            // real, through HadesControl's own client - not just CoreSupervisor's internal
            // bookkeeping.
            let connection = try #require(Discovery.read(home: home.path))
            let ping = try await ControlClient(connection: connection).ping()
            #expect(ping.version == "fakecore-1.0")
        }
    }

    @Test("a stale discovery file whose port no longer answers means 'not running' - spawns, does not fail")
    func staleDiscoveryFileSpawnsInstead() async throws {
        let home = try makeTempHome()

        // A discovery file pointing at a port nothing is listening on, exactly what is left behind
        // when a core exits without cleaning up after itself (ControlListener.Dispose never
        // deletes control.token - see Plan 12 Task 2's report).
        let stale = #"{"port":1,"token":"stale-token-nobody-will-ever-answer-to"}"#
        try Data(stale.utf8).write(to: home.appendingPathComponent("control.token"))

        let supervisor = CoreSupervisor(configuration: testConfiguration(home: home))
        try await withSupervisor(supervisor) {
            await supervisor.start()

            #expect(await supervisor.state == .running(.spawned))

            // The discovery file was overwritten with a real, reachable connection - not left
            // pointing at the stale port.
            let connection = try #require(Discovery.read(home: home.path))
            #expect(connection.port != 1)
            _ = try await ControlClient(connection: connection).ping() // throws if unreachable
        }
    }

    // MARK: - Restart on death, bounded

    @Test("restarts a spawned core that dies, with a fresh token, and returns to running")
    func restartsSpawnedCoreOnDeath() async throws {
        let home = try makeTempHome()
        let supervisor = CoreSupervisor(configuration: testConfiguration(home: home))

        try await withSupervisor(supervisor) {
            await supervisor.start()
            #expect(await supervisor.state == .running(.spawned))

            let firstToken = try #require(Discovery.read(home: home.path)).token
            let firstPID = try #require(readFakeCorePID(home: home))

            kill(firstPID, SIGKILL)

            // Restart passes through `.restarting` on its way back to `.running` - this
            // observation is best-effort (it is a narrow window) but worth a soft check; the hard
            // assertions are the ones after the second `waitUntil` below.
            _ = await waitUntil(timeout: .seconds(2)) {
                if case .restarting = await supervisor.state { return true }
                return false
            }

            let recovered = await waitUntil(timeout: .seconds(10)) {
                if case .running(.spawned) = await supervisor.state { return true }
                return false
            }
            #expect(recovered)

            // A genuinely NEW core: different pid, different token (never a cached, now-stale
            // one).
            let secondPID = try #require(readFakeCorePID(home: home))
            let secondToken = try #require(Discovery.read(home: home.path)).token
            #expect(secondPID != firstPID)
            #expect(secondToken != firstToken)
        }
    }

    @Test("exhausting restart attempts surfaces a visible, terminal error state")
    func exhaustedRestartsSurfaceError() async throws {
        let home = try makeTempHome()
        // /usr/bin/true exits (code 0) essentially instantly and never listens on anything or
        // writes a discovery file - every attempt is guaranteed to fail, quickly.
        let configuration = testConfiguration(
            home: home,
            coreExecutable: URL(fileURLWithPath: "/usr/bin/true"),
            maxRestartAttempts: 3,
            backoff: { _ in .milliseconds(50) },
            pingTimeout: .seconds(2)
        )
        let supervisor = CoreSupervisor(configuration: configuration)

        try await withSupervisor(supervisor) {
            await supervisor.start()

            let failed = await waitUntil(timeout: .seconds(10)) {
                if case .failed = await supervisor.state { return true }
                return false
            }
            #expect(failed)
            #expect(await supervisor.state == .failed(attempts: 3))
        }
    }

    // MARK: - Fast death after ready must consume budget, not reset it (Defect B, Plan 13 Task 8)

    @Test("a core that dies moments after every single spawn still exhausts the attempt budget and reaches .failed - it must not get a fresh budget on every death")
    func fastDeathAfterReadyExhaustsBudgetInsteadOfResetting() async throws {
        let home = try makeTempHome()
        // FakeCore exits shortly after answering its FIRST ping on EVERY attempt - "declared
        // ready, then died moments later", deterministically (tied to a real successful ping,
        // not a wall-clock race against how fast this process happens to start listening - see
        // FakeCore's own doc comment on FAKECORE_EXIT_AFTER_PING_MS). This is exactly the
        // sequence the live Task 8 measurement found (a ping answers, the process is declared
        // running, then it dies moments later) - the one no existing test could express because
        // the old FakeCore always answers and never dies on its own.
        //
        // minimumStableUptime is set far larger than the 50ms exit delay, so this test can never
        // accidentally pass because some death happened to look "stable enough" to reset the
        // budget - every single one of the 4 deaths below must be attributed to the SAME
        // depleting budget for the assertion to hold.
        let configuration = testConfiguration(
            home: home,
            extraEnvironment: ["FAKECORE_EXIT_AFTER_PING_MS": "50"],
            maxRestartAttempts: 4,
            backoff: { _ in .milliseconds(30) },
            pingTimeout: .seconds(2),
            pingPollInterval: .milliseconds(20),
            minimumStableUptime: .seconds(5)
        )
        let supervisor = CoreSupervisor(configuration: configuration)

        try await withSupervisor(supervisor) {
            await supervisor.start()

            // Bounded, per this project's own "no unbounded Process.waitUntilExit()" lesson (a
            // prior `swift test` hang, fixed with bounded polling - see ReaperForceKillTests'
            // own comment on why). If the attempt budget resets on every death instead of
            // depleting (the bug), `.failed` is simply never reached and this returns false
            // after 5 real seconds, rather than hanging - the same shape as the live
            // 49-attempts-in-75-seconds measurement, just on a test-scaled clock.
            let failed = await waitUntil(timeout: .seconds(5)) {
                if case .failed = await supervisor.state { return true }
                return false
            }
            #expect(failed, "the supervisor must stop at maxRestartAttempts even when every death follows a successful ping - a reset-every-time bug loops past this instead")
            #expect(await supervisor.state == .failed(attempts: 4))
        }
    }

    // MARK: - Adoption never restarts

    @Test("never restarts an adopted core, and refresh() reflects it being gone without acting on it")
    func neverRestartsAdoptedCore() async throws {
        let home = try makeTempHome()
        let existing = try startFakeCoreDirectly(home: home)
        try await #require(waitUntil { readFakeCorePID(home: home) != nil })

        let supervisor = CoreSupervisor(configuration: testConfiguration(home: home))
        try await withSupervisor(supervisor) {
            await supervisor.start()
            #expect(await supervisor.state == .running(.adopted))

            existing.terminate() // simulate whatever was running it stopping, outside the app
            // NOT existing.waitUntilExit(): confirmed by repeated `sample` captures of a hung
            // `swift test` run to be capable of blocking forever even after the child has fully
            // exited and been reaped (zero matching process in the system process table, not
            // even a zombie) - a lost-wakeup somewhere inside Foundation's own NSTask exit
            // notification, not anything under this package's control, and not dependent on any
            // other suite running concurrently (reproduced with this suite filtered in
            // isolation). Every other liveness check in this file polls a real OS-level
            // condition with a timeout instead of trusting that notification; do the same here.
            let existingExited = await waitUntil(timeout: .seconds(5)) {
                !processIsAlive(existing.processIdentifier)
            }
            #expect(existingExited, "existing FakeCore should exit after terminate()")

            // Give a would-be (incorrect) auto-restart every opportunity to happen.
            try await Task.sleep(for: .seconds(1))
            #expect(readFakeCorePID(home: home) == existing.processIdentifier) // stale file, unwritten

            // refresh() is the ONLY thing that re-checks an adopted core, and it must not spawn
            // anything even once it notices the core is gone.
            await supervisor.refresh()
            #expect(await supervisor.state == .notStarted)
        }
    }

    // MARK: - Quit semantics: the entire point of adopt-or-spawn

    @Test("quitting with an adopted core leaves it running")
    func stopLeavesAdoptedCoreRunning() async throws {
        let home = try makeTempHome()
        let existing = try startFakeCoreDirectly(home: home)
        defer { existing.terminate() }
        try await #require(waitUntil { readFakeCorePID(home: home) != nil })
        let originalPID = try #require(readFakeCorePID(home: home))

        let supervisor = CoreSupervisor(configuration: testConfiguration(home: home))
        await supervisor.start()
        #expect(await supervisor.state == .running(.adopted))

        await supervisor.stop()

        // Still alive, still the SAME process, still answering - stop() touched nothing.
        #expect(processIsAlive(originalPID))
        let connection = try #require(Discovery.read(home: home.path))
        let ping = try await ControlClient(connection: connection).ping()
        #expect(ping.version == "fakecore-1.0")
    }

    @Test("quitting with a spawned core stops it")
    func stopKillsSpawnedCore() async throws {
        let home = try makeTempHome()
        let supervisor = CoreSupervisor(configuration: testConfiguration(home: home))
        try await withSupervisor(supervisor) {
            await supervisor.start()
            #expect(await supervisor.state == .running(.spawned))
            let port = try #require(readPortFromDiscoveryFile(home: home))
            let pid = try #require(readFakeCorePID(home: home))

            await supervisor.stop()

            let gone = await waitUntil(timeout: .seconds(5)) {
                !processIsAlive(pid) && canBindLoopback(port: port)
            }
            #expect(gone)
        }
    }
}
