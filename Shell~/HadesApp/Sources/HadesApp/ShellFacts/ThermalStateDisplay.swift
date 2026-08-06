import Foundation

/// **A narrow, explicit exception to spec #3 §1 ("Swift renders, .NET decides") - scoped to
/// `ProcessInfo.ThermalState` only, nothing else.** Every other enum this app renders stays
/// API-sourced: it either comes from .NET as literal text (`settings.logLevel.level`,
/// `mcpPort.message`) or renders through a picture-only mapping like
/// `StatusIcon.symbolName(for:)` (see `ControlEnum`'s own doc comment in `HadesControl/DTOs.swift`
/// for why a second place deciding wording is exactly the drift spec #3 §1 exists to prevent).
/// `ProcessInfo.ThermalState` cannot drift the same way, because it cannot have a second source AT
/// ALL: a headless .NET process has no way to observe this Mac's thermal state (see
/// `ResourceGuardReading`'s own doc comment). There is no .NET answer for this mapping to ever
/// disagree with. The plan's own carve-out already named this row - "an OS fact about the shell's
/// own process or machine is Swift's" - and a carve-out that lets the shell read the fact but
/// forbids it from saying what the fact means would defeat its own purpose.
///
/// **What this does NOT authorise.** Not a precedent for mapping any other enum to display text in
/// Swift. `ProcessInfo.ThermalState` qualifies for this narrow exception because it is (a) a
/// bounded, four-case, Apple-defined enum, not open-ended product vocabulary, and (b) has no .NET
/// counterpart and never can. Every API-sourced `ControlEnum` (`ControlIconState`, `TraceOutcome`,
/// ...) still renders through `StatusIcon` (picture only) or arrives as literal core-authored text
/// - never through a Swift-authored word switch like this one.
///
/// **Lives in `ShellFacts/`, alongside `ResourceGuardReader`, not inline in `SettingsView`** - the
/// same "isolated and auditable in one small file" reasoning `LaunchAtLoginService`'s own doc
/// comment already gives for keeping an OS-fact seam out of the view layer. A view that printed
/// this mapping via its own private switch would put the one authorised exception to spec #3 §1
/// somewhere nobody auditing that rule would think to look.
///
/// **`@unknown default` is mandatory, not defensive boilerplate.** `ProcessInfo.ThermalState` is an
/// Apple system enum this build does not control the case list of - a future OS can add a case this
/// binary was compiled before ever seeing (confirmed empirically: this is a plain `NS_ENUM`, not
/// `NS_CLOSED_ENUM`, so Swift treats it as non-frozen and an out-of-range raw value is constructible
/// and switches on cleanly into `@unknown default`, it does not trap). Same "never crash, never
/// silently mislabel an unrecognised value" discipline `ControlEnum.unknownFallback` holds
/// API-sourced enums to, and the same discipline the existing
/// `SettingsView.thermalStateSymbolName` icon switch already holds this exact enum to (see that
/// property's own doc comment) - an unrecognised case here renders "Unknown", the text equivalent
/// of that switch's "questionmark.circle" fallback glyph, never a crash and never a guess at a real
/// state.
public enum ThermalStateDisplay {
    /// `ProcessInfo.ThermalState` -> a hand-written display word. Exhaustive and fixed at compile
    /// time - the only kind of switch this type's own narrow carve-out allows (see this type's own
    /// doc comment). Every known case is the literal, capitalised case name Apple itself already
    /// uses to describe the state in its own documentation - not invented product copy.
    public static func text(for thermalState: ProcessInfo.ThermalState) -> String {
        switch thermalState {
        case .nominal: return "Nominal"
        case .fair: return "Fair"
        case .serious: return "Serious"
        case .critical: return "Critical"
        @unknown default: return "Unknown"
        }
    }
}
