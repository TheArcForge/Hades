import Foundation
import HadesControl
import HadesSupervision
import Testing

@testable import HadesApp

/// `MenuBarViewModel` is the one piece of orchestration Plan 12 Task 3 asks to be TDD-tested (see
/// the plan's STANDING RULES: "view models and state mapping should be [testable]") - everything
/// here runs against `FakeCoreSupervisor`/`FakeConnectionProvider`/`FakeSummaryFetcher`
/// (Support/TestSupport.swift), never a real process or a real network call, so these tests are
/// fast and prove the ORCHESTRATION contract Plan 12 Task 3 spells out explicitly: a stale token
/// re-reads discovery instead of erroring, Release never surfaces an error, and polling starts and
/// stops on command rather than running a background timer of its own.
@Suite("MenuBarViewModel")
@MainActor
struct MenuBarViewModelTests {

    static let idleSummary = SummaryResult(
        iconState: .idle, headline: "No Unity Editor attached",
        rows: [SummaryRow(project: "Hades-Unity-Client", productGuid: "15c012f27331e49229cef25e74537816", status: "No Editor attached \u{00b7} indexed 32s ago", severity: .ok)],
        lease: nil
    )

    // MARK: - Bootstrap / basic mapping

    @Test("bootstrap() performs exactly one fetch and populates content from it")
    func bootstrapFetchesOnce() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let fetcher = FakeSummaryFetcher([.success(Self.idleSummary)])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")])
        let viewModel = MenuBarViewModel(
            supervisor: supervisor,
            discover: { await connections.provide() },
            makeClient: { _ in fetcher }
        )

        #expect(viewModel.content == .notRunning) // before any tick

        await viewModel.bootstrap()

        #expect(viewModel.content == .running(ownership: .spawned, summary: Self.idleSummary))
        #expect(await fetcher.summaryCallCount == 1)
    }

    @Test("every tick calls CoreSupervisor.refresh() first - the menu drives refresh(), not a timer inside the supervisor")
    func tickAlwaysCallsRefresh() async {
        let supervisor = FakeCoreSupervisor(state: .notStarted)
        let connections = FakeConnectionProvider([], repeatLast: true)
        let viewModel = MenuBarViewModel(
            supervisor: supervisor,
            discover: { await connections.provide() },
            makeClient: { _ in FakeSummaryFetcher([]) }
        )

        await viewModel.bootstrap()
        #expect(await supervisor.refreshCallCount == 1)

        await viewModel.bootstrap()
        #expect(await supervisor.refreshCallCount == 2)
    }

    @Test("when the supervisor is not running, content reflects that directly and no summary fetch is attempted")
    func notRunningNeverFetchesSummary() async {
        let supervisor = FakeCoreSupervisor(state: .failed(attempts: 5))
        let fetcher = FakeSummaryFetcher([])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = MenuBarViewModel(
            supervisor: supervisor,
            discover: { await connections.provide() },
            makeClient: { _ in fetcher }
        )

        await viewModel.bootstrap()

        #expect(viewModel.content == .failed(attempts: 5))
        #expect(await fetcher.summaryCallCount == 0)
    }

    @Test("a stale summary does not survive the supervisor leaving .running")
    func staleSummaryClearedOnceNotRunning() async {
        let supervisor = FakeCoreSupervisor(state: .running(.adopted))
        let fetcher = FakeSummaryFetcher([.success(Self.idleSummary)])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let viewModel = MenuBarViewModel(
            supervisor: supervisor,
            discover: { await connections.provide() },
            makeClient: { _ in fetcher }
        )

        await viewModel.bootstrap()
        #expect(viewModel.content == .running(ownership: .adopted, summary: Self.idleSummary))

        // refresh() finding the adopted core gone drops CoreSupervisor.state to .notStarted - see
        // CoreSupervisor.refresh()'s own doc comment.
        await supervisor.setStateAfterNextRefresh(.notStarted)
        await viewModel.bootstrap()

        #expect(viewModel.content == .notRunning)

        // And if a core is later adopted/spawned again, the OLD summary must not flash back before
        // a fresh fetch completes.
        await supervisor.setState(.running(.spawned))
        let neverFetches = FakeSummaryFetcher([])
        let viewModel2 = MenuBarViewModel(
            supervisor: supervisor,
            discover: { nil }, // discovery file unreadable this instant
            makeClient: { _ in neverFetches }
        )
        await viewModel2.bootstrap()
        #expect(viewModel2.content == .notRunning)
    }

    // MARK: - staleToken

    @Test("a .staleToken error self-heals on the next tick by re-reading discovery - never shown as an error state")
    func staleTokenSelfHeals() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let staleConnection = ControlConnection(port: 1, token: "stale-token")
        let freshConnection = ControlConnection(port: 2, token: "fresh-token")
        let staleFetcher = FakeSummaryFetcher([.failure(.staleToken)])
        let freshFetcher = FakeSummaryFetcher([.success(Self.idleSummary)])
        let connections = FakeConnectionProvider([staleConnection, freshConnection])

        let viewModel = MenuBarViewModel(
            supervisor: supervisor,
            discover: { await connections.provide() },
            makeClient: { connection in connection.port == 1 ? staleFetcher : freshFetcher }
        )

        await viewModel.bootstrap() // tick 1: throws .staleToken
        #expect(viewModel.content == .notRunning) // not an error state - just "nothing to show yet"

        await viewModel.bootstrap() // tick 2: discover() re-read fresh, new token works
        #expect(viewModel.content == .running(ownership: .spawned, summary: Self.idleSummary))
        #expect(await connections.callCount == 2) // discovery genuinely re-read, not cached
    }

    @Test("a .transport error (core briefly unreachable) also self-heals without becoming an error state")
    func transportErrorSelfHeals() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let connection = ControlConnection(port: 1, token: "t")
        let fetcher = FakeSummaryFetcher([.failure(.transport(URLError(.timedOut))), .success(Self.idleSummary)])
        let connections = FakeConnectionProvider([connection], repeatLast: true)
        let viewModel = MenuBarViewModel(
            supervisor: supervisor,
            discover: { await connections.provide() },
            makeClient: { _ in fetcher }
        )

        await viewModel.bootstrap()
        #expect(viewModel.content == .notRunning)

        await viewModel.bootstrap()
        #expect(viewModel.content == .running(ownership: .spawned, summary: Self.idleSummary))
    }

    // MARK: - Release

    @Test("release(leaseId:) posts the exact lease id and never surfaces an error, even when the API call fails")
    func releaseNeverSurfacesAnError() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let connection = ControlConnection(port: 1, token: "t")
        let fetcher = FakeSummaryFetcher(
            [.success(Self.idleSummary)],
            releaseOutcome: .failure(.server(status: 404, message: "Unknown project 'x'."))
        )
        let connections = FakeConnectionProvider([connection], repeatLast: true)
        let viewModel = MenuBarViewModel(
            supervisor: supervisor,
            discover: { await connections.provide() },
            makeClient: { _ in fetcher }
        )

        await viewModel.release(leaseId: "15c012f27331e49229cef25e74537816")

        #expect(await fetcher.releaseCallCount == 1)
        #expect(await fetcher.lastReleasedLeaseId == "15c012f27331e49229cef25e74537816")
        // No error surface exists on MenuBarViewModel at all - content is the ONLY published
        // state, and it is never an "error" case as a result of releasing. This IS the assertion:
        // release() swallowing the failure means content still resolves normally afterward.
        #expect(viewModel.content == .running(ownership: .spawned, summary: Self.idleSummary))
    }

    @Test("release(leaseId:) on the idempotent success path (TTL already fired) behaves identically - not a special case")
    func releaseIdempotentSuccessPath() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let connection = ControlConnection(port: 1, token: "t")
        let fetcher = FakeSummaryFetcher(
            [.success(Self.idleSummary)],
            releaseOutcome: .success(ActionResult(success: true, message: "No reload lease is held \u{2014} nothing to release."))
        )
        let connections = FakeConnectionProvider([connection], repeatLast: true)
        let viewModel = MenuBarViewModel(
            supervisor: supervisor,
            discover: { await connections.provide() },
            makeClient: { _ in fetcher }
        )

        await viewModel.release(leaseId: "already-released")

        #expect(await fetcher.releaseCallCount == 1)
        #expect(viewModel.content == .running(ownership: .spawned, summary: Self.idleSummary))
    }

    @Test("release(leaseId:) triggers an immediate refresh so the UI reflects the release without waiting for the next poll tick")
    func releaseTriggersImmediateTick() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let connection = ControlConnection(port: 1, token: "t")
        let fetcher = FakeSummaryFetcher([.success(Self.idleSummary)])
        let connections = FakeConnectionProvider([connection], repeatLast: true)
        let viewModel = MenuBarViewModel(
            supervisor: supervisor,
            discover: { await connections.provide() },
            makeClient: { _ in fetcher }
        )

        await viewModel.release(leaseId: "x")

        #expect(await fetcher.summaryCallCount == 1) // tick ran as part of release(), not just release itself
    }

    // MARK: - Polling cadence: starts on command, stops on command, no timer of its own

    @Test("startPolling() ticks repeatedly at roughly the configured interval")
    func startPollingTicksRepeatedly() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let connection = ControlConnection(port: 1, token: "t")
        let fetcher = FakeSummaryFetcher([.success(Self.idleSummary)]) // repeats (script.count == 1)
        let connections = FakeConnectionProvider([connection], repeatLast: true)
        let viewModel = MenuBarViewModel(
            supervisor: supervisor,
            discover: { await connections.provide() },
            makeClient: { _ in fetcher },
            pollInterval: .milliseconds(20)
        )

        viewModel.startPolling()
        defer { viewModel.stopPolling() }

        let reachedSeveralTicks = await waitUntil(timeout: .seconds(3)) {
            await fetcher.summaryCallCount >= 3
        }
        #expect(reachedSeveralTicks)
    }

    @Test("stopPolling() halts further ticks")
    func stopPollingHaltsTicks() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let connection = ControlConnection(port: 1, token: "t")
        let fetcher = FakeSummaryFetcher([.success(Self.idleSummary)])
        let connections = FakeConnectionProvider([connection], repeatLast: true)
        let viewModel = MenuBarViewModel(
            supervisor: supervisor,
            discover: { await connections.provide() },
            makeClient: { _ in fetcher },
            pollInterval: .milliseconds(20)
        )

        viewModel.startPolling()
        let started = await waitUntil(timeout: .seconds(3)) { await fetcher.summaryCallCount >= 2 }
        #expect(started)

        viewModel.stopPolling()
        let countAtStop = await fetcher.summaryCallCount
        try? await Task.sleep(for: .milliseconds(200)) // several would-be intervals if still ticking

        #expect(await fetcher.summaryCallCount == countAtStop, "no further ticks after stopPolling()")
    }

    @Test("onContentChange fires with each resolved content, so the status item icon can stay in sync")
    func onContentChangeFires() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let connection = ControlConnection(port: 1, token: "t")
        let fetcher = FakeSummaryFetcher([.success(Self.idleSummary)])
        let connections = FakeConnectionProvider([connection], repeatLast: true)
        let viewModel = MenuBarViewModel(
            supervisor: supervisor,
            discover: { await connections.provide() },
            makeClient: { _ in fetcher }
        )

        var observed: [MenuBarContent] = []
        viewModel.onContentChange = { observed.append($0) }

        await viewModel.bootstrap()

        #expect(observed == [.running(ownership: .spawned, summary: Self.idleSummary)])
    }
}
