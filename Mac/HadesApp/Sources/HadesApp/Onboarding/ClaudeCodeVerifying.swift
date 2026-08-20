import Foundation
import HadesControl

/// What Onboarding's Claude Code step found when it last checked. Deliberately carries no failure
/// text: `.unreachable` covers every way the check can fail (no discovery file, `/control/settings`
/// unreachable, the raw MCP request timing out, a non-2xx response, an unparsable body) with a
/// single fixed, Swift-authored explanation in the view - the same "static copy for a connectivity
/// failure state" precedent `SettingsView`'s own `ContentUnavailableView("Settings Unavailable", ...,
/// description: Text("Hades is not reachable right now."))` already sets, not a re-derivation of
/// server-authored text (there is none to re-derive: a transport failure means the core produced no
/// response at all).
///
/// `.reachable(toolCount:)` carries the one live fact this check produces: a plain `Int`, exactly
/// the same "raw count, Swift-authored label" shape `ProjectDetailView`'s `LabeledContent("Nodes",
/// value: "\(project.nodeCount)")` already uses - not a combined, formatted, or invented string.
public enum ClaudeCodeVerification: Equatable, Sendable {
    case notVerified
    case verifying
    case reachable(toolCount: Int)
    case unreachable
}

/// The seam behind Onboarding's Claude Code step - see `OnboardingViewModel.verifyClaudeCode()`'s
/// own doc comment for what a `.reachable` result proves and what it only assumes. Not
/// `@MainActor`: the real conformance performs network I/O, same shape as `ControlProjectsFetching`
/// et al.
public protocol ClaudeCodeVerifying: Sendable {
    func verify() async -> ClaudeCodeVerification
}

/// The real `ClaudeCodeVerifying` - the one thing onboarding can honestly check from inside itself.
///
/// **What this proves.** Two live facts, in sequence: (1) the control API is reachable and reports
/// an `mcpPort` (via the SAME `GET /control/settings` `SettingsView` already trusts for this,
/// so a port override from a conflict-remedy is honoured, not just the documented 7823 default);
/// (2) a raw MCP `tools/list` JSON-RPC call to `http://127.0.0.1:{that port}/mcp` - the EXACT
/// address `ClaudeCodePlugin/.claude-plugin/plugin.json` declares - gets back a well-formed
/// result with one or more tools. That is "the core is up and serving N tools", proven the same way
/// Task 1 proved it by hand (`claude mcp list` + a direct `tools/list`; see
/// `Hades.Server.Tests.McpTestClient`/`TransportConformanceTests`, which this call mirrors exactly:
/// same `MCP-Protocol-Version`/`Mcp-Method` headers, same JSON-RPC envelope, same SSE-or-plain-JSON
/// unwrapping - `WithHttpTransport()`'s revision 2026-07-28 needs no prior `initialize` call, so a
/// single POST is the whole check).
///
/// **What this does NOT prove.** That Claude Code itself has connected. This type never inspects
/// Claude Code's own state - no shelling out to `claude mcp list`, no reading Claude Code's config -
/// both would mean either touching another program's files (forbidden outright) or depending on a
/// CLI that may not be on `PATH` inside the very app this check must work from. "The core is up and
/// serving N tools" and "Claude Code can see it" are different claims; the Claude Code step's own
/// copy in `OnboardingClaudeCodeStepView` says so in exactly those words.
///
/// Not unit tested itself - it genuinely dials a loopback socket, the same "nothing to unit test,
/// isolated and auditable in one small file" allowance `NSOpenPanelDirectoryPicker`/
/// `LaunchAtLoginService` already have. `OnboardingViewModelTests` fakes `ClaudeCodeVerifying`
/// instead; this type is proven live, by hand, against a real running core (see the Task 6 report).
public struct LiveClaudeCodeVerifier: ClaudeCodeVerifying {
    /// Mirrors `Hades.Server.Tests.McpTestClient.Version` / the MCP SDK's negotiated revision
    /// (`Core/src/Hades.Server/Program.cs`'s own comment: "the SDK owns... protocol-version
    /// negotiation"). Duplicated deliberately, never referenced: `Hades.Core`/`Hades.Server` are
    /// `.NET` and headless, unreachable from Swift - the same "keep this in sync if that constant
    /// ever changes" rule `V12Cleanup.McpPort`'s own doc comment already accepts for exactly this
    /// reason.
    static let mcpProtocolVersion = "2026-07-28"

    public init() {}

    public func verify() async -> ClaudeCodeVerification {
        guard let connection = Discovery.read() else { return .unreachable }

        let settings: SettingsResult
        do {
            settings = try await ControlClient(connection: connection).settings()
        } catch {
            return .unreachable
        }

        return await Self.checkToolsList(port: settings.mcpPort.port)
    }

    /// The raw MCP call. `internal` (not `private`) only so a live/manual verification pass can
    /// call it directly against a real port without going through `Discovery`/`/control/settings` -
    /// never called by an automated test (see this type's own class doc comment).
    static func checkToolsList(port: Int, session: URLSession = .shared) async -> ClaudeCodeVerification {
        guard let url = URL(string: "http://127.0.0.1:\(port)/mcp") else { return .unreachable }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json, text/event-stream", forHTTPHeaderField: "Accept")
        // Both headers are required by the MCP SDK's own transport - see
        // `Hades.Server.Tests.TransportConformanceTests.MissingProtocolVersionHeaderIsRejected`/
        // `McpMethodHeaderMismatchIsRejected` for the live proof neither is optional.
        request.setValue(mcpProtocolVersion, forHTTPHeaderField: "MCP-Protocol-Version")
        request.setValue("tools/list", forHTTPHeaderField: "Mcp-Method")
        request.httpBody = try? JSONSerialization.data(withJSONObject: [
            "jsonrpc": "2.0",
            "id": 1,
            "method": "tools/list",
            "params": [
                "_meta": [
                    "io.modelcontextprotocol/protocolVersion": mcpProtocolVersion,
                    "io.modelcontextprotocol/clientInfo": ["name": "Hades.app onboarding", "version": "1"],
                    "io.modelcontextprotocol/clientCapabilities": [String: Any](),
                ] as [String: Any]
            ],
        ])

        guard let (data, response) = try? await session.data(for: request),
            let http = response as? HTTPURLResponse, (200...299).contains(http.statusCode)
        else { return .unreachable }

        guard let count = toolCount(fromEnvelope: data) else { return .unreachable }
        return .reachable(toolCount: count)
    }

    /// Unwraps a plain-JSON or SSE-wrapped (`"data: {...}"`) response body exactly the way
    /// `Hades.Server.Tests.McpTestClient.ReadEnvelope` does server-side, then counts `result.tools`
    /// - a raw array length, never a tool's name or shape (nothing here needs either).
    static func toolCount(fromEnvelope data: Data) -> Int? {
        guard let text = String(data: data, encoding: .utf8) else { return nil }

        let jsonLine =
            text
            .split(separator: "\n", omittingEmptySubsequences: false)
            .map { line in line.hasPrefix("data: ") ? line.dropFirst(6) : line }
            .first { $0.trimmingCharacters(in: .whitespaces).hasPrefix("{") } ?? Substring(text)

        guard let envelopeData = String(jsonLine).data(using: .utf8),
            let envelope = try? JSONDecoder().decode(ToolsListEnvelope.self, from: envelopeData)
        else { return nil }

        return envelope.result?.tools.count
    }

    private struct ToolsListEnvelope: Decodable {
        struct ToolsResult: Decodable {
            let tools: [IgnoredToolEntry]
        }
        let result: ToolsResult?
    }

    /// Decodes against any JSON object shape - this check only ever needs `result.tools.count`,
    /// never a tool's name, schema, or annotations.
    private struct IgnoredToolEntry: Decodable {}
}
