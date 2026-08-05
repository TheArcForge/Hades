import HadesControl
import SwiftUI

/// One `MemoryProposalRow`, verbatim - name deliberately `MemoryProposalRowView`, not
/// `MemoryProposalRow` (that name is already `HadesControl`'s DTO), same collision-naming precedent
/// as `MemoryDocumentRowView`/`ProjectRowView`/`TraceSequenceRowView`.
///
/// A single self-contained row rather than a master-detail drill-down: `content` (which already
/// carries whatever "evidence and confidence" text an inferred convention has - e.g. "Observed in
/// 483 traces with 22% confidence.") is short markdown, not a full document, so showing it inline is
/// the same choice `ProjectWarningRow` already makes for `message`+`remedy` rather than requiring a
/// click-through. `targetFile`/`rationale` are only drawn when non-empty - `MemoryProposalRow`'s own
/// doc comment notes both are blank together on every `inferred` row in the real fixture, and an
/// empty `LabeledContent` would be a Swift-invented "nothing to show" placeholder for a fact the core
/// simply did not send.
///
/// **`status` is never switched on.** It is a plain string on the .NET side, not a closed enum (see
/// `MemoryProposalRow`'s own doc comment: a real capture already shows a value beyond
/// pending/accepted/deferred - `inferred`), so this row does not hide, relabel, or reorder itself
/// based on which value it happens to be - it is printed exactly like every other field, via
/// `LabeledContent`. For the same reason, **none of the three buttons below is ever disabled based
/// on `status`** - the exact "actions are never gated by Swift re-deriving eligibility from other
/// fields" discipline `ProjectDetailView`'s own doc comment holds to for `warnings`/`editor.state`:
/// the API's own `success`/`message` after the attempt is the only eligibility answer (e.g.
/// accepting an `inferred` cluster row, whose `targetFile` is `""`, comes back "Memory document name
/// must not be null or blank." - an honest, server-authored outcome, never a button Swift guessed
/// should be greyed out first).
struct MemoryProposalRowView: View {
    let proposal: MemoryProposalRow
    let viewModel: MemoryViewModel
    @State private var isConfirmingDismiss = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(proposal.fileName)
                .font(.subheadline.weight(.medium))
                .textSelection(.enabled)

            LabeledContent("Status", value: proposal.status)
                .font(.caption)
            if !proposal.targetFile.isEmpty {
                LabeledContent("Target", value: proposal.targetFile)
                    .font(.caption)
            }
            if let createdAgo = proposal.createdAgo {
                LabeledContent("Created", value: createdAgo)
                    .font(.caption)
            }
            if !proposal.rationale.isEmpty {
                Text(proposal.rationale)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Text(proposal.content)
                .font(.caption.monospaced())
                .textSelection(.enabled)

            actions
        }
        .padding(.vertical, 4)
    }

    /// Accept/Defer are single-click (neither is destructive - see this view's own doc comment on
    /// what each does). Dismiss confirms first, the same "confirmed is the gate itself, not a hint"
    /// discipline `ProjectsView`'s Remove button holds to - `MemoryViewModel.
    /// dismissProposal(fileName:confirmed:)` never reaches the network until this dialog's own
    /// Dismiss button sets `confirmed: true`.
    private var actions: some View {
        HStack {
            Button("Accept") {
                Task { await viewModel.acceptProposal(fileName: proposal.fileName) }
            }
            Button("Defer") {
                Task { await viewModel.deferProposal(fileName: proposal.fileName) }
            }
            Button("Dismiss\u{2026}", role: .destructive) {
                isConfirmingDismiss = true
            }
        }
        .confirmationDialog(
            "Dismiss \(proposal.fileName)?",
            isPresented: $isConfirmingDismiss,
            titleVisibility: .visible
        ) {
            Button("Dismiss", role: .destructive) {
                Task { await viewModel.dismissProposal(fileName: proposal.fileName, confirmed: true) }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            // "Dismissing a proposal deletes it" is the core's own wording (Hades.Server.Control.
            // MemoryEndpoint.DismissProposal's own 400 message when confirm=true is missing) -
            // carried over rather than inventing new copy; only the CLI-specific instruction
            // ("Pass confirm=true to proceed") is swapped for what this dialog's own Dismiss button
            // already is: the human equivalent of setting it.
            Text("Dismissing a proposal deletes it. This does not touch any memory/*.md document.")
        }
    }
}
