import Foundation
import Testing

@testable import HadesControl

/// Serialized: `honoursRealEnvironmentVariable` mutates the process's real `HADES_HOME`
/// environment variable for the duration of one test, which would race a parallel test that reads
/// the same variable via `Discovery.read()`'s default argument.
@Suite("Discovery", .serialized)
struct DiscoveryTests {
    @Test("returns nil, not an error, when the discovery file is absent")
    func missingFileReturnsNil() throws {
        let tempDir = try makeTempDir()
        defer { try? FileManager.default.removeItem(at: tempDir) }

        #expect(Discovery.read(home: tempDir.path) == nil)
    }

    @Test("returns nil, not a thrown error, when the file exists but is not valid connection JSON")
    func corruptFileReturnsNil() throws {
        let tempDir = try makeTempDir()
        defer { try? FileManager.default.removeItem(at: tempDir) }
        try Data("not json".utf8).write(to: tempDir.appendingPathComponent("control.token"))

        #expect(Discovery.read(home: tempDir.path) == nil)
    }

    @Test("decodes port and token from a real discovery file at <home>/control.token")
    func decodesConnectionFromExplicitHome() throws {
        let tempDir = try makeTempDir()
        defer { try? FileManager.default.removeItem(at: tempDir) }
        let body = #"{"port":56925,"token":"22c2d3cf5c76975de1b730c2bd69f7f57706d67557cf004cd5afd8be418a263a"}"#
        try Data(body.utf8).write(to: tempDir.appendingPathComponent("control.token"))

        let connection = Discovery.read(home: tempDir.path)

        #expect(connection == ControlConnection(
            port: 56925,
            token: "22c2d3cf5c76975de1b730c2bd69f7f57706d67557cf004cd5afd8be418a263a"
        ))
    }

    @Test("honours the real HADES_HOME environment variable when no home is passed explicitly")
    func honoursRealEnvironmentVariable() throws {
        let tempDir = try makeTempDir()
        defer { try? FileManager.default.removeItem(at: tempDir) }
        try Data(#"{"port":1,"token":"t"}"#.utf8).write(to: tempDir.appendingPathComponent("control.token"))

        let previous = ProcessInfo.processInfo.environment["HADES_HOME"]
        setenv("HADES_HOME", tempDir.path, 1)
        defer {
            if let previous { setenv("HADES_HOME", previous, 1) } else { unsetenv("HADES_HOME") }
        }

        // No `home:` argument: must fall through to the default, which reads the real env var.
        #expect(Discovery.read()?.port == 1)
    }

    @Test("defaults to ~/Library/Application Support/Hades, matching Hades.Core.Storage.AppPaths")
    func defaultRootMatchesAppPaths() {
        let expected = NSHomeDirectory() + "/Library/Application Support/Hades"
        #expect(Discovery.defaultRoot(fileManager: .default) == expected)
    }
}

private func makeTempDir() throws -> URL {
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    return dir
}
