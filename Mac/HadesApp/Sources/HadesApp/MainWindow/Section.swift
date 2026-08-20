/// The sidebar destinations inside the main window - Spec #3 §3.2-§3.4 (Projects, Charon, Asphodel).
///
/// **Charon** is the observability surface and **Asphodel** the project-memory surface. The enum
/// cases stay `.traces`/`.memory` - they name what the section *is* in the control API, which
/// still speaks traces and memory; these titles are the product names users read.
///
/// **Settings is deliberately not a case here.** Spec #3 §3.5 is a standard macOS Settings scene,
/// reachable by Cmd-, like every other Mac app - not a sidebar destination alongside these three.
/// See `HadesMenuBarApp`'s own Settings-menu wiring.
public enum Section: Hashable, Sendable, CaseIterable {
    case projects
    case traces
    case memory

    /// Fixed sidebar chrome, not control-API data - same allowance `SupervisionFooterView`'s own
    /// ownership labels already use (spec #3 §1: literal Swift copy is fine for concepts with no
    /// API equivalent; a sidebar destination's name is UI navigation, not a rendered DTO field).
    public var title: String {
        switch self {
        case .projects: return "Projects"
        case .traces: return "Charon"
        case .memory: return "Asphodel"
        }
    }
}
