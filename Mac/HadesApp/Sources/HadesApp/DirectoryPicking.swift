import AppKit

/// Chooses a directory for "Add Project" - `NSOpenPanel` is the only place the shell picks a path
/// (Task 4's own requirement: `ProjectsViewModel.addProject(path:)` only ever carries a path
/// something else already chose). A narrow protocol, the same AppKit-seam pattern
/// `MainWindowScene` already established for `makeWindow`/`focusWindow`/`setActivationPolicy`:
/// `ProjectsView`'s own glue - call the picker, and if it returned a path, call
/// `viewModel.addProject(path:)` - is exercised by nothing but a real macOS window session, since a
/// real `NSOpenPanel` sheet cannot be driven headlessly. Only Task 8's hand-run pass proves the real
/// `NSOpenPanelDirectoryPicker` below actually opens a working picker and returns what the user
/// chose; this seam exists so that fact is isolated and auditable in one small file, not to make it
/// unit-testable (see this project's Plan 13 Task 4 report for what was and was not proven).
@MainActor
public protocol DirectoryPicking {
    /// Presents the picker and returns the chosen directory's absolute path, or `nil` if the user
    /// cancelled.
    func pickDirectory() -> String?
}

/// The real picker - a directory-only, single-selection `NSOpenPanel`. `ProjectsView`'s own default;
/// tests never construct this (see `DirectoryPicking`'s own doc comment).
public struct NSOpenPanelDirectoryPicker: DirectoryPicking {
    public init() {}

    public func pickDirectory() -> String? {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "Add"
        panel.message = "Choose a Unity project folder"
        return panel.runModal() == .OK ? panel.url?.path : nil
    }
}
