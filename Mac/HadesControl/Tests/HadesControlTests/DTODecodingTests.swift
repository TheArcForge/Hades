import Foundation
import Testing

@testable import HadesControl

/// Every DTO decoded here comes from a fixture captured off a real, running Hades.Server (see
/// Fixtures.swift's own doc comment) - proof these types match actual response shapes, not just
/// the shapes this package assumes.
@Suite("DTO decoding")
struct DTODecodingTests {
    @Test("PingResult decodes GET /control/ping")
    func ping() throws {
        let result = try Fixtures.decode(PingResult.self, "ping")

        #expect(result.version == "2.0.0-dev")
        #expect(result.uptimeSeconds > 0)
    }

    @Test("SummaryResult decodes with the optional lease field genuinely absent")
    func summaryIdleNoLease() throws {
        let result = try Fixtures.decode(SummaryResult.self, "summary_idle_no_lease")

        #expect(result.iconState == .idle)
        #expect(result.headline == "No Unity Editor attached")
        #expect(result.lease == nil)
        #expect(result.rows.count == 1)
        #expect(result.rows[0].project == "Hades-Unity-Client")
        // Stable per-project identity, present even with no lease in play - a view must key rows
        // on this, never on `project` (see SummaryRow's own doc comment).
        #expect(result.rows[0].productGuid == "15c012f27331e49229cef25e74537816")
        #expect(result.rows[0].status.contains("indexed"))
        #expect(result.rows[0].severity == .ok)
    }

    @Test("SummaryResult decodes every SummaryLease field when a lease is present")
    func summaryLeaseHeld() throws {
        let result = try Fixtures.decode(SummaryResult.self, "summary_lease_held")

        #expect(result.iconState == .leaseHeld)
        let lease = try #require(result.lease)
        #expect(lease.project == "Hades-Unity-Client")
        #expect(lease.leaseId == "15c012f27331e49229cef25e74537816")
        #expect(lease.heldForSeconds == 42)
        #expect(lease.expiresInSeconds == 18)
        #expect(lease.releasable == true)
        // The row's own productGuid matches the lease's leaseId here - both resolve to the SAME
        // holding project's real identity (see Hades.Server.Control.SummaryLease.LeaseId's own doc
        // comment on the .NET side for why leaseId is a productGuid in the first place).
        #expect(result.rows[0].productGuid == lease.leaseId)
    }

    @Test("an unrecognised iconState or severity decodes to .unknown rather than throwing")
    func unknownEnumValuesFallBackInsteadOfThrowing() throws {
        // Mutated from a real capture (summary_idle_no_lease): iconState and severity are the
        // only two edits, so this proves the fallback without guessing at any other field's
        // shape - see the Plan 12 Task 1 report for why a real fixture can't reach this branch
        // (it requires a core newer than this client, which does not exist yet).
        let result = try Fixtures.decode(SummaryResult.self, "summary_unknown_enum_values")

        #expect(result.iconState == .unknown)
        #expect(result.rows[0].severity == .unknown)
        // productGuid is an ordinary String, not a ControlEnum - unaffected by the enum-fallback
        // path this fixture exercises, and still required to decode.
        #expect(result.rows[0].productGuid == "15c012f27331e49229cef25e74537816")
    }

    @Test("ProjectsResult: absent unityVersion, an empty warnings array, and a populated one")
    func projectsWithWarning() throws {
        let result = try Fixtures.decode(ProjectsResult.self, "projects_with_warning")

        #expect(result.projects.count == 2)

        let missing = try #require(result.projects.first { $0.name == "fake-project-p12t1" })
        #expect(missing.unityVersion == nil)
        #expect(missing.indexState == .indexed)
        #expect(missing.editor.state == .absent)
        #expect(missing.editor.unityVersion == nil)
        #expect(missing.editor.processId == nil)
        #expect(missing.editor.connectionAgeSeconds == nil)
        #expect(missing.warnings.count == 1)
        #expect(missing.warnings[0].code == "pathMissing")
        #expect(missing.warnings[0].severity == .error)
        #expect(!missing.warnings[0].message.isEmpty)
        #expect(!missing.warnings[0].remedy.isEmpty)

        let healthy = try #require(result.projects.first { $0.name == "Hades-Unity-Client" })
        #expect(healthy.unityVersion == "6000.3.2f1")
        #expect(healthy.nodeCount == 494)
        #expect(healthy.edgeCount == 332)
        #expect(healthy.warnings.isEmpty)
    }

    @Test("ProjectRow.editor's optional fields decode when an Editor IS attached, not just when absent")
    func projectsEditorAttachedFieldsPresent() throws {
        // See this file's own note on summaryLeaseHeld for how this fixture was produced: no
        // fixture already captured has a live attached Editor, so ProjectEditorInfo's
        // unityVersion/processId/connectionAgeSeconds were only ever proven absent, never
        // present, until this one.
        let result = try Fixtures.decode(ProjectsResult.self, "projects_editor_attached")

        let project = try #require(result.projects.first)
        #expect(project.editor.state == .attached)
        #expect(project.editor.status == "Editor attached")
        #expect(project.editor.unityVersion == "6000.3.2f1")
        #expect(project.editor.processId == 54321)
        #expect(project.editor.connectionAgeSeconds == 180)
    }

    @Test("EditorsResult decodes an empty editors array when nothing is attached")
    func editorsEmpty() throws {
        let result = try Fixtures.decode(EditorsResult.self, "editors_empty")

        #expect(result.editors.isEmpty)
    }

    @Test("EditorsResult decodes attached vs busy rows with every optional field present")
    func editorsAttachedAndBusy() throws {
        let result = try Fixtures.decode(EditorsResult.self, "editors_attached")

        #expect(result.editors.count == 2)

        let attached = try #require(result.editors.first { $0.state == .attached })
        #expect(attached.project == "Hades-Unity-Client")
        #expect(attached.status == "Editor attached")
        #expect(attached.unityVersion == "6000.3.2f1")
        #expect(attached.processId == 54321)
        #expect(attached.connectionAgeSeconds == 180)

        let busy = try #require(result.editors.first { $0.state == .busy })
        #expect(busy.status == "Editor attached (busy)")
        #expect(busy.connectionAgeSeconds == 8)
    }

    @Test("ActionResult decodes an idempotent release-with-nothing-held success")
    func releaseIdempotentSuccess() throws {
        let result = try Fixtures.decode(ActionResult.self, "release_idempotent_success")

        #expect(result.success == true)
        #expect(result.message.contains("nothing to release"))
    }

    @Test("the server's {\"error\": ...} envelope decodes for a 404 (unknown project)")
    func releaseUnknownProject404() throws {
        struct ErrorBody: Decodable { let error: String }
        let body = try Fixtures.decode(ErrorBody.self, "release_unknown_project_404")

        #expect(body.error.contains("Unknown project"))
    }

    // MARK: - Plan 13 Task 1: Projects actions

    @Test("ProjectRow decodes POST /control/projects/add's response - the same row shape GET /control/projects uses, including a populated pluginVersionMismatch warning")
    func projectsAdd() throws {
        let result = try Fixtures.decode(ProjectRow.self, "projects_add")

        #expect(result.name == "Hades-Unity-Client")
        #expect(result.productGuid == "15c012f27331e49229cef25e74537816")
        #expect(result.unityVersion == "6000.3.2f1")
        #expect(result.indexState == .indexed)
        #expect(result.nodeCount == 494)
        #expect(result.edgeCount == 332)
        // Hades.Server.Control.ProjectsEndpoint's own pluginVersionMismatch warning (major-skew
        // wording - installed v1.2.0 vs this app's v2.0.0-dev) - exercises the decode path for a
        // warning code every OTHER projects fixture leaves this array empty for.
        #expect(result.warnings.count == 1)
        #expect(result.warnings[0].code == "pluginVersionMismatch")
        #expect(result.warnings[0].severity == .warning)
        #expect(result.warnings[0].message == "The installed Hades plugin (v1.2.0) is a different major version from this app (v2.0.0-dev) — compatibility is not assured, and most Editor-dependent tools should be expected to fail until it is updated.")
        #expect(result.warnings[0].remedy == "Use Install/Update Plugin for this project, then restart Unity if it is already running.")
    }

    @Test("ActionResult decodes real remove/revealInFinder/openInUnity responses, success and failure alike")
    func projectsActionResults() throws {
        let removed = try Fixtures.decode(ActionResult.self, "projects_action_remove")
        #expect(removed.success == true)
        #expect(removed.message.contains("removed from Hades"))
        #expect(removed.message.contains("Nothing was deleted from disk"))

        let revealed = try Fixtures.decode(ActionResult.self, "projects_action_reveal_in_finder")
        #expect(revealed.success == true)
        #expect(revealed.message == "Revealed Hades-Unity-Client in Finder.")

        // Captured against a decoy project whose Unity version is guaranteed not installed, so this
        // is a genuine `success: false` response, not a hand-typed one - see the Task 1 report for
        // why openInUnity was deliberately never called against a version that IS installed.
        let openFailed = try Fixtures.decode(ActionResult.self, "projects_action_open_in_unity_not_found")
        #expect(openFailed.success == false)
        #expect(openFailed.message.contains("was not found at the default Unity Hub install location"))
    }

    @Test("InstallPluginResult decodes a real installPlugin response")
    func projectsInstallPlugin() throws {
        let result = try Fixtures.decode(InstallPluginResult.self, "projects_action_install_plugin")

        #expect(result.success == true)
        #expect(result.needsRestart == false)
        #expect(result.message == "Plugin installed. It will load automatically the next time this project opens in Unity.")
    }

    @Test("RebuildStartedResult decodes a real rebuild response with a guid-shaped operationId")
    func projectsRebuildStarted() throws {
        let result = try Fixtures.decode(RebuildStartedResult.self, "projects_action_rebuild_started")

        #expect(UUID(uuidString: result.operationId) != nil)
    }

    // MARK: - Plan 13 Task 1: Operations

    @Test("OperationResult decodes a running operation with progress/error/result genuinely absent")
    func operationRunning() throws {
        let result = try Fixtures.decode(OperationResult.self, "operation_running")

        #expect(result.kind == "rebuild")
        #expect(result.state == .running)
        #expect(result.finishedAtUtc == nil)
        #expect(result.progress == nil)
        #expect(result.error == nil)
        #expect(result.result == nil)
        // Carried verbatim as a String, never parsed into a Date - see OperationResult's own doc
        // comment for why (elapsedSeconds is the resolved fact a view actually needs).
        #expect(result.startedAtUtc == "2026-08-05T08:39:05.172842+00:00")
    }

    @Test("OperationResult decodes a done operation: result present as a ControlJSONValue object, error absent")
    func operationDone() throws {
        let result = try Fixtures.decode(OperationResult.self, "operation_done")

        #expect(result.state == .done)
        #expect(result.error == nil)
        #expect(result.progress == nil)
        #expect(result.startedAtUtc == "2026-08-05T08:39:05.172842+00:00")
        #expect(result.finishedAtUtc == "2026-08-05T08:39:05.516226+00:00")

        let rebuildResult = try #require(result.result)
        #expect(rebuildResult["nodesBefore"]?.intValue == 494)
        #expect(rebuildResult["nodesAfter"]?.intValue == 494)
        #expect(rebuildResult["message"]?.stringValue?.contains("494 nodes") == true)
    }

    @Test("an unrecognised operation state decodes to .unknown rather than throwing")
    func operationUnknownState() throws {
        // Mutated from a real capture (operation_running): `state` is the only edit - same
        // technique, and the same reason a live capture cannot reach this branch (it needs a core
        // newer than any that exists), as summary_unknown_enum_values.json (Plan 12 Task 1).
        let result = try Fixtures.decode(OperationResult.self, "operation_unknown_state")

        #expect(result.state == .unknown)
    }

    // MARK: - Plan 13 Task 1: Settings

    @Test("SettingsResult decodes a real mcpPort conflict: port is where THIS instance actually runs (never the documented 7823 - inUse can only be true when they differ), message names both ports with no hedge, includes the actionable remedy verbatim; launchAtLogin/resourceGuards are gone, not just empty")
    func settingsMcpPortInUse() throws {
        let result = try Fixtures.decode(SettingsResult.self, "settings_mcp_port_in_use")

        // Plan 13 Task 8's own fix (see Hades.Server.Tests.Control.SettingsResolveTests.
        // McpPortInUse_MessageNamesTheActualPortAndTheDocumentedPortConflict...NoHedge...): `inUse`
        // is only ever true when `port` (where this instance actually runs) differs from the
        // documented 7823 - a fixture pinning `port: 7823` alongside `inUse: true` would describe a
        // state the server can never actually produce.
        #expect(result.mcpPort.port == 9999)
        #expect(result.mcpPort.inUse == true)
        #expect(result.mcpPort.message == "Hades is running on port 9999 — the documented MCP port 7823 is already in use by another process. Find and stop whatever is using port 7823 (`lsof -nP -iTCP:7823 -sTCP:LISTEN`), or set ASPNETCORE_URLS to run Hades at a different address.")
        // No longer hedges "either this Hades instance itself, or another process" - once inUse
        // can be true at all, it is proven to be someone else (see the fix's own doc comment).
        #expect(!result.mcpPort.message.contains("either this Hades instance itself"))
        // Plan 13 Task 7: the core's own McpBinding.RemedyForPortInUse, not a paraphrase - the
        // `lsof` command is the proof this is the SAME actionable text a hard startup failure gives.
        #expect(result.mcpPort.message.contains("lsof -nP -iTCP:7823 -sTCP:LISTEN"))
        #expect(result.logLevel.level == "Information")
        // launchAtLogin/resourceGuards no longer exist as SettingsResult properties at all - this is
        // a COMPILE-TIME proof of omission, the strongest form the "endpoint stops reporting it"
        // requirement can take, backed by the live fixture itself carrying neither key (see
        // GetSettings_NeverReturnsLaunchAtLoginOrResourceGuards_NeitherCanBeSubstantiatedByThisProcess
        // on the .NET side for the wire-level proof).
    }

    // MARK: - Plan 13 Task 1: Traces

    @Test("TraceSequencesResult groups real MCP tool calls into one sequence")
    func tracesSequences() throws {
        let result = try Fixtures.decode(TraceSequencesResult.self, "traces_sequences")

        #expect(result.truncated == false)
        let sequence = try #require(result.sequences.first)
        #expect(sequence.callCount == 7)
        #expect(sequence.tools == [
            "hades_status", "search_by_name", "find_references_to", "search_by_name",
            "propose_memory_update", "propose_memory_update", "propose_memory_update",
        ])
        #expect(!sequence.pattern.isEmpty)
        #expect(sequence.outcome == .error)
        #expect(sequence.traceIds.count == 7)
    }

    @Test("SlowToolsResult decodes real per-tool timing stats")
    func tracesSlow() throws {
        let result = try Fixtures.decode(SlowToolsResult.self, "traces_slow")

        let proposeStats = try #require(result.tools.first { $0.tool == "propose_memory_update" })
        #expect(proposeStats.callCount == 3)
        #expect(proposeStats.maxDurationMs == 16)
    }

    @Test("FailedCallsResult decodes a real failure with its exact triggering message")
    func tracesFailures() throws {
        let result = try Fixtures.decode(FailedCallsResult.self, "traces_failures")

        let failure = try #require(result.failures.first)
        #expect(failure.tool == "search_by_name")
        #expect(failure.error?.contains("needs a non-empty 'namePattern'") == true)
    }

    @Test("TraceDetailResult decodes a successful call's pre-rendered attributes as flat key/valueDisplay rows, with events genuinely absent")
    func traceDetailSuccess() throws {
        let result = try Fixtures.decode(TraceDetailResult.self, "trace_detail_success")

        #expect(result.outcome == .ok)
        let span = try #require(result.spans.first)
        #expect(span.status == "ok")
        #expect(span.parentSpanId == nil)
        #expect(span.events == nil)

        let attributes = try #require(span.attributes)
        #expect(attributes.contains(SpanAttributeRow(key: "arguments.namePattern", valueDisplay: "Hades")))
        #expect(attributes.contains(SpanAttributeRow(key: "resultType", valueDisplay: "CallToolResult")))
        // The gap this closes: resultSizeBytes is an Int on the wire - Task 5's own
        // ControlJSONValue.stringLeaves() could never have surfaced this at all.
        #expect(attributes.contains(SpanAttributeRow(key: "resultSizeBytes", valueDisplay: "236")))
    }

    @Test("TraceDetailResult decodes a failed call's events as flat key/valueDisplay rows, including the previously-invisible timeUtcMs")
    func traceDetailFailure() throws {
        let result = try Fixtures.decode(TraceDetailResult.self, "trace_detail_failure")

        #expect(result.outcome == .error)
        let span = try #require(result.spans.first)
        #expect(span.status == "error")

        let attributes = try #require(span.attributes)
        #expect(attributes.contains(SpanAttributeRow(key: "arguments.namePattern", valueDisplay: "")))

        let events = try #require(span.events)
        #expect(events.contains(SpanAttributeRow(key: "[0].name", valueDisplay: "exception")))
        #expect(events.first { $0.key == "[0].message" }?.valueDisplay.contains("needs a non-empty") == true)
        // The gap this closes: timeUtcMs is an Int on the wire, silently dropped by the retired
        // stringLeaves() - now a plain row like every other leaf.
        #expect(events.contains(where: { $0.key == "[0].timeUtcMs" }))
    }

    @Test("an unrecognised trace outcome decodes to .unknown rather than throwing")
    func traceDetailUnknownOutcome() throws {
        // Mutated from a real capture (trace_detail_success): `outcome` is the only edit - same
        // technique as summary_unknown_enum_values.json (Plan 12 Task 1).
        let result = try Fixtures.decode(TraceDetailResult.self, "trace_detail_unknown_outcome")

        #expect(result.outcome == .unknown)
    }

    // MARK: - Plan 13 Task 1: Memory

    @Test("MemoryResult decodes a document's lastReviewed both present and genuinely absent (no frontmatter key)")
    func memoryDocumentsLastReviewed() throws {
        let result = try Fixtures.decode(MemoryResult.self, "memory_populated")

        let withDate = try #require(result.documents.first { $0.name == "conventions.md" })
        #expect(withDate.lastReviewed == "2026-05-12")

        let withoutDate = try #require(result.documents.first { $0.name == "p13t1-no-frontmatter.md" })
        #expect(withoutDate.lastReviewed == nil)
    }

    @Test("MemoryResult decodes status as a plain string beyond pending/accepted/deferred - a real 'inferred' proposal - with createdAtUtc/createdAgo genuinely absent together")
    func memoryInferredProposalHasNoCreatedDate() throws {
        let result = try Fixtures.decode(MemoryResult.self, "memory_populated")

        let inferred = try #require(result.proposals.first { $0.status == "inferred" })
        #expect(inferred.createdAtUtc == nil)
        #expect(inferred.createdAgo == nil)
        #expect(inferred.targetFile == "")
        #expect(inferred.rationale == "")
    }

    @Test("MemoryResult decodes createdAtUtc when present, and reflects real accept/dismiss/defer outcomes")
    func memoryProposalsWithCreatedDate() throws {
        let result = try Fixtures.decode(MemoryResult.self, "memory_populated")

        let accepted = try #require(result.proposals.first { $0.fileName == "20260805-084055-p13t1-fixture-conventions.md" })
        #expect(accepted.status == "accepted")
        #expect(accepted.createdAgo != nil)
        // Carried verbatim as a String, never parsed into a Date - see MemoryProposalRow's own doc
        // comment for why (createdAgo is the resolved fact a review-queue view actually needs).
        #expect(accepted.createdAtUtc == "2026-08-05T08:40:55.468879+00:00")

        let deferred = try #require(result.proposals.first { $0.fileName == "20260805-084055-p13t1-fixture-conventions-3.md" })
        #expect(deferred.status == "deferred")

        // The dismissed proposal (fixture-conventions-2) is deleted, not merely status-flagged - it
        // must not appear in the list at all.
        #expect(!result.proposals.contains { $0.fileName.contains("fixture-conventions-2") })

        // A pre-existing "pending" proposal, imported from the real project's own authored memory
        // (not created by this test run) - proves createdAtUtc decodes for a proposal beyond the
        // ones this run added, at a DIFFERENT fractional-second digit count (5, not 6) than the
        // instant checked above, which the field's plain String type carries either way unchanged.
        let preExisting = try #require(result.proposals.first { $0.fileName == "convention-render_pipeline.md" })
        #expect(preExisting.createdAtUtc == "2026-07-09T09:19:21.17006+00:00")
    }

    @Test("MemoryDocumentResult decodes a document's raw content byte for byte, frontmatter included")
    func memoryDocument() throws {
        let result = try Fixtures.decode(MemoryDocumentResult.self, "memory_document")

        #expect(result.name == "p13t1-fixture-conventions.md")
        #expect(result.content.hasPrefix("---\nlast_reviewed: 2026-08-05\n---\n"))
        #expect(result.content.contains("Written during Plan 13 Task 1 live fixture capture."))
    }

    @Test("ActionResult decodes real responses from every memory write/proposal action")
    func memoryActionResults() throws {
        let wrote = try Fixtures.decode(ActionResult.self, "memory_action_write_document")
        #expect(wrote.success == true)
        #expect(wrote.message == "Saved p13t1-fixture-conventions.md.")

        let accepted = try Fixtures.decode(ActionResult.self, "memory_action_accept_proposal")
        #expect(accepted.success == true)
        #expect(accepted.message.contains("merged into p13t1-fixture-conventions.md"))

        let dismissed = try Fixtures.decode(ActionResult.self, "memory_action_dismiss_proposal")
        #expect(dismissed.success == true)
        #expect(dismissed.message == "Proposal dismissed.")

        let deferred = try Fixtures.decode(ActionResult.self, "memory_action_defer_proposal")
        #expect(deferred.success == true)
        #expect(deferred.message == "Proposal deferred.")
    }
}

/// Plan 14 Task 10: `/control/migration/*` - the missing caller. Every fixture here (except the
/// three noted below) was captured from a real, running Hades.Server against a synthetic scratch
/// v1.2 project - never the developer's own machine or real Claude Desktop config, matching this
/// task's own explicit constraint.
///
/// `migration_clean_claude_desktop_config_removed.json` and
/// `migration_clean_claude_desktop_config_no_go_ahead.json` are hand-authored exceptions: both
/// mirror `Hades.Server.Control.MigrationClaudeDesktopConfigCleanupResult`'s wire shape and the
/// exact literal strings `V12Cleanup.CleanClaudeDesktopConfig` produces (already proven for real,
/// byte-for-byte, by `Hades.Core.Tests.Migration.V12CleanupTests` and
/// `Hades.Server.Tests.Control.MigrationEndpointHttpTests` against scratch files) rather than
/// live-captured, because a live capture would require pointing a running core's
/// `claudeDesktopConfigPath` at either a scratch file (in which case it is not meaningfully more
/// "real" than hand-authoring the same JSON) or - the one thing this whole task explicitly forbids
/// - the developer's own real `claude_desktop_config.json`.
///
/// `migration_clean_claude_md_no_go_ahead_with_remaining_content.json` (added for the per-item
/// cleanup UI task) is hand-authored for a narrower reason: it is the exact literal string
/// `V12Cleanup.CleanClaudeMd`'s dry-run branch now produces for the hybrid shape, already proven
/// byte-for-byte by `V12CleanupTests` and `MigrationEndpointHttpTests` against scratch files, so a
/// live capture would exercise nothing this suite does not already cover elsewhere.
///
/// `migration_clean_hades_hub_not_found.json`, `migration_clean_hades_hub_no_go_ahead.json`, and
/// `migration_clean_hades_hub_removed.json` (the fifth `V12Cleanup` target - closing the spec #4 §1
/// gap where `~/.arcforge/hades-hub/launcher.js` was named among what v2 retires but no cleanup
/// method ever removed it) are hand-authored exceptions for the identical reason as the
/// claude_desktop_config.json ones above: each mirrors `Hades.Server.Control.MigrationHadesHubCleanupResult`'s
/// wire shape and the exact literal strings `V12Cleanup.CleanHadesHub` produces (already proven for
/// real, byte-for-byte, by `Hades.Core.Tests.Migration.V12CleanupTests` and
/// `Hades.Server.Tests.Control.MigrationEndpointHttpTests` against scratch directories) rather than
/// live-captured - a live capture would require pointing a running core's `hadesHubDirectory` at
/// either a scratch directory (not meaningfully more "real" than hand-authoring the same JSON) or -
/// the one thing this whole task explicitly forbids - the developer's own real
/// `~/.arcforge/hades-hub/`.
@Suite("Migration DTO decoding")
struct MigrationDTODecodingTests {
    @Test("MigrationDetectionResult decodes a full v1.2 project - every item present")
    func detectV12() throws {
        let result = try Fixtures.decode(MigrationDetectionResult.self, "migration_detect_v12")

        #expect(result.isV12Project == true)
        #expect(result.manifestEntry.present == true)
        #expect(result.manifestEntry.value == "file:/Users/mike/Projects/Hades")
        #expect(result.manifestEntry.resolvedPath == "/Users/mike/Projects/Hades")
        #expect(result.hasMemory == true)
        #expect(result.memoryDocumentCount == 3)
        #expect(result.hasTraces == true)
        #expect(result.hasGraph == false)
        #expect(result.hasGeneratedMcpConfig == true)
        #expect(result.claudeMd.shape == .marked)
        #expect(result.hasUnityPlugin == false)
    }

    @Test("MigrationDetectionResult decodes a non-v1.2 project - everything absent, honestly, not an error")
    func detectNonV12() throws {
        let result = try Fixtures.decode(MigrationDetectionResult.self, "migration_detect_non_v12")

        #expect(result.isV12Project == false)
        #expect(result.manifestEntry.present == false)
        #expect(result.manifestEntry.value == nil)
        #expect(result.manifestEntry.resolvedPath == nil)
        #expect(result.hasMemory == false)
        #expect(result.memoryDocumentCount == 0)
        #expect(result.claudeMd.shape == .absent)
    }

    @Test("MigrationMemoryImportResult decodes a real collision report - never overwrites")
    func importMemorySkipped() throws {
        // Captured against a project already adopted once before this call (ProjectService.Adopt
        // auto-imports memory on first sight - see that method's own doc comment), so every entry
        // is a genuine, real "already exists" skip, not a hand-typed approximation.
        let result = try Fixtures.decode(MigrationMemoryImportResult.self, "migration_import_memory")

        #expect(result.imported.isEmpty)
        #expect(result.skipped.count == 3)
        let conventions = try #require(result.skipped.first { $0.source == "conventions.md" })
        #expect(conventions.reason.contains("already exists"))
        #expect(result.skipped.contains { $0.source == "proposals/idea.md" })
    }

    @Test("MigrationTracesImportResult decodes a real successful import")
    func importTraces() throws {
        let result = try Fixtures.decode(MigrationTracesImportResult.self, "migration_import_traces")

        #expect(result.imported == true)
        #expect(result.skippedReason == nil)
    }

    @Test("MigrationClaudeMdCleanupResult: no go-ahead leaves removed false, remainingContentOutsideBlock false")
    func cleanClaudeMdNoGoAhead() throws {
        let result = try Fixtures.decode(MigrationClaudeMdCleanupResult.self, "migration_clean_claude_md_no_go_ahead")

        #expect(result.removed == false)
        #expect(result.message.contains("no go-ahead"))
        #expect(result.remainingContentOutsideBlock == false)
    }

    @Test("MigrationClaudeMdCleanupResult: the real hybrid shape - removed true AND remainingContentOutsideBlock true")
    func cleanClaudeMdRemovedWithRemainingContent() throws {
        // "cleanup succeeded" and "the file is now clean" are different claims - this is the field
        // that keeps the shell from conflating them. See this task's own brief.
        let result = try Fixtures.decode(MigrationClaudeMdCleanupResult.self, "migration_clean_claude_md_removed_with_remaining_content")

        #expect(result.removed == true)
        #expect(result.remainingContentOutsideBlock == true)
    }

    @Test("MigrationClaudeMdCleanupResult: the hybrid shape BEFORE agreeing - remainingContentOutsideBlock is true even on a proceed:false dry run")
    func cleanClaudeMdNoGoAheadWithRemainingContent() throws {
        // The per-item cleanup UI task's own gap fix: a caller building a confirmation prompt must
        // learn "other content will survive" BEFORE the user agrees, not only after acting. Same
        // fact as cleanClaudeMdRemovedWithRemainingContent above, one step earlier.
        let result = try Fixtures.decode(MigrationClaudeMdCleanupResult.self, "migration_clean_claude_md_no_go_ahead_with_remaining_content")

        #expect(result.removed == false)
        #expect(result.remainingContentOutsideBlock == true)
        #expect(result.message.localizedCaseInsensitiveContains("outside"))
    }

    @Test("MigrationManifestCleanupResult decodes occurrencesFound and the port-conflict warning even before removal")
    func cleanManifestNoGoAhead() throws {
        let result = try Fixtures.decode(MigrationManifestCleanupResult.self, "migration_clean_manifest_no_go_ahead")

        #expect(result.removed == false)
        #expect(result.occurrencesFound == 1)
        #expect(result.portConflictWarning.localizedCaseInsensitiveContains("port"))
    }

    @Test("MigrationManifestCleanupResult decodes a real removal")
    func cleanManifestRemoved() throws {
        let result = try Fixtures.decode(MigrationManifestCleanupResult.self, "migration_clean_manifest_removed")

        #expect(result.removed == true)
        #expect(result.occurrencesFound == 1)
    }

    @Test("MigrationMcpConfigCleanupResult decodes both outcomes")
    func cleanMcpConfig() throws {
        let notRemoved = try Fixtures.decode(MigrationMcpConfigCleanupResult.self, "migration_clean_mcp_config_no_go_ahead")
        #expect(notRemoved.removed == false)

        let removed = try Fixtures.decode(MigrationMcpConfigCleanupResult.self, "migration_clean_mcp_config_removed")
        #expect(removed.removed == true)
        #expect(removed.message == "Removed the generated .mcp.json.")
    }

    @Test("MigrationClaudeDesktopConfigCleanupResult always carries a global scope warning")
    func cleanClaudeDesktopConfig() throws {
        let result = try Fixtures.decode(MigrationClaudeDesktopConfigCleanupResult.self, "migration_clean_claude_desktop_config_removed")

        #expect(result.removed == true)
        #expect(result.scopeWarning.localizedCaseInsensitiveContains("global"))
        #expect(result.scopeWarning.contains("claude_desktop_config.json"))
        #expect(result.occurrencesFound == 1)
    }

    @Test("MigrationClaudeDesktopConfigCleanupResult: occurrencesFound is populated on a proceed:false dry run too - the only presence signal this global-scope route has, with no per-project detect endpoint behind it")
    func cleanClaudeDesktopConfigNoGoAhead() throws {
        let result = try Fixtures.decode(MigrationClaudeDesktopConfigCleanupResult.self, "migration_clean_claude_desktop_config_no_go_ahead")

        #expect(result.removed == false)
        #expect(result.occurrencesFound == 1)
        #expect(result.scopeWarning.localizedCaseInsensitiveContains("global"))
    }

    // MARK: - The fifth target: ~/.arcforge/hades-hub/ (spec #4 §1's launcher.js retirement gap)

    @Test("MigrationHadesHubCleanupResult: absent directory - removed false, found false, no go-ahead needed")
    func cleanHadesHubNotFound() throws {
        let result = try Fixtures.decode(MigrationHadesHubCleanupResult.self, "migration_clean_hades_hub_not_found")

        #expect(result.removed == false)
        #expect(result.found == false)
        #expect(result.message.contains("nothing to remove"))
    }

    @Test("MigrationHadesHubCleanupResult: found is populated on a proceed:false dry run too - the only presence signal this global-scope route has, with no per-project detect endpoint behind it")
    func cleanHadesHubNoGoAhead() throws {
        let result = try Fixtures.decode(MigrationHadesHubCleanupResult.self, "migration_clean_hades_hub_no_go_ahead")

        #expect(result.removed == false)
        #expect(result.found == true)
        #expect(result.message.localizedCaseInsensitiveContains("no go-ahead"))
    }

    @Test("MigrationHadesHubCleanupResult decodes a real removal, naming the directory")
    func cleanHadesHubRemoved() throws {
        let result = try Fixtures.decode(MigrationHadesHubCleanupResult.self, "migration_clean_hades_hub_removed")

        #expect(result.removed == true)
        #expect(result.found == true)
        #expect(result.message.contains("hades-hub"))
        #expect(result.message.contains("Removed"))
    }
}
