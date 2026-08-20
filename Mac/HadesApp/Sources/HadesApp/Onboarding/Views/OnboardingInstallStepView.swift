import SwiftUI

/// Step 1 - spec #4 §4: "download DMG, drag to Applications, launch." By the time this view is on
/// screen the user has already done exactly that (this window would not exist otherwise), so - per
/// Plan 14 Task 6's own instruction - this step is "mostly a welcome": fixed Swift-authored copy,
/// the same "no API equivalent, so literal Swift copy is fine" allowance `Section.title`/
/// `OnboardingStep.title` already document, not a rendered DTO field.
struct OnboardingInstallStepView: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Welcome to Hades")
                .font(.largeTitle.bold())
            Text(
                "Hades is installed and running. This short setup connects it to Claude Code and the Unity projects you want it to index — five steps, and you can stop after the fourth with a fully working setup."
            )
            .foregroundStyle(.secondary)
            Spacer()
        }
    }
}
