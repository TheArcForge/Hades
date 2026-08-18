import Foundation
import Testing

@testable import HadesApp

/// Release blocker #3: proves the one pure part of the Intel refuse-to-launch gate. See
/// `ArchitectureGate`'s own doc comment for why `decide(for:)` is deliberately the only testable
/// part of this path - this machine is Apple Silicon, so there is no way to run this suite AS the
/// x86_64 slice and observe a real Intel launch end-to-end. `decide(for:)` exists precisely so the
/// decision itself does not need real Intel hardware, or an actual x86_64 build, to prove correct;
/// `HadesMenuBarAppTests`' own doc comment gives the matching reason `presentUnsupportedArchitectureAlertAndExit`
/// itself is not unit tested (a real modal session and a real `exit(0)`, not a fake-able seam).
@Suite("ArchitectureGate")
struct ArchitectureGateTests {

    @Test(
        "each executing architecture maps to its own fixed decision - Apple Silicon proceeds, Intel refuses with the exact hand-typed message",
        arguments: [
            (ExecutingArchitecture.appleSilicon, ArchitectureGate.Decision.proceed),
            (
                ExecutingArchitecture.intel,
                ArchitectureGate.Decision.refuse(message: ArchitectureGate.unsupportedMessage)
            ),
        ]
    )
    func decisionForArchitecture(slice: ExecutingArchitecture, expected: ArchitectureGate.Decision) {
        #expect(ArchitectureGate.decide(for: slice) == expected)
    }

    /// The exact facts release blocker #3 itself asks for, plus the "nothing to fix" framing -
    /// checked as separate substring assertions (not one long `==` against a re-typed copy) so this
    /// test still catches a future edit that drifts to quietly drop one of them, not just one that
    /// changes the wording elsewhere.
    @Test("the Intel message names Apple Silicon as the requirement, names Intel as what this Mac has, and says there is nothing to fix")
    func intelMessageCoversTheRequiredFacts() {
        let message = ArchitectureGate.unsupportedMessage

        #expect(message.contains("Apple Silicon"))
        #expect(message.contains("Intel"))
        #expect(message.contains("nothing to install or change"))
    }

    /// Non-technical, per release blocker #3's own instruction: no build/architecture jargon a
    /// non-technical user would not recognise, and no mention of Rosetta specifically - naming it
    /// would read as a workaround worth trying when none exists for a real Intel Mac (see
    /// `ArchitectureGate.currentSlice`'s own doc comment for why).
    @Test(
        "the Intel message stays non-technical and implies no workaround",
        arguments: ["arm64", "x86_64", "Rosetta", "universal binary", "slice"]
    )
    func intelMessageStaysNonTechnical(jargon: String) {
        #expect(!ArchitectureGate.unsupportedMessage.localizedCaseInsensitiveContains(jargon))
    }
}
