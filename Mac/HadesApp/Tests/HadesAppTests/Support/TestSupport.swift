import Foundation
import HadesControl
import HadesSupervision

@testable import HadesApp

/// A settable stand-in for `CoreSupervisor` - an actor (not a class with locks) purely because
/// that is the simplest thing that is safe to call from `MenuBarViewModel`'s `@MainActor` code
/// under Swift 6 strict concurrency, matching how the real `CoreSupervisor` is itself an actor.
/// Lets `MenuBarViewModelTests` drive every `CoreSupervisor.State` case without spawning a single
/// real process - the state-machine behaviour those transitions represent is already
/// `CoreSupervisorTests`' job (Plan 12 Task 2), not this package's.
actor FakeCoreSupervisor: CoreSupervising {
    private(set) var state: CoreSupervisor.State
    private(set) var refreshCallCount = 0

    /// If set, `refresh()` applies this transition the NEXT time it is called (once), simulating
    /// `CoreSupervisor.refresh()` noticing an adopted core is gone - see that method's own doc
    /// comment for why refresh is the only thing that re-checks an adopted core.
    private var stateAfterNextRefresh: CoreSupervisor.State?

    init(state: CoreSupervisor.State) {
        self.state = state
    }

    func setState(_ newState: CoreSupervisor.State) {
        state = newState
    }

    func setStateAfterNextRefresh(_ newState: CoreSupervisor.State) {
        stateAfterNextRefresh = newState
    }

    func refresh() async {
        refreshCallCount += 1
        if let stateAfterNextRefresh {
            state = stateAfterNextRefresh
            self.stateAfterNextRefresh = nil
        }
    }
}

/// A settable stand-in for "wherever the discovery file currently points" - an actor so a test can
/// hand `MenuBarViewModel` a DIFFERENT connection on a later call (simulating the core restarting
/// and rewriting the discovery file with a fresh token) without the "mutate a captured local var
/// from inside an escaping @Sendable closure" trap Swift 6 strict concurrency forbids.
actor FakeConnectionProvider {
    private var queue: [ControlConnection?]
    private let repeatLast: Bool
    private(set) var callCount = 0

    /// `repeatLast: true` keeps returning the final queued value forever once the queue empties -
    /// for open-ended polling tests that do not pin down an exact tick count. `repeatLast: false`
    /// (the default) returns nil once exhausted, so a test asserting an EXACT call count notices
    /// if `MenuBarViewModel` calls this more often than expected.
    init(_ queue: [ControlConnection?], repeatLast: Bool = false) {
        self.queue = queue
        self.repeatLast = repeatLast
    }

    func provide() async -> ControlConnection? {
        callCount += 1
        if queue.isEmpty { return nil }
        if queue.count == 1 { return repeatLast ? queue[0] : queue.removeFirst() }
        return queue.removeFirst()
    }
}

/// A scriptable stand-in for `ControlClient` conforming to `ControlSummaryFetching`. Outcomes are
/// consumed in order; once exhausted, the last outcome repeats - same open-ended-polling rationale
/// as `FakeConnectionProvider`.
actor FakeSummaryFetcher: ControlSummaryFetching {
    enum Outcome {
        case success(SummaryResult)
        case failure(ControlClientError)
    }

    private var script: [Outcome]
    private(set) var summaryCallCount = 0
    private(set) var releaseCallCount = 0
    private(set) var lastReleasedLeaseId: String?
    private var releaseOutcome: Result<ActionResult, ControlClientError>

    init(
        _ script: [Outcome],
        releaseOutcome: Result<ActionResult, ControlClientError> = .success(
            ActionResult(success: true, message: "released"))
    ) {
        self.script = script
        self.releaseOutcome = releaseOutcome
    }

    func summary() async throws(ControlClientError) -> SummaryResult {
        summaryCallCount += 1
        let outcome: Outcome
        if script.isEmpty {
            fatalError("FakeSummaryFetcher.summary() called with an empty script - not scripted for this call")
        } else if script.count == 1 {
            outcome = script[0]
        } else {
            outcome = script.removeFirst()
        }
        switch outcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    func releaseLease(id: String) async throws(ControlClientError) -> ActionResult {
        releaseCallCount += 1
        lastReleasedLeaseId = id
        switch releaseOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }
}

/// A scriptable stand-in for `ControlClient` conforming to `ControlProjectsFetching`. `projects()`
/// keeps its original consume-in-order / repeat-last-on-exhaustion contract - outcomes are consumed
/// in order; once exhausted, the last outcome repeats, for the same open-ended-polling tests need.
///
/// Task 4's six actions are each a SINGLE fixed outcome plus a call count and the last-seen
/// argument(s) - the same simpler shape `FakeSummaryFetcher.releaseLease` already established for
/// `ControlSummaryFetching`'s own action method, since every one of these is a single
/// user-initiated call in a test, never polled repeatedly the way `projects()`/`operation(id:)`
/// are. Every new initializer parameter defaults to a `.failure(.staleToken)` sentinel, so every
/// existing positional `FakeProjectsFetcher([...])` call site (Task 3's tests) keeps compiling
/// unchanged - the new capabilities are purely additive.
///
/// `operation(id:)` is the one exception: multi-tick rebuild polling needs a DIFFERENT answer on
/// successive calls (e.g. running, then done, or done then pruned), which a single fixed outcome
/// cannot express - so it gets its own consumable script, the same repeat-last-forever contract as
/// `projects()` above.
actor FakeProjectsFetcher: ControlProjectsFetching {
    enum Outcome {
        case success(ProjectsResult)
        case failure(ControlClientError)
    }

    enum OperationOutcome {
        case success(OperationResult)
        case failure(ControlClientError)
    }

    private var script: [Outcome]
    private(set) var projectsCallCount = 0

    private var addOutcome: Result<ProjectRow, ControlClientError>
    private(set) var addCallCount = 0
    private(set) var lastAddedPath: String?

    private var removeOutcome: Result<ActionResult, ControlClientError>
    private(set) var removeCallCount = 0
    private(set) var lastRemovedProductGuid: String?

    private var rebuildOutcome: Result<RebuildStartedResult, ControlClientError>
    private(set) var rebuildCallCount = 0
    private(set) var lastRebuiltProductGuid: String?

    private var installPluginOutcome: Result<InstallPluginResult, ControlClientError>
    private(set) var installPluginCallCount = 0
    private(set) var lastInstallPluginProductGuid: String?

    private var revealInFinderOutcome: Result<ActionResult, ControlClientError>
    private(set) var revealInFinderCallCount = 0
    private(set) var lastRevealedProductGuid: String?

    private var openInUnityOutcome: Result<ActionResult, ControlClientError>
    private(set) var openInUnityCallCount = 0
    private(set) var lastOpenedProductGuid: String?

    private var operationScript: [OperationOutcome]
    private(set) var operationCallCount = 0
    private(set) var lastRequestedOperationId: String?

    init(
        _ script: [Outcome],
        addOutcome: Result<ProjectRow, ControlClientError> = .failure(.staleToken),
        removeOutcome: Result<ActionResult, ControlClientError> = .failure(.staleToken),
        rebuildOutcome: Result<RebuildStartedResult, ControlClientError> = .failure(.staleToken),
        installPluginOutcome: Result<InstallPluginResult, ControlClientError> = .failure(.staleToken),
        revealInFinderOutcome: Result<ActionResult, ControlClientError> = .failure(.staleToken),
        openInUnityOutcome: Result<ActionResult, ControlClientError> = .failure(.staleToken),
        operationScript: [OperationOutcome] = []
    ) {
        self.script = script
        self.addOutcome = addOutcome
        self.removeOutcome = removeOutcome
        self.rebuildOutcome = rebuildOutcome
        self.installPluginOutcome = installPluginOutcome
        self.revealInFinderOutcome = revealInFinderOutcome
        self.openInUnityOutcome = openInUnityOutcome
        self.operationScript = operationScript
    }

    /// Lets one test drive a failure then a success against the SAME view model - the only way to
    /// prove `lastActionMessage` is cleared rather than merely never set.
    func setAddOutcome(_ outcome: Result<ProjectRow, ControlClientError>) {
        addOutcome = outcome
    }

    func projects() async throws(ControlClientError) -> ProjectsResult {
        projectsCallCount += 1
        let outcome: Outcome
        if script.isEmpty {
            fatalError("FakeProjectsFetcher.projects() called with an empty script - not scripted for this call")
        } else if script.count == 1 {
            outcome = script[0]
        } else {
            outcome = script.removeFirst()
        }
        switch outcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    func addProject(path: String) async throws(ControlClientError) -> ProjectRow {
        addCallCount += 1
        lastAddedPath = path
        switch addOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    func removeProject(productGuid: String) async throws(ControlClientError) -> ActionResult {
        removeCallCount += 1
        lastRemovedProductGuid = productGuid
        switch removeOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    func rebuildProject(productGuid: String) async throws(ControlClientError) -> RebuildStartedResult {
        rebuildCallCount += 1
        lastRebuiltProductGuid = productGuid
        switch rebuildOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    /// Lets a test change the outcome mid-run (e.g. simulating a transport failure on a SECOND
    /// call after a first one succeeded) - actor-isolated mutation, the same reason
    /// `FakeConnectionProvider`/`FakeCoreSupervisor` expose setters instead of a mutable stored
    /// property a test could touch directly.
    func setRebuildOutcome(_ outcome: Result<RebuildStartedResult, ControlClientError>) {
        rebuildOutcome = outcome
    }

    func installPlugin(productGuid: String) async throws(ControlClientError) -> InstallPluginResult {
        installPluginCallCount += 1
        lastInstallPluginProductGuid = productGuid
        switch installPluginOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    func revealInFinder(productGuid: String) async throws(ControlClientError) -> ActionResult {
        revealInFinderCallCount += 1
        lastRevealedProductGuid = productGuid
        switch revealInFinderOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    /// See `setRebuildOutcome(_:)`'s own doc comment.
    func setRevealInFinderOutcome(_ outcome: Result<ActionResult, ControlClientError>) {
        revealInFinderOutcome = outcome
    }

    func openInUnity(productGuid: String) async throws(ControlClientError) -> ActionResult {
        openInUnityCallCount += 1
        lastOpenedProductGuid = productGuid
        switch openInUnityOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    func operation(id: String) async throws(ControlClientError) -> OperationResult {
        operationCallCount += 1
        lastRequestedOperationId = id
        let outcome: OperationOutcome
        if operationScript.isEmpty {
            fatalError("FakeProjectsFetcher.operation(id:) called with an empty script - not scripted for this call")
        } else if operationScript.count == 1 {
            outcome = operationScript[0]
        } else {
            outcome = operationScript.removeFirst()
        }
        switch outcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }
}

/// A scriptable stand-in for `ControlClient` conforming to `ControlTracesFetching`. `tracesSequences`
/// keeps the same consume-in-order / repeat-last-on-exhaustion contract `FakeProjectsFetcher.projects()`
/// established, for the same open-ended-polling tests (`TracesViewModel.refresh()` re-fetches
/// sequences every tick, same as `ProjectsViewModel.refresh()` does for `projects`).
/// `tracesFailures`/`tracesSlow`/`traceDetail` are each a single fixed outcome plus a call count and
/// the last-seen argument(s) - the same simpler shape `FakeProjectsFetcher`'s own six actions use,
/// since nothing in this task's tests needs a multi-call script for any of the three.
actor FakeTracesFetcher: ControlTracesFetching {
    enum SequencesOutcome {
        case success(TraceSequencesResult)
        case failure(ControlClientError)
    }

    /// See `projects()`'s own doc comment.
    enum ProjectsOutcome {
        case success(ProjectsResult)
        case failure(ControlClientError)
    }

    private var sequencesScript: [SequencesOutcome]
    private(set) var sequencesCallCount = 0
    private(set) var lastProject: String?
    private(set) var lastTool: String?
    private(set) var lastOutcome: String?
    private(set) var lastMinDurationMs: Int?
    private(set) var lastMaxDurationMs: Int?

    private var failuresOutcome: Result<FailedCallsResult, ControlClientError>
    private(set) var failuresCallCount = 0
    private(set) var lastFailuresProject: String?

    private var slowOutcome: Result<SlowToolsResult, ControlClientError>
    private(set) var slowCallCount = 0
    private(set) var lastSlowProject: String?

    private var detailOutcome: Result<TraceDetailResult, ControlClientError>
    private(set) var detailCallCount = 0
    private(set) var lastRequestedTraceId: String?

    private var projectsScript: [ProjectsOutcome]
    private(set) var projectsCallCount = 0

    init(
        sequencesScript: [SequencesOutcome] = [.success(TraceSequencesResult(sequences: [], truncated: false))],
        failuresOutcome: Result<FailedCallsResult, ControlClientError> = .success(FailedCallsResult(failures: [])),
        slowOutcome: Result<SlowToolsResult, ControlClientError> = .success(SlowToolsResult(tools: [])),
        detailOutcome: Result<TraceDetailResult, ControlClientError> = .failure(.staleToken),
        projectsScript: [ProjectsOutcome] = [.success(ProjectsResult(projects: []))]
    ) {
        self.sequencesScript = sequencesScript
        self.failuresOutcome = failuresOutcome
        self.slowOutcome = slowOutcome
        self.detailOutcome = detailOutcome
        self.projectsScript = projectsScript
    }

    func tracesSequences(
        project: String?, tool: String?, outcome: String?, minDurationMs: Int?, maxDurationMs: Int?, limit: Int?
    ) async throws(ControlClientError) -> TraceSequencesResult {
        sequencesCallCount += 1
        lastProject = project
        lastTool = tool
        lastOutcome = outcome
        lastMinDurationMs = minDurationMs
        lastMaxDurationMs = maxDurationMs
        let outcome: SequencesOutcome
        if sequencesScript.isEmpty {
            fatalError("FakeTracesFetcher.tracesSequences() called with an empty script - not scripted for this call")
        } else if sequencesScript.count == 1 {
            outcome = sequencesScript[0]
        } else {
            outcome = sequencesScript.removeFirst()
        }
        switch outcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }

    func tracesFailures(project: String?, limit: Int?) async throws(ControlClientError) -> FailedCallsResult {
        failuresCallCount += 1
        lastFailuresProject = project
        switch failuresOutcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }

    func tracesSlow(project: String?, limit: Int?) async throws(ControlClientError) -> SlowToolsResult {
        slowCallCount += 1
        lastSlowProject = project
        switch slowOutcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }

    func traceDetail(traceId: String, project: String?) async throws(ControlClientError) -> TraceDetailResult {
        detailCallCount += 1
        lastRequestedTraceId = traceId
        switch detailOutcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }

    /// See `FakeProjectsFetcher.setRebuildOutcome(_:)`'s own doc comment for why this exists: lets a
    /// test change the outcome mid-run (e.g. a second `selectTrace` call after the first succeeded).
    func setDetailOutcome(_ outcome: Result<TraceDetailResult, ControlClientError>) {
        detailOutcome = outcome
    }

    /// `GET /control/projects` - populates `TracesViewModel.knownProjects` (the project Picker) and
    /// is what `refresh()` consults to resolve a default project when ambiguous. Same
    /// consume-in-order / repeat-last-on-exhaustion contract as `sequencesScript` above. Defaults to
    /// a SINGLE empty-list outcome so every call site that does not care about the project picker
    /// (every test written before this fetcher gained this method) keeps compiling AND passing
    /// unchanged: an empty `knownProjects` never triggers `TracesViewModel`'s default-selection
    /// logic, so `projectFilter` stays exactly the `""` it always defaulted to, and `project: nil` is
    /// sent exactly as it was before this method existed.
    func projects() async throws(ControlClientError) -> ProjectsResult {
        projectsCallCount += 1
        let outcome: ProjectsOutcome
        if projectsScript.isEmpty {
            fatalError("FakeTracesFetcher.projects() called with an empty script - not scripted for this call")
        } else if projectsScript.count == 1 {
            outcome = projectsScript[0]
        } else {
            outcome = projectsScript.removeFirst()
        }
        switch outcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }
}

/// A scriptable stand-in for `ControlClient` conforming to `ControlMemoryFetching`. `memory()` keeps
/// the same consume-in-order / repeat-last-on-exhaustion contract `FakeProjectsFetcher.projects()` /
/// `FakeTracesFetcher.tracesSequences` established, for the same open-ended-polling tests
/// (`MemoryViewModel.refresh()` re-fetches `GET /control/memory` every tick while Memory is
/// selected). `memoryDocument`/`writeMemoryDocument`/`acceptMemoryProposal`/`dismissMemoryProposal`/
/// `deferMemoryProposal` are each a single fixed outcome plus a call count and the last-seen
/// argument(s) - the same simpler shape `FakeProjectsFetcher`'s own six actions and
/// `FakeTracesFetcher.traceDetail` already use, since nothing in this task's tests needs a
/// multi-call script for any of the five.
actor FakeMemoryFetcher: ControlMemoryFetching {
    enum Outcome {
        case success(MemoryResult)
        case failure(ControlClientError)
    }

    /// See `projects()`'s own doc comment.
    enum ProjectsOutcome {
        case success(ProjectsResult)
        case failure(ControlClientError)
    }

    private var script: [Outcome]
    private(set) var memoryCallCount = 0
    private(set) var lastMemoryProject: String?

    private var documentOutcome: Result<MemoryDocumentResult, ControlClientError>
    private(set) var documentCallCount = 0
    private(set) var lastRequestedDocumentName: String?
    private(set) var lastDocumentProject: String?

    private var writeOutcome: Result<ActionResult, ControlClientError>
    private(set) var writeCallCount = 0
    private(set) var lastWrittenName: String?
    private(set) var lastWrittenContent: String?
    private(set) var lastWriteProject: String?

    private var acceptOutcome: Result<ActionResult, ControlClientError>
    private(set) var acceptCallCount = 0
    private(set) var lastAcceptedFileName: String?
    private(set) var lastAcceptProject: String?

    private var dismissOutcome: Result<ActionResult, ControlClientError>
    private(set) var dismissCallCount = 0
    private(set) var lastDismissedFileName: String?
    private(set) var lastDismissConfirm: Bool?
    private(set) var lastDismissProject: String?

    private var deferOutcome: Result<ActionResult, ControlClientError>
    private(set) var deferCallCount = 0
    private(set) var lastDeferredFileName: String?
    private(set) var lastDeferProject: String?

    private var projectsScript: [ProjectsOutcome]
    private(set) var projectsCallCount = 0

    init(
        _ script: [Outcome] = [.success(MemoryResult(documents: [], proposals: []))],
        documentOutcome: Result<MemoryDocumentResult, ControlClientError> = .failure(.staleToken),
        writeOutcome: Result<ActionResult, ControlClientError> = .failure(.staleToken),
        acceptOutcome: Result<ActionResult, ControlClientError> = .failure(.staleToken),
        dismissOutcome: Result<ActionResult, ControlClientError> = .failure(.staleToken),
        deferOutcome: Result<ActionResult, ControlClientError> = .failure(.staleToken),
        projectsScript: [ProjectsOutcome] = [.success(ProjectsResult(projects: []))]
    ) {
        self.script = script
        self.documentOutcome = documentOutcome
        self.writeOutcome = writeOutcome
        self.acceptOutcome = acceptOutcome
        self.dismissOutcome = dismissOutcome
        self.deferOutcome = deferOutcome
        self.projectsScript = projectsScript
    }

    func memory(project: String?) async throws(ControlClientError) -> MemoryResult {
        memoryCallCount += 1
        lastMemoryProject = project
        let outcome: Outcome
        if script.isEmpty {
            fatalError("FakeMemoryFetcher.memory() called with an empty script - not scripted for this call")
        } else if script.count == 1 {
            outcome = script[0]
        } else {
            outcome = script.removeFirst()
        }
        switch outcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }

    func memoryDocument(name: String, project: String?) async throws(ControlClientError) -> MemoryDocumentResult {
        documentCallCount += 1
        lastRequestedDocumentName = name
        lastDocumentProject = project
        switch documentOutcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }

    /// See `FakeProjectsFetcher.setRebuildOutcome(_:)`'s own doc comment for why this exists: lets a
    /// test change the outcome mid-run (e.g. a second `selectDocument` call after the first succeeded).
    func setDocumentOutcome(_ outcome: Result<MemoryDocumentResult, ControlClientError>) {
        documentOutcome = outcome
    }

    func writeMemoryDocument(name: String, content: String, project: String?) async throws(ControlClientError) -> ActionResult {
        writeCallCount += 1
        lastWrittenName = name
        lastWrittenContent = content
        lastWriteProject = project
        switch writeOutcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }

    func acceptMemoryProposal(fileName: String, project: String?) async throws(ControlClientError) -> ActionResult {
        acceptCallCount += 1
        lastAcceptedFileName = fileName
        lastAcceptProject = project
        switch acceptOutcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }

    func dismissMemoryProposal(fileName: String, confirm: Bool, project: String?) async throws(ControlClientError) -> ActionResult {
        dismissCallCount += 1
        lastDismissedFileName = fileName
        lastDismissConfirm = confirm
        lastDismissProject = project
        switch dismissOutcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }

    func deferMemoryProposal(fileName: String, project: String?) async throws(ControlClientError) -> ActionResult {
        deferCallCount += 1
        lastDeferredFileName = fileName
        lastDeferProject = project
        switch deferOutcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }

    /// See `FakeProjectsFetcher.setRebuildOutcome(_:)`'s own doc comment.
    func setDeferOutcome(_ outcome: Result<ActionResult, ControlClientError>) {
        deferOutcome = outcome
    }

    /// `GET /control/projects` - populates `MemoryViewModel.knownProjects` (the project Picker) and
    /// is what `refresh()` consults to resolve a default project when ambiguous. Same
    /// consume-in-order / repeat-last-on-exhaustion contract, and the same "defaults to a single
    /// empty-list outcome so every pre-existing call site keeps compiling and passing unchanged"
    /// reasoning, as `FakeTracesFetcher.projects()`.
    func projects() async throws(ControlClientError) -> ProjectsResult {
        projectsCallCount += 1
        let outcome: ProjectsOutcome
        if projectsScript.isEmpty {
            fatalError("FakeMemoryFetcher.projects() called with an empty script - not scripted for this call")
        } else if projectsScript.count == 1 {
            outcome = projectsScript[0]
        } else {
            outcome = projectsScript.removeFirst()
        }
        switch outcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }
}

/// A scriptable stand-in for `ControlClient` conforming to `ControlSettingsFetching`. Same
/// consume-in-order / repeat-last-on-exhaustion contract `FakeProjectsFetcher.projects()` /
/// `FakeTracesFetcher.tracesSequences` established, for the same "SettingsViewModel.refresh() may be
/// called every time the Settings window opens, not just once" shape.
actor FakeSettingsFetcher: ControlSettingsFetching {
    enum Outcome {
        case success(SettingsResult)
        case failure(ControlClientError)
    }

    private var script: [Outcome]
    private(set) var settingsCallCount = 0

    init(_ script: [Outcome]) {
        self.script = script
    }

    func settings() async throws(ControlClientError) -> SettingsResult {
        settingsCallCount += 1
        let outcome: Outcome
        if script.isEmpty {
            fatalError("FakeSettingsFetcher.settings() called with an empty script - not scripted for this call")
        } else if script.count == 1 {
            outcome = script[0]
        } else {
            outcome = script.removeFirst()
        }
        switch outcome {
        case .success(let value): return value
        case .failure(let error): throw error
        }
    }
}

/// A settable stand-in for `LaunchAtLoginReading` - see that protocol's own doc comment for why the
/// real `LaunchAtLoginService` must never be touched by an automated test (it genuinely registers a
/// login item with launchd). `isEnabled` and the outcome of `setEnabled` are independently
/// controllable, exactly what `SettingsViewModelTests` needs to prove "reflects the OS's real answer
/// after toggling, not the requested value" - a request that "succeeds" (no throw) but that the OS
/// silently ignores is a DIFFERENT failure mode than a thrown error, and both must be provable.
@MainActor
final class FakeLaunchAtLoginReading: LaunchAtLoginReading {
    var isEnabled: Bool
    private(set) var setEnabledCallCount = 0
    private(set) var lastRequestedValue: Bool?

    /// When set, `setEnabled` throws this instead of applying `applyRequestToIsEnabled`. Defaults to
    /// `nil` (no throw), matching a real OS accepting the request.
    var errorToThrow: (any Error)?

    /// When `true` (the default), a non-throwing `setEnabled(_:)` also updates `isEnabled` to match
    /// the request - the ordinary "the OS honoured it" path. `false` simulates the OS silently
    /// ignoring the request (no throw, but `isEnabled` does not change) - the exact silent-failure
    /// mode `SettingsViewModel.toggleLaunchAtLogin(to:)` must guard against.
    var applyRequestToIsEnabled = true

    init(isEnabled: Bool) {
        self.isEnabled = isEnabled
    }

    func setEnabled(_ enabled: Bool) throws {
        setEnabledCallCount += 1
        lastRequestedValue = enabled
        if let errorToThrow {
            throw errorToThrow
        }
        if applyRequestToIsEnabled {
            isEnabled = enabled
        }
    }
}

/// A settable stand-in for `ResourceGuardReading` - a plain read-only fake, since this protocol has
/// no mutating method to script failure modes for (see that protocol's own doc comment: it only ever
/// reads two OS values, never changes one).
@MainActor
final class FakeResourceGuardReading: ResourceGuardReading {
    var isLowPowerModeEnabled: Bool
    var thermalState: ProcessInfo.ThermalState

    init(isLowPowerModeEnabled: Bool = false, thermalState: ProcessInfo.ThermalState = .nominal) {
        self.isLowPowerModeEnabled = isLowPowerModeEnabled
        self.thermalState = thermalState
    }
}

/// A settable stand-in for `OnboardingCompletionTracking` - see that protocol's own doc comment for
/// why the real `UserDefaultsOnboardingStore` must never be touched by an automated test (real
/// `UserDefaults` persists on disk across test runs on the same machine).
@MainActor
final class FakeOnboardingCompletionTracking: OnboardingCompletionTracking {
    private(set) var hasCompletedOnboarding: Bool
    private(set) var markCompletedCallCount = 0

    init(hasCompletedOnboarding: Bool = false) {
        self.hasCompletedOnboarding = hasCompletedOnboarding
    }

    func markCompleted() {
        markCompletedCallCount += 1
        hasCompletedOnboarding = true
    }
}

/// A scriptable stand-in for `ClaudeCodeVerifying` - `LiveClaudeCodeVerifier` genuinely dials a
/// loopback socket, so `OnboardingViewModelTests` fakes the protocol instead, the same reasoning
/// every other real-network/real-OS seam in this file already follows. Same consume-in-order /
/// repeat-last-on-exhaustion contract `FakeProjectsFetcher.projects()` established.
actor FakeClaudeCodeVerifying: ClaudeCodeVerifying {
    private var script: [ClaudeCodeVerification]
    private(set) var verifyCallCount = 0

    init(_ script: [ClaudeCodeVerification]) {
        self.script = script
    }

    func verify() async -> ClaudeCodeVerification {
        verifyCallCount += 1
        if script.isEmpty {
            fatalError("FakeClaudeCodeVerifying.verify() called with an empty script - not scripted for this call")
        } else if script.count == 1 {
            return script[0]
        } else {
            return script.removeFirst()
        }
    }
}

/// A scriptable stand-in for `ControlClient` conforming to `ControlMigrationFetching` - what
/// `LiveMigrationOfferingTests` uses to prove `LiveMigrationOffering`'s own logic (resolving a
/// path to a productGuid via `projects()`, then calling the right migration route) without a real
/// `URLSession` round trip, and what `MigrationCleanupViewModelTests`/`SettingsViewModelTests` use
/// to prove the per-item cleanup UI's own contract. Each migration action is a single fixed outcome
/// plus a call count and the last-seen argument(s) - the same simpler shape `FakeProjectsFetcher`'s
/// own six actions use (see that type's own doc comment): nothing here needs a multi-call script,
/// since every consumer calls each endpoint at most once per its own method invocation. Every new
/// initializer parameter added for the five cleanup routes defaults to `.failure(.staleToken)`, so
/// every existing `FakeMigrationFetcher(projectsOutcome:...)` call site (`LiveMigrationOfferingTests`)
/// keeps compiling and passing unchanged - the same additive-parameter convention this file already
/// uses throughout (see `FakeProjectsFetcher`'s own doc comment).
actor FakeMigrationFetcher: ControlMigrationFetching {
    enum ProjectsOutcome {
        case success(ProjectsResult)
        case failure(ControlClientError)
    }

    enum DetectOutcome {
        case success(MigrationDetectionResult)
        case failure(ControlClientError)
    }

    enum MemoryImportOutcome {
        case success(MigrationMemoryImportResult)
        case failure(ControlClientError)
    }

    enum TracesImportOutcome {
        case success(MigrationTracesImportResult)
        case failure(ControlClientError)
    }

    enum ClaudeMdCleanupOutcome {
        case success(MigrationClaudeMdCleanupResult)
        case failure(ControlClientError)
    }

    enum ManifestCleanupOutcome {
        case success(MigrationManifestCleanupResult)
        case failure(ControlClientError)
    }

    enum McpConfigCleanupOutcome {
        case success(MigrationMcpConfigCleanupResult)
        case failure(ControlClientError)
    }

    enum ClaudeDesktopConfigCleanupOutcome {
        case success(MigrationClaudeDesktopConfigCleanupResult)
        case failure(ControlClientError)
    }

    enum HadesHubCleanupOutcome {
        case success(MigrationHadesHubCleanupResult)
        case failure(ControlClientError)
    }

    private var projectsOutcome: ProjectsOutcome
    private(set) var projectsCallCount = 0

    private var detectOutcome: DetectOutcome
    private(set) var detectCallCount = 0
    private(set) var lastDetectedProductGuid: String?

    private var importMemoryOutcome: MemoryImportOutcome
    private(set) var importMemoryCallCount = 0
    private(set) var lastImportMemoryProductGuid: String?

    private var importTracesOutcome: TracesImportOutcome
    private(set) var importTracesCallCount = 0
    private(set) var lastImportTracesProductGuid: String?

    private var cleanClaudeMdOutcome: ClaudeMdCleanupOutcome
    private(set) var cleanClaudeMdCallCount = 0
    private(set) var lastCleanClaudeMdProductGuid: String?
    private(set) var lastCleanClaudeMdProceed: Bool?

    private var cleanManifestOutcome: ManifestCleanupOutcome
    private(set) var cleanManifestCallCount = 0
    private(set) var lastCleanManifestProductGuid: String?
    private(set) var lastCleanManifestProceed: Bool?

    private var cleanMcpConfigOutcome: McpConfigCleanupOutcome
    private(set) var cleanMcpConfigCallCount = 0
    private(set) var lastCleanMcpConfigProductGuid: String?
    private(set) var lastCleanMcpConfigProceed: Bool?

    private var cleanClaudeDesktopConfigOutcome: ClaudeDesktopConfigCleanupOutcome
    private(set) var cleanClaudeDesktopConfigCallCount = 0
    private(set) var lastCleanClaudeDesktopConfigProceed: Bool?

    private var hadesHubCleanupOutcome: HadesHubCleanupOutcome
    private(set) var cleanHadesHubCallCount = 0
    private(set) var lastCleanHadesHubProceed: Bool?

    init(
        projectsOutcome: ProjectsOutcome,
        detectOutcome: DetectOutcome = .failure(.staleToken),
        importMemoryOutcome: MemoryImportOutcome = .failure(.staleToken),
        importTracesOutcome: TracesImportOutcome = .failure(.staleToken),
        cleanClaudeMdOutcome: ClaudeMdCleanupOutcome = .failure(.staleToken),
        cleanManifestOutcome: ManifestCleanupOutcome = .failure(.staleToken),
        cleanMcpConfigOutcome: McpConfigCleanupOutcome = .failure(.staleToken),
        cleanClaudeDesktopConfigOutcome: ClaudeDesktopConfigCleanupOutcome = .failure(.staleToken),
        hadesHubCleanupOutcome: HadesHubCleanupOutcome = .failure(.staleToken)
    ) {
        self.projectsOutcome = projectsOutcome
        self.detectOutcome = detectOutcome
        self.importMemoryOutcome = importMemoryOutcome
        self.importTracesOutcome = importTracesOutcome
        self.cleanClaudeMdOutcome = cleanClaudeMdOutcome
        self.cleanManifestOutcome = cleanManifestOutcome
        self.cleanMcpConfigOutcome = cleanMcpConfigOutcome
        self.cleanClaudeDesktopConfigOutcome = cleanClaudeDesktopConfigOutcome
        self.hadesHubCleanupOutcome = hadesHubCleanupOutcome
    }

    func projects() async throws(ControlClientError) -> ProjectsResult {
        projectsCallCount += 1
        switch projectsOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    func migrationDetect(productGuid: String) async throws(ControlClientError) -> MigrationDetectionResult {
        detectCallCount += 1
        lastDetectedProductGuid = productGuid
        switch detectOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    func migrationImportMemory(productGuid: String) async throws(ControlClientError) -> MigrationMemoryImportResult {
        importMemoryCallCount += 1
        lastImportMemoryProductGuid = productGuid
        switch importMemoryOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    func migrationImportTraces(productGuid: String) async throws(ControlClientError) -> MigrationTracesImportResult {
        importTracesCallCount += 1
        lastImportTracesProductGuid = productGuid
        switch importTracesOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    func migrationCleanClaudeMd(productGuid: String, proceed: Bool) async throws(ControlClientError) -> MigrationClaudeMdCleanupResult {
        cleanClaudeMdCallCount += 1
        lastCleanClaudeMdProductGuid = productGuid
        lastCleanClaudeMdProceed = proceed
        switch cleanClaudeMdOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    /// See `FakeProjectsFetcher.setRebuildOutcome(_:)`'s own doc comment for why this exists: lets a
    /// test change the outcome mid-run (e.g. a proceed:false preview followed by a proceed:true
    /// confirm that returns a different result).
    func setCleanClaudeMdOutcome(_ outcome: ClaudeMdCleanupOutcome) {
        cleanClaudeMdOutcome = outcome
    }

    func migrationCleanManifest(productGuid: String, proceed: Bool) async throws(ControlClientError) -> MigrationManifestCleanupResult {
        cleanManifestCallCount += 1
        lastCleanManifestProductGuid = productGuid
        lastCleanManifestProceed = proceed
        switch cleanManifestOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    /// See `setCleanClaudeMdOutcome(_:)`'s own doc comment.
    func setCleanManifestOutcome(_ outcome: ManifestCleanupOutcome) {
        cleanManifestOutcome = outcome
    }

    func migrationCleanMcpConfig(productGuid: String, proceed: Bool) async throws(ControlClientError) -> MigrationMcpConfigCleanupResult {
        cleanMcpConfigCallCount += 1
        lastCleanMcpConfigProductGuid = productGuid
        lastCleanMcpConfigProceed = proceed
        switch cleanMcpConfigOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    /// See `setCleanClaudeMdOutcome(_:)`'s own doc comment.
    func setCleanMcpConfigOutcome(_ outcome: McpConfigCleanupOutcome) {
        cleanMcpConfigOutcome = outcome
    }

    func migrationCleanClaudeDesktopConfig(proceed: Bool) async throws(ControlClientError) -> MigrationClaudeDesktopConfigCleanupResult {
        cleanClaudeDesktopConfigCallCount += 1
        lastCleanClaudeDesktopConfigProceed = proceed
        switch cleanClaudeDesktopConfigOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    /// See `setCleanClaudeMdOutcome(_:)`'s own doc comment.
    func setCleanClaudeDesktopConfigOutcome(_ outcome: ClaudeDesktopConfigCleanupOutcome) {
        cleanClaudeDesktopConfigOutcome = outcome
    }

    func migrationCleanHadesHub(proceed: Bool) async throws(ControlClientError) -> MigrationHadesHubCleanupResult {
        cleanHadesHubCallCount += 1
        lastCleanHadesHubProceed = proceed
        switch hadesHubCleanupOutcome {
        case .success(let result): return result
        case .failure(let error): throw error
        }
    }

    /// See `setCleanClaudeMdOutcome(_:)`'s own doc comment.
    func setHadesHubCleanupOutcome(_ outcome: HadesHubCleanupOutcome) {
        hadesHubCleanupOutcome = outcome
    }
}

/// A scriptable stand-in for `MigrationOffering` - see that protocol's own doc comment for why
/// production never constructs a real conformance at all (the control API has no migration endpoint
/// yet). Exists purely to prove `OnboardingViewModel`'s offered-never-silently-performed contract
/// ahead of that endpoint existing.
actor FakeMigrationOffering: MigrationOffering {
    private let isV12ProjectResult: Bool
    private(set) var isV12ProjectCallCount = 0
    private(set) var lastCheckedPath: String?

    private(set) var performMigrationCallCount = 0
    private(set) var lastPerformedPath: String?

    init(isV12ProjectResult: Bool) {
        self.isV12ProjectResult = isV12ProjectResult
    }

    func isV12Project(projectPath: String) async -> Bool {
        isV12ProjectCallCount += 1
        lastCheckedPath = projectPath
        return isV12ProjectResult
    }

    func performMigration(projectPath: String) async {
        performMigrationCallCount += 1
        lastPerformedPath = projectPath
    }
}

/// Polls `condition` until it is true or `timeout` elapses - the same "never trust a fixed sleep"
/// standard `HadesSupervisionTests`' own `waitUntil` applies, reused here for the same reason:
/// tests run as fast as real async work allows and still tolerate a slow CI machine. `@MainActor`
/// (unlike the HadesSupervision original) because every caller here is a `@MainActor` test
/// closure capturing `MenuBarViewModel` - keeping this function on the same actor avoids sending a
/// main-actor-isolated closure across an isolation boundary just to poll it.
@MainActor
@discardableResult
func waitUntil(
    timeout: Duration = .seconds(5),
    interval: Duration = .milliseconds(10),
    _ condition: @MainActor () async -> Bool
) async -> Bool {
    let deadline = ContinuousClock.now.advanced(by: timeout)
    while ContinuousClock.now < deadline {
        if await condition() { return true }
        try? await Task.sleep(for: interval)
    }
    return await condition()
}
