/// The seam through which onboarding would surface Task 2/3/4's v1.2 migration decision-makers -
/// `V12Detector`, `V12Importer`, `V12Cleanup`, all `.NET`, all in `App~/src/Hades.Core/Migration/` -
/// **if the control API exposed them.**
///
/// **It does not, today.** Grepping every file in `App~/src/Hades.Server/Control/` for
/// `V12Detector`/`V12Importer`/`V12Cleanup` finds zero references. Spec #4 §5's migration offer is
/// fully designed and fully tested at the `.NET` layer (Plan 14 Tasks 2-4 - 23 tests for the
/// detector alone), but has no HTTP surface for a Swift client to reach. That is an API gap for
/// `.NET`, out of scope for this Swift-only task - see Plan 14 Task 6's own instruction: "If the
/// control API doesn't yet expose them, say so and stop rather than reaching around it."
///
/// **"Stop" means this protocol is deliberately thin, and production wires no real conformance at
/// all.** `OnboardingViewModel.migrationOffering` defaults to `nil`; `AppDelegate` never constructs
/// one, because there is nothing honest to construct it FROM (see this project's own "name the
/// caller" standard - there is, at present, no live caller for a real conformance in production).
/// `OnboardingViewModel.addProject(path:)` therefore never offers migration for a real user today.
/// Tests supply `FakeMigrationOffering` (`Tests/HadesAppTests/Support/TestSupport.swift`) to prove
/// the one contract that IS legitimately Swift's own to own regardless of what eventually backs this
/// protocol: an offer is surfaced, never auto-performed, and performing requires a separate,
/// explicit confirmation - spec #4 §10, "Migration is always offered, never performed silently."
///
/// **Deliberately NOT shaped like `V12DetectionResult`.** That record has eight independent fields
/// (manifest entry, memory document count, traces/graph/generated-config presence, `CLAUDE.md`
/// shape, plugin presence), and the six actual migration actions (`V12Importer`'s two methods,
/// `V12Cleanup`'s four) each carry their own mandatory/optional/destructive rules that `V12Importer`
/// and `V12Cleanup`'s own doc comments already own at length - e.g. memory import is mandatory and
/// never individually confirmed, while every `V12Cleanup` step is optional and individually gated.
/// Mirroring that shape here, ahead of a real wire contract nobody on the `.NET` side has agreed to
/// yet, would BE "reaching around the API gap": it would mean Swift inventing which of six actions
/// are destructive - a business decision `V12Cleanup` already owns - rather than waiting for a real
/// endpoint to report it. This protocol carries only the one boolean `V12DetectionResult.IsV12Project`
/// already computes (`ManifestEntry.Present` - spec #4 §5's sole trigger condition for offering
/// migration at all), and one all-or-nothing `performMigration` Swift never looks inside. Both are
/// narrow enough to be trivially replaced, never revisited-in-anger, once a real endpoint exists.
public protocol MigrationOffering: Sendable {
    /// Mirrors `V12DetectionResult.IsV12Project`. A real implementation would call a not-yet-built
    /// control endpoint wrapping `V12Detector.Detect(projectRoot)`; today only test fakes implement
    /// this at all.
    func isV12Project(projectPath: String) async -> Bool

    /// Called only after the onboarding UI has recorded an explicit user confirmation
    /// (`OnboardingViewModel.confirmMigration()`) - never speculatively, and never as a side effect
    /// of `isV12Project` returning `true`. A real implementation would call a not-yet-built control
    /// endpoint wrapping `V12Importer`'s two methods and `V12Cleanup`'s four; WHICH of those six
    /// actually run, and under what per-item confirmation, is entirely that endpoint's decision to
    /// report back - Swift decides nothing about it, only WHEN this method is reachable at all.
    func performMigration(projectPath: String) async
}
