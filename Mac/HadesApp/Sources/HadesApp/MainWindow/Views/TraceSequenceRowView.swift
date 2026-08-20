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
///
/// **`pattern` wraps, and the row grows to fit - `.lineLimit(8)` is the fix, not `.fixedSize`.**
/// Spec #3 §3.3 requires sequences to be legible enough to tell apart - a 2-call and a 48-call
/// sequence that happen to share their first few tool names must not render identically. Two prior
/// passes both landed on `.fixedSize(horizontal: false, vertical: true)` alone, and both shipped a
/// row that still rendered every sequence at the exact same height regardless of call count - proved
/// live, in the running app (1-, 2-, 14-, 42- and 48-call sequences all measuring identical; this is
/// not something a unit test can see at all, since none of them render a real `List`). Tested live,
/// each in isolation, against the real app: removing `DisclosureGroup` entirely changed nothing;
/// adding `.frame(maxWidth: .infinity, alignment: .leading)` changed nothing; only adding an explicit
/// `.lineLimit` changed anything. Something in this `List`/`NavigationSplitView` sidebar context
/// caps `Text` to one line independent of `.fixedSize` - which candidate specifically was not
/// isolated further than that, since `.lineLimit` is the one modifier both necessary and sufficient
/// and further narrowing would not change what this view does. `.fixedSize(horizontal: false,
/// vertical: true)` is left in place as a harmless "don't compress me vertically" hint (it predates
/// this fix and is not the mechanism, but it is not wrong either).
///
/// **Capped at 8 lines, not unlimited.** `.lineLimit(nil)` was tried first and did work - a 48-call
/// `pattern` wraps to roughly 19 lines at this column's ~460pt width, unambiguously taller than a
/// 2-call row's one line - but it also let a single sequence occupy most of the visible list, which
/// reintroduces a legibility problem of its own (a list where one row swamps the rest is not
/// "legible" either). 8 lines keeps this project's real sequences (1-2 calls: 1 line; 14 calls: ~6
/// lines, fully visible and uncapped; 42-48 calls: capped at 8, clearly the "long" tier) sorted into
/// visibly distinct tiers without any single row dominating the window - the two longest sequences
/// (42 and 48 calls) both hit the cap and render the same height as each other, which is the
/// deliberate trade-off, not an oversight. `pattern` itself is never shortened to reach this: the cap
/// is `Text`'s own rendering, spec #1's "Swift may not shorten it" constraint about the underlying
/// `String` never applies to how many of its own lines `Text` chooses to draw - `row.pattern` passed
/// to `Text` above is the same complete, un-substringed value it always was.
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
                    .fixedSize(horizontal: false, vertical: true)
                    .lineLimit(8)
                    .textSelection(.enabled)
                    // Selecting text near the cap (e.g. triple-click, which selects the whole
                    // block) paints a highlight sized for the FULL, uncapped layout - a 42-call
                    // pattern wants ~19 lines - not the 8 actually drawn, so without clipping the
                    // highlight rectangle bleeds down past this Text's own bounds and over
                    // `Calls`/`Duration (ms)` below it. `.clipped()` confines the highlight (and
                    // anything else) to the frame `.lineLimit(8)` already computed; `row.pattern`
                    // itself is unchanged, this only clips how the selection decoration paints.
                    .clipped()
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
