import Foundation
import Testing

@testable import HadesApp

/// Exhaustive, one-to-one mapping tests for the one Swift-authored enum-to-text mapping this app
/// is explicitly authorised to make - see `ThermalStateDisplay`'s own doc comment for why
/// `ProcessInfo.ThermalState`, and only that enum, may cross "Swift renders, .NET decides". Same
/// "no re-derivation" contract `StatusIconTests` holds every `StatusIcon.symbolName(for:)` case to:
/// every expected string below is typed literally, never built from the switch/logic under test -
/// a test that re-derives its expectation from the code it is checking proves nothing.
@Suite("ThermalStateDisplay")
struct ThermalStateDisplayTests {

    @Test(
        "every known ProcessInfo.ThermalState case maps to its own fixed, hand-typed word",
        arguments: [
            (ProcessInfo.ThermalState.nominal, "Nominal"),
            (ProcessInfo.ThermalState.fair, "Fair"),
            (ProcessInfo.ThermalState.serious, "Serious"),
            (ProcessInfo.ThermalState.critical, "Critical"),
        ]
    )
    func knownCaseMapping(state: ProcessInfo.ThermalState, expected: String) {
        #expect(ThermalStateDisplay.text(for: state) == expected)
    }

    /// `ProcessInfo.ThermalState` is a plain `NS_ENUM` (not `NS_CLOSED_ENUM`), so Swift treats it as
    /// non-frozen: `init(rawValue:)` accepts any `Int`, including one no known case owns, and a
    /// switch over it requires `@unknown default` to stay exhaustive - confirmed empirically against
    /// this SDK (`NSProcessInfo.h`) rather than assumed. Raw value 99 is not, and is never going to
    /// become, a real case Apple assigns - it exists here purely to construct a value that cannot
    /// match `.nominal`/`.fair`/`.serious`/`.critical`, exactly the "a future OS added a case this
    /// build does not recognise" scenario `@unknown default` exists to handle.
    @Test("an unrecognised thermal state renders an honest fallback word, never a crash or a guessed real state")
    func unknownCaseMapping() {
        let unrecognised = ProcessInfo.ThermalState(rawValue: 99)!

        #expect(ThermalStateDisplay.text(for: unrecognised) == "Unknown")
    }
}
