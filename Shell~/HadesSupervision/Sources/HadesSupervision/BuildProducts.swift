import Foundation

/// Finds a sibling executable built by this same SwiftPM package - the product directory
/// (`.build/<triple>/<config>/`) that `swift build`/`swift test` place every build product into.
/// This is how tests (and `FakeAppHarness`) locate `HadesCoreReaper` and `FakeCore` without
/// hardcoding a path that would differ between a debug and release build, or between one
/// developer's checkout and another's.
///
/// This is deliberately NOT how the eventual HadesApp Xcode target should locate its bundled copy
/// of `HadesCoreReaper` - an app bundle has its own, unrelated layout (e.g. `Contents/MacOS/`).
/// `CoreSupervisor.Configuration.reaperExecutable` is a plain `URL` for exactly this reason: a
/// caller that is not `swift test` points it wherever its own packaging puts the binary, and never
/// needs this type at all.
public enum BuildProducts {
    /// This package's root directory, derived from this source file's own compile-time path
    /// (`Sources/HadesSupervision/BuildProducts.swift`, two directories up) rather than from the
    /// running process's own location. The first version of this type used
    /// `CommandLine.arguments[0]` of the CURRENTLY RUNNING process instead, on the assumption that
    /// `swift test` runs its test binary out of `.build/<triple>/<config>/` alongside every other
    /// product. That assumption was wrong: under Swift 6.3.3 / Xcode 26.6, `swift test` launches
    /// tests through a toolchain-internal host process (observed at a path under
    /// `XcodeDefault.xctoolchain/usr/libexec/swift/pm/...`), so `CommandLine.arguments[0]` pointed
    /// at the toolchain, not this package - caught by actually running the test suite once before
    /// trusting it, not by inspection.
    static var packageRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent() // BuildProducts.swift -> Sources/HadesSupervision/
            .deletingLastPathComponent() // -> Sources/
            .deletingLastPathComponent() // -> package root
    }

    /// Searches `.build/<every triple present>/{debug,release}/<name>`, preferring `debug`, and
    /// returning whichever executable actually exists - `swift build`/`swift test` may have
    /// produced either configuration. Falls back to the (less reliable, but occasionally correct
    /// for other invocation shapes) sibling-of-the-running-process guess only if nothing is found
    /// the reliable way.
    public static func executable(named name: String) -> URL {
        let buildDirectory = packageRoot.appendingPathComponent(".build")
        let triples =
            (try? FileManager.default.contentsOfDirectory(at: buildDirectory, includingPropertiesForKeys: nil))
            ?? []
        for configuration in ["debug", "release"] {
            for triple in triples {
                let candidate = triple.appendingPathComponent(configuration).appendingPathComponent(name)
                if FileManager.default.isExecutableFile(atPath: candidate.path) {
                    return candidate
                }
            }
        }
        return URL(fileURLWithPath: CommandLine.arguments[0])
            .resolvingSymlinksInPath()
            .deletingLastPathComponent()
            .appendingPathComponent(name)
    }
}
