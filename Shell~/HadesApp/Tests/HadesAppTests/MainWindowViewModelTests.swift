import HadesControl
import HadesSupervision
import Testing

@testable import HadesApp

/// `MainWindowViewModel` holds exactly two pieces of view state: which sidebar `Section` is
/// selected, and the shell-level poll loop that keeps `CoreSupervisor` current while the window is
/// open. It deliberately does NOT hold Projects/Traces/Memory business data - those belong to each
/// section's own view (Tasks 3, 5, 6 of this plan), not to the window shell. What it polls for is
/// `CoreSupervisor.refresh()` - the same "callers drive the cadence" contract `MenuBarViewModel`
/// already proves (see `CoreSupervisor.refresh()`'s own doc comment, which anticipates exactly this:
/// "Callers drive the cadence (e.g. the menu bar's own ~1Hz poll while its window is open)" - the
/// main window is a second such caller), reusing the SAME `FakeCoreSupervisor` test double
/// `MenuBarViewModelTests` already established in Support/TestSupport.swift.
@Suite("MainWindowViewModel")
@MainActor
struct MainWindowViewModelTests {

    @Test("defaults to the Projects section")
    func defaultsToProjects() {
        let viewModel = MainWindowViewModel(supervisor: FakeCoreSupervisor(state: .notStarted))
        #expect(viewModel.selectedSection == .projects)
    }

    @Test("select(_:) updates the selected section")
    func selectUpdatesSection() {
        let viewModel = MainWindowViewModel(supervisor: FakeCoreSupervisor(state: .notStarted))

        viewModel.select(.traces)
        #expect(viewModel.selectedSection == .traces)

        viewModel.select(.memory)
        #expect(viewModel.selectedSection == .memory)
    }

    @Test("startPolling() ticks CoreSupervisor.refresh() repeatedly at roughly the configured interval")
    func startPollingTicksRepeatedly() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let viewModel = MainWindowViewModel(supervisor: supervisor, pollInterval: .milliseconds(20))

        viewModel.startPolling()
        defer { viewModel.stopPolling() }

        let reachedSeveralTicks = await waitUntil(timeout: .seconds(3)) {
            await supervisor.refreshCallCount >= 3
        }
        #expect(reachedSeveralTicks)
    }

    @Test("stopPolling() halts further ticks")
    func stopPollingHaltsTicks() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let viewModel = MainWindowViewModel(supervisor: supervisor, pollInterval: .milliseconds(20))

        viewModel.startPolling()
        let started = await waitUntil(timeout: .seconds(3)) { await supervisor.refreshCallCount >= 2 }
        #expect(started)

        viewModel.stopPolling()
        let countAtStop = await supervisor.refreshCallCount
        try? await Task.sleep(for: .milliseconds(200)) // several would-be intervals if still ticking

        #expect(await supervisor.refreshCallCount == countAtStop, "no further ticks after stopPolling()")
    }

    @Test("startPolling() is idempotent - a second call while already running does not start a competing loop")
    func startPollingIsIdempotent() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let viewModel = MainWindowViewModel(supervisor: supervisor, pollInterval: .milliseconds(20))

        viewModel.startPolling()
        viewModel.startPolling() // if this started a SECOND loop, stopPolling() below only cancels one

        let started = await waitUntil(timeout: .seconds(3)) { await supervisor.refreshCallCount >= 2 }
        #expect(started)

        viewModel.stopPolling()
        let countAtStop = await supervisor.refreshCallCount
        try? await Task.sleep(for: .milliseconds(200))

        #expect(await supervisor.refreshCallCount == countAtStop, "a leaked second loop would keep ticking here")
    }

    // MARK: - refreshSelectedSection: the seam Task 3 (ProjectsViewModel), 5, 6 hook into

    @Test(
        "startPolling() calls refreshSelectedSection with whichever Section is CURRENTLY selected on every tick - never the other two"
    )
    func startPollingRefreshesOnlyTheSelectedSection() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let viewModel = MainWindowViewModel(supervisor: supervisor, pollInterval: .milliseconds(20))
        viewModel.select(.traces)

        var recordedSections: [Section] = []
        viewModel.refreshSelectedSection = { section in recordedSections.append(section) }

        viewModel.startPolling()
        defer { viewModel.stopPolling() }

        let tickedTwice = await waitUntil(timeout: .seconds(3)) { recordedSections.count >= 2 }
        #expect(tickedTwice)
        #expect(recordedSections.allSatisfy { $0 == .traces }, "an unselected section must not be refreshed")
    }

    @Test("changing the selected section mid-poll changes which section subsequent ticks refresh")
    func changingSelectionChangesWhichSectionIsRefreshed() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let viewModel = MainWindowViewModel(supervisor: supervisor, pollInterval: .milliseconds(20))
        viewModel.select(.projects)

        var recordedSections: [Section] = []
        viewModel.refreshSelectedSection = { section in recordedSections.append(section) }

        viewModel.startPolling()
        defer { viewModel.stopPolling() }

        _ = await waitUntil(timeout: .seconds(3)) { recordedSections.count >= 1 }
        viewModel.select(.memory)
        _ = await waitUntil(timeout: .seconds(3)) { recordedSections.last == .memory }

        #expect(recordedSections.first == .projects)
        #expect(recordedSections.last == .memory)
    }

    /// The actual production wiring pattern (`AppDelegate.applicationDidFinishLaunching` sets
    /// `refreshSelectedSection` to exactly this closure, against a real `ProjectsViewModel` rather
    /// than a fake recorder) - proves the two already-independently-tested pieces
    /// (`MainWindowViewModel`'s per-tick dispatch, proven above with a fake closure; `ProjectsViewModel.
    /// refresh()`, proven in `ProjectsViewModelTests`) actually compose: selecting Projects and
    /// polling drives a real fetch into `projectsViewModel.projects`, and switching away stops
    /// driving it. `AppDelegate` itself stays untested per this plan's own STANDING RULES (AppKit
    /// composition root) - this is the one integration test standing in for it.
    @Test("wiring ProjectsViewModel into refreshSelectedSection - the real production pattern - populates it only while Projects is selected")
    func wiresProjectsViewModelIntoRefreshSelectedSection() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let viewModel = MainWindowViewModel(supervisor: supervisor, pollInterval: .milliseconds(20))

        let project = ProjectRow(
            name: "Hades-Unity-Client", path: "/Users/mike/Projects/Hades-Unity-Client",
            productGuid: "15c012f27331e49229cef25e74537816", unityVersion: "6000.3.2f1",
            indexState: .indexed, indexStatus: "indexed 4m ago", nodeCount: 494, edgeCount: 332,
            editor: ProjectEditorInfo(state: .absent, status: "No Editor attached", unityVersion: nil, processId: nil, connectionAgeSeconds: nil),
            warnings: []
        )
        let fetcher = FakeProjectsFetcher([.success(ProjectsResult(projects: [project]))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let projectsViewModel = ProjectsViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        // The exact pattern AppDelegate.applicationDidFinishLaunching wires up - see this test's
        // own doc comment.
        viewModel.refreshSelectedSection = { section in
            if section == .projects {
                await projectsViewModel.refresh()
            }
        }

        viewModel.startPolling()
        defer { viewModel.stopPolling() }

        let populated = await waitUntil(timeout: .seconds(3)) { !projectsViewModel.projects.isEmpty }
        #expect(populated)
        #expect(projectsViewModel.projects == [project])

        // Switching away from Projects must stop driving its fetch - same "an unselected section
        // gets no background polling" discipline `startPollingRefreshesOnlyTheSelectedSection` above
        // proves generically, now proven against the real ProjectsViewModel too.
        viewModel.select(.traces)
        let countAfterSwitch = await fetcher.projectsCallCount
        try? await Task.sleep(for: .milliseconds(200))  // several would-be ticks if still refreshing Projects
        #expect(await fetcher.projectsCallCount == countAfterSwitch, "no further Projects fetches once another section is selected")
    }

    /// Task 5's own version of `wiresProjectsViewModelIntoRefreshSelectedSection` immediately above -
    /// same real production wiring pattern (`AppDelegate.applicationDidFinishLaunching` sets
    /// `refreshSelectedSection` to exactly this closure, against a real `TracesViewModel`), proving
    /// `MainWindowViewModel`'s per-tick dispatch and `TracesViewModel.refresh()` actually compose:
    /// selecting Traces and polling drives a real fetch into `tracesViewModel.sequences`, and
    /// switching away stops driving it.
    @Test("wiring TracesViewModel into refreshSelectedSection - the real production pattern - populates it only while Traces is selected")
    func wiresTracesViewModelIntoRefreshSelectedSection() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let viewModel = MainWindowViewModel(supervisor: supervisor, pollInterval: .milliseconds(20))
        viewModel.select(.traces)

        let sequence = TraceSequenceRow(
            id: "8e26d015f3f64ca887a8f397fce03799",
            tools: ["hades_status", "search_by_name"],
            pattern: "hades_status \u{2192} search_by_name",
            callCount: 2, startUtcMs: 1_785_919_231_447, endUtcMs: 1_785_919_241_034, durationMs: 9587,
            outcome: .ok, traceIds: ["a", "b"]
        )
        let fetcher = FakeTracesFetcher(sequencesScript: [.success(TraceSequencesResult(sequences: [sequence], truncated: false))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let tracesViewModel = TracesViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        // The exact pattern AppDelegate.applicationDidFinishLaunching wires up - see this test's own
        // doc comment.
        viewModel.refreshSelectedSection = { section in
            if section == .traces {
                await tracesViewModel.refresh()
            }
        }

        viewModel.startPolling()
        defer { viewModel.stopPolling() }

        let populated = await waitUntil(timeout: .seconds(3)) { !tracesViewModel.sequences.isEmpty }
        #expect(populated)
        #expect(tracesViewModel.sequences == [sequence])

        // Switching away from Traces must stop driving its fetch - same discipline proven generically
        // above, now proven against the real TracesViewModel too.
        viewModel.select(.projects)
        let countAfterSwitch = await fetcher.sequencesCallCount
        try? await Task.sleep(for: .milliseconds(200))  // several would-be ticks if still refreshing Traces
        #expect(await fetcher.sequencesCallCount == countAfterSwitch, "no further Traces fetches once another section is selected")
    }

    /// Task 6's own version of the two wiring tests immediately above - same real production wiring
    /// pattern (`AppDelegate.applicationDidFinishLaunching` sets `refreshSelectedSection` to exactly
    /// this closure, against a real `MemoryViewModel`), proving `MainWindowViewModel`'s per-tick
    /// dispatch and `MemoryViewModel.refresh()` actually compose: selecting Memory and polling drives
    /// a real fetch into `memoryViewModel.documents`, and switching away stops driving it.
    @Test("wiring MemoryViewModel into refreshSelectedSection - the real production pattern - populates it only while Memory is selected")
    func wiresMemoryViewModelIntoRefreshSelectedSection() async {
        let supervisor = FakeCoreSupervisor(state: .running(.spawned))
        let viewModel = MainWindowViewModel(supervisor: supervisor, pollInterval: .milliseconds(20))
        viewModel.select(.memory)

        let document = MemoryDocumentRow(name: "conventions.md", sizeBytes: 191, sizeDisplay: "191 B", lastReviewed: "2026-05-12")
        let fetcher = FakeMemoryFetcher([.success(MemoryResult(documents: [document], proposals: []))])
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        let memoryViewModel = MemoryViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })

        // The exact pattern AppDelegate.applicationDidFinishLaunching wires up - see this test's own
        // doc comment.
        viewModel.refreshSelectedSection = { section in
            if section == .memory {
                await memoryViewModel.refresh()
            }
        }

        viewModel.startPolling()
        defer { viewModel.stopPolling() }

        let populated = await waitUntil(timeout: .seconds(3)) { !memoryViewModel.documents.isEmpty }
        #expect(populated)
        #expect(memoryViewModel.documents == [document])

        // Switching away from Memory must stop driving its fetch - same discipline proven generically
        // above, now proven against the real MemoryViewModel too.
        viewModel.select(.projects)
        let countAfterSwitch = await fetcher.memoryCallCount
        try? await Task.sleep(for: .milliseconds(200))  // several would-be ticks if still refreshing Memory
        #expect(await fetcher.memoryCallCount == countAfterSwitch, "no further Memory fetches once another section is selected")
    }
}
