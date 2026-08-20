import Foundation
import HadesSupervision

// FakeAppHarness - a tiny process that embeds CoreSupervisor exactly the way the real HadesApp
// target eventually will, so a test can SIGKILL something that is genuinely "an app with a
// supervised core" - not just the reaper in isolation. See ReaperForceKillTests.swift for why
// this level of proof exists alongside the more direct reaper-only test.
//
// Reads HADES_HOME the ordinary way (inherited environment variable - CoreSupervisor.Configuration
// picks it up on its own). Prints "READY\n" to stdout, unbuffered, once CoreSupervisor.start()
// has returned, then blocks forever. The test holds this process's pid directly and sends it
// SIGKILL; this process does nothing special to handle that (no signal handler at all) because
// that is exactly the scenario being proven: the app gets zero chance to run cleanup code, so
// cleanup has to come from somewhere else (the reaper this harness spawned via CoreSupervisor).

let configuration = CoreSupervisor.Configuration(
    coreExecutable: BuildProducts.executable(named: "FakeCore"),
    coreArguments: [],
    reaperExecutable: BuildProducts.executable(named: "HadesCoreReaper")
)

let supervisor = CoreSupervisor(configuration: configuration)
await supervisor.start()

FileHandle.standardOutput.write(Data("READY\n".utf8))

while true {
    try? await Task.sleep(for: .seconds(1))
}
