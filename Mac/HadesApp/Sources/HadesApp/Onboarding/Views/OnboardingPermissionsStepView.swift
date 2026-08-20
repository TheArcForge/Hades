import SwiftUI

/// Step 2 - spec #4 §4: "macOS will ask for access to the folders Hades watches. The app explains
/// why before the prompt: it indexes Unity projects on disk." Plan 14 Task 6's own instruction is
/// explicit about ordering: "Explain why before the prompt fires, not after." This view is that
/// explanation - fixed, Swift-authored copy shown strictly before step 4 (Projects) can ever trigger
/// the actual macOS permission prompt (a project is only ever added from the Projects step, which
/// comes later in `OnboardingStep`'s fixed order - see that type's own doc comment). There is
/// nothing to trigger from Swift here: the prompt itself is a macOS-owned side effect of a later
/// file access, never something this view (or any code in this task) requests directly - see the
/// Task 6 report for why.
struct OnboardingPermissionsStepView: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Folder Access")
                .font(.largeTitle.bold())
            Text(
                "macOS will ask permission to access files on your Mac. Hades needs this to index the Unity projects you add in the next steps — it reads project files on disk to build the graph Claude Code queries."
            )
            .foregroundStyle(.secondary)
            Text("Nothing leaves your machine: indexing runs entirely locally, and the prompt only covers the folders you choose to add.")
                .foregroundStyle(.secondary)
            Spacer()
        }
    }
}
