/// The seam through which onboarding surfaces Task 2/3/4's v1.2 migration decision-makers -
/// `V12Detector`, `V12Importer`, `V12Cleanup`, all `.NET`, all in `App~/src/Hades.Core/Migration/`.
///
/// **Real as of Plan 14 Task 10.** The control API now exposes all three under `/control/migration/*`
/// (`Hades.Server.Control.MigrationEndpoint`), and `AppDelegate` constructs a real conformance -
/// `LiveMigrationOffering` - instead of leaving `OnboardingViewModel.migrationOffering` `nil`. See
/// `LiveMigrationOffering`'s own doc comment for exactly what it does (resolves a path to a
/// productGuid via `GET /control/projects`, then imports memory and traces) and does not do (call
/// any of `V12Cleanup`'s four cleanup routes - those are real, tested, and reachable from
/// `ControlClient` today, but wiring a per-item confirmation UI for them is follow-up work, not
/// something this protocol's thin, one-boolean/one-opaque-call shape can honestly drive). Tests
/// still supply `FakeMigrationOffering` (`Tests/HadesAppTests/Support/TestSupport.swift`) to prove
/// `OnboardingViewModel`'s own contract in isolation: an offer is surfaced, never auto-performed,
/// and performing requires a separate, explicit confirmation - spec #4 §10, "Migration is always
/// offered, never performed silently."
///
/// **Deliberately NOT shaped like `V12DetectionResult`/`MigrationDetectionResult`.** That type has
/// eight independent fields (manifest entry, memory document count, traces/graph/generated-config
/// presence, `CLAUDE.md` shape, plugin presence), and the six actual migration actions
/// (`V12Importer`'s two methods, `V12Cleanup`'s four) each carry their own mandatory/optional/
/// destructive rules that `V12Importer`/`V12Cleanup`'s own doc comments already own at length - e.g.
/// memory import is mandatory and never individually confirmed, while every `V12Cleanup` step is
/// optional and individually gated, with no default `proceed`. Mirroring that shape here would mean
/// Swift re-deriving which of six actions are destructive - a business decision `V12Cleanup` already
/// owns, and the exact re-derivation "Swift renders, .NET decides" forbids. This protocol carries
/// only the one boolean `MigrationDetectionResult.isV12Project` already resolves (spec #4 §5's sole
/// trigger condition for offering migration at all), and one all-or-nothing `performMigration` Swift
/// never looks inside - narrow enough that `LiveMigrationOffering` could commit to a firm, honest
/// answer for what it means (import, not cleanup) without needing this protocol to grow at all.
public protocol MigrationOffering: Sendable {
    /// Mirrors `MigrationDetectionResult.isV12Project`. `LiveMigrationOffering` is the real
    /// conformance - see that type's own doc comment for how it resolves `projectPath` to a
    /// productGuid before calling `GET /control/migration/{productGuid}/detect`.
    func isV12Project(projectPath: String) async -> Bool

    /// Called only after the onboarding UI has recorded an explicit user confirmation
    /// (`OnboardingViewModel.confirmMigration()`) - never speculatively, and never as a side effect
    /// of `isV12Project` returning `true`. `LiveMigrationOffering` is the real conformance - see
    /// that type's own doc comment for exactly which of `V12Importer`/`V12Cleanup`'s six actions it
    /// runs under this call (two: memory and traces import) and which it deliberately does not
    /// (any of `V12Cleanup`'s four cleanup routes, and why).
    func performMigration(projectPath: String) async
}
