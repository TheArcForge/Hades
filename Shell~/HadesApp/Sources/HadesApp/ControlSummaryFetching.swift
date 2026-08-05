import HadesControl

/// The narrow slice of `ControlClient` that the menu bar needs: fetch the one summary endpoint it
/// renders, and release a lease. Exists purely so tests can fake the control API without a real
/// `URLSession`/`MockURLProtocol` round trip - see `FakeSummaryFetcher` in
/// `Tests/HadesAppTests/Support/TestSupport.swift`. `ControlClient` needed no changes to conform
/// (empty extension below): its `summary()`/`releaseLease(id:)` already match this signature,
/// typed throws included.
public protocol ControlSummaryFetching: Sendable {
    func summary() async throws(ControlClientError) -> SummaryResult
    func releaseLease(id: String) async throws(ControlClientError) -> ActionResult
}

extension ControlClient: ControlSummaryFetching {}
