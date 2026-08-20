import Foundation
import Testing

/// Loads a captured control-API response from Tests/HadesControlTests/Fixtures/<name>.json. Every
/// fixture there was captured from a real, running Hades.Server (see the Plan 12 Task 1 report for
/// exactly how each one was produced) - never hand-typed - so a test decoding one is proof the DTO
/// actually matches what the core sends, not just what this package assumes it sends.
enum Fixtures {
    static func url(_ name: String) throws -> URL {
        try #require(
            Bundle.module.url(forResource: name, withExtension: "json", subdirectory: "Fixtures"),
            "Missing fixture: \(name).json"
        )
    }

    static func data(_ name: String) throws -> Data {
        try Data(contentsOf: url(name))
    }

    static func decode<T: Decodable>(_ type: T.Type, _ name: String) throws -> T {
        try JSONDecoder().decode(T.self, from: data(name))
    }
}
