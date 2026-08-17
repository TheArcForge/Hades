import Foundation
import HadesControl
import Testing

@testable import HadesApp

/// The per-item migration cleanup UI's own view model: the three `{productGuid}`-scoped
/// `V12Cleanup` actions (`cleanClaudeMd`, `cleanManifest`, `cleanMcpConfig`) rendered inside
/// `ProjectDetailView`'s "v1.2 Cleanup" section. See `MigrationCleanupViewModel`'s own doc comment
/// for the fourth, global action (`cleanClaudeDesktopConfig`), which lives on `SettingsViewModel`
/// instead - `SettingsViewModelTests` covers that one.
///
/// Every test here proves one clause of this task's own brief:
/// - `loadProjectStateOffers*` - "The detection result drives what is offered: do not offer to
///   clean a file that is not there."
/// - `*DeclinedNeverCallsAPI` - "Nothing runs without explicit per-action agreement."
/// - `*ConfirmedCallsAPIAndStoresResult` - "Each result renders its Message verbatim" (proven at
///   the data layer: the view renders `MigrationCleanupViewModel`'s own published dictionaries with
///   no view-owned logic - see `MigrationCleanupViews.swift`'s own doc comment).
/// - `cleanClaudeMdConfirmedSurfacesRemainingContentOutsideBlock` - "RemainingContentOutsideBlock
///   surfaced rather than swallowed."
/// - `failureOfOneLeavesOthersAvailable` - "Failure of one leaves the others available."
@Suite("MigrationCleanupViewModel")
@MainActor
struct MigrationCleanupViewModelTests {

    static func makeViewModel(fetcher: FakeMigrationFetcher) -> MigrationCleanupViewModel {
        let connections = FakeConnectionProvider([ControlConnection(port: 1, token: "t")], repeatLast: true)
        return MigrationCleanupViewModel(discover: { await connections.provide() }, makeClient: { _ in fetcher })
    }

    static func detection(
        manifestPresent: Bool = false, hasGeneratedMcpConfig: Bool = false,
        claudeMdShape: MigrationClaudeMdShape = .absent
    ) -> MigrationDetectionResult {
        MigrationDetectionResult(
            projectRoot: "/tmp/some-project", isV12Project: manifestPresent,
            manifestEntry: MigrationManifestEntryInfo(
                present: manifestPresent, value: manifestPresent ? "file:/Users/mike/Projects/Hades" : nil,
                resolvedPath: manifestPresent ? "/Users/mike/Projects/Hades" : nil),
            hasMemory: false, memoryDocumentCount: 0, hasTraces: false, hasGraph: false,
            hasGeneratedMcpConfig: hasGeneratedMcpConfig, claudeMd: MigrationClaudeMdInfo(shape: claudeMdShape),
            hasUnityPlugin: false
        )
    }

    // MARK: - loadProjectState: the detection result drives what is offered

    @Test("loadProjectState(productGuid:) offers all three actions when detection reports all three present")
    func loadProjectStateOffersEverythingDetectionReportsPresent() async {
        let claudeMdPreview = MigrationClaudeMdCleanupResult(
            removed: false, message: "Found a well-formed HADES:START/END block; not removed (no go-ahead).",
            remainingContentOutsideBlock: false)
        let manifestPreview = MigrationManifestCleanupResult(
            removed: false, message: "Found 1 occurrence(s) of com.arcforge.hades in manifest.json; not removed (no go-ahead).",
            occurrencesFound: 1, portConflictWarning: "If v1.2's package entry stays in Packages/manifest.json...")
        let mcpConfigPreview = MigrationMcpConfigCleanupResult(removed: false, message: "Found .mcp.json; not removed (no go-ahead).")

        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])),
            detectOutcome: .success(Self.detection(manifestPresent: true, hasGeneratedMcpConfig: true, claudeMdShape: .marked)),
            cleanClaudeMdOutcome: .success(claudeMdPreview),
            cleanManifestOutcome: .success(manifestPreview),
            cleanMcpConfigOutcome: .success(mcpConfigPreview)
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.loadProjectState(productGuid: "abc")

        #expect(viewModel.claudeMdState["abc"] == claudeMdPreview)
        #expect(viewModel.manifestState["abc"] == manifestPreview)
        #expect(viewModel.mcpConfigState["abc"] == mcpConfigPreview)
        #expect(await fetcher.cleanClaudeMdCallCount == 1)
        #expect(await fetcher.lastCleanClaudeMdProductGuid == "abc")
        #expect(await fetcher.lastCleanClaudeMdProceed == false)
        #expect(await fetcher.cleanManifestCallCount == 1)
        #expect(await fetcher.lastCleanManifestProceed == false)
        #expect(await fetcher.cleanMcpConfigCallCount == 1)
        #expect(await fetcher.lastCleanMcpConfigProceed == false)
    }

    @Test("loadProjectState(productGuid:) offers nothing when detection reports nothing present - never dry-runs an absent item")
    func loadProjectStateOffersNothingWhenDetectionReportsNothing() async {
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])),
            detectOutcome: .success(Self.detection())
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.loadProjectState(productGuid: "abc")

        #expect(viewModel.claudeMdState["abc"] == nil)
        #expect(viewModel.manifestState["abc"] == nil)
        #expect(viewModel.mcpConfigState["abc"] == nil)
        #expect(await fetcher.cleanClaudeMdCallCount == 0)
        #expect(await fetcher.cleanManifestCallCount == 0)
        #expect(await fetcher.cleanMcpConfigCallCount == 0)
    }

    @Test("loadProjectState(productGuid:) does not offer CLAUDE.md cleanup when unmarked - V12Cleanup never deletes it regardless of proceed")
    func loadProjectStateDoesNotOfferClaudeMdWhenUnmarked() async {
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])),
            detectOutcome: .success(Self.detection(claudeMdShape: .unmarked))
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.loadProjectState(productGuid: "abc")

        #expect(viewModel.claudeMdState["abc"] == nil)
        #expect(await fetcher.cleanClaudeMdCallCount == 0)
    }

    @Test("loadProjectState(productGuid:) offers only the items detection reports present, independently of each other")
    func loadProjectStateOffersOnlyWhatIsPresent() async {
        let manifestPreview = MigrationManifestCleanupResult(
            removed: false, message: "Found 1 occurrence(s) of com.arcforge.hades in manifest.json; not removed (no go-ahead).",
            occurrencesFound: 1, portConflictWarning: "port warning")
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])),
            detectOutcome: .success(Self.detection(manifestPresent: true)),
            cleanManifestOutcome: .success(manifestPreview)
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.loadProjectState(productGuid: "abc")

        #expect(viewModel.manifestState["abc"] == manifestPreview)
        #expect(viewModel.claudeMdState["abc"] == nil)
        #expect(viewModel.mcpConfigState["abc"] == nil)
        #expect(await fetcher.cleanManifestCallCount == 1)
        #expect(await fetcher.cleanClaudeMdCallCount == 0)
        #expect(await fetcher.cleanMcpConfigCallCount == 0)
    }

    @Test("loadProjectState(productGuid:) scopes state per project - loading a second project never touches the first's")
    func loadProjectStateIsScopedPerProject() async {
        let firstPreview = MigrationMcpConfigCleanupResult(removed: false, message: "first project preview")
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])),
            detectOutcome: .success(Self.detection(hasGeneratedMcpConfig: true)),
            cleanMcpConfigOutcome: .success(firstPreview)
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.loadProjectState(productGuid: "project-1")

        await fetcher.setCleanMcpConfigOutcome(.success(MigrationMcpConfigCleanupResult(removed: false, message: "second project preview")))
        await viewModel.loadProjectState(productGuid: "project-2")

        #expect(viewModel.mcpConfigState["project-1"] == firstPreview)
        #expect(viewModel.mcpConfigState["project-2"]?.message == "second project preview")
    }

    // MARK: - Declining: nothing runs without explicit per-action agreement

    @Test("cleanClaudeMd(confirmed: false) never calls the API and leaves the offered preview exactly as it was")
    func cleanClaudeMdDeclinedNeverCallsAPI() async {
        let preview = MigrationClaudeMdCleanupResult(
            removed: false, message: "Found a well-formed HADES:START/END block; not removed (no go-ahead).",
            remainingContentOutsideBlock: false)
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])),
            detectOutcome: .success(Self.detection(claudeMdShape: .marked)),
            cleanClaudeMdOutcome: .success(preview)
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.loadProjectState(productGuid: "abc")
        #expect(await fetcher.cleanClaudeMdCallCount == 1) // the preview dry run only

        await viewModel.cleanClaudeMd(productGuid: "abc", confirmed: false)

        #expect(await fetcher.cleanClaudeMdCallCount == 1) // unchanged - decline never calls again
        #expect(viewModel.claudeMdState["abc"] == preview) // unchanged - still just the offer
    }

    @Test("cleanManifest(confirmed: false) never calls the API")
    func cleanManifestDeclinedNeverCallsAPI() async {
        let fetcher = FakeMigrationFetcher(projectsOutcome: .success(ProjectsResult(projects: [])))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.cleanManifest(productGuid: "abc", confirmed: false)

        #expect(await fetcher.cleanManifestCallCount == 0)
    }

    @Test("cleanMcpConfig(confirmed: false) never calls the API")
    func cleanMcpConfigDeclinedNeverCallsAPI() async {
        let fetcher = FakeMigrationFetcher(projectsOutcome: .success(ProjectsResult(projects: [])))
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.cleanMcpConfig(productGuid: "abc", confirmed: false)

        #expect(await fetcher.cleanMcpConfigCallCount == 0)
    }

    // MARK: - Confirming: each action authorised alone, its own real result rendered verbatim

    @Test("cleanClaudeMd(confirmed: true) calls proceed:true and stores the result verbatim, surfacing remainingContentOutsideBlock rather than swallowing it")
    func cleanClaudeMdConfirmedSurfacesRemainingContentOutsideBlock() async {
        let preview = MigrationClaudeMdCleanupResult(
            removed: false,
            message: "Found a well-formed HADES:START/END block, with other content outside it that will remain untouched; not removed yet (no go-ahead).",
            remainingContentOutsideBlock: true)
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])),
            detectOutcome: .success(Self.detection(claudeMdShape: .marked)),
            cleanClaudeMdOutcome: .success(preview)
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.loadProjectState(productGuid: "abc")

        let confirmed = MigrationClaudeMdCleanupResult(
            removed: true,
            message: "Removed the HADES:START/END block. Other content outside the block remains in the file, untouched.",
            remainingContentOutsideBlock: true)
        await fetcher.setCleanClaudeMdOutcome(.success(confirmed))

        await viewModel.cleanClaudeMd(productGuid: "abc", confirmed: true)

        #expect(await fetcher.cleanClaudeMdCallCount == 2)
        #expect(await fetcher.lastCleanClaudeMdProceed == true)
        #expect(viewModel.claudeMdState["abc"] == confirmed)
        #expect(viewModel.claudeMdState["abc"]?.remainingContentOutsideBlock == true)
        #expect(viewModel.claudeMdState["abc"]?.message == confirmed.message)
    }

    @Test("cleanManifest(confirmed: true) calls proceed:true and stores the result - including the port-conflict warning - verbatim")
    func cleanManifestConfirmedStoresResultVerbatim() async {
        let confirmed = MigrationManifestCleanupResult(
            removed: true, message: "Removed 1 occurrence(s) of com.arcforge.hades from manifest.json.",
            occurrencesFound: 1,
            portConflictWarning: "If v1.2's package entry stays in Packages/manifest.json while the app is also running, both will try to bind port 7823 and conflict.")
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])),
            cleanManifestOutcome: .success(confirmed)
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.cleanManifest(productGuid: "abc", confirmed: true)

        #expect(await fetcher.cleanManifestCallCount == 1)
        #expect(await fetcher.lastCleanManifestProductGuid == "abc")
        #expect(await fetcher.lastCleanManifestProceed == true)
        #expect(viewModel.manifestState["abc"] == confirmed)
    }

    @Test("cleanMcpConfig(confirmed: true) calls proceed:true and stores the result verbatim")
    func cleanMcpConfigConfirmedStoresResultVerbatim() async {
        let confirmed = MigrationMcpConfigCleanupResult(removed: true, message: "Removed the generated .mcp.json.")
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])),
            cleanMcpConfigOutcome: .success(confirmed)
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)

        await viewModel.cleanMcpConfig(productGuid: "abc", confirmed: true)

        #expect(await fetcher.cleanMcpConfigCallCount == 1)
        #expect(await fetcher.lastCleanMcpConfigProceed == true)
        #expect(viewModel.mcpConfigState["abc"] == confirmed)
    }

    // MARK: - Failure isolation: failure of one leaves the others available

    @Test("failure of one cleanup action leaves the other two untouched and still independently actionable")
    func failureOfOneLeavesOthersAvailable() async {
        let claudeMdPreview = MigrationClaudeMdCleanupResult(removed: false, message: "claude md preview", remainingContentOutsideBlock: false)
        let manifestPreview = MigrationManifestCleanupResult(removed: false, message: "manifest preview", occurrencesFound: 1, portConflictWarning: "port warning")
        let mcpConfigPreview = MigrationMcpConfigCleanupResult(removed: false, message: "mcp config preview")
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])),
            detectOutcome: .success(Self.detection(manifestPresent: true, hasGeneratedMcpConfig: true, claudeMdShape: .marked)),
            cleanClaudeMdOutcome: .success(claudeMdPreview),
            cleanManifestOutcome: .success(manifestPreview),
            cleanMcpConfigOutcome: .success(mcpConfigPreview)
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.loadProjectState(productGuid: "abc")

        // Manifest's own confirm fails (e.g. the project vanished between preview and confirm);
        // claudeMd's and mcpConfig's confirms succeed independently.
        await fetcher.setCleanManifestOutcome(.failure(.server(status: 404, message: "Unknown project 'abc'.")))
        let claudeMdDone = MigrationClaudeMdCleanupResult(
            removed: true, message: "Removed the HADES:START/END block. Every other byte in the file is untouched.",
            remainingContentOutsideBlock: false)
        await fetcher.setCleanClaudeMdOutcome(.success(claudeMdDone))
        let mcpConfigDone = MigrationMcpConfigCleanupResult(removed: true, message: "Removed the generated .mcp.json.")
        await fetcher.setCleanMcpConfigOutcome(.success(mcpConfigDone))

        await viewModel.cleanManifest(productGuid: "abc", confirmed: true)
        await viewModel.cleanClaudeMd(productGuid: "abc", confirmed: true)
        await viewModel.cleanMcpConfig(productGuid: "abc", confirmed: true)

        // The failed action's own offer is left exactly as it was (self-heals, retryable) and the
        // server's own message is surfaced rather than swallowed.
        #expect(viewModel.manifestState["abc"] == manifestPreview)
        #expect(viewModel.lastActionMessage == "Unknown project 'abc'.")
        // The other two are completely unaffected by manifest's failure.
        #expect(viewModel.claudeMdState["abc"] == claudeMdDone)
        #expect(viewModel.mcpConfigState["abc"] == mcpConfigDone)
    }

    @Test("a transient failure (no server message) self-heals - leaves prior state exactly as it was")
    func transientFailureSelfHeals() async {
        let preview = MigrationMcpConfigCleanupResult(removed: false, message: "mcp config preview")
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])),
            detectOutcome: .success(Self.detection(hasGeneratedMcpConfig: true)),
            cleanMcpConfigOutcome: .success(preview)
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        await viewModel.loadProjectState(productGuid: "abc")

        await fetcher.setCleanMcpConfigOutcome(.failure(.transport(URLError(.timedOut))))
        await viewModel.cleanMcpConfig(productGuid: "abc", confirmed: true)

        #expect(viewModel.mcpConfigState["abc"] == preview) // untouched
        #expect(viewModel.lastActionMessage == nil) // nothing server-authored to show
    }

    // MARK: - lastActionMessage: the live property MigrationCleanupViews.swift renders verbatim

    @Test("lastActionMessage flows from one confirmed failure to the next - the same live, @Observable property MigrationCleanupViews.swift's rows render, not a value read once and forgotten")
    func lastActionMessageFlowsAcrossSuccessiveFailures() async {
        let fetcher = FakeMigrationFetcher(
            projectsOutcome: .success(ProjectsResult(projects: [])),
            cleanManifestOutcome: .failure(.server(status: 404, message: "Unknown project 'abc'.")),
            cleanMcpConfigOutcome: .failure(.server(status: 404, message: "Unknown project 'xyz'."))
        )
        let viewModel = Self.makeViewModel(fetcher: fetcher)
        #expect(viewModel.lastActionMessage == nil) // nothing yet

        await viewModel.cleanManifest(productGuid: "abc", confirmed: true)
        #expect(viewModel.lastActionMessage == "Unknown project 'abc'.")

        // A second, independent failure overwrites the first rather than accumulating or sticking -
        // a view bound to this property redraws with the new text every time it changes.
        await viewModel.cleanMcpConfig(productGuid: "xyz", confirmed: true)
        #expect(viewModel.lastActionMessage == "Unknown project 'xyz'.")
    }
}
