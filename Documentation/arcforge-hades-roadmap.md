# Hades — Roadmap Document

**Version:** 1.5
**Status:** Phase 10 complete — v1.0.0 shipped (2026-05-31)
**Last updated:** 2026-06-03
**Companion to:** Vision document, Architecture document, Plugin document

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
- Phases 4-7 are evolutionary, not foundational. They expand, polish, and ship what Phase 1-3 prove out.

### 1.3 Single ship event at the end

Hades is shipped publicly as a complete product after Phase 7 (the pre-prod to prod phase). Phases 0-6 are internal milestones with their own version tags but no public announcements, marketing, or release events. Phase 6 produces a v0.9 beta; Phase 7 is the v1.0 public release. Phase 8 is post-launch evolution.

The reason: an integrated three-layer product is what differentiates Hades. Shipping just the Graph (after Phase 1) would position Hades as "another knowledge graph for Unity" — competitive with existing tools but not differentiated. Shipping just Graph + Charon would position it as "Unity tooling with observability" — interesting but not the full vision. The complete value proposition requires all three layers + skills, which materializes after Phase 5, and is polished and validated through Phases 6 and 7.

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
- Plugin manifest skeleton (`plugin.json` in `.claude-plugin/`)
- README explaining repository structure and setup instructions

### Scope: what's out

- Any actual scanners (Phase 1)
- SQLite database setup (Phase 1)
- MCP tools beyond ping (Phase 1)
- Charon (Phase 2)
- Asphodel (Phase 3)
- Skills (Phase 4)
- Anthropic marketplace submission (Phase 5, optional)

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

**Risk: Plugin manifest format changes.** Claude Code plugin format evolves; current research is from May 2026.
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

- [x] SQLite database initializes with full schema (nodes, edges, supporting tables) and proper WAL configuration
- [x] SceneScanner builds correct graph from fixture project's scenes (open-scene mode and closed-scene mode both work)
- [x] PrefabScanner builds correct graph including prefab variants and override edges
- [x] ScriptScanner extracts types and methods (shallow mode); deep mode optional and behind config flag
- [x] ScriptableObjectScanner produces nodes for both type definitions and instances
- [x] AddressablesScanner produces graph entries for addressable groups and entries
- [x] MaterialScanner and ShaderScanner produce basic asset nodes
- [x] ProjectSettingsScanner produces singleton nodes for build settings, render pipeline, etc.
- [x] GraphBuilder coordinates full rebuild and incremental updates correctly
- [x] Incremental update triggered by AssetPostprocessor; updates complete within 1 second on typical edits
- [x] At least 10 MCP tools are implemented and tested (specific list below)
- [x] All MCP tool responses include the `confidence` block per Architecture §6.7
- [x] "Rebuild in progress" signal works: queries during rebuild return current data with explicit warning attribute
- [x] Database stays consistent across domain reloads (verified by tests)
- [x] Database stays consistent across Unity restart (verified by tests)
- [x] Manual `Hades: Rebuild Graph` menu command works
- [x] Bundled MCP bridge process auto-registers with Claude Code config on Unity Package install

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
- Plugin manifest (`plugin.json`) with no skills yet (placeholder)
- Setup wizard in Unity Package that auto-registers MCP server with Claude Code config
- Setup wizard prompts user to install Claude Code plugin; eventual public method is `/plugin install hades@TheArcForge/Hades` via the Anthropic marketplace, but local installs use `claude --plugin-dir <path>` during development and testing

### Scope: what's out

- Charon (Phase 2)
- Asphodel (Phase 3)
- Skills library (Phase 4)
- Roslyn deep mode (Phase 1 ships shallow only; deep mode behind feature flag for Phase 1+)
- Per-method call graphs (deep mode requirement)
- Cross-project queries
- Tier 2 inferred memory (Phase 5)
- Eval framework (Phase 8)
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

The phase includes the observability infrastructure (OpenTelemetry instrumentation, SQLite trace backend) and a minimal dashboard for inspecting traces. The full eval framework with annotation tooling is deferred to Phase 8; Phase 2 ships "trace viewer", not "trace analytics platform".

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

- Eval framework dataset features (Phase 8)
- LLM-as-judge eval (Phase 8)
- Agent-side replay (impossible due to non-determinism, see Architecture §3.7.2)
- Aggregations dashboard view beyond simple latency display (Phase 8)
- Annotation tooling (Phase 8)
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

**Scenario 4: Diagnose a problem** ✅ (partial — rebuild too fast on test project to catch mid-flight)

The developer encounters a confusing agent suggestion. They ask the agent for the same task again, then run `/hades:show-traces` and inspect the trace from the first attempt. They see the chain of tool calls, the data the agent saw, and identify why the agent made the choice it did.

For testing purposes, deliberately create a confusing situation: ask the agent to find references to a script while a graph rebuild is in progress. The first attempt may return incomplete results. The trace explicitly shows the rebuild was in progress; the second attempt (after rebuild) returns complete results.

**Demonstrates:** Charon trace inspection.
**Implicitly verifies:** Phase 1 graph queries (still work), Phase 1 incremental update (rebuild detection), Phase 2 confidence modeling propagates through traces.
**Pass criteria:** developer can identify root cause of the confusing suggestion from the trace alone, without needing to reproduce or guess.

**Actual result (2026-05-26):** Fired `hades_rebuild_graph` and `search_by_name` simultaneously via Hub round-trip. Rebuild completed in ~193ms (test project has only 163k nodes from package cache scanning — too fast to catch mid-rebuild). Both calls traced by Charon: rebuild trace shows 35 spans covering each scanner invocation and GUID resolution with per-span timing. Search trace shows `graph.query.search_by_name` child span with 8 results and `confidence.graph_freshness: "current"`. Error traces also captured (wrong parameter names return `status: Error`). Confidence system is wired and would report `"rebuilding"` on a larger project. Root cause identification from trace alone: confirmed — the span tree shows exactly which scanner ran, what it produced, and how long each took.

**Scenario 5: Performance investigation** ✅

The developer notices a tool call feels slow. They open the dashboard, find the trace, and see exactly which sub-operation took the time — a specific graph query, a slow scanner, an HTTP roundtrip.

For testing purposes, deliberately introduce a slow query (e.g., recursive deep traversal on a large fixture). Verify the trace surfaces the slowness clearly.

**Demonstrates:** Charon as performance debugging tool.
**Implicitly verifies:** Phase 1 graph performs reasonably; Phase 2 instrumentation captures latency accurately.
**Pass criteria:** the slow operation is immediately visible in the trace; developer doesn't have to dig.

**Actual result (2026-05-26):** Ran `trace_dependencies` on `SmokeTestScene.unity` with depth 5. Total duration: 1031ms. Trace inspection immediately reveals the bottleneck: child span `graph.query.search_by_name` took 1013ms (97.5% of total) scanning 163,449 nodes with a wildcard pattern, while the actual `graph.query.traverse_dependencies` span took 0ms. A second test (`query_graph` for ScriptType nodes) showed the same pattern: 107ms total, 101ms in `graph.query.find_by_type` across 13,264 results. In both cases the slow operation is immediately visible in the span tree without any guesswork — exactly what the scenario requires.

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
- Sophisticated semantic search over memory (Phase 8)
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

- [x] All 10 UniClaude skills migrated to Hades plugin format with updated content *(11 migrated — unity-workflow included)*
- [x] At least 11 new domain skills covering gaps identified in Vision §5.2.3 (recipe skills deferred to Phase 8 based on demand)
- [x] Every skill has the required structure: when to apply, decision framework, code examples, anti-examples, cross-references
- [x] Every skill that makes architectural recommendations integrates Graph queries and/or Asphodel reads where applicable
- [ ] Skill versioning works: `plugin.json` declares MCP server compatibility version *(deferred — plugin.json has version but no MCP compatibility range field yet)*
- [ ] Compatibility check: agent client warns if MCP version mismatch *(deferred — requires bridge-side version negotiation)*
- [x] Skills are activatable via Claude Code based on description matching
- [x] All planned slash commands work (`/hades:status`, `/hades:rebuild-graph`, `/hades:show-traces`, `/hades:validate-memory`, `/hades:show-proposals`, `/hades:export-traces`)
- [x] Plugin structure validated: `plugin.json`, `skills/`, `commands/` all correctly configured and discoverable by Claude Code

### Scope: what's in

**Migrated UniClaude skills (11):**
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
11. unity-workflow (process skill)

For each migrated skill: rewrite to integrate Graph queries and Asphodel reads where applicable. Add concrete code examples (UniClaude versions were decision-heavy, example-light per Vision §5.3). Workflow skills (scene-authoring, prefab-workflow, animation-workflow) were rewritten from MCP-tool-based patterns to C# Editor scripting patterns — UniClaude's original versions assumed editor-action MCP tools that don't exist in Hades.

**New skills (11 domain skills — recipe skills deferred to Phase 8 based on demand):**
1. unity-ui (UI Toolkit, uGUI, layouts, dialog systems)
2. unity-networking (Netcode, Mirror, Fishnet decision frameworks)
3. unity-ai-behavior (state machines, behavior trees, GOAP, NavMesh)
4. unity-audio (audio managers, mixers, spatial audio)
5. unity-input (new Input System, action maps, multi-device)
6. unity-shaders-urp (URP shader patterns, render features)
7. unity-shaders-hdrp (HDRP shader patterns, custom passes)
8. unity-vfx (VFX Graph, particle systems)
9. unity-addressables (Addressables vs Resources, async loading)
10. unity-ecs (when to use ECS, Burst, hybrid)
11. unity-testing (EditMode/PlayMode tests, mocking)

**Deferred to Phase 8 (recipe skills — added based on user demand):**
- unity-recipes-health (health/damage system patterns)
- unity-recipes-inventory (inventory system patterns)
- unity-recipes-save (save/load system patterns)
- unity-recipes-spawn (spawning, pooling, waves)

**Skill content structure (per Architecture §5.3):**
- When to apply (1-3 sentence activation condition)
- Decision framework (the actual reasoning — decision tree or question set)
- Code examples (concrete C# scaffolds — substantial, not snippets)
- Anti-examples (what shouldn't be written, with explanation)
- Cross-references (other skills, Graph queries, Asphodel reads)

**Slash commands:**
- All commands described in Architecture §5.7

**Distribution:**
- Plugin structure in main repo: `.claude-plugin/plugin.json`, `skills/`, `commands/`
- CI for plugin validation (manifest correctness, skill structure)

### Scope: what's out

- Submission to official Anthropic marketplace (optional, deferred)
- Skills for engines other than Unity (out of scope)
- Generated skills (e.g., from documentation) — manually curated only
- Anthropic marketplace submission (optional discoverability, not delivery)
- Tier 2 inferred memory integration into skills (Phase 5)

### Dependencies

- Phase 3 complete (skills integrate with Asphodel, so Asphodel must work first)
- Plugin format established in Phase 0/1

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

**Scenario 10: Set up audio with project awareness** ✅ (partial — project-aware but test project limits full exercise)

The developer asks:

> "I need to add audio to my game."

The agent should:

1. Activate `unity-audio` skill
2. The skill instructs the agent to check the graph for existing AudioSource components, AudioMixer references, and audio-related scripts
3. The skill instructs the agent to read Asphodel for any documented audio conventions or architectural decisions
4. The agent finds: project already has scattered AudioSource components on prefabs (per graph), no centralized audio manager, and SO event channels as the inter-system communication pattern (per memory)
5. The agent proposes an audio manager architecture that uses SO event channels for audio events, consolidates the scattered AudioSources under a managed system, and follows the project's existing patterns

**Demonstrates:** skills + graph + memory integration on a moderate-scope task.
**Implicitly verifies:** Phase 1 (graph queries for AudioSource/AudioMixer), Phase 2 (traces capture skill activation), Phase 3 (memory read for conventions), Phase 4 (skill correctly integrates all layers).
**Pass criteria:** the audio architecture aligns with the project's existing patterns (SO event channels, existing AudioSources acknowledged). Recommendation is recognizable as project-aware, not generic.

**Actual result (2026-05-13):** Unbiased agent (no hints about Hades) made 20 tool calls, 13 of which were Hades MCP tools (`get_project_summary`, `search_by_name` ×8, `get_scene_summary`, `analyze_render_pipeline`). Correctly identified: URP project, no audio infrastructure, no gameplay scripts yet. Asked 5 targeted clarifying questions (genre, audio categories, scale, built-in vs middleware, scope) before recommending. Agent was project-aware but the test project (the dev sandbox) is not a game — so the "scattered AudioSources + SO event channels" scenario couldn't materialize. Skill activation not testable via subagent dispatch (skills auto-activate in Claude Code's plugin system).

**Scenario 11: Architecture decision support** ✅ (correct behavior, but test project doesn't fit scenario premise)

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

**Actual result (2026-05-13):** Unbiased agent (no hints about Hades) made 3 tool calls (basic file reading, no Hades MCP tools). Correctly pushed back on the premise: "Neither — Hades is an AI infrastructure plugin, not a multiplayer game." This is the right answer for this project. The test project is a dev sandbox; the networking decision framework couldn't be exercised because multiplayer doesn't apply. Need a real game project to fully validate this scenario.

**Scenario 12: Code review with severity tiering** ✅

The developer asks the agent to review a recent script change. The agent activates `unity-reviewer` skill, which provides a severity-tiered review approach. The agent uses graph queries to identify dependencies of the changed script, reads memory for project conventions, and produces a review organized as:

- **Critical:** breaking changes to dependents
- **Important:** divergence from project conventions
- **Nice-to-have:** minor style notes

**Demonstrates:** unity-reviewer skill integrated with Graph and Asphodel.
**Implicitly verifies:** all prior phases.
**Pass criteria:** the review is project-aware (cites actual dependencies, actual conventions) rather than generic.

**Actual result (2026-05-13):** Unbiased agent (no hints about Hades) made 17 tool calls, 6 of which were Hades MCP tools (`get_project_summary`, `search_by_name` ×3, `trace_dependencies`, `find_references_to`). Picked GraphDatabase.cs (618 lines, the most architecturally important file). Found 8 real issues across severity tiers: non-atomic `last_insert_rowid()` (high), fragile singleton (medium), misleading return value on INSERT OR IGNORE conflict (medium), N+1 queries in TraverseDependencies (medium), hardcoded column indices (low). Used `trace_dependencies` and `find_references_to` for impact analysis. Review was project-aware and substantive. Skill activation not tested (subagent limitation) but MCP tool discovery was organic.

### Phase 4 implementation notes

Issues encountered during Phase 4 development, documented for future reference:

1. **SKILL.md format not pre-documented.** Claude Code's plugin system expects YAML frontmatter with `description` field only — no `name`, no `allowed_tools`. The body is free-form markdown. This format wasn't captured in the Architecture document or design spec; had to reverse-engineer from working plugins. Future skills must follow this exact format or Claude Code won't discover them.

2. **UniClaude workflow skills assumed non-existent MCP tools.** The original scene-authoring, prefab-workflow, and animation-workflow skills were built around MCP editor-action tools (`scene_create_gameobject`, `component_add`, `prefab_create`, `animation_create_controller`, etc.) that exist in UniClaude but not in Hades. Hades provides graph *query* tools, not editor *action* tools. All three workflow skills were rewritten to teach C# Editor scripting patterns (EditorSceneManager, PrefabUtility, AnimatorController APIs) instead of tool sequences. This was the right call — the rewritten skills are more durable since they don't depend on a specific tool inventory.

3. **Plugin directory paths diverged from design spec.** The Phase 4 design spec referenced `.claude-plugin/skills/` paths. The actual Claude Code plugin format originally used `Skills~/` and `Commands~/` at repo root (tilde-suffix directories are ignored by Unity's asset pipeline). In Phase 10, these were renamed to the standard `skills/` and `commands/` paths that Claude Code auto-discovers by convention. The explicit `"skills"` and `"commands"` fields were removed from `plugin.json`.

4. **`marketplace.json` eliminated.** The design spec assumed a separate `marketplace.json` file for Anthropic marketplace metadata. The current plugin format puts all metadata in `plugin.json` (`name`, `version`, `description`, `author`, `keywords`, etc.). Deleted `marketplace.json` — it was a Phase 0 artifact based on early plugin format research.

5. **Happy Path testing methodology required correction.** The first round of Happy Path agents were given full context about Hades tools and skills, effectively handing them the answer sheet. This tested whether agents *could* use the tools, not whether they would *discover* them organically. Re-dispatched with truly unbiased prompts (only the developer's question and project path). The unbiased results are more informative: 2 of 3 agents spontaneously discovered and used Hades MCP tools.

6. **Test project limits domain skill validation.** The test project is a development sandbox for the Hades package, not a game. Scenarios 10 (audio) and 11 (networking) couldn't fully exercise their respective skill decision frameworks because there's no gameplay context (no AudioSources, no player controllers, no game managers). The audio agent correctly asked clarifying questions; the networking agent correctly pushed back on the premise. Both are valid outcomes for this project, but neither demonstrates the full "project-aware recommendation" flow. Full validation of domain skills requires testing against a real game project.

7. **Skill activation not testable via subagent dispatch.** Skills auto-activate in Claude Code's plugin system when the user's task matches the SKILL.md `description` field. Subagent dispatch (used for Happy Path testing) doesn't have this mechanism — subagents see MCP tools but not skills. This means Happy Path testing validated MCP tool discovery and project-awareness, but not skill activation or skill-guided workflows. Skill activation should be validated manually in a real Claude Code session.

8. **22 skills, not 21.** The design spec counted 10 migrated + 11 new = 21. The actual count is 11 migrated + 11 new = 22, because `unity-workflow` (the process/meta skill) was included in the migration. This is a count discrepancy in the spec, not a scope change.

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

This is also the phase where Hades may optionally be submitted to the official Anthropic plugin marketplace for discoverability. By this point, the product has been internally used for some time, has accumulated traces and validated patterns, and is ready for outside scrutiny.

### Done criteria

- [x] Tier 2 inferred memory generation works: pattern detection runs against trace database
- [x] Tier 2 → Tier 1 promotion proposals appear in queue when confidence/sample thresholds met
- [x] Inferred patterns are clearly labeled as inferred in agent context (per Architecture §4.6.1)
- [x] Cross-layer feedback loops work correctly per Architecture §6.4 (graph evolution → memory updates, traces → inference, memory invalidates graph assumptions)
- [x] UniClaude MCP tool migration complete: 68 editor-action tools ported across 14 files, plus ManualReloadStrategy and GameObjectResolver *(7 tools skipped: 6 FileTools redundant with native file tools, 1 ProjectSearch superseded by Graph)*
- [x] Post-migration workflow skill updates: scene-authoring, prefab-workflow, animation-workflow skills reference ported tools as alternative to C# scripting
- [ ] Performance optimization passes complete: large project benchmark (50k+ assets) shows acceptable build/query latency
- [ ] All known edge cases from Architecture §8 have explicit handling
- [ ] Documentation complete: user-facing setup guide, troubleshooting, recovery procedures
- [ ] Optional: submitted to official Anthropic plugin marketplace for discoverability (does not block Phase 5 completion)
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

**UniClaude MCP tool migration (75 → Hades): ✅ Complete (2026-05-15)**

UniClaude shipped 75 MCP tools providing direct editor actions (scene manipulation, component management, prefab operations, etc.). Phase 0 migrated the *infrastructure* (MCPServer, MCPDispatcher, HttpTransport) but not the tool implementations. This sub-phase migrated the tool implementations.

**Result:** 68 tools ported across 14 files. 7 tools skipped (6 FileTools redundant with native file tools across all clients, 1 ProjectSearch superseded by Graph). Additionally ported: `ManualReloadStrategy` (implements `IDomainReloadStrategy` for explicit domain reload control) and `GameObjectResolver` (shared utility used by 7 tool files for resolving GameObjects by hierarchy path including inactive objects).

Hades now has **89 total MCP tools**: 21 original (Graph, Charon, Asphodel, Core) + 68 migrated editor-action tools.

Migration was mechanical — same `[MCPTool]`/`[MCPToolParam]` attribute pattern, same `MCPToolResult` return type. Each tool file was copied to `Editor/MCP/Tools/`, namespace changed to `ArcForge.Hades.Editor.MCP.Tools`, XML doc comments stripped to match Hades style. `MCPDispatcher` auto-discovers all tools via reflection.

**Ported tools by category (68 tools, 14 files):**

| Category | Tools | File |
|----------|-------|------|
| Scene Hierarchy (7) | `scene_get_hierarchy`, `scene_create_gameobject`, `scene_create_primitive`, `scene_delete_gameobject`, `scene_reparent_gameobject`, `scene_rename_gameobject`, `scene_setup` | SceneTools.cs |
| Scene Management (6) | `scene_save`, `scene_create`, `scene_open`, `scene_duplicate`, `scene_list_build`, `scene_set_build` | SceneManagementTools.cs |
| Inspector (2) | `inspector_select`, `inspector_inspect` | InspectorTools.cs |
| Component (8) | `component_add`, `component_find`, `component_remove`, `component_get_all`, `component_get_property`, `component_set_property`, `component_set_properties`, `component_list_properties` | ComponentTools.cs |
| Prefab (8) | `prefab_create`, `prefab_instantiate`, `prefab_apply_overrides`, `prefab_get_contents`, `prefab_edit_property`, `prefab_open_editing`, `prefab_save_editing`, `prefab_create_variant` | PrefabTools.cs |
| Material (6) | `material_create`, `material_set_property`, `material_get_properties`, `material_assign`, `material_duplicate`, `material_swap_shader` | MaterialTools.cs |
| Tag & Layer (5) | `tag_create`, `tag_delete`, `tag_list`, `layer_create`, `layer_list` | TagLayerTools.cs |
| Animation (5) | `animation_assign_controller`, `animation_assign_clip`, `animation_get_controller`, `animation_create_controller`, `animation_edit_controller` | AnimationTools.cs |
| Reference (3) | `reference_set`, `reference_get`, `reference_find_unset` | ReferenceTools.cs |
| Event (4) | `event_add_listener`, `event_remove_listener`, `event_list_listeners`, `event_find_all` | EventTools.cs |
| Asset (4) | `asset_get_info`, `asset_find`, `asset_move`, `asset_import` | AssetTools.cs |
| Asset Import (3) | `asset_get_import_settings`, `asset_set_import_settings`, `asset_set_clip_import_settings` | AssetImportTools.cs |
| Domain Reload (3) | `BeginScriptEditing`, `EndScriptEditing`, `project_recompile_scripts` | DomainReloadTools.cs |
| Project (4) | `project_run_tests`, `project_get_console_log`, `project_get_settings`, `project_refresh_assets` | ProjectTools.cs |

**Skipped tools (7):**

| Category | Tools | Reason |
|----------|-------|--------|
| File (6) | `file_read`, `file_write`, `file_create_script`, `file_modify_script`, `file_delete`, `file_find` | Redundant with native file tools (Claude Code and Claude Desktop both have file tools via MCP). `file_create_script` functionality covered by skills teaching proper Unity script templates. |
| Project Search (1) | `project_search` | Superseded by Hades Graph tools (`query_graph`, `search_by_name`). Graph provides richer results with dependency information. |

**Post-migration: workflow skills updated.** Phase 4 rewrote workflow skills (scene-authoring, prefab-workflow, animation-workflow) to teach C# Editor scripting patterns because the editor-action tools didn't exist in Hades. These skills now reference the ported tools as an *alternative* approach — the agent can choose between direct tool calls and C# scripting based on context.

**Testing:** `MigratedToolDiscoveryTests.cs` verifies all 68 tool names are discoverable by `MCPDispatcher`. HTTP smoke tests (calling tools and verifying response shape) deferred — require a running Unity Editor with a test scene fixture for tools that mutate state.

**Deferred:**
- `asset_get_info` enrichment with Graph dependency data (optional polish)
- HTTP smoke tests for editor-action tools (requires test scene fixture)

**Deferred from Phase 4:**
- Skill/MCP version compatibility range in `plugin.json` (tool version negotiation)
- Hub-side compatibility check warning on version mismatch

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

**Anthropic marketplace (optional):**
- Submission to `platform.claude.com/plugins/submit` if traction warrants
- All required submission materials prepared (valid `plugin.json`, public repo, documentation)

### Scope: what's out

- Eval framework annotation tooling (Phase 8)
- Runtime instrumentation evaluation (Phase 8)
- Multi-project workflow features (Phase 8)
- Asset Store distribution (Phase 8)
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
*Mitigation:* Hades is fully functional without marketplace listing — users install directly from GitHub. Marketplace is purely discoverability. Don't block release on approval.

**Risk: Documentation underestimated as scope.** Quality docs take real time.
*Mitigation:* Treat docs as a Phase 5 deliverable, not an afterthought. Invest accordingly.

### Implementation hints

- **Architecture §4.6** is the Tier 2 specification. Pay special attention to §4.6.1 (labeling) and §4.6.2 (promotion lifecycle).
- **Architecture §6.4** describes the three feedback loops. Implement each end-to-end and verify.
- **Architecture §8** is the failure modes catalog. Treat as a checklist; each entry should have a corresponding test.
- **Architecture §2.3.3 deep mode safeguards** are finalized in this phase.
- **Architecture §2.6 performance characteristics** are the targets. Verify or update with reality.
- **Vision §7.5 marketplace strategy:** Anthropic marketplace submission is optional discoverability. Submit if traction warrants.

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

### Phase 5a implementation notes

Issues and decisions from the Tier 2 inferred memory implementation, documented for future reference:

1. **4 pluggable analyzers implemented.** The pattern detection engine ships with AcceptanceRate, TopicCluster, TimeOfDay, and FailureCorrelation analyzers. Each implements `IPatternAnalyzer` and is registered with `PatternInferenceEngine` at startup. New analyzers can be added without touching the engine — open/closed principle holds.

2. **PatternInferenceEngine orchestrates analyzers against Charon trace data.** The engine queries the `traces` and `spans` tables directly (read-only) and fans out to registered analyzers. Each analyzer receives a windowed slice of trace data and returns zero or more `InferredPattern` candidates. The engine deduplicates and merges candidates from multiple analyzers before persisting to `.arcforge/memory/inferred/`.

3. **PromotionEvaluator handles the full Pending→Proposed→Accepted/Dismissed/Deferred lifecycle.** Thresholds (90% confidence, 50 samples by default) are checked on each inference pass. When a candidate crosses both thresholds, `PromotionEvaluator` writes a promotion proposal to the existing proposal queue at `.arcforge/memory/proposals/`. The accept/reject/defer UI in the dashboard's "Proposals" view was reused without changes — the queue format is identical whether the proposal came from an agent or from Tier 2.

4. **GraphBuilder.OnRebuildComplete event replaces direct calls for cross-layer feedback.** Previously, Asphodel's validation triggers were wired with direct method calls from `GraphBuilder`. Replacing these with a `static event Action OnRebuildComplete` broke the direct dependency and allowed Asphodel and the inference engine to subscribe independently. This also makes the feedback loop testable in isolation — tests can raise the event without running a real rebuild.

5. **Dashboard API extended with /inferred endpoints.** Three new Express routes were added: `GET /api/inferred` (list inferred patterns with status), `GET /api/inferred/:id` (pattern detail with supporting trace evidence), and `GET /api/inferred/promotions` (patterns currently above threshold, awaiting review). The existing Proposals view was extended with a "From Tier 2" filter to distinguish agent-proposed entries from system-inferred ones.

6. **7 test files + shared fixture factory.** Coverage: `AcceptanceRateAnalyzerTests` (5), `TopicClusterAnalyzerTests` (5), `TimeOfDayAnalyzerTests` (5), `FailureCorrelationAnalyzerTests` (6), `PromotionEvaluatorTests` (7), `PatternInferenceEngineTests` (5), and `InferenceIntegrationTests` (3) — 36 tests total. `SyntheticTraceFixtures` provides 5 reusable fixture factories (AcceptanceRate, TopicCluster, TimeOfDay, FailureCorrelation, Empty) shared across analyzer test files.

7. **Fixed: MemoryFileWatcher → Validator infinite loop.** `ValidateFile()` writes updated frontmatter (validation_status, last_validated_against_graph) back to the memory file. `MemoryFileWatcher` detected this write and scheduled another `ValidateFile()` via `delayCall`, creating an infinite loop that pegged the CPU at 100% and froze the Editor on startup. Fix: added `Suppress()`/`Resume()` methods to `MemoryFileWatcher` that toggle `EnableRaisingEvents`. Both `OnGraphRebuild()` and `OnMemoryFileChanged()` in `AsphodeInitializer` now suppress the watcher around internal writes. External edits (user or MCP tools) still trigger validation normally.

### Phase 5b implementation notes

Issues and decisions from the UniClaude MCP tool migration, documented for future reference:

1. **68 tools migrated, 7 skipped.** All 6 FileTools (`file_read`, `file_write`, `file_create_script`, `file_modify_script`, `file_delete`, `file_find`) skipped — redundant with native file tools available in both Claude Code and Claude Desktop (via MCP filesystem server). `file_create_script` functionality covered by existing skills teaching Unity script templates. `project_search` skipped — superseded by Graph tools (`query_graph`, `search_by_name`).

2. **GameObjectResolver ported as shared utility.** 7 of the 14 tool files depend on `GameObjectResolver.FindByPath()` for resolving GameObjects by hierarchy path (including inactive objects). This was not listed in the original roadmap migration plan — it was discovered during implementation as a compilation dependency. Ported to `Editor/MCP/Utilities/GameObjectResolver.cs` in namespace `ArcForge.Hades.Editor.MCP.Tools`.

3. **ManualReloadStrategy ported but not wired as selectable.** `ManualReloadStrategy` implements `IDomainReloadStrategy` and is referenced by `DomainReloadTools` (`BeginScriptEditing`/`EndScriptEditing`). Ported to `Editor/MCP/DomainReload/ManualReloadStrategy.cs`. `MCPServer.Start()` still defaults to `AutoReloadStrategy`. The `DomainReloadTools` gracefully no-op when Auto is active (the `as ManualReloadStrategy` cast returns null). To enable manual reload control, a setting must be added to `HadesSettings` to choose between Auto/Manual strategies.

4. **ConsoleLogBuffer session key preserved.** `ProjectTools.cs` contains an inline `ConsoleLogBuffer` class with a `SessionStateKey` of `"UniClaude.ConsoleBuffer"`. This runtime string was preserved as-is during the mechanical port. It's a `SessionState` key (editor-session-scoped), not user-facing, and changing it would lose any buffered log data across the migration. Could be renamed to `"Hades.ConsoleBuffer"` in a future cleanup.

5. **Tool count discrepancy from roadmap estimates.** The roadmap originally estimated "48 tools in Tier 1" and "~54 editor-action tools" to migrate. Actual counts: 68 tools migrated (the tiers were estimates and some tools weren't individually counted). The roadmap also referenced "75 tools" total in UniClaude; the actual `[MCPTool(` annotation count is 75, confirming 68 migrated + 7 skipped = 75.

6. **Existing Hades tool count is 21, not 41.** The design spec initially estimated 41 existing Hades tools. The actual count is 21 — the earlier estimate double-counted `[MCPToolParam]` annotations as tools. Corrected total: 21 original + 68 migrated = 89 MCP tools.

7. **Workflow skills updated with tool alternatives.** scene-authoring, prefab-workflow, and animation-workflow skills now include an "Alternative: Direct MCP Tool Calls" section listing the relevant ported tools. The guidance: use tools for quick one-off operations, use C# scripting for reusable Editor tools or complex batch operations. Tool names also added to each skill's Cross-References section.

8. **HTTP smoke tests deferred.** Discovery tests verify all 68 tools are found by `MCPDispatcher` via reflection. HTTP-level smoke tests (calling tools and verifying response shape) require a running Unity Editor with a test scene fixture for state-mutating tools. These are deferred until a test scene fixture is established.

### Phase 5c implementation notes

Issues and decisions from the Node.js script scanner migration, documented for future reference:

1. **ScriptScanner migrated from C#/Mono to Node.js.** The `ScriptScanner` class and `ParallelScanPhase` pipeline were replaced by a standalone Node.js process in `Scanner~/`. Motivation: Mono's regex engine does not JIT-compile `RegexOptions.Compiled` to native IL, making it 15-30x slower than V8 for the same patterns. On a test project with 6,268 package scripts, package scanning dropped from 3-5 minutes to ~9 seconds.

2. **Scanner versioning preserved migration path.** The Node.js scanner uses version 2 (C# was version 1). When projects upgrade, the version mismatch in `scanned_assets` automatically triggers a full re-scan — no manual migration step needed.

3. **Package path resolution via PackageInfo.** Initial implementation hardcoded `Packages/com.arcforge.hades/Scanner~/` as the scanner path. This failed because Unity resolves `file:` package references differently — the actual disk path must be retrieved via `PackageInfo.FindForAssembly(typeof(GraphBuilder).Assembly).resolvedPath`. Fixed during live testing.

4. **DbFlushPhase became dead code.** Deleting `ParallelScanPhase.cs` left `DbFlushPhase.cs` without any consumer. Its `AssetScanEntry` type (defined in `ParallelScanPhase`) caused a compilation error. Resolved by deleting `DbFlushPhase.cs` as well.

5. **GraphBuildLog import dependency.** `GraphBuildLog` lives in the `Pipeline` namespace. Removing `using ArcForge.Hades.Editor.Graph.Pipeline` from `GraphBuilder.cs` (intended to drop the `ParallelScanPhase` dependency) also broke `GraphBuildLog`. Restored the import.

6. **Worker threads for large scans.** Full scans with 1000+ files use `worker_threads` to parallelize parsing across `cpus - 1` cores. Workers handle file I/O and regex; the main thread handles all SQLite writes in a single transaction. Below 1000 files, parsing is synchronous (worker spawn overhead not justified).

7. **58 Node.js tests.** Test suite covers: hasher (3), meta-resolver (3), parser (18), db-writer (25), discovery (4), integration (5). All tests use Jest with `--experimental-vm-modules` for ESM support. Test runtime: ~0.4 seconds.

8. **Verified results: 163,449 nodes, 161,696 edges.** First boot on a near-empty Unity project (only Hades package installed) indexed 13,261 package types across 6,268 .cs files in ~10 seconds. MCP query stress test: 20 queries averaging 154ms each against the 163K-node graph.

### Regression coverage

All prior phase tests pass. Phase 5 adds substantial new tests but should not break existing behavior; if it does, that's a bug.

### Bridge to next phase

Phase 5 delivers the core product. Phase 6 resolves accumulated polish items — documentation drift, validation gaps, bug fixes, performance benchmarking — and produces a v0.9.0 beta suitable for early external users. Phase 7 then takes v0.9 to v1.0 public release.

---

## 8. Phase 6: Polish and ship-readiness (v0.9)

### Strategic intent

Phase 6 takes the functionally complete Phase 5 product and resolves every known rough edge standing between "works for the author" and "works for an external developer." This phase does not add features; it fixes documentation drift, validates the Hub end-to-end, runs skipped acceptance scenarios, benchmarks performance at scale, writes user-facing guides, and synchronizes version numbers.

The target state after Phase 6: a fully functional v0.9.0 beta that an external developer can install and use without hand-holding. This is the beta gate.

### Done criteria

- [x] Hub end-to-end validated: Claude Code → Launcher → Hub → Unity round-trip works
- [x] All version fields synchronized at 0.9.0 across `package.json`, `plugin.json`, and roadmap
- [x] Architecture doc updated: no stale references to `server.json`, port scanning, or 3-process model
- [x] Architecture doc config example (§6.6) reflects actual current settings (dead fields removed)
- [x] Architecture doc skill list (§5.2.3) matches actual 22 skills (split URP/HDRP/VFX, deferred recipes)
- [x] Architecture doc recovery table (§8.8) references Hub (`hub.json`), not `server.json`
- [x] Phase 1 done criteria checkboxes updated to `[x]` in this roadmap
- [x] Validation warning duplication bug fixed (idempotent warning writes)
- [x] `ConsoleLogBuffer` SessionState key renamed from `UniClaude.ConsoleBuffer` to `Hades.ConsoleBuffer`
- [x] Phase 2 Happy Path scenarios 4 and 5 executed and documented
- [x] Large-project performance benchmark run (163k nodes, results documented vs Architecture §2.6 targets)
- [x] User-facing README rewritten for external developers (installation, first use, troubleshooting)
- [x] Troubleshooting guide written (symptom → cause → fix table)
- [x] Roadmap updated with Phase 6 status and known issues resolved

### Scope: what's in

**Documentation fixes (Architecture doc):**
- §1.1: Update process count from 3 to 5 (add Hub and Launcher)
- §1.2: Update system diagram to show Hub/Launcher routing
- §5.2.3: Replace single `unity-shaders` with `unity-shaders-urp`, `unity-shaders-hdrp`, `unity-vfx`; remove `unity-recipes` (deferred)
- §6.6: Remove dead `dashboard_port` and `mcp.port_range` config fields; replace with current settings
- §8.8: Replace `server.json` reference with `hub.json` in recovery table

**Bug fixes:**
- `MemoryValidator.cs`: Strip existing `<!-- HADES VALIDATION WARNING -->` blocks before writing new ones (idempotent writes)
- `ProjectTools.cs`: Rename `ConsoleLogBuffer` SessionState key from `"UniClaude.ConsoleBuffer"` to `"Hades.ConsoleBuffer"`

**Validation:**
- Hub end-to-end: start Hub, start Launcher, register Unity, round-trip tool call, order-independent startup, domain reload resilience
- Phase 2 Happy Path scenario 4 (Diagnose a problem via Charon traces)
- Phase 2 Happy Path scenario 5 (Performance investigation via dashboard)
- Large-project performance benchmark against Architecture §2.6 targets

**Version synchronization:**
- Sync `package.json` (currently 0.1.0), `plugin.json` (currently 0.6.0), and roadmap (currently says 0.5.0) to 0.9.0

**User-facing documentation:**
- README rewrite for external developers
- Troubleshooting guide consolidating Plugin doc §7, Architecture §8.8, and known issues

### Scope: what's out

- New features (none — this is purely polish and validation)
- Marketplace submission (Phase 7)
- Plugin repo split (Phase 7)
- CI workflows (Phase 7)
- v1.0.0 release (Phase 7)

### Dependencies

- Phase 5 complete (all sub-phases 5a/5b/5c delivered)

### Risk assessment

**Risk: Hub doesn't work end-to-end.** The Hub source is compiled but has never been started in a real Claude Code session (`~/.arcforge/hades-hub/hub.json` absent).
*Mitigation:* This is the highest-priority validation item. Execute early. If the Hub has bugs, fix them before proceeding.

**Risk: Performance benchmark reveals unacceptable latency on large projects.** Optimization was deferred through Phase 5.
*Mitigation:* Architecture §2.6 has explicit targets. If exceeded, optimize hot paths identified in Charon traces. Acceptable degradation is documented; unacceptable degradation blocks the phase.

**Risk: Documentation refresh takes longer than expected.** Quality external-facing docs are real work.
*Mitigation:* Focus on the README and troubleshooting guide — the two documents an external user needs on day one. Architecture-level docs can be polished incrementally.

### Implementation hints

- Architecture doc fixes are mechanical — the correct content is known from the audit, just needs to be written into the existing sections.
- Hub validation protocol: (1) Hub starts and writes `hub.json`, (2) Launcher starts Hub if needed and bridges stdio↔HTTP, (3) Unity registers with Hub, (4) Full round-trip via Claude Code `/hades:status`, (5) Order-independent startup, (6) Domain reload resilience.
- For the performance benchmark, use Charon traces to get per-operation timing breakdown.
- The README should follow the install flow from Plugin doc §4.1: Step 1 Unity Package via UPM, Step 2 Claude Code plugin. Local installs use `claude --plugin-dir <path>`; the marketplace `/plugin install` flow is the eventual public method. The README has been updated to reflect both paths.

### Tests added

**Automated:**
- Validation warning idempotency test: validate same file twice, assert only one warning block exists

**Manual:**
- Hub end-to-end validation (6-step protocol described above)
- Phase 2 Happy Path scenarios 4 and 5
- Large-project performance benchmark
- README install instructions followed on a test environment

### Happy Path scenarios

Phase 6 does not introduce new Happy Path scenarios. It completes the two skipped scenarios from Phase 2:

**Scenario 4: Diagnose a problem** ✅ (originally Phase 2, executed in Phase 6)

Results documented in Phase 2's Happy Path section. Charon traces capture full span trees with per-operation timing; root cause identifiable from trace alone.

**Scenario 5: Performance investigation** ✅ (originally Phase 2, executed in Phase 6)

Results documented in Phase 2's Happy Path section. Slow sub-operations immediately visible in span waterfall — 1031ms call revealed 1013ms (97.5%) in a single child span.

### Regression coverage

All Phase 0–5 tests must continue to pass. Bug fixes in this phase (validation warning, ConsoleLogBuffer key) add their own regression tests.

### Phase 6 implementation notes

Issues encountered during Phase 6 development, documented for future reference:

1. **Hub breadcrumb path is the normal startup flow.** Unity's `HubClient.Register()` checks for a running Hub and falls back to writing a breadcrumb to `~/.arcforge/hades-hub/pending/`. The Hub reads pending breadcrumbs on startup. This order-independent startup works correctly but means the first tool call after a cold start may need the Hub to start (via Launcher) before Unity's registration is picked up. Not a bug — working as designed.

2. **Hub auto-exits after 60 seconds with no connected launchers.** If the Hub is started manually (without a Launcher connecting), it shuts down after 60s idle. This caused confusion during E2E validation when the Hub exited before Unity was opened. The Launcher's `/api/launcher/connect` call keeps the Hub alive.

3. **Rebuild too fast for mid-rebuild confidence testing.** On the test project (~20 project assets), full rebuild completes in ~200ms. The confidence system's `graph_freshness: "rebuilding"` state cannot be observed at this speed. A larger project (50k+ real assets) would be needed to validate the during-rebuild confidence path. Deferred to Phase 8.

4. **`trace_dependencies` wildcard scan inflates latency.** The `trace_dependencies` tool performs a `search_by_name` with `%` wildcard (scanning all 163k nodes) before doing the actual traversal. This caused a 1031ms call where the traversal itself took 0ms. This is a query planner issue worth optimizing in Phase 8.

5. **Performance targets in §2.6 are aspirational for edge cases.** Average query performance meets targets, but outliers (full-text search across 163k nodes, full type enumeration of 13k results) exceed targets. All outliers remain under agent reasoning latency and don't affect user experience. See `Documentation/performance-benchmark.md` for full data.

6. **Version 0.1.0 still shows in runtime `hades_ping` responses.** The `package.json` version was bumped to 0.9.0 but the Unity client hasn't been recompiled with the updated package. The runtime version string comes from the compiled assembly, not the package.json at rest. This resolves itself on the next Unity compilation after the version bump is committed.

### Bridge to next phase

Phase 6 produces a validated v0.9.0 beta. Phase 7 takes this to v1.0 public release: plugin repo split, CI, marketplace submission, and release tagging.

---

## 9. Phase 7: Friends-and-family prep (complete)

### Strategic intent

Phase 7 prepared Hades for its first external users. This phase handled plugin distribution, agent routing (ensuring agents use Hades MCP tools instead of defaulting to bash), onboarding documentation, and repository cleanup.

Phase 7 was validated by deploying to an external developer working on a large-scale real Unity project. The feedback confirmed the core thesis: Hades's typed Unity-asset navigation is materially better than grep, and the graph delivers real value. It also surfaced four reproducible bugs and a set of agent UX gaps — these drive Phases 8 and 9.

### Done criteria

- [x] Sync script (`scripts/sync-plugin.sh`) implemented — produces plugin repo content (872KB, 62 files) from main repo, copies `plugin-README.md` and `plugin-CLAUDE.md` as the plugin repo's `README.md` and `CLAUDE.md`
- [x] Plugin manifest fix: added `"commands"` to `plugin.json` (commands weren't being discovered) — later removed when directories renamed to standard `commands/` path in Phase 10
- [x] Agent routing — three-layer guidance so agents use Hades MCP tools instead of defaulting to bash:
  - MCP `instructions` field in initialize response (universal, both Claude Code and Desktop)
  - `CLAUDE.md` auto-generated to Unity project root on server start (Claude Code)
  - 22 skills copied to `~/.claude/skills/hades-*/` on server start (Claude Desktop)
- [x] User documentation: `Documentation/getting-started.md` (full walkthrough), `scripts/plugin-README.md`, `scripts/plugin-CLAUDE.md`
- [x] Main `README.md` updated with local install instructions (`--plugin-dir`)
- [x] Repo cleanup: removed 49MB stale `Bridge~/hub/node_modules/` and 61MB `Scanner~/node_modules/` from git tracking, updated `.gitignore`
- [x] Dry-run validation: plugin install via `--plugin-dir`, MCP tools working, agent routing confirmed (agent says "I'll query the Hades knowledge graph" instead of using bash)
- [x] All four documentation docs refreshed (Architecture, Roadmap, Plugin, Vision) to reflect current state
- [x] Field smoke-test completed on a large-scale real Unity project

### Scope: what was delivered

**Plugin distribution:** Sync script, plugin-specific README and CLAUDE.md, `--plugin-dir` workflow validated.

**Agent routing:** MCP `instructions` field in `MCPDispatcher.HandleInitialize()`, auto-generated `CLAUDE.md` at project root, skills copied to `~/.claude/skills/hades-*/` for Claude Desktop.

**User documentation:** `Documentation/getting-started.md` (full walkthrough), `scripts/plugin-README.md`, `scripts/plugin-CLAUDE.md`.

**Repo cleanup:** Removed 110MB of stale `node_modules/` from git tracking.

### Phase 7 validation results

First field feedback on a large-scale production project (on the order of 10k+ assets, 13k+ C# types). Key findings:

**What worked well:**
- `search_by_name` — fast, accurate, typed results. Standout tool.
- `trace_dependencies` — genuine value for prefab hierarchy inspection.
- `find_references_to` on assets — asset-pointer wiring discovery that grep can't do.
- `get_project_summary` — solid structural overview.
- Agent routing confirmed: agent queries graph first instead of bash.

**Bugs found (4 reproducible):**
1. macOS quarantine blocks `libgilzoide-sqlite-net.dylib` on zip install → Phase 8
2. Scanner `npm install` silently fails on first boot → graph missing all C# nodes (38% smaller) → Phase 8
3. Launcher startup race → MCP "failed" on every cold start, Reconnect succeeds → Phase 8
4. `pending_edges` accumulates ~67k unresolvable entries, misleading log → Phase 8

**Coverage gaps identified:**
- No C# code-level reference graph (`find_references_to` returns 0 on scripts) → Phase 9
- Graph stops at Unity project boundary (monorepo sibling projects invisible) → Phase 9
- Unscanned asset types (textures, meshes, animations, audio, fonts) → 67k dead-end edges → Phase 9

### Bridge to next phase

Phase 7 validated that Hades works on real projects and identified the gaps standing between v0.9 and v1.0. Phase 8 addresses first-run reliability (the bugs that hit during install). Phase 9 expands graph coverage (the gaps that limit query value). Phase 10 handles the public release mechanics.

---

## 10. Phase 8: First-run reliability

### Strategic intent

Phase 8 addresses every bug and UX issue that hits during install and first use. The field feedback (Phase 7) revealed a cross-cutting pattern: **operations that fail or partially complete report success or are silenced**, leaving users to investigate manually. This is unacceptable for first-run experience — the highest-stakes window for building trust.

All four bugs share the "looks like failure" anti-pattern. The fixes are individually small but collectively transform the install path from "works if you know the workarounds" to "works on first try."

This phase also addresses the Hub/Unity recovery gap (one-shot registration with no re-Register path) and the build pipeline observability debt that causes phantom bug reports.

### Done criteria

- [x] **Bug 1 — macOS quarantine:** `getting-started.md` recommends git URL install path first; `xattr -dr com.apple.quarantine` workaround documented prominently in troubleshooting guide; zip distribution deprecated in favor of git URL
- [x] **Bug 2 — Scanner npm install:** Exit codes in `GraphBuilder.RunNodeScanner` are distinct (not all exit 3); `node_modules` freshness check validates `node_modules/better-sqlite3/package.json` exists (not just `Directory.Exists`); npm error text surfaces in the graph build log step (not just `Debug.LogError`); one retry with extended timeout on `npm install` failure
- [x] **Bug 3 — Launcher startup race:** Launcher attaches stdin reader before `await ensureHub()`, buffers incoming MCP messages until Hub is ready; alternatively, launcher answers `initialize` locally without Hub round-trip
- [x] **Bug 4 — pending_edges log:** `ResolvePendingEdges()` distinguishes transient pending from permanently unresolvable; log message reads `Resolved N, K unresolvable (unscanned asset types)` instead of misleading `Resolved N/M`
- [x] **Hub recovery:** Unity re-registers with the Hub when `hub.json` changes (FileSystemWatcher or re-Register on failed heartbeat), eliminating the one-heartbeat-interval dead-air gap
- [x] **Build observability:** Each graph build step distinguishes *succeeded* / *expected-unresolvable* / *actually broken* in its output; exit codes from subprocesses are not overloaded with environmental errors
- [x] All fixes have automated tests
- [ ] Clean install tested end-to-end on macOS (the field platform) — all four bugs confirmed resolved

### Scope: what's in

**Bug 1 — macOS quarantine on zip distribution:**
- Update `Documentation/getting-started.md` to recommend git URL install as primary path
- Add `xattr -dr com.apple.quarantine` workaround to `Documentation/troubleshooting.md` with cross-reference from "first Unity open fails" symptom
- Test `ditto -ck --sequesterRsrc --keepParent` as zip repackaging method (doesn't set quarantine seed flag)
- Long-term: evaluate Apple Developer ID + notarization for bundled native libs

**Bug 2 — Scanner npm install silent failure:**
- `Editor/Graph/GraphBuilder.cs`: Assign distinct exit codes — reserve 3 for scanner's own `EXIT_DB_OPEN_FAILED`, use 100 for "Node.js not found", 101 for "npm install failed"
- Replace `Directory.Exists(node_modules)` freshness check with validation that `node_modules/better-sqlite3/package.json` exists
- Surface npm error text in the `GraphBuildLog` step output, not just `Debug.LogError`
- Add one retry of `npm install` with 5-minute timeout before giving up
- Add "exit 3 in Step 1/3" → "run `cd Scanner~ && npm install`" cross-reference in troubleshooting guide

**Bug 3 — Launcher startup race:**
- `Bridge~/launcher/src/index.ts`: Restructure `main()` to attach stdin reader (via `createInterface`) before `await ensureHub()`. Buffer incoming lines in an array; drain after Hub is ready.
- Evaluate answering MCP `initialize` locally from launcher constants (protocol version, server info, capabilities) without Hub round-trip — only `tools/list` and `tools/call` need the Hub.
- Raise `HUB_STARTUP_TIMEOUT_MS` from 5000 to 15000 as defense-in-depth.
- Consider pre-warming Hub from `MCPClientConfig.OnServerStart` (fire-and-forget `node launcher.js < /dev/null &`).

**Bug 4 — pending_edges misleading log:**
- `Editor/Graph/GraphBuilder.cs:ResolvePendingEdges()`: After resolution pass, classify remaining pending edges as "transient" (target type has a registered scanner) vs "permanent" (target extension not covered by any scanner).
- Log: `[Hades] Pending edges: N resolved, K unresolvable (refs to textures, meshes, audio, etc. — asset types not indexed by Hades)`
- Add "Coverage" note to `Documentation/troubleshooting.md` listing which asset types are indexed and which aren't.

**Hub recovery (cross-cutting):**
- `Editor/MCP/MCPServer.cs`: Add `FileSystemWatcher` on `hub.json` (or extend `HubClient.Heartbeat` to detect `hub.json` PID/port changes since last call) and call `Register()` when a new Hub appears.
- This eliminates the up-to-one-heartbeat-interval dead-air gap when the Hub restarts.

**Build pipeline observability (cross-cutting):**
- Each `GraphBuildLog` step reports: succeeded count, expected-unresolved count, actually-broken count.
- Subprocess exit codes carry distinct meanings, not overloaded.
- When the system "logs and continues," the log makes explicit whether Hades is running degraded.

### Scope: what's out

- New scanners for unscanned asset types (Phase 9)
- C# code-level reference graph (Phase 9)
- Search improvements (Phase 9)
- Parameter naming normalization (Phase 9)
- Plugin repo creation, CI, marketplace (Phase 10)

### Dependencies

- Phase 7 complete (field feedback received)

### Risk assessment

**Risk: Launcher stdin buffering introduces message ordering issues.** Buffered lines must be drained in order after Hub ready.
*Mitigation:* Simple array buffer with sequential drain. The MCP protocol is request-response over stdio — messages don't arrive faster than the agent can generate them. Test with rapid `initialize` + `tools/list` in sequence.

**Risk: `npm install` retry masks a real failure.** Two retries might hide a genuine incompatibility.
*Mitigation:* Log each attempt with full error output. If both retries fail, the final error is surfaced prominently — not silently swallowed.

**Risk: FileSystemWatcher on `hub.json` is unreliable on some platforms.** macOS FSEvents and Linux inotify have known edge cases.
*Mitigation:* FileSystemWatcher is the fast path. The existing heartbeat-based auto-register (`server.js:53-66`) remains as the slow fallback. Belt and suspenders.

### Implementation hints

- Bug 2: The probe for `node_modules/better-sqlite3/package.json` is more robust than `Directory.Exists` — it catches partial installs, empty directories, and version mismatches.
- Bug 3: The key insight from the field is that Claude Code's *init timeout* is shorter than its *per-request timeout*. Answering `initialize` from the launcher eliminates the race entirely — the Hub only matters for `tools/list` and `tools/call`, which have 30s per-request budgets.
- Bug 4: `ScannerRegistry` already knows which extensions it covers. Use it to classify pending edges: if the target GUID resolves to a `.meta` whose main asset extension isn't in any scanner's `SupportedAssetType`, it's permanently unresolvable.
- Hub recovery: `HubClient` already reads `hub.json` on every heartbeat. Comparing `(pid, port)` from the file against the last-known values is a 2-line change that detects a Hub restart.

### Tests added

**Automated:**
- `GraphBuilder` exit code tests: verify distinct codes for "node not found" (100), "npm install failed" (101), "scanner DB error" (3)
- `GraphBuilder` npm freshness check: `Directory.Exists` alone doesn't pass; `better-sqlite3/package.json` required
- `GraphBuilder.ResolvePendingEdges`: synthetic pending edges with unscanned-type targets classified as "permanent"; log output matches expected format
- Launcher: integration test sending `initialize` before Hub is ready → response arrives (not timeout)
- Hub recovery: simulate `hub.json` change → Unity re-registers within one update tick

**Manual:**
- Clean macOS install end-to-end: download zip, `xattr` workaround, first Unity open, first Claude Code session — all four bugs confirmed resolved
- Cold-start launcher: kill Hub, start Claude Code — no "failed" state in `/mcp`

### Happy Path scenarios

**Scenario 17: Clean install works on first try**

A developer follows `getting-started.md` on macOS. They:
1. Install via git URL (or zip + `xattr`)
2. Open Unity — graph builds with all C# nodes present (no exit 3 failures)
3. Open Claude Code — MCP server shows connected (not "failed")
4. Build log reports honest numbers: resolved edges, expected-unresolved (unscanned types), no alarming ratios

**Demonstrates:** all four bugs fixed.
**Pass criteria:** developer reaches a working state without any manual workarounds or investigation.

**Scenario 18: Hub restart recovery**

The developer is working. The Hub process dies (or is killed). Within seconds, the next heartbeat tick detects the change. The developer starts a new Claude Code session — it spawns a new Hub, Unity re-registers, tools work immediately.

**Demonstrates:** Hub recovery without manual intervention.
**Pass criteria:** no "failed" state, no stale connections, no manual restarts required.

### Regression coverage

All Phase 0–7 tests must continue to pass. Bug fixes in this phase are additive (new exit codes, better log messages, stdin buffering) and should not change the behavior of working code paths.

### Phase 8 implementation notes

Issues and decisions from Phase 8 development, documented for future reference:

1. **Launcher local initialize eliminates the race entirely.** Rather than buffering stdin messages during Hub startup, the launcher now answers MCP `initialize` locally from constants. Only `tools/list` and `tools/call` need the Hub. This means Claude Code's MCP connection succeeds in <1ms regardless of Hub startup time. Hub startup timeout raised from 5s to 15s as defense-in-depth.

2. **npm freshness validated by marker file, not directory existence.** `Directory.Exists(node_modules)` was replaced with `File.Exists(node_modules/better-sqlite3/package.json)`. This catches empty directories, partial installs, and missing native modules — all conditions that cause silent failures.

3. **Exit codes now distinguish environmental from scanner errors.** 100 = Node.js not found, 101 = npm install failed, 3 = scanner DB error, 2 = database contention. Previously all non-2 failures returned exit 3, making diagnosis impossible.

4. **Heartbeat-based Hub recovery chosen over FileSystemWatcher.** FSWatcher has known reliability issues on macOS. Heartbeat runs every 30s, compares cached (pid, port) from hub.json. Up-to-30s detection lag is acceptable for rare Hub restart events.

5. **Pending edge classification uses ScannerRegistry.** Unresolved pending edges are classified as "permanent" (target extension not covered by any scanner) vs "transient" (should resolve on next rebuild). The log now reads "N resolved, K unresolvable (textures, meshes, etc.)" instead of the misleading "Resolved N/M".

6. **GraphBuildLog tracks degraded state.** A new `ReportDegraded(reason)` method accumulates degradation reasons. The final log message and build log file explicitly report when Hades is running degraded (e.g., "C# nodes missing — Scanner npm install failed").

### Bridge to next phase

Phase 8 makes the install path robust. Phase 9 expands the graph's coverage to close the gaps field testing identified — unscanned asset types and C# code-level references.

---

## 11. Phase 9: Graph coverage expansion

### Strategic intent

Phase 9 addresses the two biggest functional gaps field testing identified: **unscanned asset types** (textures, meshes, animations, audio, fonts) leaving 67k dead-end edges, and **no C# code-level reference graph** making `find_references_to` useless on scripts. These are the gaps that limit Hades from "good Unity asset navigator" to "complete project understanding tool."

The C# reference graph is the single highest-impact improvement identified in external testing. When a developer asks "where is this class used?", the answer is almost always in other C# files — and today Hades returns 0 results. Solving this transforms `find_references_to` from a prefab-only tool into the universal "what depends on this?" query the Vision document promises.

The approach for C# indexing must balance speed and quality. Roslyn semantic analysis gives perfect results but is heavyweight. Tree-sitter or regex-based approaches are fast but miss implicit dependencies. This phase evaluates both paths and ships whichever delivers the right trade-off for the graph's needs.

### Done criteria

- [x] **MetaScanner:** Lightweight scanner creates `Asset` nodes (Texture, Mesh, AnimationClip, AnimatorController, AudioClip, Font, Sprite, Model) from `.meta` files — GUID + path + type, no binary parsing
- [x] **pending_edges near-zero:** After MetaScanner runs, `pending_edges` drops from ~67k to near-zero (only truly missing references remain)
- [x] **Queries on unscanned types work:** `find_references_to` on a `.png` returns all prefabs/materials referencing it; `trace_dependencies` on a UI prefab includes sprites, fonts, audio
- [x] **UnityEngine.dll base types indexed:** Runtime reflection of Unity assemblies seeded as `ScriptType` nodes with `tier = "builtin"` flag; resolves `inherits_from`/`implements` pending edges. Cached by Unity version.
- [x] **C# reference graph:** `find_references_to` on a script/type returns all C# types that reference it (field types, method parameters, construction, inheritance, interface implementation, casts, attributes, generic arguments, local variables). Tree-sitter C# grammar approach chosen (AD-1).
- [x] **C# reference approach validated:** Tree-sitter (Option A) chosen. ~85-90% reference coverage; fast incremental updates. Known gaps: `using` aliases, extension methods, implicit conversions, `var` inference. See implementation notes.
- [x] **Search improvements:** `search_by_name` supports filtering by `path_prefix` (e.g., `Assets/` only, exclude `Packages/`) and `match_mode` (`contains`/`exact`/`prefix`). LIMIT 200.
- [x] **Scanned roots surfaced:** `get_project_summary` reports `asset_coverage` section with indexed type counts, pending edge count, and coverage percentage
- [x] **Parameter naming consistency:** All 89 MCP tools use snake_case. 33 camelCase parameters renamed across 12 tool files. Clean break — no backwards-compatible fallback.

### Scope: what's in

**MetaScanner — lightweight node creation for unscanned asset types:**
- New `MetaScanner` (or per-type scanners: `TextureScanner`, `MeshScanner`, `AnimationScanner`, `AudioClipScanner`, `FontScanner`) that creates a single `Asset` node per `.meta` file
- No binary file parsing — just GUID from `.meta`, asset path, Unity import type from the importer class name in `.meta`
- Handles: `.png`, `.jpg`, `.tga`, `.psd`, `.gif` (Texture); `.fbx`, `.obj`, `.blend` (Model/Mesh); `.anim` (AnimationClip); `.controller` (AnimatorController); `.wav`, `.mp3`, `.ogg` (AudioClip); `.ttf`, `.otf` (Font); `.spriteatlas` (SpriteAtlas)
- These nodes serve as edge targets — the existing scanners (PrefabScanner, MaterialScanner, SceneScanner) already emit edges to these GUIDs; the nodes just need to exist for the edges to resolve
- Incremental updates via `AssetPostprocessor` (same debouncer path as other scanners)

**UnityEngine.dll base type seeding:**
- Ship a precomputed JSON (`Editor/Graph/Data/unity-builtin-types.json`) listing public types from core Unity assemblies: `UnityEngine.CoreModule`, `UnityEngine.PhysicsModule`, `UnityEngine.UI`, `UnityEngine.InputModule`, `TMPro`, etc.
- Seed as `ScriptType` nodes with `properties.source = "builtin"` on graph initialization
- Resolves `inherits_from` edges to `MonoBehaviour`, `ScriptableObject`, `Component`, etc.
- Resolves `implements` edges to `IDisposable`, `IPointerClickHandler`, `ISerializationCallbackReceiver`, etc.
- JSON regenerated per Unity major version; shipped version targets Unity 6000.x

**C# code-level reference graph:**
- **Goal:** For every `ScriptType` node, capture which other `ScriptType` nodes reference it — via field declarations, method parameters/return types, local variable types, constructor calls, inheritance, interface implementation, generic type arguments, attribute usage.
- **Approach evaluation (to be resolved early in the phase):**
  - *Option A — Extend Node.js scanner with tree-sitter C# grammar:* Fast (V8 speed), already proven architecture. Tree-sitter parses syntax without semantic resolution — captures explicit type names but misses `using` alias resolution, partial classes across files, implicit conversions, extension methods. Estimated 80-90% of references captured. Can be supplemented with a `using`-statement resolver pass.
  - *Option B — Roslyn semantic analysis via Node.js child process:* `dotnet` CLI tool that runs Roslyn `SemanticModel` analysis, outputs JSON. Perfect accuracy but requires .NET SDK on the developer's machine (which Unity developers have). Speed concern: Roslyn compilation of a full Unity project can take 30-60 seconds. Incremental: Roslyn supports `WithChangedDocument` for single-file re-analysis.
  - *Option C — Hybrid:* Tree-sitter for fast incremental updates (single file changed → re-parse in <100ms), Roslyn for periodic full validation (background, non-blocking). Ship tree-sitter first, add Roslyn as optional deep mode.
- **Decision criteria:** Speed at scale (13k+ types); correctness on real Unity code (generics, partial classes, nested types, extension methods); incremental update time for single-file changes.
- New edge types: `type_references` (ScriptType → ScriptType), with `properties.reference_kind` distinguishing field, parameter, return, construction, attribute, generic_argument.
- Results surfaced through existing `find_references_to` tool — no new tool needed, just richer results.

**Search improvements:**
- `search_by_name`: Add optional `path_filter` parameter (e.g., `"Assets/"` excludes `Packages/` results)
- `search_by_name`: Add optional `match_mode` parameter: `substring` (default, current behavior), `exact`, `word` (word-boundary matching)
- Consider fuzzy fallback: if exact/word match returns 0 results, auto-retry with substring and surface "did you mean?" in the response

**Scanned roots visibility:**
- `get_project_summary` includes a `scanned_roots` field listing which directories are indexed (e.g., `Assets/`, `Packages/`) and a `coverage_notes` field listing known blind spots (e.g., "sibling C# projects outside the Unity project are not indexed")
- MCP `instructions` field updated to tell the agent about project boundary limitations

**Parameter naming normalization:**
- Audit all 89 tools for parameter naming convention
- Normalize to snake_case (the convention used by native Hades tools)
- For migrated UniClaude tools using camelCase: accept both forms transparently (check for snake_case first, fall back to camelCase) to avoid breaking existing agent muscle memory

### Scope: what's out

- Binary asset parsing (texture dimensions, mesh vertex counts, audio sample rates) — nodes are metadata-only
- Runtime instrumentation for DI/reflection (Phase 11 candidate)
- Cross-project graph queries (monorepo sibling projects) — document the boundary instead
- Roslyn deep mode for method call graphs (`calls` edges between `ScriptMethod` nodes) — deferred to Phase 11
- Plugin repo creation, CI, marketplace (Phase 10)

### Dependencies

- Phase 8 complete (install path is reliable)
- For C# reference graph Option B (Roslyn): .NET SDK available on developer machines (standard for Unity developers)

### Risk assessment

**Risk: MetaScanner increases graph size significantly.** A project with 10k textures adds 10k nodes.
*Mitigation:* The nodes are lightweight (GUID + path + type, no substructure). Storage impact is minimal. Query performance impact negligible — existing indexes handle the volume.

**Risk: C# reference graph approach doesn't scale.** Roslyn is too slow; tree-sitter misses too many references.
*Mitigation:* Evaluate both approaches early in the phase on a large-scale project (13k+ types). Set a clear bar: full scan <30s, incremental <1s, accuracy >85% of references captured. If neither approach meets all three, ship tree-sitter (fast + good enough) and document limitations.

**Risk: UnityEngine.dll type list maintenance.** Unity adds/removes types across versions.
*Mitigation:* The JSON is generated from a script that reads the actual assemblies. Ship a generation script alongside the JSON. Regenerate when targeting a new Unity version. The list changes slowly — major updates only.

**Risk: Parameter naming change breaks existing agent behavior.** Agents may have learned camelCase parameter names from prior sessions.
*Mitigation:* Accept both conventions transparently. Log a deprecation note in Charon traces when camelCase is used, so we can track adoption and eventually remove the fallback.

**Risk: Fuzzy search produces confusing results.** Auto-retry with broader matching may surface irrelevant hits.
*Mitigation:* Fuzzy results are clearly labeled as "similar matches" in the response. The agent can decide whether to use them. If noise is too high, make fuzzy opt-in rather than automatic.

### Implementation hints

- **MetaScanner:** The `.meta` file format is YAML with a `guid` field at the top and an importer class name (e.g., `TextureImporter`, `ModelImporter`). The importer class name directly tells you the asset type. A single scanner that maps importer class → node type handles all cases.
- **UnityEngine.dll types:** `TypeCache.GetTypesDerivedFrom<UnityEngine.Object>()` at editor time gives you the full list. Export once, ship as JSON. Alternatively, a Roslyn analysis of the Unity reference assemblies (in `<Unity>/Data/Managed/UnityEngine/`) produces the complete public API.
- **C# references (tree-sitter path):** The existing Node.js scanner already parses C# with regex. Tree-sitter's C# grammar (`tree-sitter-c-sharp`) provides a proper AST. The migration path: replace regex patterns with tree-sitter queries, add reference extraction queries for field types, base types, and constructor calls. The `worker_threads` parallelization from Phase 5c applies directly.
- **C# references (Roslyn path):** A standalone `dotnet tool` that opens the Unity project's `.csproj` files (generated by Unity), runs `SemanticModel.GetSymbolInfo()` on every identifier, and outputs a JSON of `(source_type, target_type, reference_kind)` tuples. The tool runs as a subprocess from `GraphBuilder`, same pattern as the Node.js scanner.
- **Parameter naming:** `MCPDispatcher.BindArguments()` already does parameter mapping by name. Adding a fallback lookup (snake_case → camelCase) is a small change in the binding logic.
- Architecture §2.9 lists the static analysis boundaries. Phase 9 closes some of them (unscanned asset types, inheritance resolution) but not all (DI, reflection, addressable-by-key remain dynamic).

### Tests added

**Automated:**
- MetaScanner: produces correct node type for each supported extension; GUID matches `.meta` file; no binary file access
- MetaScanner incremental: asset added/removed/moved → node created/deleted/updated
- UnityEngine.dll seeding: `MonoBehaviour`, `ScriptableObject`, `Component` nodes exist after init; `inherits_from` edges resolve
- C# reference graph: fixture with known type references → correct `type_references` edges produced
- C# reference incremental: modify one `.cs` file → only that file's outgoing references updated; other files' references unchanged
- `find_references_to` on a `.png` file: returns prefabs/materials that reference it
- `search_by_name` with `path_filter`: excludes `Packages/` results when filtering to `Assets/`
- Parameter naming: both `name_pattern` and `namePattern` accepted by `search_by_name`

**Performance benchmarks:**
- MetaScanner full scan on fixture with 5k+ asset files: completes in <10s
- C# reference full scan on fixture with 500+ types: completes in <30s
- C# reference incremental (single file change): completes in <1s
- `pending_edges` count after full build with MetaScanner: <100 (down from ~67k)

**Manual:**
- Run against a large-scale test project: verify `find_references_to` on a sprite returns real results; verify `find_references_to` on a script returns C# references; verify `pending_edges` is near-zero

### Happy Path scenarios

**Scenario 19: "Which prefabs use this sprite?"**

The developer asks about a commonly-used UI sprite. With MetaScanner, the graph now contains `Texture` nodes for every `.png`. `find_references_to` on `icStar_1x.png` returns all 3,586 prefabs that reference it (previously returned 0).

**Demonstrates:** MetaScanner resolves the dead-end edges gap.
**Pass criteria:** Results match a manual grep of GUID references in prefab YAML files.

**Scenario 20: "Where is this class used?"**

The developer asks about `ApplicationLogic.cs`. With the C# reference graph, `find_references_to` returns every class that holds a field of type `ApplicationLogic`, calls its methods, or inherits from it. Previously returned 0.

**Demonstrates:** C# reference graph transforms script queries.
**Pass criteria:** Results include at least the references visible in a manual code search. Agent no longer falls back to bash for this query.

**Scenario 21: "Search only in my project code"**

The developer searches for `%Manager%` with `path_filter: "Assets/"`. Results exclude the 200+ Manager classes from Unity packages and third-party libraries, returning only the project's own Manager implementations.

**Demonstrates:** Search filtering reduces noise.
**Pass criteria:** Zero `Packages/` results in the filtered output.

### Regression coverage

All Phase 0–8 tests must continue to pass. MetaScanner adds nodes that didn't previously exist — this may cause some snapshot-based tests to show higher node counts, which is expected and correct. C# reference edges are additive and don't change existing edge types.

### Phase 9 implementation notes

Issues and decisions from Phase 9 development, documented for future reference:

1. **Tree-sitter chosen over Roslyn for C# reference graph (AD-1).** Tree-sitter C# grammar (`tree-sitter-c-sharp@0.23.5`) provides fast AST-based parsing via the existing Node.js scanner architecture. Estimated 85-90% reference coverage. Known gaps: `using` aliases, extension methods, implicit conversions, `var` type inference. These are acceptable because the graph is supplementary to grep, not a replacement for Roslyn's semantic model.

2. **MetaScanner implemented as single unified scanner.** Rather than per-type scanners (TextureScanner, MeshScanner, etc.), a single `meta-scanner.js` module maps 34 file extensions to 16 Unity node types via an `EXTENSION_TO_TYPE` lookup. This is simpler and easier to extend. The scanner reads `.meta` files for GUID extraction, creates Asset nodes, and skips files without valid GUIDs.

3. **Unity builtin types seeded via runtime reflection, not precomputed JSON.** The plan specified shipping a static JSON file. The implementation uses `SeedBuiltinTypes()` which reflects on loaded Unity assemblies (`UnityEngine.*`, `UnityEditor.*`) at graph build time. Results are cached by Unity version in the `scanned_assets` table. This is more maintainable — automatically adapts to the user's Unity version without shipping version-specific JSON files.

4. **Jest VM module conflicts with tree-sitter native bindings.** When running all test suites in a single Jest process with `--experimental-vm-modules`, tree-sitter's native C++ addon corrupts across VM contexts. Lazy init, fresh-per-call parsers, `--maxWorkers=1`, and Jest `projects` config all failed. Fixed by splitting `npm test` into two separate Jest invocations — one excluding `integration.test.js`, one running only `integration.test.js`. The `createParser()` per-call pattern is also retained as defense-in-depth.

5. **Scanner version bumped from 2 to 3.** The tree-sitter parser produces different output than the regex parser (adds `codeReferences`, different AST structure). Bumping `scannerVersion` to 3 ensures all existing scanned assets are re-scanned on next build, picking up the new reference data.

6. **`code_references` edge type distinct from PrefabScanner's `references`.** C# cross-file type references use the `code_references` edge type in `pending_edges`, with `reference_kind` (field, parameter, constructor, cast, attribute, return_type, local_var, generic_arg) stored in the `target_namespace` column. This avoids conflating code-level references with asset-pointer references from prefabs/materials.

7. **snake_case rename was a clean break.** 33 parameters across 12 tool files were renamed from camelCase to snake_case. The plan suggested backwards-compatible dual-name support; the implementation chose a clean break instead. Rationale: agents learn parameter names from the tool schema on each session — no persistent muscle memory to break.

8. **81 Scanner tests pass (76 + 5).** Test suite split: 76 tests in the first Jest run (meta-scanner, ts-parser, db-writer, discovery, hasher, meta-resolver, meta-integration), 5 tests in the second run (integration tests that import the full pipeline). All green.

9. **`find_references_to` enhanced with ScriptType child traversal.** When `find_references_to` targets a Script node, it now also queries `code_references` edges targeting the Script's child `ScriptType` nodes. This means querying a `.cs` file returns both asset-pointer references (from prefabs) and code-level references (from other C# files). A `seenIds` HashSet prevents duplicate results.

10. **`SearchByNameAdvanced` added to GraphDatabase.** Supports `matchMode` (contains/exact/prefix), `typeFilter`, `pathPrefix`, and LIMIT 200. Uses `LIKE` patterns for contains/prefix modes and `=` for exact mode.

### Bridge to next phase

Phase 9 delivers a complete graph that covers all asset types and C# code-level references. Phase 10 handles the public release: repo split, CI, marketplace submission, version bump to v1.0.0.

---

## 11.5 Phase 9.5: Field-report hardening (v1.0 blockers)

### Strategic intent

Phase 9.5 exists because field testing of Hades 0.9.9 against a large production project (on the order of **~600k nodes / ~660k edges**, ~40-minute build) produced a field report that proves the headline capabilities of Phases 8 and 9 **do not function on a normally-launched (Finder/Dock) Unity Editor.**

This is corrective, not net-new. The same gaps were flagged in Phase 7 and routed to Phases 8 and 9:

- **Phase 8** treated the *symptom* of the scanner's `npm install` failing, but missed the *root cause*: `ProcessResolver.Run` never propagates `PATH` to child processes. Under Unity's GUI-minimal environment (Finder/Dock launch on macOS), npm's internal `#!/usr/bin/env node` lookup fails (`env: node: No such file or directory`), so the C# / meta scanner, the package scan, **and** the Charon dashboard all silently fail to install. The reason we never caught it: we launch Unity from a terminal, which leaks the full login-shell `PATH`.
- **Phase 9** built the tree-sitter C# reference graph and the MetaScanner, but neither runs on a real machine because of that same PATH bug — compounded by a second blocker: on **Node 25**, tree-sitter / better-sqlite3 native addons fail to compile because V8's bundled headers now require C++20 while the bindings default to C++17 (`CXXFLAGS="-std=c++20"` fixes it).

The net effect is that the v1.0 README markets capability ("Where do we use `PlayerController`?", "scenes, prefabs, **scripts**, and dependencies") that returns a confident, wrong `0` in the field. **This phase is the true v1.0 gate.** Field testing root-caused and fix-verified the repair end-to-end (reachable `node` + `CXXFLAGS=-std=c++20 npm install` → `ScriptType` nodes with paths, `Script` nodes, pending `code_references`, and `Texture` nodes all appeared as expected), so the path forward is known.

A secondary theme runs through the report and is the team's own standing principle: **operations that fail or partially complete must not report success or a confident empty result.** Several tools (`find_references_to`, `query_graph` `where`, `find_orphan_scripts`) silently mislead in the degraded state — this phase closes those.

The user also requested that, while addressing the post-rebuild WAL-checkpoint freeze, the phase **investigate general Graph performance** for speed insights (the ~40-minute build, the single giant scan transaction, checkpoint cadence, index usage, and the `trace_dependencies` wildcard-scan latency carried over from Phase 6).

### Done criteria

- [x] **PATH propagation (root cause):** `ProcessResolver.Run` injects the resolved tool directory **and** node's directory into the child process `PATH`, so every `ProcessResolver.Run` call site (graph scanner, package scan, Charon dashboard) resolves `node` under Unity's GUI-minimal PATH. Verified by launching Unity from Finder/Dock (not a terminal) and confirming a clean scanner install + C# nodes present. *(Code complete; field re-run user-side.)*
- [x] **Binary resolution hardened:** the PATH propagation removes the `env node` shebang failure at its source (node's dir is on the child PATH), so no separate `node <npm-cli.js>` invocation is needed. The resolver additionally probes common install locations (`/opt/homebrew/bin`, `/usr/local/bin`, `~/.nvm/versions/node/*/bin`, Volta/fnm) as a fallback and resolves via the user's actual `$SHELL` (POSIX shells) rather than a hardcoded `bash -lc`.
- [x] **Node 25 native build:** the scanner / dashboard `npm install` invocation forces `CXXFLAGS=-std=c++20` (CXX only — `CFLAGS` breaks tree-sitter's C sources) via `ProcessResolver.NativeBuildEnv`, so native addons compile out of the box on current Node releases. Build node and run node are the same binary (ABI / `NODE_MODULE_VERSION` pinning).
- [x] **No silent-wrong-answers when degraded:** when the build log records `DEGRADED: C# nodes missing` (persisted as `csharp_scan_status` metadata), `find_references_to` / `trace_dependencies` on a `.cs` target return an explicit "C# scanning unavailable" status instead of `reference_count: 0` / "Asset not found".
- [x] **`query_graph` `where` honored:** the `where` clause is wired into the executor for `name`/`path` exact filters; queries containing an unsupported `where` key are rejected rather than silently ignored; `returned_count` reflects the actual filtered/returned set, not the unfiltered table total.
- [x] **`find_orphan_scripts` made safe:** excludes Unity builtins (empty path) and non-removable package types (non-`Assets/` path); early-returns an "unavailable" status when the build is in `DEGRADED: C# nodes missing`, so it can never imply a builtin is "unused and safe to remove."
- [x] **WAL-checkpoint freeze fixed:** an explicit `PRAGMA wal_checkpoint(TRUNCATE)` (`GraphDatabase.Checkpoint()`) runs **under** the progress bar ("Finalizing (checkpointing database)…") before `ClearProgressBar()` in both `RebuildParallel` and `ScanPackages`; trailing metadata writes (`ClearCurrentOperation`) are ordered ahead of the checkpoint. *(Asset-scan transaction left as a single transaction by design — see implementation notes: the explicit final checkpoint resolves the freeze, and batching would trade rebuild atomicity for only a lower transient WAL peak.)*
- [x] **`traces.db` bounded:** a configurable size cap (`CharonMaxSizeMb`, default 500) trims oldest traces (`CharonDatabase.EnforceSizeLimit`) and reclaims space via `VACUUM` + `wal_checkpoint(TRUNCATE)` after a large prune; `Flush()` wrapped in a single transaction; the size check runs at startup (off the hot path), not in `TickFlush`. Spans cascade via existing `ON DELETE CASCADE`.
- [x] **Graph performance investigation:** findings recorded in the implementation notes below. Dominant build cost is the main-thread scene/prefab scan (field log: ≈0.5s per asset across a few thousand assets), i.e. Unity parse + per-asset work — not SQLite/checkpoint. Low-risk DB win applied: `InsertNode`/`InsertEdge` now read the new rowid via the direct `SQLite3.LastInsertRowid(handle)` C call instead of a throwaway `SELECT last_insert_rowid()` statement (~1.25M fewer statements per full build). Deeper hot-path / parse-side work logged for Phase 11.
- [x] **Reporting bugs fixed:** `get_project_summary.asset_coverage.indexed_types` is derived from actual node-type counts (no longer a fixed whitelist that returned `{}`), and a new `script_type_count` reports `ScriptType` nodes so `script_count: 0` (one node per `.cs` file) no longer reads as "no scripts" when builtin/scanned types exist. Long rebuilds now surface a structured `status: "busy"` (`reason: "rebuild_in_progress"`) to MCP clients instead of "No Unity instance found": a thread-safe `GraphBuilder.IsBusy` flag (mirrors `_status`) is read on the transport's background thread in `MCPServer.EnqueueAndWait`, which short-circuits with an immediate busy response rather than enqueuing a call that would stall behind the blocked main thread until the 30s timeout. `hades_rebuild_graph` no longer presents as a hard MCP timeout — it schedules the rebuild via `EditorApplication.delayCall` and returns `status: "rebuild_started"` immediately, so the response flushes before the main-thread block begins; clients poll `hades_status` to confirm completion.
- [x] **`.meta` console spam resolved (author-side):** the loose asset files shipped without `.meta` (`skills/` ×22, `commands/` ×6, `Documentation/` ×1, `scripts/` ×1, top-level `CODE_OF_CONDUCT.md` / `CONTRIBUTING.md` / `SECURITY.md`) have committed `.meta` files generated by the package author in local/embedded mode. (User-owned step — completed by the user.)
- [ ] **Field repro re-run:** on a real GUI-launched Unity, the report's reproduction calls (§7) pass — `find_references_to` on a `.cs` returns real C# references, content-asset nodes exist, `pending_edges` drops sharply, and no degraded warnings remain.

### Scope: what's in

- **`ProcessResolver` hardening** — PATH propagation to children, `node`-direct npm invocation, broadened binary discovery, `$SHELL`-aware resolution. Single fix benefits scanner, package scan, and dashboard.
- **Scanner / dashboard build robustness** — `CXXFLAGS=-std=c++20` in the install invocation; ABI pinning of build vs run node; preflight diagnostic when native compilation fails.
- **Degraded-state honesty** — explicit "C# scanning unavailable" statuses; `query_graph` `where` execution + correct `count`; `find_orphan_scripts` builtin exclusion + degraded gating.
- **Finalization / DB lifecycle** — explicit checkpoint under progress bar, batched asset-scan transaction, ordered trailing writes.
- **Charon retention** — `CharonMaxSizeMb` cap, space reclaim, transactional `Flush`, off-hot-path size checks, WAL truncation.
- **Graph performance investigation** — profiling the large-project build, transaction/checkpoint cadence, query-plan/index review, `trace_dependencies` wildcard pre-scan; apply low-risk wins, document the rest.
- **Reporting fixes** — `indexed_types`, `script_count`, busy/rebuilding status, rebuild-call timeout behavior.
- **`.meta` generation (author-side, user-owned)** for the loose asset files.

### Scope: what's out

- Roslyn deep mode / method call graphs (Phase 11).
- Closing the remaining static-analysis blind spots — `using` aliases, extension methods, implicit conversions, DI/reflection (Phase 11; tracked in Architecture §2.9).
- The remaining ~33% asset-edge backlog beyond what the repaired scanner resolves (re-measure post-fix; residual is Phase 11).
- A fully async/non-blocking rebuild (this phase improves the *finalization* freeze and surfaces a busy status; a background rebuild architecture is Phase 11).
- Vendoring prebuilt native binaries for all Node ABIs (consider in Phase 11 if compile-on-install proves fragile).

### Dependencies

- Phase 9 complete (the C# reference graph and MetaScanner exist as code — this phase makes them actually run in the field).
- A large, GUI-launched Unity project for validation (the field report's project, or an equivalent), since the bugs are invisible on small / terminal-launched setups.

### Risk assessment

**Risk: the PATH fix passes on the dev machine but fails on another environment.** The whole class of bug is environment-specific (Finder/Dock vs terminal, zsh vs bash, Homebrew vs nvm vs Volta).
*Mitigation:* validate specifically from Finder/Dock launch; broaden binary discovery beyond `bash -lc`; add a clear preflight diagnostic so a failure is self-explaining rather than silent.

**Risk: forcing `-std=c++20` breaks a different toolchain / older Node.** Older Node majors compiled fine at C++17.
*Mitigation:* C++20 is backward-compatible for these sources; CXX-only (never CFLAGS). If a regression appears, gate the flag by detected Node major.

**Risk: explicit `wal_checkpoint(TRUNCATE)` lengthens the visible finalize step.** Moving the stall under the progress bar trades a hidden freeze for a visible wait.
*Mitigation:* this is the intended UX trade — a labeled "flushing database…" beats a silent multi-minute freeze. Transaction batching keeps the per-checkpoint cost bounded.

**Risk: `traces.db` size-pruning / VACUUM is itself a heavy op that could stall.** VACUUM rewrites the whole file.
*Mitigation:* run off the hot path (init + low-frequency timer), prefer `auto_vacuum=INCREMENTAL` set at creation; reserve full `VACUUM` for one-time large prunes under an indicator.

**Risk: scope creep into a general perf rewrite.** "Investigate performance" can balloon.
*Mitigation:* time-box to investigation + low-risk wins; everything structural is logged for Phase 11. The phase gate is correctness in the field, not peak throughput.

### Implementation hints

- **`ProcessResolver.Run` (Editor/Core/ProcessResolver.cs):** before `Process.Start`, set `psi.EnvironmentVariables["PATH"]` to `{resolved tool dir}{sep}{node dir}{sep}{inherited PATH}`. Resolve node via the same `FindExecutable("node")` the resolver already uses. This single change covers `GraphBuilder.RunNodeScanner`, `GraphBuilder.ScanPackages`, and `CharonDashboard.EnsureDashboardBuilt` (~:197).
- **Build flag:** set `CXXFLAGS=-std=c++20` in the `ProcessStartInfo.EnvironmentVariables` for the `npm install` runs in `Scanner~` and `Dashboard~`. Do **not** set `CFLAGS`.
- **Degraded signal:** the build log already records `DEGRADED: C# nodes missing` / `Package C# nodes missing`. Surface that state into the graph metadata so MCP tools can branch on it; `find_references_to` / `trace_dependencies` read it and return an explicit status.
- **WAL finalize (GraphBuilder.cs ~:285 scan txn, ~:312 edge txn, ~:330-336 finally):** batch the asset scan (the older `RebuildGraph` path at ~:170-185 already batches by 50 — apply the same to the `RebuildParallel` path), run `wal_checkpoint(PASSIVE)` between batches, and a final `wal_checkpoint(TRUNCATE)` before `ClearProgressBar()`. Move `ClearCurrentOperation` (a `DELETE` that commits) ahead of the checkpoint.
- **Charon (CharonEmitter.cs:79-145, CharonInitializer.cs:32/:42, CharonDatabase.cs:54, HadesSettings.cs:59):** wrap `Flush()` in one transaction; add a size-based prune alongside the existing time-based `PruneOlderThan`; expose `CharonMaxSizeMb`; spans cascade via existing `ON DELETE CASCADE`.
- **Perf investigation:** start from the two transaction boundaries and the checkpoint cadence; use `EXPLAIN QUERY PLAN` on the hot tool queries; revisit the `trace_dependencies` wildcard `search_by_name('%')` pre-scan (Phase 6 note 4) that scanned 163k nodes for a 0ms traversal.
- **`.meta` (author-side):** reference the package in local/embedded mode (`"com.arcforge.hades": "file:../path/to/Hades"`), open in Unity once to generate `.meta` for the loose files, commit them. (Alternative considered and rejected for now: relocating `skills/`/`commands/`/`Documentation/`/`scripts/` into `~`-suffixed folders would hide them from Unity but also from anyone browsing the package as assets.)

### Tests added

**Automated:**
- `ProcessResolver`: child `PATH` includes resolved tool dir + node dir; given a minimal/`env -i`-style environment, a `node`-dependent command still resolves.
- Degraded-state tools: with C# nodes absent, `find_references_to` on a `.cs` returns the "unavailable" status, not `0`.
- `query_graph`: `where` filters the result set; `count` equals returned/filtered count; unsupported `where` is rejected rather than ignored.
- `find_orphan_scripts`: builtin/package types never appear; tool is gated under degraded builds.
- Charon retention: a `traces.db` seeded over `CharonMaxSizeMb` is trimmed and the file actually shrinks (page_count drops); `Flush` writes in a single transaction.

**Performance benchmarks / manual:**
- On a large GUI-launched project: clean scanner install from Finder/Dock launch; `find_references_to` on a `.cs` returns C# refs; content-asset nodes present; `pending_edges` drops sharply; post-rebuild finalize shows a labeled flush rather than a silent freeze.
- Build-time profile captured before/after batching + checkpoint changes.

### Happy Path scenarios

**Scenario 22.5a: It just works from the Dock**

A developer launches Unity the normal way (Finder/Dock, not a terminal) on a large project and installs Hades. The graph builds, the scanner installs cleanly on current Node, and asking *"Where do we use `BattleController`?"* returns the real C# references — not `0`.
**Demonstrates:** the root-cause PATH + build fixes make the marketed capability real in the field.
**Pass criteria:** no terminal-launch workaround, no manual `npm install`; results match a `grep -rl` ground truth.

**Scenario 22.5b: Honest when blind**

On a machine where C# scanning genuinely could not run, the agent asks `find_references_to` on a script and receives an explicit "C# scanning unavailable" status, not a confident `0`. The agent falls back to grep and says so.
**Demonstrates:** the degraded state is surfaced, never silently wrong.
**Pass criteria:** no tool returns a confident empty/zero result while the build log says `DEGRADED`.

**Scenario 22.5c: No more mystery freeze**

After a rebuild on a large project, instead of a multi-minute silent freeze, the editor shows "Finalizing — flushing database…" and returns promptly; `traces.db` stays under its cap across sessions.
**Demonstrates:** the WAL-finalize + Charon retention fixes.
**Pass criteria:** the post-rebuild stall is labeled and bounded; `traces.db` does not grow without limit.

### Regression coverage

All Phase 0–9 tests must continue to pass. The degraded-state and `query_graph` `where` changes alter tool *outputs* in failure / filtered cases — update any snapshot tests that previously encoded the silent-`0` or unfiltered-`count` behavior, since those encoded bugs.

### Phase 9.5 implementation notes

**Keystone fix — `ProcessResolver` PATH propagation.** The root cause of the
field report's two biggest blockers was that `ProcessResolver.Run` started child
processes with `UseShellExecute=false` but never set the child `PATH`. Under a
Finder/Dock-launched Unity (GUI-minimal `PATH`), npm's `#!/usr/bin/env node`
shebang then failed with `env: node: No such file or directory`. Terminal-launched
Unity masked the bug by inheriting a login `PATH` — which is why Phases 8/9
"fixes" never worked in the field. `ApplyChildPath` now prepends the resolved
tool dir + node's dir + common install dirs ahead of the inherited `PATH`. One
change fixes the graph scanner, package scan, and Charon dashboard (all route
through `ProcessResolver.Run`). The Node 25 C++20 build fix piggybacks on the same
call via `ProcessResolver.NativeBuildEnv` (`CXXFLAGS=-std=c++20`).

**Degraded-state signal.** The build records C# scan success/failure into
`graph_metadata` as `csharp_scan_status` (`ok`/`degraded`). `GraphQueryTools`
reads it at query time so `.cs` queries return an explicit "unavailable" status
instead of a confident `0` — the distinction the report called for.

**WAL finalize freeze.** Confirmed exactly as the field report diagnosed (§8): with
WAL + `synchronous=NORMAL` and no explicit checkpoint, the deferred WAL→DB flush
landed on the next write *after* `ClearProgressBar()` — `ClearCurrentOperation`'s
`DELETE`, or the next MCP read — as a silent multi-minute stall. Fix: reorder so
`ClearCurrentOperation` runs first, then an explicit `wal_checkpoint(TRUNCATE)`
**under** a "Finalizing (checkpointing database)…" bar, then clear the bar
(`RebuildParallel` and `ScanPackages`).

**Decision — asset-scan transaction left un-batched.** The field report also suggested
batching the single giant asset-scan transaction (`RebuildParallel`) and running
`wal_checkpoint(PASSIVE)` between batches. Evaluated and deferred: the explicit
final `TRUNCATE` checkpoint already moves the freeze under visible progress, and a
single transaction preserves rebuild atomicity (all-or-nothing) with one fsync at
commit (cheap under WAL+NORMAL). Batching would only lower the *transient* WAL
peak during the scan — not a reported problem — at the cost of atomicity and extra
checkpoint passes. Logged for Phase 11 if peak WAL ever becomes an issue.

**`traces.db` bounding.** Implemented per the field report's §8.1 spec:
`CharonMaxSizeMb` (default 500), `EnforceSizeLimit` trims oldest traces (spans
cascade) to ~90% of budget in one pass then `VACUUM` + `wal_checkpoint(TRUNCATE)`
to actually reclaim disk, run at startup off the hot path. `CharonEmitter.Flush`
is now a single transaction instead of dozens of per-trace commits.

**Performance investigation (the "boost our speed" ask).** Build time is **not**
SQLite-bound. The field log shows the main-thread scene/prefab scan alone
dominated the build (**≈0.5 s/asset across a few thousand assets**) —
overwhelmingly Unity's YAML/prefab parsing and per-asset `AssetDatabase` work
inside `ScanAsset`, not the DB writes. The one clear, low-risk DB win was applied: `InsertNode`/`InsertEdge`
read the new rowid via the direct `SQLite3.LastInsertRowid(_connection.Handle)` C
call instead of preparing+stepping+finalizing a throwaway `SELECT
last_insert_rowid()` for every row (~1.25M fewer statements per full build).
A fuller prepared-statement *reuse* rewrite of the insert path was deliberately
**not** done: the vendored `SQLitePreparedStatement` binds strings with
`SQLITE_STATIC` (no copy), which is sound for read paths that bind-then-step once
but risky for ~1.25M bulk writes, and the expected gain is marginal on a
parse-bound workload.

A second, larger scanner-side win was then applied to the scene/prefab path
itself. Both `SceneScanner` and `PrefabScanner` previously resolved a component's
backing MonoScript GUID with `AssetDatabase.FindAssets("t:MonoScript <name>") +
LoadAssetAtPath + GetClass()` for **every non-builtin component instance** —
potentially 100k+ asset-database searches across a large project even though the
`Type → script-GUID` map is constant for a rebuild. They also re-resolved the
same referenced-asset GUID once per reference. A shared per-rebuild memo
(`ScanResolver`, cleared at each rebuild start and reset on domain reload)
collapses both to once per distinct `Type` / referenced object. The serialized-
reference resolution was kept behaviour-preserving on purpose: rather than swap to
`TryGetGUIDAndLocalFileIdentifier` (which would have started emitting edges for
`Packages/` and built-in resources, changing output), the cache retains the prior
`GetAssetPath` + `Assets/`-only filter and simply stops recomputing it per
reference. This attacks the redundant per-component/per-reference `AssetDatabase`
cost; the irreducible remainder is Unity's own scene/prefab deserialization
(`OpenScene` / `LoadPrefabContents`), which can only be bypassed by parsing the
`.unity` / `.prefab` YAML directly — a higher-risk rewrite deferred to Phase 11.
Deeper build-speed work (profiling the parse side, an async / non-blocking
rebuild, direct YAML parsing) is Phase 11.

### Bridge to next phase

Phase 9.5 makes Phases 8 and 9 real on a normally-launched Editor and removes the confident-wrong-answer class of bug. With the field blockers closed, Phase 10 (public release) can proceed on a product whose headline claims hold up on a clean, GUI-launched install.

---

## 11.6 Phase 9.6: Post-9.5 field-verification fixes

### Strategic intent

After Phase 9.5 shipped, a post-fix field verification on a large production project (order of **~700k nodes / ~1M edges**) confirmed every 9.5 repair holds in the field: the C# reference layer populates, content-asset types are present, the post-rebuild WAL freeze is gone at idle, `traces.db` stays capped, `query_graph` `where` filters, and `find_orphan_scripts` is honest. Graph coverage rose from roughly two-thirds to ~94%.

The same verification surfaced a set of follow-on defects — every one triaged against the current code and confirmed reproducible. This phase commits to fixing **all verified problems**, organized into four workstreams: **(A)** build-write threading, **(B)** busy-mechanism correctness, **(C)** graph data correctness, and **(D)** small tool-output bugs. Two reported items that did **not** reproduce, and the larger architecture bets, are explicitly out of scope and recorded under open questions.

**Workstream A — incremental-update cost + WAL.** Under sustained write churn the Editor hard-hangs, but profiling the incremental path (`GraphBuilder.UpdateAssets`, triggered by Unity's asset post-processor) shows the cost is **O(graph) per write**, not the SQLite commit: every incremental rebuilds the full session node-map (`SELECT` of all ~700k guid nodes) and re-scans the entire `pending_edges` table, on top of Unity's own main-thread asset deserialization. A background writer thread would not touch any of those, so the fix is to make incrementals **O(changed)** — drop the full session-map rebuild in favour of targeted `FindNodeByGuid` lookups for the changed set, and scope pending-edge resolution to the affected source assets — and to **bound WAL** growth (`wal_autocheckpoint` / `journal_size_limit`, retaining the 9.5 finalize `TRUNCATE`). A background single-writer connection is held in reserve (Workstream A4) and added only if, after these fixes, measurement still shows the commit itself blocking — otherwise it stays a deferred architecture question (DB ownership, below).

**Workstream B — busy-mechanism correctness.** The 9.5 busy short-circuit (`MCPServer.EnqueueAndWait`) gates on `GraphBuilder.IsBusy`, which is true for *any* non-Idle status — including the fast transient incremental `Updating` state. After Workstream A made incrementals O(changed) (sub-frame), gating them returns a spurious "busy" and opens an at-least-once retry window: a write lands while idle, its asset-import flips `IsBusy` via `Updating`, and a client retry sees "busy" although the edit already applied. Fix: gate only on a genuine **long, main-thread-blocking op** (`Rebuilding` / `ScanningPackages`) via a distinct `IsInLongOperation` flag, never on transient `Updating`. This closes both defects with a one-flag change — reads and writes flow freely during fast incrementals, and a "busy" is only ever returned when the op genuinely could not be served.

Note on read/write classification: the original draft proposed classifying every tool read-vs-write so reads could run *during* a full rebuild. But all tools execute on the **main thread** through a single shared SQLite connection, and a full rebuild blocks that thread synchronously — so no tool, read or write, can be served mid-rebuild without a second background read connection (the deferred **A4** work). Until A4 exists, classification has no runtime effect (reads and writes are treated identically: both flow during incrementals, both get an honest "busy" during a true rebuild). Per YAGNI it is deferred to A4 rather than added as speculative scaffolding.

**Workstream C — graph data correctness.** Three issues understate or misreport the graph: (1) `Script` nodes are keyed by absolute filesystem path while every other asset uses project-relative `Assets/...`, so `find_references_to` / `trace_dependencies` return 0 for a script addressed by its documented relative path; (2) tens of thousands of pending `code_references` target names match no node — .NET BCL types, attributes, generic/backtick-arity names (from builtin-type seeding), and precompiled-DLL types — and the schema has no terminal `external` state, so they read as permanently unresolved and depress the coverage metric; the asset extension→type map is also missing entries (`.spriteatlasv2`, `.bmp`); (3) some edges are never emitted at all — nested-prefab `m_SourcePrefab` links (only prefab *variants* are walked) and serialized references nested below the top level — so the coverage percentage overstates completeness.

**Workstream D — small tool-output bugs.** `get_recently_changed` has no result cap (can return very large outputs right after a rebuild); `get_project_summary` mixes tier scopes (`script_count` spans all tiers, `script_type_count` is project-only).

Honest scoping: none of this shrinks the ~40-minute full-rebuild wall-clock — that cost is Unity's own scene/prefab deserialization (irreducible without parsing `.unity`/`.prefab` YAML directly), kept on the Phase 11 track.

### Done criteria

**Workstream A — incremental-update cost + WAL**
- [x] **Incrementals are O(changed), not O(graph):** `UpdateAssets` no longer rebuilds the full session node-map (the `SELECT` of all ~700k guid nodes) on every write, and no longer re-scans the entire `pending_edges` table. The changed set is resolved by targeted `FindNodeByGuid` lookups; pending-edge resolution is scoped to the affected source assets.
- [x] **No hard-hang under sustained write churn:** a burst of rapid write tools (repeated component/material/tag edits) keeps the Editor responsive; per-write work scales with the number of changed assets, not graph size.
- [x] **WAL bounded:** the write-ahead log cannot grow without bound between full checkpoints — via a configured `wal_autocheckpoint` / `journal_size_limit`. The 9.5 explicit `wal_checkpoint(TRUNCATE)` at rebuild finalize is retained.
- [x] **No regression to read latency:** MCP read/query timings stay within the 9.5 envelope; the O(changed) path does not add per-read cost.
- [ ] **(A4, conditional) Background writer held in reserve:** only if, after the O(changed) + WAL fixes, measurement still shows the SQLite commit itself blocking the main thread, a single dedicated background writer connection is added (serialized work queue, single-writer semantics, flush/close on `beforeAssemblyReload` / `quitting`). Otherwise this stays a deferred architecture question (DB ownership, below).

**Workstream B — busy-mechanism correctness**
- [x] **No busy during fast incrementals:** the transient `Updating` state never produces a "busy" response. Reads and writes both flow during sub-frame O(changed) incrementals. The gate keys off a distinct `IsInLongOperation` flag (`Rebuilding` / `ScanningPackages` only), not `IsBusy`.
- [x] **Busy means not-applied:** a `status: "busy"` response is only returned when the request was genuinely **not** applied (the main thread is blocked by a real long op). A write tool can never return "busy" after its mutation already landed.
- [x] **No double-apply window:** a client that retries on `busy` cannot apply a non-idempotent write twice (no "busy" is returned for an op that already applied during `Updating`).
- [ ] **(Deferred to A4) Reads served mid-rebuild + tool classification:** serving reads *during* a full rebuild requires the background read connection (A4). The read-vs-write tool classification is added together with A4, when it first has a runtime effect — not before.

**Workstream C — graph data correctness**
- [x] **Script path lookups work:** `find_references_to` / `trace_dependencies` resolve a `.cs` target given its project-relative path (`Assets/...`), not only its absolute path. Achieved by normalizing the path at the tool boundary and/or indexing the project-relative path for `Script` nodes. A relative-path query for a script returns its real references, not `0`.
- [x] **Pending edges reflect reality:** terminal external references (.NET BCL types, attributes, precompiled-DLL types, backtick-generics) are tallied as `external` and excluded from the unresolved count (count-only — no fabricated nodes, no schema change); backtick-arity names are normalized in builtin-type seeding; the asset extension→type map covers `.spriteatlasv2` and `.bmp`. The reported coverage metric counts only genuinely-unresolved edges.
- [x] **Previously-invisible edges are emitted:** nested-prefab `m_SourcePrefab` links are extracted as edges. (The recursive serialized-reference walk is deferred until it has a focused test harness — see open questions.)

**Workstream D — tool-output bugs**
- [x] **`get_recently_changed` is bounded:** a default result cap (with the limit surfaced in the response) prevents multi-megabyte outputs after a rebuild.
- [x] **`get_project_summary` scopes are consistent:** counts either share a tier scope or each label its scope explicitly, so `script_count` and `script_type_count` are no longer silently incomparable.

### Scope: what's in

- **(A)** Make `UpdateAssets` **O(changed)** — drop the full session-map rebuild and full pending-edge re-scan in favour of targeted lookups scoped to the changed assets; **WAL bounding** (`wal_autocheckpoint` / `journal_size_limit`, retaining the finalize `TRUNCATE`). A background single-writer (A4) only if commit-bound blocking remains after measurement.
- **(B)** Busy gate narrowed from `IsBusy` (any non-Idle) to a distinct `IsInLongOperation` flag (`Rebuilding` / `ScanningPackages` only), so the fast transient `Updating` never returns "busy". (Read-vs-write tool classification deferred to A4, where it first has a runtime effect.)
- **(C)** Script-node **path normalization** (relativize at write in the Node scanner + tolerant query); **pending-edge reclassification** — *count-only*: known-external targets (BCL/attribute/precompiled-DLL/backtick-generic) are tallied separately and excluded from the unresolved coverage count, **no new node rows / no schema change**; backtick-arity normalization in builtin seeding; extension-map additions (`.spriteatlasv2`, `.bmp`); **never-extracted edges** — *conservative*: nested-prefab `m_SourcePrefab` edges this phase, the recursive serialized-reference walk deferred until it has a focused test harness.
- **(D)** `get_recently_changed` cap; `get_project_summary` tier-scope alignment.

### Scope: what's out (→ open questions)

- Making the Node scanner the sole DB writer, and a persistent off-Editor sidecar — **not** adopted (rationale below).
- Direct YAML asset parsing to cut the ~40-minute full-rebuild wall-clock — Phase 11.
- The two reported-but-not-reproduced tool bugs (`analyze_render_pipeline`, `query_graph` `edges` filter) — re-test, do not fix blind.

### Dependencies

- Phase 9.5 complete (the freeze/retention fixes Workstream A builds on).
- A large, GUI-launched project to reproduce the churn-hang and to re-measure pending-edge / coverage numbers after a fresh rebuild.

### Risk assessment

**Risk: the O(changed) refactor drops nodes/edges the full rebuild used to catch.** Replacing the full session-map rebuild with targeted lookups could miss a node that the old full scan happened to repair.
*Mitigation:* scope the targeted set conservatively from the changed-asset GUIDs (the same set the debouncer already tracks); keep the full rebuild path intact for explicit `hades_rebuild_graph`; regression-test that an incremental produces the same nodes/edges for a changed asset as a full rebuild.

**Risk: WAL bounding via autocheckpoint adds mid-write checkpoint stalls.** A `wal_autocheckpoint` that fires during a burst could itself introduce a hitch.
*Mitigation:* size the autocheckpoint / `journal_size_limit` generously relative to per-write page counts; keep the explicit `TRUNCATE` only at rebuild finalize (not per-write); measure WAL growth under a churn burst.

**Risk: (A4) threading a SQLite connection introduces corruption / contention** — *only if A4 is taken.* Moving writes off-thread demands strict ownership.
*Mitigation:* defer A4 unless measurement proves the commit blocks; if taken, a single dedicated writer thread + serialized queue (no shared connection across threads), flush/close on reload/quit, queued items re-derivable from asset hashes.

**Risk: narrowing the gate lets a request enqueue during a long op and time out** instead of getting a fast "busy."
*Mitigation:* `IsInLongOperation` covers exactly the two states that block the main thread (`Rebuilding` / `ScanningPackages`); the transient `Updating` is now sub-frame, so a request that enqueues during it drains within a frame, well under the transport timeout. (Read/write classification and its drift risk move to A4, when classification is actually introduced.)

**Risk: path normalization breaks existing relative-path callers** or double-counts a script under two keys.
*Mitigation:* normalize at the boundary (don't duplicate node rows); regression-test both absolute and relative inputs return the same node.

**Risk: classifying targets as terminal `external` hides genuinely-missing project edges** (a real unresolved edge mislabeled external).
*Mitigation:* only classify external when the name resolves to a known BCL/attribute/DLL type set; everything else stays pending; re-measure against ground truth.

**Risk: scope balloons across four workstreams.**
*Mitigation:* land in dependency order — **A → B** first (the stability/regression core, since B depends on A), then **C**, then **D** — each independently shippable and testable.

### Implementation hints

- **(A)** Hot path: `GraphBuilder.UpdateAssets` (asset-postprocessor path). It currently calls `BuildSessionMapFromExistingNodes()` (full `SELECT` of all guid nodes) and `ResolvePendingEdges()` (full `pending_edges` scan) on every incremental — both O(graph). Replace the session-map rebuild with per-changed-GUID `GraphDatabase.FindNodeByGuid` lookups; scope pending-edge resolution to the changed source assets (use `DeletePendingEdgesBySourceAsset` + re-resolve only those, instead of re-scanning the whole table). WAL bound via `PRAGMA journal_size_limit` and/or `PRAGMA wal_autocheckpoint` in `GraphDatabase.ApplyPragmas`, retaining the 9.5 finalize `wal_checkpoint(TRUNCATE)`. **A4 only if needed:** `BlockingCollection<T>` queue drained by one long-lived writer thread, drain/close on `AssemblyReloadEvents.beforeAssemblyReload` / `EditorApplication.quitting`.
- **(B)** `MCPServer.EnqueueAndWait` shorts on `GraphBuilder.IsBusy` (true for any non-Idle status). Add a distinct `GraphBuilder.IsInLongOperation` volatile, set in the `_status` setter to `Rebuilding || ScanningPackages`, and gate on that instead. `IsBusy` is left intact for its other consumers; the transient `Updating` no longer triggers a busy response. (When A4 lands, also classify tools and let reads bypass to the background read connection.)
- **(C)** *Path keys:* the Node scanner writes `Script.path = filePath` (absolute) in `Scanner~/src/ts-parser.js`; normalize to project-relative at write time, or normalize the query argument in `find_references_to` / `trace_dependencies` (GraphQueryTools `n.Path == arg`). *Pending edges:* `GraphBuilder.ResolvePendingEdges` only logs permanent/transient counts — add a terminal `external` resolution and persist it; strip backtick arity where builtin seeding sets `type.BaseType.Name` / `iface.Name`; extend the extension map in `Scanner~/src/meta-scanner.js`. *Never-extracted:* read `m_SourcePrefab` in `PrefabScanner` (it currently handles only variants via `GetCorrespondingObjectFromOriginalSource`); recurse `ScanSerializedReferences` past the top level in `SceneScanner` / `ScriptableObjectScanner` / `PrefabScanner`.
- **(D)** Add a `LIMIT` / default bound to `GraphDatabase.GetRecentlyChanged`; align the tier filters in `get_project_summary` (`GraphQueryTools` ~:41/:46) or annotate each count's scope.

### Tests added

- **(A)** An incremental update for a changed asset produces the same nodes/edges as a full rebuild of that asset (O(changed) parity); per-write cost does not scale with total graph size (no full session-map rebuild / no full pending-edge scan on the incremental path); sustained writes do not grow the WAL past its configured limit. *(A4, if taken:* concurrent reads during a background write return consistent data; a simulated reload mid-queue closes the writer cleanly.)*
- **(B)** A request during transient `Updating` does **not** receive a `busy` response (it enqueues and applies); a request during `Rebuilding` / `ScanningPackages` receives an immediate `busy` and provably did not apply; retrying a non-idempotent write that returned `busy` applies it exactly once. (Tool-classification and reads-served-mid-rebuild tests arrive with A4.)
- **(C)** `find_references_to` / `trace_dependencies` return identical results for a script's absolute and project-relative paths; a BCL/attribute/DLL target resolves to `external` and drops out of the pending count; a project with a nested prefab and a nested serialized reference emits those edges; the coverage metric matches the emitted-edge ground truth.
- **(D)** `get_recently_changed` output is capped; `get_project_summary` counts are scope-consistent (or carry explicit scope labels).

### Happy Path scenarios

**Scenario 9.6a: Responsive under churn**

An agent makes a rapid series of edits (materials, tags, components) on a large project. The Editor stays responsive; the graph catches up in the background instead of freezing.
**Pass criteria:** no multi-second UI hangs during a write burst; the graph reflects all edits once the queue drains.

**Scenario 9.6b: Incrementals never blocked, busy is honest**

While the graph is catching up on a burst of edits (transient `Updating`), the agent runs `query_graph`, `search_by_name`, and a `material_swap_shader` — all succeed; none receive a spurious "busy." During a genuine full rebuild, a `material_swap_shader` returns `status: "busy"`, the material is provably unchanged, and a retry applies it exactly once.
**Pass criteria:** no "busy" during fast incrementals; a busy write never half-applies; no double-apply on retry. (Serving reads *during* a full rebuild is an A4 follow-on, not in 9.6.)

**Scenario 9.6c: Scripts answer to their documented path**

The agent asks *"Where do we use `Assets/Scripts/BattleController.cs`?"* using the project-relative path and gets the real C# references.
**Pass criteria:** a relative-path script query returns references, not `0`; coverage no longer counts BCL/attribute/DLL references as unresolved.

### Regression coverage

All Phase 0–9.5 tests must continue to pass. Workstream B changes tool *gating* behavior (reads previously returned `busy` mid-rebuild) and Workstream C changes *outputs* (script path resolution, pending-edge counts, coverage metric) — update any snapshot tests that encoded the old gated-read or unresolved-`external` behavior, since those encoded the bugs.

### Open questions (deferred, not adopted)

- **A4 — background read connection (+ tool classification).** Serving MCP reads *during* a full rebuild requires a second, read-only SQLite connection owned by a background thread (WAL's one-writer/many-reader guarantee). Only then does a read-vs-write tool classification have any runtime effect, so the two ship together. Deferred until measurement shows reads-during-full-rebuild matters in practice — full rebuilds are now rare (first boot, explicit rebuild, package changes) and incrementals are never gated, so the value is narrow. Workstream B already delivers the high-frequency win (no spurious busy on fast incrementals) without it.
- **Single-writer ownership.** The graph is currently written by two processes — the C# Editor and the Node scanner. Consolidating to one writer is a sound invariant, but this phase changes only *threading*, not ownership; which side should own the write is left open.
- **Node as sole writer / persistent off-Editor sidecar.** Evaluated and **not** adopted on performance grounds: the dominant build costs are Unity deserialization (stays in C#) and main-thread blocking (fixed by Workstream A), neither of which DB ownership addresses; routing the ~80 in-process read tools through IPC would tax reads to fix a problem already solved more cheaply. Revisit only if off-Editor persistence (surviving domain reloads, headless operation) becomes an explicit product goal.
- **Direct YAML asset parsing.** The real lever against the ~40-minute full-rebuild wall-clock is parsing `.unity` / `.prefab` files directly (as the scanner already does for `.cs`), bypassing `OpenScene` / `LoadPrefabContents`. This is the Phase 11 build-speed play, sized separately.
- **Reported but not reproduced.** `analyze_render_pipeline` reporting Built-in incorrectly, and the `query_graph` `edges` filter being a no-op, were both verified as **working** in the current code (`GraphicsSettings.defaultRenderPipeline` is read correctly; the `edges` filter is applied). Likely fixed since the reported build — re-test on a fresh rebuild before assuming a defect.

### Bridge to next phase

Phase 9.6 closes every verified follow-on defect from the field verification — threading/WAL stability, busy-mechanism correctness, the graph data-correctness gaps, and the small tool-output bugs — without committing to an architecture change. With the verified defects closed, Phase 10 (public release) proceeds on a graph whose read path is honest under load and whose coverage metric reflects reality. The deferred architecture questions feed Phase 11 (build-speed via direct YAML parsing, single-writer consolidation).

---

## 12. Phase 10: Public release (v1.0)

### Strategic intent

Phase 10 handles the production release mechanics. By this point, Hades has been externally tested (Phase 7), first-run bugs are fixed (Phase 8), and graph coverage is comprehensive (Phase 9). What remains is the distribution infrastructure: splitting the repository, setting up CI, submitting to the Anthropic marketplace, and validating the install experience from a clean machine.

After Phase 10, Hades is:
- A Unity Package at `TheArcForge/Hades` installable via UPM git URL
- A Claude Code plugin at `TheArcForge/hades-plugin` installable via `/plugin install` (marketplace) or `claude --plugin-dir <path>` (local)
- Listed on the Anthropic plugin marketplace for discoverability
- v1.0.0 tagged and announced

### Done criteria

- [ ] `TheArcForge/hades-plugin` repository created with plugin-relevant subset (per Plugin doc §5.3)
- [ ] Sync script wired to CI: auto-sync plugin repo on release tags
- [ ] CI on main repo: Bridge + Scanner tests on push/PR
- [ ] CI on main repo: auto-sync plugin repo on release publish
- [ ] CI on plugin repo: validate plugin structure (manifest, skills count, commands count, Bridge dist)
- [ ] Full install tested from scratch on a clean machine (no prior Hades state)
- [ ] Anthropic marketplace submission completed via `platform.claude.com/plugins/submit`
- [ ] Marketplace compliance checklist passes (Plugin doc §5.2 — all items verified)
- [ ] Version fields set to 1.0.0 across both repos
- [ ] CHANGELOG.md covers all phases 0–10
- [ ] At least one external developer (not the author) has installed and used Hades successfully without workarounds
- [ ] GitHub release created with release notes on both repos

### Scope: what's in

**Plugin repository:**
- Create `TheArcForge/hades-plugin` with plugin-relevant subset: `.claude-plugin/`, `.mcp.json`, `skills/`, `commands/`, `Bridge~/` (dist only), `Scanner~/` (source, no tests)
- Sync script (`scripts/sync-plugin.sh`) wired to CI

**CI workflows:**
- Main repo: Bridge + Scanner test runs on push/PR
- Main repo: release-triggered sync to plugin repo (tag, push, release)
- Plugin repo: structural validation (plugin.json, .mcp.json, skill count, command count, Bridge dist presence)

**Version and release:**
- Bump all version fields to 1.0.0
- CHANGELOG.md covering all phases
- Git tag v1.0.0 on both repos
- GitHub releases on both repos

**Marketplace:**
- Walk Plugin doc §5.2 compliance checklist
- Submit to `platform.claude.com/plugins/submit`
- Document submission date and status

**Clean-machine validation:**
- Install from scratch on environment with no prior Hades state
- Follow README instructions exactly
- Verify first-use scenario ("Tell me about this project")
- Test plugin-only install (without Unity Package)

### Scope: what's out

- New features (Phase 11)
- Enterprise features
- Asset Store distribution (Phase 11 candidate)

### Dependencies

- Phase 9 complete (graph coverage comprehensive, install path reliable)

### Risk assessment

**Risk: Marketplace rejection.** Anthropic's review may flag issues not anticipated.
*Mitigation:* Hades is fully functional without a marketplace listing. Submit early, iterate on feedback. Don't block release on approval.

**Risk: Clean-machine install reveals hidden dependencies.** Something works on the dev machine but not on a fresh install.
*Mitigation:* Phase 8 specifically addressed first-run reliability. Clean-machine test is the final validation.

**Risk: Plugin repo sync drift.** The plugin repo gets out of sync with the main repo after manual changes.
*Mitigation:* Automated sync on release tags via CI. Plugin repo CI validates structure on every push. No manual edits to the plugin repo.

### Happy Path scenarios

**Scenario 22: v1.0 clean install**

A developer who has never seen Hades follows the getting-started guide from a cold start on a clean machine. They complete the full install and first-use flow without encountering any of the four bugs from Phase 7 testing. Graph includes all asset types and C# references. Agent uses Hades tools by default.

**Demonstrates:** the full product is ready for public release.
**Pass criteria:** zero manual workarounds required. Developer reaches a working state by following documentation alone.

### Regression coverage

All Phase 0–9 tests must continue to pass.

### Bridge to next phase

Phase 10 declares Hades publicly released at v1.0. Phase 11 is post-launch evolution.

---

## 12.5 Phase 10.1: Graph relationship & coverage correctness (post-1.0 field report)

**Status:** Code-complete, validated; pending release tag.
**Trigger:** Two v0.9.9 field reports from an Addressables-heavy production project — one on addressable group-membership references, one a 240-sample trustworthiness audit of the C# relationship layer. Code was treated as ground truth; every finding was confirmed against source before any change.

### Strategic intent

Restore C# graph coverage that had regressed, fix the C#-relationship and prefab-query inaccuracies that made "who references X / is X used" unreliable, and — where a gap is inherent (precompiled DLL types, runtime dispatch) — make it *honest* (visible degraded/coverage signals) instead of silently wrong.

### Scope (four waves)

- **Wave A — coverage restoration.** The tree-sitter parser now emits `ScriptType` nodes for enums, records, and nested types (a Phase-9 parser swap had silently dropped them); the package-tier scan was made non-destructive on failure (scan-then-reconcile instead of wipe-first) with a `package_scan_status` flag and a longer package-tier timeout.
- **Wave B — relationship correctness.** The parser captures previously-missed reference forms (`using`-aliases, generic method-invocation type args, property and generic-return type args); base-list supertypes are now classified as `inherits_from` vs `implements` by the *resolved* target type's kind (a new `kind` node property) rather than by base-list position — fixing missed first-party interfaces.
- **Wave C — query-layer correctness.** `trace_dependencies` no longer pads results with the file's own methods; `find_references_to` no longer over-counts via structural/transitive edges and no longer merges co-located sibling types; `find_prefabs_with_component` walks the full containment chain (finds deeply-nested hosts) and de-dups variant-inherited components; `SceneScanner` emits a scene→prefab `instantiates` edge (previously an under-count).
- **Wave D — Addressables + honesty.** An addressable group→member edge so a group surfaces as a referrer; honest signals on the relationship tools (`static_analysis_coverage`, `package_scan` degraded, `supertypes_external_unresolved`).

### Follow-ups (found during validation)

- **`find_references_to` `nested_by` bucket** — excluding structural edges to kill the over-count also hid the *direct* nesting parent, which would report a false "unused" for a nested prefab. Direct structural parents (nesting prefabs, prefab variants) now surface in a separate `nested_by` array, keeping `reference_count` clean.
- **Inference NRE** — `PatternInferenceEngine` threw a swallowed `NullReferenceException` on every rebuild after the first (an inferred pattern's `TargetFile` was never round-tripped through frontmatter, then dereferenced in conflict detection); guarded, and the catch now logs the full exception.
- **Test-isolation** — EditMode tests constructed `GraphDatabase` with temp DBs that hijacked and then nulled the process-wide singleton, leaving the live graph unqueryable after any test run. All graph-using tests now save/restore the singleton around each test.

### Validation

`Scanner~` JS tests green; Unity EditMode suite green. Live MCP validation against a sandbox confirmed the behavior of every wave end-to-end (deep-nested component discovery, variant de-dup, honesty signals, `nested_by`, URP detection, and — notably — the graph surviving a full test run). Items needing a richer project to exercise at scale (scene→prefab instantiation, the Addressables group edge, at-scale relationship reliability) are deferred to validation on a real Addressables project.

### Inherent limits (documented, not "fixed")

Precompiled DLL types cannot be source-scanned into nodes; the honest signals make that visible rather than closing it. Static analysis does not see reflection, runtime/string dispatch, or DI-resolved wiring — surfaced via the coverage caveat.

---

## 13. Phase 11: Long-tail and post-launch

### Strategic intent

Phase 11 is open-ended. Unlike phases 0-10 which had defined scope, Phase 11 evolves based on what actually happens after Hades is in real users' hands. The roadmap cannot predict which features matter most until adoption signals tell us.

This chapter lists candidate directions rather than prescribing them. Whether and when each is pursued depends on usage data, contributor interest, ecosystem evolution, and product feedback.

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
- Cross-project graph queries for monorepo scenarios

**Distribution:**
- Asset Store as supplementary channel
- Documentation site as standalone web property

**Advanced graph features:**
- Roslyn deep mode for method call graphs (`calls` edges between `ScriptMethod` nodes)
- Semantic similarity over graph (if useful in practice)
- Cross-project boundary scanning for monorepo sibling C# projects

**Skills ecosystem:**
- Community skill contributions
- Skill marketplace beyond Hades's own (third-party contributions)
- Specialized skills for sub-domains (mobile, console, VR)
- Recipe skills (health, inventory, save, spawn) deferred from Phase 4

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

### Phase 11 has no Done criteria

This phase doesn't end. It continues as long as Hades is maintained.

---

## 14. Cross-phase concerns

### 14.1 Test infrastructure summary

By the end of Phase 9, the test suite includes:

- **C# unit tests** (NUnit): hundreds of tests covering scanners (including MetaScanner), graph operations, validation engine, MCP infrastructure
- **Node.js unit tests** (Vitest/Jest): tests covering bridge process, dashboard, MCP protocol handling, C# scanner
- **Integration tests** (Unity Test Runner): scanner correctness on fixtures, end-to-end MCP calls, multi-instance behavior
- **Charon-based regression**: traces of happy paths replayed for deterministic Hades-side parts
- **Performance benchmarks**: graph build time, query latency, incremental update time at scale
- **Manual happy path scenarios**: 22 scenarios across phases, manually run after each phase

CI runs the full automated suite on every commit. Manual scenarios are part of phase completion gates.

### 14.2 Cumulative regression principle

Every phase adds tests. No phase removes tests. Test fixtures, once stable, are frozen. This grows the suite over time but ensures regressions cannot occur silently.

When a Phase N change breaks a Phase M test (M < N), this is a bug in Phase N, not "expected behavior change." Investigate and fix before continuing.

### 14.3 Documentation cadence

Documentation is built incrementally:

- Phase 0: README setup instructions
- Phase 1: tool reference, basic usage
- Phase 2: dashboard guide
- Phase 3: memory authoring guide
- Phase 4: skills overview
- Phase 5: implementation notes for each sub-phase
- Phase 6: README rewrite for external users, troubleshooting guide, architecture doc refresh
- Phase 7: getting-started guide, plugin README/CLAUDE.md
- Phase 8: troubleshooting updates (quarantine, npm install, launcher race, pending edges)
- Phase 9: coverage documentation (which asset types indexed), parameter naming migration notes
- Phase 10: CHANGELOG, release notes, plugin repo README

Documentation is not an afterthought; it accumulates throughout the journey.

### 14.4 Versioning across phases

Hades version progresses with each phase:

- Phase 0 complete: v0.1.0 ✅
- Phase 1 complete: v0.2.0 ✅
- Phase 2 complete: v0.3.0 ✅
- Phase 3 complete: v0.4.0 ✅
- Phase 4 complete: v0.5.0 ✅
- Phase 5 complete (5a/5b/5c): v0.6.0 ✅
- Phase 6 complete (polish, beta-ready): v0.9.0 ✅
- Phase 7 complete (friends-and-family): v0.9.0 ✅ *(same version — no new features, only distribution prep)*
- Phase 8 complete (first-run reliability): v0.9.1 ✅
- Phase 9 complete (graph coverage): v0.9.5 ✅
- Phase 10 in progress (public release): v0.9.9
- Phase 10 complete (public release): **v1.0.0**
- Post-1.0 maintenance round "Update 1" — graph ownership model + incremental integrity, startup & connection reliability, and a felt-performance pass: **v1.1.0**
- Phase 11: v1.x and v2.x as evolution dictates

Versions 0.1.0–0.6.0 are internal milestones. v0.9.0–0.9.9 are beta releases incorporating field feedback. v1.0.0 is the first publicly announced release; v1.1.0 is the first post-release correctness-and-performance round (see `CHANGELOG.md` for the full entry). Both `package.json` (UPM) and `plugin.json` (Claude Code) track these tags in lockstep.

### 14.5 Anthropic plugin marketplace submission

Marketplace submission is part of Phase 10 (v1.0). The full marketplace strategy, compliance checklist, and standalone plugin repo considerations are documented in the **Plugin document** (`Documentation/arcforge-hades-plugin.md`, §5). That document is the authoritative source.

**Summary:** Hades is fully functional without a marketplace listing. The marketplace adds discoverability only. Current plugin structure passes all known marketplace compliance requirements (no fixed ports, no orphan processes, no config file manipulation).

### 14.6 The product after Phase 10

After Phase 10, Hades is:

- A Unity Package at `TheArcForge/Hades` distributable via UPM git URL
- A Claude Code plugin at `TheArcForge/hades-plugin` installable via `/plugin install` (marketplace) or `claude --plugin-dir <path>` (local), discoverable through the Anthropic plugin marketplace
- A coherent product with three integrated layers (Graph, Charon, Asphodel) plus 22 Skills and 6 slash commands
- 90 MCP tools (22 native + 68 migrated editor-action tools), with comprehensive graph coverage across all Unity asset types and C# code-level references
- Documented for users with setup guide, troubleshooting guide, and architecture docs
- Battle-tested on real Unity projects through dogfooding and large-scale field validation
- Open source under MIT license
- Free of dependencies on Anthropic auth that could be revoked

It is ready to be used by developers who are not the original developer.

**Update 1 (v1.1.0)** hardened this foundation: the graph's incremental-update path got an ownership model (`owner_guid`) that ends a class of silent node/edge corruption; Editor startup became a single ordered bootstrap (`HadesBootstrap`) that keeps the MCP server reachable across domain reloads; the hub/launcher connectivity was made resilient (forgiving path-matching, time-based hub liveness, spawn-lock); the dead Charon→Asphodel inference loop was repaired; and a felt-performance pass took the per-save editor freeze and the full-table query scans off the hot path. See `CHANGELOG.md` §[1.1.0]. (One residual is tracked: a backgrounded, App-Napped editor can still delay the post-reload bootstrap — `wake-unity.sh` is the recovery.)

---

## 15. Known issues

Issues discovered during development that need resolution. Tracked here for visibility across phases.

### MCP server entry lost during compilation failures

**Discovered:** Phase 5a development (2026-05-13), supersedes the Phase 3 "multi-instance discovery" hypothesis
**Severity:** High — makes MCP server invisible to Claude Code until manual intervention or successful recompile
**Status:** Resolved by MCP Hub architecture
**Ref:** `Editor/MCP/MCPServer.cs`, `Editor/Core/MCPClientConfig.cs`

The actual problem: `MCPServer.Stop()` calls `MCPClientConfig.OnServerStop()` which deletes the server entry file. During domain reload, if compilation fails, `MCPServer`'s `[InitializeOnLoad]` static constructor never fires to recreate the entry. The bridge polls indefinitely, finding nothing — even though the HTTP listener may still be alive on a background thread.

**Resolution:** The MCP Hub architecture (`Documentation/arcforge-hades-plugin.md`, §3) eliminates file-based discovery entirely. Unity registers with the Hub via HTTP. The Hub's heartbeat monitor probes Unity's HTTP endpoint directly before marking an instance stale, keeping the connection alive even during compilation failures. See also the MCP Hub design spec (`docs/superpowers/specs/2026-05-13-mcp-hub-design.md`).

### MCP connection lost while editor backgrounded / idle (napped main thread)

**Discovered:** 2026-06-02, during connection-resilience work
**Severity:** Medium — surfaces as "Server hades unavailable" / "No Unity instance found" while the Unity Editor is still running, requiring manual recovery
**Status:** Resolved (background-timer heartbeat + eviction recovery); reload/cold-bootstrap boundary covered by a recovery script
**Ref:** `Editor/MCP/MCPServer.cs`, `Editor/MCP/AppNapGuard.cs`, `Scripts/wake-unity.sh`

The original heartbeat rode `EditorApplication.update`, which runs on Unity's main thread. When a backgrounded macOS editor's main thread is napped or stalled (App Nap / background-timer coalescing), the heartbeat stops firing, the Hub's TTL expires, and the instance is evicted — even though the HTTP listener (on background threadpool threads) is still alive and able to serve requests.

**Resolution (two layers):**
- **Steady-state idle** — the heartbeat now runs on a dedicated background `System.Threading.Timer` that survives a napped main thread, with cached project values so it never touches a main-thread-only Unity API. If a heartbeat finds the Hub up but no longer tracking this instance, it re-registers automatically; it also detects a restarted Hub (new PID/port) and re-registers. A refcounted macOS App Nap opt-out (`AppNapGuard`) is held while a request is in flight as cheap extra insurance. *Verified by a 5-minute hidden-editor soak: the Hub reported `healthy` throughout and `lastHeartbeat` advanced every 30s with no eviction.*
- **Reload / cold-bootstrap boundary** — the one gap the background timer cannot close is the moment just after a domain reload, when the fresh server must wait for a single `EditorApplication.delayCall` tick to bootstrap. A napped backgrounded editor starves that tick. The `Scripts/wake-unity.sh` helper recovers this by briefly bringing Unity to the foreground (un-napping the main thread so it re-registers) and then restoring the user's previous app focus. Documented in the Troubleshooting guide ("Recovering a stalled MCP connection").

### MCP config scoped to Unity project, not package source

**Discovered:** Phase 5a development (2026-05-13)
**Severity:** Medium — blocks development workflow when Claude Code is started from the package source directory
**Status:** Resolved by MCP Hub architecture
**Ref:** `Editor/Core/MCPClientConfig.cs:WriteClaudeCodeConfig()`

`MCPClientConfig.WriteClaudeCodeConfig()` writes `.mcp.json` to `PathSandbox.ProjectRoot` (the Unity project directory). Claude Code sessions started from the package source directory never see the config.

**Resolution:** The MCP Hub architecture makes connectivity directory-independent. The plugin's `.mcp.json` is discovered by Claude Code's plugin system (not by working directory). The Hub routes tool calls to the correct Unity instance via project path matching, which includes matching the CWD as a child of a registered project or via `manifest.json` `file:` references. See **Plugin document** §3.5.

**Note:** `WriteProjectMcpJson()` was deliberately reintroduced alongside the Hub architecture. It writes `.mcp.json` to the Unity project root pointing to the stable Hub launcher copy at `HadesPaths.LauncherDir` — always the project-relative `.arcforge/hades-hub/launcher.js`, independent of hub scope. The original scoping issue is resolved because the Hub provides directory-independent routing regardless of where Claude Code is launched. The project-level `.mcp.json` serves a complementary purpose: it enables Claude Code auto-discovery when launched directly from the Unity project directory, without relying on the plugin system finding the config first.

### Validation warnings duplicate on repeated runs

**Discovered:** Phase 3 happy path validation (2026-05-12)
**Severity:** Low — cosmetic, does not affect validation correctness
**Status:** Resolved (Phase 6)
**Ref:** `Editor/Asphodel/MemoryValidator.cs`

Fixed by `ClearOldWarnings()` — a static method that strips all existing `<!-- HADES VALIDATION WARNING -->` blocks via regex before writing new ones. Called at the start of each validation pass, making warning writes idempotent. Each validation run produces a clean, non-duplicated set of warnings. Also correctly handles the case where a previously failing rule now passes (the stale warning gets removed). Test `Validate_RepeatedFailure_DoesNotDuplicateWarnings` verifies the idempotency property.

### MCP config not discovered when Claude Code launched from repo root

**Discovered:** Phase 5a development (2026-05-13)
**Severity:** Medium — MCP server invisible to Claude Code until user relaunches from correct directory
**Status:** Resolved by MCP Hub architecture
**Ref:** `Editor/Core/MCPClientConfig.cs:WriteClaudeCodeConfig()`

When a Unity project lives in a subdirectory of the git repo (e.g., `MyRepo/MyUnityProject/`), `.mcp.json` written to the Unity project directory is not found by Claude Code launched from the repo root.

**Resolution:** The fix is now dual-path. (1) Hub parent match strategy: the Hub matches the CWD as a parent of a registered project path, so Claude Code launched from the repo root finds the correct Unity instance via the Hub. (2) `MCPClientConfig.WriteProjectMcpJson()` writes `.mcp.json` to the Unity project root pointing at the Hub launcher, giving Claude Code a project-local config to discover directly when launched from that directory. Together these cover the full range of CWD scenarios. See **Plugin document** §3.5.

### Claude Desktop cannot reach a project-local hub

**Discovered:** Project-local installation verification (2026-08-03)
**Severity:** Low — Claude Code is unaffected; Claude Desktop has a documented working configuration
**Status:** Open — mitigated by defaulting `desktop_integration` to off; fix designed, not implemented
**Ref:** `Editor/Core/MCPClientConfig.cs:UpdateClaudeDesktopConfig()`, `Bridge~/launcher/src/hub-dir.ts:resolveHubDir()`

The launcher finds the hub from its **working directory**, not from where its own file sits. Claude Code satisfies that by construction — it discovers `.mcp.json` in the directory it was started from and spawns the server there — but Claude Desktop spawns MCP servers from a directory outside any Unity project. `findProjectRoot` walks up and finds no `ProjectSettings/ProjectVersion.txt`, returns `null`, and `resolveHubDir` falls through to rung 3, `$HOME/.arcforge/hades-hub`. Meanwhile a Unity in the default `hub_scope: local` publishes `hub.json` into `<projectRoot>/.arcforge/hades-hub`. The two never rendezvous: Desktop's launcher finds no `hub.json`, spawns an orphan hub in the global directory, and Unity — which reads `hub.json` from its own scope — never joins it.

**Current mitigation:** `desktop_integration` defaults to **off**, so nothing misleading is written. The working Claude Desktop configuration is `hub_scope: global` + `skills_scope: global` (the latter because `~/.claude/skills` is the only skills location Desktop reads) + Desktop integration on. Project Settings → Hades warns when Desktop integration is enabled against a local hub.

**Designed fix:** have `UpdateClaudeDesktopConfig` write the resolved hub directory into the Desktop entry as an environment variable:

```json
{
  "mcpServers": {
    "hades": {
      "command": "node",
      "args": ["/Users/you/Projects/YourGame/.arcforge/hades-hub/launcher.js"],
      "env": { "HADES_HUB_DIR": "/Users/you/Projects/YourGame/.arcforge/hades-hub" }
    }
  }
}
```

`HADES_HUB_DIR` is rung 1 of the resolution chain, ahead of any cwd-derived inference, so this pins Desktop to exactly the hub Unity publishes to and makes Desktop work under the default local scope — at which point `desktop_integration` could reasonably default back to on.

**Known remaining edge, to resolve before doing it:** the hub directory is pinned but the project identity is not. `PROJECT_PATH` still degrades to `process.cwd()` when no project root is found, so the `X-Hades-Project` header is wrong and routing leans on the hub's single-instance fallback. That is fine for one Unity attached to a project-scoped hub — which is exactly the local-scope case — but the launcher would want a companion `HADES_PROJECT_PATH` (or an argv) to be correct in general. Since a *global* hub can have several Unity instances attached, the env-var fix must not be applied to global scope without also fixing the header.

### Field bugs (Phase 7 feedback)

**Status:** Resolved — bugs 1–3 shipped in v0.9.1 (Phase 8); bug 4 shipped in v0.9.5 (Phase 9)

Four reproducible bugs discovered during the first external smoke-test on a large-scale production project:

1. **macOS quarantine blocks native dylib** — `com.apple.quarantine` on zip distribution blocked `libgilzoide-sqlite-net.dylib`. Workaround: `xattr -dr com.apple.quarantine`. Fixed in v0.9.1 (Phase 8).
2. **Scanner npm install silently fails** — first-boot graph was missing all C# nodes (38% smaller). Exit code 3 was overloaded. Workaround: manual `cd Scanner~ && npm install`. Fixed in v0.9.1 (Phase 8).
3. **Launcher startup race** — MCP reported "failed" on every cold start; Reconnect succeeded. stdin was not consumed until after Hub bootstrap. Fixed in v0.9.1 (Phase 8).
4. **pending_edges misleading log** — "Resolved 80/67504" when 99.88% were expected-unresolvable (unscanned asset types). Fixed in v0.9.5 (Phase 9): log updated and MetaScanner added to cover previously unscanned asset types.


---

## 16. Closing

This roadmap is a sequence of phases, each building on the last, each producing something coherent on its own, accumulating into a complete product that realizes the Vision.

The roadmap has evolved through contact with reality. Phases 0–6 built the components. Phase 7 validated in the field and revealed that the install path needed hardening (Phase 8) and the graph needed broader coverage (Phase 9) before public release (Phase 10). This is the process working as intended — field feedback reshaping priorities before they calcify.

The roadmap is honest about what's hard. Phase 1 was the highest-risk: the Graph thesis validated. Phase 9 is the next critical bet: whether C# code-level reference indexing can be made both fast and accurate enough to transform `find_references_to` from a prefab-only tool into the universal dependency query. Phase 8 is unglamorous but essential — first impressions determine adoption.

What this document does not commit to:

- Specific dates or durations
- Specific allocation of effort between phases
- Specific staffing assumptions

What this document does commit to:

- The order of phases (validated by dependencies and real-world feedback)
- The Done criteria of each phase
- The Happy Path scenarios that validate each phase
- The TDD-first principle throughout
- The cumulative regression principle throughout

Execute with discipline against this plan, adjusting as reality teaches. The Vision tells us where we're going. The Architecture tells us how the pieces fit. The Roadmap tells us what to do next.

That is sufficient.

---

*End of Roadmap document.*
