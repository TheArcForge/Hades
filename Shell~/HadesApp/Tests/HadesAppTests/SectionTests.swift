import Testing

@testable import HadesApp

/// `Section` is pure UI navigation state - which sidebar destination is selected - not control-API
/// data, so unlike `MenuBarContentTests` there is nothing to prove about mapping from a DTO. These
/// tests pin down the two facts that matter: exactly the three sidebar destinations Spec #3 §3.2-
/// §3.4 describes (Settings is deliberately excluded - see `Section`'s own doc comment), and that
/// each has a fixed display title (Swift chrome, not data read from a `SummaryResult`/`ProjectRow`/
/// etc. - allowed under spec #3 §1 the same way `SupervisionFooterView`'s fixed ownership labels
/// are).
@Suite("Section")
struct SectionTests {

    @Test("exactly three sections - Projects, Traces, Memory - and Settings is not among them")
    func allCasesAreProjectsTracesMemoryOnly() {
        #expect(Section.allCases == [.projects, .traces, .memory])
    }

    @Test("each section has a fixed, non-empty display title")
    func titlesAreFixedChrome() {
        #expect(Section.projects.title == "Projects")
        #expect(Section.traces.title == "Traces")
        #expect(Section.memory.title == "Memory")
    }
}
