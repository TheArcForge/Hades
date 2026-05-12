# Hades — Roadmap Document

**Version:** 1.0
**Status:** Pre-development execution plan
**Last updated:** 2026-05-09
**Companion to:** Vision document, Architecture document

---

## 0. About this document

This document is the execution plan for Hades. It translates the Architecture into a sequence of buildable phases, each with clear completion criteria, scope boundaries, dependencies, risks, and validation steps.

The document deliberately avoids time estimates and calendar commitments. Phases are sequenced by **logical dependency** and **risk frontloading**, not by elapsed time. The right pace depends on the actual implementation experience, which we cannot predict in advance.

The document also avoids "ship events" per phase. Hades is delivered as a complete product after the full vision is realized; intermediate phases are internal milestones that establish value but are not separately announced or released. The rationale: Hades's integrated value is what differentiates it from competitors, and releasing partial functionality publicly risks anchoring users to a less-than-full experience.

The roadmap structure follows from a few core principles described in §1. Each phase from §2 onwards uses the same structural template. The final chapter covers cross-phase concerns: testing strategy, regression protection, and post-release evolution.

---

## 1. Roadmap principles

### 1.1 Vertical slices over horizontal layers

The temptation in a multi-layer system is to build each layer to completion before starting the next: full Graph in Phase 1, full Charon in Phase 2, full Asphodel in Phase 3. This is rejected.

The actual approach: each phase builds a **minimal but complete vertical slice** — just enough of multiple layers to deliver standalone value. Phase 1 includes a minimal Graph, the MCP plumbing to query it, and the most-useful tools — not just "the Graph". Phase 2 adds Charon across what already exists, not Charon in isolation.

This approach has three benefits. First, each phase produces a usable product, not half-built infrastructure. Second, integration issues surface early when the cost of correction is lower. Third, if the project must stop at phase N for any reason, the result is a coherent product rather than orphaned components.

### 1.2 Risk frontloading

Phases are sequenced so that the highest-risk architectural assumptions are validated earliest. The biggest unknown is whether the Unity-aware semantic graph genuinely delivers the value the Vision claims. If it does not, we need to know in Phase 1, not Phase 5.

Concretely, this means:

- Phase 1 must produce **demonstrable value** to a real developer on a real project. Not a benchmark, not a demo of capabilities, but actual "agent-aware-of-project" experience that the developer can compare against generic Claude Code behavior.
- Phases 2 and 3 build on validated Phase 1 success. If Phase 1 underdelivers, the architecture revisits the assumption before continuing.
- Phases 4-6 are evolutionary, not foundational. They expand and polish what Phase 1-3 prove out.

### 1.3 Single ship event at the end

Hades is shipped publicly as a complete product after Phase 5 (the integration polish phase). Phases 0-5 are internal milestones with their own version tags but no public announcements, marketing, or release events. Phase 6 is post-launch evolution.

The reason: an integrated three-layer product is what differentiates Hades. Shipping just the Graph (after Phase 1) would position Hades as "another knowledge graph for Unity" — competitive with existing tools but not differentiated. Shipping just Graph + Charon would position it as "Unity tooling with observability" — interesting but not the full vision. The complete value proposition requires all three layers + skills, which materializes after Phase 5.

This means internal momentum during Phase 1-4 must be self-sustaining. The work happens without external validation cycles. This is acceptable because the Architecture is solid and the roadmap is committed.

### 1.4 TDD-first as explicit principle

Every phase follows test-first discipline:

1. Define Done criteria (in this document, in advance).
2. Write Happy Path scenarios (manual acceptance tests).
3. Write integration tests for those scenarios (failing initially).
4. Write unit tests as components are built.
5. Implement to make tests green.
6. Run full regression suite from previous phases.
7. Manually run all Happy Path scenarios from this phase plus prior phases.
8. Phase done.

Tests that can be automated must be automated. Tests that cannot be reasonably automated (because they involve agent behavior, Unity Editor UI interactions, or other non-deterministic surfaces) are documented as Happy Path scenarios and run manually after each phase.

Happy Path scenarios serve a dual purpose. They validate the new phase's value, and because each phase's scenarios naturally use capabilities from previous phases, they implicitly exercise prior work. Phase 4's Happy Path scenarios touch Graph (Phase 1), Charon (Phase 2), and Asphodel (Phase 3) by virtue of being realistic agent interactions. Manual regression of "the whole stack" emerges from manual validation of "the latest phase".

### 1.5 Cumulative test suite

Tests added in any phase remain in the suite forever. New phases extend the suite; they do not replace prior tests. Test fixtures, once stable, are frozen — new fixtures are created for new scenarios rather than modifying existing ones to fit. This keeps the regression suite reliable as the codebase grows.

CI runs the full suite on every commit. Slow but reliable.

### 1.6 Phase template

Every phase chapter from §2 onwards follows this structure:

- **Strategic intent** — why this phase exists, what it proves
- **Done criteria** — concrete booleans
- **Scope: what's in** — features delivered
- **Scope: what's out** — explicit non-goals (anti-scope-creep)
- **Dependencies** — what must exist before starting
- **Risk assessment** — phase-specific risks, mitigations
- **Implementation hints** — pointers to Architecture sections that matter for this phase
- **Tests added** — automated test commitments
- **Happy Path scenarios** — 2-3 manual acceptance tests
- **Regression coverage** — what continues to pass from prior phases
- **Bridge to next phase** — what this unlocks

---

## 2. Phase 0: Foundation

### Strategic intent

Phase 0 is preparatory and does not produce user-facing value. It establishes the development infrastructure on which all subsequent phases build. The deliverable is an empty but functional Hades repository that can be cloned, built, tested, and run on a Unity project — even though it has no features yet.

This phase exists because rushing into Phase 1 without solid testing infrastructure leads to accumulated debt that becomes impossible to retire later. Tests-first development requires the test runner to exist first.

A second purpose: validate that the UniClaude infrastructure we plan to reuse actually works as expected when extracted into a clean repository. The 60% reuse claim from Architecture §1.4 is an estimate; Phase 0 confirms it.

### Done criteria

- [x] Hades GitHub repository exists with planned directory structure
- [x] Unity Package skeleton is installable via UPM git URL
- [x] Node.js MCP bridge skeleton starts and connects to Unity-side server
- [x] Empty MCP tool that returns "Hades is alive" works end-to-end (agent → bridge → Unity → response)
- [x] Unity Test Runner configured and runs sample tests
- [x] Node.js test framework configured and runs sample tests
- [ ] CI pipeline runs both test suites on every commit *(Bridge tests only — Unity tests require license server, deferred)*
- [x] At least one synthetic Unity project fixture exists for integration tests
- [x] UniClaude infrastructure (MCPServer, HttpTransport, MCPDispatcher, Main Thread Bridge, Path Sandbox, Domain Reload Resilience) is extracted and works in the new repository

### Scope: what's in

- Repository structure and tooling: monorepo layout, Unity Package, Node.js bridge, dashboard placeholder
- CI configuration: GitHub Actions running tests on every commit
- Test infrastructure: Unity Test Runner config, NUnit setup, Vitest setup, fixture project loader
- UniClaude extraction: MCPServer.cs, HttpTransport, MCPDispatcher (with attribute-based tool registration), Main Thread Bridge, Path Sandbox, Domain Reload Resilience
- One test MCP tool (`hades_ping` or similar) that returns a static string — proves end-to-end pipeline
- One synthetic Unity fixture project: 5 scenes, 10 prefabs (some variants), 20 scripts. Frozen as test fixture going forward.
- Discovery file mechanism (`.arcforge/server.json` written and read correctly)
- Plugin manifest skeleton (`plugin.json`, `marketplace.json`)
- README explaining repository structure and setup instructions

### Scope: what's out

- Any actual scanners (Phase 1)
- SQLite database setup (Phase 1)
- MCP tools beyond ping (Phase 1)
- Charon (Phase 2)
- Asphodel (Phase 3)
- Skills (Phase 4)
- Public marketplace listing (Phase 5)

### Dependencies

- Access to UniClaude source code (already exists)
- Unity 6000.0+ for development
- Node.js 20+ for bridge and dashboard
- GitHub repository access

### Risk assessment

**Risk: UniClaude extraction is messier than expected.** UniClaude's infrastructure is coupled with chat UI in places. Decoupling may surface unexpected dependencies.
*Mitigation:* Allocate sufficient time to surgical extraction. If decoupling proves harder than expected, document it as a finding and plan rewrites for Phase 1 rather than fighting the legacy code.

**Risk: CI pipeline complexity.** Running Unity in headless mode in CI has known pain points (license servers, batch mode quirks).
*Mitigation:* Start with simple smoke tests in CI; expand coverage as we learn what works. Don't block Phase 0 completion on CI being perfect; block on CI being functional.

**Risk: Plugin manifest format changes.** Anthropic plugin marketplace format evolves; current research is from May 2026.
*Mitigation:* Use the format as currently documented. Treat plugin manifest as a maintenance item; expect minor updates.

### Implementation hints

- **Architecture §1.4** lists the exact UniClaude components to reuse. Start with the smallest viable subset: MCPServer + HttpTransport + MCPDispatcher. Add others as needed.
- **Architecture §1.5** describes the discovery file mechanism. Implement this in Phase 0 even though the only "tool" is ping — it makes Phase 1 trivial.
- **Architecture §1.6** describes the Main Thread Bridge. Reuse UniClaude's implementation directly; don't reimplement.
- **Architecture §1.8** describes multi-instance behavior. Phase 0 should test this: run Hades on two fixture projects simultaneously, verify they don't interfere.
- The `hades_ping` test tool is a useful long-term diagnostic. Keep it in the codebase forever, not just Phase 0.

### Tests added

**Automated:**

- **C# unit tests**: MCPServer lifecycle (start, stop, restart after domain reload), MCPDispatcher tool registration (attribute discovery, parameter mapping), Path Sandbox (allowed and disallowed paths)
- **Node.js unit tests**: HTTP client correct request format, MCP protocol message construction, discovery file parsing
- **Integration tests**: end-to-end ping (Node.js client → Unity server → response), Unity Test Runner runs Hades-internal tests headlessly in CI
- **Multi-instance test**: launch two Unity instances on two fixture projects, verify each starts its own MCP server on its own port, verify cross-talk does not occur

**Manual (Happy Path scenarios are deferred to Phase 1; Phase 0 has no user-facing scenarios)**

### Happy Path scenarios

Phase 0 produces no user-visible functionality. The Done criteria are sufficient validation.

### Regression coverage

Not applicable — this is the first phase. Future phases must continue to pass all Phase 0 tests.

### Bridge to next phase

Phase 0 unlocks Phase 1 by providing:

- Working MCP communication infrastructure
- Test infrastructure that can validate Phase 1's Graph as it's built
- A frozen fixture project against which Phase 1 scanners can be validated
- Confirmed UniClaude infrastructure works in the new repo

Phase 1 begins by building scanners against the fixture project, with tests written before implementation.

---

## 3. Phase 1: Graph MVP

### Strategic intent

Phase 1 is the most important phase in the roadmap and the highest-risk one. It validates the central thesis of the entire product: that a Unity-aware semantic knowledge graph delivers measurable value to a developer using Claude Code on a Unity project.

If this phase succeeds — meaning the developer feels a tangible difference when working with Hades versus without — every subsequent phase has a foundation. If this phase fails — meaning the graph turns out to be uninteresting in practice, or the developer cannot tell the difference — the architecture must be revisited before continuing.

The phase produces a minimal but complete Graph layer: scanners for the most important asset types, the SQLite-backed graph database, incremental update mechanism, and 8-12 high-value MCP tools that the agent can call. Plus enough confidence modeling that uncertain results are not silently presented as facts.

This phase explicitly does not include observability, memory, or skills. Those are deferred. Phase 1 is "graph and tools, that's it" — the leanest test of the central thesis.

### Done criteria

- [ ] SQLite database initializes with full schema (nodes, edges, supporting tables) and proper WAL configuration
- [ ] SceneScanner builds correct graph from fixture project's scenes (open-scene mode and closed-scene mode both work)
- [ ] PrefabScanner builds correct graph including prefab variants and override edges
- [ ] ScriptScanner extracts types and methods (shallow mode); deep mode optional and behind config flag
- [ ] ScriptableObjectScanner produces nodes for both type definitions and instances
- [ ] AddressablesScanner produces graph entries for addressable groups and entries
- [ ] MaterialScanner and ShaderScanner produce basic asset nodes
- [ ] ProjectSettingsScanner produces singleton nodes for build settings, render pipeline, etc.
- [ ] GraphBuilder coordinates full rebuild and incremental updates correctly
- [ ] Incremental update triggered by AssetPostprocessor; updates complete within 1 second on typical edits
- [ ] At least 10 MCP tools are implemented and tested (specific list below)
- [ ] All MCP tool responses include the `confidence` block per Architecture §6.7
- [ ] "Rebuild in progress" signal works: queries during rebuild return current data with explicit warning attribute
- [ ] Database stays consistent across domain reloads (verified by tests)
- [ ] Database stays consistent across Unity restart (verified by tests)
- [ ] Manual `Hades: Rebuild Graph` menu command works
- [ ] Bundled MCP bridge process auto-registers with Claude Code config on Unity Package install

### Scope: what's in

**Scanners (in order of priority):**
1. SceneScanner (with both open and closed modes per Architecture §2.3.3)
2. PrefabScanner (with variant detection and override capture)
3. ScriptScanner (shallow mode default)
4. ScriptableObjectScanner
5. AddressablesScanner
6. MaterialScanner (basic — shader, color, texture references)
7. ShaderScanner (basic — properties only)
8. ProjectSettingsScanner

**Graph infrastructure:**
- SQLite database with full schema from Architecture §2.2
- WAL mode and PRAGMA configuration from Architecture §2.7.1
- Asset content hashing for change detection
- Scanner versioning for migrations
- `current_operation` mechanism for "rebuild in progress" signaling

**Incremental updates:**
- AssetPostprocessor handler with debouncer (250ms idle, 2000ms max delay)
- EditorApplication.projectChanged handler
- EditorSceneManager.sceneSaved handler
- PrefabStage.prefabSaved handler
- Diff-based update logic (preserves node IDs across updates)

**MCP tools (initial set):**
1. `hades_ping` — diagnostic (carryover from Phase 0)
2. `get_project_summary(depth)` — project overview
3. `get_scene_summary(scene_path, depth)` — scene structure
4. `find_prefabs_with_component(component_type)` — find component usage
5. `find_components_using_pattern(pattern_name)` — pattern matching
6. `find_references_to(target_path)` — reverse reference lookup
7. `trace_dependencies(asset_path, max_depth)` — forward dependency traversal
8. `find_orphan_scripts()` — unused scripts
9. `analyze_render_pipeline()` — pipeline summary
10. `search_by_name(name_pattern, type_filter)` — name-based search
11. `get_recently_changed(hours)` — temporal queries
12. `query_graph(structured_query)` — escape hatch for complex queries

**Confidence modeling:**
- Every tool response includes `confidence` block
- "I don't know" vs "no results" distinction implemented (via `result_status` field)
- Graceful degradation paths for graph rebuilds, scanner failures, partial coverage

**Slash commands:**
- `/hades:status` — current state
- `/hades:rebuild-graph` — trigger full rebuild

**Distribution:**
- Unity Package installable via UPM git URL
- Self-published Hades plugin marketplace on GitHub (`arcforge/hades-marketplace`)
- Plugin manifest with no skills yet (placeholder), valid `.mcp.json`
- Setup wizard in Unity Package that auto-registers MCP server with Claude Code

### Scope: what's out

- Charon (Phase 2)
- Asphodel (Phase 3)
- Skills library (Phase 4)
- Roslyn deep mode (Phase 1 ships shallow only; deep mode behind feature flag for Phase 1+)
- Per-method call graphs (deep mode requirement)
- Cross-project queries
- Tier 2 inferred memory (Phase 5)
- Eval framework (Phase 5/6)
- Performance optimization for very large projects (Phase 5)
- Roslyn-based call graph extraction
- Charon dashboard
- Memory files of any kind
- Skill library beyond placeholder

### Dependencies

- Phase 0 complete
- All UniClaude infrastructure validated
- Test fixture project frozen and committed

### Risk assessment

**Risk: Graph doesn't deliver felt value.** The core thesis fails — agents with Graph don't behave noticeably better than agents without.
*Mitigation:* Happy Path scenarios are designed to make value visible. If they don't feel different, this is an architectural alarm, not a product polish issue. Pause and re-evaluate before continuing.

**Risk: Scanner correctness is harder than estimated.** Unity assets have many edge cases (nested prefabs, missing references, broken meta files).
*Mitigation:* Start with the simplest fixture project. Iteratively add complexity. Capture every edge case as a test fixture so it cannot regress.

**Risk: Incremental update logic has holes.** Graph drifts from reality without anyone noticing.
*Mitigation:* Periodic 5%-sample integrity check (Architecture §2.4.4). Manual rebuild as recovery action documented prominently in user-facing materials.

**Risk: SQLite performance worse than estimated on real projects.** Queries become slow on large graphs.
*Mitigation:* Benchmark on a real-world large project before declaring Phase 1 done. If queries exceed acceptable latency, optimize indexes before moving to Phase 2.

**Risk: Domain reload disruption.** Edits during reload corrupt graph state.
*Mitigation:* UniClaude infrastructure handles this for chat scenarios; Phase 1 stress-tests it with graph operations. Address gaps as they appear.

### Implementation hints

- **Architecture §2.2** is the authoritative schema. Implement nodes and edges tables exactly as specified, including all indexes.
- **Architecture §2.3.3** specifies open-scene mode vs closed-scene mode for SceneScanner. This is a critical performance optimization — do not skip it. PrefabScanner has analogous handling via `PrefabStageUtility`.
- **Architecture §2.4** describes incremental update flow. The debouncer parameters (250ms idle, 2000ms max) are tuned defaults; adjust only with reason.
- **Architecture §2.7** specifies SQLite PRAGMA configuration. Apply on every database connection. `journal_mode = WAL` is non-negotiable.
- **Architecture §2.7.5** specifies the "rebuild in progress" mechanism. Implement this in Phase 1 even though it's an edge case — Pipeline 12 in Architecture demonstrates why it matters.
- **Architecture §2.9** lists static analysis boundaries. Scanner code should produce explicit signals when it detects dynamic patterns it cannot resolve.
- **Architecture §6.7** specifies the confidence response shape. Every MCP tool must return this. Don't shortcut early thinking it's optional polish.
- **Pipeline 1 (Architecture §7)** walks through a full `get_scene_summary` call. Use as reference for tool implementation.
- For Roslyn shallow mode: parse the C# AST without semantic resolution. Type names, method signatures, field declarations. No call graph yet. Save deep mode for Phase 5.

### Tests added

**Automated unit tests (C#):**
- Each scanner: produces correct nodes and edges for fixture inputs (one test per asset type with known structure)
- GraphBuilder: full rebuild produces expected node count, incremental update preserves IDs correctly, diff logic correct on add/modify/delete
- SQLite schema: WAL mode active, foreign keys enforced, indexes used (verify via EXPLAIN QUERY PLAN)
- Confidence modeling: rebuild-in-progress signal correctly attached to relevant queries

**Automated unit tests (Node.js):**
- Each MCP tool: correct request shape, response parsing, error handling
- Bridge process: connects to Unity-side server, reconnects after Unity restart, handles port changes

**Integration tests (Unity Test Runner):**
- Full rebuild on fixture project produces expected graph (snapshot test)
- Incremental update after modifying a prefab in fixture preserves correctness
- Domain reload during scan: graph state survives correctly
- Multi-instance test: two Unity instances scan two fixtures concurrently, no interference

**End-to-end tests:**
- Each MCP tool called via the bridge process returns expected result shape (does not validate semantic correctness, only protocol)

### Happy Path scenarios

These are run manually on a real Unity project (not the fixture) after Phase 1 is otherwise complete. They are the Phase's acceptance test.

**Scenario 1: First impressions matter**

A developer with a real Unity project (their own active project, ideally medium complexity) installs Hades. They open Claude Code. They ask:

> "Tell me about this project."

Without Hades, the agent would respond with platitudes or generic Unity advice, or it would start exploring through file reads. With Hades, the agent should respond with a substantive summary including the render pipeline, key directories, scene count, prefab count, scripting language assumptions, and notable subsystems — all sourced from the Graph in a single tool call.

**Demonstrates:** project-level awareness from the Graph.
**Implicitly verifies:** scanners ran and produced correct top-level data; MCP communication works end-to-end; confidence modeling does not flag false uncertainty.
**Pass criteria:** the response is recognizable as describing _this specific project_, not generic Unity. The developer feels the agent "knows" their project.

**Scenario 2: Pattern discovery**

The developer asks:

> "Where do we use the [SomeComponent] component?"

Substitute `[SomeComponent]` with a real component from the developer's project. Without Hades, the agent grep'd for it. With Hades, the agent calls `find_prefabs_with_component` and returns precise locations including:

- Scene paths and GameObject names where it's used
- Prefabs containing it
- Whether any prefab variants override it

**Demonstrates:** structural understanding beyond text search.
**Implicitly verifies:** PrefabScanner correctly captured prefab variants; SceneScanner correctly extracted GameObject hierarchies; references between scripts and Components are correctly modeled.
**Pass criteria:** the result is correct and complete. Compare against manual inspection; results should match exactly.

**Scenario 3: Architectural understanding**

The developer asks:

> "I want to remove [SomeScript]. What would break?"

Substitute `[SomeScript]` with a real script that has dependencies. The agent should:

- Identify direct references (other scripts, prefabs, scene-bound components)
- Trace transitive dependencies to indicate scope of impact
- If addressables are involved, surface those
- Surface any blind spots (e.g., "I see this is referenced in 7 places statically; if your project uses reflection, there may be more")

**Demonstrates:** dependency traversal, multi-layer awareness, confidence modeling around static analysis blind spots.
**Implicitly verifies:** trace_dependencies works across asset types; AddressablesScanner contributes to results; confidence modeling correctly flags reflection as a possible blind spot.
**Pass criteria:** the response identifies real dependencies the developer recognizes, plus appropriate caveats about what static analysis cannot see.

After running these three scenarios on a real project and confirming the results feel substantive (not just technically correct), Phase 1 is validated. If any scenario underdelivers — the agent's answer feels generic or wrong — investigate and fix before Phase 2.

### Regression coverage

Phase 1 is the first phase with substantial behavior. Prior phase's tests (Phase 0's infrastructure tests) must continue to pass.

The Phase 1 test suite becomes the regression baseline for all subsequent phases.

### Phase 1 implementation notes

Issues encountered during Phase 1 development, documented for future reference:

1. **Mono.Data.Sqlite was unusable.** The bundled DLLs in Unity's `MonoBleedingEdge/` directory are reference assembly stubs — they compile but throw `InvalidProgramException` at runtime. Every database-touching test failed. Replaced with vendored gilzoide/unity-sqlite-net (see Architecture ADR).

2. **sqlite-net API differences from ADO.NET.** Three breaking differences: (a) `Bind()` is 1-indexed, not 0-indexed; (b) `Bind()` does not accept null strings — must coalesce to safe defaults; (c) `ExecuteScript()` is needed for multi-statement DDL instead of `Execute()`.

3. **Editor freeze from asset postprocessor re-entry.** On a 55k-node production project, full graph rebuild triggered scene scanning → `OnPostprocessAllAssets` fired → re-enqueued graph work → infinite loop. Fixed with `IsBusy` guard in the postprocessor. Not caught in unit tests because re-entry requires enough assets to trigger scene scanning.

4. **Claude Desktop only supports stdio MCP servers.** Initial assumption was both Claude Code and Claude Desktop could connect via HTTP URL. Only Claude Code can. Required building a Node.js bridge script that translates stdio ↔ HTTP/SSE via `npx mcp-remote`. Node.js is now a runtime dependency for Claude Desktop support.

5. **Port instability across Unity restarts.** The MCP server port changes on recompile/restart, breaking any hardcoded config. Motivated the auto-discovery system: central server registry at `~/.arcforge/servers/`, bridge script with standby mode, and auto-managed client configs.

6. **Streamable HTTP endpoint mismatch.** `mcp-remote` uses an "http-first" strategy (POST before SSE fallback). When the bridge URL pointed to `/sse`, POST returned 404, and the SSE fallback caused rapid connect/disconnect cycles. Fixed by pointing to `/rpc` which handles both `POST` (JSON-RPC) and `GET` (SSE) per the MCP Streamable HTTP spec.

7. **MCPToolResult envelope mismatch.** `MCPToolResult.Success()` does not wrap results in the `{"result":...}` envelope expected by status-checking tools. `SuccessWithConfidence()` was needed for tools that return structured graph metadata.

### Bridge to next phase

Phase 1 unlocks Phase 2 by:

- Providing a stable Graph layer that Charon can instrument
- Establishing MCP tools that have well-defined inputs and outputs (Charon traces these)
- Producing a body of "what Hades looks like in production" experience that informs which observability features matter most

Phase 2 begins by adding OpenTelemetry instrumentation to existing Phase 1 code paths, without changing what those code paths do. The behavior stays identical; only the observability changes.

---

## 4. Phase 2: Charon basic

### Strategic intent

Phase 2 adds observability across what Phase 1 built. The Graph layer keeps doing exactly what it did, but every meaningful operation — MCP tool calls, graph queries, scanner runs — emits trace data that can be inspected.

The motivation, reiterating from Architecture §3.1: when an AI agent has the ability to modify project files, mistakes are not abstract. The agent does something wrong, the user reverts, and without traces, neither the user nor we can diagnose the cause. Charon makes the previously-invisible visible.

This phase is also when our internal development workflow shifts. From Phase 2 onwards, every Hades feature we build uses Charon for our own debugging. This is the dogfooding moment — if Charon doesn't help us, it won't help users.

The phase includes the observability infrastructure (OpenTelemetry instrumentation, SQLite trace backend) and a minimal dashboard for inspecting traces. The full eval framework with annotation tooling is deferred to Phase 5/6; Phase 2 ships "trace viewer", not "trace analytics platform".

### Done criteria

- [x] ~~OpenTelemetry SDK integrated into the Hades Unity Package~~ Custom CharonEmitter API (OpenTelemetry-inspired span model without the SDK dependency)
- [x] CharonEmitter API allows starting and ending spans with attributes and events
- [x] Every MCP tool call automatically emits a root span (via `CallToolWithTracing` interceptor)
- [x] Every graph query emits a child span with query type and result count
- [x] Every scanner invocation emits a span with duration and outcome
- [ ] Every memory operation emits a span (preparation for Phase 3) *(deferred — Asphodel not yet built)*
- [x] Spans nest correctly via `AsyncLocal<Span>` context propagation
- [x] SQLite trace database initializes with full schema (traces, spans tables)
- [x] Trace buffer flushes asynchronously every 500ms or 1000 spans
- [x] Cross-process trace ID propagation via `X-Hades-Trace-Id` header works
- [x] Charon dashboard process starts via `Hades: Open Charon Dashboard` menu
- [x] ~~Dashboard handles port collisions (tries 7878, 7879, etc.)~~ Dashboard uses OS-assigned ephemeral port via `app.listen(0)` (see Architecture ADR)
- [x] Dashboard displays trace list with filters (date, status, name pattern)
- [x] Dashboard displays trace detail with span tree visualization
- [x] Privacy defaults: paths not redacted, content not captured, 30-day retention with auto-pruning on startup
- [ ] ~~Trace WAL survives Unity crash (verified by force-killing Unity, restarting, traces from before crash present)~~ *(SKIPPED — not explicitly verified, expected to work due to WAL mode)*

### Scope: what's in

**Instrumentation:**
- CharonEmitter API in C#
- Wrapper/interceptor adding root span to every MCP tool call automatically
- Manual span emission for graph queries, scanner invocations, lifecycle events
- AsyncLocal-based parent span tracking

**Storage:**
- SQLite trace database with full schema
- WAL mode and PRAGMA configuration matching graph database
- Asynchronous batched writes (500ms or 1000 spans)
- 30-day retention with auto-pruning on Unity startup

**Cross-process:**
- `X-Hades-Trace-Id` header support in HttpTransport (Unity side)
- Bridge process reads trace ID from environment if provided, generates if not
- Spans link correctly across process boundary

**Dashboard:**
- Node.js Express server reading SQLite via `better-sqlite3`
- React or similar SPA rendering trace data
- Trace list view with filters (date, status, name pattern, outcome)
- Trace detail view with span tree (waterfall visualization)
- Span attribute inspection
- Port auto-fallback per Architecture §1.8
- Local-only (binds to 127.0.0.1)

**Privacy controls:**
- Configurable redaction (paths, content) in `.arcforge/config.yaml`
- Default: paths and content not redacted (helps debug)
- Retention configurable; default 30 days
- Export controls (manual export with optional scrubbing)

**Charon-based regression testing:**
- Trace recording for happy path scenarios
- Replay tooling that re-runs deterministic Hades-side parts (graph queries, memory reads if Phase 3 ships first)
- Comparison logic: same inputs should produce same Hades-side outputs

### Scope: what's out

- Eval framework dataset features (Phase 5)
- LLM-as-judge eval (Phase 5/6)
- Agent-side replay (impossible due to non-determinism, see Architecture §3.7.2)
- Aggregations dashboard view beyond simple latency display (Phase 5)
- Annotation tooling (Phase 5/6)
- Cross-project trace views (deferred indefinitely)
- Cloud trace export (out of scope by design — local-first)

### Dependencies

- Phase 1 complete and validated
- Hades has user-visible behavior to instrument

### Risk assessment

**Risk: Trace volume overwhelms local storage faster than expected.** Heavy users generate gigabytes of traces per week.
*Mitigation:* 30-day retention default. Soft warning at 1GB. Hard guard at 80% disk fill (drop traces mode). Architecture §3.9 covers this; implement as specified.

**Risk: AsyncLocal context propagation has gotchas in Unity.** Unity's threading is unusual and AsyncLocal may not propagate the way expected in some scenarios.
*Mitigation:* Test propagation rigorously across Main Thread Bridge, async/await boundaries, and coroutines. Have explicit unit tests for context handling.

**Risk: Dashboard adds another moving part to the system.** More code = more bugs.
*Mitigation:* Keep dashboard simple. Read-only views, no write operations. If it crashes, the rest of Hades continues working.

**Risk: Performance overhead of instrumentation.** Every operation now does extra work.
*Mitigation:* Async batched writes mean instrumentation is "fire and forget" — no blocking on disk I/O. Verify via benchmarks: instrumentation should add <5% latency to typical tool calls.

### Implementation hints

- **Architecture §3.2** specifies the trace and span schema. Implement exactly as specified for compatibility with future eval tooling.
- **Architecture §3.3** describes the emitter pattern. The fluent API with `using` statements ensures spans are always closed even on exceptions.
- **Architecture §3.3.1** specifies `X-Hades-Trace-Id` header for cross-process. Add this to HttpTransport handling in Phase 2; Phase 1's HttpTransport doesn't need to know about it.
- **Architecture §3.4** lists what to instrument. Cover all categories; don't selectively skip "boring" ones.
- **Architecture §3.5** specifies privacy defaults. Don't deviate without reason.
- **Architecture §3.6** describes dashboard architecture. Node.js + React + SQLite. Don't introduce new dependencies (e.g., a separate database) for dashboard purposes.
- **Architecture §3.10** lists edge cases. Address each in tests.
- **Use Charon for your own debugging during Phase 2.** This is the dogfooding test. If you're not naturally reaching for Charon when debugging Phase 2 implementation issues, the dashboard isn't useful enough yet.

### Tests added

**Automated unit tests:**
- CharonEmitter: span creation, attribute setting, event addition, status setting
- Span context propagation across async boundaries
- Trace ID generation and parent linking
- SQLite trace schema validity
- Buffer drain mechanism: timing, batch size, flushes on shutdown

**Integration tests:**
- Every MCP tool call from Phase 1 produces a trace with expected structure
- Graph queries produce nested child spans
- Cross-process trace ID propagation through bridge
- Dashboard reads traces correctly from database
- Multiple Unity instances produce separate trace databases (no cross-contamination)

**Charon-based regression:** *(SKIPPED — framework built but no baseline datasets recorded yet; deferred to future workflow integration)*
- Record trace of Phase 1 Happy Path scenario 1 ("Tell me about this project")
- Replay deterministic parts (graph queries with same inputs)
- Verify same outputs produced — this is now part of regression suite

### Happy Path scenarios

**Scenario 4: Diagnose a problem** *(SKIPPED — deferred to later validation)*

The developer encounters a confusing agent suggestion. They ask the agent for the same task again, then run `/hades:show-traces` and inspect the trace from the first attempt. They see the chain of tool calls, the data the agent saw, and identify why the agent made the choice it did.

For testing purposes, deliberately create a confusing situation: ask the agent to find references to a script while a graph rebuild is in progress. The first attempt may return incomplete results. The trace explicitly shows the rebuild was in progress; the second attempt (after rebuild) returns complete results.

**Demonstrates:** Charon trace inspection.
**Implicitly verifies:** Phase 1 graph queries (still work), Phase 1 incremental update (rebuild detection), Phase 2 confidence modeling propagates through traces.
**Pass criteria:** developer can identify root cause of the confusing suggestion from the trace alone, without needing to reproduce or guess.

**Scenario 5: Performance investigation** *(SKIPPED — deferred to later validation)*

The developer notices a tool call feels slow. They open the dashboard, find the trace, and see exactly which sub-operation took the time — a specific graph query, a slow scanner, an HTTP roundtrip.

For testing purposes, deliberately introduce a slow query (e.g., recursive deep traversal on a large fixture). Verify the trace surfaces the slowness clearly.

**Demonstrates:** Charon as performance debugging tool.
**Implicitly verifies:** Phase 1 graph performs reasonably; Phase 2 instrumentation captures latency accurately.
**Pass criteria:** the slow operation is immediately visible in the trace; developer doesn't have to dig.

**Scenario 6: Multi-project workflow** ✅

The developer has two Unity instances open on different projects (per Architecture §1.8). They open dashboards for both. Traces from each project show only that project's activity. No cross-contamination.

**Demonstrates:** multi-instance correctness with observability.
**Implicitly verifies:** §1.8 isolation property holds with traces; dashboard port fallback works.
**Pass criteria:** two dashboards run simultaneously, each showing only its project's traces.

### Regression coverage

All Phase 1 tests must continue to pass. Specifically:

- Graph build tests (the addition of instrumentation must not change graph correctness)
- All MCP tool tests (responses must be functionally identical, just now with traces emitted)
- Confidence modeling tests
- Multi-instance tests

Charon-based regression for Phase 1 Happy Path scenarios is now part of the suite (recorded in this phase).

### Phase 2 implementation notes

Issues encountered during Phase 2 development, documented for future reference:

1. **Unity's `Process.Start` does not inherit the shell PATH.** Launching `node` directly via `Process.Start("node", ...)` fails with "Cannot find the specified file" because Unity's process environment lacks PATH entries from login shells (nvm, fnm, Homebrew, etc.). This affects macOS, Linux, and Windows differently. Fixed by building `ProcessResolver.cs` — a cross-platform utility that resolves executable paths via `which` (macOS/Linux, using `bash -lc` for login shell) or `where` (Windows), with per-session caching. This is now the standard way to launch external processes throughout Hades (see Architecture ADR).

2. **Package path resolution fails with `file:` UPM references.** During development, the Unity Package is installed via `file:` reference, meaning `Packages/com.arcforge.hades/Dashboard~` is a symlink that doesn't resolve the way `PackageInfo.resolvedPath` does. Fixed by using `PackageInfo.FindForAssembly(typeof(CharonDashboard).Assembly).resolvedPath` to get the actual filesystem path regardless of how the package was installed (git URL, tarball, or local `file:` reference).

3. **`OutputDataReceived` events silently fail in Unity's process context.** The dashboard server started correctly (verified by manual `node` invocation) but Unity never received stdout/stderr events from the child process. Unity's event pump does not reliably deliver async process output events. Fixed by replacing stdout parsing with port file IPC: the server writes its assigned port to a temp file via `HADES_PORT_FILE` environment variable, and Unity polls for the file (200ms intervals, 6-second timeout). This is more reliable than event-based approaches across all platforms.

4. **Dashboard process orphaned on Unity domain reload.** When Unity recompiles scripts, it tears down and reconstructs the AppDomain. Static fields — including the `Process` reference to the dashboard — are lost. The dashboard process continues running but Unity can no longer stop it. Fixed by storing the PID in `SessionState` (which survives domain reloads) and reattaching via `Process.GetProcessById()` in a static constructor. `EditorApplication.quitting` hook is re-registered on reattach.

5. **TOCTOU race in port assignment.** The original architecture specified sequential port scanning (try 7878, then 7879, etc.), which has a time-of-check-to-time-of-use race condition. Between finding a free port and binding to it, another process could claim it. Fixed by using `app.listen(0)` — the OS atomically assigns an available port. The assigned port is communicated back via the port file IPC mechanism from issue #3. This also eliminates the arbitrary port range limitation. Architecture §3.6 updated accordingly.

6. **`ReadToEnd` on both stdout and stderr causes pipe buffer deadlock.** Calling `stdout.ReadToEnd()` and `stderr.ReadToEnd()` synchronously in sequence can deadlock: if stderr's OS pipe buffer fills while stdout is being read, the child process blocks writing to stderr, which prevents it from writing to stdout, which prevents `ReadToEnd()` from completing. Fixed by reading stderr asynchronously (`StandardError.ReadToEndAsync()`) while reading stdout synchronously. For long-lived processes (like the dashboard), stdout/stderr are not redirected at all — the process runs detached.

7. **Regression framework test parameter type mismatch.** The `hades_regression_record` tool declares `tool_calls` as a `string` parameter (expecting a JSON array serialized as string), but tests passed a raw `JArray` object in the arguments `JObject`. The MCPDispatcher's `BindArguments` cannot convert `JArray` to `string`, causing all 4 `RegressionToolsTests` to fail. Fixed by serializing the array with `.ToString(Formatting.None)` before passing. This was a test bug, not a tool bug — the tool's contract is correct.

### Bridge to next phase

Phase 2 unlocks Phase 3 by:

- Memory operations need to emit traces; the infrastructure for this is now in place
- Memory validation events become observable, which is critical for the validation cycle
- Tier 2 inferred memory (Phase 5) requires the trace data Charon now collects; Phase 2 starts populating that dataset

Phase 3 begins by adding markdown memory file I/O to the Unity Package, with emission of trace events for every read/write/validation already wired in.

---

## 5. Phase 3: Asphodel

### Strategic intent

Phase 3 introduces persistent project memory. With Phase 1 (Graph) and Phase 2 (Charon) in place, the project gains a third pillar: memory that survives across sessions and travels with the project in git.

Asphodel is what transforms Hades from "smart project introspection" into "team-aware AI infrastructure." Graph alone tells the agent what exists in the project right now. Memory tells the agent what the team has decided about how the project should be structured. The two together inform suggestions in a way neither alone can.

This phase ships only Tier 1 (explicit, human-curated memory). Tier 2 (inferred memory from Charon traces) is deferred to Phase 5 because it depends on accumulated trace data and on the validation infrastructure being battle-tested first. Phase 3 ships a memory system that requires explicit human input to populate; Phase 5 makes it semi-automatic.

The validation engine — C# code that checks memory claims against the Graph and writes results back to memory files — is the most consequential part of this phase. Without validation, memory drifts and becomes worse than no memory.

### Done criteria

- [x] `.arcforge/memory/` directory structure created on Hades initialization
- [x] Default Tier 1 file templates exist (decisions.md, patterns.md, conventions.md, pitfalls.md, glossary.md, intent.md)
- [x] Markdown parser correctly handles YAML frontmatter and inline comments
- [x] Memory file I/O via Unity Package (read, write atomic via temp+rename)
- [x] FileSystemWatcher detects external edits to memory files
- [x] Validation engine (C# code) parses validation rules from frontmatter
- [x] Validation engine executes graph queries and compares results against expected outcomes
- [x] Validation engine writes results back to memory file (frontmatter status, inline HTML comments)
- [x] Validation runs on three triggers: startup, post-graph-update, on demand
- [x] Validation budget per query (1 second default) is enforced
- [x] Memory MCP tools work: `get_memory_summary`, `recall_memory`, `propose_memory_update`, `validate_memory`
- [x] Proposal queue at `.arcforge/memory/proposals/` exists; Claude proposals go there
- [x] Charon dashboard adds "Memory" view: shows files, validation status, conflicts
- [x] Charon dashboard adds "Proposals" view: pending proposals with accept/reject UI
- [x] Tier 1 files are git-tracked (in `.gitignore` template, only Tier 2 inferred/ subdirectory excluded)
- [x] Conflict detection: when memory file edited externally during Hades operation, conflict surfaces in dashboard

### Scope: what's in

**Memory storage:**
- `.arcforge/memory/` directory creation and population with default templates
- Tier 1 markdown files with YAML frontmatter
- Atomic writes (temp file + rename)
- FileSystemWatcher for external edit detection

**Validation engine (C# in Unity Package):**
- YAML frontmatter parser (existing library, e.g., YamlDotNet)
- Validation rule schema interpretation
- Query execution against Graph
- Result comparison (exists, count thresholds, equality)
- Frontmatter status updates
- Inline HTML comment generation for inconsistencies
- Per-query budget enforcement (1 second default, configurable)
- Trigger handling: startup, post-rebuild, manual menu command

**MCP tools:**
- `get_memory_summary()` — returns short summary across all Tier 1 files for system prompt injection
- `recall_memory(query)` — returns relevant content for on-demand retrieval
- `propose_memory_update(file, content, rationale)` — adds proposal to queue
- `validate_memory()` — triggers explicit re-validation

**Slash commands:**
- `/hades:validate-memory` — manual validation trigger
- `/hades:show-proposals` — shows pending proposals (opens dashboard)

**Dashboard extensions:**
- "Memory" view: file list with status, click to inspect content + validation history
- "Proposals" view: pending memory proposals with accept/edit/reject actions
- "Conflicts" view: memory files where validation has flagged inconsistency

**Charon integration:**
- Every memory read/write/validation emits trace span
- Memory operation traces visible in main trace view

### Scope: what's out

- Tier 2 inferred memory generation (Phase 5)
- Tier 2 → Tier 1 promotion proposals (Phase 5)
- Pattern detection algorithms over traces (Phase 5)
- Skills (Phase 4)
- Sophisticated semantic search over memory (Phase 5/6)
- Cross-project memory inheritance (deferred indefinitely)

### Dependencies

- Phase 1 complete (Graph required for validation queries)
- Phase 2 complete (trace emission expected for memory operations)

### Risk assessment

**Risk: Validation rules become slow on large projects.** Some queries (e.g., "find all components matching pattern X") can become expensive on big graphs.
*Mitigation:* Per-query budget (1 sec default). Queries exceeding budget are skipped with warning. User can disable expensive validations.

**Risk: Memory files get out of sync between developers on the same team.** Standard git merge conflicts apply but markdown merging is messy.
*Mitigation:* Memory files are markdown; git's standard 3-way merge works. Document the merge convention. Provide clear conflict markers when validation status differs between branches.

**Risk: Frontmatter parser breaks on malformed YAML.** User edits a file, accidentally invalidates YAML.
*Mitigation:* Forgiving parser. Logs warning, treats malformed file as no-validation. User sees warning in dashboard. File is not deleted or corrupted.

**Risk: Validation gives false negatives.** A genuine pattern is in the project but the query doesn't match how it's named.
*Mitigation:* Treat validation as "hint not enforcement". Failed validation surfaces a warning, not an error. User can update the validation rule or accept the warning. Architecture §4.5 explicitly addresses this — validation is visibility, not enforcement.

**Risk: Proposal queue grows unbounded.** User stops reviewing proposals; queue piles up.
*Mitigation:* Proposals have expiration (configurable, default 30 days). Old proposals auto-archive. Dashboard surface notification when queue hits threshold.

### Implementation hints

- **Architecture §4.1** lists three design philosophies. The neutrality principle (Asphodel mirrors decisions, doesn't judge) is critical — don't let validation become opinionated about what's "good Unity architecture."
- **Architecture §4.2** specifies Tier 1 file structure. Use exactly these names and approximate templates.
- **Architecture §4.3** specifies write paths. Direct human edit, agent proposal (queued), Tier 2 system (deferred to Phase 5). Implement first two.
- **Architecture §4.4** specifies read patterns. Pre-injection at session start (via `get_memory_summary`) and on-demand retrieval (`recall_memory`).
- **Architecture §4.5** is the validation engine specification. Pay particular attention to §4.5.1 (who performs validation) — this is C# code, not the agent.
- **Architecture §4.6 is deferred to Phase 5.** Don't implement Tier 2 inference here.
- **Architecture §4.8** lists edge cases. Each should have a corresponding test.
- **Atomic file writes:** write to `<file>.tmp`, fsync, then rename to `<file>`. This prevents corrupted files if Hades crashes mid-write.
- **Consider Asphodel as a "documentation system that knows about your code"** — that's the user mental model.

### Tests added

**Automated unit tests:**
- Markdown parser: handles frontmatter, content, comments correctly
- YAML parser tolerance: malformed frontmatter logged, not crashed
- Validation rule parser: extracts rules from frontmatter
- Query execution: runs against Graph correctly
- Result comparison: exists/count/equality checks
- Frontmatter writeback: updates status, preserves content
- HTML comment insertion: appears in correct location
- Atomic file write: temp file + rename works correctly

**Integration tests:**
- End-to-end: edit memory file, validation runs, status updates
- Edit memory file with intentional inconsistency, verify warning generated
- Edit project to bring it back in sync, verify warning clears on re-validation
- Proposal queue: agent calls `propose_memory_update`, proposal appears in queue
- Manual proposal accept: file updated correctly
- FileSystemWatcher detects external edit, validation re-runs

**Charon-based regression:**
- Memory read traces produced correctly
- Validation traces include query results
- Phase 2 happy path scenarios continue to work (now with memory traces possibly present)

### Happy Path scenarios

**Scenario 7: Document a decision and verify it sticks**

The developer makes an architectural decision in their project. They open `decisions.md` and add an entry with a validation rule: "We use ScriptableObject event channels; expect at least 3 SO assets matching `*Channel`."

Validation runs (or the developer triggers it manually). The status reports OK because the project does have such SOs.

The developer asks Claude:

> "What's our pattern for inter-system communication?"

The agent reads memory, finds the pattern, and responds with the team's documented approach plus reference to the validated entries.

**Demonstrates:** memory creation, validation, and agent consumption.
**Implicitly verifies:** Phase 1 graph queries work for validation, Phase 2 traces capture memory operations, Phase 3 read/write/validate cycle.
**Pass criteria:** the agent's response references the team's actual decision, not generic Unity advice.

**Scenario 8: Detect drift**

The developer adds a `decisions.md` entry: "We use UnityEvents for inter-system communication" — but the project has actually migrated away from UnityEvents to SO event channels.

Validation runs. The entry's status updates to `warning`. An inline comment explains: "validation expected UnityEvent usage but graph contains 0 such usages."

The developer asks the agent for advice on adding a new system. The agent reads memory, sees the warning, and surfaces the conflict: "Your decisions document says UnityEvents, but I see your project actually uses SO event channels in 4 places. Should we align with the actual pattern?"

**Demonstrates:** validation as honesty mechanism.
**Implicitly verifies:** validation engine works, conflict surfacing works, agent reads validation status correctly.
**Pass criteria:** the agent does not silently accept the contradicted decision; it surfaces the conflict for the developer to resolve.

**Scenario 9: Agent proposes memory update**

The developer and agent collaborate on a non-trivial feature. After completion, the agent recognizes the work established a pattern worth recording. The agent calls `propose_memory_update` with rationale.

The developer opens the dashboard, sees the proposal in the queue, reviews it, edits slightly for clarity, and accepts.

The memory file updates. Validation runs against the new entry. The developer reopens Claude later — the new pattern is now in the agent's context.

**Demonstrates:** proposal workflow as accumulation mechanism.
**Implicitly verifies:** Phase 1 graph (agent uses to inform proposal), Phase 2 traces (proposal call traced), Phase 3 proposal queue and accept flow.
**Pass criteria:** memory grows organically through this loop without losing user control.

### Regression coverage

All Phase 1 and Phase 2 tests must continue to pass. The Phase 1 Happy Path scenarios run again — they should still pass, now with memory possibly empty (which is fine; default behavior unchanged) or possibly populated (which should enrich responses without breaking them).

### Bridge to next phase

Phase 3 unlocks Phase 4 (Skills) by:

- Skills can read memory via `recall_memory` to inform suggestions
- Skills can use validation status to know whether a documented pattern is currently in use
- Skills become genuinely project-aware, not just Unity-domain-aware

Phase 4 begins by migrating UniClaude's existing skills into Hades's plugin format and integrating them with Graph and Asphodel queries.

---

## 6. Phase 4: Skills expansion

### Strategic intent

Phase 4 closes the gap between Hades and the most polished competitor in the skills space, Nice-Wolf-Studio's 35-skill library. Up to this point, Hades has focused on infrastructure (Graph, Charon, Asphodel) — the layers no competitor offers. But infrastructure without breadth of guidance still loses to a focused skill library on day-to-day tasks like "how do I implement an audio manager."

This phase migrates UniClaude's existing 10 skills into Hades's plugin format, then expands the library to cover the domains UniClaude lacked: UI Toolkit, networking, AI/behavior, audio, input, shaders, addressables, recipes, ECS, testing.

The differentiator versus competitors' libraries is **integration**. Every Hades skill knows it can query the Graph and read Asphodel. A skill recommending "use this networking pattern" first checks whether the project already uses a networking framework. A skill recommending "use this audio architecture" first reads `decisions.md` for documented audio choices. The skill doesn't deliver generic advice; it delivers project-aware guidance.

### Done criteria

- [ ] All 10 UniClaude skills migrated to Hades plugin format with updated content
- [ ] At least 15 new skills covering domains identified in Vision §5.2.3
- [ ] Every skill has the required structure: when to apply, decision framework, code examples, anti-examples, cross-references
- [ ] Every skill that makes architectural recommendations integrates Graph queries and/or Asphodel reads where applicable
- [ ] Skill versioning works: `plugin.json` declares MCP server compatibility version
- [ ] Compatibility check: agent client warns if MCP version mismatch
- [ ] Skills are activatable via Claude Code based on description matching
- [ ] All planned slash commands work (`/hades:status`, `/hades:rebuild-graph`, `/hades:show-traces`, `/hades:validate-memory`, `/hades:show-proposals`, `/hades:export-traces`)
- [ ] Plugin marketplace at `arcforge/hades-marketplace` is set up and properly published

### Scope: what's in

**Migrated UniClaude skills (10):**
1. unity-architect (top-level routing skill)
2. component-design
3. data-modeling
4. scene-architecture
5. prefab-architecture
6. unity-performance
7. scene-authoring (workflow)
8. prefab-workflow (workflow)
9. animation-workflow (workflow)
10. unity-reviewer

For each migrated skill: rewrite to integrate Graph queries and Asphodel reads where applicable. Add concrete code examples (UniClaude versions were decision-heavy, example-light per Vision §5.3).

**New skills (15+ to fill identified gaps):**
1. unity-ui (UI Toolkit, uGUI, layouts, dialog systems)
2. unity-networking (Netcode, Mirror, Fishnet decision frameworks)
3. unity-ai-behavior (state machines, behavior trees, GOAP, NavMesh)
4. unity-audio (audio managers, mixers, spatial audio)
5. unity-input (new Input System, action maps, multi-device)
6. unity-shaders-urp (URP shader patterns, render features)
7. unity-shaders-hdrp (HDRP shader patterns, custom passes)
8. unity-vfx (VFX Graph, particle systems)
9. unity-addressables (Addressables vs Resources, async loading)
10. unity-recipes-health (health/damage system patterns)
11. unity-recipes-inventory (inventory system patterns)
12. unity-recipes-save (save/load system patterns)
13. unity-recipes-spawn (spawning, pooling, waves)
14. unity-ecs (when to use ECS, Burst, hybrid)
15. unity-testing (EditMode/PlayMode tests, mocking)

**Skill content structure (per Architecture §5.3):**
- When to apply (1-3 sentence activation condition)
- Decision framework (the actual reasoning — decision tree or question set)
- Code examples (concrete C# scaffolds — substantial, not snippets)
- Anti-examples (what shouldn't be written, with explanation)
- Cross-references (other skills, Graph queries, Asphodel reads)

**Slash commands:**
- All commands described in Architecture §5.7

**Distribution:**
- `arcforge/hades-marketplace` GitHub repository complete
- `marketplace.json` listing all skills with proper metadata
- README explaining the marketplace and how to install
- CI for plugin validation (manifest correctness, skill structure)

### Scope: what's out

- Submission to official Anthropic marketplace (Phase 5)
- Skills for engines other than Unity (out of scope)
- Generated skills (e.g., from documentation) — manually curated only
- Skill marketplace UI beyond GitHub repo (out of scope; Anthropic marketplace handles this if/when listed)
- Tier 2 inferred memory integration into skills (Phase 5)

### Dependencies

- Phase 3 complete (skills integrate with Asphodel, so Asphodel must work first)
- Plugin format and marketplace structure established in Phase 0/1

### Risk assessment

**Risk: Skill content quality is hard to measure.** A skill is "done" when it gives good advice — but "good" is subjective.
*Mitigation:* Code examples are testable; advice quality is assessed by manual review using real project scenarios. Cross-reference Nice-Wolf-Studio's library as a quality bar; aim to match or exceed.

**Risk: Maintenance burden of 25+ skills.** Each skill needs updates as Unity evolves.
*Mitigation:* Acceptable cost. Skills don't change often. Major updates trigger one focused review pass. Open source contributors can submit updates.

**Risk: Skill activation false positives.** A skill activates when it shouldn't, displacing better skills.
*Mitigation:* Description matching is precise. If a skill activates wrong, fix the description. Charon traces help diagnose ("agent activated skill X but should have activated Y").

**Risk: Code examples become stale or wrong.** Unity APIs change.
*Mitigation:* Skill examples reference Unity 6+ APIs. Note versioning in skill content. Open source PRs will catch outdated examples.

### Implementation hints

- **Architecture §5** is the authoritative skills specification. Each skill follows that structure exactly.
- **Architecture §5.3** specifies content philosophy: both decision frames and code examples. Don't ship skills that are pure prose.
- **Architecture §5.4** lists three integration patterns. Most skills should use at least one of: check graph state, read memory, propose memory update.
- **Architecture §5.5** specifies versioning. Use semver for plugin and declare MCP compatibility ranges.
- **Architecture §5.6** specifies plugin structure. Use exactly the `.claude-plugin/` and `skills/` layout.
- **Look at Nice-Wolf-Studio** for inspiration on code-example heavy skills. Their skills are public; learn from what they do well.
- **Skill activation is via description matching.** Spend time on writing good descriptions — they're how skills get triggered correctly.
- **Test skills on a real project**, not just by reading them. Activate the skill, see what the agent does, refine.

### Tests added

**Automated tests:**
- Plugin manifest schema validation (every skill has valid `SKILL.md`)
- Skill description quality check (length, presence of activation condition)
- Code example syntactic validity (C# files in skills compile against fixture project)
- MCP version compatibility check correctness
- Slash command execution returns expected results

**Manual quality reviews:**
- Each skill reviewed against real-project scenarios
- Comparison against Nice-Wolf-Studio's equivalent skill (if exists) for quality
- Activation testing: ask the agent a representative question, verify correct skill activates

**Charon-based regression:**
- Phase 1, 2, 3 happy paths run with skills installed — should still work, possibly with richer agent responses

### Happy Path scenarios

**Scenario 10: Implement a complex feature with project awareness**

The developer asks:

> "Add an inventory system to my game."

The agent should:

1. Activate `unity-recipes-inventory` skill
2. The skill instructs the agent to read existing patterns from Asphodel
3. The skill instructs the agent to check the graph for related existing components
4. The agent finds: project has SO event channels (per memory), already has `ItemConfig` SOs (per graph)
5. The agent proposes an inventory implementation that uses SO event channels and references `ItemConfig` — not a generic implementation

**Demonstrates:** skills + graph + memory integration.
**Implicitly verifies:** Phase 1 (graph queries), Phase 2 (traces capture skill activation), Phase 3 (memory read), Phase 4 (skill correctly integrates).
**Pass criteria:** the inventory implementation aligns with the project's existing patterns. Code is recognizable as "fitting the codebase" rather than generic.

**Scenario 11: Architecture decision support**

The developer is starting a new system and asks:

> "Should we use Netcode for GameObjects or Mirror for our multiplayer?"

The agent should:

1. Activate `unity-networking` skill
2. The skill provides a decision framework comparing the two
3. The agent reads project context (Phase 3 memory: "we target mobile, performance critical")
4. The agent reads project state (Phase 1 graph: "no existing networking code")
5. The agent provides a recommendation calibrated to the project's actual context

**Demonstrates:** skills providing project-aware architectural advice.
**Implicitly verifies:** All phases working together.
**Pass criteria:** the recommendation references the project's actual constraints (mobile, performance) and clean-slate state, not just a generic comparison.

**Scenario 12: Code review with severity tiering**

The developer asks the agent to review a recent script change. The agent activates `unity-reviewer` skill, which provides a severity-tiered review approach. The agent uses graph queries to identify dependencies of the changed script, reads memory for project conventions, and produces a review organized as:

- **Critical:** breaking changes to dependents
- **Important:** divergence from project conventions
- **Nice-to-have:** minor style notes

**Demonstrates:** unity-reviewer skill integrated with Graph and Asphodel.
**Implicitly verifies:** all prior phases.
**Pass criteria:** the review is project-aware (cites actual dependencies, actual conventions) rather than generic.

### Regression coverage

All Phase 1, 2, 3 tests continue to pass. Skills should not change graph correctness, trace emission, or memory behavior — they only enrich what the agent does with that information.

### Bridge to next phase

Phase 4 unlocks Phase 5 by:

- Skills are now in place; eval framework can measure skill effectiveness using accumulated trace data
- Tier 2 inferred memory has substantial real usage data to detect patterns from
- The product surface is now complete enough that polishing for public release makes sense

Phase 5 focuses on integration polish, Tier 2 memory, marketplace submission, and getting Hades production-ready for public release.

---

## 7. Phase 5: Integration polish + Tier 2

### Strategic intent

Phase 5 brings Hades to production-grade. Phases 0-4 built the components; Phase 5 polishes the integration, addresses accumulated rough edges, completes deferred features (notably Tier 2 inferred memory), and prepares for public release.

This is also the phase where Hades gets submitted to the official Anthropic plugin marketplace. By this point, the product has been internally used for some time, has accumulated traces and validated patterns, and is ready for outside scrutiny.

### Done criteria

- [ ] Tier 2 inferred memory generation works: pattern detection runs against trace database
- [ ] Tier 2 → Tier 1 promotion proposals appear in queue when confidence/sample thresholds met
- [ ] Inferred patterns are clearly labeled as inferred in agent context (per Architecture §4.6.1)
- [ ] Cross-layer feedback loops work correctly per Architecture §6.4 (graph evolution → memory updates, traces → inference, memory invalidates graph assumptions)
- [ ] Performance optimization passes complete: large project benchmark (50k+ assets) shows acceptable build/query latency
- [ ] All known edge cases from Architecture §8 have explicit handling
- [ ] Documentation complete: user-facing setup guide, troubleshooting, recovery procedures
- [ ] Submitted to official Anthropic plugin marketplace
- [ ] At least 3 in-depth technical writeups about Hades architecture exist (blog posts, in-depth README sections)
- [ ] Public release readiness: README is high quality, examples work, demo materials exist

### Scope: what's in

**Tier 2 inferred memory:**
- Background pattern detection running over trace data
- Acceptance rate analysis by suggestion shape
- Topic clustering from user queries
- Failure correlation analysis
- Inferred pattern files at `.arcforge/memory/inferred/` (gitignored)
- INFERRED labeling discipline per Architecture §4.6.1

**Promotion workflow:**
- Threshold-based promotion proposals (90% confidence, 50 samples by default)
- Proposal entry into queue when threshold crossed
- Dashboard view for promotion proposals with accept/edit/reject

**Feedback loop polish:**
- Graph emit events that Asphodel listens for
- Charon traces feed inferred memory generation
- Memory validation triggers correctly across all change types

**Performance:**
- Profile typical operations on real large project
- Optimize hot paths identified in traces
- Roslyn deep mode safeguards finalized (timeout, memory budget per Architecture §2.3.3)
- Pre-aggregated rollup tables for dashboard if needed

**Edge cases:**
- All §8 failure modes have explicit handling
- Manual recovery procedures documented and tested
- Crash recovery verified

**Documentation:**
- User setup guide with screenshots
- Troubleshooting guide (recovery procedures table from Architecture §8.8)
- Architecture overview for users (less technical than this document)
- Migration guide from competing tools
- Best practices for memory file curation

**Marketplace submission:**
- Submission to `platform.claude.com/plugins/submit`
- All required submission materials prepared

### Scope: what's out

- Eval framework annotation tooling (Phase 6)
- Runtime instrumentation evaluation (Phase 6)
- Multi-project workflow features (Phase 6)
- Asset Store distribution (Phase 6)
- Enterprise features (out of scope)
- Cross-project memory inheritance (out of scope)

### Dependencies

- Phase 4 complete
- Sufficient internal usage to have meaningful trace data for Tier 2

### Risk assessment

**Risk: Pattern detection produces poor inferences.** Tier 2 surfaces patterns that aren't real preferences.
*Mitigation:* Conservative thresholds (90%, 50 samples). All promotions require human review. Architecture §4.6.1 labeling makes inference explicit.

**Risk: Performance issues on large projects emerge late.** Issues only visible at scale aren't caught until Phase 5.
*Mitigation:* Test on a real large project early in Phase 5, not late. If problems emerge, fix before declaring done.

**Risk: Marketplace submission rejection or delay.** Anthropic's review process timing is unpredictable.
*Mitigation:* Self-published marketplace at `arcforge/hades-marketplace` keeps the product fully usable regardless of submission status. Don't block release on official approval.

**Risk: Documentation underestimated as scope.** Quality docs take real time.
*Mitigation:* Treat docs as a Phase 5 deliverable, not an afterthought. Invest accordingly.

### Implementation hints

- **Architecture §4.6** is the Tier 2 specification. Pay special attention to §4.6.1 (labeling) and §4.6.2 (promotion lifecycle).
- **Architecture §6.4** describes the three feedback loops. Implement each end-to-end and verify.
- **Architecture §8** is the failure modes catalog. Treat as a checklist; each entry should have a corresponding test.
- **Architecture §2.3.3 deep mode safeguards** are finalized in this phase.
- **Architecture §2.6 performance characteristics** are the targets. Verify or update with reality.
- **Vision §7.5 phased marketplace strategy:** Phase 1 self-published, Phase 5 official submission. Now is the time.

### Tests added

**Automated tests:**
- Pattern detection algorithms produce expected outputs on synthetic trace data
- Threshold logic: promotion triggered at exactly the right point
- Inferred labeling: agent receives content with INFERRED markers preserved
- Performance benchmarks on large fixture project: build time, query latency, incremental update time

**Manual reviews:**
- Documentation completeness review against checklist
- Real-project usage sessions: dogfooding for two weeks, log issues
- Performance sanity: large project usage feels responsive

**Charon-based regression:**
- All prior phase happy paths run with full Hades stack
- Performance traces show no regression vs Phase 4 baseline

### Happy Path scenarios

**Scenario 13: Pattern emerges and gets formalized**

Over weeks of normal use, the developer consistently uses object pooling for spawned entities. Tier 2 detects this pattern: 95% acceptance rate of object pool suggestions over 60 samples.

The system creates a promotion proposal: "Detected pattern: 'Object pooling for spawned entities'. Add to patterns.md as team convention?"

The developer reviews in dashboard, edits text slightly, accepts. The pattern moves to `patterns.md` with explicit confirmation. From now on, the agent treats this as official team pattern.

**Demonstrates:** Tier 2 inference and promotion workflow.
**Implicitly verifies:** Phase 2 trace accumulation, Phase 3 promotion queue (now used by Tier 2 not just agent).
**Pass criteria:** the cycle runs to completion. Pattern that genuinely emerged from practice gets formalized through visibility, not silent automation.

**Scenario 14: Production-grade performance**

The developer works with a real large project (10k+ assets, 50+ scenes). All operations feel responsive — build time at startup is acceptable, queries return quickly, dashboard renders smoothly.

**Demonstrates:** Phase 5 performance polish.
**Implicitly verifies:** all components handle scale.
**Pass criteria:** subjective "feels fast enough" plus objective benchmark numbers within Architecture §2.6 targets.

**Scenario 15: Recovery from a problem**

A scenario is deliberately introduced: graph database corruption, or scanner failure on edge-case asset. The user invokes recovery procedure from the documentation. Hades returns to working state.

**Demonstrates:** robustness and recovery.
**Implicitly verifies:** error handling, manual recovery commands, documentation accuracy.
**Pass criteria:** the recovery procedure works exactly as documented.

### Regression coverage

All prior phase tests pass. Phase 5 adds substantial new tests but should not break existing behavior; if it does, that's a bug.

### Bridge to next phase

Phase 5 declares Hades production-ready. Phase 6 is post-launch evolution — not building toward a fixed scope, but responding to real-world usage data and feedback.

---

## 8. Phase 6: Long-tail and post-launch

### Strategic intent

Phase 6 is open-ended. Unlike phases 0-5 which had defined scope, Phase 6 evolves based on what actually happens after Hades is in real users' hands. The roadmap cannot predict which features matter most until adoption signals tell us.

This chapter therefore lists candidate directions rather than prescribing them. Whether and when each is pursued depends on usage data, contributor interest, ecosystem evolution, and product feedback.

### Candidate directions

**Eval framework deeper:**
- Annotation tooling for marking trace outcomes
- Aggregation views over trace datasets
- Statistical regression detection
- LLM-as-judge for automated quality assessment

**Runtime instrumentation:**
- Hooks during play mode that capture actual runtime relationships
- Filling in DI/reflection/dynamic instantiation blind spots
- This is a major engineering undertaking; only pursue if there's clear demand

**Multi-project workflows:**
- Cross-project skill sharing with explicit provenance
- Shared eval datasets across projects
- Memory inheritance patterns

**Distribution:**
- Asset Store as supplementary channel
- Documentation site as standalone web property
- Official Anthropic marketplace approval (if not yet obtained)

**Advanced graph features:**
- Roslyn deep mode default-on (after performance is solved)
- Method call graph analysis
- Semantic similarity over graph (if useful in practice)

**Skills ecosystem:**
- Community skill contributions
- Skill marketplace beyond Hades's own (third-party contributions)
- Specialized skills for sub-domains (mobile, console, VR)

**Enterprise considerations:**
- If demand emerges, evaluate enterprise features
- Air-gapped deployment, audit logging, compliance certifications
- Likely separate effort from open-source product

### Decision framework

For each candidate direction, the question to answer is:

1. Is there evidence of real demand? (User requests, traces showing gap, contributor interest)
2. Does it advance the integrated three-layer thesis? Or is it scope drift?
3. Is the engineering cost justified by the impact?

Pursue directions where the answer to all three is yes. Defer or skip where any is no.

### Phase 6 has no Done criteria

This phase doesn't end. It continues as long as Hades is maintained.

---

## 9. Cross-phase concerns

### 9.1 Test infrastructure summary

By the end of Phase 5, the test suite includes:

- **C# unit tests** (NUnit): hundreds of tests covering scanners, graph operations, validation engine, MCP infrastructure
- **Node.js unit tests** (Vitest): tests covering bridge process, dashboard, MCP protocol handling
- **Integration tests** (Unity Test Runner): scanner correctness on fixtures, end-to-end MCP calls, multi-instance behavior
- **Charon-based regression**: traces of happy paths replayed for deterministic Hades-side parts
- **Manual happy path scenarios**: 15 scenarios across phases, manually run after each phase

CI runs the full automated suite on every commit. Manual scenarios are part of phase completion gates.

### 9.2 Cumulative regression principle

Every phase adds tests. No phase removes tests. Test fixtures, once stable, are frozen. This grows the suite over time but ensures regressions cannot occur silently.

When a Phase N change breaks a Phase M test (M < N), this is a bug in Phase N, not "expected behavior change." Investigate and fix before continuing.

### 9.3 Documentation cadence

Documentation is built incrementally:

- Phase 0: README setup instructions
- Phase 1: tool reference, basic usage
- Phase 2: dashboard guide
- Phase 3: memory authoring guide
- Phase 4: skills overview
- Phase 5: comprehensive documentation refresh, troubleshooting guide

Documentation is not a Phase 5 afterthought; it accumulates throughout the journey.

### 9.4 Versioning across phases

Hades version progresses with each phase:

- Phase 0 complete: v0.1
- Phase 1 complete: v0.2
- Phase 2 complete: v0.3
- Phase 3 complete: v0.4
- Phase 4 complete: v0.5
- Phase 5 complete: **v1.0** (public release)
- Phase 6: v1.x and v2.x as evolution dictates

These are internal version tags during phases 0-4. The v1.0 tag is the first publicly announced release.

### 9.5 The product after Phase 5

After Phase 5, Hades is:

- A Unity Package distributable via UPM
- A Claude Code plugin available through self-published marketplace and (pending) official marketplace
- A coherent product with three integrated layers (Graph, Charon, Asphodel) plus 25+ Skills
- Documented for users
- Battle-tested on real Unity projects through dogfooding
- Open source under MIT license
- Free of dependencies on Anthropic auth that could be revoked

It is ready to be used by developers who are not the original developer.

---

## 10. Known issues

Issues discovered during development that need resolution. Tracked here for visibility across phases.

### Multi-instance MCP discovery broken

**Discovered:** Phase 3 happy path validation (2026-05-12)
**Severity:** Medium — blocks multi-project workflows, does not affect single-project use
**Ref:** Architecture §1.8

Running two Unity projects simultaneously with Hades should produce two independent MCP servers on different ports, each discoverable by Claude Code. In practice, opening a second project does not register a second MCP server in the Claude Code tool list — only the first project's connection is visible.

Likely cause: discovery file collision. Both projects write `.arcforge/server.json` to advertise their MCP endpoint, but Claude Code's stdio bridge discovers servers by scanning a fixed path or process name. Two instances overwrite the same discovery entry rather than registering separately. Needs investigation of the bridge's server enumeration logic and the `server.json` path resolution.

### Validation warnings duplicate on repeated runs

**Discovered:** Phase 3 happy path validation (2026-05-12)
**Severity:** Low — cosmetic, does not affect validation correctness
**Ref:** `Editor/Asphodel/MemoryValidator.cs`

When `validate_memory` is called multiple times on a file with failing rules (or when the FileWatcher re-triggers validation after the validator itself writes back to the file), identical `<!-- HADES VALIDATION WARNING -->` HTML comment blocks are appended each time. Observed: a single failing rule produced 3 duplicate warning blocks after 3 validation passes.

The warning-writing logic in `MemoryValidator` needs to be idempotent — either strip existing warning comments before writing new ones, or check for duplicates before appending. The former is cleaner: on each validation pass, remove all `<!-- HADES VALIDATION WARNING ... -->` blocks, then write current warnings fresh. This also correctly handles the case where a previously failing rule now passes (the stale warning gets removed).

---

## 11. Closing

This roadmap is a sequence of phases, each building on the last, each producing something coherent on its own, accumulating into a complete product that realizes the Vision.

The roadmap is honest about what's hard. Phase 1 is the highest-risk: the Graph thesis must validate. Phase 3 has subtle correctness requirements (validation that doesn't drift). Phase 5 has the broadest scope (everything must come together). Phases 2 and 4 are evolutionary rather than transformational — they extend what exists rather than introducing new architectural risks.

The roadmap is also honest about what's outside its scope. Many directions could be pursued — runtime instrumentation, enterprise features, multi-project workflows. These are explicitly Phase 6 candidates, not Phase 0-5 commitments.

What this document does not commit to:

- Specific dates or durations
- Specific allocation of effort between phases
- Specific staffing assumptions

What this document does commit to:

- The order of phases (validated by dependencies)
- The Done criteria of each phase
- The Happy Path scenarios that validate each phase
- The TDD-first principle throughout
- The cumulative regression principle throughout

Execute with discipline against this plan, adjusting as reality teaches. The Vision tells us where we're going. The Architecture tells us how the pieces fit. The Roadmap tells us what to do next.

That is sufficient.

---

*End of Roadmap document.*
