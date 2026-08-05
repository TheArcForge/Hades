import HadesControl
import SwiftUI

/// One project's full detail - every `ProjectRow` field `ProjectsView`'s compact list row does not
/// already show (name, path), printed verbatim: Unity version, index state and freshness, node/edge
/// counts, attached-Editor state, and every warning (via `ProjectWarningRow`) - plus, per Task 4,
/// every action that targets THIS project: rebuild, install/update plugin, reveal in Finder, open
/// in Unity, and remove. Per spec #3 §1 ("Swift renders, .NET decides"), nothing here combines two
/// fields into one string, formats a number, or invents display text: `nodeCount`/`edgeCount`/
/// `processId`/`connectionAgeSeconds` are `Int`/`Int?` printed via plain string interpolation
/// (never SwiftUI's locale-formatting `Text(_:)` integer initializer) - the exact same decimal
/// digits the JSON carried, not a formatted number. A field that is `Optional` on `ProjectRow`/
/// `ProjectEditorInfo` is a row that is simply not drawn when absent - never a Swift-invented
/// placeholder like "Unknown" standing in for a fact the core did not send, the same discipline
/// `MenuBarContentView` already holds to for `summary.lease` (`if let`, not a substitute string).
///
/// **None of the five buttons below are ever disabled based on `project.warnings`/`editor.state`.**
/// `GET /control/projects` exposes no per-action eligibility field to gate on (confirmed by reading
/// `Hades.Server.Control.ProjectsEndpoint.cs`: none of its six actions reference `LeaseRegistry` or
/// any other precondition beyond "does this productGuid exist"), so Swift has nothing to derive
/// eligibility FROM - see this project's Plan 13 Task 4 report. Every button always calls its
/// action; a project the action cannot actually complete against (a missing path, an unattached
/// Editor) is reported back via that action's own `success: false` + `message`, rendered verbatim
/// by `ProjectsViewModel.lastActionMessage` - never guessed at ahead of time.
struct ProjectDetailView: View {
    let project: ProjectRow
    let viewModel: ProjectsViewModel
    @State private var isConfirmingRemove = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                header
                Divider()
                actions
                if let progress = viewModel.rebuildProgress[project.productGuid] {
                    Divider()
                    rebuildProgressView(progress)
                }
                Divider()
                indexingFacts
                Divider()
                editorFacts
                if !project.warnings.isEmpty {
                    Divider()
                    warnings
                }
            }
            .padding()
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(project.name)
                .font(.title2.bold())
            Text(project.path)
                .font(.caption)
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
        }
    }

    /// The five actions that target THIS project - see this type's own doc comment for why none is
    /// ever preemptively disabled. `Task { await ... }` is the standard SwiftUI shape for calling
    /// an `async` view-model method from a synchronous `Button` action - none of these five awaits
    /// its own completion before returning control to the button tap.
    private var actions: some View {
        HStack {
            Button("Rebuild") {
                Task { await viewModel.rebuildProject(productGuid: project.productGuid) }
            }
            Button("Install/Update Plugin") {
                Task { await viewModel.installPlugin(productGuid: project.productGuid) }
            }
            Button("Reveal in Finder") {
                Task { await viewModel.revealInFinder(productGuid: project.productGuid) }
            }
            Button("Open in Unity") {
                Task { await viewModel.openInUnity(productGuid: project.productGuid) }
            }
            Button("Remove…", role: .destructive) {
                isConfirmingRemove = true
            }
        }
        // Remove confirms first - see `ProjectsViewModel.removeProject(productGuid:confirmed:)`'s
        // own doc comment for why `confirmed` is enforced there too, not only by this dialog
        // existing. The message states plainly what removing actually does: nothing is deleted
        // from disk at all - confirmed by reading `Hades.Core.Projects.ProjectStore.Remove` and
        // `ProjectsEndpoint.Remove` (a `Removed` flag rewritten into project.json, never a file
        // deletion) - so this wording matches the real, fully non-destructive behaviour, not the
        // more cautious-sounding "derived files are deleted, authored memory is kept" phrasing a
        // skim of this app's OWN storage-layer doc comments (`AppPaths.GraphDb`/`MemoryDir`) might
        // suggest; that distinction is real, but it is not what THIS action does today.
        .confirmationDialog(
            "Remove \(project.name) from Hades?",
            isPresented: $isConfirmingRemove,
            titleVisibility: .visible
        ) {
            Button("Remove", role: .destructive) {
                Task { await viewModel.removeProject(productGuid: project.productGuid, confirmed: true) }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(
                "This only removes \(project.name) from Hades \u{2014} nothing is deleted from disk. Its indexed graph, traces, and authored memory all remain untouched, and you can add it again later."
            )
        }
    }

    /// A polled rebuild's own state, verbatim - see `OperationProgress`'s own doc comment.
    /// `.pruned` is rendered exactly like a normal completion, never with error styling: the server
    /// itself says this is ordinary ("may have completed and been pruned"), so nothing here treats
    /// it as one.
    @ViewBuilder
    private func rebuildProgressView(_ progress: OperationProgress) -> some View {
        switch progress {
        case .tracked(let result):
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    Image(systemName: StatusIcon.symbolName(for: result.state))
                        .accessibilityHidden(true)
                    Text("Rebuild")
                        .font(.headline)
                }
                LabeledContent("Elapsed (seconds)", value: "\(result.elapsedSeconds)")
                if let progressText = result.progress {
                    Text(progressText)
                }
                if let error = result.error {
                    Text(error)
                }
                if let resultMessage = result.result?["message"]?.stringValue {
                    Text(resultMessage)
                }
            }
        case .pruned(let message):
            VStack(alignment: .leading, spacing: 4) {
                Text("Rebuild")
                    .font(.headline)
                Text(message)
            }
        }
    }

    private var indexingFacts: some View {
        VStack(alignment: .leading, spacing: 4) {
            if let unityVersion = project.unityVersion {
                LabeledContent("Unity Version", value: unityVersion)
            }
            LabeledContent("Index Status", value: project.indexStatus)
            LabeledContent("Nodes", value: "\(project.nodeCount)")
            LabeledContent("Edges", value: "\(project.edgeCount)")
        }
    }

    private var editorFacts: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Editor")
                .font(.headline)
            LabeledContent("Status", value: project.editor.status)
            if let unityVersion = project.editor.unityVersion {
                LabeledContent("Unity Version", value: unityVersion)
            }
            if let processId = project.editor.processId {
                LabeledContent("Process ID", value: "\(processId)")
            }
            if let connectionAgeSeconds = project.editor.connectionAgeSeconds {
                LabeledContent("Connection Age (seconds)", value: "\(connectionAgeSeconds)")
            }
        }
    }

    private var warnings: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Warnings")
                .font(.headline)
            // Index-based id, not `\.code`: today's `BuildWarnings` never emits two warnings with
            // the same code, but nothing SHOULD depend on that for a stable identity when this list
            // is never mutated in place (it is a fixed snapshot for as long as `project` is
            // unchanged) - see `ProjectWarning`'s own doc comment on `code` being reserved for
            // future values, not a guaranteed-unique key.
            ForEach(Array(project.warnings.enumerated()), id: \.offset) { _, warning in
                ProjectWarningRow(warning: warning)
            }
        }
    }
}
