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

    @Test("ProjectRow decodes POST /control/projects/add's response - the same row shape GET /control/projects uses")
    func projectsAdd() throws {
        let result = try Fixtures.decode(ProjectRow.self, "projects_add")

        #expect(result.name == "Hades-Unity-Client")
        #expect(result.productGuid == "15c012f27331e49229cef25e74537816")
        #expect(result.unityVersion == "6000.3.2f1")
        #expect(result.indexState == .indexed)
        #expect(result.nodeCount == 494)
        #expect(result.edgeCount == 332)
        #expect(result.warnings.isEmpty)
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

    @Test("SettingsResult decodes a real mcpPort conflict, message includes the actionable remedy verbatim; launchAtLogin/resourceGuards are gone, not just empty")
    func settingsMcpPortInUse() throws {
        let result = try Fixtures.decode(SettingsResult.self, "settings_mcp_port_in_use")

        #expect(result.mcpPort.port == 7823)
        #expect(result.mcpPort.inUse == true)
        #expect(result.mcpPort.message.contains("already in use"))
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
