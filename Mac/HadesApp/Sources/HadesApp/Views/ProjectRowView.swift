import HadesControl
import SwiftUI

/// One project's line in the dropdown. `row.project` and `row.status` are drawn exactly as the
/// control API sent them - no interpolation joining them into one new string, so neither field is
/// ever "built" by Swift, only laid out. The leading glyph's color is a one-to-one accent for
/// `row.severity` (parallel to `StatusIcon`'s own SF Symbol mapping) - styling, not text.
struct ProjectRowView: View {
    let row: SummaryRow

    var body: some View {
        HStack(alignment: .top, spacing: 6) {
            Image(systemName: StatusIcon.symbolName(for: row.severity))
                .foregroundStyle(severityColor)
                .imageScale(.small)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 1) {
                Text(row.project)
                    .font(.subheadline.weight(.medium))
                Text(row.status)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var severityColor: Color {
        switch row.severity {
        case .ok: return .green
        case .warning: return .yellow
        case .error: return .red
        case .unknown: return .gray
        }
    }
}
