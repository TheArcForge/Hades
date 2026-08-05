import HadesControl
import Observation

/// Builds a `ControlMemoryFetching` for a given connection (normally `ControlClient.init`) - the
/// Memory-view analogue of `ProjectsClientFactory`/`TracesClientFactory`.
public typealias MemoryClientFactory = @Sendable (ControlConnection) -> any ControlMemoryFetching

/// Owns the Memory section's own fetch and published state - nothing else. Per the settled data-
/// ownership split (`MainWindowViewModel`'s own doc comment, `ProjectsViewModel`'s and
/// `TracesViewModel`'s before it): `MainWindowViewModel` owns navigation and the polling LIFECYCLE
/// only; each section owns its own view model and its own fetch. This is that seam for Memory -
/// `refresh()` is what `MainWindowViewModel.refreshSelectedSection` calls once per tick, but only
/// while `.memory` is the selected section (see `AppDelegate`, the composition root, for the actual
/// wiring - see `MainWindowViewModelTests.wiresMemoryViewModelIntoRefreshSelectedSection` for the
/// same pattern proven against a real `MemoryViewModel`, mirroring
/// `wiresProjectsViewModelIntoRefreshSelectedSection` / `wiresTracesViewModelIntoRefreshSelectedSection`).
/// This type never starts a timer of its own - same discipline every other view model in this app
/// already holds to.
///
/// **Memory is authored and irreplaceable - the sharpest instance of that distinction in this app.**
/// `graph.db`, `traces.db` and `memory-index.db` are derived and deletable; `memory/*.md` is
/// authored, with no other copy (see `Hades.Core.Memory.MemoryStore`'s own class doc comment).
/// `saveDocument` and `dismissProposal` are the two actions here that destroy or overwrite something
/// real, and both take an explicit `confirmed: Bool` gate that is enforced HERE, not only by
/// whatever SwiftUI dialog sets it - `false` never reaches the network at all, the exact same
/// "confirmed is the gate itself, not a hint" discipline `ProjectsViewModel.
/// removeProject(productGuid:confirmed:)` already holds to. `acceptProposal`/`deferProposal` need no
/// such gate: accepting only ever APPENDS to a document (creating it if missing) and dismissing a
/// proposal never touches an authored memory/*.md file at all - see `Hades.Server.Control.
/// MemoryEndpoint`'s own class doc comment for the exact, deliberately non-destructive behaviour
/// each of the three proposal actions performs.
@MainActor
@Observable
public final class MemoryViewModel {
    public private(set) var documents: [MemoryDocumentRow] = []
    public private(set) var proposals: [MemoryProposalRow] = []

    /// Every known project, from `GET /control/projects` - populates this view's own project Picker
    /// (`ProjectRow.name`/`productGuid`, verbatim - see `MemoryView`) and is what `refresh()` consults
    /// to resolve `projectFilter`'s own default when it is still unset (see that property's own doc
    /// comment). A fetch failure here self-heals exactly like every other fetch in this type -
    /// `knownProjects` is left exactly as it was.
    public private(set) var knownProjects: [ProjectRow] = []

    /// Which project every call below scopes to - Picker-driven, set only by `selectProject(_:)`
    /// (the Memory analogue of `TracesViewModel.projectFilter`/`selectProject(_:)`; see that type's
    /// own doc comments for the full reasoning, identical here). `""` means "nothing explicitly
    /// chosen yet"; every method below sends `nil` for `project` in that case, letting the server's
    /// own auto-resolve (a single known project) or ambiguity error (2+, see `refresh()`) decide.
    ///
    /// **Defaults to the first known project once `refresh()` has seen at least one** - same
    /// uniform "apply whenever `knownProjects` is non-empty" rule `TracesViewModel.projectFilter`
    /// documents, for the identical reason: `GET /control/memory` (and every other call below) 400s
    /// with "Hades knows N projects, so this call needs a 'project' argument..." whenever 2+ projects
    /// are known and none is given.
    public private(set) var projectFilter: String = ""

    /// The most recent REFRESH failure the shell cannot act on silently, verbatim from the server -
    /// see `refresh()`'s own doc comment for exactly which failures set this versus self-heal.
    /// Deliberately separate from `lastActionMessage`: that property is the most recent ACTION's own
    /// result (Accept/Defer/Dismiss/Save), and a passive poll failure overwriting a just-seen action
    /// success (or vice versa) would be actively misleading - each reflects only its own kind of
    /// attempt. `nil` once a later refresh (e.g. after the user picks a project) succeeds.
    public private(set) var refreshError: String?

    /// The currently open document's fetch state - see `MemoryDocumentFetchState`'s own doc comment.
    /// Populated only by `selectDocument(name:)`, never by `refresh()`: an open document is a fixed
    /// snapshot for as long as it is being read or edited, not something `refresh()` should silently
    /// overwrite out from under an in-progress edit the way it does for `documents`/`proposals`.
    public private(set) var selectedDocument: MemoryDocumentFetchState = .notSelected

    /// The most recent action's server-authored result text, verbatim - `ActionResult.message`, or
    /// (on a thrown `ControlClientError.server`) the server's own error text. Shared across every
    /// action rather than one property per action - same reasoning `ProjectsViewModel.
    /// lastActionMessage`'s own doc comment gives: at most one action is ever in flight from this
    /// view at a time, and a single "last thing that happened" is the same shape that view model
    /// already is. Never Swift-invented text: a transport/staleToken/decoding failure leaves this
    /// exactly as it was, the same self-heal discipline `refresh()` already holds for
    /// `documents`/`proposals`.
    public private(set) var lastActionMessage: String?

    private let discover: ConnectionProvider
    private let makeClient: MemoryClientFactory

    public init(
        discover: @escaping ConnectionProvider = { Discovery.read() },
        makeClient: @escaping MemoryClientFactory = { ControlClient(connection: $0) }
    ) {
        self.discover = discover
        self.makeClient = makeClient
    }

    /// `GET /control/projects` + `/control/memory` - the known-project list (for the Picker and this
    /// method's own default-selection below) and documents-plus-proposal-queue together, in the one
    /// round trip the latter endpoint itself provides. Called by
    /// `MainWindowViewModel.refreshSelectedSection` once per tick while Memory is the selected
    /// section.
    ///
    /// **Self-heals independently on a TRANSIENT failure** (`.staleToken`, `.transport`,
    /// `.decoding`, or a `.server` with no message) - `documents`/`proposals`/`knownProjects` are
    /// left exactly as they were, the same self-healing-next-tick contract
    /// `ProjectsViewModel.refresh()`/`TracesViewModel.refresh()` already established: one unlucky
    /// poll must not flash a list already on screen back to empty. **Narrowed, not deleted, for a
    /// `.server` failure that DOES carry a message** - most commonly `Hades.Core.Projects.
    /// ProjectResolver`'s own "Hades knows N projects, so this call needs a 'project' argument" when
    /// more than one project is known and `project` is still unresolved. That is not a transient
    /// blip; it is the server explaining something the shell cannot act on silently, so it is
    /// surfaced verbatim via `refreshError` instead - data is still left untouched exactly as the
    /// self-heal contract above requires. `refreshError` is recomputed fresh every tick (cleared
    /// here first, then set again below if the fetch fails with a message).
    ///
    /// **The `knownProjects` fetch runs first and feeds `projectFilter`'s own default before the
    /// `memory` fetch uses it** - see `projectFilter`'s own doc comment for exactly when that default
    /// applies.
    public func refresh() async {
        guard let connection = await discover() else { return }
        let client = makeClient(connection)

        refreshError = nil

        do {
            knownProjects = try await client.projects().projects
        } catch {
            if case .server(_, let message?) = error { refreshError = message }
            // staleToken/transport/decoding, or a .server with no message: self-heals - knownProjects
            // is left exactly as it was.
        }

        if projectFilter.isEmpty, let firstKnown = knownProjects.first {
            projectFilter = firstKnown.productGuid
        }

        do {
            let result = try await client.memory(project: projectFilter.isEmpty ? nil : projectFilter)
            documents = result.documents
            proposals = result.proposals
        } catch {
            if case .server(_, let message?) = error {
                refreshError = message
            }
            // Otherwise self-heals next tick - see this method's own doc comment.
        }
    }

    /// Sets `projectFilter` alone and immediately re-fetches, rather than waiting for the next tick -
    /// the project Picker's own selection. The Memory analogue of
    /// `TracesViewModel.selectProject(_:)`; see that method's own doc comment for the full reasoning
    /// (identical here, minus Traces' extra tool/outcome/duration filters this type does not have).
    public func selectProject(_ productGuid: String) async {
        projectFilter = productGuid
        await refresh()
    }

    /// `GET /control/memory/document` - one document's complete raw text, for reading or editing.
    /// Independent of `refresh()`'s own tick - the same "user-initiated fourth fetch, never polled"
    /// shape `TracesViewModel.selectTrace(traceId:)` already established for `selectedTraceDetail`.
    /// Scoped to the currently selected `projectFilter`, same as every other call below - opening a
    /// document must resolve against the SAME project the list it came from was fetched for.
    public func selectDocument(name: String) async {
        guard let connection = await discover() else { return }
        do {
            let document = try await makeClient(connection).memoryDocument(
                name: name, project: projectFilter.isEmpty ? nil : projectFilter)
            selectedDocument = .loaded(document)
        } catch {
            if case .server(_, let message?) = error {
                selectedDocument = .failed(message: message)
            }
            // Any other failure (staleToken/transport/decoding, or a .server with no message): leave
            // selectedDocument exactly as it was - same self-heal discipline as everywhere else in
            // this app, since re-selecting can succeed later.
        }
    }

    /// Returns to the ordinary "nothing selected" state - called when the view's own selection is
    /// cleared (e.g. the user deselects a row, or switches to the Proposals list).
    public func clearSelectedDocument() {
        selectedDocument = .notSelected
    }

    /// `POST /control/memory/document` - the shell's own text-editor save path. `confirmed` is the
    /// gate itself, not a hint - see this type's own class doc comment: a save always OVERWRITES
    /// `name`'s current content on disk (`Hades.Core.Memory.MemoryStore.Write` -> `AtomicWrite` ->
    /// `File.Move(..., overwrite: true)`, confirmed by reading the core), with no merge and no
    /// version history. The dialog that sets `confirmed` to `true` lives in `MemoryDocumentView`;
    /// this parameter is what makes "never overwrite an authored file without confirming" provable
    /// here rather than only trusted of the SwiftUI call site.
    public func saveDocument(name: String, content: String, confirmed: Bool) async {
        guard confirmed else { return }
        guard let connection = await discover() else { return }
        do {
            lastActionMessage = try await makeClient(connection).writeMemoryDocument(
                name: name, content: content, project: projectFilter.isEmpty ? nil : projectFilter
            ).message
        } catch {
            recordServerMessage(from: error)
        }
    }

    /// `POST /control/memory/proposals/accept`. No confirmation gate - see this type's own class doc
    /// comment for why accepting is never destructive.
    public func acceptProposal(fileName: String) async {
        guard let connection = await discover() else { return }
        do {
            lastActionMessage = try await makeClient(connection).acceptMemoryProposal(
                fileName: fileName, project: projectFilter.isEmpty ? nil : projectFilter
            ).message
        } catch {
            recordServerMessage(from: error)
        }
    }

    /// `POST /control/memory/proposals/defer`. No confirmation gate - pure bookkeeping, never
    /// deletes, never writes an authored document (see this type's own class doc comment).
    public func deferProposal(fileName: String) async {
        guard let connection = await discover() else { return }
        do {
            lastActionMessage = try await makeClient(connection).deferMemoryProposal(
                fileName: fileName, project: projectFilter.isEmpty ? nil : projectFilter
            ).message
        } catch {
            recordServerMessage(from: error)
        }
    }

    /// `POST /control/memory/proposals/dismiss` - deletes the proposal file. `confirmed` is the gate
    /// itself, not a hint - same reasoning `saveDocument(name:content:confirmed:)`'s own doc comment
    /// gives. The core ALSO refuses with its own 400 unless `confirm=true` is set
    /// (`Hades.Server.Control.MemoryEndpoint.DismissProposal`), so this is defense in depth, not the
    /// only gate - `confirm: true` is only ever sent once this method's own guard has already passed.
    public func dismissProposal(fileName: String, confirmed: Bool) async {
        guard confirmed else { return }
        guard let connection = await discover() else { return }
        do {
            lastActionMessage = try await makeClient(connection).dismissMemoryProposal(
                fileName: fileName, confirm: true, project: projectFilter.isEmpty ? nil : projectFilter
            ).message
        } catch {
            recordServerMessage(from: error)
        }
    }

    // MARK: - Private helpers

    /// The shared tail of every simple action above: `ControlClientError.server(message:)` is the
    /// one failure case with server-authored text meant to be shown; every other case has nothing to
    /// render, so `lastActionMessage` is left exactly as it was rather than being cleared or
    /// replaced with Swift-invented text - same helper shape `ProjectsViewModel.
    /// recordServerMessage(from:)` already established.
    private func recordServerMessage(from error: ControlClientError) {
        if case .server(_, let message?) = error {
            lastActionMessage = message
        }
    }
}
