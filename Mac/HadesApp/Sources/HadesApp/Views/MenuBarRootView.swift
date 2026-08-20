import SwiftUI

/// The one view `MenuBarController` hosts. Reads `viewModel.content` directly - an `@Observable`
/// property access inside `body` - so SwiftUI re-renders automatically on every poll tick with no
/// Combine/`@Published` plumbing needed. Everything below this is pure presentation: see
/// `MenuBarContentView`'s own doc comment for the verbatim-only contract the rest of the view tree
/// holds to.
struct MenuBarRootView: View {
    let viewModel: MenuBarViewModel
    let onOpenHades: () -> Void
    let onQuit: () -> Void

    var body: some View {
        MenuBarContentView(
            content: viewModel.content,
            onRelease: { leaseId in
                Task { await viewModel.release(leaseId: leaseId) }
            },
            onOpenHades: onOpenHades,
            onQuit: onQuit
        )
    }
}
