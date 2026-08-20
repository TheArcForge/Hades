import SwiftUI

/// Step 3 - spec #4 §4: show the plugin install command, then verify the server is reachable and
/// report its tool count.
///
/// **The marketplace command is the one shown, as of the 2.0.0 release.** For one internal-testing
/// round this view deliberately showed `claude --plugin-dir <checkout>/ClaudeCodePlugin` instead,
/// because `TheArcForge/hades-plugin` still served the retired v1.2 plugin (~89 tools, Node stdio
/// launcher) and handing a new user a command that silently installs the wrong generation is worse
/// in onboarding than in a doc they might never open. That comment set an explicit flip condition -
/// "back to the marketplace form once the plugin repo has actually been resynced at release" - and
/// this is that flip: pushing tag `vX.Y.Z` runs `.github/workflows/release.yml`, which syncs
/// `ClaudeCodePlugin/` to that repo (`Documentation/ReleasePipeline.md` §8.5).
///
/// `--plugin-dir` remains, demoted to the contributor route, because **the DMG user has no
/// checkout**: leading with a `<path-to-your-Hades-checkout>` placeholder asked the ordinary user
/// to substitute a directory they had never cloned. README.md and `install.sh` already lead with
/// the marketplace for that reason; this view was the last place that did not, which made the app
/// itself the odd one out.
///
/// Both are fixed, literal CLI invocations, so printing them verbatim as Swift string literals is
/// not a spec #3 §1 violation (there is no DTO for a plugin-install command; this is the guidance
/// half spec #4 §2 asks for - "the app's onboarding SHOWS the install command").
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
            Text("Run these two commands inside a Claude Code session:")
                .foregroundStyle(.secondary)

            Text("/plugin marketplace add TheArcForge/hades-plugin\n/plugin install hades")
                .font(.system(.body, design: .monospaced))
                .textSelection(.enabled)
                .padding(12)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.quaternary, in: RoundedRectangle(cornerRadius: 8))

            Text(
                "This installs the plugin permanently — you don't repeat it per session. Afterwards run /mcp and confirm hades reports 32 tools."
            )
            .font(.caption)
            .foregroundStyle(.secondary)

            Text("Working from a clone of the Hades repo instead? Start Claude Code with the plugin directory directly — this one is per-session, so pass it every time:")
                .font(.caption)
                .foregroundStyle(.secondary)

            Text("claude --plugin-dir <your-Hades-checkout>/ClaudeCodePlugin")
                .font(.system(.caption, design: .monospaced))
                .textSelection(.enabled)
                .padding(8)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.quaternary, in: RoundedRectangle(cornerRadius: 6))

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
