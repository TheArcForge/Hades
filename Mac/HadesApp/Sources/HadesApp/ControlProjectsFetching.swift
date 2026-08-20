import HadesControl

/// The narrow slice of `ControlClient` that the Projects view needs: fetch the one endpoint it
/// renders, and every Projects action (Task 4) - same "fetch plus act" shape
/// `ControlSummaryFetching` already established for the menu bar (`summary()` plus
/// `releaseLease(id:)`), not a separate protocol per HTTP verb. Exists purely so tests can fake the
/// control API without a real `URLSession` round trip - see `FakeProjectsFetcher` in
/// `Tests/HadesAppTests/Support/TestSupport.swift`. `ControlClient` needed no changes to conform
/// (empty extension below): every one of these already matches this signature, typed throws
/// included.
///
/// `operation(id:)` lives here rather than in a protocol of its own: the only caller that ever
/// polls an operation id is `ProjectsViewModel.refresh()`, tracking an id `rebuildProject(productGuid:)`
/// itself just returned - the same view model, the same client, the same seam.
public protocol ControlProjectsFetching: Sendable {
    func projects() async throws(ControlClientError) -> ProjectsResult

    /// `POST /control/projects/add`. The panel that chose `path` is the only place the shell picks
    /// one - see `DirectoryPicking`.
    func addProject(path: String) async throws(ControlClientError) -> ProjectRow

    /// `POST /control/projects/{productGuid}/remove`. Callers must only reach this after the
    /// shell's own confirmation - see `ProjectsViewModel.removeProject(productGuid:confirmed:)`.
    func removeProject(productGuid: String) async throws(ControlClientError) -> ActionResult

    /// `POST /control/projects/{productGuid}/rebuild`. Returns immediately; the id it hands back is
    /// what `operation(id:)` below polls.
    func rebuildProject(productGuid: String) async throws(ControlClientError) -> RebuildStartedResult

    func installPlugin(productGuid: String) async throws(ControlClientError) -> InstallPluginResult
    func revealInFinder(productGuid: String) async throws(ControlClientError) -> ActionResult
    func openInUnity(productGuid: String) async throws(ControlClientError) -> ActionResult

    /// `GET /control/operations/{id}` - the poll side of `rebuildProject(productGuid:)`. An unknown
    /// id surfaces as `ControlClientError.server(status: 404, message:)`, the server's own "may
    /// have completed and been pruned, or the id is wrong" - a normal outcome for a finished
    /// rebuild, never thrown as something else. See `OperationProgress`.
    func operation(id: String) async throws(ControlClientError) -> OperationResult
}

extension ControlClient: ControlProjectsFetching {}
