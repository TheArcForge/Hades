# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/).

## [Unreleased] — Graph relationship & coverage correctness

Post-1.0 correctness round driven by field reports from an Addressables-heavy project. Changes treat the scanner/graph as ground truth and add honest signals where a gap is inherent (precompiled DLL types, runtime dispatch).

### Added

- **`nested_by` on `find_references_to`** — surfaces direct structural parents (nesting prefabs, prefab variants) separately from `references`, so a nested-only asset no longer reads as "unused" while `reference_count` stays free of transitive over-count
- **Honest coverage signals on relationship tools** — `static_analysis_coverage` (names reflection / runtime dispatch / DI blind spots), `package_scan` degraded, and `supertypes_external_unresolved` counts on `find_references_to` / `trace_dependencies` / inheritance queries
- **`package_scan_status`** flag in `scan_health` (`get_project_summary`)
- **Scene→prefab `instantiates` edge** — `find_references_to(prefab)` now surfaces scenes that instantiate it
- **`kind` property on `ScriptType` nodes** (class / struct / interface / enum / record)

### Changed

- **C# parser** now emits `ScriptType` nodes for enums, records, and nested types (restores coverage lost in the Phase-9 tree-sitter swap); captures `using`-aliases and generic method-invocation / property / generic-return type arguments; replaces the `base_type` node property with a `supertypes` list
- **`inherits_from` vs `implements`** is decided by the resolved supertype's kind, not base-list position (fixes missed first-party interfaces)
- **`find_prefabs_with_component`** walks the full containment chain (finds deeply-nested component hosts) and de-dups variant-inherited components (`count` excludes inherited; `total_including_inherited_variants` reported)
- **Package-tier scan** is non-destructive on failure (scan-then-reconcile instead of delete-then-rescan); longer package-tier timeout
- **Addressable group→member edge** so addressable groups surface as referrers of their members

### Fixed

- **`trace_dependencies`** no longer reports the queried file's own methods as dependencies
- **`find_references_to`** no longer over-counts via structural/transitive prefab edges, and no longer merges referrers of co-located sibling types into a `.cs` query
- **`find_components_using_pattern`** reads the new `supertypes` property (was silently broken by the `base_type` removal)
- **Inference `NullReferenceException`** on every rebuild after the first (`PatternInferenceEngine` dereferenced a never-persisted `TargetFile`); guarded, and the catch now logs the full exception
- **Test isolation** — EditMode tests no longer leave the live `GraphDatabase` singleton null after a run (they save/restore it), so the graph stays queryable post-test

### Fixed — field-fix batch (large Addressables project)

- **Rebuild-path unification** — the `Hades → Rebuild Graph` menu ran a divergent `RebuildAll` that skipped `ScanProjectSettings`/`ScanAddressables` (and the Node C# scan), so menu rebuilds produced 0 `AddressableGroup` / 0 `RenderPipelineAsset` nodes. Collapsed all entry points onto `RebuildParallel`; removed dead `RebuildAllChunked`.
- **Addressable group membership** — `AddressableAssetGroup.entries` was cast `as IList` (a `Dictionary.ValueCollection`, always null) → every group orphaned. Cast to `IEnumerable`; entries + `addressable_for` edges now populate and match the group `.asset` files.
- **Deferred-edge property loss** — `pending_edges` gained a `properties` column (schema **v3**, additive migration); a forward-reference edge now keeps its `{field}`/`{addressable:true}` enrichment through deferral instead of resolving with `NULL` properties.
- **`AddressableEntry` path collision** — entries no longer set `Path` to the real asset's path (kept in `properties.asset_path`); fixes path resolution / `trace_dependencies` landing on the entry instead of the asset.
- **Incremental edge erosion (Unity + C#)** — re-scanning a changed asset cascade-deleted inbound edges from *unchanged* assets, which were never recreated and eroded each incremental. Inbound edges are now captured before delete and re-pointed after re-scan; the C# scanner deletes a file's full node set by `file_id` (was leaking NULL-guid `ScriptType`/`ScriptMethod` nodes).
- **Pending-edge classification** — on a full pass, unresolved type-name edges (BCL/framework/attributes/generics/unscanned-package types) are now classified terminal (`external`/`unindexed`) instead of being logged "will resolve on next rebuild."
- **`query_graph` guardrail** — an unknown `from` node type now errors and lists valid types instead of silently returning `count:0`.

---

## [1.0.0] — 2026-05-31 — Phase 10: Public Release

### Added

- Self-hosted marketplace listing (`marketplace.json` in plugin repo)
- CI workflow for Bridge + Scanner tests on push/PR (`ci.yml`)
- CI workflow for auto-syncing plugin repo on release tag (`release.yml`)
- Plugin repo structural validation CI (`validate.yml`)
- Release documentation: `ReleasePipeline.md`, `plugin-publish-pipeline.md`, `anthropic-plugin-reference.md`
- Marketplace install path documented in `plugin-README.md`

### Changed

- Renamed `Skills~/` → `skills/` and `Commands~/` → `commands/` (Anthropic standard auto-discovery paths)
- Enriched `plugin.json` with `$schema`, `displayName`, author object, `license`, `homepage`, `repository`, `keywords`
- Removed explicit `"skills"` and `"commands"` fields from `plugin.json` (now auto-discovered by convention)
- Version bumps: product `0.9.5` → `1.0.0`; Bridge, Hub, and Scanner internals to `1.0.0`

---

## [0.9.5] — Phase 9: Graph Coverage Expansion

### Added

- **MetaScanner:** Creates lightweight `Asset` nodes for 16 Unity asset types (textures, meshes, audio, fonts, animations, sprites, models) from `.meta` files — no binary parsing required
- **C# reference graph:** Tree-sitter C# grammar (v0.23.5) used to extract cross-file type references; `find_references_to` on a script now returns all C# types that reference it (fields, parameters, constructors, inheritance, generics, attributes)
- **Unity builtin type seeding:** Runtime reflection seeds `MonoBehaviour`, `ScriptableObject`, `Component`, and other Unity base types as graph nodes, resolving `inherits_from`/`implements` pending edges
- **Search improvements:** `search_by_name` gains optional `path_filter` (e.g., `"Assets/"` to exclude packages) and `match_mode` (`contains`/`exact`/`prefix`) parameters; result limit capped at 200
- **Coverage visibility:** `get_project_summary` now includes an `asset_coverage` section with indexed type counts and pending edge count
- 81 Node.js scanner tests covering meta-scanner, tree-sitter parser, db-writer, and integration

### Changed

- Pending edges drop from ~67k to near-zero after MetaScanner resolves dead-end asset references
- All 89 MCP tools normalized to snake_case parameters (clean break — no backwards-compatible fallback); 33 parameters renamed across 12 tool files
- `find_references_to` on a `.cs` file now returns both asset-pointer references (from prefabs) and C# code-level references
- Scanner version bumped from 2 to 3; existing scanned assets automatically re-scanned on next build
- `npm test` split into two Jest invocations to avoid tree-sitter native binding conflicts with VM modules

---

## [0.9.1] — Phase 8: First-Run Reliability

### Fixed

- **macOS quarantine:** `getting-started.md` now recommends git URL install as the primary path; `xattr -dr com.apple.quarantine` workaround documented prominently for zip installs
- **Scanner npm install silent failure:** `node_modules` freshness now validated by `better-sqlite3/package.json` existence (not just directory presence); npm error text surfaced in graph build log; one retry with extended timeout
- **Launcher startup race:** MCP `initialize` answered locally from launcher constants — Hub not required for initial handshake; only `tools/list` and `tools/call` need the Hub; Hub startup timeout raised from 5s to 15s
- **Pending edges misleading log:** Unresolvable edges now classified as "permanent" (unscanned asset types) vs "transient"; log reads `N resolved, K unresolvable (textures, meshes, etc.)` instead of the alarming `Resolved N/M` ratio

### Added

- Hub recovery: heartbeat compares cached `(pid, port)` against `hub.json`; Unity re-registers automatically when a new Hub appears
- Build pipeline observability: each graph build step reports succeeded / expected-unresolvable / actually-broken counts; `GraphBuildLog.ReportDegraded()` accumulates degradation reasons for the final log message
- Distinct subprocess exit codes: 100 = Node.js not found, 101 = npm install failed, 3 = scanner DB error, 2 = database contention

---

## [0.9.0] — Phase 6 + 7: Polish, Ship-Readiness, Friends-and-Family

### Added

- **Hub end-to-end validation:** Claude Code → Launcher → Hub → Unity round-trip tested and confirmed; order-independent startup and domain reload resilience verified
- **Agent routing (three layers):** MCP `instructions` field in initialize response guides agents to use Hades tools instead of bash; `CLAUDE.md` auto-generated to Unity project root on server start; 22 skills copied to `~/.claude/skills/hades-*/` for Claude Desktop
- **Sync script** (`scripts/sync-plugin.sh`): produces plugin repo content (872KB, 62 files) from main repo for distribution
- `Documentation/getting-started.md`: full walkthrough for new users
- Troubleshooting guide consolidating known issues, recovery procedures, and symptom → cause → fix table
- Validation warning idempotency: `ClearOldWarnings()` strips stale warning blocks before each validation pass

### Changed

- README rewritten for external developers (installation, first use, troubleshooting)
- All version fields synchronized to 0.9.0 across `package.json`, `plugin.json`, and roadmap
- Architecture doc updated: stale references to `server.json`, port scanning, and 3-process model removed
- `ConsoleLogBuffer` SessionState key renamed from `UniClaude.ConsoleBuffer` to `Hades.ConsoleBuffer`

### Removed

- 110MB of stale `node_modules/` removed from git tracking (`Bridge~/hub/node_modules/`, `Scanner~/node_modules/`)

---

## [0.6.0] — Phase 5: Integration Polish + Tier 2

### Added

- **Tier 2 inferred memory:** Background pattern detection over Charon trace data; 4 analyzers (AcceptanceRate, TopicCluster, TimeOfDay, FailureCorrelation); inferred patterns stored at `.arcforge/memory/inferred/` (gitignored)
- **Promotion workflow:** Patterns crossing 90% confidence + 50-sample thresholds generate proposals in the existing queue; dashboard "Proposals" view gains a "From Tier 2" filter
- **68 editor-action tools migrated from UniClaude:** Scene manipulation, component management, prefab operations, material editing, animation, domain reload, asset import, and more — total MCP tool count reaches 89
- `GameObjectResolver` shared utility for resolving GameObjects by hierarchy path including inactive objects
- `ManualReloadStrategy` (`IDomainReloadStrategy`) for explicit domain reload control
- **Node.js scanner migration:** `ScriptScanner` replaced by standalone Node.js process using V8 regex (15-30x faster than Mono on `RegexOptions.Compiled`); package scanning drops from 3-5 min to ~9 seconds on 6,268 files
- `GraphBuilder.OnRebuildComplete` static event decouples Asphodel validation and inference engine from direct GraphBuilder dependencies
- 36 new C# tests for pattern inference (analyzers, promotion evaluator, integration)
- 58 Node.js scanner tests (hasher, meta-resolver, parser, db-writer, discovery, integration)

### Changed

- Cross-layer feedback loops wired: graph rebuild events trigger Asphodel validation and Tier 2 inference passes
- Workflow skills (scene-authoring, prefab-workflow, animation-workflow) updated to reference migrated editor-action tools as an alternative to C# scripting

### Fixed

- `MemoryFileWatcher` → `MemoryValidator` infinite loop on startup (CPU pegged at 100%); watcher suppressed around internal writes

---

## [0.5.0] — Phase 4: Skills Expansion

### Added

- **22 skills** available via Claude Code plugin: 11 migrated from UniClaude (unity-architect, component-design, data-modeling, scene-architecture, prefab-architecture, unity-performance, scene-authoring, prefab-workflow, animation-workflow, unity-reviewer, unity-workflow) and 11 new domain skills (unity-ui, unity-networking, unity-ai-behavior, unity-audio, unity-input, unity-shaders-urp, unity-shaders-hdrp, unity-vfx, unity-addressables, unity-ecs, unity-testing)
- Every skill integrates Graph queries and/or Asphodel memory reads for project-aware guidance
- **6 slash commands:** `/hades:status`, `/hades:rebuild-graph`, `/hades:show-traces`, `/hades:validate-memory`, `/hades:show-proposals`, `/hades:export-traces`
- Plugin structure validated: `plugin.json`, `skills/`, `commands/` discoverable by Claude Code

### Changed

- Workflow skills (scene-authoring, prefab-workflow, animation-workflow) rewritten from UniClaude MCP-tool-based patterns to C# Editor scripting patterns (`EditorSceneManager`, `PrefabUtility`, `AnimatorController` APIs) — the editor-action tools they depended on don't exist in Hades

---

## [0.4.0] — Phase 3: Asphodel (Persistent Memory)

### Added

- **Persistent project memory:** `.arcforge/memory/` directory with 6 default Tier 1 markdown templates (decisions.md, patterns.md, conventions.md, pitfalls.md, glossary.md, intent.md)
- **Validation engine (C#):** Parses YAML frontmatter validation rules, executes graph queries, writes validation status and inline HTML comments back to memory files; runs on startup, post-graph-rebuild, and on demand; 1-second per-query budget
- **Proposal queue:** Agent proposals written to `.arcforge/memory/proposals/`; accept/edit/reject UI in dashboard
- **Memory MCP tools:** `get_memory_summary`, `recall_memory`, `propose_memory_update`, `validate_memory`
- Slash commands: `/hades:validate-memory`, `/hades:show-proposals`
- **Dashboard Memory view:** File list with validation status; click to inspect content + history
- **Dashboard Proposals view:** Pending proposals with accept/edit/reject actions
- `FileSystemWatcher` detects external edits to memory files and triggers re-validation
- Atomic file writes via temp+rename to prevent corruption on crash
- Tier 1 files are git-tracked; Tier 2 `inferred/` subdirectory gitignored

---

## [0.3.0] — Phase 2: Charon (Observability)

### Added

- **CharonEmitter API:** OpenTelemetry-inspired fluent span API; every MCP tool call automatically emits a root span via `CallToolWithTracing` interceptor
- **SQLite trace database:** Full schema (traces, spans tables); WAL mode; async batched writes (500ms or 1000 spans); 30-day retention with auto-pruning
- **Cross-process trace propagation:** `X-Hades-Trace-Id` header links bridge-side and Unity-side spans into a single trace
- **Charon dashboard:** Node.js Express + React SPA; trace list with filters (date, status, name pattern); trace detail with span waterfall; local-only (127.0.0.1); port assigned by OS (`app.listen(0)`) with port communicated via temp file IPC
- Privacy controls: configurable path/content redaction; retention configurable in `.arcforge/config.yaml`
- `ProcessResolver.cs`: cross-platform utility for resolving `node` executable path via login shell (`bash -lc which`) to handle nvm/fnm/Homebrew PATH setups

### Fixed

- Dashboard process survives Unity domain reload via PID stored in `SessionState` + `Process.GetProcessById()` reattach
- `ReadToEnd` deadlock on stdout/stderr pipes eliminated; long-lived processes run detached

---

## [0.2.0] — Phase 1: Graph MVP

### Added

- **SQLite knowledge graph:** Full schema (nodes, edges, supporting tables); WAL mode; asset content hashing for change detection; scanner versioning for schema migrations
- **8 scanners:** SceneScanner (open-scene and closed-scene modes), PrefabScanner (variants + override edges), ScriptScanner (shallow Mono parsing), ScriptableObjectScanner, AddressablesScanner, MaterialScanner, ShaderScanner, ProjectSettingsScanner
- **12 MCP tools:** `get_project_summary`, `get_scene_summary`, `find_prefabs_with_component`, `find_components_using_pattern`, `find_references_to`, `trace_dependencies`, `find_orphan_scripts`, `analyze_render_pipeline`, `search_by_name`, `get_recently_changed`, `query_graph`, plus `hades_ping` (carryover from Phase 0)
- **Confidence modeling:** Every tool response includes a `confidence` block; "rebuilding" vs "current" vs "no data" states propagated correctly
- **Incremental updates:** `AssetPostprocessor` with 250ms/2000ms debouncer; diff-based update logic preserves node IDs; handles scene save, prefab save, and project change events
- `Hades: Rebuild Graph` menu command for manual full rebuilds
- Unity Package auto-registers MCP bridge with Claude Code config on install
- Setup wizard writing `.mcp.json` pointing to Hub launcher

### Changed

- Replaced bundled Mono.Data.Sqlite stubs with vendored `gilzoide/unity-sqlite-net` (Mono's stubs are reference-only and throw `InvalidProgramException` at runtime)
- MCP server switched from direct HTTP to Node.js stdio-to-HTTP bridge for Claude Desktop compatibility

### Fixed

- `AssetPostprocessor` re-entry guard (`IsBusy`) prevents infinite loop on large projects where scene scanning triggers further post-processing events
- Streamable HTTP endpoint corrected to `/rpc` (handles both POST JSON-RPC and GET SSE per MCP Streamable HTTP spec)

---

## [0.1.0] - 2026-05-10

### Added

- MCP server running inside Unity Editor with HTTP transport
- Main thread bridge (ConcurrentQueue + EditorApplication.update)
- Attribute-based tool discovery (`[MCPTool]`, `[MCPToolParam]`)
- Domain reload resilience (AutoReloadStrategy with assembly locking)
- Path sandbox for secure file operations
- Discovery file mechanism (`.arcforge/server.json`)
- EditorPrefs-backed settings (HadesSettings)
- `hades_ping` diagnostic tool
- Node.js stdio-to-HTTP bridge
- Unity Test Runner tests (NUnit)
- Bridge tests (Vitest)
- CI pipeline (GitHub Actions)
- Synthetic fixture project for integration tests
- Claude Code plugin manifest
