import SwiftUI

/// Step 3 - spec #4 §4: "show `/plugin marketplace add TheArcForge/hades` and
/// `/plugin install hades`, then verify the server is reachable and reporting tools." The two
/// commands are fixed, literal CLI invocations - not control-API data, so printing them verbatim as
/// Swift string literals is not a spec #3 §1 violation (there is no DTO for a plugin-install command;
/// this is the guidance half spec #4 §2 asks for - "the app's onboarding SHOWS the install command").
/// The verification half is `viewModel.verifyClaudeCode()` - see `ClaudeCodeVerifying`'s own doc
/// comment for exactly what a `.reachable` result proves, and note that `reachableExplanation`
/// below states the "proves vs. assumes" distinction directly in the UI, not just in code comments.
struct OnboardingClaudeCodeStepView: View {
    let viewModel: OnboardingViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Connect Claude Code")
                .font(.largeTitle.bold())
            Text("Run these two commands in Claude Code:")
                .foregroundStyle(.secondary)

            VStack(alignment: .leading, spacing: 6) {
                Text("/plugin marketplace add TheArcForge/hades")
                    .font(.system(.body, design: .monospaced))
                    .textSelection(.enabled)
                Text("/plugin install hades")
                    .font(.system(.body, design: .monospaced))
                    .textSelection(.enabled)
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.quaternary, in: RoundedRectangle(cornerRadius: 8))

            Button("Verify") {
                Task { await viewModel.verifyClaudeCode() }
            }

            verificationStatus
            Spacer()
        }
    }

    @ViewBuilder
    private var verificationStatus: some View {
        switch viewModel.claudeCodeVerification {
        case .notVerified:
            EmptyView()
        case .verifying:
            ProgressView("Checking…")
        case .reachable(let toolCount):
            Label(
                "Hades is running and reporting \(toolCount) tools at the address the plugin uses. "
                    + "This confirms the core is ready — it doesn't confirm Claude Code has connected yet.",
                systemImage: "checkmark.circle.fill"
            )
            .foregroundStyle(.green)
        case .unreachable:
            Label("Hades didn't respond. Make sure the app is running, then try again.", systemImage: "xmark.circle.fill")
                .foregroundStyle(.red)
        }
    }
}
