import HadesControl
import SwiftUI

/// Step 4 - spec #4 §4: "offer Unity Hub discovery, or add manually. Indexing starts immediately and
/// reports progress; nothing blocks." Adding is `viewModel.addProject(path:)`, which delegates
/// straight to `ProjectsViewModel.addProject(path:)` - the SAME call `ProjectsView`'s own toolbar
/// button makes - so "indexing starts immediately and reports progress" is not re-implemented here:
/// it is `ProjectsViewModel.projects`' existing `indexState`/`indexStatus` fields, already polled
/// and already rendered verbatim, exactly like `ProjectsView` renders them.
///
/// **Unity Hub discovery is not offered here.** `SettingsView`'s own doc comment already names why:
/// "Hub discovery is 'its own piece of work' with no discovery mechanism built anywhere in this
/// codebase yet" (confirmed again for this task - `App~/src/Hades.Server/Control/SettingsEndpoint.cs`
/// and `Program.cs` both still list it as future work). Inventing a client-side scan of Unity Hub's
/// own project list would both re-derive logic spec #1 §5.3 assigns to `.NET` and violate spec #3
/// §1 the same way reaching around the migration API gap would - see the Task 6 report for this
/// second, related gap. The text below says so plainly instead of showing a button that does
/// nothing.
///
/// **The migration offer** (spec #4 §5, offered when `V12Detector` fires) renders here too,
/// directly beneath the project list, whenever `viewModel.migrationOfferedProjectPath` is non-nil -
/// see `MigrationOffering`'s own doc comment for the real `LiveMigrationOffering` conformance that
/// populates it (Plan 14 Task 10).
struct OnboardingProjectsStepView: View {
    let viewModel: OnboardingViewModel
    let directoryPicker: any DirectoryPicking

    init(viewModel: OnboardingViewModel, directoryPicker: any DirectoryPicking = NSOpenPanelDirectoryPicker()) {
        self.viewModel = viewModel
        self.directoryPicker = directoryPicker
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Add Projects")
                .font(.largeTitle.bold())
            Text(
                "Add the Unity projects you want Hades to index. Indexing starts right away and continues in the background — nothing here waits on it."
            )
            .foregroundStyle(.secondary)
            Text("Unity Hub discovery isn't available yet — add projects manually for now.")
                .font(.caption)
                .foregroundStyle(.secondary)

            Button("Add Project…") {
                if let path = directoryPicker.pickDirectory() {
                    Task { await viewModel.addProject(path: path) }
                }
            }

            if viewModel.projectsViewModel.projects.isEmpty {
                ContentUnavailableView("No Projects Yet", systemImage: "folder.badge.plus")
            } else {
                List(viewModel.projectsViewModel.projects, id: \.productGuid) { project in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(project.name).font(.subheadline.weight(.medium))
                        Text(project.indexStatus).font(.caption).foregroundStyle(.secondary)
                    }
                }
                .frame(minHeight: 140)
            }

            if let path = viewModel.migrationOfferedProjectPath {
                migrationOfferBanner(for: path)
            }

            Spacer()
        }
    }

    @ViewBuilder
    private func migrationOfferBanner(for path: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("This looks like a v1.2 Hades project.")
                .font(.headline)
            Text(path)
                .font(.caption)
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
            Text("Hades can migrate it into this app. Nothing changes until you confirm.")
                .foregroundStyle(.secondary)
            HStack {
                Button("Migrate…") { Task { await viewModel.confirmMigration() } }
                Button("Not Now") { viewModel.declineMigration() }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary, in: RoundedRectangle(cornerRadius: 8))
    }
}
