import HadesControl
import SwiftUI

/// Spec #3 §3.5: MCP port with its conflict state, launch at login, resource guards, and log level.
/// A standard macOS Settings window (see `HadesMenuBarApp.makeMainMenu`'s own doc comment for why
/// this is reachable by Cmd-, rather than a `Section` sidebar destination) - this view owns its
/// whole window's content, the same shape `ProjectsView`/`TracesView`/`MemoryView` have for their
/// own section.
///
/// **Two rows are Swift-rendered OS facts, not control-API data - the plan's own carve-out,
/// applied.** Launch at login and resource guards (Low Power Mode, thermal state) come from
/// `viewModel.launchAtLoginEnabled`/`isLowPowerModeEnabled`/`thermalState` - `ShellFacts/` readers,
/// never `/control/settings` (see `Hades.Server.Control.SettingsEndpoint`'s own class doc comment
/// for why .NET stopped reporting either). Everything else - `mcpPort`, `logLevel` - is
/// `viewModel.settings`, printed verbatim: `mcpPort.message` already states the conflict AND its
/// actionable remedy in one core-authored sentence (see that field's own doc comment on
/// `Hades.Server.Control.McpPortSetting`) - this view never re-derives "in use" from `inUse` itself
/// or paraphrases the message.
///
/// **Unity Hub discovery opt-in and update channel are not rendered here.** Both remain a real API
/// gap, unchanged from Plan 11's own decision (Hub discovery is "its own piece of work" with no
/// discovery mechanism built anywhere in this codebase yet; update channel belongs with spec #4,
/// still explicitly out of scope for this plan too) - inventing a toggle for either with nothing
/// behind it would be exactly the "reports a value it cannot substantiate" problem this task exists
/// to close, one layer up.
struct SettingsView: View {
    let viewModel: SettingsViewModel

    var body: some View {
        Form {
            if let settings = viewModel.settings {
                SwiftUI.Section("MCP") {
                    LabeledContent("Port", value: "\(settings.mcpPort.port)")
                    LabeledContent("Status", value: settings.mcpPort.message)
                        .textSelection(.enabled)
                }
                SwiftUI.Section("Logging") {
                    LabeledContent("Log Level", value: settings.logLevel.level)
                }
            } else {
                ContentUnavailableView(
                    "Settings Unavailable", systemImage: "gearshape",
                    description: Text("Hades is not reachable right now.")
                )
            }

            SwiftUI.Section("Login") {
                Toggle(
                    "Launch Hades at Login",
                    isOn: Binding(
                        get: { viewModel.launchAtLoginEnabled },
                        set: { viewModel.toggleLaunchAtLogin(to: $0) }
                    )
                )
            }

            SwiftUI.Section("Resource Guards") {
                // A read-only OS fact rendered via the system's own Toggle chrome - no Swift-authored
                // "On"/"Off" text, and no action: this is not something the user sets HERE, it is
                // whatever macOS itself currently reports.
                Toggle("Low Power Mode", isOn: .constant(viewModel.isLowPowerModeEnabled))
                    .disabled(true)
                LabeledContent("Thermal State") {
                    Image(systemName: thermalStateSymbolName)
                }
            }
        }
        .formStyle(.grouped)
        .frame(minWidth: 420, minHeight: 340)
    }

    /// The one picture-only decision this view makes about `ProcessInfo.ThermalState` - an
    /// exhaustive, fixed-at-compile-time switch, never a text label ("Fair", "Hot", ...) - the same
    /// "an icon is the only display this type invents" contract
    /// `StatusIcon.symbolName(for state: OperationState)` already holds every Control-API enum to,
    /// applied here to the one Swift-owned OS enum in this whole app. `@unknown default` because a
    /// future OS could add a case this build does not recognise - the same "never crash on an
    /// unrecognised value" discipline `ControlEnum.unknownFallback` holds server-resolved enums to.
    private var thermalStateSymbolName: String {
        switch viewModel.thermalState {
        case .nominal: return "thermometer.low"
        case .fair: return "thermometer.medium"
        case .serious: return "thermometer.high"
        case .critical: return "thermometer.sun.fill"
        @unknown default: return "questionmark.circle"
        }
    }
}
