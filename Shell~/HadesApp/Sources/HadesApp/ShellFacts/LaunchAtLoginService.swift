import ServiceManagement

/// The first of the two OS facts this plan's own carve-out names (see "The rule, and the one
/// carve-out this plan makes explicit", and `Hades.Server.Control.SettingsEndpoint`'s class doc
/// comment for the .NET side of the same carve-out): whether Hades is registered to launch at login
/// is an `SMAppService` fact the OS owns outright. A separate, headless .NET process cannot observe
/// it - it does not run as, or alongside, the Swift process, and has no ServiceManagement access at
/// all. Behind a protocol, the same AppKit/system-API seam pattern `DirectoryPicking` already
/// established: real work is one line deep, and the whole point of the seam is making it fakeable so
/// `SettingsViewModelTests` never has to touch the real OS API - see this protocol's own doc comment
/// on `setEnabled` for exactly why `SMAppService` is unsafe to exercise from an automated test.
@MainActor
public protocol LaunchAtLoginReading {
    /// The OS's own current registration status - never a cached value, never the value a caller
    /// most recently requested. This is the ONLY source of truth `SettingsViewModel` reads, both on
    /// an ordinary refresh and immediately after `setEnabled` returns (see that method's own doc
    /// comment) - so a toggle that silently fails can never display as on.
    var isEnabled: Bool { get }

    /// Requests a change to the login-item registration. Throws on failure (the OS can refuse, or
    /// require the user to approve it in System Settings > General > Login Items) - but a caller
    /// must not infer success from the mere ABSENCE of a thrown error either: `SMAppService` can
    /// also silently leave `status` unchanged in some OS states even when `register()`/`unregister()`
    /// return normally (Apple's own documented behaviour). The only honest way to know whether this
    /// took effect is to re-read `isEnabled` afterward - which is exactly what
    /// `SettingsViewModel.toggleLaunchAtLogin(to:)` does, never trusting this call's own return.
    func setEnabled(_ enabled: Bool) throws
}

extension LaunchAtLoginReading {
    /// Requests a launch-at-login change, then re-reads `isEnabled` from the SAME OS source -
    /// never the requested value - so a request the OS refuses OR silently ignores can never be
    /// reported back as having succeeded. See `setEnabled`'s own doc comment for exactly why a
    /// thrown error alone is not the only failure mode this must guard against. Shared by every
    /// caller that toggles this OS fact (`SettingsViewModel.toggleLaunchAtLogin`,
    /// `OnboardingViewModel.toggleLaunchAtLogin`) so the "always re-read after writing" contract
    /// lives in exactly one place instead of being copied at each call site.
    @discardableResult
    func settingEnabled(to requested: Bool) -> Bool {
        try? setEnabled(requested)
        return isEnabled
    }
}

/// The real `LaunchAtLoginReading`, backed by `SMAppService.mainApp` directly. Not unit tested
/// itself - there is nothing to unit test: it is a one-line pass-through to a system API that
/// genuinely registers a login item with launchd. `SettingsViewModelTests` fakes the protocol
/// instead, exactly so a test suite never does that as a side effect of running (same reasoning
/// `DirectoryPicking`'s own doc comment gives for `NSOpenPanelDirectoryPicker` - "this seam exists so
/// that fact is isolated and auditable in one small file, not to make it unit-testable"). Proven
/// live, once, outside the automated suite - see the Plan 13 Task 7 report for that check's own
/// register -> confirm -> unregister -> confirm result; Task 8's hand-run pass is the standing check
/// after that, the same way it is for `NSOpenPanelDirectoryPicker`.
@MainActor
public struct LaunchAtLoginService: LaunchAtLoginReading {
    public init() {}

    public var isEnabled: Bool {
        SMAppService.mainApp.status == .enabled
    }

    public func setEnabled(_ enabled: Bool) throws {
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    }
}
