import Foundation
import HadesControl
import Testing

@testable import HadesApp

/// `TracesViewModel` owns the Traces section's fetch and published state - the Task 5 analogue of
/// `ProjectsViewModel` (Task 3), same settled data-ownership split (`MainWindowViewModel` owns
/// navigation/polling LIFECYCLE only; each section owns its own fetch). Unlike Projects, Traces has
/// no single fetch: `refresh()` drives THREE independent endpoints every tick - `tracesSequences`,
/// `tracesFailures`, `tracesSlow` - because spec #3 §3.3 requires failures and slow calls to come
/// from their own endpoints, never filtered client-side out of the sequences list (see
/// `Hades.Server.Control.TracesEndpoint`'s own class doc comment). `selectTrace(traceId:)` is a
/// separate, user-initiated fourth fetch (`GET /control/traces/{traceId}`), never polled on a timer.
///
/// Fixture values below mirror the real captures in `HadesControlTests/Fixtures/traces_*.json` /
/// `trace_detail_*.json` (Task 1) - same "grounded in a real response shape, not invented" standard
/// `ProjectsViewModelTests` already holds to.
@Suite("TracesViewModel")
@MainActor
struct TracesViewModelTests {

    // MARK: - Fixtures (verbatim from traces_sequences.json / traces_failures.json / traces_slow.json)

    static let realSequence = TraceSequenceRow(
        id: "8e26d015f3f64ca887a8f397fce03799",
        tools: [
            "hades_status", "search_by_name", "find_references_to", "search_by_name",
            "propose_memory_update", "propose_memory_update", "propose_memory_update",
        ],
        pattern:
            "hades_status \u{2192} search_by_name \u{2192} find_references_to \u{2192} search_by_name \u{2192} propose_memory_update \u{2192} propose_memory_update \u{2192} propose_memory_update",
        callCount: 7,
        startUtcMs: 1_785_919_231_447,
        endUtcMs: 1_785_919_255_543,
        durationMs: 24_096,
        outcome: .error,
        traceIds: [
            "8e26d015f3f64ca887a8f397fce03799", "f2e55401519949998c03769e11dab297",
            "982b5658d8224e0fb784f95a01bfb886", "0186f14733ee4656ace4074295c5915b",
            "71e7bbf575ac48e3af1bf500f6f14712", "98e99d699524482fa1ba932abed84463",
            "37a2abc514b04fcab61fbcc90238699e",
        ]
    )

    static let realFailure = FailedCallRow(
        traceId: "0186f14733ee4656ace4074295c5915b",
        tool: "search_by_name",
        startUtcMs: 1_785_919_241_100,
        durationMs: 0,
        error:
            "search_by_name needs a non-empty 'namePattern' \u{2014} the substring to look for, e.g. {\"namePattern\": \"PlayerController\"}. Add it and call again."
    )

    static let realSlowTool = SlowToolRow(
        tool: "propose_memory_update", callCount: 3, averageDurationMs: 5.333_333_333_333_333, maxDurationMs: 16
    )

    static let realTraceDetail = TraceDetailResult(
        traceId: "f2e55401519949998c03769e11dab297",
        tool: "search_by_name",
        startUtcMs: 1_785_919_241_032,
        endUtcMs: 1_785_919_241_034,
        durationMs: 2,
        outcome: .ok,
        spans: [
            SpanRow(
                spanId: "e0dc4e7215914986b3a599a8daab5d15",
                parentSpanId: nil,
                name: "search_by_name",
                kind: "tool_call",
                startUtcMs: 1_785_919_241_032,
                endUtcMs: 1_785_919_241_034,
                durationMs: 2,
                status: "ok",
                attributes: [
                    SpanAttributeRow(key: "arguments.namePattern", valueDisplay: "Hades"),
                    SpanAttributeRow(key: "resultType", valueDisplay: "CallToolResult"),
                    SpanAttributeRow(key: "resultSizeBytes", valueDisplay: "2532"),
                ],
                events: nil
            )
        ]
    )

    /// Two known projects - the fixture shape the Plan 13 Task 8 re-run's whole finding turns on:
    /// every test above this point exercises a SINGLE project (`FakeTracesFetcher`'s own default
    /// `projectsScript` returns an empty list), which is precisely why the ambiguous-project defect
    /// was structurally invisible until a real hand-run with two registered projects hit it.
    static let projectAlpha = ProjectRow(
        name: "Alpha", path: "/Users/mike/Projects/Alpha", productGuid: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        unityVersion: "6000.3.2f1", indexState: .indexed, indexStatus: "indexed 1m ago", nodeCount: 10, edgeCount: 5,
        editor: ProjectEditorInfo(state: .absent, status: "No Editor attached", unityVersion: nil, processId: nil, connectionAgeSeconds: nil),
        warnings: []
    )
    static let projectBeta = ProjectRow(
        name: "Beta", path: "/Users/mike/Projects/Beta", productGuid: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        unityVersion: "6000.3.2f1", indexState: .indexed, indexStatus: "indexed 2m ago", nodeCount: 20, edgeCount: 8,
        editor: ProjectEditorInfo(state: .absent, status: "No Editor attached", unityVersion: nil, processId: nil, connectionAgeSeconds: nil),
        warnings: []
    )

    /// Verbatim shape of `Hades.Core.Projects.ProjectResolver.Resolve`'s own ambiguous-project
    /// message (read from that method directly - no live two-project fixture exists to capture this
    /// from, so this is "the exact string is right there in the method that emits it", the same
    /// standard `ProjectsViewModelTests.mixedSerializationWarning` already holds itself to for its
    /// own uncaptured warning), for the two projects above, in the order `Catalogue` joins them.
    static let ambiguousProjectMessage =
        "Hades knows 2 projects, so this call needs a 'project' argument. Known projects: Alpha (aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa); Beta (bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb). Call hades_status for details."

    // MARK: - Initial state

    @Test("starts with nothing before any refresh - no sequences, failures, slow tools, or selected trace")
    func startsEmpty() {
        let viewModel = TracesViewModel(discover: { nil }, makeClient: { _ in FakeTracesFetcher() })
        #expect(viewModel.sequences.isEmpty)
        #expect(viewModel.failures.isEmpty)
        #expect(viewModel.slowTools.isEmpty)
        #expect(viewModel.selectedTraceDetail == .notSelected)
        #expect(viewModel.knownProjects.isEmpty)
        #expect(viewModel.refreshError == nil)
    }

    // MARK: - refresh(): three independent fetches

    @Test("refresh() populates sequences verbatim, including truncated, from a successful fetch")
    func refreshPopulatesSequencesVerbatim() async {
        let fetcher = FakeTracesFetcher(
            sequencesScript: [.success(TraceSequencesResult(sequences: [Self.realSequence], truncated: true))]
        )
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.sequences == [Self.realSequence])
        #expect(viewModel.sequencesTruncated == true)
        #expect(await fetcher.sequencesCallCount == 1)
    }

    @Test("refresh() populates failures and slowTools verbatim, each from its own endpoint")
    func refreshPopulatesFailuresAndSlowToolsVerbatim() async {
        let fetcher = FakeTracesFetcher(
            failuresOutcome: .success(FailedCallsResult(failures: [Self.realFailure])),
            slowOutcome: .success(SlowToolsResult(tools: [Self.realSlowTool]))
        )
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.failures == [Self.realFailure])
        #expect(viewModel.slowTools == [Self.realSlowTool])
        #expect(await fetcher.failuresCallCount == 1, "failures must come from GET /control/traces/failures, not be derived from sequences")
        #expect(await fetcher.slowCallCount == 1, "slow tools must come from GET /control/traces/slow, not be derived from sequences")
    }

    @Test("an empty result on every endpoint is a legitimate, ordinary state - not an error - the same as tracing-on-but-no-calls-yet")
    func emptyResultsAreOrdinaryNotAnError() async {
        let fetcher = FakeTracesFetcher()  // defaults: empty sequences/failures/slow
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.sequences.isEmpty)
        #expect(viewModel.failures.isEmpty)
        #expect(viewModel.slowTools.isEmpty)
    }

    @Test("discover() returning nil leaves state exactly as it was, and never attempts a fetch")
    func discoveryUnavailableLeavesStateUnchanged() async {
        let fetcher = FakeTracesFetcher(
            sequencesScript: [.success(TraceSequencesResult(sequences: [Self.realSequence], truncated: false))]
        )
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")])  // exhausts after one call
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()  // succeeds, connection consumed
        #expect(viewModel.sequences == [Self.realSequence])

        await viewModel.refresh()  // discover() now returns nil
        #expect(viewModel.sequences == [Self.realSequence], "one unlucky discovery read must not clear existing data")
        #expect(await fetcher.sequencesCallCount == 1, "a fetch is never attempted without a connection")
    }

    @Test("a failed sequences refresh self-heals: existing sequences survive, next refresh repopulates")
    func failedRefreshSelfHeals() async {
        let fetcher = FakeTracesFetcher(
            sequencesScript: [
                .success(TraceSequencesResult(sequences: [Self.realSequence], truncated: false)),
                .failure(.staleToken),
            ]
        )
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()
        #expect(viewModel.sequences == [Self.realSequence])

        await viewModel.refresh()  // fails
        #expect(viewModel.sequences == [Self.realSequence], "a failed refresh must not clear a sequence list already on screen")
    }

    // MARK: - knownProjects: populates the project Picker (Plan 13 Task 8 re-run, requirement B)

    @Test("refresh() populates knownProjects verbatim from GET /control/projects")
    func refreshPopulatesKnownProjectsVerbatim() async {
        let fetcher = FakeTracesFetcher(projectsScript: [.success(ProjectsResult(projects: [Self.projectAlpha, Self.projectBeta]))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.knownProjects == [Self.projectAlpha, Self.projectBeta])
    }

    @Test("a failed knownProjects fetch self-heals: the previous list survives, exactly like every other fetch in this type")
    func knownProjectsFetchSelfHeals() async {
        let fetcher = FakeTracesFetcher(projectsScript: [
            .success(ProjectsResult(projects: [Self.projectAlpha, Self.projectBeta])),
            .failure(.staleToken),
        ])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })
        await viewModel.refresh()
        #expect(viewModel.knownProjects == [Self.projectAlpha, Self.projectBeta])

        await viewModel.refresh()

        #expect(viewModel.knownProjects == [Self.projectAlpha, Self.projectBeta], "one unlucky projects() poll must not clear the picker's own list")
    }

    // MARK: - Default project selection when ambiguous (requirement C)

    @Test("refresh() defaults projectFilter to the first known project when nothing has been explicitly chosen and more than one project exists")
    func refreshDefaultsToFirstKnownProjectWhenAmbiguous() async {
        let fetcher = FakeTracesFetcher(projectsScript: [.success(ProjectsResult(projects: [Self.projectAlpha, Self.projectBeta]))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.projectFilter == Self.projectAlpha.productGuid)
        #expect(
            await fetcher.lastProject == Self.projectAlpha.productGuid,
            "the resolved default must actually reach tracesSequences, not just sit unused in projectFilter")
        #expect(await fetcher.lastFailuresProject == Self.projectAlpha.productGuid)
        #expect(await fetcher.lastSlowProject == Self.projectAlpha.productGuid)
    }

    @Test("with exactly one known project, refresh() still resolves it with no interaction - today's single-project behaviour, unchanged")
    func singleKnownProjectStillWorksWithNoInteraction() async {
        let fetcher = FakeTracesFetcher(
            sequencesScript: [.success(TraceSequencesResult(sequences: [Self.realSequence], truncated: false))],
            projectsScript: [.success(ProjectsResult(projects: [Self.projectAlpha]))]
        )
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.sequences == [Self.realSequence])
        #expect(viewModel.projectFilter == Self.projectAlpha.productGuid)
    }

    @Test("selectProject(_:) sets projectFilter and immediately re-fetches with the new project, without waiting for the next tick")
    func selectProjectImmediatelyRefetches() async {
        let fetcher = FakeTracesFetcher(projectsScript: [.success(ProjectsResult(projects: [Self.projectAlpha, Self.projectBeta]))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })
        await viewModel.refresh()
        #expect(viewModel.projectFilter == Self.projectAlpha.productGuid, "defaulted to Alpha first")
        #expect(await fetcher.sequencesCallCount == 1)

        await viewModel.selectProject(Self.projectBeta.productGuid)

        #expect(viewModel.projectFilter == Self.projectBeta.productGuid)
        #expect(await fetcher.lastProject == Self.projectBeta.productGuid)
        #expect(await fetcher.sequencesCallCount == 2, "selecting a project re-fetches immediately, not just on the next tick")
    }

    @Test("an explicitly chosen project is never overridden by the first-known-project default on a later refresh")
    func explicitSelectionSurvivesLaterRefresh() async {
        let fetcher = FakeTracesFetcher(projectsScript: [.success(ProjectsResult(projects: [Self.projectAlpha, Self.projectBeta]))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })
        await viewModel.selectProject(Self.projectBeta.productGuid)

        await viewModel.refresh()  // the ~1Hz tick path

        #expect(viewModel.projectFilter == Self.projectBeta.productGuid, "the tick must not silently reset an explicit choice back to the default")
    }

    // MARK: - Surfacing a real server error the shell cannot act on silently (requirement A)

    @Test("refresh() surfaces a 'needs a project argument' server error verbatim via refreshError, instead of silently self-healing to an empty list")
    func refreshSurfacesAmbiguousProjectErrorVerbatim() async {
        let fetcher = FakeTracesFetcher(
            sequencesScript: [.failure(.server(status: 400, message: Self.ambiguousProjectMessage))],
            failuresOutcome: .failure(.server(status: 400, message: Self.ambiguousProjectMessage)),
            slowOutcome: .failure(.server(status: 400, message: Self.ambiguousProjectMessage))
            // projectsScript defaults to an empty list - the exact "two projects exist server-side,
            // but the picker has not (yet) resolved a default" gap this fix exists to close.
        )
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.refreshError == Self.ambiguousProjectMessage)
        #expect(viewModel.sequences.isEmpty, "still no invented data - only a NEW published error, never a fabricated sequence list")
    }

    @Test("a transient (.transport) refresh failure does NOT set refreshError - self-heal is narrowed to explained server errors, not broadened to every failure")
    func transientFailureDoesNotSetRefreshError() async {
        let fetcher = FakeTracesFetcher(sequencesScript: [.failure(.transport(URLError(.timedOut)))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()

        #expect(viewModel.refreshError == nil)
    }

    @Test("refreshError clears on the next fully successful refresh")
    func refreshErrorClearsOnNextSuccess() async {
        let fetcher = FakeTracesFetcher(
            sequencesScript: [
                .failure(.server(status: 400, message: Self.ambiguousProjectMessage)),
                .success(TraceSequencesResult(sequences: [Self.realSequence], truncated: false)),
            ],
            projectsScript: [
                .success(ProjectsResult(projects: [])),
                .success(ProjectsResult(projects: [Self.projectAlpha])),
            ]
        )
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()
        #expect(viewModel.refreshError == Self.ambiguousProjectMessage)

        await viewModel.refresh()
        #expect(viewModel.refreshError == nil)
        #expect(viewModel.sequences == [Self.realSequence])
    }

    @Test("a surfaced server error still must not clear sequences/failures/slowTools already on screen - narrowing self-heal for errors must not weaken the existing must-not-clear-good-data contract")
    func serverErrorDoesNotClearExistingData() async {
        let fetcher = FakeTracesFetcher(
            sequencesScript: [
                .success(TraceSequencesResult(sequences: [Self.realSequence], truncated: false)),
                .failure(.server(status: 400, message: Self.ambiguousProjectMessage)),
            ]
        )
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.refresh()
        #expect(viewModel.sequences == [Self.realSequence])

        await viewModel.refresh()
        #expect(viewModel.sequences == [Self.realSequence], "an explained server error still must not erase data already on screen")
        #expect(viewModel.refreshError == Self.ambiguousProjectMessage, "but it DOES now surface, unlike before this fix")
    }

    // MARK: - Filters: Swift-chosen query parameters, never rendered API data

    @Test("applyFilters(...) sets tool/outcome/duration state and passes it verbatim to tracesSequences; a project selected via selectProject(_:) stays cross-cutting to failures/slow across a later applyFilters call")
    func applyFiltersPassesStateToEveryFetch() async {
        let fetcher = FakeTracesFetcher()
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        // Project selection is Picker-driven and applies immediately (see selectProject(_:)'s own
        // doc comment) - independent of the free-text/duration filters below, which still batch
        // until "Apply Filters" is tapped.
        await viewModel.selectProject("Hades-Unity-Client")
        await viewModel.applyFilters(tool: "search_by_name", outcome: "error", minDurationMs: 10, maxDurationMs: 5000)

        #expect(await fetcher.lastProject == "Hades-Unity-Client", "a project selected earlier must survive a later applyFilters call, which no longer touches it")
        #expect(await fetcher.lastTool == "search_by_name")
        #expect(await fetcher.lastOutcome == "error")
        #expect(await fetcher.lastMinDurationMs == 10)
        #expect(await fetcher.lastMaxDurationMs == 5000)
        #expect(await fetcher.lastFailuresProject == "Hades-Unity-Client", "the project filter is cross-cutting - it applies to failures too")
        #expect(await fetcher.lastSlowProject == "Hades-Unity-Client", "the project filter is cross-cutting - it applies to slow tools too")
    }

    @Test("an empty tool filter is sent as nil, never an empty string; project stays nil too when nothing has ever been selected and no known project resolves a default")
    func emptyFilterTextIsSentAsNil() async {
        let fetcher = FakeTracesFetcher()
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.applyFilters(tool: "", outcome: nil, minDurationMs: nil, maxDurationMs: nil)

        #expect(await fetcher.lastProject == nil)
        #expect(await fetcher.lastTool == nil)
    }

    @Test("refresh() (the tick path) re-applies whatever filters were last set via applyFilters")
    func refreshReappliesLastFilters() async {
        let fetcher = FakeTracesFetcher()
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.applyFilters(tool: "propose_memory_update", outcome: "ok", minDurationMs: nil, maxDurationMs: nil)
        #expect(await fetcher.sequencesCallCount == 1)

        await viewModel.refresh()  // the ~1Hz tick path, not applyFilters

        #expect(await fetcher.sequencesCallCount == 2)
        #expect(await fetcher.lastTool == "propose_memory_update", "the tick must keep using the last-applied filter, not reset it")
        #expect(await fetcher.lastOutcome == "ok")
    }

    // MARK: - selectTrace(traceId:): span detail, independent of which list the id came from

    @Test("selectTrace(traceId:) populates selectedTraceDetail verbatim on success")
    func selectTracePopulatesDetailVerbatim() async {
        let fetcher = FakeTracesFetcher(detailOutcome: .success(Self.realTraceDetail))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.selectTrace(traceId: Self.realTraceDetail.traceId)

        #expect(viewModel.selectedTraceDetail == .loaded(Self.realTraceDetail))
        #expect(await fetcher.lastRequestedTraceId == Self.realTraceDetail.traceId)
    }

    @Test("selectTrace(traceId:) failure (e.g. unknown trace, 404) becomes .failed with the server's own message verbatim")
    func selectTraceServerFailureBecomesFailedState() async {
        let message = "Unknown trace 'bogus'."
        let fetcher = FakeTracesFetcher(detailOutcome: .failure(.server(status: 404, message: message)))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.selectTrace(traceId: "bogus")

        #expect(viewModel.selectedTraceDetail == .failed(message: message))
    }

    @Test("a transient selectTrace failure (.transport) self-heals: an already-loaded detail is left in place, not cleared")
    func selectTraceTransientFailureSelfHeals() async {
        let fetcher = FakeTracesFetcher(detailOutcome: .success(Self.realTraceDetail))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        await viewModel.selectTrace(traceId: Self.realTraceDetail.traceId)
        #expect(viewModel.selectedTraceDetail == .loaded(Self.realTraceDetail))

        await fetcher.setDetailOutcome(.failure(.transport(URLError(.timedOut))))
        await viewModel.selectTrace(traceId: Self.realTraceDetail.traceId)

        #expect(viewModel.selectedTraceDetail == .loaded(Self.realTraceDetail), "a transient failure must not clobber an already-loaded detail")
    }

    @Test("clearSelectedTrace() resets to .notSelected")
    func clearSelectedTraceResets() async {
        let fetcher = FakeTracesFetcher(detailOutcome: .success(Self.realTraceDetail))
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })
        await viewModel.selectTrace(traceId: Self.realTraceDetail.traceId)
        #expect(viewModel.selectedTraceDetail != .notSelected)

        viewModel.clearSelectedTrace()

        #expect(viewModel.selectedTraceDetail == .notSelected)
    }
}
