import HadesControl
import SwiftUI

/// Spec #3 §3.4's Memory surface (Asphodel). A master-detail split, same shape `ProjectsView`/
/// `TracesView` already established: the primary pane picks between `MemoryViewModel.documents` and
/// `.proposals` (a segmented `Picker`, the same UI-only "concepts with no API equivalent" chrome
/// `TracesView.ListSelection` already uses for Sequences/Failures/Slow - there is no separate
/// endpoint for documents vs proposals either; `GET /control/memory` already returns both together,
/// see `MemoryViewModel.refresh()`'s own doc comment); the detail pane is `MemoryDocumentView`,
/// scoped to whichever document is currently selected in the Documents list.
///
/// Proposals need no drill-down detail pane of their own - `ProposalQueueView`'s own rows already
/// show every field, `content` included, inline (see that view's own doc comment) - so the detail
/// pane only ever renders something while the Documents list is active; while Proposals is active it
/// falls back to the same "nothing selected" placeholder `ProjectsView`/`TracesView` already show.
///
/// `.id(selectedDocumentName)` on the `MemoryDocumentView` call below is deliberate: SwiftUI does
/// NOT reset a view's `@State` just because one of its plain parameters (`name`) changed value while
/// its identity in the tree stays put, and `MemoryDocumentView.draftContent` is exactly the kind of
/// state that must never leak from one document into another when the user clicks a different row.
///
/// **A project Picker, where there used to be no project-filter UI at all.** `viewModel.knownProjects`
/// (from `GET /control/projects`) populates it, and selecting writes straight through
/// `viewModel.selectProject(_:)` - the exact same shape `TracesView.projectPicker` uses, see that
/// view's own doc comment. `viewModel.refreshError` (rendered above everything else, same as
/// `TracesView`) is the other half of the same fix: with 2+ projects registered and none chosen yet,
/// `GET /control/memory` used to fail with a real, actionable server error that this view silently
/// swallowed, reading identically to "no memory yet" - now it surfaces verbatim instead.
struct MemoryView: View {
    let viewModel: MemoryViewModel
    @State private var selectedDocumentName: String?
    @State private var listSelection: ListSelection = .documents

    private enum ListSelection: String, CaseIterable, Hashable {
        case documents = "Documents"
        case proposals = "Proposals"
    }

    var body: some View {
        NavigationSplitView {
            content
        } detail: {
            if listSelection == .documents, let selectedDocumentName {
                MemoryDocumentView(name: selectedDocumentName, viewModel: viewModel)
                    .id(selectedDocumentName)
            } else {
                ContentUnavailableView("Select a Document", systemImage: "doc.text")
                    .foregroundStyle(.secondary)
            }
        }
        .onChange(of: selectedDocumentName) { _, newValue in
            if let newValue {
                Task { await viewModel.selectDocument(name: newValue) }
            } else {
                viewModel.clearSelectedDocument()
            }
        }
    }

    @ViewBuilder
    private var content: some View {
        VStack(spacing: 0) {
            // A response the shell cannot act on - most commonly "Hades knows 2 projects, so this
            // call needs a 'project' argument" before `knownProjects` has resolved a default, or a
            // project that no longer exists - rendered verbatim, exactly as `MemoryViewModel.
            // refreshError`'s own doc comment describes. Shown above everything else, same as
            // `TracesView`, so it cannot be missed regardless of which list is showing.
            if let refreshError = viewModel.refreshError {
                Label(refreshError, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .font(.callout)
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
            }

            // The most recent action's own result, verbatim - see `MemoryViewModel.
            // lastActionMessage`'s own doc comment. Shown here, above the split view, rather than
            // only in one of the two lists: a proposal action (Accept/Dismiss/Defer) and a document
            // save can each happen while the OTHER list is showing.
            if let message = viewModel.lastActionMessage {
                Text(message)
                    .font(.callout)
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
            }

            projectPicker

            Picker("List", selection: $listSelection) {
                ForEach(ListSelection.allCases, id: \.self) { selection in
                    Text(selection.rawValue).tag(selection)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(8)

            switch listSelection {
            case .documents: documentsList
            case .proposals: ProposalQueueView(viewModel: viewModel)
            }
        }
    }

    /// `viewModel.knownProjects` (`GET /control/projects`), verbatim - see `TracesView.projectPicker`'s
    /// own doc comment for the identical shape and reasoning; this is Memory's copy of it.
    private var projectPicker: some View {
        Picker(
            "Project",
            selection: Binding(
                get: { viewModel.projectFilter },
                set: { newValue in Task { await viewModel.selectProject(newValue) } }
            )
        ) {
            ForEach(viewModel.knownProjects, id: \.productGuid) { project in
                Text(project.name).tag(project.productGuid)
            }
        }
        .padding(.horizontal, 8)
    }

    @ViewBuilder
    private var documentsList: some View {
        if viewModel.documents.isEmpty {
            ContentUnavailableView(
                "No Memory Documents", systemImage: "doc.text.magnifyingglass",
                description: Text("Authored memory documents will appear here once Hades has some.")
            )
        } else {
            List(viewModel.documents, id: \.name, selection: $selectedDocumentName) { document in
                MemoryDocumentRowView(row: document)
            }
        }
    }
}
