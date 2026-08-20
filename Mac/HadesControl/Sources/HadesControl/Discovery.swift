import Foundation

/// Where the running core's control API can be reached, and the bearer token every request must
/// carry. Mirrors `Hades.Server.Control.ControlConnectionInfo` (and `Hades.Cli.ConnectionInfo`)
/// exactly - same JSON shape, same discovery file, written at file mode `0600`.
public struct ControlConnection: Decodable, Equatable, Sendable {
    public let port: Int
    public let token: String

    public init(port: Int, token: String) {
        self.port = port
        self.token = token
    }
}

/// Reads the control API's discovery file exactly as the `hades` CLI and the Unity plugin already
/// do - never a hardcoded port. A missing file means Hades is not running, which is an ordinary
/// state, not an error: `read` returns `nil`, never throws, for that case.
public enum Discovery {
    /// Reads `<home>/control.token`, honouring `HADES_HOME` exactly as
    /// `Hades.Core.Storage.AppPaths` does: `home` overrides the root when non-nil (even when set
    /// to an empty string, matching the .NET side's `root ?? DefaultRoot()`); otherwise the root
    /// defaults to `~/Library/Application Support/Hades`, the same path
    /// `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)` resolves to on
    /// macOS (verified against the .NET runtime this project targets).
    ///
    /// Returns `nil` - not a thrown error - whenever the file does not exist, is unreadable, or
    /// does not parse as a `ControlConnection`: every one of those means "Hades is not running (or
    /// not running with a usable discovery file right now)", never a condition worth surfacing as
    /// a client error.
    public static func read(
        home: String? = ProcessInfo.processInfo.environment["HADES_HOME"],
        fileManager: FileManager = .default
    ) -> ControlConnection? {
        let root = home ?? defaultRoot(fileManager: fileManager)
        let tokenFilePath = root + "/control.token"

        guard let data = fileManager.contents(atPath: tokenFilePath) else { return nil }
        return try? JSONDecoder().decode(ControlConnection.self, from: data)
    }

    /// `~/Library/Application Support/Hades` - matches `Hades.Core.Storage.AppPaths.DefaultRoot()`
    /// (`Path.Combine(Environment.SpecialFolder.ApplicationData, "Hades")`) on macOS.
    static func defaultRoot(fileManager: FileManager) -> String {
        let applicationSupport = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
        let base = applicationSupport?.path ?? (NSHomeDirectory() + "/Library/Application Support")
        return base + "/Hades"
    }
}
