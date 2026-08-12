import Darwin
import Foundation
import HadesControl
import HadesSupervision

/// A fresh, isolated `HADES_HOME`-equivalent directory per test - never the real
/// `~/Library/Application Support/Hades`, and never shared between tests, so parallel execution
/// (Swift Testing's default) cannot let one test's core answer another test's ping.
func makeTempHome() throws -> URL {
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    return dir
}

/// Polls `condition` until it is true or `timeout` elapses. Every CoreSupervisor test that waits
/// for a real child process to spawn, answer a ping, or die uses this instead of a fixed `sleep`,
/// so tests run as fast as the real work allows and still tolerate a slow CI machine.
@discardableResult
func waitUntil(
    timeout: Duration = .seconds(10),
    interval: Duration = .milliseconds(50),
    _ condition: () async -> Bool
) async -> Bool {
    let deadline = ContinuousClock.now.advanced(by: timeout)
    while ContinuousClock.now < deadline {
        if await condition() { return true }
        try? await Task.sleep(for: interval)
    }
    return await condition()
}

/// Reads the pid `FakeCore` writes next to its discovery file - lets a test target the CORE
/// specifically (as opposed to the reaper spawned above it), e.g. to simulate a crash.
func readFakeCorePID(home: URL) -> pid_t? {
    guard let content = try? String(contentsOf: home.appendingPathComponent("fakecore.pid"), encoding: .utf8),
        let value = Int32(content.trimmingCharacters(in: .whitespacesAndNewlines))
    else { return nil }
    return value
}

func processIsAlive(_ pid: pid_t) -> Bool {
    kill(pid, 0) == 0
}

/// Reads the append-only launch history `FakeCore` writes to `fakecore_launches.log` - one pid
/// per line, covering every process ever spawned against this home, including one deliberately
/// held back by `.fakecore_hang_once` that never gets as far as writing `fakecore.pid`. Lets a
/// test prove exactly how many FakeCore processes were spawned, not just which one is current.
func readLaunchLog(home: URL) -> [pid_t] {
    guard let content = try? String(contentsOf: home.appendingPathComponent("fakecore_launches.log"), encoding: .utf8)
    else { return [] }
    return content.split(separator: "\n").compactMap { pid_t($0) }
}

/// Whether a NEW listener could bind `127.0.0.1:port` right now. This is the most direct possible
/// check for "does an orphan still hold this port": it does not depend on the control API's HTTP
/// framing or auth being correct, only on whether the OS still considers the port occupied by a
/// live listening socket.
///
/// Deliberately does NOT set `SO_REUSEADDR`. It was here originally (to avoid a false "occupied"
/// reading from a PRIOR test's own bind lingering in TIME_WAIT) and turned out to be actively
/// wrong: caught empirically when this check reported a port free while `FakeCore` was
/// demonstrably still listening on it. `FakeCore` binds via `NWListener`, which binds the WILDCARD
/// address (confirmed with `lsof`: `TCP *:<port>` on IPv6, not `127.0.0.1:<port>`) - and on
/// Darwin, `SO_REUSEADDR` specifically permits a new bind to a SPECIFIC address to succeed
/// alongside an EXISTING listener on the WILDCARD address for the same port. That is standard,
/// intentional BSD socket behaviour (letting a specific bind coexist with a wildcard one) - it is
/// just the wrong tool for "is this port truly free", which is what every caller of this function
/// actually means. A single-shot listen-then-immediately-close probe like this never lingers in
/// TIME_WAIT in the first place (that state applies to closing an ESTABLISHED connection, not a
/// bind/listen/close cycle with no accepted connections), so the original justification for
/// `SO_REUSEADDR` did not even apply here.
func canBindLoopback(port: Int) -> Bool {
    let sock = socket(AF_INET, SOCK_STREAM, 0)
    guard sock >= 0 else { return false }
    defer { close(sock) }
    var address = sockaddr_in()
    address.sin_family = sa_family_t(AF_INET)
    address.sin_port = UInt16(port).bigEndian
    address.sin_addr.s_addr = inet_addr("127.0.0.1")
    let result = withUnsafePointer(to: &address) { pointer -> Int32 in
        pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockaddrPointer in
            bind(sock, sockaddrPointer, socklen_t(MemoryLayout<sockaddr_in>.size))
        }
    }
    return result == 0
}

/// Reads `port` back out of a `control.token`-shaped discovery file without going through
/// `Discovery`/`ControlConnection` decoding, so a test can inspect it even when the file is
/// deliberately stale or malformed.
func readPortFromDiscoveryFile(home: URL) -> Int? {
    guard let connection = Discovery.read(home: home.path) else { return nil }
    return connection.port
}

/// A `Configuration` pointed at the real `FakeCore`/`HadesCoreReaper` binaries this same package
/// build produces (see `BuildProducts`), with short timings so tests fail fast instead of hanging
/// for production-scale durations. Individual tests override whichever fields they are exercising.
func testConfiguration(
    home: URL,
    coreExecutable: URL = BuildProducts.executable(named: "FakeCore"),
    coreArguments: [String] = [],
    extraEnvironment: [String: String] = [:],
    maxRestartAttempts: Int = 5,
    backoff: @escaping @Sendable (Int) -> Duration = { _ in .milliseconds(100) },
    pingTimeout: Duration = .seconds(10),
    pingPollInterval: Duration = .milliseconds(50),
    minimumStableUptime: Duration = .seconds(3)
) -> CoreSupervisor.Configuration {
    CoreSupervisor.Configuration(
        home: home.path,
        coreExecutable: coreExecutable,
        coreArguments: coreArguments,
        extraEnvironment: extraEnvironment,
        reaperExecutable: BuildProducts.executable(named: "HadesCoreReaper"),
        maxRestartAttempts: maxRestartAttempts,
        backoff: backoff,
        pingTimeout: pingTimeout,
        pingPollInterval: pingPollInterval,
        adoptionProbeTimeout: .milliseconds(500),
        minimumStableUptime: minimumStableUptime
    )
}

/// Launches `FakeCore` directly as the CALLING test's own child (bypassing CoreSupervisor
/// entirely) - this is how tests simulate "a core the app did not start", i.e. exactly what
/// adoption is for.
@discardableResult
func startFakeCoreDirectly(home: URL) throws -> Process {
    let process = Process()
    process.executableURL = BuildProducts.executable(named: "FakeCore")
    var environment = ProcessInfo.processInfo.environment
    environment["HADES_HOME"] = home.path
    process.environment = environment
    try process.run()
    return process
}

/// Runs `body`, guaranteeing `supervisor.stop()` afterwards whether `body` throws or returns
/// normally. `defer` cannot call `async` functions in this Swift version ("'async' call cannot
/// occur in a defer body" - confirmed by actually trying it, not assumed), so this is the
/// do/catch equivalent. Every test that spawns a core THROUGH CoreSupervisor uses this: both
/// `FakeCore` and `HadesCoreReaper` loop forever and never exit on their own, so an early
/// `#require` failure without guaranteed cleanup would leak a real process for the rest of the
/// test run (this happened once during development - see Plan 12 Task 2's report).
func withSupervisor<T>(_ supervisor: CoreSupervisor, _ body: () async throws -> T) async throws -> T {
    do {
        let result = try await body()
        await supervisor.stop()
        return result
    } catch {
        await supervisor.stop()
        throw error
    }
}
