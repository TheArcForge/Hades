import SwiftUI

/// Step 5 - spec #4 §4: "explained as optional. Offered per project, with a plain statement of what
/// it adds: live scene and prefab editing, play mode, console, and test running." The sentence
/// below is that plain statement, verbatim from spec #4 §4 - fixed product copy describing a
/// capability, the same category as every other static button label or section header in this app,
/// not a rendered DTO field (there is no control-API endpoint that describes what the Unity plugin
/// does; this is product documentation, not server state).
///
/// **Genuinely optional, not just described that way.** "Finish" (`OnboardingRootView`'s own footer
/// button) calls the SAME `viewModel.advance()` every other step's "Continue" does - there is no
/// separate code path this view or `OnboardingViewModel` gates on any project actually having the
/// plugin installed. See `OnboardingViewModel`'s own class doc comment, and
/// `completingOnboardingNeverRequiresInstallingTheUnityPlugin` in `OnboardingViewModelTests` for the
/// proof.
struct OnboardingUnityPluginStepView: View {
    let viewModel: OnboardingViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Unity Plugin — Optional")
                .font(.largeTitle.bold())
            Text(
                "Everything you've set up already gives you a working Hades: graph queries, memory, and traces across every project you added. The Unity plugin is an upgrade, not a requirement — it adds live scene and prefab editing, play mode, console, and test running from inside Claude Code."
            )
            .foregroundStyle(.secondary)
            Text("Install it now per project below, or skip this and add it later from the Projects view. Either way, click Finish when you're done.")
                .foregroundStyle(.secondary)

            if viewModel.projectsViewModel.projects.isEmpty {
                ContentUnavailableView("No Projects Added", systemImage: "puzzlepiece.extension")
            } else {
                List(viewModel.projectsViewModel.projects, id: \.productGuid) { project in
                    HStack {
                        Text(project.name)
                        Spacer()
                        Button("Install Plugin") {
                            Task { await viewModel.projectsViewModel.installPlugin(productGuid: project.productGuid) }
                        }
                    }
                }
                .frame(minHeight: 140)
            }

            if let message = viewModel.projectsViewModel.lastActionMessage {
                Text(message)
                    .font(.callout)
                    .textSelection(.enabled)
            }

            Spacer()
        }
    }
}
