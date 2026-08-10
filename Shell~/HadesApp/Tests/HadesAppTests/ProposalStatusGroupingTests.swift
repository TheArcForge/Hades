import Foundation
import HadesControl
import Testing

@testable import HadesApp

/// `groupProposalsByStatus(_:)` is the one piece of view-layer logic `ProposalQueueView` uses to
/// turn `MemoryViewModel.proposals` - a flat array, in whatever order `GET /control/memory`
/// returned it (`Hades.Core.Memory.MemoryProposals.OrderForReview` already puts every non-
/// "inferred" status ahead of "inferred", keeping equal statuses contiguous - see that method's
/// own doc comment) - into one `Section` per contiguous same-`status` run. A pure function, tested
/// here as a plain value: this app has no SwiftUI snapshot test infrastructure, so
/// `ProposalQueueView` itself never does more than call this function and iterate its result.
///
/// **This is grouping, not classification.** No test below asserts anything about what a status
/// VALUE means - only that consecutive equal values land in the same group, in the order given,
/// exactly as many groups as there are runs. Same "no re-derivation" contract `StatusIconTests`/
/// `ThermalStateDisplayTests` hold their own subjects to: expected groups below are built from
/// hand-typed rows, never by re-running the function under test against itself.
@Suite("groupProposalsByStatus")
struct ProposalStatusGroupingTests {

    static func row(_ fileName: String, status: String) -> MemoryProposalRow {
        MemoryProposalRow(
            fileName: fileName, targetFile: "", createdAtUtc: nil, createdAgo: nil,
            rationale: "", status: status, content: "")
    }

    @Test("no proposals produces no groups")
    func empty() {
        #expect(groupProposalsByStatus([]) == [])
    }

    @Test("a single proposal produces a single group carrying just that row")
    func singleProposal() {
        let proposal = Self.row("a.md", status: "pending")

        #expect(groupProposalsByStatus([proposal]) == [ProposalStatusGroup(status: "pending", proposals: [proposal])])
    }

    @Test("consecutive proposals sharing a status collapse into one group, in the given order")
    func consecutiveSameStatus() {
        let a = Self.row("a.md", status: "inferred")
        let b = Self.row("b.md", status: "inferred")
        let c = Self.row("c.md", status: "inferred")

        #expect(groupProposalsByStatus([a, b, c]) == [ProposalStatusGroup(status: "inferred", proposals: [a, b, c])])
    }

    @Test("a change in status starts a new group - the real 'authored ahead of analyzer' shape .NET's own ordering produces")
    func statusChangeStartsNewGroup() {
        let pendingA = Self.row("convention-naming.md", status: "pending")
        let pendingB = Self.row("convention-render_pipeline.md", status: "pending")
        let inferredA = Self.row("topic_cluster-1.md", status: "inferred")
        let inferredB = Self.row("acceptance_rate-1.md", status: "inferred")

        let groups = groupProposalsByStatus([pendingA, pendingB, inferredA, inferredB])

        #expect(
            groups == [
                ProposalStatusGroup(status: "pending", proposals: [pendingA, pendingB]),
                ProposalStatusGroup(status: "inferred", proposals: [inferredA, inferredB]),
            ])
    }

    @Test(
        "the SAME status appearing in two non-adjacent runs stays two separate groups - grouping is by consecutive run, never a full re-sort or re-partition by key"
    )
    func sameStatusNonContiguousStaysSeparate() {
        let firstPending = Self.row("a.md", status: "pending")
        let inferred = Self.row("b.md", status: "inferred")
        let secondPending = Self.row("c.md", status: "pending")

        let groups = groupProposalsByStatus([firstPending, inferred, secondPending])

        #expect(
            groups == [
                ProposalStatusGroup(status: "pending", proposals: [firstPending]),
                ProposalStatusGroup(status: "inferred", proposals: [inferred]),
                ProposalStatusGroup(status: "pending", proposals: [secondPending]),
            ])
    }

    @Test("a blank status - the real corpus has had exactly this shape (see Hades.Core.Memory.MemoryProposalsTests) - groups without crashing")
    func blankStatusDoesNotCrash() {
        let malformed = Self.row("20260614-174745-.md", status: "")
        let inferred = Self.row("topic_cluster-1.md", status: "inferred")

        let groups = groupProposalsByStatus([malformed, inferred])

        #expect(
            groups == [
                ProposalStatusGroup(status: "", proposals: [malformed]),
                ProposalStatusGroup(status: "inferred", proposals: [inferred]),
            ])
    }

    @Test("nothing is lost - every input row appears in exactly one output group, verbatim, in order")
    func nothingLost() {
        let rows = (0..<8).map { Self.row("\($0).md", status: $0 < 3 ? "pending" : "inferred") }

        let groups = groupProposalsByStatus(rows)

        #expect(groups.flatMap(\.proposals) == rows)
    }
}
