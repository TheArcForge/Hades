import HadesControl
import SwiftUI

/// Spec #3 §3.2's primary view: every known project, rendered from `ProjectsViewModel.projects` -
/// `ProjectRow` verbatim, nothing combined or derived. Reads `viewModel.projects` directly (an
/// `@Observable` property access inside `body`, the same no-property-wrapper-needed pattern
/// `MenuBarRootView` already uses for `MenuBarViewModel`), so this redraws on every poll tick with
/// no Combine/`@Published` plumbing needed.
///
/// A master-detail split: the list shows only `name`/`path` (both individually verbatim, never
/// joined into one line - `GET /control/projects` has no pre-combined display string the way
/// `SummaryRow.status` is for the menu bar, so concatenating `indexStatus`/`editor.status` here
/// would be exactly the "combining fields" spec #3 §1 forbids); every other field - Unity version,
/// index state and freshness, node/edge counts, attached-Editor state, warnings - AND every action
/// on the currently-selected project (remove/rebuild/installPlugin/revealInFinder/openInUnity) are
/// `ProjectDetailView`'s job. `addProject` is the one action this view owns directly: it has no
/// selected project to act on, and `directoryPicker` (Task 4's `NSOpenPanel` seam - see
/// `DirectoryPicking`'s own doc comment) is the one place the shell chooses a path at all.
struct ProjectsView: View {
    let viewModel: ProjectsViewModel
    let directoryPicker: any DirectoryPicking
    @State private var selectedProductGuid: String?

    init(viewModel: ProjectsViewModel, directoryPicker: any DirectoryPicking = NSOpenPanelDirectoryPicker()) {
        self.viewModel = viewModel
        self.directoryPicker = directoryPicker
    }

    var body: some View {
        NavigationSplitView {
            list
                // Wider than SwiftUI's own default sidebar ideal, same fix Traces/Memory already
                // made for their own sidebars (see `TracesView`'s own doc comment) - the default
                // left the detail pane doing almost all the work at the window's own default size,
                // most of it empty. Narrower than Traces/Memory's own 360/460/640 (chosen for a
                // filter block + segmented picker + multi-line sequence patterns): a project row is
                // only ever `name` + `path` on two short lines, so it does not need nearly as much
                // room to read well. The divider stays user-draggable either way - this only changes
                // the default.
                .navigationSplitViewColumnWidth(min: 220, ideal: 300, max: 400)
        } detail: {
            if let selected {
                ProjectDetailView(project: selected, viewModel: viewModel)
            } else {
                Text("Select a project")
                    .foregroundStyle(.secondary)
            }
        }
        .toolbar {
            ToolbarItem {
                Button("Add Project…") {
                    if let path = directoryPicker.pickDirectory() {
                        Task { await viewModel.addProject(path: path) }
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var list: some View {
        VStack(spacing: 0) {
            // The most recent action's own result, verbatim - see `ProjectsViewModel.
            // lastActionMessage`'s own doc comment. Shown here, above the split view, rather than
            // only in ProjectDetailView: `addProject` (this view's own action) has no selected
            // project for a detail pane to attach a message to.
            if let message = viewModel.lastActionMessage {
                Text(message)
                    .font(.callout)
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
            }

            if viewModel.projects.isEmpty {
                ContentUnavailableView("No Projects", systemImage: "folder.badge.questionmark")
            } else {
                // Selection is driven by the `id:` keypath below, matched against
                // `selectedProductGuid` directly - no `.tag()` needed (that modifier matters for a
                // bare `ForEach` inside `List(selection:)`, not for this data+id+selection
                // initializer).
                List(viewModel.projects, id: \.productGuid, selection: $selectedProductGuid) { project in
                    VStack(alignment: .leading, spacing: 1) {
                        Text(project.name)
                            .font(.subheadline.weight(.medium))
                        Text(project.path)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            // Truncate the HEAD, not the tail (SwiftUI's default): the leaf folder
                            // at the end is the most identifying part of a path, and two projects
                            // that only differ deep in a shared prefix (e.g. two /tmp/hades-e2e-...
                            // scratch checkouts) are indistinguishable if the tail is what gets
                            // dropped. `project.path` itself is untouched - this only changes which
                            // end `Text` elides when the column is too narrow for the whole string.
                            .lineLimit(1)
                            .truncationMode(.head)
                    }
                }
            }
        }
    }

    private var selected: ProjectRow? {
        viewModel.projects.first { $0.productGuid == selectedProductGuid }
    }
}
