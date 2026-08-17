import HadesControl
import SwiftUI

/// The per-item migration cleanup UI's four confirmation rows - one per `V12Cleanup` action.
/// `MigrationCleanupViewModel`/`SettingsViewModel` already hold a `proceed: false` dry-run preview
/// (or, once confirmed, the real result) fetched straight from the control API - see those types'
/// own doc comments for why that dry run, not Swift-authored text, is where every confirmation's
/// wording comes from. Every row below does nothing but print that state verbatim and gate a single
/// confirm button behind a `.confirmationDialog` that repeats the SAME already-fetched text - no
/// view here ever invents, paraphrases, or combines a warning of its own; the only Swift-authored
/// strings are neutral action labels/titles ("CLAUDE.md", "Clean Up…", "Remove the … entry from
/// claude_desktop_config.json?") that name what a control does, never why it matters or what its
/// consequences are - the same "title says what, message (API text) says why" split
/// `ProjectDetailView`'s own Remove confirmation already uses.
///
/// `result.removed` alone decides whether the button still shows: `true` is a completed action
/// (nothing further to confirm), `false` is still offered - including a real, confirmed attempt that
/// itself declined to remove anything (e.g. malformed JSON found between the preview and the
/// confirm), whose message says why, and whose button stays so the user can retry after fixing it
/// outside the app.
///
/// **`MigrationCleanClaudeMdRow`/`MigrationCleanManifestRow`/`MigrationCleanMcpConfigRow`** are used
/// only from `ProjectDetailView`'s "v1.2 Cleanup" section - each rendered exactly when
/// `MigrationCleanupViewModel`'s own detection-gated dictionary carries an entry for the current
/// project, never unconditionally ("the detection result drives what is offered").
///
/// **`MigrationCleanClaudeDesktopConfigRow`** is used only from `SettingsView` - the one action with
/// no `productGuid` anywhere in its signature, rendered on the one surface that is not project-scoped
/// at all. `SettingsView` gates it on `occurrencesFound > 0`, the only presence signal this global
/// route has (see `MigrationClaudeDesktopConfigCleanupResult.occurrencesFound`'s own doc comment for
/// why - this route has no companion per-project detect endpoint the other three targets get from
/// `MigrationDetectionResult`).
///
/// **`MigrationCleanHadesHubRow`** is the fifth such row, added to close the spec #4 §1 gap where
/// `~/.arcforge/hades-hub/launcher.js` (the retired v1.2 stdio launcher) was named among what v2
/// retires but no cleanup method ever removed it. Same shape and same surface as
/// `MigrationCleanClaudeDesktopConfigRow` immediately below, and for the identical reason:
/// `~/.arcforge/hades-hub/` is global and per-user, not per-project, so it is rendered from
/// `SettingsView` too, gated on `found`, the only presence signal this route has (see
/// `MigrationHadesHubCleanupResult.found`'s own doc comment).
struct MigrationCleanClaudeMdRow: View {
    let productGuid: String
    let result: MigrationClaudeMdCleanupResult
    let viewModel: MigrationCleanupViewModel
    @State private var isConfirming = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("CLAUDE.md")
                .font(.subheadline.weight(.medium))
            Text(result.message)
                .font(.callout)
                .textSelection(.enabled)
            if !result.removed {
                Button("Clean Up…") { isConfirming = true }
            }
            // The most recent CONFIRMED action's own server-authored failure text, verbatim - see
            // MigrationCleanupViewModel.lastActionMessage's own doc comment. Same presentation
            // idiom ProjectsView/MemoryView already use for their own lastActionMessage.
            if let message = viewModel.lastActionMessage {
                Text(message)
                    .font(.callout)
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
            }
        }
        .confirmationDialog(
            "Remove the HADES:START/END block from CLAUDE.md?",
            isPresented: $isConfirming,
            titleVisibility: .visible
        ) {
            Button("Remove", role: .destructive) {
                Task { await viewModel.cleanClaudeMd(productGuid: productGuid, confirmed: true) }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(result.message)
        }
    }
}

struct MigrationCleanManifestRow: View {
    let productGuid: String
    let result: MigrationManifestCleanupResult
    let viewModel: MigrationCleanupViewModel
    @State private var isConfirming = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Packages/manifest.json")
                .font(.subheadline.weight(.medium))
            Text(result.message)
                .font(.callout)
                .textSelection(.enabled)
            if !result.removed {
                Text(result.portConflictWarning)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                Button("Clean Up…") { isConfirming = true }
            }
            // The most recent CONFIRMED action's own server-authored failure text, verbatim - see
            // MigrationCleanupViewModel.lastActionMessage's own doc comment. Same presentation
            // idiom ProjectsView/MemoryView already use for their own lastActionMessage.
            if let message = viewModel.lastActionMessage {
                Text(message)
                    .font(.callout)
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
            }
        }
        .confirmationDialog(
            "Remove the com.arcforge.hades entry from Packages/manifest.json?",
            isPresented: $isConfirming,
            titleVisibility: .visible
        ) {
            Button("Remove", role: .destructive) {
                Task { await viewModel.cleanManifest(productGuid: productGuid, confirmed: true) }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            VStack(alignment: .leading) {
                Text(result.message)
                Text(result.portConflictWarning)
            }
        }
    }
}

struct MigrationCleanMcpConfigRow: View {
    let productGuid: String
    let result: MigrationMcpConfigCleanupResult
    let viewModel: MigrationCleanupViewModel
    @State private var isConfirming = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(".mcp.json")
                .font(.subheadline.weight(.medium))
            Text(result.message)
                .font(.callout)
                .textSelection(.enabled)
            if !result.removed {
                Button("Clean Up…") { isConfirming = true }
            }
            // The most recent CONFIRMED action's own server-authored failure text, verbatim - see
            // MigrationCleanupViewModel.lastActionMessage's own doc comment. Same presentation
            // idiom ProjectsView/MemoryView already use for their own lastActionMessage.
            if let message = viewModel.lastActionMessage {
                Text(message)
                    .font(.callout)
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
            }
        }
        .confirmationDialog(
            "Delete .mcp.json?",
            isPresented: $isConfirming,
            titleVisibility: .visible
        ) {
            Button("Delete", role: .destructive) {
                Task { await viewModel.cleanMcpConfig(productGuid: productGuid, confirmed: true) }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(result.message)
        }
    }
}

struct MigrationCleanClaudeDesktopConfigRow: View {
    let result: MigrationClaudeDesktopConfigCleanupResult
    let viewModel: SettingsViewModel
    @State private var isConfirming = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(result.message)
                .font(.callout)
                .textSelection(.enabled)
            if !result.removed {
                Text(result.scopeWarning)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .textSelection(.enabled)
                Button("Remove…", role: .destructive) { isConfirming = true }
            }
        }
        .confirmationDialog(
            "Remove the hades entry from claude_desktop_config.json?",
            isPresented: $isConfirming,
            titleVisibility: .visible
        ) {
            Button("Remove", role: .destructive) {
                Task { await viewModel.cleanClaudeDesktopConfig(confirmed: true) }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            VStack(alignment: .leading) {
                Text(result.message)
                Text(result.scopeWarning)
            }
        }
    }
}

struct MigrationCleanHadesHubRow: View {
    let result: MigrationHadesHubCleanupResult
    let viewModel: SettingsViewModel
    @State private var isConfirming = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(result.message)
                .font(.callout)
                .textSelection(.enabled)
            if !result.removed {
                Button("Remove…", role: .destructive) { isConfirming = true }
            }
        }
        .confirmationDialog(
            "Remove ~/.arcforge/hades-hub/?",
            isPresented: $isConfirming,
            titleVisibility: .visible
        ) {
            Button("Remove", role: .destructive) {
                Task { await viewModel.cleanHadesHub(confirmed: true) }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(result.message)
        }
    }
}
