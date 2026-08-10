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
/// or paraphrases the message. Both read-only rows carry the same `.opacity(0.5)` dimming cue,
/// applied directly to each row rather than trusted to `.disabled(true)` alone - see the Low Power
/// Mode `Toggle`'s own comment for why.
///
/// **One further, narrower exception lives inside the Resource Guards section.** Thermal state's
/// icon is a picture-only decision this view makes itself (`thermalStateSymbolName`, below); the
/// word next to it comes from `ThermalStateDisplay.text(for:)` in `ShellFacts/`, the one place in
/// this entire app explicitly authorised to map an enum to display text in Swift - see that type's
/// own doc comment for exactly why `ProcessInfo.ThermalState`, and only that enum, may cross
/// "Swift renders, .NET decides".
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
                //
                // **`.opacity`, not `.disabled` alone.** Confirmed live: `.disabled(true)` by itself
                // left this toggle pixel-for-pixel identical to the interactive "Launch Hades at
                // Login" toggle above it - correct in the accessibility tree (`enabled=false`) but no
                // cue a sighted user could see before clicking it. The dimming is a rendering-only
                // decision about how an already-disabled control is drawn, never a change to the
                // value itself or a new derived string, so it stays inside this row's own carve-out.
                Toggle("Low Power Mode", isOn: .constant(viewModel.isLowPowerModeEnabled))
                    .disabled(true)
                    .opacity(0.5)
                // Thermal State now pairs the icon with its own display word - a narrow, explicitly
                // authorised exception to this row's own carve-out (see this view's own class doc
                // comment, and `ThermalStateDisplay`'s own doc comment in
                // `ShellFacts/ThermalStateDisplay.swift`, for exactly why `ProcessInfo.ThermalState`,
                // and only that enum, may cross "Swift renders, .NET decides"). `.opacity(0.5)`
                // matches Low Power Mode directly above: the same read-only OS fact, rendered via a
                // non-interactive control, gets the same dimming cue for the same reason - see that
                // Toggle's own comment.
                LabeledContent("Thermal State") {
                    Label(ThermalStateDisplay.text(for: viewModel.thermalState), systemImage: thermalStateSymbolName)
                }
                .opacity(0.5)
            }

            migrationCleanup
        }
        .formStyle(.grouped)
        .frame(minWidth: 420, minHeight: 340)
    }

    /// The two GLOBAL `V12Cleanup` actions, `cleanClaudeDesktopConfig` and `cleanHadesHub` -
    /// deliberately here, on the one surface that is not project-scoped at all, never under
    /// Projects. Each row is rendered only when its own dry run found something to offer -
    /// `claudeDesktopConfigCleanup.occurrencesFound > 0` / `hadesHubCleanup.found` - "do not offer
    /// to clean something that is not there," the same discipline `ProjectDetailView`'s per-project
    /// "v1.2 Cleanup" section holds to, applied here to the two targets with no per-project detect
    /// endpoint behind them (see `MigrationClaudeDesktopConfigCleanupResult.occurrencesFound`'s and
    /// `MigrationHadesHubCleanupResult.found`'s own doc comments). The section itself only appears
    /// once at least one of the two has something to offer, so an all-clean v2-only machine shows
    /// neither an empty section nor two separately-gated ones.
    @ViewBuilder
    private var migrationCleanup: some View {
        let showClaudeDesktopConfig = (viewModel.claudeDesktopConfigCleanup?.occurrencesFound ?? 0) > 0
        let showHadesHub = viewModel.hadesHubCleanup?.found ?? false
        if showClaudeDesktopConfig || showHadesHub {
            SwiftUI.Section("v1.2 Cleanup") {
                if showClaudeDesktopConfig, let cleanup = viewModel.claudeDesktopConfigCleanup {
                    MigrationCleanClaudeDesktopConfigRow(result: cleanup, viewModel: viewModel)
                }
                if showHadesHub, let cleanup = viewModel.hadesHubCleanup {
                    MigrationCleanHadesHubRow(result: cleanup, viewModel: viewModel)
                }
            }
        }
    }

    /// The picture-only decision this view makes about `ProcessInfo.ThermalState` - an exhaustive,
    /// fixed-at-compile-time switch, never a text label itself - the same "an icon is the only
    /// display this type invents" contract `StatusIcon.symbolName(for state: OperationState)`
    /// already holds every Control-API enum to, applied here to the one Swift-owned OS enum in this
    /// whole app. `@unknown default` because a future OS could add a case this build does not
    /// recognise - the same "never crash on an unrecognised value" discipline
    /// `ControlEnum.unknownFallback` holds server-resolved enums to. The word sitting next to this
    /// icon in the Resource Guards row comes from `ThermalStateDisplay.text(for:)` instead
    /// (`ShellFacts/ThermalStateDisplay.swift`) - a separate, narrowly authorised exception to
    /// spec #3 §1; this property's own job stays picture-only.
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
