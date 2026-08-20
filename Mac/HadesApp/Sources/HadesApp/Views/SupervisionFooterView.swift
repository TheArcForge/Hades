import HadesSupervision
import SwiftUI

/// Whether quitting the app right now would stop Hades. `CoreSupervisor.Ownership` has no .NET
/// equivalent - the core cannot know which app process (if any) spawned it - so unlike everything
/// above this footer, the two labels here are fixed Swift copy keyed on a Swift-native enum, not
/// text sourced from a control-API field. See `MenuBarContent`'s own doc comment for why that is
/// still within spec #3 §1's rule rather than an exception to it.
struct SupervisionFooterView: View {
    let ownership: CoreSupervisor.Ownership

    var body: some View {
        HStack(spacing: 4) {
            Image(systemName: ownership == .adopted ? "link.circle" : "bolt.circle")
                .imageScale(.small)
                .accessibilityHidden(true)
            Text(label)
        }
        .font(.caption)
        .foregroundStyle(.secondary)
    }

    private var label: String {
        switch ownership {
        case .adopted: return "Adopted \u{2014} quitting Hades leaves it running"
        case .spawned: return "Started by this app \u{2014} quitting stops it"
        }
    }
}
