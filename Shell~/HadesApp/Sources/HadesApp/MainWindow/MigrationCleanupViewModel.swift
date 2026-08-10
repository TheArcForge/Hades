import HadesControl
import Observation

/// Owns the three `{productGuid}`-scoped `V12Cleanup` actions (`cleanClaudeMd`, `cleanManifest`,
/// `cleanMcpConfig`) for the per-item migration cleanup UI - rendered inside `ProjectDetailView`'s
/// "v1.2 Cleanup" section, the same "each section owns its own view model and its own fetch" split
/// `ProjectsViewModel`/`TracesViewModel`/`MemoryViewModel` already establish. The fourth,
/// GLOBAL `cleanClaudeDesktopConfig` action deliberately does NOT live here - see
/// `SettingsViewModel`'s own doc comment for why it lives there instead, and why that split is not
/// an accident: this type's every method takes a `productGuid`, and `cleanClaudeDesktopConfig` has
/// no `productGuid` parameter anywhere on its route to take.
///
/// **Where confirmation text comes from.** Spec #3 §1 is "Swift renders, .NET decides" - but
/// `V12Cleanup`'s result records only ever carried `Message` (and the manifest/global targets'
/// own warning fields) as the OUTCOME of an action already taken, so the wording a user must read
/// BEFORE agreeing did not exist anywhere on the wire. Rather than author that wording in Swift -
/// exactly the "Swift inventing warnings about behaviour V12Cleanup owns" this task's own brief
/// forbids - `loadProjectState(productGuid:)` calls each applicable cleanup route with
/// `proceed: false` first: a real, already-tested, non-destructive dry run
/// (`V12CleanupTests.NoGoAhead_AcrossAllFourTargets_NothingOnDiskChangesAtAll`) that returns the
/// exact same `Message`/`PortConflictWarning` fields a real removal would, without writing
/// anything. `MigrationCleanupViews.swift` renders that dry-run response verbatim in each action's
/// confirmation dialog; only once the user explicitly agrees does `cleanClaudeMd`/`cleanManifest`/
/// `cleanMcpConfig` below call the SAME route again with `proceed: true`.
///
/// One gap this task found and closed in `Hades.Core.Migration.V12Cleanup` itself:
/// `CleanClaudeMd`'s dry run never used to compute `RemainingContentOutsideBlock` at all (only the
/// real removal did), so the one fact a user must know before agreeing - that unmarked content like
/// the reference project's own ~60 stale lines will survive - was unavailable pre-action. Fixed
/// there, not papered over here with a client-side guess.
///
/// **Detection gates what is offered.** `loadProjectState(productGuid:)` only dry-runs a target
/// whose `MigrationDetectionResult` field says it is actually present (`claudeMd.shape == .marked`,
/// `manifestEntry.present`, `hasGeneratedMcpConfig`) - "do not offer to clean a file that is not
/// there." `claudeMdState`/`manifestState`/`mcpConfigState` are keyed by `productGuid`, mirroring
/// `ProjectsViewModel.rebuildProgress`'s own per-project dictionary shape: switching projects in the
/// sidebar never mixes one project's offered cleanup state into another's.
///
/// **Every action is independently authorised and independently failable.** `confirmed: Bool` is
/// the actual gate, enforced here (not just trusted of whatever SwiftUI dialog sets it) - the same
/// discipline `ProjectsViewModel.removeProject(productGuid:confirmed:)` already holds to. A failed
/// confirm (e.g. a stale productGuid) leaves that ONE target's dictionary entry exactly as it was
/// (self-heals, still offered, still retryable) and surfaces the server's own message via
/// `lastActionMessage` - it never touches the other two targets' own state.
@MainActor
@Observable
public final class MigrationCleanupViewModel {
    /// `productGuid` -> the most recent dry-run preview OR real result for `cleanClaudeMd`, keyed
    /// per project. Present only when `loadProjectState` last saw `claudeMd.shape == .marked` for
    /// this project - absence IS the "do not offer" signal `MigrationCleanClaudeMdRow` reads.
    public private(set) var claudeMdState: [String: MigrationClaudeMdCleanupResult] = [:]

    /// `productGuid` -> the most recent dry-run preview OR real result for `cleanManifest`. Present
    /// only when `loadProjectState` last saw `manifestEntry.present`.
    public private(set) var manifestState: [String: MigrationManifestCleanupResult] = [:]

    /// `productGuid` -> the most recent dry-run preview OR real result for `cleanMcpConfig`.
    /// Present only when `loadProjectState` last saw `hasGeneratedMcpConfig`.
    public private(set) var mcpConfigState: [String: MigrationMcpConfigCleanupResult] = [:]

    /// The most recent CONFIRMED action's server-authored failure text, verbatim - same shape and
    /// same reasoning as `ProjectsViewModel.lastActionMessage`'s own doc comment: a transport/
    /// staleToken/decoding failure (or a `.server` failure with no message) leaves this exactly as
    /// it was, never Swift-invented text. A successful confirm (even one that itself reports
    /// `Removed: false`, e.g. "manifest.json is not valid JSON; refusing to modify it.") is NOT an
    /// error - that message lands in the relevant `*State` dictionary instead, rendered verbatim
    /// there, the same as any other result.
    public private(set) var lastActionMessage: String?

    private let discover: ConnectionProvider
    private let makeClient: MigrationClientFactory

    public init(
        discover: @escaping ConnectionProvider = { Discovery.read() },
        makeClient: @escaping MigrationClientFactory = { ControlClient(connection: $0) }
    ) {
        self.discover = discover
        self.makeClient = makeClient
    }

    /// Detects, then dry-runs (`proceed: false`) whichever of the three targets detection reports
    /// present for `productGuid` - never polled, called once per project shown (`ProjectDetailView`'s
    /// own `.task(id: project.productGuid)`), the same "user-initiated, never-polled" shape
    /// `MemoryViewModel.selectDocument(name:)` already establishes for a single-project fetch.
    ///
    /// A target detection reports ABSENT is removed from that dictionary outright (not merely left
    /// alone) - if a previous load had it, and the underlying file is now genuinely gone (e.g. the
    /// user already cleaned it up and revisited), there is nothing left to offer.
    ///
    /// Self-heals on a `migrationDetect` failure: none of the three dry runs can be gated correctly
    /// without a fresh detection result, so this returns early and leaves every dictionary exactly
    /// as it was, the same self-heal discipline every other view model's `refresh()` already holds
    /// to. A single dry-run call failing after a successful detect self-heals the SAME way, scoped
    /// to only that one target - the other two (if detection also offered them) still update.
    public func loadProjectState(productGuid: String) async {
        guard let connection = await discover() else { return }
        let client = makeClient(connection)

        let detection: MigrationDetectionResult
        do {
            detection = try await client.migrationDetect(productGuid: productGuid)
        } catch {
            return
        }

        if detection.claudeMd.shape == .marked {
            if let result = try? await client.migrationCleanClaudeMd(productGuid: productGuid, proceed: false) {
                claudeMdState[productGuid] = result
            }
        } else {
            claudeMdState.removeValue(forKey: productGuid)
        }

        if detection.manifestEntry.present {
            if let result = try? await client.migrationCleanManifest(productGuid: productGuid, proceed: false) {
                manifestState[productGuid] = result
            }
        } else {
            manifestState.removeValue(forKey: productGuid)
        }

        if detection.hasGeneratedMcpConfig {
            if let result = try? await client.migrationCleanMcpConfig(productGuid: productGuid, proceed: false) {
                mcpConfigState[productGuid] = result
            }
        } else {
            mcpConfigState.removeValue(forKey: productGuid)
        }
    }

    // MARK: - Actions - each independently authorised, each independently failable

    /// The ONLY path that ever calls `migrationCleanClaudeMd(proceed: true)`. `confirmed` is the
    /// actual gate: `false` never reaches the network, matching every other destructive action's
    /// own confirmation contract in this app.
    public func cleanClaudeMd(productGuid: String, confirmed: Bool) async {
        guard confirmed else { return }
        guard let connection = await discover() else { return }
        do {
            claudeMdState[productGuid] = try await makeClient(connection).migrationCleanClaudeMd(productGuid: productGuid, proceed: true)
        } catch {
            recordServerMessage(from: error)
        }
    }

    /// The ONLY path that ever calls `migrationCleanManifest(proceed: true)`.
    public func cleanManifest(productGuid: String, confirmed: Bool) async {
        guard confirmed else { return }
        guard let connection = await discover() else { return }
        do {
            manifestState[productGuid] = try await makeClient(connection).migrationCleanManifest(productGuid: productGuid, proceed: true)
        } catch {
            recordServerMessage(from: error)
        }
    }

    /// The ONLY path that ever calls `migrationCleanMcpConfig(proceed: true)`.
    public func cleanMcpConfig(productGuid: String, confirmed: Bool) async {
        guard confirmed else { return }
        guard let connection = await discover() else { return }
        do {
            mcpConfigState[productGuid] = try await makeClient(connection).migrationCleanMcpConfig(productGuid: productGuid, proceed: true)
        } catch {
            recordServerMessage(from: error)
        }
    }

    // MARK: - Private helpers

    /// The shared tail of every confirm action above - identical to
    /// `ProjectsViewModel.recordServerMessage(from:)`'s own doc comment: `.server` with a message is
    /// the one failure case meant to be shown; everything else self-heals silently.
    private func recordServerMessage(from error: ControlClientError) {
        if case .server(_, let message?) = error {
            lastActionMessage = message
        }
    }
}
