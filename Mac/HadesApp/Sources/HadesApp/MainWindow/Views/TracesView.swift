import HadesControl
import SwiftUI

/// Spec #3 §3.3's Traces surface (Charon) - replaces the old `Dashboard~` Express UI. Renders
/// `TracesViewModel`'s three independently-fetched lists - `sequences`, `failures`, `slowTools` -
/// EACH surfaced from its own endpoint, never one filtered client-side out of another (see
/// `TracesViewModel.refresh()`'s own doc comment, and `Hades.Server.Control.TracesEndpoint`'s own
/// class doc comment on why sequences/failures/slow are three separate queries, not one).
///
/// **Sequences are the primary timeline, not an afterthought.** `GET /control/traces/sequences` is
/// the ONLY traces endpoint that accepts project/tool/outcome/duration filters at all (see
/// `ControlTracesFetching`) - there is no separate "flat list of every call" endpoint in this API. A
/// `TraceSequenceRow`'s own `tools`/`traceIds` (parallel arrays - see that DTO's own doc comment) ARE
/// the individual calls, in order; each sequence row below expands (`DisclosureGroup`) to list every
/// one of them as its own selectable row, feeding the SAME detail pane a Failures row does. That is
/// how this one view satisfies both "timeline of tool calls, filter by project/tool/outcome/duration"
/// AND "sequences legible, not just individual calls" - the API itself does not separate those two
/// asks into two different shapes, so neither does this view.
///
/// A master-detail split, same shape `ProjectsView` already established: the primary pane picks
/// which of the three lists is showing, filters it, and lets the user select a call; `TraceDetailView`
/// renders whichever call is currently selected, independent of which list it was selected from -
/// always by re-fetching `GET /control/traces/{traceId}` fresh (`TracesViewModel.selectTrace`), never
/// by reading fields back off the row that was clicked.
///
/// Filter controls are plain View-local `@State`, pushed into the view model only when "Apply
/// Filters" is tapped (`TracesViewModel.applyFilters`) - the same "viewmodel state is written only
/// through an intent-revealing method, never a raw two-way binding" discipline `ProjectsView`/
/// `ProjectDetailView` already hold to for `addProject(path:)`/`removeProject(productGuid:confirmed:)`
/// and friends. The one exception is the project Picker (`projectPicker` below): it writes through
/// `viewModel.selectProject(_:)` directly, with no local `@State` buffer at all - see that method's
/// own doc comment for why a Picker applies immediately rather than waiting for "Apply Filters".
///
/// **A Picker, not free text, for project.** `viewModel.knownProjects` (from `GET /control/projects`)
/// populates it - a typo in a free-text project field used to produce a mystery empty state
/// indistinguishable from "tracing is on, nothing has been called yet" (see `viewModel.refreshError`
/// below for the other half of that fix: a project the API genuinely cannot resolve now surfaces
/// verbatim instead of silently self-healing to nothing).
struct TracesView: View {
    let viewModel: TracesViewModel
    @State private var selectedTraceId: String?
    @State private var listSelection: ListSelection = .sequences

    @State private var toolFilterText: String = ""
    @State private var outcomeSelection: OutcomeFilter = .all
    @State private var minDurationText: String = ""
    @State private var maxDurationText: String = ""

    private enum ListSelection: String, CaseIterable, Hashable {
        case sequences = "Sequences"
        case failures = "Failures"
        case slow = "Slow"
    }

    /// UI-only filter chrome, same "literal Swift copy is fine for concepts with no API equivalent"
    /// allowance `Section.title` already documents - `queryValue` is the exact raw string
    /// `ControlTracesFetching.tracesSequences(outcome:)` accepts, never a value read back off the API
    /// and re-labelled.
    private enum OutcomeFilter: String, CaseIterable, Hashable {
        case all = "All"
        case ok = "OK"
        case error = "Error"

        var queryValue: String? {
            switch self {
            case .all: return nil
            case .ok: return "ok"
            case .error: return "error"
            }
        }
    }

    var body: some View {
        NavigationSplitView {
            content
                // Wider than SwiftUI's own default sidebar ideal - see `TraceSequenceRowView`'s own
                // doc comment for the layout half of the sequence-legibility fix this pairs with:
                // wrapping alone still means a long `pattern` needs many lines at a narrow width,
                // and this is the one column-width knob Task 5 left at the default. A wider default
                // here means fewer wrapped lines per sequence at the window's own default size,
                // without the user ever dragging the divider - spec #3 §3.3's own bar.
                .navigationSplitViewColumnWidth(min: 360, ideal: 460, max: 640)
        } detail: {
            TraceDetailView(state: viewModel.selectedTraceDetail)
        }
        .onChange(of: selectedTraceId) { _, newValue in
            if let newValue {
                Task { await viewModel.selectTrace(traceId: newValue) }
            } else {
                viewModel.clearSelectedTrace()
            }
        }
    }

    @ViewBuilder
    private var content: some View {
        VStack(spacing: 0) {
            // A response the shell cannot act on - most commonly "Hades knows 2 projects, so this
            // call needs a 'project' argument" before `knownProjects` has resolved a default, or a
            // project that no longer exists - rendered verbatim, exactly as `TracesViewModel.
            // refreshError`'s own doc comment describes. Shown above everything else so it cannot be
            // missed regardless of which of the three lists below is showing.
            if let refreshError = viewModel.refreshError {
                Label(refreshError, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .font(.callout)
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
            }
            filters
            Divider()
            Picker("List", selection: $listSelection) {
                ForEach(ListSelection.allCases, id: \.self) { selection in
                    Text(selection.rawValue).tag(selection)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(8)

            switch listSelection {
            case .sequences: sequencesList
            case .failures: failuresList
            case .slow: slowList
            }
        }
    }

    private var filters: some View {
        VStack(alignment: .leading, spacing: 6) {
            projectPicker
            TextField("Tool contains\u{2026}", text: $toolFilterText)
            Picker("Outcome", selection: $outcomeSelection) {
                ForEach(OutcomeFilter.allCases, id: \.self) { option in
                    Text(option.rawValue).tag(option)
                }
            }
            HStack {
                TextField("Min ms", text: $minDurationText)
                TextField("Max ms", text: $maxDurationText)
            }
            Button("Apply Filters") {
                Task {
                    await viewModel.applyFilters(
                        tool: toolFilterText,
                        outcome: outcomeSelection.queryValue,
                        minDurationMs: Int(minDurationText),
                        maxDurationMs: Int(maxDurationText)
                    )
                }
            }
        }
        .padding(8)
        .textFieldStyle(.roundedBorder)
    }

    /// `viewModel.knownProjects` (`GET /control/projects`), verbatim: `name` for display, `productGuid`
    /// as the tag/value - a project identifier is view state, never derived display data (spec #3
    /// §1's own carve-out for exactly this shape). Selecting writes straight through
    /// `viewModel.selectProject(_:)` - see this view's own doc comment for why this Picker, alone
    /// among these filters, has no local `@State` buffer and no "Apply Filters" step.
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
    }

    /// The primary timeline - see this type's own doc comment. An empty `sequences` is the ordinary
    /// "tracing is on, nothing has been called yet" state, never rendered as an error.
    @ViewBuilder
    private var sequencesList: some View {
        if viewModel.sequences.isEmpty {
            // `.frame(maxWidth/maxHeight: .infinity)` - without it, this view's own ideal size is
            // just its icon+title+description, so the `VStack` in `content` above sizes to fit
            // (filters + divider + picker + this) instead of filling the column, and the whole
            // block ends up vertically centered in the leftover space - the filters visibly jump
            // down from where they sit when `List` (which IS greedy) is showing instead. Matches
            // `List`'s own greedy sizing so switching tabs never moves the filters above it.
            ContentUnavailableView(
                "No Sequences Yet", systemImage: "point.3.connected.trianglepath.dotted",
                description: Text("Tool calls will appear here, grouped into sequences, once tracing records some.")
            )
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            List {
                if viewModel.sequencesTruncated {
                    Text("Older sequences exist beyond what is shown here.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                ForEach(viewModel.sequences, id: \.id) { sequence in
                    DisclosureGroup {
                        ForEach(Array(zip(sequence.tools, sequence.traceIds)), id: \.1) { tool, traceId in
                            Button {
                                selectedTraceId = traceId
                            } label: {
                                Text(tool)
                            }
                            .buttonStyle(.plain)
                        }
                    } label: {
                        TraceSequenceRowView(row: sequence)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var failuresList: some View {
        if viewModel.failures.isEmpty {
            // See `sequencesList`'s own comment on this modifier - same fix, same reason.
            ContentUnavailableView("No Failures", systemImage: "checkmark.circle")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            List(viewModel.failures, id: \.traceId, selection: $selectedTraceId) { failure in
                VStack(alignment: .leading, spacing: 2) {
                    Text(failure.tool)
                        .font(.subheadline.weight(.medium))
                    if let error = failure.error {
                        Text(error)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    if let durationMs = failure.durationMs {
                        LabeledContent("Duration (ms)", value: "\(durationMs)")
                            .font(.caption2)
                    }
                }
            }
        }
    }

    /// No selection binding here - `SlowToolRow` carries no `traceId` at all (it is an aggregate:
    /// tool name plus call-count/average/max, not one specific call), so a row here has nothing to
    /// hand `TracesViewModel.selectTrace(traceId:)`.
    @ViewBuilder
    private var slowList: some View {
        if viewModel.slowTools.isEmpty {
            // See `sequencesList`'s own comment on this modifier - same fix, same reason.
            ContentUnavailableView("No Data Yet", systemImage: "gauge")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            List(viewModel.slowTools, id: \.tool) { row in
                VStack(alignment: .leading, spacing: 2) {
                    Text(row.tool)
                        .font(.subheadline.weight(.medium))
                    LabeledContent("Calls", value: "\(row.callCount)")
                    LabeledContent("Average (ms)", value: "\(row.averageDurationMs)")
                    LabeledContent("Max (ms)", value: "\(row.maxDurationMs)")
                }
            }
        }
    }
}
