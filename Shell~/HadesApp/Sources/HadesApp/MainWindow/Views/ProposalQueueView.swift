import SwiftUI

/// Spec #3 §3.4's promotion-proposal queue - "replacing `/hades:show-proposals` as the primary
/// surface." Renders `MemoryViewModel.proposals` in the exact order `GET /control/memory` returned
/// it - never re-sorted, grouped, or filtered by `status` here (see `MemoryProposalRowView`'s own
/// doc comment for why: `status` is not a closed enum, so any Swift-authored bucketing risks quietly
/// mis-routing a future value). This one flat list is simultaneously the "proposal queue with
/// Accept/Dismiss/Defer" AND the "inferred conventions with their evidence and confidence" spec #3
/// §3.4 asks for: both are the same `proposals` array, just rows whose own `content` happens to read
/// differently depending on which analyzer or human action produced them - there is no second
/// endpoint to draw a second view from.
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
            List(viewModel.proposals, id: \.fileName) { proposal in
                MemoryProposalRowView(proposal: proposal, viewModel: viewModel)
            }
        }
    }
}
