import Darwin
import Foundation
import HadesControl
import Testing

@testable import HadesSupervision

/// Proves the parent-death mechanism (see `HadesCoreReaper`'s own doc comment) under the one
/// condition that matters and that no amount of graceful-quit testing can substitute for: the app
/// process receiving SIGKILL, which gives it zero opportunity to run any cleanup code at all.
///
/// This spawns `FakeAppHarness` - a separate process that embeds `CoreSupervisor` exactly the way
/// the real `HadesApp` target eventually will - specifically so there is something to SIGKILL that
/// is neither this test process itself (killing `swift test` would just abort the run before it
/// could assert anything) nor the reaper directly (which would only prove the reaper reacts to ITS
/// OWN parent dying, not that `CoreSupervisor` actually wires the reaper into its normal spawn path
/// in the first place). Together with `CoreSupervisorTests`' graceful-stop assertions, this closes
/// the loop end to end: spawn happens through the reaper, and the reaper survives and cleans up
/// even when the thing that spawned it gets no chance to say goodbye.
@Suite("Force-kill leaves no orphan", .serialized)
struct ReaperForceKillTests {
    @Test("SIGKILLing the app process leaves no orphan holding the control port")
    func forceKillLeavesNoOrphan() async throws {
        let home = try makeTempHome()

        let harness = Process()
        harness.executableURL = BuildProducts.executable(named: "FakeAppHarness")
        var environment = ProcessInfo.processInfo.environment
        environment["HADES_HOME"] = home.path
        harness.environment = environment
        try harness.run()
        // Guaranteed even if an assertion below fails early: FakeAppHarness (like FakeCore and
        // HadesCoreReaper) loops forever and never exits on its own. `kill`/`isRunning` are
        // synchronous, so - unlike `CoreSupervisor.stop()` elsewhere in this package - a plain
        // `defer` works here without needing the do/catch workaround `withSupervisor` uses.
        defer { if harness.isRunning { kill(harness.processIdentifier, SIGKILL) } }

        // Wait for the harness's own CoreSupervisor.start() to have actually spawned a reachable
        // core - the same readiness check every other test in this package uses, not some stdout
        // convention specific to this one test.
        let cameUp = await waitUntil(timeout: .seconds(15)) {
            guard let connection = Discovery.read(home: home.path) else { return false }
            return (try? await ControlClient(connection: connection).ping()) != nil
        }
        #expect(cameUp)

        let connection = try #require(Discovery.read(home: home.path))
        let port = connection.port
        let corePID = try #require(readFakeCorePID(home: home))
        #expect(!canBindLoopback(port: port)) // sanity: something IS listening before the kill
        #expect(harness.isRunning)

        // The core of the proof: SIGKILL, not terminate(). SIGTERM is catchable (and IS caught,
        // deliberately, for the graceful-quit path exercised elsewhere) - it would only prove the
        // easy case. The plan is explicit that a graceful-quit-only solution does not satisfy this
        // requirement.
        kill(harness.processIdentifier, SIGKILL)

        let cleanedUp = await waitUntil(timeout: .seconds(10)) {
            !processIsAlive(corePID) && canBindLoopback(port: port)
        }
        #expect(cleanedUp)
        #expect(!processIsAlive(corePID), "the core must not survive its app being SIGKILLed")
        #expect(canBindLoopback(port: port), "no orphan may still hold the control port")
    }
}
