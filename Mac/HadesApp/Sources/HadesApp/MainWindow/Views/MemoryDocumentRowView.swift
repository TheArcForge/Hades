import HadesControl
import SwiftUI

/// One `MemoryDocumentRow`'s compact row - name deliberately `MemoryDocumentRowView`, not
/// `MemoryDocumentRow` (that name is already `HadesControl`'s DTO), following the exact precedent
/// `ProjectRowView`/`TraceSequenceRowView` already set for `SummaryRow`/`TraceSequenceRow` rather
/// than shadowing an imported type name.
///
/// `sizeDisplay` is the already human-readable size ("500 B", "2.0 KB") - printed exactly as sent,
/// never re-derived from `sizeBytes` (see that property's own doc comment: Swift never converts
/// `sizeBytes` itself). `lastReviewed` is a bare verbatim `Text` when present, never wrapped in
/// Swift-authored words - the same "two individually verbatim fields, never joined into one string"
/// discipline `ProjectsView`'s own compact row already holds to for `project.name`/`project.path`.
struct MemoryDocumentRowView: View {
    let row: MemoryDocumentRow

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(row.name)
                .font(.subheadline.weight(.medium))
            HStack(spacing: 8) {
                Text(row.sizeDisplay)
                if let lastReviewed = row.lastReviewed {
                    Text(lastReviewed)
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
    }
}
