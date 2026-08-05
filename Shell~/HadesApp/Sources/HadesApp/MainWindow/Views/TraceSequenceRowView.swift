import HadesControl
import SwiftUI

/// One `TraceSequenceRow`'s own summary - name deliberately `TraceSequenceRowView`, not
/// `TraceSequenceRow` (the plan's literal file name), because `TraceSequenceRow` is already
/// `HadesControl`'s DTO type; this follows the exact precedent `ProjectRowView` already set for
/// `SummaryRow`/`ProjectRow` (see that file's own doc comment) rather than shadowing an imported
/// type name - see the Plan 13 Task 5 report.
///
/// `row.pattern` is the complete, already arrow-joined tool sequence - THE thing that makes a
/// sequence legible, printed verbatim with no re-joining of `row.tools` here. `callCount`/
/// `durationMs` are single Int fields each printed alone via plain interpolation (never combined
/// into one string, never given an invented unit suffix beyond the label itself) - the same
/// discipline `ProjectDetailView`'s own `LabeledContent("Elapsed (seconds)", value: "\(n)")` already
/// holds to. `outcome`'s icon is a one-to-one `StatusIcon` mapping, same as every other severity/
/// outcome accent in this app - never text inspection.
struct TraceSequenceRowView: View {
    let row: TraceSequenceRow

    var body: some View {
        HStack(alignment: .top, spacing: 6) {
            Image(systemName: StatusIcon.symbolName(for: row.outcome))
                .foregroundStyle(outcomeColor)
                .imageScale(.small)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 2) {
                Text(row.pattern)
                    .font(.subheadline.weight(.medium))
                LabeledContent("Calls", value: "\(row.callCount)")
                LabeledContent("Duration (ms)", value: "\(row.durationMs)")
            }
        }
    }

    /// Identical mapping shape to `ProjectRowView.severityColor`/`ProjectWarningRow.severityColor` -
    /// duplicated, not shared, matching those views' own existing precedent.
    private var outcomeColor: Color {
        switch row.outcome {
        case .ok: return .green
        case .error: return .red
        case .unknown: return .gray
        }
    }
}
