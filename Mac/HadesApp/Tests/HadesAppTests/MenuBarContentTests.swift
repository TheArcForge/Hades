import HadesControl
import HadesSupervision
import Testing

@testable import HadesApp

/// One test per state the menu bar must render (Plan 12 Task 3's own list: no core running, no
/// projects, indexing, attached, busy, lease held, restart-failed), plus `restarting` and the
/// `.error` iconState for full coverage of every case `MenuBarContent.resolve` switches on. Every
/// assertion compares the WHOLE resolved `MenuBarContent` against a value built from the same
/// fields the input carried - proof `resolve` combines, formats, and derives nothing: what goes in
/// verbatim is what a view would draw.
///
/// Where a real fixture exists (Plan 12 Task 1's `summary_lease_held.json` /
/// `summary_idle_no_lease.json`), the exact captured strings are reused here via `SummaryResult`'s
/// own public initializer rather than retyped by hand, so these tests stay grounded in a real
/// response shape. `indexing`/`busy`/`no projects` have no captured fixture yet (no live core was
/// reachable while writing this - see the Plan 12 Task 3 report), so those use plausible values
/// following the same `status` text convention already proven real by `editors_attached.json`'s
/// `"Editor attached (busy)"` row - constructing TEST INPUT this way is not Swift inventing
/// display text; it is this test standing in for what the core would send.
@Suite("MenuBarContent.resolve")
struct MenuBarContentTests {

    // MARK: - Supervision-only cases (no summary in play)

    @Test("no core running: not started yet")
    func noCoreRunningNotStarted() {
        let content = MenuBarContent.resolve(supervisorState: .notStarted, lastSummary: nil)
        #expect(content == .notRunning)
    }

    @Test("no core running: starting")
    func noCoreRunningStarting() {
        let content = MenuBarContent.resolve(supervisorState: .starting, lastSummary: nil)
        #expect(content == .notRunning)
    }

    @Test("a running core that has not yet answered a summary fetch reads as not-running, never as an error")
    func runningWithNoSummaryYetReadsAsNotRunning() {
        let content = MenuBarContent.resolve(supervisorState: .running(.spawned), lastSummary: nil)
        #expect(content == .notRunning)
    }

    @Test("restarting carries the supervisor's own attempt count verbatim")
    func restarting() {
        let content = MenuBarContent.resolve(supervisorState: .restarting(attempt: 2), lastSummary: nil)
        #expect(content == .restarting(attempt: 2))
    }

    @Test("restart-failed carries the supervisor's own attempts count verbatim")
    func restartFailed() {
        let content = MenuBarContent.resolve(supervisorState: .failed(attempts: 5), lastSummary: nil)
        #expect(content == .failed(attempts: 5))
    }

    @Test("restart-failed ignores a stale summary left over from before the core died")
    func restartFailedIgnoresStaleSummary() {
        let stale = SummaryResult(iconState: .idle, headline: "No Unity Editor attached", rows: [], lease: nil)
        let content = MenuBarContent.resolve(supervisorState: .failed(attempts: 5), lastSummary: stale)
        #expect(content == .failed(attempts: 5))
    }

    // MARK: - Running, differentiated only by the SummaryResult payload (the API's own domain)

    @Test("no projects: running, empty rows, adopted")
    func noProjects() {
        let summary = SummaryResult(iconState: .idle, headline: "No projects configured", rows: [], lease: nil)
        let content = MenuBarContent.resolve(supervisorState: .running(.adopted), lastSummary: summary)
        #expect(content == .running(ownership: .adopted, summary: summary))
    }

    @Test("indexing: iconState indexing, a row reporting index progress")
    func indexing() {
        let summary = SummaryResult(
            iconState: .indexing,
            headline: "Indexing Hades-Unity-Client",
            rows: [
                SummaryRow(project: "Hades-Unity-Client", productGuid: "15c012f27331e49229cef25e74537816", status: "Indexing 42 of 100 files", severity: .ok)
            ],
            lease: nil
        )
        let content = MenuBarContent.resolve(supervisorState: .running(.spawned), lastSummary: summary)
        #expect(content == .running(ownership: .spawned, summary: summary))
    }

    @Test("attached: iconState attached, row status verbatim from a real captured shape")
    func attached() {
        // project/status text mirrors summary_idle_no_lease.json's row convention but with an
        // Editor attached, matching projects_editor_attached.json's own editor.status string
        // ("Editor attached") for the attached half.
        let summary = SummaryResult(
            iconState: .attached,
            headline: "Editor attached to Hades-Unity-Client",
            rows: [
                SummaryRow(project: "Hades-Unity-Client", productGuid: "15c012f27331e49229cef25e74537816", status: "Editor attached \u{00b7} indexed 4m ago", severity: .ok)
            ],
            lease: nil
        )
        let content = MenuBarContent.resolve(supervisorState: .running(.spawned), lastSummary: summary)
        #expect(content == .running(ownership: .spawned, summary: summary))
    }

    @Test("busy: row status carries the same '(busy)' convention proven real by editors_attached.json")
    func busy() {
        let summary = SummaryResult(
            iconState: .attached,
            headline: "Editor attached to Hades-Unity-Client",
            rows: [
                SummaryRow(project: "Hades-Unity-Client", productGuid: "15c012f27331e49229cef25e74537816", status: "Editor attached (busy)", severity: .ok)
            ],
            lease: nil
        )
        let content = MenuBarContent.resolve(supervisorState: .running(.spawned), lastSummary: summary)
        #expect(content == .running(ownership: .spawned, summary: summary))
    }

    @Test("lease held: exact strings from the real captured summary_lease_held.json fixture")
    func leaseHeld() {
        let summary = SummaryResult(
            iconState: .leaseHeld,
            headline: "Holding script reload for Hades-Unity-Client \u{2014} 42s",
            rows: [
                SummaryRow(project: "Hades-Unity-Client", productGuid: "15c012f27331e49229cef25e74537816", status: "Editor attached \u{00b7} indexed 5m ago", severity: .ok)
            ],
            lease: SummaryLease(
                project: "Hades-Unity-Client",
                leaseId: "15c012f27331e49229cef25e74537816",
                heldForSeconds: 42,
                expiresInSeconds: 18,
                releasable: true
            )
        )
        let content = MenuBarContent.resolve(supervisorState: .running(.adopted), lastSummary: summary)
        #expect(content == .running(ownership: .adopted, summary: summary))

        // Unpack explicitly too, so this test also pins down the exact fields a Release button
        // needs - not just that SOME content came through.
        guard case .running(let ownership, let resolved) = content, let lease = resolved.lease else {
            Issue.record("expected .running with a lease")
            return
        }
        #expect(ownership == .adopted)
        #expect(lease.leaseId == "15c012f27331e49229cef25e74537816")
        #expect(lease.releasable == true)
    }

    @Test("a non-releasable lease is carried through unchanged too - Swift never overrides releasable")
    func leaseHeldNotReleasable() {
        let summary = SummaryResult(
            iconState: .leaseHeld,
            headline: "Holding script reload for Hades-Unity-Client \u{2014} 2s",
            rows: [],
            lease: SummaryLease(
                project: "Hades-Unity-Client", leaseId: "abc123", heldForSeconds: 58, expiresInSeconds: 2,
                releasable: false
            )
        )
        let content = MenuBarContent.resolve(supervisorState: .running(.spawned), lastSummary: summary)
        guard case .running(_, let resolved) = content, let lease = resolved.lease else {
            Issue.record("expected .running with a lease")
            return
        }
        #expect(lease.releasable == false)
    }

    @Test("error iconState (a project warning of severity error) is carried through, not translated")
    func errorIconState() {
        let summary = SummaryResult(
            iconState: .error,
            headline: "Hades-Unity-Client needs attention",
            rows: [
                SummaryRow(project: "Hades-Unity-Client", productGuid: "15c012f27331e49229cef25e74537816", status: "Project path not found", severity: .error)
            ],
            lease: nil
        )
        let content = MenuBarContent.resolve(supervisorState: .running(.spawned), lastSummary: summary)
        #expect(content == .running(ownership: .spawned, summary: summary))
    }

    @Test("an unrecognised iconState (.unknown, decoded from a newer core) is carried through, not rejected")
    func unknownIconState() {
        let summary = SummaryResult(iconState: .unknown, headline: "No Unity Editor attached", rows: [], lease: nil)
        let content = MenuBarContent.resolve(supervisorState: .running(.adopted), lastSummary: summary)
        #expect(content == .running(ownership: .adopted, summary: summary))
    }
}
