import HadesControl
import SwiftUI

/// One contiguous run of proposals sharing the same `status`, in the order `GET /control/memory`
/// returned them - the plain, testable value `ProposalQueueView` renders as one `Section`. See
/// `groupProposalsByStatus(_:)`'s own doc comment for what "contiguous" means and why grouping by
/// literal equality is not the classification spec #3 §1 forbids the shell from doing.
struct ProposalStatusGroup: Equatable {
    let status: String
    let proposals: [MemoryProposalRow]
}

/// Chunks `proposals` into contiguous same-`status` runs, preserving order exactly as received -
/// never re-sorted here (see `ProposalQueueView`'s own doc comment: ordering is entirely
/// `Hades.Core.Memory.MemoryProposals.OrderForReview`'s job, which already keeps every equal-status
/// run contiguous for exactly this reason). This is grouping, not classification: the function
/// never asks what a `status` VALUE means, only whether two CONSECUTIVE rows share the same one -
/// the same "print the field verbatim" discipline `MemoryProposalRowView` already holds to for the
/// per-row `Status` caption, extended here to a section header. A free function, not a `View`,
/// specifically so a test can assert the grouping as a plain value - this app has no SwiftUI
/// snapshot test infrastructure (see `ProposalStatusGroupingTests`).
func groupProposalsByStatus(_ proposals: [MemoryProposalRow]) -> [ProposalStatusGroup] {
    var groups: [ProposalStatusGroup] = []
    for proposal in proposals {
        if let last = groups.last, last.status == proposal.status {
            groups[groups.count - 1] = ProposalStatusGroup(status: last.status, proposals: last.proposals + [proposal])
        } else {
            groups.append(ProposalStatusGroup(status: proposal.status, proposals: [proposal]))
        }
    }
    return groups
}

/// Spec #3 §3.4's promotion-proposal queue - "replacing `/hades:show-proposals` as the primary
/// surface." Renders `MemoryViewModel.proposals` in the exact order `GET /control/memory` returned
/// it, grouped into one `Section` per contiguous same-`status` run (`groupProposalsByStatus(_:)`)
/// so the handful of proposals a person would actually act on read as visually distinct from
/// dozens of analyzer-generated rows beneath them - `Hades.Core.Memory.MemoryProposals.
/// OrderForReview` already puts every non-"inferred" status ahead of "inferred" and keeps equal
/// statuses contiguous for exactly this reason.
///
/// Still never re-sorted, re-filtered, or classified here (see `MemoryProposalRowView`'s own doc
/// comment for why: `status` is not a closed enum, so any Swift-authored bucketing risks quietly
/// mis-routing a future value) - grouping by literal equality on an already-server-ordered array,
/// with the group's own header printing that same literal string verbatim, is the one operation
/// this view performs beyond what it always did; it would group and label any future status value
/// exactly as readily as the ones known today, the same "honest, un-invented" text every other
/// label in this view already uses (`(no status)` for a blank one is the one fallback here, and
/// it fires on emptiness alone, never on a specific value - the same "condition on absence, never
/// on meaning" precedent `MemoryProposalRowView` already sets for `targetFile`/`rationale`).
///
/// This one flat list is simultaneously the "proposal queue with Accept/Dismiss/Defer" AND the
/// "inferred conventions with their evidence and confidence" spec #3 §3.4 asks for: both are the
/// same `proposals` array, just rows whose own `content` happens to read differently depending on
/// which analyzer or human action produced them - there is no second endpoint to draw a second
/// view from.
struct ProposalQueueView: View {
    let viewModel: MemoryViewModel

    var body: some View {
        if viewModel.proposals.isEmpty {
            // Same fix, same reason as `TracesView`'s empty states (see that type's own doc
            // comment): without a greedy frame here, `MemoryView`'s `Project` picker and
            // Documents/Proposals control above this view jump down whenever there are no
            // proposals, the same way Traces' filter block used to.
            ContentUnavailableView(
                "No Proposals", systemImage: "tray",
                description: Text("Promotion proposals and inferred conventions will appear here once Hades has some.")
            )
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            List {
                // `id: \.offset`, not `\.status`: two non-adjacent groups can legitimately share a
                // `status` (see `groupProposalsByStatus(_:)`'s own "non-contiguous stays separate"
                // test) - SwiftUI's `ForEach` requires a unique id per element regardless, and the
                // group's position is always unique even when its label is not.
                ForEach(Array(groupProposalsByStatus(viewModel.proposals).enumerated()), id: \.offset) { _, group in
                    SwiftUI.Section(group.status.isEmpty ? "(no status)" : group.status) {
                        ForEach(group.proposals, id: \.fileName) { proposal in
                            MemoryProposalRowView(proposal: proposal, viewModel: viewModel)
                        }
                    }
                }
            }
        }
    }
}
