import HadesControl
import SwiftUI

/// One document's read/edit/save pane - `GET`+`POST /control/memory/document`, resolved into
/// `MemoryViewModel.selectedDocument` by `selectDocument(name:)`. Takes the fetch state via the view
/// model directly (same shape `TraceDetailView` takes `TraceDetailFetchState`), and `name` only to
/// know which document `MemoryView` wants shown - see `MemoryView`'s own doc comment on why it also
/// applies `.id(name)` at the call site, so switching documents gets a fresh `@State` instance rather
/// than this view's own draft text leaking from one document into another.
///
/// **Editing is entirely local `@State` until Save is confirmed.** `draftContent` is seeded exactly
/// once per document (`seedDraftIfNeeded`) from `selectedDocument`'s own verbatim `content` - never
/// re-synced afterward, so `refresh()` ticking `documents`/`proposals` elsewhere in `MemoryViewModel`
/// can never silently clobber an in-progress edit (it does not touch `selectedDocument` at all - see
/// that property's own doc comment). Saving posts `draftContent` byte for byte; nothing here
/// reformats, trims, or otherwise touches what the user typed.
///
/// **Save always confirms first.** Per this project's Task 6 brief: memory documents are authored
/// and irreplaceable, and a save through this view always OVERWRITES a document this pane already
/// read - there is no "create new" flow here, only "open an existing document, edit, save" (spec #3
/// §3.4's own words) - so every save from this view is, by construction, replacing existing content.
/// `MemoryViewModel.saveDocument(name:content:confirmed:)` enforces the same gate again beneath this
/// dialog, so a call site that forgot to confirm still cannot reach the network.
struct MemoryDocumentView: View {
    let name: String
    let viewModel: MemoryViewModel
    @State private var draftContent: String = ""
    @State private var hasLoadedDraft = false
    @State private var isConfirmingSave = false

    var body: some View {
        content
            .onAppear { seedDraftIfNeeded(from: viewModel.selectedDocument) }
            .onChange(of: viewModel.selectedDocument) { _, newState in
                seedDraftIfNeeded(from: newState)
            }
    }

    @ViewBuilder
    private var content: some View {
        switch viewModel.selectedDocument {
        case .notSelected:
            ContentUnavailableView("Select a Document", systemImage: "doc.text")
        case .failed(let message):
            Text(message)
                .padding()
                .textSelection(.enabled)
        case .loaded(let document):
            editor(document)
        }
    }

    private func editor(_ document: MemoryDocumentResult) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(document.name)
                    .font(.title2.bold())
                Spacer()
                Button("Save\u{2026}") { isConfirmingSave = true }
            }
            TextEditor(text: $draftContent)
                .font(.body.monospaced())
                .border(Color.secondary.opacity(0.3))
        }
        .padding()
        .confirmationDialog(
            "Save changes to \(document.name)?",
            isPresented: $isConfirmingSave,
            titleVisibility: .visible
        ) {
            Button("Save", role: .destructive) {
                Task { await viewModel.saveDocument(name: document.name, content: draftContent, confirmed: true) }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(
                "This replaces the current contents of \(document.name) on disk and cannot be undone. Memory documents are authored and have no other copy."
            )
        }
    }

    /// Seeds `draftContent` from `selectedDocument` exactly once per document - see this type's own
    /// doc comment for why a later `selectedDocument` change (there is none from `refresh()`, but
    /// `onChange` is here defensively for the `.notSelected` -> `.loaded` transition itself, which
    /// DOES fire once when `selectDocument(name:)`'s own fetch completes after this view already
    /// appeared) must not re-seed and discard whatever the user has already typed.
    private func seedDraftIfNeeded(from state: MemoryDocumentFetchState) {
        guard !hasLoadedDraft, case .loaded(let document) = state else { return }
        draftContent = document.content
        hasLoadedDraft = true
    }
}
