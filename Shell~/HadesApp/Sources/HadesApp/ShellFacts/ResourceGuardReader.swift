import Foundation

/// The second of the two OS facts this plan's own carve-out names (see `LaunchAtLoginReading`'s own
/// doc comment for the first, and `Hades.Server.Control.SettingsEndpoint`'s class doc comment for
/// the .NET side of both): Low Power Mode and thermal state are `ProcessInfo` signals about the
/// shell's own machine that a headless .NET process cannot observe at all. Behind a protocol for the
/// same fakeable-in-tests reason `LaunchAtLoginReading` is - `SettingsViewModelTests` never depends
/// on this machine's actual power/thermal state to pass.
///
/// **Not a licence to compute**, per this plan's own explicit limit on the carve-out: this protocol
/// hands back the two raw OS values and nothing else. It does not combine them, does not decide
/// whether background work "should" pause, and does not turn `thermalState` into a sentence - see
/// `SettingsView`'s own doc comment for the one picture-only (never text) decision it makes about
/// `thermalState`, the same "an icon is the only display this type invents" contract
/// `StatusIcon.symbolName(for state: OperationState)` already holds Control-API enums to.
@MainActor
public protocol ResourceGuardReading {
    /// `ProcessInfo.processInfo.isLowPowerModeEnabled`, read directly.
    var isLowPowerModeEnabled: Bool { get }

    /// `ProcessInfo.processInfo.thermalState`, read directly - the OS's own enum value, unmapped.
    var thermalState: ProcessInfo.ThermalState { get }
}

/// The real `ResourceGuardReading`, backed by `ProcessInfo.processInfo` directly - see that
/// protocol's own doc comment. Not unit tested itself: both properties are a one-line read of a
/// system value with no logic to exercise. `SettingsViewModelTests` fakes the protocol instead.
/// Genuinely observable live at any time (unlike `LaunchAtLoginService`, this reads state rather than
/// mutating it, so there is no register/unregister risk to manage) - Task 8's hand-run pass confirms
/// it reflects this Mac's real state.
@MainActor
public struct ResourceGuardReader: ResourceGuardReading {
    public init() {}

    public var isLowPowerModeEnabled: Bool {
        ProcessInfo.processInfo.isLowPowerModeEnabled
    }

    public var thermalState: ProcessInfo.ThermalState {
        ProcessInfo.processInfo.thermalState
    }
}
