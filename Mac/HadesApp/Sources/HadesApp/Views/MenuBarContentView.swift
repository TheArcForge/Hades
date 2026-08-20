import HadesControl
import HadesSupervision
import SwiftUI

/// The dropdown's content, switching on `MenuBarContent` - and nothing else. Every `Text` in this
/// file that renders API data reads a `SummaryResult`/`SummaryRow`/`SummaryLease` field verbatim;
/// the only literal Swift copy is for the three supervision-only cases (`notRunning`/
/// `restarting`/`failed`), which by construction have no control-API response to read from at all
/// (see `MenuBarContent`'s own doc comment). No formatter, no string interpolation joining two API
/// fields together, no severity-vs-severity comparison to decide what to show.
struct MenuBarContentView: View {
    let content: MenuBarContent
    let onRelease: (String) -> Void
    let onOpenHades: () -> Void
    let onQuit: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            switch content {
            case .notRunning:
                Label("Hades is not running", systemImage: StatusIcon.symbolName(for: content))

            case .restarting(let attempt):
                Label("Restarting Hades \u{2014} attempt \(attempt)", systemImage: StatusIcon.symbolName(for: content))

            case .failed(let attempts):
                VStack(alignment: .leading, spacing: 4) {
                    Label("Hades failed to start", systemImage: StatusIcon.symbolName(for: content))
                        .foregroundStyle(.red)
                    Text("Gave up after \(attempts) attempts.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

            case .running(let ownership, let summary):
                RunningContentView(ownership: ownership, summary: summary, onRelease: onRelease)
            }

            Divider()

            // Spec #3 §3.1, verbatim: the dropdown gives "...a jump to the main window, and quit."
            // Always available regardless of `content` - opening the window (Task 2's own shell,
            // still empty of Projects/Traces/Memory data until Tasks 3/5/6 land) does not depend on
            // a core running any more than quitting does.
            HStack {
                Spacer()
                Button("Open Hades", action: onOpenHades)
                Button("Quit Hades", action: onQuit)
                    .keyboardShortcut("q")
            }
        }
        .padding(12)
        .frame(width: 300, alignment: .leading)
    }
}

/// The `.running` case's content: the headline first and most prominent (always drawn, verbatim -
/// this is true whether or not a lease is held, since `headline` is the control API's own one-line
/// summary of "what's going on" in every state), the Release button directly beneath it ONLY when
/// `summary.lease` is present (net #7 of the reload-safety design), then per-project rows, then
/// the supervision footer.
private struct RunningContentView: View {
    let ownership: CoreSupervisor.Ownership
    let summary: SummaryResult
    let onRelease: (String) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 6) {
                Label(summary.headline, systemImage: StatusIcon.symbolName(for: summary.iconState))
                    .font(.headline)

                if let lease = summary.lease {
                    Button("Release", action: { onRelease(lease.leaseId) })
                        .disabled(!lease.releasable)
                }
            }

            if !summary.rows.isEmpty {
                Divider()
                VStack(alignment: .leading, spacing: 6) {
                    // Keyed by productGuid, never project (the display name): two different
                    // projects can share a name (e.g. two checkouts of the same repo), and keying
                    // on name alone collided them into one row - see SummaryRow's own doc comment.
                    ForEach(summary.rows, id: \.productGuid) { row in
                        ProjectRowView(row: row)
                    }
                }
            }

            Divider()
            SupervisionFooterView(ownership: ownership)
        }
    }
}
