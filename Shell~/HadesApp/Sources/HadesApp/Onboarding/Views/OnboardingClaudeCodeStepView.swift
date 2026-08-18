import SwiftUI

/// Step 3 - spec #4 §4 originally called for showing `/plugin marketplace add TheArcForge/hades`
/// and `/plugin install hades`, then verifying the server is reachable and reporting tools. This
/// view no longer shows that marketplace command. Two problems, found this round: the slug itself
/// was wrong (`TheArcForge/hades` - every other reference in this repo, e.g.
/// `.github/workflows/release.yml`, `scripts/plugin-README.md`, `Documentation/ReleasePipeline.md`
/// §1/§4, uses `TheArcForge/hades-plugin`), and even with the slug fixed, that marketplace still
/// serves the retired v1.2 plugin (~89 tools, Node stdio launcher) as of this internal testing
/// round - see `Documentation/ReleasePipeline.md` §4 "Current install paths" and the same warning
/// in `Documentation/InternalTesting-Install.md` / `scripts/plugin-README.md`. Onboarding is the
/// first thing a new user sees, so handing them a command that silently installs the wrong,
/// retired plugin generation here is worse than the same mistake sitting in a doc a user might
/// never open.
///
/// Shown instead: `claude --plugin-dir <checkout>/Plugin-ClaudeCode~` - the path both of those
/// docs already point testers at today. Still a fixed, literal CLI invocation, so printing it
/// verbatim as a Swift string literal is not a spec #3 §1 violation for the same reason the
/// marketplace commands weren't (there is no DTO for a plugin-install command; this is the
/// guidance half spec #4 §2 asks for - "the app's onboarding SHOWS the install command"). The
/// marketplace path is named in passing, not offered as a command to run, until
/// `Documentation/ReleasePipeline.md` §5/§8 confirms `TheArcForge/hades-plugin` has actually been
/// resynced to this plugin at release - flip the command shown below back to the marketplace form
/// then, not before.
///
/// The verification half is unchanged: `viewModel.verifyClaudeCode()` - see `ClaudeCodeVerifying`'s
/// own doc comment for exactly what a `.reachable` result proves, and note that `reachableExplanation`
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
            Text("Run this in Terminal to start Claude Code with the Hades plugin loaded:")
                .foregroundStyle(.secondary)

            Text("claude --plugin-dir <path-to-your-Hades-checkout>/Plugin-ClaudeCode~")
                .font(.system(.body, design: .monospaced))
                .textSelection(.enabled)
                .padding(12)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.quaternary, in: RoundedRectangle(cornerRadius: 8))

            Text(
                "Replace the path with wherever you checked out the Hades repo. This loads the plugin for that session only — pass the flag every time you start claude. A persistent marketplace install is coming once TheArcForge/hades-plugin is republished for this release."
            )
            .font(.caption)
            .foregroundStyle(.secondary)

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
