import HadesControl
import Observation

/// Builds a `ControlProjectsFetching` for a given connection (normally `ControlClient.init`) - the
/// Projects-view analogue of `SummaryClientFactory`.
public typealias ProjectsClientFactory = @Sendable (ControlConnection) -> any ControlProjectsFetching

/// Owns the Projects section's own fetch and published state - nothing else. Per the settled data-
/// ownership split (`MainWindowViewModel`'s own doc comment): `MainWindowViewModel` owns navigation
/// and the polling LIFECYCLE only; each section owns its own view model and its own fetch. This is
/// that seam for Projects - `refresh()` is what `MainWindowViewModel.refreshSelectedSection` calls
/// once per tick, but only while `.projects` is the selected section (see `AppDelegate`, the
/// composition root, for the actual wiring - see `MainWindowViewModelTests.
/// wiresProjectsViewModelIntoRefreshSelectedSection` for the same pattern proven against a real
/// `ProjectsViewModel`). This type never starts a timer of its own - same discipline
/// `MenuBarViewModel`/`MainWindowViewModel` already hold to; a section with no window open, or not
/// currently selected, has no business polling.
///
/// Holds no state a view could turn into new display text: `projects` is `ProjectsResult.projects`,
/// completely unchanged - see `DTOs.swift`'s own doc comment ("Swift renders, .NET decides"). A
/// fetch failure (`discover()` returning nil, or the client throwing) leaves `projects` exactly as
/// it was - the same self-healing-next-tick contract `MenuBarViewModel.tick()` already established,
/// for the same reason: one unlucky poll must not flash a project list already on screen back to
/// empty.
@MainActor
@Observable
public final class ProjectsViewModel {
    public private(set) var projects: [ProjectRow] = []

    /// The most recent action's server-authored result text, verbatim - `ActionResult.message` /
    /// `InstallPluginResult.message`, or (on a thrown `ControlClientError.server`) the server's own
    /// error text. Shared across all six actions rather than one property per action: at most one
    /// action is ever in flight from this view at a time (every button click awaits its own
    /// `Task`), and a single "last thing that happened" is the same shape `MenuBarContent` already
    /// is for the menu bar - one published fact, not six. Never Swift-invented text: a transport/
    /// staleToken/decoding failure - see `recordServerMessage(from:)` - leaves this exactly as it
    /// was, the same self-heal discipline `refresh()` already holds for `projects`.
    public private(set) var lastActionMessage: String?

    /// One polled rebuild operation's display state per project - see `OperationProgress`'s own
    /// doc comment. Populated by `refresh()`, never by `rebuildProject(productGuid:)` itself (which
    /// only starts tracking - see that method's own doc comment).
    public private(set) var rebuildProgress: [String: OperationProgress] = [:]

    private let discover: ConnectionProvider
    private let makeClient: ProjectsClientFactory

    /// productGuid -> operationId, for every rebuild `refresh()` should keep polling. Removed the
    /// instant an operation reaches a terminal state (done/failed) or is found pruned (404) - see
    /// `pollTrackedOperations(using:)`.
    private var trackedOperationIds: [String: String] = [:]

    public init(
        discover: @escaping ConnectionProvider = { Discovery.read() },
        makeClient: @escaping ProjectsClientFactory = { ControlClient(connection: $0) }
    ) {
        self.discover = discover
        self.makeClient = makeClient
    }

    /// `GET /control/projects`, plus re-polling every tracked rebuild operation (see
    /// `pollTrackedOperations(using:)`) - one discovery read and one client for both, since both
    /// belong to the SAME ~1Hz tick `MainWindowViewModel.refreshSelectedSection` drives while
    /// Projects is selected. Called by `MainWindowViewModel.refreshSelectedSection` once per tick -
    /// see this type's own doc comment for why a `projects` fetch failure here is swallowed rather
    /// than surfaced as a separate error state: the next tick re-reads discovery and re-fetches on
    /// its own, the exact same self-heal `MenuBarViewModel.tick()` relies on.
    public func refresh() async {
        guard let connection = await discover() else { return }
        let client = makeClient(connection)
        do {
            projects = try await client.projects().projects
        } catch {
            // Self-heals next tick - see this method's own doc comment. Nothing to do here.
        }
        await pollTrackedOperations(using: client)
    }

    // MARK: - Actions (Task 4)
    //
    // Every method below: discover a connection, call the one matching `ControlProjectsFetching`
    // method, and either record its server-authored message or start tracking (rebuild only) -
    // nothing else. None derives eligibility from `ProjectRow`/`warnings`/`editor.state` because
    // none of these methods RECEIVE a `ProjectRow` at all, only a bare `productGuid` - see this
    // file's own test suite (`ProjectsViewModelActionsTests`) for why that is deliberate. A
    // `discover()` failure (Hades not reachable at all) is swallowed the same way `refresh()`
    // swallows one - there is no server text to show for a call that was never made.

    /// `POST /control/projects/add`. `path` is whatever `DirectoryPicking` already chose - this
    /// method is the only thing that ever calls the endpoint, so the panel really is the one place
    /// the shell picks a path (Task 4's own requirement). The success response is a bare
    /// `ProjectRow` with no `.message` field (see `ControlClient.addProject`'s own doc comment) -
    /// the new row appearing in `projects` on the next `refresh()` tick IS the feedback, so nothing
    /// is recorded into `lastActionMessage` on success.
    public func addProject(path: String) async {
        guard let connection = await discover() else { return }
        do {
            _ = try await makeClient(connection).addProject(path: path)
        } catch {
            recordServerMessage(from: error)
        }
    }

    /// `POST /control/projects/{productGuid}/remove`. `confirmed` is the gate itself, not a hint -
    /// `false` never reaches the network at all. The dialog that sets it to `true` lives in
    /// `ProjectDetailView`; this parameter is what makes "never call remove without confirming"
    /// provable here rather than only trusted of the SwiftUI call site.
    public func removeProject(productGuid: String, confirmed: Bool) async {
        guard confirmed else { return }
        guard let connection = await discover() else { return }
        do {
            lastActionMessage = try await makeClient(connection).removeProject(productGuid: productGuid).message
        } catch {
            recordServerMessage(from: error)
        }
    }

    /// `POST /control/projects/{productGuid}/rebuild`. Registers the returned `operationId` for
    /// `refresh()` to poll - does NOT poll it itself, matching this type's "never starts a timer of
    /// its own" discipline (see this type's own class doc comment): the very next tick, which is
    /// always imminent since Rebuild is only ever clickable while Projects is the selected section
    /// (the same section `refresh()` is already being driven for), picks it up. Clears any stale
    /// `rebuildProgress` entry for this project first, so a previous rebuild's frozen `.tracked`/
    /// `.pruned` state cannot linger on screen while the new one is still unpolled.
    public func rebuildProject(productGuid: String) async {
        guard let connection = await discover() else { return }
        do {
            let started = try await makeClient(connection).rebuildProject(productGuid: productGuid)
            rebuildProgress.removeValue(forKey: productGuid)
            trackedOperationIds[productGuid] = started.operationId
        } catch {
            recordServerMessage(from: error)
        }
    }

    /// `POST /control/projects/{productGuid}/installPlugin`. `InstallPluginResult.message` already
    /// says whether a restart is needed in plain language (see that type's own doc comment) - this
    /// renders it verbatim, never re-stating `needsRestart` as separate Swift-authored text.
    public func installPlugin(productGuid: String) async {
        guard let connection = await discover() else { return }
        do {
            lastActionMessage = try await makeClient(connection).installPlugin(productGuid: productGuid).message
        } catch {
            recordServerMessage(from: error)
        }
    }

    /// `POST /control/projects/{productGuid}/revealInFinder`.
    public func revealInFinder(productGuid: String) async {
        guard let connection = await discover() else { return }
        do {
            lastActionMessage = try await makeClient(connection).revealInFinder(productGuid: productGuid).message
        } catch {
            recordServerMessage(from: error)
        }
    }

    /// `POST /control/projects/{productGuid}/openInUnity`.
    public func openInUnity(productGuid: String) async {
        guard let connection = await discover() else { return }
        do {
            lastActionMessage = try await makeClient(connection).openInUnity(productGuid: productGuid).message
        } catch {
            recordServerMessage(from: error)
        }
    }

    // MARK: - Private helpers

    /// Re-polls `GET /control/operations/{id}` for every project this view model is currently
    /// tracking a rebuild for - see `trackedOperationIds`'s own doc comment. A terminal state
    /// (done/failed) or a pruned (404) result stops tracking that project; a transient failure
    /// (staleToken/transport/decoding, or a 404 with no message) leaves `rebuildProgress` exactly
    /// as it was and keeps tracking, self-healing on the next tick - same discipline `refresh()`'s
    /// own `projects` fetch already holds to.
    private func pollTrackedOperations(using client: any ControlProjectsFetching) async {
        for (productGuid, operationId) in trackedOperationIds {
            do {
                let result = try await client.operation(id: operationId)
                rebuildProgress[productGuid] = .tracked(result)
                if result.state != .running {
                    trackedOperationIds.removeValue(forKey: productGuid)
                }
            } catch {
                if case .server(let status, let message?) = error, status == 404 {
                    rebuildProgress[productGuid] = .pruned(message: message)
                    trackedOperationIds.removeValue(forKey: productGuid)
                }
                // Any other failure: leave rebuildProgress as-is, keep tracking, retry next tick.
            }
        }
    }

    /// The shared tail of every simple action above: `ControlClientError.server(message:)` is the
    /// one failure case with server-authored text meant to be shown (every Control endpoint's error
    /// responses carry one - see `ControlClientError.server`'s own doc comment); every other case
    /// (`staleToken`, `transport`, `decoding`, `encoding`, or a `.server` with no message) has
    /// nothing to render, so `lastActionMessage` is left exactly as it was rather than being
    /// cleared or replaced with Swift-invented text.
    private func recordServerMessage(from error: ControlClientError) {
        if case .server(_, let message?) = error {
            lastActionMessage = message
        }
    }
}
