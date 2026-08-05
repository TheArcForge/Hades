import HadesControl

/// The one place `ControlIconState` / `ControlSeverity` / `MenuBarContent` become an SF Symbol
/// name. Every mapping here is one-to-one and fixed at compile time by an exhaustive switch -
/// never a comparison between two pieces of data, which is exactly the "precedence logic" spec #3
/// forbids in Swift. Precedence (which project's state wins, whether an error outranks an
/// in-progress index) already happened server-side to produce the single `iconState` value
/// `symbolName(for iconState:)` switches on; this type only picks a picture for a value the core
/// already resolved.
public enum StatusIcon {
    /// `ControlIconState` -> SF Symbol name. Covers `.unknown` too - the fallback
    /// `ControlEnum.init(from:)` decodes any unrecognised raw value to (see that protocol's own
    /// doc comment) - so a newer core adding an `iconState` case cannot make this switch
    /// non-exhaustive or crash this client; it just shows a neutral "something this build does
    /// not recognise" glyph.
    public static func symbolName(for iconState: ControlIconState) -> String {
        switch iconState {
        case .idle: return "circle"
        case .indexing: return "arrow.triangle.2.circlepath"
        case .attached: return "checkmark.circle.fill"
        case .leaseHeld: return "lock.circle.fill"
        case .error: return "exclamationmark.triangle.fill"
        case .unknown: return "questionmark.circle"
        }
    }

    /// `ControlSeverity` -> SF Symbol name, for the small accent drawn next to a per-project row.
    /// Same one-to-one contract as above.
    public static func symbolName(for severity: ControlSeverity) -> String {
        switch severity {
        case .ok: return "circle.fill"
        case .warning: return "exclamationmark.triangle.fill"
        case .error: return "xmark.octagon.fill"
        case .unknown: return "questionmark.circle"
        }
    }

    /// `OperationState` -> SF Symbol name, for `OperationProgress.tracked` (a polled rebuild's own
    /// state). Same one-to-one, fixed-at-compile-time contract as every mapping above - never a
    /// text label ("Running…"/"Complete"), because `OperationResult` (unlike `ProjectRow.editor`,
    /// which pairs `state` with a sibling `status` string) has no server-authored status text to
    /// print instead; an icon is the only display this type invents.
    public static func symbolName(for state: OperationState) -> String {
        switch state {
        case .running: return "arrow.triangle.2.circlepath"
        case .done: return "checkmark.circle.fill"
        case .failed: return "xmark.octagon.fill"
        case .unknown: return "questionmark.circle"
        }
    }

    /// `TraceOutcome` -> SF Symbol name, for a trace/sequence's own resolved outcome (Traces, Task
    /// 5). Same one-to-one, fixed-at-compile-time contract as every mapping above - the core already
    /// resolved `.error` for a sequence the moment any one of its calls failed (see
    /// `Hades.Server.Control.TracesEndpoint.BuildSequence`'s own `anyError` check); this only picks
    /// a picture for that already-resolved value.
    public static func symbolName(for outcome: TraceOutcome) -> String {
        switch outcome {
        case .ok: return "checkmark.circle.fill"
        case .error: return "xmark.octagon.fill"
        case .unknown: return "questionmark.circle"
        }
    }

    /// `MenuBarContent` -> SF Symbol name for the status item itself, including the three
    /// supervision-only cases `ControlIconState` cannot express because there is no core to ask.
    /// This is the one seam where Swift necessarily decides the icon on its own - not a
    /// precedence decision among API data, but the unavoidable consequence of there being no API
    /// response to read at all when nothing is running. The `.running` branch delegates entirely
    /// to the API's own resolved `iconState` - it does not look at `summary.rows` or
    /// `summary.lease` to decide anything itself.
    public static func symbolName(for content: MenuBarContent) -> String {
        switch content {
        case .notRunning: return "circle.dotted"
        case .restarting: return "arrow.triangle.2.circlepath"
        case .failed: return "xmark.octagon.fill"
        case .running(_, let summary): return symbolName(for: summary.iconState)
        }
    }
}
