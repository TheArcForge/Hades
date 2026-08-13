import SwiftUI

/// Step 3 - spec #4 §4: "show `/plugin marketplace add TheArcForge/hades` and
/// `/plugin install hades`, then verify the server is reachable and reporting tools." The two
/// commands are fixed, literal CLI invocations - not control-API data, so printing them verbatim as
/// Swift string literals is not a spec #3 §1 violation (there is no DTO for a plugin-install command;
/// this is the guidance half spec #4 §2 asks for - "the app's onboarding SHOWS the install command").
/// The verification half is `viewModel.verifyClaudeCode()` - see `ClaudeCodeVerifying`'s own doc
/// comment for exactly what a `.reachable` result proves, and note that `reachableExplanation`
/// below states the "proves vs. assumes" distinction directly in the UI, not just in code comments.
///
/// **The launch-at-login toggle lives here, not on some later step.** Claude Code does not retry
/// an MCP server that was unreachable at session start - reconnecting needs `/mcp` plus an
/// explicit reconnect, or a whole new session (verified against Claude Code's own docs; see
/// `Documentation/InternalTesting-Install.md`'s "Known issues" section for the same note aimed at
/// testers). This step is exactly where a user is setting up that connection, so it is the moment
/// the fix - keeping Hades running in the background - is most worth surfacing, regardless of
/// whether `verifyClaudeCode()` above just succeeded or failed. Always visible, never gated on
/// verification: forgetting to start Hades before a session is exactly the "unreachable at session
/// start" case this exists to prevent NEXT time. The same `Binding(get:set:)` shape `SettingsView`'s
/// own "Launch Hades at Login" row already uses - this view only ever reads
/// `viewModel.launchAtLoginEnabled` and calls `viewModel.toggleLaunchAtLogin(to:)`, never touching
/// `LaunchAtLoginReading` itself (that boundary is `OnboardingViewModel`'s job - see its own
/// `toggleLaunchAtLogin(to:)` doc comment).
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

            Toggle(
                "Start Hades when you log in — recommended, so Claude Code always finds it",
                isOn: Binding(
                    get: { viewModel.launchAtLoginEnabled },
                    set: { viewModel.toggleLaunchAtLogin(to: $0) }
                )
            )

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
