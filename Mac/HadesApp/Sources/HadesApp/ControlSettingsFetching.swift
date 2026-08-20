import HadesControl

/// The narrow slice of `ControlClient` the Settings view needs: the one `GET /control/settings`
/// endpoint - same "fetch only, no action" shape `ControlTracesFetching` already established
/// (Settings, like Traces, has no POST/write endpoint at all - see
/// `Hades.Server.Control.SettingsEndpoint`'s own class doc comment: "no settings-WRITE action").
/// Exists purely so tests can fake the control API without a real `URLSession` round trip - see
/// `FakeSettingsFetcher` in `Tests/HadesAppTests/Support/TestSupport.swift`. `ControlClient` needed
/// no changes to conform (empty extension below): its `settings()` already matches this signature,
/// typed throws included.
public protocol ControlSettingsFetching: Sendable {
    func settings() async throws(ControlClientError) -> SettingsResult
}

extension ControlClient: ControlSettingsFetching {}
