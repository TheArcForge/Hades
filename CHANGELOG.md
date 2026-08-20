# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/).

## [2.0.0] — Standalone macOS App — 2026-08-17

Hades is now a **standalone macOS menu-bar app** rather than an in-Unity-Editor package. A .NET 10 core builds and serves the knowledge graph over MCP; the Unity plugin is optional and dials out to the app only for live-Editor features. The v1.x architecture (in-Editor MCP server, Node.js Bridge/Scanner, browser dashboard, Charon/Asphodel) is **retired** — its docs live under `Documentation/Retired/`, and its code only in git history.

This entry covers the whole standalone-app rewrite, released as `2.0.0`; the internal beta.1–beta.3 builds were not separately logged.

### Added

- **Standalone macOS app.** A SwiftUI menu-bar shell supervises a .NET 10 core through `HadesCoreReaper` (which guarantees the core dies with the app, even on SIGKILL); a local control API drives a Projects / Traces / Memory / Settings window. The core serves MCP at `http://127.0.0.1:7823/mcp`.
- **32 consolidated MCP tools.** The ~90 granular in-Editor tools folded into 32 family tools (`graph_query`, `search_by_name`, `find_references_to`, `trace_dependencies`, `inspect_asset`, `scene_apply`, `prefab_apply`, `material_apply`, `animation_apply`, the memory/settings tools, and the editor-proxy tools).
- **Binary/imported assets as graph nodes.** Textures, models, audio, fonts, shaders, and animation clips are indexed meta-only (path/name/kind/GUID), so reference and dependency queries answer for them. Targets resolved outside every scanned root (e.g. a registry package's own copy under `Library/PackageCache`) remain honestly dangling.
- **MCP roots auto-adoption.** A Unity project opened as a session root registers automatically, with a one-line announcement in the first tool result.
- **Server-side regression capture/replay** (`hades_regression`) covering the whole tool surface, not just editor-routed calls.
- **Guided migration from v1.2** — detection and cleanup of the retired hub/config/plugin state, preserving authored `.arcforge/memory/` byte-for-byte.
- **Regression-coverage matrix** ([`Documentation/RegressionCoverage.md`](Documentation/RegressionCoverage.md)) mapping every fixed issue to its pinning test, and a batchmode Unity plugin test runner (`scripts/regression/run-plugin-editmode.sh`).

### Changed

- The Unity plugin (`UnityPlugin`, version **1.4.0**) dials out to the app over a local socket instead of hosting the MCP server in-Editor; a plugin-version skew warning surfaces when the installed plugin lags the app.
- Memory conventions and proposals surface through the app's Memory window and the `/hades:*` commands rather than a browser dashboard.

### Fixed

Two rounds of internal-tester feedback and three proactive hardening passes, each landing with a regression test (traceable in [`Documentation/RegressionCoverage.md`](Documentation/RegressionCoverage.md)). Highlights:

- **A deeply-nested prefab/scene no longer crashes the server.** `inspect_asset` bounds hierarchy and node recursion (512 levels) and returns a clean error instead of an uncatchable stack overflow that took down every project.
- **Asset-path writes cannot escape the assets root or overwrite unrelated files**; over-long paths and cycle-shaped inputs (reparent-under-self, scene-onto-itself, variant whose base equals its target) are refused before any write.
- **Running tests never writes your open scene.** `project_run_tests` used to save every dirty open scene before running — for EditMode as well as PlayMode — silently modifying version-controlled files. That save is gone; a dirty scene stays dirty.
- **A corrupt asset file no longer aborts a rebuild or wedges background sync** — an unparseable file drops from the graph cleanly and is named in a warning.
- **Concurrent memory-proposal writes can't clobber each other**, and migration cleanup removes only Hades's own MCP entry, never other configured servers.
- Full 32-tool handshake acceptance, live re-indexing without a manual rebuild, honest result-truncation flags, and correct trace attribution on multi-project servers.

## [Unreleased on the v1.x line] — Project-Local Installation

Contributed by [@PaoloOranges](https://github.com/PaoloOranges).

A Unity project can now hold its entire Hades installation inside its own workspace, and does so by default. Previously the data plane was project-scoped but the control plane was not: the hub rendezvous directory was hardcoded under `$HOME`, skills were copied to `~/.claude/skills/`, and settings lived in Unity `EditorPrefs`, which is global per Unity install. Two projects on one machine therefore shared a hub process and a single set of preferences.

### Changed

- **Hades installs project-local by default.** The hub rendezvous directory moved from `~/.arcforge/hades-hub/` to `<projectRoot>/.arcforge/hades-hub/`, and skills install to `<projectRoot>/.claude/skills/`. Both are switchable per project at **Project Settings → Hades** (also reachable via the new **Hades → Settings…** menu item), and `HADES_HUB_DIR` overrides the hub directory outright for a single launcher process. Global hub scope remains fully supported and is the right choice when one Claude Code session spans a Unity project and a separate `file:`-referenced package repo — a project-local hub is not discoverable from outside the project directory, so that case falls back to the shared hub automatically. Changing hub scope takes effect on the next Claude Code session, because the launcher reads the setting at process start.
- **Settings moved out of Unity `EditorPrefs`** into `<projectRoot>/.arcforge/config.local.yaml` — a flat, gitignored, per-developer file. Port, log level, domain-reload strategy, and Charon retention are now per project instead of shared by every project on the Unity install. Existing values can be imported on first load via a one-time prompt; the EditorPrefs keys are left in place, since another project on the machine may not have been migrated yet.
- **The project `.mcp.json` now records a project-relative launcher path** — `.arcforge/hades-hub/launcher.js` instead of `/Users/you/Projects/YourGame/.arcforge/hades-hub/launcher.js`. Claude Code discovers `.mcp.json` in the directory it was started from and spawns the server with that directory as cwd, which is the same cwd the launcher walks up from to find the Unity project, so the relative path resolves to the same file with no absolute machine path in the file. The Claude Desktop config still uses an absolute path, having no project cwd.
- **The stable launcher copy is now always project-local**, at `<projectRoot>/.arcforge/hades-hub/launcher.js`, in either hub scope. It previously went into the *resolved* hub directory, which meant global hub scope pushed `~/.arcforge/hades-hub/launcher.js` into `.mcp.json`'s `args[0]`. The two were never related: the launcher resolves its hub at startup from `HADES_HUB_DIR`, a cwd walk-up, and `hub_scope` in `config.local.yaml` — never from its own location — so the hub directory was serving as a storage location for a file that only needed a version-stable path. `HadesPaths.LauncherDir` and `HadesPaths.HubDir` are now separate, and global hub scope behaves exactly as before. Under global scope you will see a project-local `hades-hub/` containing only `launcher.js`, while `hub.json`, `hub-path.json`, and `pending/` live under `$HOME`; `hub-path.json` follows the hub because `findHubEntry` reads it from there at runtime.
- **`.mcp.json` is now git-tracked rather than gitignored.** With `args[0]` byte-identical on every machine and under either hub scope, it is team configuration, not machine state — committing it means a fresh clone reaches Hades without opening Unity first. The launcher it names stays ignored, so Claude Code reports the server as failed until the Editor has run once to regenerate it. Projects that already gitignore `.mcp.json` can keep doing so; nothing depends on it being tracked.
- **The Claude Desktop config write is gated by a new `desktop_integration` setting, and now defaults to off.** It is the one Hades write that cannot be project-local — Claude Desktop is a single application with exactly one config file — so with the default local hub and skills scopes, Hades now writes nothing at all outside the project directory. Off is also the only *correct* default under a local hub: Claude Desktop spawns the launcher with a working directory outside the project, so the launcher finds no Unity project and resolves the global hub directory, while a local-scope Unity publishes `hub.json` into the project's own — the entry would be written but could never connect. Turn it on together with `hub_scope: global` (and `skills_scope: global` for the skills) to use Hades from Claude Desktop; see the roadmap for the planned fix that makes Desktop work under a local hub. Turning the setting off does not remove an existing `mcpServers.hades` entry.

### Fixed

- **Package path resolution never worked on the documented install path.** `FindPackageLauncherDir` and `FindPackageSkillsDir` guessed `<project>/Packages/com.arcforge.hades`, which only exists for an *embedded* package. A git-URL UPM install resolves to `Library/PackageCache/com.arcforge.hades@<hash>`, so both returned `null` and the stable launcher copy plus the skills install silently no-opped. Both now resolve through `PackageInfo.FindForAssembly`, matching the rest of the codebase.
- **Writing `.mcp.json` deleted every other MCP server declared in it.** `WriteProjectMcpJson` built a fresh single-entry object and wrote it over the file on every Unity server start, so a project that also declared, say, a Postgres or Playwright server lost those entries each time the Editor came up — silently, and with `.mcp.json` gitignored, git could not show what had gone. `.mcp.json` is Claude Code's project-level MCP registry, not a Hades-owned file; Hades now merges its own `mcpServers.hades` entry and leaves all sibling servers and top-level keys intact. A file that does not parse as JSON is still replaced, with a warning, so a package version bump or hub scope change can always self-heal.
- **A non-object `mcpServers` crashed the Claude Desktop config write.** `"mcpServers": null` reads back as a JSON-null `JValue` rather than C# `null`, so the existing `== null` guard passed and the following `(JObject)` cast threw; the exception was swallowed and the entry silently never written. Both config writers now share one type-checked accessor.

### Notes

- **`~/.arcforge/hades-hub/` is no longer used in the default configuration.** Nothing in it needs migrating — `hub-path.json` is regenerated on every server start, `hub.json`, `hub.lock`, and `pending/` are live runtime state of a possibly-running hub, and `launcher.js` is no longer written there at all. Hades neither moves nor deletes the folder, because it cannot know whether another project on the machine still depends on it; a one-time notice points it out instead. Delete it by hand once every project has been updated and no hub process is running.

## [1.2.0] — Graph-Grounded Convention Inference

Hades now reads a project's *conventions* directly off the knowledge graph and offers each one as a promotable memory entry — so a fresh session, or a teammate after `git pull`, starts already knowing how the project does things without anyone having written it down.

A new `ConventionInferrer` runs alongside the existing trace-based inference but reads graph *structure* instead of behaviour: six deterministic detectors recognize ScriptableObject event channels, Addressables adoption, prefab-variant strategy, ScriptableObject config data, type-naming conventions, and the render pipeline. Each fired convention is re-derived on every rebuild, so it is **self-validating** — it retracts itself the moment the structure that supports it disappears, the one guarantee statistical trace inference can't make. Conventions surface through the existing proposal queue (dashboard + `/hades:show-proposals`), and a small dismissal ledger means a rejected convention is never proposed again.

This release also hardens the memory-write path and closes three reliability/security gaps carried on the audit.

### Added

- **Graph-grounded convention inference** — a `ConventionInferrer` (a sibling to `PatternInferenceEngine`, but reading the graph rather than Charon traces) with six deterministic detectors: ScriptableObject **event channels**, **Addressables** adoption, **prefab-variant** strategy, **ScriptableObject config** data, type-**naming** suffixes, and the **render pipeline** (URP/HDRP). It runs on graph-rebuild-complete on its own throttle, separate from the periodic trace-inference pass.
- **Self-validating Tier-2 conventions** — `.arcforge/memory/inferred/convention-*.md` is reconciled on every run (written when a detector fires, deleted when it stops firing), so the inferred view can never go stale.
- **Convention promotion proposals** — each detected convention becomes a one-click-promotable proposal in the existing queue; Accept writes it to Tier-1 (`patterns.md` / `conventions.md`) with a stable marker. A dismissal ledger (`inferred/.conventions-state.json`) remembers rejections so a dismissed convention isn't re-proposed, and re-flags a promoted convention that later stops holding.
- **`GraphDatabase.FindNodesByTypeAndTier`** — a tier-scoped node query (reads project-only types for the naming detector, so engine/BCL builtins don't pollute the result).

### Changed

- **`MemoryManager.CreateProposal` accepts an optional stable id** — lets the convention inferrer write one idempotent proposal per detector (`convention-{key}`) instead of a fresh timestamped file every rebuild.

### Fixed

- **Security: path traversal in memory-file writes.** `propose_memory_update` and the proposal-accept path (C#), and the dashboard's memory API (Node), accepted caller-supplied file names and joined them into the memory directory with no validation — so a `../…` name could read or overwrite files outside `.arcforge/memory/`. Both now reject any non-basename / traversal / rooted name, on both the propose and the accept side (accept re-validates the untrusted `target_file` it reads back from the proposal).
- **A timed-out tool no longer applies twice.** A tool call that exceeded the 30-second transport timeout was reported to the client as failed, but its work item stayed queued and still executed when the main thread freed up — so a mutating tool the agent was told had failed applied late, and the agent's retry applied it a second time. Queued work now carries a deadline and is skipped once it has expired.
- **App-Nap starvation window narrowed.** The anti–App-Nap activity assertion is now acquired in the `[InitializeOnLoad]` static constructor (which runs synchronously during a domain reload) rather than inside the later, starvable `Boot` tick, so a backgrounded editor is more likely to keep running long enough to re-register the MCP server after a reload. *(A deeply backgrounded editor can still nap; this narrows the window rather than eliminating it.)*

## [1.1.0] — Graph Ownership Model, Incremental Integrity, Startup Reliability & Felt Performance

A correctness round on the incremental-update path. Every graph node now records the asset that owns it (`owner_guid`), so an asset's full node set is created, deleted, and rebuilt as a single unit. This closes a class of silent graph corruption where domain reloads and re-scans destroyed or leaked nodes, and promotes meta-scanned assets (textures, models, audio, animation, fonts, etc.) to first-class citizens of the incremental lifecycle.

It also makes Editor startup deterministic: a single ordered bootstrap replaces the per-subsystem `[InitializeOnLoad]` race so the MCP server registers *before* the (blocking) graph startup work runs, keeping it reachable across domain reloads.

A felt-performance pass takes work off the interactive hot path: the flagship graph-query tools are now index-backed instead of loading the entire node table, node properties parse lazily, and the Charon trace layer stops amplifying writes (per-query micro-spans removed) and no longer runs a synchronous `VACUUM` of the trace DB at startup.

### Added

- **`owner_guid` on every graph node** — records the GUID of the asset that owns it: root asset nodes own themselves, while sub-object children (GameObject/Component of a scene or prefab; ScriptType/ScriptMethod of a script) carry their parent asset's GUID. Enables total, asset-scoped deletion.
- **Incremental creation of meta-scanned assets** — newly added textures/models/audio/etc. now appear in the graph on import instead of only after a full rebuild.
- **`MetaAssetTypes`** — a single, parity-tested C#↔Node source of truth for the non-code asset extension→node-type map.
- **`HadesBootstrap`** — a single ordered startup composition root (Charon → Graph → Asphodel → MCP server → graph hooks → deferred startup sync) replacing eight independent `[InitializeOnLoad]` entry points.

### Changed

- **Unified node deletion onto `owner_guid`** — `DeleteNodesByOwnerGuid` (C#) / `deleteByOwnerGuid` (Node) replace the previously divergent guid-only (C#) and `file_id`-based (Node) delete paths, so scenes, prefabs, scripts, and meta assets all clean up the same way.
- **Meta assets are tracked in `scanned_assets`** with a cheap sentinel identity instead of a content hash, so the stale check never reads or MD5-hashes a binary asset.
- **Graph schema v3 → v4** (adds the `owner_guid` column). The graph rebuilds once automatically on upgrade.
- **Editor startup is one ordered bootstrap** — the MCP server's listener, hub registration, and heartbeat now start *before* the blocking graph startup sync, which is deferred to a later tick.
- **Tool calls during startup return a structured `busy`** instead of a 30-second timeout (the startup stale-scan is now covered by the busy gate, not only full rebuilds).
- **Hub routing is more forgiving** — project-path matching canonicalizes (`realpath` + case-fold), the launcher resolves the real Unity project root by walking up from its cwd, and a single-instance fallback routes an unidentifiable call (e.g. a launcher whose cwd is `/`) when exactly one Unity is open.
- **Flagship query tools are index-backed** — `find_references_to`, `trace_dependencies`, and `find_prefabs_with_component` resolve their target through the `idx_nodes_path` / `idx_nodes_name_type` indexes instead of loading and materializing the entire `nodes` table on every call (the O(N)-per-query pattern the rebuild path had already shed). `NodeRecord` properties now parse lazily — bulk reads that never touch `Properties` pay no JSON-deserialization cost.
- **Charon trace write-amplification cut** — the eight per-query `graph.query.*` micro-spans are removed, so a single graph traversal no longer emits thousands of trace rows (the tool-level `mcp.tool.*` span still records each call).
- **Editor startup no longer `VACUUM`s the trace DB** — the trace-size backstop caps by row count (delete oldest + passive checkpoint) instead of a synchronous `VACUUM` of a multi-GB `traces.db`, which could freeze startup.

### Fixed

- **Critical: domain reloads destroyed meta-scanned nodes and their reference edges.** Because textures/models/audio were never recorded in `scanned_assets`, every domain reload (i.e. every script compile) flagged them stale, deleted their nodes, and — having no scanner to recreate them — left them gone, silently dropping `material→texture` / `scene→model` edges and re-MD5-hashing the entire `Assets/` folder each time. Meta assets are now tracked and refreshed incrementally, and the stale check no longer hashes binaries.
- **Incremental scene/prefab re-scans leaked `GameObject`/`Component` nodes.** `NULL`-guid child nodes were never deleted on re-scan (only the guid-bearing root was), so each save permanently accumulated a stale copy of the asset's node set until the next full rebuild. Children are now deleted as a unit via `owner_guid`.
- **"Server hades unavailable" after domain reloads.** The MCP server's post-reload registration raced an undefined-order, main-thread-blocking graph startup; if the graph work won, the server never registered and the hub evicted it. It now registers and arms its heartbeat *before* any blocking startup work. *(A deeply App-Napped, backgrounded editor can still starve the one bootstrap tick — `wake-unity.sh` remains the recovery for that narrower case.)*
- **`PatternInferenceEngine` was silently null in every session.** `AsphodeInitializer` read `CharonEmitter.Database` once, before Charon's undefined-order init had set it, so inferred-memory analysis never ran. The ordered bootstrap now initializes Charon before Asphodel.
- **Inferred-memory analyzers were dead on arrival.** The trace emitter wrote span attributes under `tool.name`/`tool.input`, but every inference analyzer read `tool_name` — so the Charon→Asphodel loop produced no patterns even once the engine existed. Emitter, analyzers, and test fixtures now share a single `SpanAttributes` constant (so the keys can't drift apart again), and the topic analyzer no longer tokenizes the raw input-JSON blob into "topics".
- **Hub returned a raw `HTTP 500`** when forwarding a tool call to a Unity instance that had just begun a domain reload; it now returns a clean, retryable JSON-RPC error.
- **Racing launchers could spawn duplicate (zombie) hub processes** (no lock around the spawn); an exclusive spawn lock with stale-lock recovery now guarantees a single hub.
- **The hub became immortal and never picked up new builds.** Auto-exit was gated on a launcher *count* that decremented only on an explicit `disconnect` — which an abruptly-killed launcher (editor restart, crash, sub-agent teardown) never sends — so the count leaked and the hub stayed alive for days, routing stale code and accumulating stale registry state. Hub liveness is now time-based (last launcher activity), so the hub auto-exits when genuinely idle and a fresh build deploys on the next call.
- **Saving a C# script froze the editor.** The incremental graph update ran the Node `.cs` scanner synchronously on the main thread (blocking in `WaitForExit`, no progress bar), so every script save hitched the editor and tool calls during the scan timed out. The interactive path now spawns the scan off the main thread and resolves the rest of the batch when it finishes; a tool call during the scan returns a structured `busy` instead of a 30-second timeout. (Editor-startup catch-up keeps the synchronous path.)

## [1.0.0] - 2026-06-09 — Phase 10: Public Release

The first public release: Phase 10 release/distribution infrastructure plus a graph relationship & coverage correctness round driven by field reports from a large Addressables-heavy project. The correctness work treats the scanner/graph as ground truth and adds honest signals where a gap is inherent (precompiled DLL types, runtime dispatch).

### Added

- Self-hosted marketplace listing (`marketplace.json` in plugin repo)
- CI workflow for Bridge + Scanner tests on push/PR (`ci.yml`)
- CI workflow for auto-syncing plugin repo on release tag (`release.yml`)
- Plugin repo structural validation CI (`validate.yml`)
- Release documentation: `ReleasePipeline.md`, `plugin-publish-pipeline.md`, `anthropic-plugin-reference.md`
- Marketplace install path documented in `plugin-README.md`
- **`nested_by` on `find_references_to`** — surfaces direct structural parents (nesting prefabs, prefab variants) separately from `references`, so a nested-only asset no longer reads as "unused" while `reference_count` stays free of transitive over-count
- **Honest coverage signals on relationship tools** — `static_analysis_coverage` (names reflection / runtime dispatch / DI blind spots), `package_scan` degraded, and `supertypes_external_unresolved` counts on `find_references_to` / `trace_dependencies` / inheritance queries
- **`package_scan_status`** flag in `scan_health` (`get_project_summary`)
- **Scene→prefab `instantiates` edge** — `find_references_to(prefab)` now surfaces scenes that instantiate it
- **`kind` property on `ScriptType` nodes** (class / struct / interface / enum / record)

### Changed

- Renamed `Skills~/` → `skills/` and `Commands~/` → `commands/` (Anthropic standard auto-discovery paths)
- Enriched `plugin.json` with `$schema`, `displayName`, author object, `license`, `homepage`, `repository`, `keywords`
- Removed explicit `"skills"` and `"commands"` fields from `plugin.json` (now auto-discovered by convention)
- Version bumps: product `0.9.5` → `1.0.0`; Bridge, Hub, and Scanner internals to `1.0.0`
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
