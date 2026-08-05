import Foundation
import Network

// FakeCore - a minimal stand-in for Hades.Server, used only by HadesSupervisionTests. It speaks
// just enough of the control API (token-checked `GET /control/ping`) to exercise
// CoreSupervisor's adopt/spawn/restart logic against a REAL child process and a REAL discovery
// file, without depending on `dotnet`/App~/src being installed or fast to cold-start in whatever
// environment runs `swift test`. It is not part of the package's public product list - internal
// plumbing for tests only, the same role MockURLProtocol.swift plays in HadesControlTests.
//
// Honors HADES_HOME exactly like the real core (Hades.Core.Storage.AppPaths /
// HadesControl.Discovery), so CoreSupervisor's launch configuration never needs to know or care
// which "core" it is actually spawning - a real `Configuration` swaps `dotnet` for this binary's
// path and nothing else changes.

let home = ProcessInfo.processInfo.environment["HADES_HOME"]
    ?? (NSHomeDirectory() + "/Library/Application Support/Hades")
try? FileManager.default.createDirectory(atPath: home, withIntermediateDirectories: true)

// A fresh token every launch, exactly like the real core - this is what makes CoreSupervisor's
// "re-read the discovery file after every spawn, never cache a token" requirement observable in
// tests: a restarted FakeCore answers with a DIFFERENT token than its predecessor.
let token = UUID().uuidString

// Discoverable by tests that need to kill "the core" specifically, as distinct from killing the
// reaper that spawned it - this is what lets a test prove restart-on-death reacts to the CORE
// dying, not merely to the reaper process disappearing for some unrelated reason.
try? String(ProcessInfo.processInfo.processIdentifier).write(
    toFile: home + "/fakecore.pid", atomically: true, encoding: .utf8)

// Lets a test simulate the Plan 13 Task 8 race directly instead of hoping for a real ~100ms
// window to land: when set, this process exits shortly after answering its FIRST successful
// ping - "declared ready, then died moments later" - the one sequence a fake that always answers
// instantly can never express. Deliberately tied to "answered a ping", not a wall-clock timer
// since process launch: that is what makes this deterministic (no race against how fast THIS
// process itself happens to start listening) rather than a second timing gamble layered on top
// of the one already being tested.
let exitAfterPingMs = ProcessInfo.processInfo.environment["FAKECORE_EXIT_AFTER_PING_MS"].flatMap { Int($0) }

let listener: NWListener
do {
    listener = try NWListener(using: .tcp, on: .any)
} catch {
    FileHandle.standardError.write(Data("FakeCore: failed to create listener: \(error)\n".utf8))
    exit(1)
}

func respond(to connection: NWConnection, request: String) {
    let requestLine = request.split(separator: "\r\n", maxSplits: 1, omittingEmptySubsequences: false).first ?? ""
    let isPing = requestLine.contains("/control/ping")
    let authorized = request.contains("Authorization: Bearer \(token)")

    let status: String
    let body: String
    if !authorized {
        status = "401 Unauthorized"
        body = #"{"error":"Missing or invalid token"}"#
    } else if isPing {
        status = "200 OK"
        body = #"{"version":"fakecore-1.0","uptimeSeconds":0}"#
    } else {
        status = "404 Not Found"
        body = #"{"error":"not found"}"#
    }

    let response = "HTTP/1.1 \(status)\r\n"
        + "Content-Type: application/json\r\n"
        + "Content-Length: \(body.utf8.count)\r\n"
        + "Connection: close\r\n\r\n\(body)"

    connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in
        connection.cancel()
        // After the response above has actually been handed to the OS to send - never before -
        // so the caller that just proved this process "ready" genuinely received that answer,
        // exactly like a real core that answers once and then dies moments later.
        if isPing, authorized, let delayMs = exitAfterPingMs {
            DispatchQueue.main.asyncAfter(deadline: .now() + .milliseconds(delayMs)) {
                exit(0)
            }
        }
    })
}

func handle(_ connection: NWConnection) {
    connection.stateUpdateHandler = { state in
        if case .failed = state { connection.cancel() }
    }
    connection.start(queue: .main)
    connection.receive(minimumIncompleteLength: 1, maximumLength: 8192) { data, _, _, _ in
        guard let data, !data.isEmpty else {
            connection.cancel()
            return
        }
        respond(to: connection, request: String(decoding: data, as: UTF8.self))
    }
}

listener.newConnectionHandler = { connection in handle(connection) }

listener.stateUpdateHandler = { state in
    switch state {
    case .ready:
        let port = Int(listener.port?.rawValue ?? 0)
        let connectionFile = home + "/control.token"
        let payload = #"{"port":\#(port),"token":"\#(token)"}"#
        FileManager.default.createFile(atPath: connectionFile, contents: Data(payload.utf8))
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: connectionFile)
        FileHandle.standardError.write(Data("FakeCore: listening on 127.0.0.1:\(port), pid \(getpid())\n".utf8))
    case .failed(let error):
        FileHandle.standardError.write(Data("FakeCore: listener failed: \(error)\n".utf8))
        exit(1)
    default:
        break
    }
}

listener.start(queue: .main)
dispatchMain()
