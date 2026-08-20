import HadesControl
import SwiftUI

/// One `ProjectWarning`, verbatim - `message`/`remedy` are the complete, human-readable strings to
/// print with no interpolation joining them together. `severity` picks the accent via the SAME
/// one-to-one `StatusIcon` mapping `ProjectRowView` already uses for `SummaryRow.severity` - not a
/// new mapping, not text inspection. `code` is a plain internal identifier (see `ProjectWarning`'s
/// own doc comment - reserved for a future `"oracleConformanceMismatch"` value) and is never shown,
/// the same way `SummaryLease.leaseId` is used programmatically elsewhere in this app and never
/// printed.
struct ProjectWarningRow: View {
    let warning: ProjectWarning

    var body: some View {
        HStack(alignment: .top, spacing: 6) {
            Image(systemName: StatusIcon.symbolName(for: warning.severity))
                .foregroundStyle(severityColor)
                .imageScale(.small)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 2) {
                Text(warning.message)
                    .font(.callout)
                Text(warning.remedy)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    /// Identical mapping to `ProjectRowView.severityColor` - duplicated, not shared, matching that
    /// view's own existing precedent rather than introducing a new shared helper for two call sites.
    private var severityColor: Color {
        switch warning.severity {
        case .ok: return .green
        case .warning: return .yellow
        case .error: return .red
        case .unknown: return .gray
        }
    }
}
