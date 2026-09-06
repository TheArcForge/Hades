import Foundation

// This file decodes the control API's response bodies into plain data. Per spec #3 §1 ("Swift
// renders, .NET decides") these types combine nothing, format nothing, and derive nothing: every
// property is either read verbatim off a JSON field the core already resolved, or is `Optional`
// because the core's own JSON omits it (see JsonIgnoreCondition.WhenWritingNull on the server
// side). A view is expected to print `status`/`headline`/`message` etc. verbatim.
//
// Mirrors, field for field, the record types in Core/src/Hades.Server/Control/{SummaryEndpoint,
// ProjectsEndpoint,EditorsEndpoint}.cs and PingResult in Core/src/Hades.Server/Mcp/SummaryTools.cs.

// MARK: - Enums

/// Shared decode behaviour for every closed string enum the control API sends: an unrecognised
/// raw value decodes to a defined fallback case rather than throwing, so a newer core can add a
/// case (e.g. a new `iconState`) without crashing an older Swift client. This is the ONLY
/// behaviour these enums have beyond carrying a raw string - no case ever maps to different
/// display text; a view prints the sibling `status`/`headline` string instead.
public protocol ControlEnum: RawRepresentable, Decodable, Equatable, Sendable where RawValue == String {
    /// The case an unrecognised raw value decodes to.
    static var unknownFallback: Self { get }
}

extension ControlEnum {
    public init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = Self(rawValue: raw) ?? Self.unknownFallback
    }
}

/// Mirrors `Hades.Server.Control.ControlIconState`. The menu bar's icon state, decided entirely
/// by the core - precedence between projects is already resolved server-side; Swift never
/// compares rows to pick one.
public enum ControlIconState: String, ControlEnum {
    case idle
    case indexing
    case attached
    case leaseHeld
    case error

    /// Decode target for any value this build does not recognise. Never sent by the core today;
    /// exists so a future core adding a new state cannot crash this client.
    case unknown

    public static var unknownFallback: Self { .unknown }
}

/// Mirrors `Hades.Server.Control.ControlSeverity`. A row's already-resolved severity.
public enum ControlSeverity: String, ControlEnum {
    case ok
    case warning
    case error

    /// Decode target for any value this build does not recognise - see `ControlIconState.unknown`.
    case unknown

    public static var unknownFallback: Self { .unknown }
}

/// Mirrors `Hades.Server.Control.ProjectEditorState`.
public enum ProjectEditorState: String, ControlEnum {
    case attached
    case busy
    case absent

    /// Decode target for any value this build does not recognise - see `ControlIconState.unknown`.
    case unknown

    public static var unknownFallback: Self { .unknown }
}

/// Mirrors `Hades.Server.Control.ProjectIndexState`.
public enum ProjectIndexState: String, ControlEnum {
    case indexed

    /// An index or rebuild is running for this project RIGHT NOW.
    case indexing

    /// No index has ever completed, and none is running.
    ///
    /// Added 2026-09-01. This and `indexing` were one case, meaning "no index has completed in the
    /// core's current process" - which conflated two different facts and, because the timestamp
    /// behind it did not survive a restart, made every project report as permanently indexing after
    /// every launch. The menu bar showed a spinner and "Indexing X…" over a finished graph with
    /// nothing running. See the server-side enum for the full account.
    case neverIndexed

    /// Decode target for any value this build does not recognise - see `ControlIconState.unknown`.
    case unknown

    public static var unknownFallback: Self { .unknown }
}

/// Mirrors `Hades.Server.Control.OperationState`. A long-running control-API action's state (today,
/// only `rebuild` reaches this) - the shell maps this straight to a spinner/checkmark/error icon and
/// does nothing else, same rule as `ControlIconState`.
public enum OperationState: String, ControlEnum {
    case running
    case done
    case failed

    /// Decode target for any value this build does not recognise - see `ControlIconState.unknown`.
    case unknown

    public static var unknownFallback: Self { .unknown }
}

/// Mirrors `Hades.Server.Control.TraceOutcome`. A trace or sequence's already-resolved outcome -
/// Swift never infers this from a raw status string itself.
public enum TraceOutcome: String, ControlEnum {
    case ok
    case error

    /// Decode target for any value this build does not recognise - see `ControlIconState.unknown`.
    case unknown

    public static var unknownFallback: Self { .unknown }
}

// MARK: - Generic JSON values

/// One JSON value with no fixed shape - used ONLY for the two wire fields in this API that are
/// genuinely dynamic on the .NET side too, never as a shortcut around defining a real DTO:
/// `SpanRow.attributes`/`events` are `JsonElement?` (parsed nested JSON whose shape depends on
/// which MCP tool the span belongs to - see a live `attributes.arguments` in
/// `trace_detail_success.json`), and `OperationResult.result` is `object?` (shape depends on
/// `OperationResult.kind` - today always `RebuildOperationResult`-shaped when `kind` is
/// `"rebuild"`, the only kind that exists, but `kind` is a plain string specifically so a future
/// kind can carry a different result shape with no wire change - see
/// `Hades.Server.Control.OperationRecord.Kind`'s own doc comment). Every other field in this file
/// has one fixed shape and its own struct.
public enum ControlJSONValue: Decodable, Equatable, Sendable {
    case string(String)
    case int(Int)
    case double(Double)
    case bool(Bool)
    case object([String: ControlJSONValue])
    case array([ControlJSONValue])
    case null

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Int.self) {
            self = .int(value)
        } else if let value = try? container.decode(Double.self) {
            self = .double(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode([String: ControlJSONValue].self) {
            self = .object(value)
        } else if let value = try? container.decode([ControlJSONValue].self) {
            self = .array(value)
        } else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "Unsupported JSON value")
        }
    }

    /// Object-keyed access when this value is `.object`; `nil` otherwise, including when the key is
    /// absent - lets a caller read e.g. `attributes["arguments"]?["namePattern"]` without a nested
    /// `switch` at every level.
    public subscript(key: String) -> ControlJSONValue? {
        if case .object(let dict) = self { return dict[key] }
        return nil
    }

    public var stringValue: String? { if case .string(let value) = self { value } else { nil } }
    public var intValue: Int? { if case .int(let value) = self { value } else { nil } }
    public var arrayValue: [ControlJSONValue]? { if case .array(let value) = self { value } else { nil } }
}

// MARK: - GET /control/ping

/// Mirrors `Hades.Server.Mcp.PingResult`.
public struct PingResult: Decodable, Equatable, Sendable {
    public let version: String
    public let uptimeSeconds: Double

    public init(version: String, uptimeSeconds: Double) {
        self.version = version
        self.uptimeSeconds = uptimeSeconds
    }
}

// MARK: - GET /control/summary

/// Mirrors `Hades.Server.Control.SummaryRow`. One project's line in the menu bar; `status` is the
/// complete, human-readable string to print verbatim. `productGuid` is this project's stable
/// identity - a view must key/identify rows by this, never by `project` (the display name), which
/// two different projects can share (e.g. two checkouts of the same repo).
public struct SummaryRow: Decodable, Equatable, Sendable {
    public let project: String
    public let productGuid: String
    public let status: String
    public let severity: ControlSeverity

    public init(project: String, productGuid: String, status: String, severity: ControlSeverity) {
        self.project = project
        self.productGuid = productGuid
        self.status = status
        self.severity = severity
    }
}

/// Mirrors `Hades.Server.Control.SummaryLease`. The held reload lease worth surfacing right now.
/// `heldForSeconds`/`expiresInSeconds` are already whole seconds the core computed; Swift never
/// subtracts timestamps. `leaseId` is what a Release button passes as `{id}` to
/// `POST /control/leases/{id}/release`.
public struct SummaryLease: Decodable, Equatable, Sendable {
    public let project: String
    public let leaseId: String
    public let heldForSeconds: Int
    public let expiresInSeconds: Int
    public let releasable: Bool

    public init(project: String, leaseId: String, heldForSeconds: Int, expiresInSeconds: Int, releasable: Bool) {
        self.project = project
        self.leaseId = leaseId
        self.heldForSeconds = heldForSeconds
        self.expiresInSeconds = expiresInSeconds
        self.releasable = releasable
    }
}

/// Mirrors `Hades.Server.Control.SummaryResult`, the full `GET /control/summary` response.
/// `lease` is absent from the JSON (not `null`) whenever no project holds one - see
/// `ControlClientTests`/`DTODecodingTests` for a fixture proving the field is genuinely absent.
public struct SummaryResult: Decodable, Equatable, Sendable {
    public let iconState: ControlIconState
    public let headline: String
    public let rows: [SummaryRow]
    public let lease: SummaryLease?

    public init(iconState: ControlIconState, headline: String, rows: [SummaryRow], lease: SummaryLease?) {
        self.iconState = iconState
        self.headline = headline
        self.rows = rows
        self.lease = lease
    }
}

// MARK: - GET /control/projects

/// Mirrors `Hades.Server.Control.ProjectWarning`. One resolved, human-readable warning about a
/// project. `code` is a plain string on the .NET side too (not a closed enum), so it is never
/// switched on here - `message`/`remedy` are the complete strings to display.
public struct ProjectWarning: Decodable, Equatable, Sendable {
    public let code: String
    public let severity: ControlSeverity
    public let message: String
    public let remedy: String

    public init(code: String, severity: ControlSeverity, message: String, remedy: String) {
        self.code = code
        self.severity = severity
        self.message = message
        self.remedy = remedy
    }
}

/// Mirrors `Hades.Server.Control.ProjectEditorInfo`, the attached-Editor half of a project row.
/// `unityVersion`/`processId`/`connectionAgeSeconds` are present only when `state` is not
/// `.absent`.
public struct ProjectEditorInfo: Decodable, Equatable, Sendable {
    public let state: ProjectEditorState
    public let status: String
    public let unityVersion: String?
    public let processId: Int?
    public let connectionAgeSeconds: Int?

    public init(
        state: ProjectEditorState, status: String, unityVersion: String?, processId: Int?,
        connectionAgeSeconds: Int?
    ) {
        self.state = state
        self.status = status
        self.unityVersion = unityVersion
        self.processId = processId
        self.connectionAgeSeconds = connectionAgeSeconds
    }
}

/// Mirrors `Hades.Server.Control.ProjectRow`, one project's fully-resolved row for
/// `GET /control/projects`. `unityVersion` is null (omitted) only when the project's path is
/// gone AND it was never attached in this process.
public struct ProjectRow: Decodable, Equatable, Sendable {
    public let name: String
    public let path: String
    public let productGuid: String
    public let unityVersion: String?
    public let indexState: ProjectIndexState
    public let indexStatus: String
    public let nodeCount: Int
    public let edgeCount: Int
    public let editor: ProjectEditorInfo
    public let warnings: [ProjectWarning]

    public init(
        name: String, path: String, productGuid: String, unityVersion: String?,
        indexState: ProjectIndexState, indexStatus: String, nodeCount: Int, edgeCount: Int,
        editor: ProjectEditorInfo, warnings: [ProjectWarning]
    ) {
        self.name = name
        self.path = path
        self.productGuid = productGuid
        self.unityVersion = unityVersion
        self.indexState = indexState
        self.indexStatus = indexStatus
        self.nodeCount = nodeCount
        self.edgeCount = edgeCount
        self.editor = editor
        self.warnings = warnings
    }
}

/// Mirrors `Hades.Server.Control.ProjectsResult`, the full `GET /control/projects` response.
public struct ProjectsResult: Decodable, Equatable, Sendable {
    public let projects: [ProjectRow]

    public init(projects: [ProjectRow]) {
        self.projects = projects
    }
}

// MARK: - GET /control/editors

/// Mirrors `Hades.Server.Control.EditorRow`, one currently-attached Unity Editor. Only
/// `.attached`/`.busy` ever appear in `state` here - an editor that is not attached is not a row
/// at all, so `GET /control/editors` never needs an `.absent` entry.
public struct EditorRow: Decodable, Equatable, Sendable {
    public let project: String
    public let productGuid: String
    public let state: ProjectEditorState
    public let status: String
    public let unityVersion: String?
    public let processId: Int?
    public let connectionAgeSeconds: Int?

    public init(
        project: String, productGuid: String, state: ProjectEditorState, status: String,
        unityVersion: String?, processId: Int?, connectionAgeSeconds: Int?
    ) {
        self.project = project
        self.productGuid = productGuid
        self.state = state
        self.status = status
        self.unityVersion = unityVersion
        self.processId = processId
        self.connectionAgeSeconds = connectionAgeSeconds
    }
}

/// Mirrors `Hades.Server.Control.EditorsResult`, the full `GET /control/editors` response.
public struct EditorsResult: Decodable, Equatable, Sendable {
    public let editors: [EditorRow]

    public init(editors: [EditorRow]) {
        self.editors = editors
    }
}

// MARK: - POST /control/leases/{id}/release

/// Mirrors `Hades.Server.Control.ActionResult`, the response of
/// `POST /control/leases/{id}/release`. `message` is always the complete, human-readable sentence
/// to display - including the idempotent "nothing to release" case, which is `success: true`, not
/// an error (see `ControlClient.releaseLease`'s own doc comment).
public struct ActionResult: Decodable, Equatable, Sendable {
    public let success: Bool
    public let message: String

    public init(success: Bool, message: String) {
        self.success = success
        self.message = message
    }
}

// MARK: - POST /control/projects/add

/// Body of `POST /control/projects/add`. Mirrors `Hades.Server.Control.AddProjectRequest`. The
/// panel that picks `path` is the only place the shell chooses one - this type just carries it.
public struct AddProjectRequest: Encodable, Equatable, Sendable {
    public let path: String

    public init(path: String) {
        self.path = path
    }
}

// The 200 response of `add` is a `ProjectRow` - `ProjectsEndpoint.AddAsync` returns the same
// `BuildRow(...)` every row in `GET /control/projects` does, not wrapped in a `ProjectsResult` - so
// no new type is declared here; see `ControlClient.addProject`.

// MARK: - POST /control/projects/{productGuid}/rebuild

/// Mirrors `Hades.Server.Control.RebuildStartedResult`, the response of
/// `POST /control/projects/{id}/rebuild`. `operationId` is pollable via
/// `GET /control/operations/{id}` from the moment this response returns.
public struct RebuildStartedResult: Decodable, Equatable, Sendable {
    public let operationId: String

    public init(operationId: String) {
        self.operationId = operationId
    }
}

// MARK: - GET /control/operations/{id}

/// Mirrors `Hades.Server.Control.OperationResult`, the full `GET /control/operations/{id}`
/// response. `progress`/`error`/`result` are absent (never null) unless applicable to the current
/// `state` - see `operation_running.json` (all three absent) vs `operation_done.json` (`result`
/// present). `progress` specifically is reserved: nothing in this app populates it today (see
/// `Hades.Server.Control.OperationRecord.Progress`'s own doc comment), so every fixture this
/// package has proves it absent, never present - that is the real, current shape, not a gap in
/// coverage.
///
/// `startedAtUtc`/`finishedAtUtc` are carried verbatim as `String`, never parsed into `Date`:
/// `elapsedSeconds` is the same fact already resolved server-side (Plan 11 Task 7's own no-logic
/// audit fix, `Operations.cs`'s own doc comment), and a raw `Date` in scope is exactly the trap
/// that audit exists to prevent - `Date().timeIntervalSince(startedAtUtc)` reads as ordinary Swift
/// but re-derives what the core already computed. A view that ever needs the raw instant for
/// something `elapsedSeconds` cannot answer still has the string.
///
/// `result`'s shape depends on `kind` - decoded as `ControlJSONValue?` rather than a concrete
/// `RebuildOperationResult` struct precisely because the wire contract does not fix one shape (see
/// `ControlJSONValue`'s own doc comment); a view reads e.g. `result?["message"]?.stringValue`.
public struct OperationResult: Decodable, Equatable, Sendable {
    public let id: String
    public let kind: String
    public let state: OperationState
    public let startedAtUtc: String
    public let finishedAtUtc: String?
    public let elapsedSeconds: Int
    public let progress: String?
    public let error: String?
    public let result: ControlJSONValue?

    public init(
        id: String, kind: String, state: OperationState, startedAtUtc: String, finishedAtUtc: String?,
        elapsedSeconds: Int, progress: String?, error: String?, result: ControlJSONValue?
    ) {
        self.id = id
        self.kind = kind
        self.state = state
        self.startedAtUtc = startedAtUtc
        self.finishedAtUtc = finishedAtUtc
        self.elapsedSeconds = elapsedSeconds
        self.progress = progress
        self.error = error
        self.result = result
    }
}

// MARK: - POST /control/projects/{productGuid}/installPlugin

/// Mirrors `Hades.Server.Control.InstallPluginResult`. `needsRestart` is true only when an Editor
/// was already attached at the moment the plugin was written - installing into a project with no
/// Editor open needs no restart at all.
public struct InstallPluginResult: Decodable, Equatable, Sendable {
    public let success: Bool
    public let needsRestart: Bool
    public let message: String

    public init(success: Bool, needsRestart: Bool, message: String) {
        self.success = success
        self.needsRestart = needsRestart
        self.message = message
    }
}

// POST .../remove, .../revealInFinder, and .../openInUnity all respond with the existing
// `ActionResult` above - no new type needed; see `ControlClient.removeProject`/`revealInFinder`/
// `openInUnity`.

// MARK: - GET /control/settings

/// Mirrors `Hades.Server.Control.McpPortSetting`. `inUse` is a live TCP bind probe the core runs
/// fresh on every call, never cached and never inferred client-side from a failed connection of its
/// own - see `settings_mcp_port_in_use.json`, a live capture of the conflict state itself (this
/// package's own test server was occupying 7823 at capture time).
public struct McpPortSetting: Decodable, Equatable, Sendable {
    public let port: Int
    public let inUse: Bool
    public let message: String

    public init(port: Int, inUse: Bool, message: String) {
        self.port = port
        self.inUse = inUse
        self.message = message
    }
}

/// Mirrors `Hades.Server.Control.LogLevelSetting`. Read from the real running server configuration,
/// genuinely live.
public struct LogLevelSetting: Decodable, Equatable, Sendable {
    public let level: String

    public init(level: String) {
        self.level = level
    }
}

/// Mirrors `Hades.Server.Control.SettingsResult`, the full `GET /control/settings` response.
/// Deliberately just two fields - Plan 13 Task 7 removed `launchAtLogin`/`resourceGuards` entirely
/// (both used to decode here as `LaunchAtLoginSetting`/`ResourceGuardsSetting`, hardcoded core-side
/// constants with nothing behind them). Both are OS facts only the Swift shell can observe - see
/// `Sources/HadesApp/ShellFacts/{LaunchAtLoginService,ResourceGuardReader}.swift`, the plan's own
/// named carve-out, for where each now actually lives.
public struct SettingsResult: Decodable, Equatable, Sendable {
    public let mcpPort: McpPortSetting
    public let logLevel: LogLevelSetting

    public init(mcpPort: McpPortSetting, logLevel: LogLevelSetting) {
        self.mcpPort = mcpPort
        self.logLevel = logLevel
    }
}

// MARK: - GET /control/traces/sequences

/// Mirrors `Hades.Server.Control.TraceSequenceRow`. `pattern` is the complete, already
/// arrow-joined tool sequence to print verbatim - Swift never rejoins `tools` itself. Per spec
/// #3 §3.3, sequences (not individual calls) are the evidence source for the tool-consolidation
/// backlog item, so `tools`/`pattern`/`traceIds` describe the whole grouped run, not one call.
public struct TraceSequenceRow: Decodable, Equatable, Sendable {
    public let id: String
    public let tools: [String]
    public let pattern: String
    public let callCount: Int
    public let startUtcMs: Int
    public let endUtcMs: Int
    public let durationMs: Int
    public let outcome: TraceOutcome
    public let traceIds: [String]

    public init(
        id: String, tools: [String], pattern: String, callCount: Int, startUtcMs: Int,
        endUtcMs: Int, durationMs: Int, outcome: TraceOutcome, traceIds: [String]
    ) {
        self.id = id
        self.tools = tools
        self.pattern = pattern
        self.callCount = callCount
        self.startUtcMs = startUtcMs
        self.endUtcMs = endUtcMs
        self.durationMs = durationMs
        self.outcome = outcome
        self.traceIds = traceIds
    }
}

/// Mirrors `Hades.Server.Control.TraceSequencesResult`, the full `GET /control/traces/sequences`
/// response. `truncated` is true when the underlying trace fetch hit its own limit - older
/// sequences may exist beyond what is returned here.
public struct TraceSequencesResult: Decodable, Equatable, Sendable {
    public let sequences: [TraceSequenceRow]
    public let truncated: Bool

    public init(sequences: [TraceSequenceRow], truncated: Bool) {
        self.sequences = sequences
        self.truncated = truncated
    }
}

// MARK: - GET /control/traces/{traceId}

/// Mirrors `Hades.Server.Control.SpanAttributeRow`. One already-rendered leaf out of a span's
/// `attributes`/`events` JSON tree, flattened server-side - `key` is the leaf's structural path
/// (dot-joined object keys, bracketed array indices), `valueDisplay` is the exact text this leaf
/// reads as, already resolved by .NET: a JSON string's own decoded text verbatim, or - for every
/// other JSON scalar (number, bool, null) - the literal JSON token as written. This is what closes
/// the Plan 13 Task 5 gap: this package's own (now-retired) `ControlJSONValue.stringLeaves()` could
/// only ever surface a `.string` leaf, so `resultSizeBytes`/`timeUtcMs`/every other numeric or
/// boolean value was silently invisible to every view - stringifying a number client-side would be
/// Swift deciding how it reads, exactly what spec #3 §1 forbids. See
/// `trace_detail_success.json`/`trace_detail_failure.json` for real captures of both a successful
/// call's `attributes` (including `resultSizeBytes`, previously unreachable) and a failed call's
/// `events` exception entry (including `timeUtcMs`, likewise).
public struct SpanAttributeRow: Decodable, Equatable, Sendable {
    public let key: String
    public let valueDisplay: String

    public init(key: String, valueDisplay: String) {
        self.key = key
        self.valueDisplay = valueDisplay
    }
}

/// Mirrors `Hades.Server.Control.SpanRow`, one span wire-shaped. `attributes`/`events` are
/// pre-rendered, flattened `SpanAttributeRow` lists - see that type's own doc comment for exactly
/// what "pre-rendered" means and why. Null exactly when the underlying column is null (nothing
/// recorded); a present-but-empty list is a theoretically possible, practically unseen "recorded but
/// nothing in it" state, kept distinct from null rather than collapsed. `parentSpanId` is always
/// absent in every fixture this package has: nothing in this app records a nested child span today,
/// only ever the one top-level `tool_call` span per trace - the field is real (reserved for if that
/// ever changes), not proven reachable.
public struct SpanRow: Decodable, Equatable, Sendable {
    public let spanId: String
    public let parentSpanId: String?
    public let name: String
    public let kind: String
    public let startUtcMs: Int
    public let endUtcMs: Int?
    public let durationMs: Int?
    public let status: String?
    public let attributes: [SpanAttributeRow]?
    public let events: [SpanAttributeRow]?

    public init(
        spanId: String, parentSpanId: String?, name: String, kind: String, startUtcMs: Int,
        endUtcMs: Int?, durationMs: Int?, status: String?, attributes: [SpanAttributeRow]?,
        events: [SpanAttributeRow]?
    ) {
        self.spanId = spanId
        self.parentSpanId = parentSpanId
        self.name = name
        self.kind = kind
        self.startUtcMs = startUtcMs
        self.endUtcMs = endUtcMs
        self.durationMs = durationMs
        self.status = status
        self.attributes = attributes
        self.events = events
    }
}

/// Mirrors `Hades.Server.Control.TraceDetailResult`, the full `GET /control/traces/{traceId}`
/// response - one trace with every span it owns.
public struct TraceDetailResult: Decodable, Equatable, Sendable {
    public let traceId: String
    public let tool: String
    public let startUtcMs: Int
    public let endUtcMs: Int?
    public let durationMs: Int?
    public let outcome: TraceOutcome
    public let spans: [SpanRow]

    public init(
        traceId: String, tool: String, startUtcMs: Int, endUtcMs: Int?, durationMs: Int?,
        outcome: TraceOutcome, spans: [SpanRow]
    ) {
        self.traceId = traceId
        self.tool = tool
        self.startUtcMs = startUtcMs
        self.endUtcMs = endUtcMs
        self.durationMs = durationMs
        self.outcome = outcome
        self.spans = spans
    }
}

// MARK: - GET /control/traces/slow

/// Mirrors `Hades.Server.Control.SlowToolRow`.
public struct SlowToolRow: Decodable, Equatable, Sendable {
    public let tool: String
    public let callCount: Int
    public let averageDurationMs: Double
    public let maxDurationMs: Int

    public init(tool: String, callCount: Int, averageDurationMs: Double, maxDurationMs: Int) {
        self.tool = tool
        self.callCount = callCount
        self.averageDurationMs = averageDurationMs
        self.maxDurationMs = maxDurationMs
    }
}

/// Mirrors `Hades.Server.Control.SlowToolsResult`, the full `GET /control/traces/slow` response.
public struct SlowToolsResult: Decodable, Equatable, Sendable {
    public let tools: [SlowToolRow]

    public init(tools: [SlowToolRow]) {
        self.tools = tools
    }
}

// MARK: - GET /control/traces/failures

/// Mirrors `Hades.Server.Control.FailedCallRow`. `error` is the triggering exception's own
/// message, read back off the trace's root span - held to the same "specific and actionable"
/// standard as every other failure surface in this API.
public struct FailedCallRow: Decodable, Equatable, Sendable {
    public let traceId: String
    public let tool: String
    public let startUtcMs: Int
    public let durationMs: Int?
    public let error: String?

    public init(traceId: String, tool: String, startUtcMs: Int, durationMs: Int?, error: String?) {
        self.traceId = traceId
        self.tool = tool
        self.startUtcMs = startUtcMs
        self.durationMs = durationMs
        self.error = error
    }
}

/// Mirrors `Hades.Server.Control.FailedCallsResult`, the full `GET /control/traces/failures`
/// response.
public struct FailedCallsResult: Decodable, Equatable, Sendable {
    public let failures: [FailedCallRow]

    public init(failures: [FailedCallRow]) {
        self.failures = failures
    }
}

// MARK: - GET /control/memory

/// Mirrors `Hades.Server.Control.MemoryDocumentRow`. `sizeDisplay` is the already-formatted,
/// human-readable size ("500 B", "2.0 KB") to print verbatim - Swift never converts `sizeBytes`
/// itself. `lastReviewed` is absent unless the document's own frontmatter sets a `last_reviewed`
/// key - see `memory_populated.json`, which has both a document with it and one without.
public struct MemoryDocumentRow: Decodable, Equatable, Sendable {
    public let name: String
    public let sizeBytes: Int
    public let sizeDisplay: String
    public let lastReviewed: String?

    public init(name: String, sizeBytes: Int, sizeDisplay: String, lastReviewed: String?) {
        self.name = name
        self.sizeBytes = sizeBytes
        self.sizeDisplay = sizeDisplay
        self.lastReviewed = lastReviewed
    }
}

/// Mirrors `Hades.Server.Control.MemoryProposalRow`. `status` is a plain string on the .NET side,
/// not a closed enum, so it is never switched on here - and a live capture already shows a value
/// beyond `pending`/`accepted`/`deferred`: `inferred`, for a proposal an analyzer wrote rather than
/// a human review action (see `memory_populated.json`). `createdAtUtc`/`createdAgo` are absent
/// together on exactly those `inferred` rows, in the very same fixture - proof of the null-omission
/// rule that did not need a second, separately-captured payload.
///
/// `createdAtUtc` is carried verbatim as `String`, never parsed into `Date`: `createdAgo` is the
/// same fact already resolved server-side (a review queue's whole reason for existing is triage by
/// age, exactly what a raw `Date` in scope invites Swift to re-derive itself - see
/// `OperationResult.startedAtUtc`'s own doc comment for the general rule this follows).
public struct MemoryProposalRow: Decodable, Equatable, Sendable {
    public let fileName: String
    public let targetFile: String
    public let createdAtUtc: String?
    public let createdAgo: String?
    public let rationale: String
    public let status: String
    public let content: String

    public init(
        fileName: String, targetFile: String, createdAtUtc: String?, createdAgo: String?,
        rationale: String, status: String, content: String
    ) {
        self.fileName = fileName
        self.targetFile = targetFile
        self.createdAtUtc = createdAtUtc
        self.createdAgo = createdAgo
        self.rationale = rationale
        self.status = status
        self.content = content
    }
}

/// Mirrors `Hades.Server.Control.MemoryResult`, the full `GET /control/memory` response -
/// documents and the proposal queue together in one round trip, since spec #3 §3.4 is one shell
/// view showing both at once.
public struct MemoryResult: Decodable, Equatable, Sendable {
    public let documents: [MemoryDocumentRow]
    public let proposals: [MemoryProposalRow]

    public init(documents: [MemoryDocumentRow], proposals: [MemoryProposalRow]) {
        self.documents = documents
        self.proposals = proposals
    }
}

// MARK: - GET /control/memory/document

/// Mirrors `Hades.Server.Control.MemoryDocumentResult` - one document's complete raw text,
/// frontmatter and all, exactly as authored, so a round-trip edit (read, change, write back) never
/// silently drops or reformats anything Swift did not touch.
public struct MemoryDocumentResult: Decodable, Equatable, Sendable {
    public let name: String
    public let content: String

    public init(name: String, content: String) {
        self.name = name
        self.content = content
    }
}

// MARK: - POST /control/memory/document

/// Body of `POST /control/memory/document`. Mirrors
/// `Hades.Server.Control.WriteMemoryDocumentRequest`.
public struct WriteMemoryDocumentRequest: Encodable, Equatable, Sendable {
    public let content: String

    public init(content: String) {
        self.content = content
    }
}

// POST /control/memory/proposals/{accept,dismiss,defer} all respond with the existing
// `ActionResult` above - no new type needed; see `ControlClient.acceptMemoryProposal`/
// `dismissMemoryProposal`/`deferMemoryProposal`.

// MARK: - /control/migration/* (Plan 14 Task 10 - the missing caller)
//
// Mirrors, field for field, Core/src/Hades.Server/Control/MigrationEndpoint.cs. That file's own
// class doc comment is the full design reference: detection is read-only and safe to call any
// time; memory/traces import needs no `proceed` (non-destructive by construction); every cleanup
// route stays independently authorised - no "clean everything" route exists on either side of the
// wire.

/// Mirrors `Hades.Server.Control.MigrationClaudeMdShape`.
public enum MigrationClaudeMdShape: String, ControlEnum {
    case absent
    case marked
    case unmarked

    /// Decode target for any value this build does not recognise - see `ControlIconState.unknown`.
    case unknown

    public static var unknownFallback: Self { .unknown }
}

/// Mirrors `Hades.Server.Control.MigrationManifestEntryInfo`.
public struct MigrationManifestEntryInfo: Decodable, Equatable, Sendable {
    public let present: Bool
    public let value: String?
    public let resolvedPath: String?

    public init(present: Bool, value: String?, resolvedPath: String?) {
        self.present = present
        self.value = value
        self.resolvedPath = resolvedPath
    }
}

/// Mirrors `Hades.Server.Control.MigrationClaudeMdInfo`. Deliberately just a shape - no marker
/// offsets on the wire at all, so this client never needs to reason about where in the file a
/// block sits; see `MigrationEndpoint.CleanClaudeMd`'s own doc comment for why cleanup re-detects
/// fresh server-side instead.
public struct MigrationClaudeMdInfo: Decodable, Equatable, Sendable {
    public let shape: MigrationClaudeMdShape

    public init(shape: MigrationClaudeMdShape) {
        self.shape = shape
    }
}

/// Mirrors `Hades.Server.Control.MigrationDetectionResult` - the full
/// `GET /control/migration/{productGuid}/detect` response.
public struct MigrationDetectionResult: Decodable, Equatable, Sendable {
    public let projectRoot: String
    public let isV12Project: Bool
    public let manifestEntry: MigrationManifestEntryInfo
    public let hasMemory: Bool
    public let memoryDocumentCount: Int
    public let hasTraces: Bool
    public let hasGraph: Bool
    public let hasGeneratedMcpConfig: Bool
    public let claudeMd: MigrationClaudeMdInfo
    public let hasUnityPlugin: Bool

    public init(
        projectRoot: String, isV12Project: Bool, manifestEntry: MigrationManifestEntryInfo,
        hasMemory: Bool, memoryDocumentCount: Int, hasTraces: Bool, hasGraph: Bool,
        hasGeneratedMcpConfig: Bool, claudeMd: MigrationClaudeMdInfo, hasUnityPlugin: Bool
    ) {
        self.projectRoot = projectRoot
        self.isV12Project = isV12Project
        self.manifestEntry = manifestEntry
        self.hasMemory = hasMemory
        self.memoryDocumentCount = memoryDocumentCount
        self.hasTraces = hasTraces
        self.hasGraph = hasGraph
        self.hasGeneratedMcpConfig = hasGeneratedMcpConfig
        self.claudeMd = claudeMd
        self.hasUnityPlugin = hasUnityPlugin
    }
}

/// Mirrors `Hades.Server.Control.MigrationMemorySkip`.
public struct MigrationMemorySkip: Decodable, Equatable, Sendable {
    public let source: String
    public let reason: String

    public init(source: String, reason: String) {
        self.source = source
        self.reason = reason
    }
}

/// Mirrors `Hades.Server.Control.MigrationMemoryImportResult` - the response of
/// `POST /control/migration/{productGuid}/importMemory`.
public struct MigrationMemoryImportResult: Decodable, Equatable, Sendable {
    public let imported: [String]
    public let skipped: [MigrationMemorySkip]

    public init(imported: [String], skipped: [MigrationMemorySkip]) {
        self.imported = imported
        self.skipped = skipped
    }
}

/// Mirrors `Hades.Server.Control.MigrationTracesImportResult` - the response of
/// `POST /control/migration/{productGuid}/importTraces`.
public struct MigrationTracesImportResult: Decodable, Equatable, Sendable {
    public let imported: Bool
    public let skippedReason: String?

    public init(imported: Bool, skippedReason: String?) {
        self.imported = imported
        self.skippedReason = skippedReason
    }
}

/// Mirrors `Hades.Server.Control.MigrationClaudeMdCleanupResult`. `remainingContentOutsideBlock`
/// is the field that keeps "cleanup succeeded" and "the file is now clean" from collapsing into
/// one claim - see that property's own doc comment on the .NET side.
public struct MigrationClaudeMdCleanupResult: Decodable, Equatable, Sendable {
    public let removed: Bool
    public let message: String
    public let remainingContentOutsideBlock: Bool

    public init(removed: Bool, message: String, remainingContentOutsideBlock: Bool) {
        self.removed = removed
        self.message = message
        self.remainingContentOutsideBlock = remainingContentOutsideBlock
    }
}

/// Mirrors `Hades.Server.Control.MigrationManifestCleanupResult`.
public struct MigrationManifestCleanupResult: Decodable, Equatable, Sendable {
    public let removed: Bool
    public let message: String
    public let occurrencesFound: Int
    public let portConflictWarning: String

    public init(removed: Bool, message: String, occurrencesFound: Int, portConflictWarning: String) {
        self.removed = removed
        self.message = message
        self.occurrencesFound = occurrencesFound
        self.portConflictWarning = portConflictWarning
    }
}

/// Mirrors `Hades.Server.Control.MigrationMcpConfigCleanupResult`.
public struct MigrationMcpConfigCleanupResult: Decodable, Equatable, Sendable {
    public let removed: Bool
    public let message: String

    public init(removed: Bool, message: String) {
        self.removed = removed
        self.message = message
    }
}

/// Mirrors `Hades.Server.Control.MigrationClaudeDesktopConfigCleanupResult`. `scopeWarning` is
/// always populated - this file is global and per-user, not per-project (see
/// `ControlClient.migrationCleanClaudeDesktopConfig`'s own doc comment for the route this backs,
/// which carries no productGuid at all). `occurrencesFound` is likewise always populated, including
/// when `removed` is false - this route has no companion per-project detect endpoint the way
/// `MigrationDetectionResult` gives the other three cleanup targets, so this field is a caller's
/// only way to learn whether there is a "hades" entry here worth offering to clean up at all.
public struct MigrationClaudeDesktopConfigCleanupResult: Decodable, Equatable, Sendable {
    public let removed: Bool
    public let message: String
    public let scopeWarning: String
    public let occurrencesFound: Int

    public init(removed: Bool, message: String, scopeWarning: String, occurrencesFound: Int) {
        self.removed = removed
        self.message = message
        self.scopeWarning = scopeWarning
        self.occurrencesFound = occurrencesFound
    }
}

/// Mirrors `Hades.Server.Control.MigrationHadesHubCleanupResult` - the fifth `V12Cleanup` target,
/// closing the spec #4 §1 gap where `~/.arcforge/hades-hub/launcher.js` (the retired v1.2 stdio
/// launcher) was named among what v2 retires but no cleanup method ever removed it. `found` is
/// always populated, including when `removed` is false - same reasoning as
/// `MigrationClaudeDesktopConfigCleanupResult.occurrencesFound`: this route has no companion
/// per-project detect endpoint, so this field is a caller's only way to learn whether there is
/// anything here worth offering to clean up at all.
public struct MigrationHadesHubCleanupResult: Decodable, Equatable, Sendable {
    public let removed: Bool
    public let message: String
    public let found: Bool

    public init(removed: Bool, message: String, found: Bool) {
        self.removed = removed
        self.message = message
        self.found = found
    }
}

/// Body of every migration cleanup POST route. Mirrors
/// `Hades.Server.Control.MigrationCleanupRequest` - `proceed` has no default here either, matching
/// `V12Cleanup`'s own required-no-default rule on the .NET side.
public struct MigrationCleanupRequest: Encodable, Equatable, Sendable {
    public let proceed: Bool

    public init(proceed: Bool) {
        self.proceed = proceed
    }
}
