import HadesControl
import SwiftUI

/// One selected call's full span detail - `GET /control/traces/{traceId}`, resolved into
/// `TraceDetailFetchState` by `TracesViewModel.selectTrace(traceId:)`. Takes the fetch state
/// directly, not the whole `TracesViewModel`: this view has no actions of its own (Traces has no
/// POST endpoint at all - see `ControlTracesFetching`'s own doc comment), so it is a pure function of
/// "what is currently selected" - which also makes `state` a plain value, the same "rendered content
/// a test can assert" shape `MenuBarContent` and `ProjectsViewModel.projects` already are.
///
/// **The attributes/events trap - closed, Plan 13 Task 7 Step 0.** `SpanRow.attributes`/`events`
/// used to be untyped JSON (`ControlJSONValue`), and this view could only ever render the `.string`
/// leaves - every `.int`/`.double`/`.bool` (`resultSizeBytes`, a span's raw `timeUtcMs`, ...) was
/// silently invisible, because rendering them here would be Swift formatting numbers, exactly what
/// spec #3 §1 forbids (see the Plan 13 Task 5 report for this gap as originally found). The core now
/// pre-renders both into flat `[SpanAttributeRow]` lists - `key`/`valueDisplay` already resolved,
/// every leaf included - so this view has no rendering decision left to make at all: it prints
/// `valueDisplay` verbatim, per row, full stop.
struct TraceDetailView: View {
    let state: TraceDetailFetchState

    var body: some View {
        switch state {
        case .notSelected:
            ContentUnavailableView(
                "Select a Call", systemImage: "point.3.connected.trianglepath.dotted",
                description: Text("Choose a call from a sequence, or from Failures, to see its span detail.")
            )
        case .failed(let message):
            Text(message)
                .padding()
                .textSelection(.enabled)
        case .loaded(let detail):
            loaded(detail)
        }
    }

    private func loaded(_ detail: TraceDetailResult) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                header(detail)
                Divider()
                ForEach(detail.spans, id: \.spanId) { span in
                    spanView(span)
                }
            }
            .padding()
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func header(_ detail: TraceDetailResult) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(detail.tool)
                .font(.title2.bold())
            LabeledContent("Trace ID", value: detail.traceId)
                .textSelection(.enabled)
            if let durationMs = detail.durationMs {
                LabeledContent("Duration (ms)", value: "\(durationMs)")
            }
        }
    }

    private func spanView(_ span: SpanRow) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(span.name)
                .font(.headline)
            if let status = span.status {
                LabeledContent("Status", value: status)
            }
            LabeledContent("Kind", value: span.kind)
            if let durationMs = span.durationMs {
                LabeledContent("Duration (ms)", value: "\(durationMs)")
            }
            attributeRows(label: "Attributes", rows: span.attributes)
            attributeRows(label: "Events", rows: span.events)
        }
        .padding(.vertical, 4)
    }

    /// See this type's own doc comment on the attributes/events trap - every row is already fully
    /// resolved (`key`, `valueDisplay`) by the core, so this makes no rendering decision at all.
    @ViewBuilder
    private func attributeRows(label: String, rows: [SpanAttributeRow]?) -> some View {
        if let rows, !rows.isEmpty {
            VStack(alignment: .leading, spacing: 2) {
                Text(label)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                ForEach(rows, id: \.key) { row in
                    LabeledContent(row.key, value: row.valueDisplay)
                        .textSelection(.enabled)
                }
            }
        }
    }
}
