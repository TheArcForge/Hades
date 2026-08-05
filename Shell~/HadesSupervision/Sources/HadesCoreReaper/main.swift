import Darwin
import Foundation

// HadesCoreReaper - the parent-death watchdog for a spawned Hades core.
//
// Why this process exists at all: when the app that owns a spawned core is force-quit (SIGKILL),
// no code inside the app gets to run - the OS gives a SIGKILLed process zero opportunity to clean
// up after itself. So a spawned core cannot be cleaned up BY the app; it has to be cleaned up by
// something else that is still alive after the app is gone. That "something else" is this process:
// CoreSupervisor spawns it directly (so its ppid at birth is the app's pid), it spawns the actual
// core as ITS OWN child, and it watches for the app's disappearance to know when to kill the core.
//
// Mechanism chosen: getppid()-polling. This process's ppid is fixed at the app's pid when it
// starts. When the app dies - by any means, gracefully, by crashing, or by SIGKILL - the kernel
// reparents this now-orphaned process to launchd, which changes what getppid() returns (verified
// empirically during development: macOS reparents to launchd, not pid 1 in the traditional Linux
// sense - this code compares against the ORIGINAL ppid rather than assuming any specific new
// value, which is correct either way). Polling for that change is how this process notices the
// app is gone. This works identically regardless of *how* the app died, because it does not
// depend on the app doing anything on its way out.
//
// Two other options were on the table and were set aside for this phase, not because they are
// wrong:
//   - A pipe the child reads, closed on parent death: near-instant (blocking read() unblocks on
//     EOF), but only if the write end never leaks into another process's fd table. `dotnet run`
//     forks at least one further child before the real listener is up (see
//     App~/scripts/e2e-editor-attach.sh's own documented lesson about this), which is exactly the
//     kind of process tree where an fd can leak somewhere unexpected and silently defeat EOF
//     detection.
//   - kqueue with NOTE_EXIT on the parent pid: event-driven, no polling latency at all, and this
//     project's spec (the reload-lease "no-hanging-state" standard) does not ask for
//     faster-than-instant cleanup - only that it happens at all, even under SIGKILL. Sub-second
//     latency from polling costs nothing this project's requirements care about, and getppid() is
//     portable POSIX with no Darwin-specific kevent struct layout to get subtly wrong. Given the
//     choice, the simpler mechanism was preferred - see CLAUDE.md's own "Simplicity First".
//
// A getppid()-polling reaper has one inherent race: if the app dies in the instant between this
// process reading its own ppid and finishing startup, there is no window to miss the *change*
// (the poll loop below re-reads getppid() on every iteration, starting immediately), so the race
// is fully closed by construction.
//
// This process ALSO exits (propagating the exit to CoreSupervisor, which is watching this
// process's own termination) when the core it spawned exits on its own - a crash, or anything
// else that kills only the core and not the reaper - so CoreSupervisor's restart-with-backoff has
// a single, reliable trigger: "the process I spawned is gone" covers both directions.
//
// Usage: HadesCoreReaper <core-executable-path> <core-argument>...
// The environment is inherited as-is from whatever spawned this process (CoreSupervisor sets
// HADES_HOME there) and forwarded verbatim to the core.
//
// Implementation note: the core is launched with raw `posix_spawn`, not Foundation's `Process`.
// This was tried first and rejected after an empirical smoke test (not just reasoning about it)
// showed `Process`-spawned children get their OWN new process group rather than inheriting the
// spawning process's group - which silently defeated the "kill everything under the core in one
// call" design below (`kill(-pgid, ...)` never reached a child in a different group). `posix_spawn`
// with `POSIX_SPAWN_SETPGROUP` + target group `0` gives explicit, verified control over this.

enum Reaper {
    /// Set only by the SIGTERM handler below; read only by the poll loop. A single aligned
    /// word-sized write/read pair like this is safe without additional synchronization even from
    /// a raw signal handler - `sig_atomic_t` is the C standard's own name for exactly this
    /// contract. `nonisolated(unsafe)` is Swift 6's spelling for "this global is manually proven
    /// safe", which this doc comment is the proof of.
    nonisolated(unsafe) static var receivedTerminate: sig_atomic_t = 0

    static let pollIntervalMicroseconds: useconds_t = 250_000 // 250ms - see rationale above.
    static let killGraceMicroseconds: useconds_t = 1_000_000 // 1s between SIGTERM and SIGKILL.

    static func fail(_ message: String, exitCode: Int32 = 1) -> Never {
        FileHandle.standardError.write(Data("HadesCoreReaper: \(message)\n".utf8))
        exit(exitCode)
    }

    /// Builds a NULL-terminated `char**` from Swift strings for `posix_spawn`'s argv/envp. Caller
    /// owns the returned pointers and must free each non-nil entry.
    static func cStringArray(_ strings: [String]) -> [UnsafeMutablePointer<CChar>?] {
        var result = strings.map { strdup($0) }
        result.append(nil)
        return result
    }

    static func run() -> Never {
        let arguments = CommandLine.arguments
        guard arguments.count >= 2 else {
            fail("usage: HadesCoreReaper <core-executable> <args...>", exitCode: 64)
        }
        let coreExecutable = arguments[1]
        let coreArguments = Array(arguments.dropFirst(2))

        // Captured before anything else can happen: this IS "the app's pid", by construction,
        // since CoreSupervisor spawns this process directly.
        let originalParentPID = getppid()

        // New process group, before the core is spawned, so the core (and anything IT forks, no
        // matter how deep - see the dotnet-run-forks-a-grandchild note above) inherits this group
        // rather than the app's. That turns "kill everything under the core" into one
        // kill(-pgid, signal) call instead of needing to know the core's listening port or walk
        // the process tree by hand.
        setpgid(0, 0)
        let group = getpid() // == this process's own pgid, immediately after setpgid(0, 0) above.

        signal(SIGTERM) { _ in Reaper.receivedTerminate = 1 }

        var spawnAttr: posix_spawnattr_t? = nil
        posix_spawnattr_init(&spawnAttr)
        defer { posix_spawnattr_destroy(&spawnAttr) }
        posix_spawnattr_setflags(&spawnAttr, Int16(POSIX_SPAWN_SETPGROUP))
        // NOT 0: per posix_spawnattr_setpgroup(3), a target pgroup of 0 means "make the child a
        // NEW group leader (child's pgid = child's own pid)" - the opposite of what is wanted
        // here. This was caught empirically (a smoke test showed the spawned child's pgid was
        // still its own pid, not the reaper's) before it became load-bearing behind a passing
        // test. Passing the reaper's own pgid explicitly makes the core JOIN that existing group,
        // which is what lets `kill(-group, signal)` below reach it.
        posix_spawnattr_setpgroup(&spawnAttr, group)

        let argv = cStringArray([coreExecutable] + coreArguments)
        defer { argv.forEach { if let p = $0 { free(p) } } }
        let envp = cStringArray(
            ProcessInfo.processInfo.environment.map { key, value in "\(key)=\(value)" })
        defer { envp.forEach { if let p = $0 { free(p) } } }

        var childPID: pid_t = 0
        let spawnResult = coreExecutable.withCString { execPath in
            posix_spawn(&childPID, execPath, nil, &spawnAttr, argv, envp)
        }
        guard spawnResult == 0 else {
            fail("posix_spawn failed for core '\(coreExecutable)': errno \(spawnResult)")
        }

        func killCoreProcessGroup() {
            kill(-group, SIGTERM)
            usleep(killGraceMicroseconds)
            kill(-group, SIGKILL)
        }

        while true {
            if receivedTerminate == 1 {
                // Explicit graceful-stop request from CoreSupervisor (the app quitting normally
                // while it owns this core). Intentional shutdown - clean up and exit quietly.
                killCoreProcessGroup()
                exit(0)
            }
            if getppid() != originalParentPID {
                // The app is gone. Whether that was a clean quit or a SIGKILL is indistinguishable
                // from here, and does not need to be distinguished: both converge on the same
                // cleanup.
                killCoreProcessGroup()
                exit(0)
            }
            var status: Int32 = 0
            let waited = waitpid(childPID, &status, WNOHANG)
            if waited == childPID {
                // The core exited on its own; nothing left to supervise. Exit so CoreSupervisor's
                // termination handler on THIS process fires and can decide whether to restart.
                exit(0)
            }
            usleep(pollIntervalMicroseconds)
        }
    }
}

Reaper.run()
