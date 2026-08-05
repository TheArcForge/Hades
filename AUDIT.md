# Hades — Architecture & Code Audit

> Working plan. Each item is a checkbox. Check it off when fixed **and** verified (test or manual repro).
> Findings were produced by 9 specialized reviewers and then **adversarially verified** against the source — every item below cites `file:line` evidence that was personally confirmed. 13 additional claims were refuted and dropped (see [Checked & cleared](#checked--cleared)).

**Audit date:** 2026-06-12 · **Version reviewed:** v1.0.0

## Legend

- `[ ]` not started · `[~]` in progress · `[x]` done & verified
- Severity: 🔴 critical · 🟠 high · 🟡 medium · 🟢 low

## Progress

> **v1.1.0 shipped 2026-06-17.** The entire 🔴 critical + felt-performance tier landed — corruption fixes (#1, #7), the responsiveness bootstrap (#6), the revived Charon→Asphodel plumbing (#6/#8), index-backed queries (#2), the off-thread per-save `.cs` scan (#3), Charon trace-write reduction + no startup VACUUM, and the hub-reliability cluster. Items marked `[x]` below are **released**; the remainder is the v1.2+ backlog. (One high, App-Nap bootstrap starvation, was *promoted* from a residual and remains open.)

| Severity | Count | Done |
|---|---|---|
| 🔴 Critical | 1 | 1 |
| 🟠 High | 9 | 5 |
| 🟡 Medium | 22 | 0 |
| 🟢 Low | 17 | 2 |
| Missing features (vision drift) | 4 | 0 |

**Two root causes worth internalizing:**
1. The **incremental-update path** never got the rigor the full-rebuild path did — that's where the corruption and main-thread freezes live (#1, #3, #7).
2. The **Charon/Asphodel intelligence layer** was built end-to-end but the wires between stages were never integration-tested — each stage works alone, the chain is dead (#6, #8, outcome capture, trace propagation).

---

## 🔴 MCP responsiveness — post-reload server-bootstrap starvation

> **Separate investigation (2026-06-13).** Root-caused live this session when Unity went persistently "Server hades unavailable" after domain reloads and `wake-unity.sh` couldn't recover it. This is the "make Unity *always* responsive" workstream. Tackle as its own change (not bolted onto the corruption refactor).

**The root cause (evidence chain, all confirmed in code):**
1. After a domain reload, two `[InitializeOnLoad]` `delayCall`s queue on the main thread in **undefined order** (AUDIT #6): `MCPServer.Start()` and `GraphUpdateHandler.CheckStartupSync()`.
2. `MCPServer.Start()` does **all three** of {start HTTP listener, register with hub, arm the background heartbeat timer} on the main thread — [MCPServer.cs:100/107/124](Editor/MCP/MCPServer.cs).
3. `CheckStartupSync()` runs **synchronously** and can block the main thread for many seconds (full rebuild, or stale-asset MD5 storm) — [GraphBuilder.cs:1368](Editor/Graph/GraphBuilder.cs).
4. If `CheckStartupSync` wins the race and blocks, `Start()` can't run → no listener, no registration, no heartbeat.
5. `OnBeforeReload` already marked the instance `transient` ([MCPServer.cs:72](Editor/MCP/MCPServer.cs)); after 30s the hub probes the port, finds no listener, marks it `stale` ([heartbeat.ts:16-26](Bridge~/hub/src/heartbeat.ts)).
6. `findByProjectPath` **excludes `stale`** ([registry.ts:101](Bridge~/hub/src/registry.ts)) → "No Unity instance found" ([mcp-handler.ts:22](Bridge~/hub/src/mcp-handler.ts)) → surfaced as "Server hades unavailable."
7. The background heartbeat (the designed safety net for a stalled main thread) is **only armed inside `Start()`** — a starved bootstrap has no safety net.

**Why `wake-unity.sh` doesn't help:** it un-naps an *idle* main thread; here the thread is **busy** running `CheckStartupSync`, not napping, so foreground focus can't preempt it.

**This session's amplifier:** the half-applied refactor (owner-deletes in, meta-tracking not yet, no clean rebuild) made `CheckStartupSync` re-MD5 every binary asset on every reload and never converge (AUDIT #1) — a long main-thread block each reload. Completing the refactor removes this amplifier but not the architectural race.

**Fix direction (make reachability independent of graph work):** — **IMPLEMENTED** via `HadesBootstrap` (spec/plan: `docs/superpowers/{specs,plans}/2026-06-13-mcp-responsiveness-bootstrap.*`). Verified: 4 new tests + full 407-test suite green; server stays reachable across reloads (no `wake-unity`). *shipped in v1.1.0*
- [x] **Single ordered composition root** (the AUDIT #6 fix): start `MCPServer` (listener + register + heartbeat) **before** kicking off `CheckStartupSync`, so the hub's 30s transient-probe finds the listener alive and marks it healthy even while graph work churns.
- [x] **Arm the heartbeat timer as early as possible** — armed in `Start()` at boot step 4, before the deferred startup sync; `AppNapGuard` held across boot + the deferred tick.
- [ ] **Stop graph startup blocking the main thread** (AUDIT #3): run full rebuild / Node scan off-thread or chunked. *(Deliberately out of scope — the chosen design keeps the rebuild on the main thread and returns `busy`; this is the deeper residual.)*
- [x] Add a test/repro: `HadesBootstrapTests` (server-before-sync ordering + #6 guard) and `StartupBusyGateTests` (busy, not timeout, during startup).
- [ ] **🟠 App Nap starves the bootstrap tick → editor unreachable after a backgrounded reload** *(promoted from a residual footnote — now the dominant interaction friction, the hub/routing friction having been resolved; see the Hub/Node cluster).*

**Problem:** the nap opt-out is acquired *inside* `Boot()` (`AppNapGuard.Acquire()`), but `Boot` runs on an `EditorApplication.delayCall` scheduled from the `[InitializeOnLoad]` static ctor. A deeply App-Napped, backgrounded editor throttles the editor-update tick that would *run* `Boot`, so `Boot` — and with it the guard, the `MCPServer` (re)start, and hub re-registration — is delayed indefinitely. The guard that would prevent nap can't engage because nap is starving the very tick that acquires it.

**Evidence (2026-06-14 session):** after a test-run reload with the editor backgrounded, `MCPServer` never re-registered (`No Unity instance found for …`, instances: none) and `HadesBootstrapTests` failed because `Boot` had not populated `BootTrace` / `InferenceEngine` before the tests read them. `wake-unity.sh` (foreground the editor) was the only recovery.

**Fix direction:** call `AppNapGuard.Acquire()` in the `[InitializeOnLoad]` **static constructor** (runs synchronously during the reload, before any `delayCall`), and release once `Boot` / `RunStartupSyncOnce` completes. `AppNapGuard` is a token-based `NSProcessInfo beginActivityWithOptions:` assertion that holds until `endActivity` — it needs **no** update ticks to sustain — so an assertion set at reload time keeps the editor un-napped through the `delayCall` window, closing the starve. Pair the ctor-`Acquire` with exactly **one** `Release` (e.g. in `RunStartupSyncOnce`'s `finally`) so the assertion isn't held forever (permanent nap-block = battery drain). Alternative/complement: an external watchdog (launcher-side) that notices "instance was healthy, reloaded, and never re-registered within N s" and nudges the editor. Add a backgrounded-reload repro.

---

## Priority 1 — Stop the corruption

> The graph silently rots on every compile; everything downstream trusts it. Do these first.

### 🔴 1. Domain reload destroys meta-scanned nodes + their edges, and re-hashes the entire `Assets/` folder
- [x] **Fixed & verified** — owner_guid model + meta lifecycle. Clean firstBoot rebuild: Texture/Model/AudioClip 100% present & owner-stamped; stale check skips binary hashing. *shipped in v1.1.0*

**Where:** [GraphBuilder.cs:1432](Editor/Graph/GraphBuilder.cs) — `CheckStaleProjectAssets` / `UpdateAssets`

**Problem:** A domain reload fires on **every script compile**, not just editor open. On each one, `CheckStaleProjectAssets` flags any `Assets/` path with no `scanned_assets` row as stale — **with no check that a scanner exists for it.** Textures/models/audio/anims (`.png/.fbx/.wav/.anim`) never get a `scanned_assets` row (the Node meta-scanner inserts their nodes but never records them). So on every reload, per such asset, the code:
1. MD5-hashes the **full file content** inside an open write transaction (a 2 GB `.psd` is read whole, every compile),
2. `DeleteNodesByGuid` deletes the Texture/Model node,
3. `ScanAsset` returns immediately (no C# scanner handles that extension) — **node never recreated, `RecordScannedAsset` never runs** → same work next reload.

The inbound-edge restore then finds no target, so **`material→texture` and `scene→model` reference edges are silently destroyed** after the first reload following a full rebuild. Contradicts LIMITATIONS.md ("incremental and near-instant") and corrupts the graph — the product's core artifact.

**Fix:** In `CheckStaleProjectAssets`, skip paths with no registered scanner (mirror the existing scanner-version branch); never `DeleteNodesByGuid` when nothing can recreate the node; **or** record meta-scanned assets in `scanned_assets` during the Node meta-scan.

**Acceptance test:** full rebuild → domain reload → assert Texture node count **and** `material→texture` edge count unchanged, and no MD5 of binary assets occurred.

---

### 🟠 7. Incremental scene/prefab re-scan orphans `GameObject`/`Component` nodes → unbounded table growth
- [x] **Fixed & verified** — `DeleteNodesByOwnerGuid`; re-scan-stability unit test passes; clean rebuild shows GameObject/Component 100% owner-stamped. *shipped in v1.1.0*

**Where:** [GraphBuilder.cs:642](Editor/Graph/GraphBuilder.cs) · [GraphDatabase.cs:546](Editor/Graph/GraphDatabase.cs) · [SceneScanner.cs:76](Editor/Graph/Scanning/SceneScanner.cs)

**Problem:** Scene/Prefab scanners emit child nodes with `guid=NULL`, no `parent_node_id`, `FileId=GetInstanceID()`. On re-scan, `DeleteNodesByGuid(sceneGuid)` runs `DELETE FROM nodes WHERE guid = ?` — matching **only the root node**. NULL-guid children are never deleted (edge `CASCADE` removes their edges but not the node rows), and re-scan inserts a fresh child set (`INSERT OR IGNORE` can't dedup them — the unique index is filtered `WHERE guid IS NOT NULL`). **Every scene/prefab save permanently leaks its previous GameObject/Component set** until a full rebuild, inflating `search_by_name` and type-count queries. The Node scanner solves this for scripts via `deleteFileNodes` (`DELETE … WHERE file_id = ?`); no Unity-side analogue exists.

**Fix:** Before re-scanning, delete the asset's full child set — set `parent_node_id` on children and delete recursively, capture the root id and delete descendants via `contains` edges, or stamp children with the owning asset's guid and delete by it.

**Acceptance test:** save the same scene/prefab twice → assert GameObject node count is stable.

---

## Priority 2 — Unblock the main thread

> What an end user actually *feels*: editor freezes and agent timeouts on the core "agent edits C#" workflow.

### 🟠 3. Incremental `.cs` updates block the main thread synchronously; busy-gate doesn't cover them → agent-visible timeouts
- [x] **Fixed & verified (v1.1.0)** — the interactive debouncer path now runs the `.cs` Node scan OFF the main thread: `ProcessResolver.Start` spawns it non-blocking and `GraphBuilder.PumpCsScan` (driven from `EditorApplication.update`) runs the deferred rest-of-batch continuation when the subprocess exits. A new `_csScanInFlight` flag keeps `IsBusyForRequests` true for the scan window, so a concurrent tool call gets a structured `busy` (not a 30s timeout); `_status` stays `Updating` so `GraphAssetPostprocessor`'s drop keeps a main-thread DB write from racing the subprocess. Startup catch-up keeps the synchronous path (`deferCsScan: false`). Full EditMode suite green except 2 unrelated, App-Nap-flaky `HadesBootstrapTests` (Boot's `delayCall` starved before the tests read its output — not a code fault). No-freeze behavior confirmed in real use post-release. *shipped in v1.1.0*

**Where:** [GraphBuilder.cs:598](Editor/Graph/GraphBuilder.cs) — `UpdateAssets`

**Problem:** Debouncer flush runs on the main thread. For a changed `.cs` file it calls `RunNodeScanner`, blocking in `Process.WaitForExit` (300 s timeout); on DB contention it does `Thread.Sleep(1000)` and re-runs the whole scan; if `node_modules` is invalid it can run `npm install` inline. **No progress bar** — the editor just freezes after a script save. `MCPServer.EnqueueAndWait` only returns the structured "busy" response for `IsInLongOperation` (Rebuilding/ScanningPackages), **excluding `Updating`** based on a comment claiming incrementals "finish in well under a frame" — false for the Node-spawning `.cs` branch. Tool calls during the freeze wait out the 30 s transport timeout and get a raw `-32000 Timeout`.

**Fix:** Run the incremental Node scan off the main thread (it writes the DB itself; only post-scan checkpoint/resolution needs the main thread); **or** at minimum set a status flag so `EnqueueAndWait` returns the busy response during `.cs` incrementals, and use a short (30 s) timeout for incremental mode.

---

### 🟠 4. The 30 s HTTP timeout doesn't cancel the queued work item → non-idempotent tools can apply twice
- [ ] **Fixed & verified**

**Where:** [HttpTransport.cs:150](Editor/MCP/Transport/HttpTransport.cs)

**Problem:** `Task.WhenAny(responseTask, Task.Delay(30000))` returns a timeout error and closes the response, **but the `WorkItem` stays in `_workQueue` and still executes** when the main thread unblocks. For mutating tools (`scene_delete_gameobject`, `prefab_apply_overrides`, `asset_move`, `component_set_property`) the edit lands *after* the client was told it failed; the agent retries → mutation applies **twice**. This is exactly the at-least-once-on-non-idempotent-writes hazard the `EnqueueAndWait` comment says it avoids for the busy path — the timeout path has no such guard.

**Fix:** Carry a `CancellationToken` into `ProcessMainThreadQueue` and skip execution if the request already timed out (check `item.Completion.Task.IsCanceled` before invoking the dispatcher), or add client idempotency-key dedup. *(Fixing #3 removes the main trigger; asset imports / slow tools remain.)*

---

### 🟠 2. The flagship query tools load the entire `nodes` table into memory on every call
- [x] **Fixed & verified** — added indexed `FindNodesByPath` (`idx_nodes_path`) + `FindNodesByNameAndTypeAll` (`idx_nodes_name_type`); `find_references_to` / `trace_dependencies` / `find_prefabs_with_component` route through them instead of the `SearchByName(null,null)` full-table scan, and `NodeRecord.Properties` parses lazily (raw JSON kept, parsed on first access). `FindNodesByPath` preserves `SearchByName`'s `ORDER BY name` so `trace_dependencies` still starts from the ScriptType, not the Script. Full EditMode suite green (404 passed / 0 failed); new `IndexedQueryTests`. *shipped in v1.1.0*

**Where:** [GraphQueryTools.cs:428](Editor/MCP/Tools/GraphQueryTools.cs) (also `:689`, `:255`)

**Problem:** `find_references_to` and `trace_dependencies` resolve a path via `db.SearchByName(null, null)` → `SELECT * FROM nodes WHERE name LIKE '%' ORDER BY name` (full-table scan + sort), then LINQ-filter in C#. `NodeRecord.PropertiesJson`'s setter eagerly `JsonConvert.DeserializeObject`s every row. The code's own comment cites 700K-node graphs — each call allocates ~700K objects + 700K JSON parses **on the main thread** to find one node, while `idx_nodes_path` exists and is unused. `find_prefabs_with_component` does the same over every `Component` node despite `idx_nodes_name_type`. This is the O(N) pattern the rebuild path was optimized to remove, surviving on the hot query path. (Secondary: `name LIKE '%'` excludes NULL-name rows, so a NULL-name node can never be found by path.)

**Fix:** Add `FindNodesByPath(string path)` (`WHERE path = ?`, uses `idx_nodes_path`) and route both tools through it; use `WHERE name = ? AND type = ?` in `find_prefabs_with_component`; make `Properties` lazily parsed (store raw JSON, parse on first access).

---

## Priority 3 — Revive the dead intelligence loop

> Charon→Asphodel is the moat. Right now it's three layers of silently-disabled wiring.

### 🟠 6. Startup is 7 undefined-order `[InitializeOnLoad]` entry points → inference engine is silently null in every real session
- [x] **Fixed & verified** — `HadesBootstrap` orders Charon→Asphodel, so `InferenceEngine` is non-null (regression-guarded by `Boot_InitializesCharonBeforeAsphodel...`). *shipped in v1.1.0*

**Where:** [AsphodeInitializer.cs:56](Editor/Asphodel/AsphodeInitializer.cs)

**Problem:** Seven `[InitializeOnLoad]` classes (`GraphInitializer`, `GraphUpdateHandler`, `PackageChangeDetector`, `CharonInitializer`, `AsphodeInitializer`, `MCPServer`, `PrefabTools`) each register their own `delayCall`. Unity doesn't define their order. The code patches around this inconsistently (`GraphUpdateHandler` calls `EnsureDatabase`; `AsphodeTools.GetValidator` lazily re-creates). But `AsphodeInitializer` reads `CharonEmitter.Database` **exactly once** and leaves `InferenceEngine` null forever if Charon ran later — no retry, no error. Editor logs across 3 sessions show Asphodel consistently inits **before** Charon → the inference engine is null in every real session. The predicted failure is already happening.

**Fix:** A single composition root (`HadesBootstrap`, one `[InitializeOnLoad]`) that inits Charon → Graph → Asphodel → MCP in explicit order and tears down in reverse on `beforeAssemblyReload`/`quitting`; **or** lazily construct `InferenceEngine` on first use (mirror the `GetValidator()` pattern). The defensive lazy-init patches can then be deleted.

---

### 🟠 8. Charon → Asphodel feedback loop is dead: `tool.name` vs `tool_name` attribute-key mismatch
- [x] **Fixed & verified** — shared `Charon.SpanAttributes` constant used by emitter + all analyzers + fixtures (can't drift again); `TopicClusterAnalyzer` also stops tokenizing the input blob. Inference suite 41/41 (incl. end-to-end promotion); full suite 408, 0 failed. *shipped in v1.1.0*

**Where:** [MCPDispatcher.cs:138](Editor/MCP/MCPDispatcher.cs) · [AcceptanceRateAnalyzer.cs:39](Editor/Asphodel/Inference/AcceptanceRateAnalyzer.cs) · `TopicClusterAnalyzer.cs:57` · `FailureCorrelationAnalyzer.cs:14`

**Problem:** The only place tool calls are traced (`CallToolWithTracing`) sets span attributes `tool.name` / `tool.input` (dots). Every inference analyzer keys on `tool_name` (underscore). `AcceptanceRateAnalyzer` does `if (!span.Attributes.ContainsKey("tool_name")) continue;` — never true → **zero patterns, always.** `TopicClusterAnalyzer`'s exclusion never fires so it tokenizes the raw input JSON as "topics." This is the vision's load-bearing "repeated behaviors become inferred preferences" loop (§3.4); 3 of 4 analyzers are dead on arrival. The synthetic test fixtures use the underscore key too, so **the tests hide the mismatch.**

**Fix:** Standardize on one key (dispatcher and analyzers); re-point fixtures to the production key; add one integration test feeding dispatcher-shaped spans through each analyzer and asserting non-empty output, so the key contract is enforced.

---

### Missing — Outcome capture (accepted/rejected/edited) does not exist anywhere
- [ ] **Implemented or vision down-scoped**

**Where:** [CharonDatabase.cs:38](Editor/Charon/CharonDatabase.cs) (no outcome column) · [AcceptanceRateAnalyzer.cs:108](Editor/Asphodel/Inference/AcceptanceRateAnalyzer.cs)

**Problem:** Vision §3.3.2 lists "Outcome (accepted, rejected, edited)" in the trace structure, §5.6 builds an eval scenario on "acceptance rate 73%→81%," §8.2 makes acceptance rate a **success criterion** — none marked "Planned." In code there's no outcome column on traces/spans and no API to tag one. The eval/acceptance-rate story has **no data source** (and combined with #8, the inference loop has neither inputs nor working consumers).

**Fix:** Either implement a real outcome-tagging path (an MCP tool the plugin calls to record an outcome, persisted to a new column) **or** explicitly down-scope the vision/eval claims to "inferred acceptance" and mark them Planned.

---

## Priority 4 — Lock hygiene & security hardening

### 🟠 5. `EndScriptEditing` / `project_recompile_scripts` unlock assemblies behind the strategy's back → unbalanced unlock
- [ ] **Fixed & verified**

**Where:** [DomainReloadTools.cs:28](Editor/MCP/Tools/DomainReloadTools.cs) · [AutoReloadStrategy.cs:58](Editor/MCP/DomainReload/AutoReloadStrategy.cs) · [MCPServer.cs:157](Editor/MCP/MCPServer.cs)

**Problem:** Both tools call `EditorApplication.UnlockReloadAssemblies()` directly while `AutoReloadStrategy._locked` stays `true`. Result: (a) the lock releases mid-turn though the strategy thinks it's still protecting calls — a reload from the accompanying `AssetDatabase.Refresh` can tear down `MCPServer` and drop the in-flight queue; (b) the 120 s safety timeout calls `UnlockReloadAssemblies()` **again**, underflowing Unity's refcount → "Unbalanced calls to Lock/UnlockReloadAssemblies." Compounded by `NotifyTurnComplete()` (the clean per-turn unlock) having **zero callers**, so the 120 s timeout is the only real unlock path on a non-script turn.

**Fix:** Route these tools through the active strategy (add `ForceUnlock`/`Reset` on `IDomainReloadStrategy` that flips `_locked=false` and unlocks exactly once). Ensure exactly one Unlock per Lock. Wire `NotifyTurnComplete` to a real turn boundary (a Claude Code Stop hook, or queue-idle heuristic) or remove it.

---

### 🟡 Hub HTTP surface has zero authentication
- [ ] **Fixed & verified**

**Where:** [server.ts:107](Bridge~/hub/src/server.ts)

**Problem:** Hub binds `127.0.0.1` (good) but has no token/auth on any endpoint. Any local process (malicious npm dep, another user on a shared machine, browser via DNS-rebinding-style POSTs) can `POST /rpc` with an arbitrary project header to drive a victim's Unity editor through destructive tools, `POST /api/register` to inject a fake instance, or `POST /api/deregister` to evict instances. `127.0.0.1` does not protect against same-host code. Not covered by SECURITY.md's "requires prior local access" carve-out for the *driving-the-editor* vector.

**Fix:** Generate a per-session shared secret in `hub.json` (user-readable only), require it as a header on every hub endpoint; reject requests lacking it. Validate `Origin`/`Host` (anti-DNS-rebinding).

---

### 🟡 MCP server has no Origin/Host validation (CSRF / DNS-rebinding)
- [ ] **Fixed & verified**

**Where:** [HttpTransport.cs](Editor/MCP/Transport/HttpTransport.cs)

**Problem:** The localhost MCP HTTP server checks no `Origin`/`Host`, so a browser page can POST to it and reach project-mutating tools. (CORS preflight blocks the *naive* case for custom-header `/rpc`, but simple-request endpoints remain exposed.)

**Fix:** Reject requests whose `Host` isn't `127.0.0.1[:port]`/`localhost`; reject cross-origin `Origin` headers.

---

### 🟡 `propose_memory_update` filename is unsanitized → path traversal
- [ ] **Fixed & verified**

**Where:** [AsphodeTools.cs:224](Editor/MCP/Tools/AsphodeTools.cs) · [MemoryManager.cs:97](Editor/Asphodel/MemoryManager.cs) · `Dashboard~/src/memory-db.ts:147`

**Problem:** The MCP tool takes a model-supplied `file` string and passes it unmodified to `CreateProposal`, which builds the proposal id as `{timestamp}-{file}`. On accept, both the (dead) C# `MemoryManager.WriteFile` and the (live) TS `acceptProposal` do `Combine/join(memoryDir, targetFile + ".md")` with no traversal guard. A `file` like `../../evil` escapes the memory dir.

**Fix:** Validate `file` against the known memory file set (`decisions`/`patterns`/`conventions`/`pitfalls`/`glossary`/`intent`), or strip to a basename and reject separators / `..` before building any path — on **both** the propose and accept sides.

---

### 🟡 Dashboard memory API does FS writes/unlinks from URL path params
- [ ] **Fixed & verified**

**Where:** `Dashboard~/src/` (memory API endpoints)

**Problem:** Filesystem writes/unlinks driven by URL path params with weak traversal protection. Same class as the above; the dashboard is the *live* memory-apply path.

**Fix:** Canonicalize + whitelist the same way as the propose/accept fix.

---

## Priority 5 — Structural debt (pays down recurrence)

### 🟡 `graph.db` schema is hand-duplicated between C# and the Node scanner, with positional reads and no parity check
- [ ] **Fixed & verified**

**Where:** [GraphDatabase.cs:70](Editor/Graph/GraphDatabase.cs) vs [db-writer.js:13](Scanner~/src/db-writer.js)

**Problem:** `CreateAllTables` and the JS `SCHEMA` are two independently maintained DDL copies; both write the same tables. Nothing enforces parity (JS neither creates nor checks `schema_version`; no cross-language test). C# reads nodes **positionally** (`ReadNodeFromStatement` hardcodes column indices; a comment notes `ALTER ADD COLUMN` "breaks our positional SELECT * reads") — so column-order drift produces silently wrong data, not an error. Drift already started: JS omits `schema_version`, `pending_invalidations`, the `pending_edges.properties` column, and two pragmas.

**Fix:** Single source of truth for the DDL (generate both sides from one schema file, or have one process own creation). Replace positional reads with column-name lookups. Add a cross-language schema-parity test.

---

### 🟡 `SceneScanner` additively opens & force-closes scenes with no Play-mode or dirty-scene guard
- [ ] **Fixed & verified**

**Where:** [SceneScanner.cs:76](Editor/Graph/Scanning/SceneScanner.cs)

**Problem:** Closed-scene mode uses `EditorSceneManager.OpenScene` additive then closes without saving — during both full rebuilds and incremental updates — with no guard for Play mode or scenes with unsaved changes. Mutates live editor state.

**Fix:** Guard on `EditorApplication.isPlayingOrWillChangePlaymode` and dirty scenes; skip or defer closed-scene scans in those states.

---

### 🟡 Graph domain logic lives in the MCP tool layer; the Graph layer exposes only CRUD
- [ ] **Fixed & verified**

**Where:** [GraphQueryTools.cs:262](Editor/MCP/Tools/GraphQueryTools.cs)

**Problem:** Reference semantics, containment ascent, variant dedup, sibling-type suppression, the `query_graph` mini-engine — all in static `GraphQueryTools`, reachable only via `GraphDatabase.Instance`. Any non-MCP consumer (menu items, future API) can't reuse it.

**Fix:** Extract a `GraphQueryService` in `Editor/Graph` holding the domain semantics; the MCP tools become thin adapters over it.

---

### 🟡 `GraphDatabase` constructor mutates a global singleton
- [ ] **Fixed & verified**

**Where:** [GraphDatabase.cs:32](Editor/Graph/GraphDatabase.cs)

**Problem:** Constructor ends with `_instance = this;`, so constructing *any* DB (e.g. a test temp DB) silently re-points the global `Instance` that all 25 call sites resolve. A `RestoreInstanceForTests` escape hatch exists solely to undo this; every test fixture pays for it.

**Fix:** Remove the constructor side-effect; set `Instance` explicitly at the composition root. Inject the DB into tools/services rather than resolving a global.

---

### 🟡 `ScannerRegistry` `.asset` collision is last-write-wins over undefined reflection order
- [ ] **Fixed & verified**

**Where:** [ScannerRegistry.cs:38](Editor/Graph/Scanning/ScannerRegistry.cs)

**Problem:** `_extensionMap[ext] = scanner` — last enumerated wins. `ProjectSettingsScanner` and `ScriptableObjectScanner` both declare `.asset`; which one scans every ScriptableObject depends on `Assembly.GetTypes()` order. `GraphBuilder` has a comment admitting the collision and works around only the ProjectSettings side.

**Fix:** Detect collisions explicitly; route `.asset` by content/location (ProjectSettings path vs `Assets/`) instead of by a single extension winner.

---

### 🟡 Memory accept/merge logic is duplicated in C# (dead) and TypeScript (live) with divergent safety behavior
- [ ] **Fixed & verified**

**Where:** [MemoryManager.cs:125](Editor/Asphodel/MemoryManager.cs) vs `Dashboard~/src/memory-db.ts:140`

**Problem:** The proposal review loop is served by the Node dashboard (`/proposals/:id/accept|reject`). The C# `MemoryManager.AcceptProposal/RejectProposal/ListProposals` implement the same merge/delete logic but have **no production caller** (tests only), and the two diverge on safety.

**Fix:** Pick one owner (the live path is Node). Delete the unused C# proposal-apply methods or route the dashboard through a single shared contract; don't maintain two.

---

### 🟡 Performance cluster
- [~] **N+1 query patterns in traversal tools** — the per-micro-query `graph.query.*` Charon spans are **removed** (8 of them in `GraphDatabase`), so a single traversal no longer emits thousands of trace rows (the tool-level `mcp.tool.*` span still records each call); the underlying one-to-two SQL statements per node/edge remain. [GraphDatabase.cs](Editor/Graph/GraphDatabase.cs) · [GraphQueryTools.cs](Editor/MCP/Tools/GraphQueryTools.cs)
- [ ] **`query_graph` loads the full type set, filters in C#, one edge query per candidate**, `limit` applied only at the end.
- [ ] **`GetRecentlyChanged` full-scans + sorts `nodes`** — add an index on `updated_at`.
- [ ] **Unbounded result sets** in `find_references_to` / `trace_dependencies` / `find_prefabs_with_component` — add a row cap to JSON responses.
- [ ] **Scanner mega-transaction** — full scan holds all worker results in memory, one giant transaction; <1,000-file projects parse single-threaded with per-file commits. Stream/chunk the writes; lower the parallelism threshold.

### 🟡 Hub / Node robustness cluster

> **Connectivity-reliability pass done shipped in v1.1.0** — 54 Bridge unit tests green, type-checked, `dist/` rebuilt. Addresses the launcher/hub friction hit repeatedly this session.

- [x] **`forwardToolCall` error handling** — a healthy-but-now-unreachable instance (Unity reloading mid-call) returns a clean JSON-RPC error instead of a raw `HTTP 500`. [mcp-handler.ts](Bridge~/hub/src/mcp-handler.ts)
- [x] **Lexical project-path matching** — `normalizePath` now `realpath`-canonicalizes + case-folds (macOS/Windows); the launcher resolves the real project root by walking up from cwd (`resolveProjectPath`); a **single-instance fallback** routes an unidentifiable call (cwd `/`) when exactly one Unity is open. Fixes the `No Unity instance found for /` we kept hitting. *(also fixed a latent `normalizePath("/") → ""` parent-match-everything bug)*
- [x] **Hub spawn race** — `O_EXCL` spawn lock (with stale-lock recovery) so only one launcher starts the hub; losers wait for `hub.json` instead of spawning a zombie. [spawn-lock.ts](Bridge~/launcher/src/spawn-lock.ts)
- [x] **Time-based hub liveness (the "immortal hub" fix)** — the hub auto-exits on *time since last launcher activity*, not a leaked launcher *count* (an abruptly-killed launcher never POSTs `disconnect`, so the count leaked and the hub ran for days serving stale code). A fresh build now deploys on the next call once the old hub idles out. [index.ts](Bridge~/hub/src/index.ts) · [registry.ts](Bridge~/hub/src/registry.ts)
- [ ] **Hub heartbeat auto-registers unknown instances**, defeating eviction/re-registration. *(not in this pass)* [registry.ts](Bridge~/hub/src/registry.ts)
- [ ] **Incremental inbound-edge capture/restore not guarded by transaction-failure cleanup** of `scanned_assets`. [db-writer.js](Scanner~/src/db-writer.js)

**Remaining (Fix D, deferred):** clear hub auto-exit/heartbeat timers on shutdown; validate `hub.json` port/pid before trusting it; the `lifecycle.test.ts` integration test kills the *real* production hub in shared `~/.arcforge/hades-hub` (should use an isolated dir) — run it only in CI.

### 🟡 Smell cluster
- [ ] **Async-then-stale-read in `project_recompile_scripts`** — reads compile status immediately after requesting async compilation; verbatim-duplicated error-extraction block. [DomainReloadTools.cs](Editor/MCP/Tools/DomainReloadTools.cs)
- [ ] **`FindComponentType` does a full `AppDomain.GetTypes()` scan on every call**, no cache. [ComponentTools.cs:394](Editor/MCP/Tools/ComponentTools.cs)
- [ ] **Hand-rolled JSON number parser duplicated** in two files that already use Newtonsoft. [ComponentTools.cs:704](Editor/MCP/Tools/ComponentTools.cs)
- [ ] **Lifted-null namespace check drops `instance_of` edges for global-namespace scripts** (copy-pasted in `SceneScanner` and `PrefabScanner`). [SceneScanner.cs:156](Editor/Graph/Scanning/SceneScanner.cs)
- [ ] **`ProcessResolver` timeout is ineffective** — synchronous `stdout.ReadToEnd()` runs before `WaitForExit`. [ProcessResolver.cs:79](Editor/Core/ProcessResolver.cs)

---

## 🟢 Low (verified)

- [x] **`MCP initialize` now resolves its version dynamically (v1.1.0)** — was a hardcoded `"0.9.1"`; `MCPDispatcher`'s `serverInfo.version` uses `PackageInfo.FindForAssembly` (matching `HadesStatus`), so it tracks the package version. The launcher's separate `SERVER_VERSION` constant was also bumped (it stays a per-release manual bump — see ReleasePipeline.md). [MCPDispatcher.cs:162](Editor/MCP/MCPDispatcher.cs)
- [ ] **`ManualReloadStrategy` + its persisted setting + `Begin/EndScriptEditing`'s manual path are unreachable** (`MCPServer.Start` always builds `AutoReloadStrategy`). Wire the setting or delete the dead path. [MCPServer.cs:96](Editor/MCP/MCPServer.cs)
- [ ] **Inverted layering** — `RegressionRunner` lives in Charon but imports the MCP layer (takes an `MCPDispatcher`). Move it to the MCP layer or invert via an interface. [RegressionRunner.cs:4](Editor/Charon/RegressionRunner.cs)
- [ ] **`ProcessMainThreadQueue` drains the whole queue in one tick** — one slow tool freezes the frame for the whole batch. [MCPServer.cs:258](Editor/MCP/MCPServer.cs)
- [ ] **Copy-pasted helpers across tool files** — `GameObjectNotFoundError` ×5, `GetPath` ×4, `FindComponentType` ×2. Consolidate into `Editor/MCP/Utilities`.
- [ ] **Fully-silent `catch` blocks** hide scanner-registration + HTTP-handler faults. Log at minimum.
- [x] **`EnforceSizeLimit`'s synchronous startup `VACUUM` removed** — startup now caps the trace table by row count (`PruneToTraceCap`: delete-oldest + PASSIVE checkpoint, no VACUUM), so freed pages are reused and the file plateaus instead of blocking startup on a multi-GB rewrite. New `SizeEnforcementTests`. *shipped in v1.1.0*
- [ ] **Session port restore writes through to machine-global `EditorPrefs`**, permanently pinning the "auto" port. Use `SessionState` (per-session) instead.
- [ ] **Scanner GUID regex requires lowercase 32-hex anchored to line start** — uppercase/indented `.meta` GUIDs silently skipped. Relax the pattern.
- [ ] **`FrontmatterParser` drops a frontmatter block with zero parseable lines** (reclassifies as body). [Editor/Asphodel](Editor/Asphodel)
- [ ] **`ValidateFile` rewrites every memory file on each validation pass** even when nothing changed — churns frontmatter, risks watcher feedback. Skip the write when unchanged.
- [ ] **`recall_memory` is naive substring OR-matching, no scoring/ranking** (inferred tier uses a different whole-query rule). [AsphodeTools.cs](Editor/MCP/Tools/AsphodeTools.cs)
- [ ] **`scene_create_primitive` swallows malformed transform JSON and reports success.** Validate and error.
- [ ] **Inconsistent Undo coverage / silent rollback** across mutation tools. Standardize undo grouping.
- [ ] **Triplicated `PendingEdge` row-materialization** in `GraphDatabase`. [GraphDatabase.cs](Editor/Graph/GraphDatabase.cs)
- [ ] **Scanner worker threads never explicitly terminated; worker `exit` unhandled** — leaked threads / silent hangs on partial failure. [Scanner~/index.js](Scanner~/index.js)
- [ ] **Cross-process trace propagation half-wired** — Unity reads `X-Hades-Trace-Id` but the bridge never sends it, so traces don't span the client→Unity boundary.

---

## Missing features / vision-vs-code drift

> Silent holes promised in the vision but not marked "Planned." (Outcome capture is tracked above under Priority 3.)

- [ ] **Tier-2 inferred-memory engine never runs** (see #6) and its analyzers are dead (see #8) — the auto-inference half of Asphodel produces nothing today.
- [ ] **Regression/eval framework reachable only via hand-built JSON** — the trace→dataset path (`RecordFromTrace`) is dead code, wired into nothing. Decide: wire it into a workflow or remove it.
- [ ] **Cross-process trace propagation** (see Low cluster) — needed for the dashboard to show client↔Unity traces as one trace.

**Verified as NOT holes (don't action):** token-usage capture is absent but isn't promised as shipped; memory→system-prompt injection works by design via the MCP `instructions` field + `CLAUDE.md`; default memory templates shipping with zero validation rules is intentional. Architecture-doc mechanisms spot-checked (debounce 250/2000/1000 ms, busy fast-path, scanner version 3, ~4000 builtin nodes) **match the code.**

---

## Checked & cleared

> 13 claims were refuted under adversarial verification and intentionally **excluded**. Recorded so we don't re-litigate them.

- "better-sqlite3 is bundled with no engines pinning" — **factually wrong**: `node_modules` is gitignored, installed per-machine.
- "Asset changes dropped while builder busy" — **structurally impossible**: busy states are synchronous main-thread; Unity can't deliver the callback during them.
- "Leaked spans corrupt the AsyncLocal chain" — mechanics real, but `AsyncLocal` copy-on-write means siblings aren't affected.
- "Schema migration `DROP TABLE` outside a transaction" — real but only on a downgrade path not reachable in normal upgrades.
- "Span events written but never read back" — the read path exists (`GetSpansByTraceId` returns the `events` column).
- "Scanner schema has drifted (missing columns/tables)" — drift is real but already captured by the parity finding above; the positional-read consequence doesn't trigger in current column order.
- "MCP `instructions` doesn't inject memory" / "default templates have no validation rules" / "token usage absent" — misread intentional design as defects (see above).
- Plus 5 lower-value claims that didn't survive code inspection.

---

## Notes for fixers

- **Unity hygiene:** edit only `.cs` / `.ts` / `.js` source; let Unity regenerate `.meta` files. Don't hand-author GUIDs.
- **Each fix should land with a test** where one is specified (especially #1 and #7 — both are silent, so without a regression test they'll come back).
- **#3, #4, #6, #8** are interlocked: #3 removes #4's main trigger; #6 must land before #8's fix can be observed working (a null engine can't run fixed analyzers).
