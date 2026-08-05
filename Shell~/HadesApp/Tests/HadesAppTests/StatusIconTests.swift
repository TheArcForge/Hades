import HadesControl
import Testing

@testable import HadesApp

/// Exhaustive, one-to-one mapping tests - the whole proof that "icon reflects iconState with no
/// precedence logic in Swift" holds: every `ControlIconState`/`ControlSeverity`/`MenuBarContent`
/// case maps to exactly one fixed SF Symbol name, chosen at compile time by a switch the compiler
/// enforces is exhaustive. Nothing here compares two rows or two fields to decide anything - the
/// core already resolved that into the single value each function switches on.
@Suite("StatusIcon")
struct StatusIconTests {

    @Test(
        "every ControlIconState maps to exactly one fixed SF Symbol name",
        arguments: [
            (ControlIconState.idle, "circle"),
            (ControlIconState.indexing, "arrow.triangle.2.circlepath"),
            (ControlIconState.attached, "checkmark.circle.fill"),
            (ControlIconState.leaseHeld, "lock.circle.fill"),
            (ControlIconState.error, "exclamationmark.triangle.fill"),
            (ControlIconState.unknown, "questionmark.circle"),
        ]
    )
    func iconStateMapping(state: ControlIconState, expected: String) {
        #expect(StatusIcon.symbolName(for: state) == expected)
    }

    @Test(
        "every ControlSeverity maps to exactly one fixed SF Symbol name",
        arguments: [
            (ControlSeverity.ok, "circle.fill"),
            (ControlSeverity.warning, "exclamationmark.triangle.fill"),
            (ControlSeverity.error, "xmark.octagon.fill"),
            (ControlSeverity.unknown, "questionmark.circle"),
        ]
    )
    func severityMapping(severity: ControlSeverity, expected: String) {
        #expect(StatusIcon.symbolName(for: severity) == expected)
    }

    @Test(
        "every TraceOutcome maps to exactly one fixed SF Symbol name",
        arguments: [
            (TraceOutcome.ok, "checkmark.circle.fill"),
            (TraceOutcome.error, "xmark.octagon.fill"),
            (TraceOutcome.unknown, "questionmark.circle"),
        ]
    )
    func traceOutcomeMapping(outcome: TraceOutcome, expected: String) {
        #expect(StatusIcon.symbolName(for: outcome) == expected)
    }

    @Test("MenuBarContent.notRunning maps to a fixed supervision-only symbol")
    func notRunningSymbol() {
        #expect(StatusIcon.symbolName(for: MenuBarContent.notRunning) == "circle.dotted")
    }

    @Test("MenuBarContent.restarting maps to a fixed supervision-only symbol regardless of attempt number")
    func restartingSymbol() {
        #expect(StatusIcon.symbolName(for: MenuBarContent.restarting(attempt: 1)) == "arrow.triangle.2.circlepath")
        #expect(StatusIcon.symbolName(for: MenuBarContent.restarting(attempt: 4)) == "arrow.triangle.2.circlepath")
    }

    @Test("MenuBarContent.failed maps to a fixed supervision-only symbol regardless of attempts")
    func failedSymbol() {
        #expect(StatusIcon.symbolName(for: MenuBarContent.failed(attempts: 3)) == "xmark.octagon.fill")
        #expect(StatusIcon.symbolName(for: MenuBarContent.failed(attempts: 5)) == "xmark.octagon.fill")
    }

    @Test("MenuBarContent.running delegates entirely to the API's own iconState - proves no precedence logic")
    func runningSymbolDelegatesToIconState() {
        let leaseHeldSummary = SummaryResult(
            iconState: .leaseHeld, headline: "Holding script reload \u{2014} 1s", rows: [], lease: nil)
        let content = MenuBarContent.running(ownership: .spawned, summary: leaseHeldSummary)
        #expect(StatusIcon.symbolName(for: content) == StatusIcon.symbolName(for: .leaseHeld))
        #expect(StatusIcon.symbolName(for: content) == "lock.circle.fill")
    }
}
