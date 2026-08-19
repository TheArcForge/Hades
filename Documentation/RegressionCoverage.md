# Regression Coverage — Issue → Test Traceability

Every issue addressed since the first internal-testing feedback round, mapped to the automated
test(s) that fail if its fix regresses. Compiled 2026-08-16 from a three-way audit (every listed
test re-executed green during the audit) plus a gap-fill round that closed every true gap the
audit found. Update this file when a future round fixes new findings.

**Status vocabulary.** COVERED — automated test(s) pin the fix. PARTIAL — the fix is pinned
indirectly or the strongest proof was a live check (reason given). DISPOSITIONED — deliberately
not fixed; the decision is the artifact (location given). No issue below is unmapped.

## How to verify everything

```
# .NET unit + integration (Contract, Cli, Core, Server suites; ~1863 total)
cd App~ && HADES_HOME=$(mktemp -d) dotnet test

# Swift shell packages (HadesControl 70, HadesSupervision 14, HadesApp 211)
cd Shell~/HadesControl && swift test
cd Shell~/HadesSupervision && swift test
cd Shell~/HadesApp && swift test

# Unity plugin EditMode suite (384 tests) in a throwaway batchmode project
scripts/regression/run-plugin-editmode.sh

# End-to-end tester suite (25 cases; editor-dependent cases need an attached Editor)
python3 scripts/regression/hades_suite.py --url http://127.0.0.1:7823/mcp
```

Known caveat: a handful of editor-link timing tests (`EditorProxyTests`, `AckGapTests`,
`CharonStatusTests`) can flake under full-suite parallel load; each passes in isolation. They
predate every round below and pin none of these issues exclusively.

## Tester feedback, round 1 (F1–F15)

| ID | Issue (paraphrase) | Pinning tests | Suite | Status |
|---|---|---|---|---|
| F1 | A boolean-form JSON-Schema subschema made the client discard the whole tool list | `TransportConformanceTests.NoToolSchemaContainsABooleanSubschemaAnywhere` (recursive) | P1, P2 | COVERED |
| F2 | Install-doc health check lacked the "connected, 0 tools" branch | doc-only (`Installing.md` now names all three outcomes) | — | PARTIAL (doc) |
| F3 | Marketplace packaging absent; v1.2 plugin silently bindable | deferred packaging; draft CI in `Documentation/hades-plugin-validate.yml`. Residual backlog: v1.2 cleanup does not touch the global plugin-enable list / plugin cache | — | DISPOSITIONED |
| F4 | v1.2 leftovers (hub state, configs) survived outside the project | `V12CleanupTests.*` (~50), `V12DetectorTests.*`, `Control.MigrationEndpointHttpTests.*` | — | COVERED |
| F5 | Handshake advertised assembly version, not product version | `TransportConformanceTests.InitializeReportsTheProductVersionConstant_NeverTheAssemblyVersion` | P3 | COVERED |
| F6 | Seven imported/binary asset kinds had no graph nodes (blind search, confident-zero traces) | `BinaryAssetIndexerTests.*` (13), `ImportedAssetKindTests.*`, `GraphToolsTests.TraceDependencies_ReportsADanglingDependency…`, `FindReferencesToTests.AnAssetTypeHadesDoesNotIndexGivesADifferentActionableMessage…`, `RelationshipQueryTests` dangling family, real-corpus smokes | G1–G4 | COVERED |
| F7 | Unrecognised kind filter answered silently empty | `QueryToolsTests.GraphQuery_UnrecognisedKind_…` family (11 incl. edgeKind/edgeTargetKind siblings), `ToolCallTests.SearchByName_UnrecognisedKind_…` (3) | G5, G6 | COVERED |
| F8 | Docs said to skip a "known issue" (flattened prefab create) the code had already fixed | Plugin `PrefabCommandsTests.CreatePrefab_*NestedPrefabInstance*` (3) | E4 | COVERED |
| F9 | Persisted project record froze UnityVersion/LastSeen | `ProjectServiceTests.RecordEditorAttached_*` (3), `EditorListenerTests.ValidHello_*` (2) | A1 | COVERED |
| F10 | Tag creation never flushed to disk | Plugin `ProjectSettingsApplyCommandsTests.CreateTag_FlushesTagManagerToDisk…`, `DeleteTag_…` | E3 | COVERED |
| F11 | Directory path produced the misleading "no longer on disk" error | `ReadThroughTests`, `ReferenceReadingTests`, `InspectToolTests` `…DirectoryError…` trio | — | COVERED |
| F12 | `project_run_tests` silently saved open scenes | Plugin `ProjectCommandsTests.RunTests_{EditMode,PlayMode}_NeverSavesDirtyOpenScene` (2), `EditorProjectToolsTests.ProjectRunTests_DescriptionDisclosesEditModeReloadAndDeniesSceneSave` | — | COVERED (fixed: the save was ours, not Unity's, and ran for EditMode too) |
| F13 | Unknown parameters silently dropped → unfiltered results that looked filtered | `ToolCallTests.UnknownParameter_*` (5) | G7 | COVERED |
| F14 | Asset mutations never re-indexed (stale/denied answers) | `ObservationServiceTests.*` (6), `IncrementalIndexTests.*`, `BinaryAssetIndexerTests.RenamingAFileMovesItsNode…` | E1, E2 | COVERED |
| F15 | Regression recorder captured only Editor-routed calls | `EditorProjectToolsTests.HadesRegression_*` (4) | — | COVERED |

## Tester feedback, round 2 (F16–F21)

| ID | Issue | Pinning tests | Suite | Status |
|---|---|---|---|---|
| F16 | Path traversal escaped the assets root; create overwrote unrelated files | Plugin guard family: `MaterialCommandsTests` (8), `SceneManagementCommandsTests` (7), `PrefabCommandsTests` (4), `AssetCommandsTests.MoveAsset_TraversalDestPath_…`, `AnimationCommandsTests.CreateController_TraversalPath_…` | E7, E8 | COVERED |
| F17 | Over-long path wedged the Editor on a modal import dialog | Plugin `…PathComponentOverSafeByteLimit_Refused` trio — runnable on demand via `run-plugin-editmode.sh` (suite excludes the repro by design: it wedges an Editor) | excluded | COVERED (plugin suite) |
| F18 | Interrupted batch: partial writes unreported, execution continued after "failed" | Timeout honesty pinned: `EditorProxyTests.CommandTimesOut_MessageIsHonest…`, `…ReportsHowManyWereInFlight`; a partial-progress store was deliberately not built (design note) | excluded | DISPOSITIONED |
| F19 | Forward trace ignored prefab-nesting edges the reverse query saw | `RelationshipQueryTests.TraceDependencies_WalksAnInstanceOfEdge…` family (5), `PrefabInstanceIndexingTests.TraceDependenciesWalksForwardThroughAnIndexedNestedPrefab` | E9 | COVERED |
| F20 | Repeated create silently replaced the existing file | Plugin `*_ExistingFile_Refused_*` / `*_ExistingDestFile_Refused*` set, `CreateController_AlreadyExists_ThrowsActionableError` | E10 | COVERED |
| F21 | Cycle-shaped inputs accepted (reparent under self/descendant; scene onto itself; variant base==target destroyed the base) | Plugin `SceneCommandsTests.ReparentGameObject_UnderItself/_UnderOwnDescendant_Refused`, `SceneManagementCommandsTests.DuplicateScene_OntoItself_Refused`, `PrefabCommandsTests.CreateVariant_BaseEqualsVariant_Refused…` | E11, **E13, E14** | COVERED |

## Committed hardening rounds (pre-round-1 sweep through pre-DMG)

Highlights; the full enumeration lives in the audit history. All pins verified green.

| Issue | Pinning tests | Status |
|---|---|---|
| Repo root must not be installable as the retired v1.2 plugin | `ClaudeCodePluginTests.RepoRoot_IsNotItselfAnInstallablePlugin…` | COVERED |
| Retired v1.2 tool names must never reappear in shipped skills/commands | `ClaudeCodePluginTests.ShippedSkillsAndCommands_NeverMentionARetiredV12ToolName` (40-name list) | COVERED |
| Sentinel-clamp family ("the graph six" + tool layer): truncated-flag honesty at and over the documented max | `QueryToolsTests.GraphQuery_AtTheDocumentedMaxLimit…`/`_AnOverMaxLimit…` (+fileType branch), `ToolCallTests.SearchByName_AnOverMaxLimit…`, `SummaryToolTests.GetRecentlyChanged_*` (2), `MemoryToolsTests.ValidateMemory_*` (2), `MemoryToolsTests.RecallMemory_*` (2) | COVERED |
| Lease CAS races (registry + release endpoint) | `LeaseRegistryTests.ReconcileAsync_*DoesNotClobber*` (2), `EditorsReleaseTests.Release_…DoesNotClobber…` | COVERED |
| CleanHadesHub locked-entry honesty; adjacent-duplicate JSON coalescing; authored-memory untouchability | `V12CleanupTests.CleanHadesHub_*` (3), `CleanManifest_Adjacent*` (4), `CleanHadesHub_NeverTouches*ArcforgeMemory*` (2) | COVERED |
| Observation sweep crash-proofing (broad catch; dispose race) | `ObservationServiceTests.ASyncFailureOutsideTheNarrowIoFilterIsSwallowedNotFatal`, `SyncsFinallyReleaseSurvivesADispose…` | COVERED |
| Bare-sequence YAML references (`m_Materials:` elements) captured | `UnityYamlReaderTests.CapturesABareFlowMapping…` (+3) | COVERED |
| Plugin animation non-numeric parameter default | Plugin `AnimationCommandsTests.CreateController/EditController_ParameterWithNonNumericDefault_*` | COVERED |
| Shell supervision races (stable-death state, stale handler double-loop, stop-then-respawn) | `CoreSupervisorTests` (3) | COVERED |
| Reaper pid-1 orphan guard | inspection + `stopThenLateHandlerDoesNotRespawn` no-orphan assertions (branch unreachable in-harness) | PARTIAL (by design) |
| Roots auto-adopt + announcement + caching/timeout/ambiguity/deepest-match | `RootsRouterTests.*` (7), `RootsResolutionToolCallTests.*`, `ToolSupportTests` | COVERED |
| Roots-resolved calls traced into the right traces.db | `RootsResolutionToolCallTests.RootsResolvedCall_IsTracedIntoTheResolvedProjectsOwnTracesDb` | COVERED |
| Plugin version skew warning (incl. prerelease/4-part parsing; mirrors at 1.4.0) | `PluginVersionSkewTests` (4), `ProjectsResolveTests.PluginVersionMismatch_*` (3), `CharonStatusTests` major-skew pair | COVERED |
| search_by_name kind description names imported-asset kinds | `ToolCallTests.SearchByName_KindParameterIsAdvertisedAsSupportingImportedAssetKinds` | COVERED |
| Tool count stays 32 | `CharonStatusTests.ToolCount_Stays32`, `LeaseVisibilityTests.ToolCount_Stays32` | COVERED |

## Bug hunt round 1 (uncommitted era)

| Issue | Pinning tests | Status |
|---|---|---|
| `edgeKind`/`edgeTargetKind` typos flipped `edgeAbsent` queries into vacuous-true false positives | `QueryToolsTests.GraphQuery_EdgeAbsentWithTypoed{EdgeKind,EdgeTargetKind}_Errors…` + vocabulary/case/valid-still-works siblings (8) | COVERED |
| recall_memory / validate_memory limit clamped before the +1 sentinel | `MemoryToolsTests.RecallMemory_*` (2), `ValidateMemory_*` (2) | COVERED |
| EditorProxy timeout message honest (work may still land; batch op count) | `EditorProxyTests.CommandTimesOut_*` (2) | COVERED |
| RebuildGraph serialized through the per-project gate | `ProjectServiceTests.RebuildGraph_Blocks/Releases…` | COVERED |
| Adopt-time path canonicalization (dedup by spelling) | `ProjectStoreTests.Adopt_CanonicalizesATrailingSlash…` (+ hunt-2 upgrades below) | COVERED |
| Plugin-side shared write-path guard (AssetPathGuard) incl. material.duplicate / scene.save | Plugin guard family (see F16/F20) + `PluginInstallerTests.Install_MatchesTheRealPluginSourceTreeExactly` (embedding) | COVERED |

## Bug hunt round 2 (uncommitted era)

| ID | Issue | Pinning tests | Status |
|---|---|---|---|
| R1 | Canonicalize resolved only the leaf symlink; project renamed to resolved leaf; Swift migration-offer join broke on spelling | `ProjectStoreTests.Adopt_ThroughAnIntermediateSymlinkedDirectory…OneRow`, `…NameTracksTheCallersOwnLeaf`; Swift `LiveMigrationOfferingTests.isV12ProjectMatchesResolvedStoredPath…` | COVERED |
| R2 | RootsRouter's duplicate leaf-only canonicalize re-announced known projects | `RootsRouterTests.ResolveAsync_MatchesAKnownProjectReportedThroughAnIntermediateSymlinkedAncestor` (asserts no announcement); single shared implementation (`ProjectStore.Canonicalize`) with delegates | COVERED |
| I1 | Poison class-id header aborted rebuilds and silently wedged sync | `UnityYamlReaderTests.PoisonClassIdHeader…`, `AssetIndexerTests.APoisonClassIdHeaderDoesNotAbort…` (2) | COVERED |
| I2 | SyncChanges took no gates (rebuild-vs-sweep lost updates) | `ProjectServiceTests.SyncChanges_Blocks/Releases…` | COVERED |
| I3 | Valid→unparseable file kept stale nodes forever | `AssetIndexerTests.Corrupting…RemovesItsNodes` (3), `ProjectServiceTests.CorruptingAValidPrefab…OnSync/OnRebuild` | COVERED |
| I4 | .meta-only changes invisible to the sweep | reverted after proving it corrupts `get_recently_changed` ordering; investigation documented at `IncrementalIndexTests.cs` (comment) — needs a schema-level column | DISPOSITIONED |
| I5 | Per-file indexing warnings discarded | `ProjectServiceTests.RebuildGraph/SyncChanges_SurfacesPerFileWarnings…`; endpoint surfacing: `ProjectsBuildAsyncTests.Rebuild_OnePoisonAsset_MessageNamesTheSingleFileAndItsPath`, `Rebuild_TwoPoisonAssets_…`, no-warning suffix absence | COVERED |
| I6 | Stat-after-read TOCTOU on file_state | ordering fixed; doc comment in `ProjectService.Reindex` (worst case: one redundant reindex); no race seam | PARTIAL (by design) |
| I7 | Duplicate GUIDs resolved nondeterministically | `GraphDatabaseTests.PathForGuid_DuplicatedGuid_AlwaysResolvesToTheLexicographicallyFirstPath` | COVERED |
| I8 | Rebuild left deleted-file file_state ghosts | `ProjectServiceTests.RebuildGraph_RemovesFileStateForFilesDeletedSinceLastIndex` | COVERED |
| I9 | Guid-less .meta misdiagnosed | not located in Core (likely server-side formatting) | DISPOSITIONED (open, LOW) |
| I10 | Unreadable directory read as mass deletion | `IncrementalIndexTests.AnUnreadableDirectory_PreservesItsRecordedState…`, `ScriptIndexerTests.AnUnreadableDirectory_PreservesItsNodes_OnFullReindex` | COVERED |
| I11 | Nameless declaration produced an empty-name node | `RoslynScriptScannerTests.SkipsATypeDeclarationWithNoName` | COVERED |
| M1 | `.mcp.json` cleanup deleted the whole file (other servers destroyed) | `V12CleanupTests.CleanMcpConfig_HadesPlusOtherServer_…ByteIdentical`, `…NeverDeletesTheFile`, dry-run parity; HTTP twins | COVERED |
| M2 | One unreadable memory file aborted the whole import | `MemoryStoreTests.Import_OneUnreadableSourceFile…`, `MigrationEndpointHttpTests.ImportMemory_UnreadableSourceFile…` | COVERED |
| M3 | Failed adopt-time import half-registered the project and killed retry | `ProjectServiceTests.Adopt_WhenTheArcforgeMemoryImportFails_StillAdoptsAndAnnounces_AndALaterAdoptRetriesTheImport` | COVERED |
| M4 | Concurrent imports raced (raw 500s) | `MigrationEndpointHttpTests.ImportGate_*`, `ImportMemory/ImportTraces_AnotherImportAlreadyInProgress…409` | COVERED |
| M5 | Stray temp file left on failed cleanup | `V12CleanupTests.CleanClaudeMd_MoveFailsAfterTempFileWritten…` (chflags-based, macOS) | COVERED |
| M6 | Missing project path read as confident "nothing to migrate" | `V12DetectorTests.Detect_NonexistentProjectRoot…`, `…IsAFile…`; HTTP `projectRootExists` pair | COVERED |
| M7 | UTF-16 CLAUDE.md dead-ended cleanup | `V12CleanupTests.CleanClaudeMd_Utf16EncodedFile_RefusesWithAClearMessageNamingTheEncoding` | COVERED |
| M8 | Span scan matched nested decoy containers | `V12CleanupTests.CleanMcpConfig_NestedMcpServersDecoy…` (2) + HTTP twin | COVERED |
| M9 | Read-only targets modified and re-permissioned | `V12CleanupTests.CleanClaudeMd/CleanMcpConfig_ReadOnlyFile_Refuses…` (2 of 4 sites; shared mechanism) | COVERED |
| M10 | Inconsistent migration error shapes / body-less 500s | `TryRun` wrappers; `MigrationEndpointHttpTests.CleanManifest_TargetFileBecomesUnmovableMidWrite_Returns500WithNonEmptyJsonErrorBody`, `CleanHadesHub_…NotABare500` | COVERED |
| P1 | Concurrent proposes silently destroyed each other (filename TOCTOU) | `MemoryProposalsTests.Write_ConcurrentProposesForTheSameTargetInTheSameSecond_EachLandsAsADistinctFileWithItsOwnContent` (16-way barrier) | COVERED |
| P2 | Extension-less targetFile produced an invisible orphan on accept | `MemoryStoreTests` normalization family (5 incl. validate-before-normalize theory), `MemoryProposalsTests` (2), `MemoryToolsTests` MCP-level, `MemoryEndpointHttpTests.AcceptProposal_ExtensionLessTargetFile_…VisibleViaSummaryRecallAndValidate`, cross-platform enumeration trio | COVERED |
| P3 | Accept not idempotent (re-accept duplicated content) | `MemoryEndpointHttpTests.AcceptProposal_AlreadyAccepted_SecondCallIsRefused…`, `…DeferredThenAccepted_Succeeds` | COVERED |
| P4 | SetStatus unlocked read-modify-write | `MemoryProposalsTests.SetStatus_ConcurrentCallsOnTheSameProposal…` (black-box cannot prove lost-update prevention; the test's comment says so) | PARTIAL (by design) |
| P5 | Control 400 bodies leaked `(Parameter 'x')` | `MemoryEndpointHttpTests.GetDocument_UnsafeName_400BodyContainsNoParameterNameLeak` | COVERED |
| T1 | Guard refusals untraced on multi-project servers; silent drops | `RootsResolutionToolCallTests.F13aRejection_…IsTracedIntoTheRootsIdentifiedProject`, `HoistedThrowRefusal_…`, `RefusedCall_WhenRootsMatchNoKnownProject_LogsAStructuredDropLine…`, `RefusedCall_WithAnExplicitProjectArgument_StillTracesSynchronously…`, `RefusedCall_WhenAReportedRootIsAnInvalidPathString_DegradesToNoMatch_NeverThrows` | COVERED |
| T2 | `/traces/failures` and `/slow` windowed silently | `TracesEndpointHttpTests.Failures_Truncated_*` (3), `SlowTools_Truncated_*` (2), `TracesResolveSequencesTests.Truncated_PassesThroughFromTheCaller` | COVERED |
| T3 | Same-millisecond traces rendered reverse-ordered | `TraceRetentionTests.RecentTraces_ExposesRowIdAsAMonotonicInsertionOrderTiebreaker`, `TracesGroupIntoSequencesTests.SameMillisecondTies_RenderInInsertionOrder…` | COVERED |
| T4 | Recorder doc claimed refusal capture it can't do | doc comments aligned (`RegressionRecorder.cs`); record = success-only, error normalization exists for replay | DISPOSITIONED (docs) |
| S1 | Cleanup outcome message never rendered | Swift `MigrationCleanupViewModelTests.lastActionMessageFlowsAcrossSuccessiveFailures` (per-row banner placement accepted as cosmetic) | COVERED |
| S2 | Summary rows lacked productGuid (name-keyed UI collisions) | `SummaryResolveTests.TwoProjectsSharingAName_RowsCarryDistinctProductGuids…`, `SummaryBuildAsyncTests` threading assertion, Swift `DTODecodingTests` + `MenuBarContentTests` keying | COVERED |
| S3 | Stale fixtures (impossible port-conflict state; never-exercised warning decode) | Swift `DTODecodingTests` over corrected `settings_mcp_port_in_use.json` / `projects_add.json` | COVERED |

## Bug hunt round 3 (proactive; read/transport/concurrency surfaces)

The round-3 theme: the read and transport paths were the untested siblings of the index and
handshake paths hardened earlier — the "one path has the guard, its sibling doesn't" pattern.

| ID | Issue | Pinning tests | Status |
|---|---|---|---|
| E1 | Deep asset (deep `m_Father` chain / nested field) → uncatchable `StackOverflowException` crashed the whole server | `InspectToolTests.Structure_MFatherChainExactlyAtTheDepthBound_StillSucceeds` + `…OneLevelPastTheDepthBound_ReturnsACleanDepthErrorNotACrash` + `Properties_FieldNested{Under,Past}TheDepthBound…`; `ReadThroughTests.MFatherChain{ExactlyAt,OneLevelPast}TheDepthBound…` + `ComponentField{Under,Past}…`; live-verified (4000-deep prefab → named error, server survives) | COVERED |
| E2 | Poison class-id header (`!u!4294967296`) swallowed on the read path behind a generic error | `InspectToolTests.Structure_APoisonClassIdHeader_NamesTheFileAndReasonRatherThanTheGenericSdkError` (`UnityYamlParseException` added to both read-path `Guarded` filters) | COVERED |
| E3 | `inspect_asset` value/properties depths had no output bound | `InspectToolTests.Value_AVeryLargeFieldIsCappedAndReportsTruncatedTrue` + `Value_ANormalSizedFieldIsNotMarkedTruncated` + `Properties_AVeryLargeEventArgumentIsCappedAndReportsTruncatedTrue` (2000-unit cap + `Truncated` flag) | COVERED |
| D-A | This session's own dispose-race fix moved the race to an unguarded `Wait()` → crash on the timer thread at shutdown | `ObservationServiceTests.SyncAfterDisposeReturnsQuietlyInsteadOfThrowingObjectDisposed` (acquire now in its own try/catch) | COVERED |
| D-B | `CoreSupervisor.stop()` didn't cancel an in-flight respawn → transient post-quit core | Swift `CoreSupervisorTests.stopDuringInFlightBackoffDoesNotSpawnFreshCore` (state re-check after each await) | COVERED |
| B-F1 | Post-handshake reader lost the 8KB flood-bound its handshake sibling enforces → unbounded allocation | `EditorSessionTests.ReceiveLoop_LineOverTheConfiguredCapWithNoNewline_FaultsTheSessionInsteadOfGrowingUnbounded` + `…UnderTheConfiguredCap_IsUnaffectedByIt`; plugin `HadesClientTests.OversizedRequestLineWithNoNewline_FaultsTheConnection…` (16 MiB cap both sides) | COVERED |
| B-F2 | `FromUnixTimeMilliseconds` on a range-unchecked plugin `long` threw before `RecordHeld` → lease desync | `EditorProjectToolsTests.ScriptEditingSession_Begin_ExpiresAtUtcMsAboveValidRange_ClampsAndStillRecordsTheLease` + `…BelowValidRange…`; `EditorSessionTests.AcquireLeaseAsync_ExpiresAtUtcMsOutOfRange_ClampsInsteadOfThrowing` (clamp, not throw) | COVERED |
| B-F3 | MiniJson accepted unpaired UTF-16 surrogate escapes | `MiniJsonTests.UnpairedSurrogateEscape_FailsToParse_InsteadOfProducingAnIllFormedString` + `PairedSurrogateEscape_StillParsesToTheCorrectCharacter` (both mirror copies byte-identical) | COVERED |
| B-F4 | Out-of-`int`-range `port` silently truncated | `MiniJsonTests.EditorConnectionInfo_PortOutOfRange_FailsToParse_RatherThanTruncating` + `…PortWithinValidRange_Parses` | COVERED |
| A-1 | RootsRouter's known-project loop canonicalized an untrusted stored path unguarded | `RootsRouterTests.ResolveAsync_SkipsAKnownProjectWithACorruptedStoredPath_RatherThanThrowing` (sibling's `ArgumentException` catch added) | COVERED |
| C-F1 | Token discovery files briefly world-readable (0644) in the write→chmod window | `ControlAuthTests.WriteConnectionFile_CreatesTheFileDirectlyAtMode0600` + `…NarrowsAPreExistingFilesModeTo0600`; `EditorListenerTests.Start_NarrowsAPreExistingTokenFilesModeTo0600` (atomic `UnixCreateMode` 0600 at both sites) | COVERED |

Round-3 residuals (no code change; recorded for a future decision):
- **E1 depth bound (512) vs `System.Text.Json` `MaxDepth` (64)** — a hierarchy 65–512 deep passes the depth guard but hits the pre-existing serializer depth cap, returning a JSON error rather than the clean depth message. Pre-existing (not introduced by the E1 fix), low-impact (such hierarchies are pathological). Aligning the two limits, or catching the serializer error, is a follow-up.
- **CLAUDE.md addressables claim** — the doc says the graph indexes addressables "groups, entries, and asset mappings", but there is no addressables-specific reader/indexer (they index as generic ScriptableObjects). A documentation reconciliation, not a code defect.

## Deliberately-open items (no fix, no test expected)

- **PackageCache scan-root boundary** — registry-package targets surface as honest *dangling*; documented in `GraphDatabase.TraceDependencies` doc; honesty pinned by the dangling tests; suite case G1 guards the in-project half.
- **`corresponds_to` forward-exclusion** — duplicate-hit avoidance; pinned by `TraceDependencies_IgnoresCorrespondsToEdges`.
- **`kindPattern` unvalidated** — deliberate substring semantics; documented at the `GraphQuery` validation site.
- **rebuild_graph has no timeout/cancellation** — disclosed in the tool description; pinned by `RebuildGraph_DescriptionIsHonestAboutBeingSynchronousWithNoCancellation`.
- **Auto-adopt announcement lost when the same call then errors** — pinned as a known limitation by `AutoAdoptedProject_KnownLimitation_AnnouncementIsLostWhenTheSameCallThenErrors`.
- **F18 partial-progress store** — design note; timeout honesty pinned instead.
- **F3 residual** — v1.2 cleanup does not touch the global plugin-enable list or plugin cache (backlog).

## Tester feedback, round 3 — final round (F22, F23)

The tester's round-3 bundle closed all six round-2 findings and opened two new ones. Their suite is
the authoritative copy; ours is now reconciled with it (25 cases, no ID collisions).

| ID | Issue | Pinning tests | Suite | Status |
|---|---|---|---|---|
| F22 | After a full rebuild, a move left the old path resolvable. Root cause was broader than the symptom: `DeleteNodesForPath` cleared nodes + edges + **file_state**, and 7 per-file re-indexing call sites reused it merely to clear old nodes — so every second-or-later full rebuild silently emptied `file_state` project-wide, after which no sweep could ever detect a deletion again. Split into `DeleteNodesAndEdgesForPath` (re-indexing) vs `DeleteNodesForPath` (genuine deletion). | `ProjectServiceTests.MovingAReferencedPrefab_RetiresTheOldPathAndPreservesItsInboundReference` (Theory: with/without intermediate rebuild), `…PureIncrementalHistory_NeverRebuilt_…`, `IncrementalAndRebuildBasedMoves_AgreeOnTheSurvivingReferenceCount`, `GraphDatabaseTests.DeleteNodesForPath_AlsoRemovesTheFilesFileStateRow`, `…DeleteNodesAndEdgesForPath_…LeavesFileStateUntouched` | E12 (flips to guard) | COVERED |
| F22-B | Tester also observed the incremental route reporting 0 inbound references where the rebuild route reported 2. | Investigated with a strict Unity-free construction (`Adopt` + `SyncChanges` only); **did not reproduce** — the incremental path preserves the count. Recorded as evidenced non-reproduction, not disproof; likely live-Editor save/FSEvents timing. | — | NOT REPRODUCED |
| F23 | The recommended install path pointed at a marketplace still serving the retired v1.2 plugin, and three docs disagreed about whether it existed. | Docs reconciled to one story (local `--plugin-dir` today, marketplace explicitly warned as not-yet-republished); in-app onboarding screen corrected. No automated pin — this is packaging/documentation. | — | FIXED (doc/packaging) |
| Undo | Release blocker: "batch is one undo group" was claimed by every mutation tool and never verified. | `CommandTableUndoGroupingTests.SceneApplyBatch_ThreeGameObjects_OnePerformUndo_ZeroOfThreeSurvive`, `TwoConsecutiveSceneApplyBatches_OnePerformUndo_OnlySecondBatchReverts` (control), `MaterialApplyBatch_CreatesAndPropertySet_OnePerformUndo_WholeBatchReverted`, plus the pre-existing `Apply_RegistersUndoAsOneGroup_…` test per family | — | **CONFIRMED** for scene/material/animation/prefab apply. `asset_manage`, `scene_manage`, `project_settings_apply` disclaim the property in their own docs and are correctly out of scope. |

## e2e suite case map (`scripts/regression/hades_suite.py`, 25 cases)

P1–P3 transport/handshake (F1, F5) · A1 project record (F9) · B-series read/query guards ·
G1–G7 graph guards (F6, F7, F13) · E1–E3 live-editor index/settings (F14, F10) · E4 prefab
nesting (F8) · E5/E6 identity guards · E7/E8 path safety (F16) · E9 nesting trace (F19) ·
E10 create refusal (F20) · E11, E13, E14 cycle refusals (F21) · **E12 move-after-rebuild (F22)** —
the one case declared `expect="fail"` when the tester shipped it; it flips to a guard now that F22
is fixed, and E2 (move without an intervening rebuild) is its control.
