# Hades — Architecture Document

**Version:** 1.0
**Status:** Pre-development technical foundation
**Last updated:** 2026-05-09
**Companion to:** Vision document, Roadmap document

---

## 0. About this document

This document is the technical foundation of Hades. It translates the Vision into concrete architecture: how each component is built, how components communicate, what data structures and protocols mediate them, what failure modes exist and how they are handled, and what the end-to-end behavior looks like across realistic scenarios.

This document is written for engineers who will build, extend, or debug Hades. The intended reader is technically fluent in Unity, Node.js, and modern AI tooling, but is not assumed to have read the source code yet. Where decisions reference UniClaude's existing infrastructure (which is reused substantially), the relevant pieces are described in enough detail to be understood without reading UniClaude's repository first.

The tone is engineering-honest. Where a design choice has known trade-offs, those are named. Where edge cases are anticipated, they are described. Where the design is speculative or pending validation, that is marked. This document avoids the optimism bias that architecture documents often slide into.

Where the document goes deep: data models, lifecycle behavior, communication protocols, failure modes, integration pipelines. Where it stays brief: high-level rationale (covered in Vision), specific timelines (covered in Roadmap), specific code listings (those belong in source).

The document is organized as a hybrid of layer-by-layer detail and cross-cutting integration. Each major component (Graph, Charon, Asphodel, Skills) gets its own deep chapter. A separate integration chapter follows, explaining how the components compose into the unified system. Twelve concrete pipelines walk through end-to-end behavior. Operational concerns and failure modes get their own chapters. Open architectural questions are explicitly listed at the end.

---

## 1. System overview

### 1.1 The composition

Hades is composed of five runtime processes and one passive artifact set:

1. **The Unity Editor process**, augmented by the Hades Unity Package. The Unity Package adds C# code that runs inside the editor and is responsible for: building and maintaining the project knowledge graph; emitting observability events; handling memory file I/O; and serving as the in-process MCP server that exposes Hades capabilities. The MCP server registers with the Hub on startup.

2. **The MCP Hub process** (Node.js). A long-running HTTP server, one per machine, shared across all Claude Code sessions and Unity instances. Maintains a registry of connected Unity instances, routes tool calls by matching the Claude Code session's working directory to the correct Unity project, and monitors instance health via heartbeats. Auto-exits after 60 seconds of no connected launchers or Unity instances. Source lives in `Bridge~/hub/`. Zero npm runtime dependencies.

3. **The MCP Launcher process** (Node.js). A thin stdio process spawned by Claude Code as declared in `.mcp.json`. Ensures the Hub is running (spawns it if not), registers as a connected session, and bridges stdio ↔ HTTP. One per Claude Code session; exits when the session ends. Source lives in `Bridge~/launcher/`. Zero npm runtime dependencies.

4. **The agent client process**. This is Claude Code, Cursor, Cline, Continue, or another MCP-compatible coding agent. Hades does not provide this process; it consumes whichever agent the user already runs. The agent client connects to Hades through the Launcher's stdio interface.

5. **The Charon dashboard process** (Node.js). A small web server that reads the trace database and renders a local web UI. Started and stopped on demand by the user via Unity menu. Optional; without it, traces still accumulate but are not human-inspectable.

6. **The artifact set**. A collection of files within the Unity project's directory that persist Hades state across sessions. These include the graph database (`.arcforge/graph.db`), the trace database (`.arcforge/traces.db`), and the memory directory (`.arcforge/memory/`). Some are gitignored by default (graph and traces, since they are machine-specific or potentially noisy), others are git-tracked (Tier 1 memory, since it is project knowledge meant to travel with the project).

There is no "ArcForge backend." Hades is local-first by design. There are no servers Anthropic or ArcForge operate on behalf of the user. There is no telemetry transmitted to a vendor. The architecture is entirely client-side. Node.js is a runtime dependency for MCP connectivity (Hub and Launcher).

### 1.2 The system as a layered model

Conceptually, the system stacks like this from bottom to top:

```
┌─────────────────────────────────────────────────────────────┐
│                    User and Unity project                   │
│         (the developer, their codebase, their git)          │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│            Hades Unity Package (in Unity Editor)            │
│  ┌────────────────────────────────────────────────────────┐ │
│  │ Scanners → build & update Graph                        │ │
│  │ Memory File I/O → read & write Asphodel                │ │
│  │ Charon Emitter → write trace events                    │ │
│  │ MCP Server (HTTP on localhost) → registers with Hub    │ │
│  └────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
              │ HTTP (register, heartbeat, tool responses)
              ▼
┌─────────────────────────────────────────────────────────────┐
│            MCP Hub (Node.js, long-running, one per machine) │
│  Registry of Unity instances ← routes by project path       │
│  Heartbeat monitoring ← buffers during domain reloads       │
└─────────────────────────────────────────────────────────────┘
              │ HTTP (tool calls, tools/list)
              ▼
┌─────────────────────────────────────────────────────────────┐
│      MCP Launcher (Node.js, stdio, one per CC session)      │
│         Starts Hub if needed ← bridges stdio ↔ HTTP         │
└─────────────────────────────────────────────────────────────┘
              │ stdio (JSON-RPC)
              ▼
┌─────────────────────────────────────────────────────────────┐
│             Agent Client (Claude Code, etc.)                │
│         Loads Hades plugin: skills + MCP config             │
│       Calls Hades tools as part of agent reasoning          │
└─────────────────────────────────────────────────────────────┘

                    ────── separately ──────

┌─────────────────────────────────────────────────────────────┐
│       Charon Dashboard (Node.js, on demand, per project)    │
│   Reads trace database → renders local web UI on localhost  │
└─────────────────────────────────────────────────────────────┘
```

The Unity Package is the heart of the system. It owns the data, the introspection, and the integration with the Unity Editor's lifecycle. The Hub is the stable routing layer that makes connectivity resilient across Unity restarts, domain reloads, and directory changes. The Launcher is the ephemeral bridge that Claude Code spawns. The agent client is the consumer of capabilities. The Charon dashboard is a side-channel viewer for accumulated trace data.

The full MCP Hub architecture is documented in the **Plugin document** (`Documentation/arcforge-hades-plugin.md`, §3).

The data flow is asymmetric. Most reads come from the agent client into the Unity Package via MCP. Most writes happen inside the Unity Package as it reacts to project changes. Memory is the exception: the agent can propose memory updates, but those are written through the Unity Package after explicit acceptance.

### 1.3 The four logical components

Within the Unity Package and its supporting infrastructure, there are four logical components that map to the Vision's four pillars:

**Hades Graph** — the project knowledge graph. Built and maintained by C# scanners running inside the editor; persisted to a SQLite database at `.arcforge/graph.db`; queried by MCP tools that translate agent intent into SQL.

**Hades Charon** — observability. Trace events emitted from every MCP tool call, every graph build, every memory operation. Persisted to a SQLite database at `.arcforge/traces.db`. Visualizable via the Charon dashboard.

**Hades Asphodel** — memory. Markdown files at `.arcforge/memory/` providing both Tier 1 (explicit, human-curated, git-tracked) and Tier 2 (inferred, auto-generated, also git-tracked alongside Tier 1). Read by MCP tools that inject relevant memory into agent context.

**Hades Skills** — distributed via the Claude Code plugin. Not technically part of the Unity Package; lives in the agent client's plugin directory. But integrated with the other three layers: skills query Graph state and Asphodel context to give project-specific guidance.

These four components share infrastructure (the MCP server, the editor lifecycle hooks, the Charon emitter) and each contributes its own specialized data and tools. The next four chapters detail each component independently. Chapter 6 covers their integration.

### 1.4 Reuse from UniClaude

A substantial portion of Hades's runtime infrastructure is reused from UniClaude. Specifically:

- **`MCPServer.cs`** — the in-process HTTP server inside the Unity Editor, now started from the ordered `HadesBootstrap` composition root (§1.7) rather than its own independent `[InitializeOnLoad]`. Battle-tested in UniClaude and survives Unity's lifecycle quirks (domain reload, assembly reload, play mode transitions).
- **`HttpTransport`** — the HTTP/SSE transport layer on localhost.
- **`MCPDispatcher`** — reflection-based discovery of methods decorated with `[MCPTool]`, parameter mapping via `[MCPToolParam]`, response wrapping via `MCPToolResult`.
- **Main Thread Bridge** — `ConcurrentQueue<WorkItem>` that funnels HTTP requests onto Unity's main thread, drained by `EditorApplication.update`. Required because Unity APIs are not thread-safe.
- **Domain Reload Resilience** — server state (port, PID) persisted in `SessionState` so it survives Unity's assembly reloads. `IDomainReloadStrategy` with `EditorApplication.LockReloadAssemblies()` to prevent reloads mid-tool-execution.
- **Path Sandboxing** — `PathSandbox.cs` ensures all file operations happen within the project root (`.arcforge/` is simply a subdirectory of it). Write operations to `.git/` are additionally blocked regardless of path. No accidental writes outside.
- **Tool primitives** — 68 of UniClaude's MCP tools have been ported to Hades. These provide direct editor actions: scene manipulation, component management, prefab operations, material editing, animation controller authoring, asset management, and more.

What changes from UniClaude:

- **No Node.js Sidecar (but an MCP Hub).** UniClaude had a separate Node.js process running the Anthropic Agent SDK, which called the MCP server's `/rpc` endpoint via custom JSON-RPC. Hades does not embed the Agent SDK and therefore does not need a sidecar. The agent client is external (Claude Code or Claude Desktop), and it speaks the standard MCP protocol. Hades uses a three-component connectivity model — Launcher (thin stdio process) → Hub (long-running HTTP router) → Unity Instance(s) — described in the **Plugin document** §3. The Hub and Launcher have zero npm runtime dependencies (Node.js built-ins only). **Node.js is a runtime dependency for MCP connectivity** (both Claude Code and Claude Desktop route through the Hub).
- **No chat UI.** UniClaude exposed a chat window in the Unity Editor as the user's primary interaction surface. Hades has no chat UI; the user interacts through their agent client.
- **MCP-compliant transport.** UniClaude used custom JSON-RPC over HTTP. Hades uses the MCP protocol's official transport (HTTP/JSON-RPC-over-POST). The `MCPServer.cs` infrastructure is upgraded to expose MCP-compliant endpoints, but the underlying threading model and lifecycle handling stay identical.

The reuse is significant. We estimate approximately 60% of Hades's runtime infrastructure code is direct reuse from UniClaude with small adaptations, primarily around the MCP transport layer.

### 1.5 The communication backbone

Communication between the agent client and the Unity Package happens over **HTTP on localhost**, routed through the **MCP Hub** — a long-running Node.js process that acts as the stable connection point between Claude Code and Unity.

The full MCP Hub architecture — launcher, hub, registration, heartbeat, project-path routing, lifecycle scenarios — is documented in the **Plugin document** (`Documentation/arcforge-hades-plugin.md`, §3) and the **MCP Hub design spec** (`docs/superpowers/specs/2026-05-13-mcp-hub-design.md`). Those documents are authoritative for connectivity detail.

**Summary of the connection model:**

```
Claude Code ←(stdio)→ Launcher ←(HTTP)→ Hub ←(HTTP)→ Unity Instance(s)
```

- The Unity Package's MCP server listens on `http://127.0.0.1:<port>` and registers with the Hub.
- The Hub maintains a registry of connected Unity instances and routes tool calls by matching the Claude Code session's working directory to the correct instance. Path matching is forgiving — it canonicalizes both sides (`realpath` + case-fold) and, when exactly one Unity is open, falls back to routing an otherwise-unidentifiable call (e.g. a launcher whose cwd resolved to `/`) to that single instance.
- The Launcher is a thin stdio process declared in the plugin's `.mcp.json`. Claude Code spawns it automatically. It resolves the real Unity project root by walking up from its cwd, ensures the Hub is running (acquiring an exclusive `O_EXCL` spawn lock so racing launchers cannot start duplicate/zombie hubs), and bridges stdio to HTTP.
- Standard MCP protocol messages flow over the connection: `initialize`, `tools/list`, `tools/call`, etc. The `initialize` response includes an `instructions` field containing agent guidance text (which tools to prefer, how to interpret results, behavioral notes). Both Claude Code and Claude Desktop read this field and use it to guide tool selection behavior for the session.

**Why HTTP on localhost:**

- HTTP is platform-portable — identical on Windows, macOS, and Linux.
- The MCP protocol specifies HTTP as a primary transport, ensuring compatibility with all MCP clients. The Unity-side server handles `POST /rpc` (JSON-RPC) and `GET /sse`; the Hub forwards requests as `POST` to `/rpc` — no SSE streaming is used in practice.
- HTTP infrastructure (request handling, error codes, content negotiation) is mature and well-understood.
- The localhost-only constraint provides sufficient security for the threat model (same-machine, same-user processes).

Performance is sufficient. The overhead of HTTP on localhost is 1-2ms per request, negligible compared to agent reasoning time (hundreds of milliseconds to seconds).

The Hub architecture resolves three known MCP connectivity issues from earlier phases (server entry lost during compilation failures, `.mcp.json` scoped to wrong directory, `.mcp.json` not found from repo root) by making connectivity directory-independent and resilient to Unity lifecycle disruptions. Later hardening made the Hub's own lifecycle robust: it auto-exits when genuinely idle based on the **time since last launcher activity** (replacing a launcher *count* that leaked whenever a launcher was killed abruptly — an "immortal hub" that ran for days serving stale code), and forwarding a tool call to a Unity instance that has just begun a domain reload returns a clean, retryable JSON-RPC error instead of a raw `HTTP 500`.

### 1.6 The threading model

Unity APIs are not thread-safe. Most Unity calls (`AssetDatabase`, `SerializedObject`, `EditorApplication`, scene manipulation) must run on Unity's main thread. HTTP requests, however, arrive on background threads (the .NET HTTP listener runs on its own thread pool).

The bridge between these two worlds is the **Main Thread Bridge** pattern, inherited from UniClaude:

1. An HTTP request arrives on a background thread (let's call it `T_http`).
2. `T_http` parses the request, identifies the tool to call, and constructs a `WorkItem` with a `TaskCompletionSource<string>`.
3. `T_http` enqueues the `WorkItem` onto a `ConcurrentQueue<WorkItem>` and `await`s the `TaskCompletionSource`'s task. No thread blocks — the `async`/`await` machinery suspends `T_http` until the result is ready.
4. On every `EditorApplication.update` tick (called by Unity on the main thread), the queue is drained: each `WorkItem` is executed (Unity APIs are now safe to call), the result is set on the `TaskCompletionSource`, which resumes `T_http`.
5. `T_http` reads the result and writes the HTTP response.

This design has these properties:

- All Unity API calls happen on the main thread.
- HTTP threads can serve multiple concurrent requests (up to the .NET HTTP listener's pool size).
- Each request has a 30-second timeout. If a request takes longer (e.g., a graph rebuild on a large project), the HTTP thread returns a timeout error while the main thread continues processing. The result is discarded when it eventually arrives.
- Domain reloads are blocked for the duration of a **turn** (not just a single request) via `EditorApplication.LockReloadAssemblies()`. The lock is acquired on the first tool call of a turn and released when `OnTurnComplete()` fires or a 120-second safety timeout elapses (`AutoReloadStrategy.cs`). This prevents Unity from reloading assemblies mid-turn, which would corrupt state.
- **Busy fast-path**: when the graph is busy in a way that would block the main-thread queue, incoming requests are answered immediately from the background thread with a structured `"busy"` / `rebuild_in_progress` response — bypassing the main-thread queue entirely, so the client gets a clean `busy` rather than a 30-second timeout. The gate (`GraphBuilder.IsBusyForRequests`) covers a genuine long operation (full rebuild / package scan), the deferred startup sync, **and** the window of an off-thread incremental `.cs` scan (`_csScanInFlight`). The fast non-`.cs` incremental work is intentionally NOT gated — it finishes within a frame, and gating it would risk a spurious retry on an already-applied non-idempotent write.

**Off-thread incremental script scan.** One main-thread operation used to violate the "fast queue" assumption: an interactive `.cs` save ran the Node scanner synchronously (`Process.WaitForExit`), freezing the editor for the scan's duration. The interactive path now spawns that subprocess **non-blocking** (`ProcessResolver.Start`) and polls `HasExited` on `EditorApplication.update` (`GraphBuilder.PumpCsScan`); the rest of the incremental batch runs in a continuation once the subprocess exits. The main-thread queue keeps draining throughout, and `_csScanInFlight` busy-gates tool calls for the scan window. The startup catch-up scan stays synchronous (it already runs behind a progress bar and the busy gate).

The threading model is robust but adds latency. A request waits until the next `EditorApplication.update` tick before its `WorkItem` is processed. The wait is typically sub-millisecond at normal frame rates. *(Note: no specific worst-case tick guarantee applies — Unity's frame rate varies and the backgrounded editor case is described below.)*

**Backgrounded editor.** When the editor is hidden (macOS ⌘H) or otherwise backgrounded, the OS coalesces its timers and `EditorApplication.update` slows to roughly ~6/s instead of the normal frame rate. The drain loop still empties within a fraction of a second, so tool calls keep working while Unity is in the background — the HTTP listener itself runs on background threadpool threads and never depended on focus. Two things harden this further: (1) while a request is in flight the server holds a refcounted macOS App Nap opt-out (`NSProcessInfo beginActivityWithOptions`) so a deeper throttle can't stall the queue; and (2) the Hub heartbeat does **not** ride `EditorApplication.update` — it runs on a dedicated background `System.Threading.Timer` (see Plugin doc §3.4), so registration stays fresh even if the main thread is fully napped. The one case the background timer cannot cover is the moment just after a domain reload, when the new server must wait for a single main-thread tick to bootstrap; a napped backgrounded editor can starve that tick, which is what the `wake-unity.sh` recovery in the Troubleshooting guide addresses.

### 1.7 The lifecycle

A typical day in the life of Hades:

1. User opens the Unity Editor. A single `[InitializeOnLoad]` composition root, **`HadesBootstrap`**, runs Hades startup in one explicit order — Charon → GraphDb → Asphodel → MCP server → graph event hooks → package watcher — replacing eight independent `[InitializeOnLoad]` entry points whose undefined relative order caused real bugs (e.g. Asphodel reading Charon's not-yet-set database and leaving the inference engine null; see §4). Crucially the MCP server begins listening, registers with the Hub, and arms its background heartbeat **before** the (potentially blocking) graph startup sync, which is deferred to a later tick — so the server stays reachable even while a rebuild pins the main thread. Server startup also writes a `CLAUDE.md` to the Unity project root for Claude Code agent guidance (non-destructive: if `CLAUDE.md` already exists, Hades content is appended inside clearly marked fenced markers rather than overwriting the file).
2. On a later tick, the graph is brought up to date with any project changes that happened while Unity was closed. This can take seconds to a minute on a large project; while it runs, tool calls return a structured `busy` (the startup sync is covered by the busy gate) rather than timing out.
3. The Charon emitter starts logging events.
4. The user opens their agent client (Claude Code or Claude Desktop). For Claude Code, the plugin's `.mcp.json` declares the launcher; for Claude Desktop, Unity configured `claude_desktop_config.json` to point at the stable launcher copy.
5. The launcher starts the Hub (if not already running), registers as a connected session, and bridges stdio to HTTP. The Hub routes tool calls to the correct Unity instance by matching the session's working directory. If the agent client starts before Unity, the tools list is empty until Unity registers. See **Plugin document** §3 for the full connectivity architecture.
6. The user starts a session with the agent. The agent makes Hades tool calls as needed.
7. While the user works, Unity Editor lifecycle events (asset save, scene save, prefab apply) trigger graph updates. These happen in the background, fast enough that the user doesn't notice.
8. Trace events accumulate in `traces.db`. The user can inspect them at any time via the Charon dashboard.
9. When the user closes Unity, the MCP server shuts down. The graph and trace databases are flushed to disk. Everything resumes from where it left off when Unity opens again.

There are interruptions in this happy path: domain reloads (assembly reloads, script recompilation), play mode transitions, Unity crashes. The system is designed to survive each of these. Chapter 8 covers failure modes in detail.

### 1.8 Multi-instance behavior

Running multiple Unity Editor instances simultaneously, each on a different project, is a common workflow — not an edge case. A developer might have one Unity instance on a game project and another on a tooling project, working on both within the same hour. Hades must handle this cleanly.

The design property that makes this work: **Hades is fully project-scoped. Each Unity instance runs an independent Hades stack against its own project's `.arcforge/` directory.** No shared state between instances. No coordination needed.

What this looks like concretely:

- **Per-project storage.** Each project has its own `.arcforge/graph.db`, `.arcforge/traces.db`, `.arcforge/memory/`. Project A's data lives in Project A's directory; Project B's lives in Project B's. No cross-contamination.
- **Independent MCP servers.** Each Unity instance starts its own MCP server on an OS-assigned ephemeral port (default `Port=0`). Each instance registers independently with the MCP Hub, keyed by project path.
- **Hub-based routing.** The MCP Hub maintains a registry of all connected Unity instances. When Claude Code makes a tool call, the Hub routes it to the correct instance by matching the session's working directory to registered project paths — canonicalized (`realpath` + case-fold) so equivalent paths match, with a single-instance fallback when only one Unity is open. See **Plugin document** §3.5 for the matching algorithm.
- **Independent dashboards.** When the user launches the Charon dashboard from Unity instance A, the dashboard process is scoped to Project A's traces database and binds to an OS-assigned ephemeral port. If the user launches a dashboard from Unity instance B, it gets its own OS-assigned port and reads Project B's traces. The two dashboards run simultaneously without interference. The user can have multiple browser tabs open, one per project.
- **Independent skills config.** Skills are installed globally in the Claude Code config (per Vision §7.5), not per project. Both instances of the agent client read from the same global skill library. This is the correct shape — skills are meant to be shared across projects.

Unity itself prevents two instances from opening the same project simultaneously, so the case of "two Hades instances writing to the same `.arcforge/` directory" cannot occur. This simplifies our concurrency model significantly: each `.arcforge/` directory has at most one writer at a time.

What this means for resource use: running N Unity instances means N Hades stacks running concurrently. CPU, memory, and disk load scale linearly with N. On modern development machines this is rarely a problem, but a user running 4+ Unity instances simultaneously may notice machine load. There is no architectural fix for this — it is the cost of running N independent Unity sessions, regardless of Hades.

Cross-project queries are not supported in v1. A user who wants to ask "show me all SO event channels across both projects" must do so manually, by inspecting each project's graph independently. If demand emerges for cross-project capabilities (e.g., shared eval datasets, multi-project memory inheritance), it is reserved for future versions.

---

## 2. Hades Graph

The graph is the foundation. Without it, the other layers have nothing useful to operate on. This chapter is the longest in the document because the graph is where the most architectural decisions are concentrated and where mistakes are hardest to recover from.

### 2.1 The conceptual model

A Unity project, viewed as an abstract structure, has two interlocking representations:

- **Asset-level**: every meaningful thing in the project is an asset with a GUID. Scripts, scenes, prefabs, materials, ScriptableObjects, audio clips, animations, addressable groups. Assets reference each other via GUID. The asset graph is what `AssetDatabase` exposes.
- **Runtime-level**: scenes contain GameObjects, GameObjects contain Components, Components have serialized fields that may reference other objects in the same scene or other assets. The runtime graph is what `SerializedObject` exposes for any given asset.

These two representations overlap. A scene asset (asset-level) contains GameObjects (runtime-level). A prefab asset contains a tree of GameObjects with Components. A Component on a GameObject in a scene may have a serialized reference to a ScriptableObject asset.

Hades Graph models both representations in a single coherent schema. Every asset is a node. Every GameObject within an asset is a node. Every Component within a GameObject is a node. Edges connect them with typed relationships: `contains`, `references`, `inherits_from` (for prefab variants), `nests_prefab` (prefab-to-prefab nesting), `uses_material`, and so on.

This unified view is what allows queries like "find all prefabs that reference a deprecated script" to compose naturally. The query traverses asset edges (prefab references script) using the same machinery as "find all GameObjects in this scene that have a Light component" (which traverses runtime edges).

### 2.2 The schema

The schema is implemented as a SQLite database with two primary tables and several supporting tables.

#### 2.2.1 The `nodes` table

```sql
CREATE TABLE nodes (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  type TEXT NOT NULL,                   -- node type discriminator
  tier TEXT NOT NULL DEFAULT 'project', -- 'project', 'package', or 'builtin'
  guid TEXT,                            -- Unity GUID for asset nodes; NULL otherwise
  file_id INTEGER,                      -- Unity fileID for sub-objects within an asset
  parent_node_id INTEGER REFERENCES nodes(id),  -- for runtime hierarchy
  name TEXT,                            -- human-readable name
  path TEXT,                            -- for asset nodes, the asset path
  source_range TEXT,                    -- for script nodes, file:line range as JSON
  properties TEXT,                      -- additional type-specific properties as JSON
  created_at INTEGER NOT NULL,          -- unix timestamp
  updated_at INTEGER NOT NULL,
  owner_guid TEXT                       -- schema v4: GUID of the asset that OWNS this node
);

CREATE INDEX idx_nodes_type ON nodes(type);
CREATE INDEX idx_nodes_guid ON nodes(guid);
CREATE INDEX idx_nodes_path ON nodes(path);
CREATE INDEX idx_nodes_parent ON nodes(parent_node_id);
CREATE INDEX idx_nodes_name_type ON nodes(name, type);
CREATE INDEX idx_nodes_tier ON nodes(tier);
CREATE INDEX idx_nodes_owner_guid ON nodes(owner_guid);
CREATE UNIQUE INDEX idx_nodes_guid_fileid ON nodes(guid, file_id) WHERE guid IS NOT NULL;
```

The `type` column is a string discriminator. Valid values are documented in the next subsection.

The `guid` and `file_id` together uniquely identify any asset or sub-object within an asset. For top-level assets, `file_id` is typically the Unity main object's fileID (often a well-known constant like `100100000` for prefab roots). For sub-objects (a Component within a prefab, a GameObject within a scene), `file_id` is the local identifier Unity assigns to that sub-object.

The `parent_node_id` provides the runtime hierarchy: a Component's parent is its GameObject, a GameObject's parent is its parent GameObject (or the scene/prefab root). This is duplicative with the `contains` edge type (described below) but is denormalized into the node table for fast hierarchy traversal queries.

The `properties` column is a JSON blob holding type-specific data. This is where flexibility lives: a Material node might have `{"shader": "URP/Lit", "color": "0xFF0000"}`, while a Component node might have `{"is_enabled": true, "execution_order": 100}`. Application code knows what schema to expect for each type. On read, `NodeRecord` keeps this JSON in its raw string form and parses it **lazily** — only on first access to `Properties` — so the flagship queries (§2.5) that scan thousands of nodes without ever touching their properties pay zero deserialization cost.

The `source_range` column applies only to script-related nodes. When a node represents a class, method, or field within a C# file, this column captures the `file:start_line:end_line` location for navigation purposes.

The `owner_guid` column (schema v4) records the GUID of the asset that **owns** this node. A root asset node owns itself (`owner_guid == guid`); its sub-object children — the GameObjects and Components of a scene or prefab, the ScriptTypes and ScriptMethods of a script — carry their owning asset's GUID even though their own `guid` is NULL. Deletion keys on this column (`DeleteNodesByOwnerGuid`), so re-scanning or deleting an asset removes its entire node set — root plus all children — as a single unit. This replaced the old guid-only delete, which removed only the guid-bearing root and leaked every NULL-guid child on each re-scan (see §2.4). `owner_guid` is NULL by design on seeded builtin types and the synthetic Project node.

Timestamps `created_at` and `updated_at` enable temporal queries: "what changed in the last hour?" These are written by the scanners on every update.

#### 2.2.2 The `edges` table

```sql
CREATE TABLE edges (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  source_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
  target_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
  type TEXT NOT NULL,                   -- edge type discriminator
  properties TEXT,                      -- additional type-specific properties as JSON
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);

CREATE INDEX idx_edges_source_type ON edges(source_id, type);
CREATE INDEX idx_edges_target_type ON edges(target_id, type);
CREATE INDEX idx_edges_type ON edges(type);
CREATE UNIQUE INDEX idx_edges_unique ON edges(source_id, target_id, type);
```

Edges are typed and directional. Every edge has a clearly defined source and target. The unique index ensures we don't accidentally insert duplicate edges of the same type between the same nodes.

The `properties` column on edges holds relationship-specific data. For an `inherits_from` edge between a prefab variant and its base prefab, properties might describe which fields are overridden. For a `references` edge, properties might describe the field through which the reference exists (e.g., `"field": "playerController"`).

`ON DELETE CASCADE` ensures edges are cleaned up automatically when nodes are deleted. This matters for incremental updates: if we delete a GameObject node, all edges touching it disappear without manual cleanup.

#### 2.2.3 Supporting tables

```sql
-- Tracks the version of the graph schema, for migrations. Current version: 4
-- (v4 added owner_guid). The graph is a rebuildable cache, so a migration that
-- changes node/edge column order recreates the tables and lets the next startup
-- repopulate rather than ALTER in place (positional SELECT * reads — see §2.10).
CREATE TABLE schema_version (
  version INTEGER PRIMARY KEY,
  applied_at INTEGER NOT NULL
);

-- Tracks which assets have been scanned, with their content hash.
-- Used to detect what needs re-scanning after Unity reopens. Meta-scanned
-- assets (textures/models/audio/animation/fonts/etc.) store a fixed SENTINEL
-- value here instead of a real content hash — their node data derives from the
-- .meta file, not the (often huge) binary, so the stale check must never MD5 them.
CREATE TABLE scanned_assets (
  guid TEXT PRIMARY KEY,
  content_hash TEXT NOT NULL,           -- real MD5 for code/scene/prefab; sentinel for meta assets
  scanned_at INTEGER NOT NULL,
  scanner_version INTEGER NOT NULL      -- so we can re-scan if scanner changed
);

-- Holds unresolved cross-asset edges until the target node is created.
-- Used by the tree-sitter C# parser to emit type-reference edges
-- before all scripts have been scanned. target_namespace repurposes
-- its column to store reference_kind at scan time.
-- Supertype entries are stored with edge_type = 'extends_or_implements'
-- (a neutral pre-resolution form); ResolvePendingEdges promotes each
-- to 'inherits_from' or 'implements' based on the resolved target's kind.
CREATE TABLE pending_edges (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  source_node_id INTEGER NOT NULL,
  edge_type TEXT NOT NULL,
  target_type_name TEXT NOT NULL,
  target_namespace TEXT,               -- repurposed: stores reference_kind at scan time
  source_asset_guid TEXT,
  properties TEXT,                      -- schema v3: carries the original edge's properties
                                        -- (e.g. {"addressable":true,"field":"m_AssetGUID"}) through
                                        -- deferral so a forward-reference edge keeps its enrichment
                                        -- when ResolvePendingEdges re-inserts it
  created_at INTEGER NOT NULL
);

CREATE INDEX idx_pending_edges_target ON pending_edges(target_type_name);
CREATE INDEX idx_pending_edges_source_asset ON pending_edges(source_asset_guid);

-- Tracks pending invalidations for the lazy-update mode (reserved; not used at runtime)
CREATE TABLE pending_invalidations (
  guid TEXT PRIMARY KEY,
  invalidated_at INTEGER NOT NULL,
  reason TEXT
);

-- Holds metadata about the graph build itself
CREATE TABLE graph_metadata (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
-- Example keys: "last_full_rebuild_at", "last_incremental_at", "build_count",
--               "unity_version", "builtin_unity_version", "current_operation",
--               "csharp_scan_status", "meta_scan_status", "addressables_scan_status",
--               "package_scan_status"  -- "ok" | "degraded" | "unknown"
```

#### 2.2.4 Node types

The set of node types is closed (not arbitrary strings) and is extended only via schema migration. Currently defined:

**Asset-level types:**

- `Scene` — a `.unity` scene asset
- `Prefab` — a `.prefab` asset
- `PrefabVariant` — a prefab whose root is marked as a variant of another prefab
- `Script` — a `.cs` source file
- `ScriptType` — a type defined within a script (substructure of `Script`). Carries a `kind` property: one of `class`, `struct`, `interface`, `enum`, or `record`. Covers top-level declarations and nested types; enums and records (including record structs) each produce their own `ScriptType` node.
- `ScriptMethod` — a method within a `ScriptType` (substructure)
- `ScriptableObject` — a `.asset` file containing a ScriptableObject instance (emits one node + `instance_of` edge to its MonoScript; no separate type node)
- `Material` — a `.mat` asset
- `Shader` — a `.shader` or `.shadergraph` asset
- `Texture` — image assets (png, jpg, jpeg, tga, psd, gif, hdr, exr, bmp)
- `AudioClip` — audio assets (wav, mp3, ogg, aif, aiff)
- `AnimationClip` — `.anim` assets
- `AnimatorController` — `.controller`, `.overrideController` assets
- `Model` — `.fbx`, `.obj`, `.blend`, `.dae`, `.3ds`
- `Font` — font assets (`.ttf`, `.otf`)
- `SignalAsset` — Timeline signal assets (`.signal`)
- `PlayableAsset` — Timeline playable assets (`.playable`)
- `SpriteAtlas` — sprite atlas assets (`.spriteatlas`, `.spriteatlasv2`)
- `RenderTexture` — `.renderTexture` assets
- `Cubemap` — `.cubemap` assets
- `AvatarMask` — `.mask` assets
- `PhysicsMaterial` — `.physicMaterial`, `.physicsMaterial` assets
- `Flare` — `.flare` assets
- `GUISkin` — `.guiskin` assets
- `AudioMixer` — `.mixer` assets
- `RenderPipelineAsset` — URP/HDRP/custom SRP asset
- `AddressableGroup` — addressable group definition
- `AddressableEntry` — individual addressable entry within a group. Given a synthetic guid (`addr_entry:{group}:{entry}`); its asset path is stored in `properties.asset_path`, **not** `Path`, so it never collides with the real asset node (the link to the asset is the `addressable_for` edge)
- `BuildSettings` — project's build settings (singleton)
- `PhysicsSettings` — physics settings (singleton)
- `InputSettings` — input system settings (singleton)
- `Asset` — generic catch-all for other asset types

**Runtime-level types:**

- `GameObject` — a GameObject within a scene or prefab
- `Component` — a Component on a GameObject

**Project-level types:**

- `Project` — singleton root node, parent of nothing in the runtime sense, but a useful anchor for global queries

This list will expand over time. Code paths that handle nodes must accept unknown types gracefully — log and skip rather than crash.

#### 2.2.5 Edge types

Similarly closed set. Currently defined:

**Containment:**

- `contains` — a parent contains a child. Scene contains GameObjects. GameObject contains Components. Prefab contains its GameObject tree. AddressableGroup contains AddressableEntries.

**Reference:**

- `references` — a serialized reference between objects. Component → ScriptableObject. Component → another GameObject. Properties JSON describes the field name.

**Type relationships:**

- `instance_of` — links instance node to its type node. Component instance → ScriptType. ScriptableObject instance → Script (the MonoScript asset for the SO's class).
- `inherits_from` — type-level inheritance. PrefabVariant → Prefab base. ScriptType → ScriptType base class (non-interface supertype). Populated by the tree-sitter parser (user scripts) and builtin type seeding (Unity types). The parser emits a transient `extends_or_implements` pending edge for each base-list entry; `GraphBuilder.ResolvePendingEdges` promotes it to `inherits_from` when the resolved target's `kind` is `class` or `struct`, or to `implements` when the target's `kind` is `interface` — replacing the prior position-in-base-list heuristic.

**Asset relationships:**

- `uses_material` — Component (Renderer, etc.) → Material
- `uses_shader` — Material → Shader
- `uses_texture` — Material → Texture
- `uses_mesh` — Component (MeshFilter, etc.) → Mesh
- `uses_audio` — AudioSource → AudioClip
- `nests_prefab` — Prefab → Prefab (a prefab contains an instance of another prefab as a sub-object)
- `instantiates` — Scene → Prefab. Emitted by `SceneScanner` for each unique source prefab instantiated within a scene. This is a real reference edge (counts as a referrer in `find_references_to`) and is intentionally absent from `StructuralEdgeTypes`.

**Build relationships:**

- `included_in_build` — Scene → BuildSettings (only emitted for *enabled* build scenes; with build index in properties)
- `addressable_for` — AddressableEntry → Asset, and also AddressableGroup → Asset. Both the entry and its group emit this edge to each member asset, so groups surface as referrers of their members.

**Script-level:**

- `defines` — Script → ScriptType. ScriptType → ScriptMethod.
- `code_references` — ScriptType → ScriptType. Cross-file type references extracted by the tree-sitter C# parser. Properties JSON includes `reference_kind` (stored via the `target_namespace` column in `pending_edges` at scan time, then promoted to edge properties on resolution). Values: `field`, `parameter`, `constructor`, `cast`, `attribute`, `return_type`, `property`, `local_var`, `generic_arg`. `using`-alias targets are resolved to the aliased outer type name before emission; generic type arguments from method invocations (e.g. a generic service-resolution call) and from generic return/base types are also captured as `generic_arg` references. Resolved during `ResolvePendingEdges()` by matching type names.
- `implements` — ScriptType → ScriptType. Interface implementation relationships extracted by the tree-sitter parser.
- `calls` — ScriptMethod → ScriptMethod. *(Planned — not yet implemented as of v1.0.0. Would require Roslyn analysis, which is not currently enabled.)*

This list, like node types, is closed and expanded via migration.

### 2.3 The scanners

The scanners are the C# code inside the Unity Package that read the project and write the graph. There is one scanner per asset type, plus a coordinator that orchestrates them.

#### 2.3.1 The coordinator: `GraphBuilder`

`GraphBuilder` is the entry point. It exposes two main operations:

```csharp
public class GraphBuilder
{
    public void RebuildAll();                    // back-compat shim → RebuildParallel
    public void RebuildParallel();               // the single canonical full-rebuild path
                                                 // (Node.js C# scan + Unity-API scanners +
                                                 //  ScanProjectSettings/ScanAddressables + checkpoint)
    public void UpdateAssets(string[] guids);    // incremental update (synchronous .cs scan)
    public void UpdateAssets(string[] guids, bool deferCsScan); // deferCsScan=true: off-thread .cs scan
    public BuildStatus GetStatus();              // current build state
}
```

A full rebuild has two phases. First, `GraphBuilder` spawns a Node.js process (`Scanner~/index.js`) that scans all `.cs` files in the project and packages, writing nodes and edges directly to `graph.db` via `better-sqlite3`. This replaced the original C#/Mono regex-based `ScriptScanner` because Mono's regex engine is 15-30x slower than V8's — what took 3-5 minutes on a medium project now completes in 10-30 seconds. Second, `GraphBuilder` runs the remaining C# scanners (scenes, prefabs, ScriptableObjects, materials, etc.) on the main thread for assets that require Unity APIs like `SerializedObject` and `AssetDatabase`.

An incremental update receives a list of GUIDs whose assets have changed. For `.cs` files it spawns the Node.js scanner with `--mode incremental --guids <list>`; on the interactive (debouncer) path that spawn is **non-blocking** — the rest of the batch is deferred until the subprocess exits, so a script save never freezes the editor (see §2.4.3). For other asset types, it deletes the asset's entire owner-scoped node set, re-scans via the appropriate C# scanner, and inserts the new nodes and edges. Both paths are typically sub-second for individual asset updates.

#### 2.3.2 Scanner interface

Each asset-type scanner implements:

```csharp
public interface IAssetScanner
{
    string[] SupportedExtensions { get; }    // file extensions this scanner handles
    string ScannerName { get; }              // e.g., "PrefabScanner"
    int Version { get; }                     // bumps when scanner output changes

    ScanResult Scan(string assetPath);
}

public class ScanResult
{
    public List<NodeRecord> Nodes;
    public List<EdgeRecord> Edges;
    public List<ScanWarning> Warnings;
}
```

The scanner is given the asset path (no `AssetDatabase` parameter; scanners call Unity APIs directly). It returns the nodes and edges that should exist in the graph as a result of that asset, plus any warnings (e.g., "this prefab has missing references"). The coordinator merges these results into the database.

#### 2.3.3 The individual scanners

**`SceneScanner`** scans a scene asset, walks the GameObject hierarchy, and produces a `Scene` node with `contains` edges to each top-level GameObject. For each GameObject, it produces a `GameObject` node with `contains` edges to its Components. For each Component, it produces a `Component` node with edges to: its `ScriptType` (`instance_of`), any referenced GameObjects (`references`), any referenced assets (`references`, `uses_material`, etc.), and so on. For each unique source prefab instantiated anywhere in the scene, the scanner also emits an `instantiates` edge from the `Scene` node to the source `Prefab`, deduped per scene so each prefab appears at most once per scene.

The scanner has two operational modes depending on whether the scene is currently open in the editor:

- **Open-scene mode (preferred)**: if `EditorSceneManager.GetSceneAt()` finds the target scene already loaded, the scanner walks its in-memory hierarchy directly. No file I/O, no scene loading. This is the fast path used during incremental updates after `sceneSaved` events — Unity already has the scene in memory because the user just saved it.
- **Closed-scene mode (fallback)**: if the scene is not open, the scanner uses `EditorSceneManager.OpenScene()` in additive mode, walks the hierarchy, and closes the scene without saving. This is the slow path, used for full rebuilds or for scenes the user hasn't touched in this session.

The distinction matters significantly for performance. Open-scene mode is sub-second per scene; closed-scene mode is 1-3 seconds per scene because Unity has to deserialize the entire scene file. By preferring open-scene mode whenever possible, the typical incremental update on a saved scene is fast enough to be invisible to the user.

For full rebuilds that must process many closed scenes, the scanner runs in batches with progress reported through `EditorUtility.DisplayProgressBar`. Scenes are processed sequentially because `OpenScene` operates on the global scene state — concurrent opens would conflict.

**`PrefabScanner`** is similar to `SceneScanner` and follows the same two-mode pattern. For prefabs currently open in the prefab stage (detected via `PrefabStageUtility.GetCurrentPrefabStage()`), the scanner walks the in-memory state directly. For other prefabs, it uses `PrefabUtility.LoadPrefabContents()` to load and `PrefabUtility.UnloadPrefabContents()` to release. It detects prefab variants by checking `PrefabUtility.GetCorrespondingObjectFromOriginalSource()` on the prefab root. For variants, it produces a `PrefabVariant` node and an `inherits_from` edge to the base prefab. Override information is recorded in the edge properties.

**Script scanning (Node.js)** — `.cs` files are scanned by a standalone Node.js process in `Scanner~/`, not by a C# `IAssetScanner`. This architectural exception exists because script scanning is pure file I/O — it needs no Unity APIs (`SerializedObject`, `AssetDatabase`, scene loading). Running on V8 instead of Mono yields a 15-30x speedup.

The Node.js scanner (`Scanner~/index.js`) uses a **tree-sitter C# grammar** for AST-based parsing (since v0.9.5). The tree-sitter parser extracts: namespace declarations, type declarations (class/struct/interface/enum/record with base types), method signatures, field declarations, and **cross-file type references**. Each type declaration — including enums, records (and record structs), and nested types — produces a `ScriptType` node with a `kind` property. Supertype entries from the base list are written as `extends_or_implements` pending edges carrying the `supertypes` array property (`[{ name, genericArgs? }]`) on the type node. Reference extraction walks the full AST to find type usage in fields, properties, parameters, constructors, casts, attributes, return types, local variables, and generic arguments from method invocations. `using`-aliases are resolved to the aliased type before emission. These produce `code_references` pending edges that are resolved during `ResolvePendingEdges()`.

The tree-sitter parser replaced the original regex-based parser (`parser.js`). The regex parser extracted the same structural information (namespace, type, method, field) but could not perform cross-file reference analysis. `index.js` imports only `ts-parser.js`; `parser.js` is retained as dead code.

GUIDs are resolved by reading `.meta` files directly (no `AssetDatabase.AssetPathToGUID()` call needed). Content-hash caching via the `scanned_assets` table ensures only changed files are re-parsed on subsequent boots.

**MetaScanner** — alongside script scanning, the Node.js scanner also runs a MetaScanner pass that creates Asset nodes for non-script types (textures, models, audio, animation, fonts, sprite atlases, render textures, audio mixers, and more) by reading `.meta` files. This brings the graph's pending edge count to near-zero by providing target nodes for cross-asset references. The MetaScanner reads GUID and file path and infers the Unity node type from file extension. That extension→node-type map is defined **once** in `MetaAssetTypes` and shared, parity-tested, between the C# side and the Node scanner, so the two can never disagree on which files are meta assets or what node type they become. The same map drives the incremental meta lifecycle (§2.4.3): a meta asset's node is created on import and tracked in `scanned_assets` with a sentinel hash — never deleted-and-recreated on a reload, never MD5-hashed — because its node carries no content-derived data to lose.

For full scans with 1000+ files, the scanner parallelizes parsing across CPU cores using Node.js `worker_threads`. Workers handle file reading, GUID resolution, and tree-sitter parsing; the main thread handles all SQLite writes in a single transaction via `better-sqlite3`.

For full rebuilds and the startup catch-up scan, `GraphBuilder` spawns the Node.js process **synchronously** via `ProcessResolver.Run()` with a 5-minute timeout (a progress bar is shown). For an interactive incremental `.cs` update it instead spawns **asynchronously** via `ProcessResolver.Start()` and polls `HasExited` on `EditorApplication.update` (`PumpCsScan`), so the main thread keeps ticking and a single script save no longer hitches the editor (§2.4.3). If Node.js is not installed, script scanning is skipped with a warning — all other scanners continue normally. Exit code 2 (database locked) triggers a single retry after 1 second on the synchronous path.

The scanner uses version 3 (version 1 was the original C# scanner, version 2 was the Node.js regex scanner). When projects upgrade, the version mismatch in `scanned_assets` triggers a full re-scan automatically.

**`ScriptableObjectScanner`** scans `.asset` files. For each file, it loads the asset, identifies its concrete type, and creates a single `ScriptableObject` node with an `instance_of` edge pointing to the MonoScript asset for that type. Serialized field values and cross-asset references are extracted via `SerializedReferenceExtractor`. No separate type node is created — the `instance_of` edge resolves to the script's existing `Script` node in the graph.

**`MaterialScanner`** extracts shader, color, texture references, and rendering-pipeline-specific properties.

**`ShaderScanner`** distinguishes legacy shaders, surface shaders, and Shader Graph assets. For Shader Graph, it can extract input/output properties; for legacy shaders, it parses the `.shader` file for property declarations.

**`AddressablesScanner`** reads the addressable settings asset and produces `AddressableGroup` and `AddressableEntry` nodes. For each member asset, it emits `addressable_for` edges from both the `AddressableEntry` and the `AddressableGroup` to the member asset, so that `find_references_to` returns the group as a referrer alongside the entry.

**`ProjectSettingsScanner`** reads the various `ProjectSettings/*.asset` files and produces singleton nodes for `BuildSettings`, `PhysicsSettings`, `InputSettings`, etc.

**`RenderPipelineScanner`** detects which render pipeline is active and produces a `RenderPipelineAsset` node with its features and quality settings.

Asset types that don't require Unity APIs for node creation (Texture, AudioClip, AnimationClip, AnimatorController, Model, Font, SignalAsset, PlayableAsset, SpriteAtlas, RenderTexture, Cubemap, AvatarMask, PhysicsMaterial, Flare, GUISkin, AudioMixer) are handled by the **MetaScanner** in the Node.js scanner pipeline, not by C# `IAssetScanner` implementations. The MetaScanner reads `.meta` files to extract GUIDs and infers node types from file extensions (36 extensions mapped to 16 node types). This is a lightweight approach that creates Asset nodes sufficient for reference resolution without needing Unity's import pipeline.

#### 2.3.4 Scanner versioning

Each scanner has a `Version` integer. When a scanner's output format changes (e.g., we add new edge types it emits), the version is bumped. The `scanned_assets` table records the scanner version that produced each asset's data. On startup, if the registered scanner version is higher than the recorded version, the asset is automatically re-scanned.

This is the safety net for graph correctness when scanners evolve.

#### 2.3.5 Unity builtin types

During graph rebuild, `GraphBuilder.SeedBuiltinTypes()` uses runtime reflection to enumerate public types from loaded Unity assemblies (`UnityEngine`, `UnityEditor`, and related). Each type is inserted as a `ScriptType` node with `source=builtin` and a `kind` property (`class`, `struct`, or `interface`) in its properties. Inheritance (`inherits_from`) and interface implementation (`implements`) edges are created between builtin types.

This provides resolution targets for `inherits_from` and `implements` pending edges when user scripts inherit from Unity base classes (e.g., `MonoBehaviour`, `ScriptableObject`, `Editor`). Without these nodes, those edges would remain unresolved. The seeding produces approximately 4,000 type nodes and 3,600 edges, varying by Unity version.

Builtin type nodes are cached across sessions — seeding is skipped when the `builtin_unity_version` metadata key already matches the current Unity version. A version upgrade or manual full rebuild clears and re-seeds them. The seeding operation takes approximately 200-400ms when it runs.

### 2.4 Incremental updates

The incremental update mechanism is the second-most-consequential piece of the graph after the schema. If incremental updates work, the graph stays fresh with no user effort. If they don't work, the graph degrades silently and the agent gives wrong answers.

#### 2.4.1 The triggering events

Unity provides several events through which we detect changes:

- **`AssetPostprocessor.OnPostprocessAllAssets`** — fired after assets are imported, deleted, moved, or modified. This is the primary trigger. The callback receives lists of changed paths classified as imported, deleted, moved, etc.
- **`EditorApplication.projectChanged`** — fired when the project structure changes. Less granular than `AssetPostprocessor` but catches some cases the postprocessor misses.
- **`EditorSceneManager.sceneSaved`** — fired when a scene is saved. Necessary because in-memory scene edits are not visible to `AssetPostprocessor` until save.
- **`PrefabStage.prefabSaved`** — fired when a prefab is saved from the prefab editing stage. Similar reason.

The Hades Unity Package registers handlers for all four. Each handler enqueues update requests onto a debounced queue.

#### 2.4.2 The debouncer

Without debouncing, every keystroke during an inspector edit could trigger a scanner run. The debouncer accumulates incoming update requests and flushes them in batches.

Debounce parameters:

- **Idle threshold**: 250ms of no new requests before flushing. Tuned to balance responsiveness against avoiding mid-edit churn.
- **Maximum delay**: 2000ms. Even if requests keep arriving, flush after 2 seconds to prevent unbounded staleness.
- **Batch size cap**: 1000 assets per batch. If more than 1000 assets are pending, the batch is split.

The debouncer runs on the main thread (since it triggers Unity API calls) and uses `EditorApplication.update` for its tick.

#### 2.4.3 The update flow

When the debouncer flushes:

1. Group pending changes by type: imported, modified, deleted, moved.
2. For deletions: remove the corresponding nodes from the database. CASCADE removes edges automatically.
3. For moves: update the `path` column on existing nodes. No re-scan needed if content didn't change.
4. For imports and modifications, the changed GUIDs are classified three ways: `.cs` files go to the Node.js scanner (`--mode incremental`, spawned off-thread on the interactive path); meta assets (texture/model/audio/etc.) go to the lightweight in-place meta path (`WriteMetaNodeIfNeeded` — create if missing, touch the sentinel row otherwise; never deleted); everything else is re-scanned via its C# scanner.
5. For each changed non-meta asset, its **entire owner-scoped node set** is deleted (`DeleteNodesByOwnerGuid` — the root plus every NULL-guid child) and fresh nodes and edges from the new scan are inserted. AUTOINCREMENT IDs are not preserved — this is a delete-and-rescan approach per asset, not a diff. (This replaced `DeleteNodesByGuid`, which removed only the guid-bearing root and leaked the children on every save — see §2.2.1.)
6. Inbound reference edges from OTHER (unchanged) assets are captured before the delete and re-pointed at the recreated node afterward; cross-asset edges whose own source was re-scanned reconnect via the pending-edge mechanism — after nodes are re-inserted with new IDs, `ResolvePendingEdges()` reconnects them by matching type names and GUIDs. Together these stop an incremental update from eroding references *into* the changed asset.
7. Update `scanned_assets` with the new content hash (or the sentinel, for meta assets).
Note: the non-`.cs` incremental work is O(changed assets) and completes well within a frame, so it is deliberately NOT gated by the "busy" fast-path (a busy response there would risk a spurious retry on a non-idempotent write that already applied). The interactive `.cs` scan IS gated for its duration: while its off-thread subprocess runs, `_csScanInFlight` is set and `IsBusyForRequests` returns true, so a concurrent tool call gets a structured `busy` instead of waiting out the transport timeout. `_status` also stays `Updating` across that window, which keeps the asset-postprocessor's drop in place so a main-thread write cannot race the subprocess's writes.

#### 2.4.4 Failure modes and recovery

Incremental updates can drift out of sync with reality. Possible causes:

- A Unity event fires but the handler crashes silently.
- A scanner has a bug that produces wrong output for some edge case.
- The user modifies files outside Unity (e.g., editing a `.unity` file in a text editor).
- Unity is closed while an update is in flight.

The system has multiple safety nets:

- **Periodic full validation** *(Planned — not yet implemented as of v1.0.0.)* A planned background check would compare `scanned_assets.content_hash` against actual file hashes for a sample of assets every 24 hours of editor time; mismatches would trigger re-scans.
- **Manual rebuild command**: `Hades: Rebuild Graph` menu option triggers a full rebuild. Documented as the recovery action for "the agent seems confused about my project."
- **Stale-on-startup detection**: when Unity opens, every asset's hash is checked against the recorded `scanned_assets.content_hash`. Mismatched assets are queued for re-scan during startup.
- **Scanner version check**: as described in 2.3.4, scanner version mismatch triggers re-scan.

These are belt-and-suspenders. The expected normal behavior is that incremental updates stay perfectly synchronized; the safety nets are there for the failure cases.

**Package-scan robustness**: `GraphBuilder.ScanPackages` uses a non-destructive update strategy. Rather than deleting the package tier upfront before scanning (which would leave the graph empty if the scan timed out), it performs a scan-then-reconcile approach: it scans first, writes new nodes, then removes only nodes whose backing file is no longer present. If the scan fails or times out, existing package nodes are preserved and `package_scan_status` is set to `"degraded"` in `graph_metadata`. The package-tier scanner timeout (10 minutes) is longer than the project-tier timeout (5 minutes) because the Unity PackageCache can be substantially larger than the project's own scripts.

### 2.5 Querying the graph

The graph is queried through MCP tools. The tools translate agent intent into SQL and return structured results.

#### 2.5.1 Tool API philosophy

Per the architectural decision in the planning phase (hybrid approach), Hades exposes:

- **A small number of granular tools** for the most common queries. These are well-documented, deterministic, and easy for the agent to choose correctly.
- **A general-purpose query tool** as an escape hatch for cases the granular tools don't cover. This tool accepts a structured query expression rather than raw SQL, to keep the abstraction at the right level.

Hades exposes 90 MCP tools total: 22 graph/observability/memory tools built natively for Hades, plus 68 editor-action tools migrated from UniClaude. The graph tools are listed below; the editor-action tools are catalogued in the Roadmap §7 (Phase 5 migration section).

#### 2.5.2 Granular query tools

- `get_project_summary(depth: shallow|medium|deep)` — returns a structured summary of the project: counts, render pipeline, key directories. The `scan_health` block reports per-scanner status: `csharp`, `meta`, `addressables`, and `packages`.
- `find_components_using_pattern(pattern_name: string)` — finds all components matching a known structural pattern (e.g., "ScriptableObjectChannel<T>"). Patterns are pre-defined. Matches against the `supertypes` property of `ScriptType` nodes.
- `find_references_to(target_path: string)` — finds all assets and components that reference a given asset. Structural/containment edges (`defines`, `contains`, `nests_prefab`) are excluded from `references` and `reference_count`; for Prefab/PrefabVariant targets, variant `inherits_from` edges are also excluded. For a `.cs` target the query attributes referrers to the `ScriptType` whose name matches the file stem (falling back to all co-located types), preventing sibling types in the same file from inflating each other's counts. The response includes a `nested_by` array listing direct structural parents (prefabs that nest the asset via `nests_prefab`; prefab variants that derive from it via `inherits_from`) so a nested-only asset is not mistaken for unused. Always includes a `static_analysis_coverage: partial` confidence factor naming blind spots (reflection, runtime dispatch, DI containers, dynamic instantiation) with the recommendation to check `nested_by` before treating an asset as unused.
- `trace_dependencies(asset_path: string, max_depth: int)` — recursively follows references from an asset, excluding `defines` edges so a script's own declared methods are not returned as dependencies. Always includes a `static_analysis_coverage: partial` confidence factor.
- `find_orphan_scripts()` — scripts not referenced anywhere.
- `find_prefabs_with_component(component_type: string)` — locate all prefabs containing a given component type. Ascends the full `contains` chain from each matching `Component` to the prefab root, so deeply-nested component hosts are found, not only direct children. Variant-inherited components are labelled `source: "inherited"` and excluded from the headline `count`; the response includes `total_including_inherited_variants` for completeness.
- `get_scene_summary(scene_path: string)` — high-level overview of a scene's structure.
- `get_prefab_inheritance(prefab_path: string)` — variant chain for a prefab.
- `analyze_render_pipeline()` — current pipeline, custom features, render features.
- `search_by_name(name_pattern: string, type_filter: string, path_prefix: string, match_mode: string)` — search across nodes by name. `path_prefix` filters results to a directory subtree. `match_mode` supports `contains` (default), `exact`, and `prefix`.
- `get_recently_changed(hours: int)` — assets changed in the last N hours.

Each tool has a clear input schema, a clear output schema, and clear documentation in the MCP `tools/list` response.

The path- and name-based lookups behind these tools are index-backed. `find_references_to` and `trace_dependencies` resolve their target node via `FindNodesByPath` (the `idx_nodes_path` index); `find_prefabs_with_component` resolves the component via `FindNodesByNameAndTypeAll` (the `idx_nodes_name_type` index) — both `O(log n)` index probes. They replaced an earlier pattern that loaded and materialized the *entire* `nodes` table per call and filtered in C# (an `O(n)` scan that, combined with eager `Properties` parsing, allocated hundreds of thousands of objects to find one node on large graphs). `FindNodesByPath` orders results by name, so a `.cs` target resolves to its `ScriptType` (named after the file stem) ahead of the `Script` node — the node `trace_dependencies` must traverse from.

#### 2.5.3 The general-purpose query tool

`query_graph(query: GraphQuery)` accepts a structured query expression:

```json
{
  "from": {"type": "Prefab"},
  "where": {
    "edges": [
      {"type": "references", "target": {"type": "Script", "name": "PlayerController"}}
    ]
  },
  "select": ["id", "name", "path"],
  "limit": 100
}
```

This is translated server-side to SQL with appropriate joins. The translation is bounded — the structured query language does not expose arbitrary SQL, only a constrained subset that the translator knows how to handle safely. This prevents agents from accidentally constructing pathologically slow queries.

### 2.6 Performance characteristics

#### 2.6.1 Build performance

Illustrative performance ranges after the Node.js script scanner migration (Phase 5). These figures are drawn from a single measured data point (163k-node project at ~10 seconds) and representative estimates; they are not a formal benchmark suite and should be treated as indicative, not guaranteed:

| Project size | Asset count | Full rebuild | Incremental (single asset) |
|---|---|---|---|
| Small | < 1k | 2-5 sec | < 100ms |
| Medium | 1k-10k | 5-15 sec | 100-200ms |
| Large | 10k-50k | 15-45 sec | 200-500ms |
| Very large | 50k-200k | 45-120 sec | 500ms-2sec |
| Enterprise | > 200k | varies | varies |

Script scanning (the dominant cost for projects with many `.cs` files) is now 15-30x faster than the original C#/Mono implementation. In a test with 6,268 package scripts + project scripts, the full first boot (package scan + project rebuild + edge resolution) completed in ~10 seconds, producing 163,449 nodes and 161,696 edges.

Scene and prefab scanners (which open assets via Unity APIs) remain the bottleneck for projects with many scenes. Script scanning is no longer a significant contributor to build time.

#### 2.6.2 Query performance

Illustrative latency targets for the schema and indexes described. Name search uses `LIKE` on a B-tree index, not an FTS table:

| Query type | Expected latency |
|---|---|
| Lookup by GUID | < 1ms |
| Lookup by path | < 1ms |
| List all nodes of a type | 1-10ms |
| Find references (one-hop) | 1-5ms |
| Find dependencies (5-hop traversal) | 10-50ms |
| Name search (`contains` mode) | 5-20ms (LIKE on B-tree; no FTS index) |
| Project-wide aggregations | 10-100ms |

These are generally well below the latency of agent reasoning, so the graph is unlikely to be the bottleneck in agent interactions. The path/name lookups behind `find_references_to`, `trace_dependencies`, and `find_prefabs_with_component` hold these targets because they use `idx_nodes_path` / `idx_nodes_name_type` rather than a full-table scan, and because `NodeRecord.Properties` parses lazily (§2.2.1) — a query that returns thousands of nodes without reading their properties does no JSON work.

#### 2.6.3 Storage size

| Project size | Database size |
|---|---|
| Small | 1-10 MB |
| Medium | 10-100 MB |
| Large | 100-500 MB |
| Very large | 500MB-2GB |

These sizes are bearable. The largest known precedent (codebase-memory-mcp) operates at similar scale.

### 2.7 Concurrency and consistency

The graph database must support concurrent reads (from MCP tool calls served on background threads via the Main Thread Bridge) alongside writes (from scanners running on the main thread). This is non-negotiable: the alternative is reads blocking on every scan, which would freeze tool response times during incremental updates.

#### 2.7.1 SQLite configuration

The database is initialized with explicit pragmas that enable concurrent access and tune for the Hades workload:

```sql
PRAGMA journal_mode = WAL;              -- Write-Ahead Logging: readers don't block writers
PRAGMA synchronous = NORMAL;            -- safer than OFF, faster than FULL; OK for our durability needs
PRAGMA busy_timeout = 5000;             -- wait up to 5 sec for locks before erroring
PRAGMA cache_size = -65536;             -- 64MB page cache (negative = KB)
PRAGMA temp_store = MEMORY;             -- temp tables in memory, not on disk
PRAGMA mmap_size = 268435456;           -- 256MB memory-mapped I/O
PRAGMA foreign_keys = ON;               -- enforce ON DELETE CASCADE
PRAGMA wal_autocheckpoint = 1000;       -- checkpoint after 1000 WAL pages (explicitly set)
PRAGMA journal_size_limit = 67108864;   -- cap WAL file at 64MB to bound disk usage
```

`WAL` mode is the most consequential. Without it, SQLite uses rollback journaling, where readers block during writes. With WAL, readers see a consistent snapshot from before the in-progress write, and writes append to a separate WAL file that is checkpointed back to the main DB periodically. This is the precondition that makes the entire Hades concurrency model viable.

`busy_timeout = 5000` handles the rare case where a lock is held longer than expected (e.g., a complex multi-statement transaction). Instead of immediately failing with `SQLITE_BUSY`, the call waits up to 5 seconds. Beyond 5 seconds, the call errors and the caller can retry or surface the issue.

`synchronous = NORMAL` is a deliberate trade-off. `FULL` fsyncs after every transaction (safest, slowest). `OFF` skips fsyncs entirely (fastest, can lose recent writes on crash). `NORMAL` fsyncs at WAL checkpoints, which is the right balance: a power loss might lose the last few seconds of writes, but the database itself stays consistent. Hades's data is recomputable from the project source, so losing a few seconds of incremental updates is recoverable; corrupting the database is not.

#### 2.7.2 Properties

With this configuration:

- Multiple readers can read concurrently from any thread.
- One writer at a time. The C# `GraphBuilder` writes on the main thread; the Node scanner subprocess writes through its own `better-sqlite3` connection (a cross-process, WAL-coordinated writer). The two never write concurrently: during a synchronous scan the main thread is blocked in `WaitForExit`, and during an *asynchronous* incremental `.cs` scan the main thread stays in the `Updating`/`_csScanInFlight` (busy-gated) state, so its deferred continuation runs only after the subprocess has exited (§2.4.3). Writers never wait on readers.
- Readers see a consistent snapshot, not affected by in-flight writes.
- Write throughput is limited by disk fsync at WAL checkpoint boundaries, not by individual writes. For the volume of writes Hades does (handfuls of nodes/edges per asset update), this is not a bottleneck.

#### 2.7.3 Consistency guarantees

- After a *synchronous* `GraphBuilder.UpdateAssets()` returns, all subsequent queries see the new state. On the *asynchronous* incremental `.cs` path, `UpdateAssets()` returns as soon as the scan is spawned; the new state lands when the subprocess exits and `PumpCsScan` runs the continuation — until then `IsBusyForRequests` is true, so a tool call gets a `busy` response rather than a stale read.
- During an in-flight update, queries see the previous state.
- There is no staleness window where queries can see partial updates within a single transaction.
- Across multiple transactions (e.g., a large rebuild that splits writes into batches), readers may see intermediate states. The build coordinator wraps related batches in a single logical operation tagged in `graph_metadata` so the agent can detect "rebuild in progress" and respond accordingly.

#### 2.7.4 Locking behavior

- Writes acquire SQLite's exclusive lock briefly, on the order of microseconds. During this window, other writers block; readers do not.
- The Main Thread Bridge ensures writes are serialized at the application level — only one update is processed at a time. Even if multiple sources trigger updates concurrently, they queue.
- WAL checkpoints happen automatically (set to every 1000 pages via `wal_autocheckpoint`; WAL file is capped at 64MB via `journal_size_limit`). Checkpoints briefly hold a lock that blocks new writers; readers continue. Checkpoint duration is sub-millisecond for typical Hades load.

#### 2.7.5 The "rebuild in progress" signal

Per the failure scenario in Pipeline 12, queries during a graph rebuild can return partial data. To prevent silent staleness:

The `graph_metadata` table holds a row with key `current_operation`. When a rebuild starts, this row is set to a JSON object describing the operation. When the rebuild completes, the row is **deleted** (not set to null — `DELETE FROM graph_metadata WHERE key = 'current_operation'`):

```
key                     value (present only during rebuild)
current_operation       '{"kind":"rebuild","started_at":1715240000}'
```

Every query tool checks `IsRebuildInProgress()` before executing. If a rebuild is in progress, the `ConfidenceBlock` in the response is downgraded unconditionally (no per-GUID intersection gating) — the response receives `level: "medium"` with factor `graph_freshness: "rebuilding"` and a recommendation to retry after the rebuild completes.

The agent reads the `confidence` block and either retries (for short rebuilds) or proceeds with explicit acknowledgment.

> **Status: Planned — not yet implemented as of v1.0.0.** The per-tool response fields `"graph_state"`, `"affected_assets"`, and `"consider_retry_after_ms"` described in earlier design drafts do not exist; the real mechanism is the unconditional `ConfidenceBlock` downgrade described above.

### 2.8 Edge cases and known gotchas

Issues that the design must handle:

- **Missing references**: a prefab references a script, the script is deleted. The prefab now has a "missing reference" placeholder. The scanner detects null components (missing script), emits a `ScanWarning`, and skips the component. A `references_missing` flag on Component nodes is not currently set — the warning is in the scan log only.
- **Circular prefab references**: prefab A contains prefab B, prefab B contains prefab A. Unity allows this in some configurations. The scanner detects cycles and emits a warning but does not crash.
- **Nested prefabs**: prefab A contains an instance of prefab B as a sub-object. The scanner produces nodes for B's sub-objects with appropriate `contains` edges, plus a `nests_prefab` edge from the prefab asset A to the prefab asset B (not a GameObject-level edge).
- **Prefab variants with deep override chains**: variant V1 inherits from variant V2 inherits from base prefab P. The `inherits_from` edges form a chain. Override information is recorded at each level.
- **Multi-scene setups**: scenes loaded additively. The graph captures all referenced scenes via build settings. Runtime additive loading is captured if it goes through `BuildSettings.scenes` or addressables.
- **GUID collisions**: extraordinarily rare but possible if a project imports an asset package that uses the same GUIDs as existing assets. The scanner detects collisions and logs warnings.
- **`.meta` file desync**: if a file's content hash doesn't match its `.meta`, Unity's behavior is undefined. The scanner records both hashes in node properties for debugging.
- **Deleted-but-referenced assets**: an asset is deleted but other assets still reference it (Unity creates "missing" placeholders). The scanner skips null components and emits `ScanWarning` entries; no special `references_missing` property is set on surviving nodes.

### 2.9 Static analysis boundaries

The graph captures what static analysis can see. Some patterns common in Unity projects are **fundamentally invisible** to static scanning. Acknowledging these boundaries explicitly is essential — pretending the graph is complete when it isn't is the fastest way to lose user trust.

#### 2.9.1 What the graph cannot capture

The following patterns produce edges that are missing or incorrect in the static graph:

**Dependency injection containers (Zenject, VContainer, etc.)** — bindings are configured at runtime or in installer code. The static graph sees the binding installer code but not the resolved relationships. A class injected via `[Inject]` has no static edge to the class providing the binding.

**Reflection-based code** — calls like `Type.GetType(stringName)`, `assembly.GetTypes().Where(...)`, `Activator.CreateInstance`. The targets are determined at runtime from string values. Static analysis cannot resolve them.

**Addressables loaded by key** — `Addressables.LoadAssetAsync<T>(stringKey)`. The key is a runtime value; the asset it resolves to is not statically determinable. The graph can capture the addressable group structure, but the relationship between a piece of code and a specific addressable it loads is not captured.

**Dynamic instantiation by name** — `Resources.Load(stringPath)`, `GameObject.Find(stringName)`. Same issue as addressables.

**UnityEvents wired in inspector** — UnityEvents declared in code are visible, but the specific listeners attached in the inspector are properties of the scene/prefab serialized data. The scanner does capture these from serialized YAML, but only for the listeners that exist as serialized references; runtime-attached listeners (`unityEvent.AddListener(...)`) are not.

**ECS worlds and systems** — Entity Component System uses runtime archetype matching. Systems query for components by type signature, not by direct reference. The static graph captures system class definitions but not which entities a system processes at runtime.

**Coroutines and async state machines** — code that "follows" through coroutines or async/await spans multiple stack frames. The static graph captures method definitions but does not model control flow across `yield` boundaries.

**Editor scripting via ScriptableSingleton or EditorPrefs** — state shared across editor scripts via these mechanisms is not captured as edges.

#### 2.9.2 How the graph signals incompleteness

The tools that depend most heavily on complete static data surface incompleteness via explicit confidence factors rather than silently returning empty results:

- `find_references_to` and `trace_dependencies` always include a `static_analysis_coverage: partial` confidence factor in every response, naming the specific blind spots (reflection, runtime/string-based dispatch, DI containers, dynamic instantiation) and recommending that "no results" not be read as "definitely unused/unreferenced".
- When the package tier is degraded, both tools additionally emit a `package_scan: degraded` confidence factor and a `supertypes_external_unresolved` count (the number of pending supertype edges pointing at precompiled/external types Hades cannot index).
- `find_references_to` populates a `nested_by` array (structural parents) so callers can distinguish "zero runtime referrers" from "truly unused".

Node-level `analysis_completeness` properties and `dynamic_dispatch_marker` nodes are not yet emitted; the signals described above apply at the tool-response level. *(Planned — not yet implemented as of v1.0.0.)*

#### 2.9.3 Future runtime instrumentation

The boundaries described above are the limits of static analysis. A future Hades version may add **runtime instrumentation** — hooks that capture actual relationships during play mode (which addressables actually loaded, which systems actually processed which entities, which DI bindings actually resolved). This would supplement the static graph with runtime evidence.

This is explicitly out of scope for v1. Static graph is hard enough; runtime instrumentation is a substantially larger undertaking. But the graph schema is designed to accommodate it later: edges could have a `evidence_source: static | runtime | both` property reserved for this purpose. *(Planned — not yet implemented as of v1.0.0.)*

#### 2.9.4 Implications for users

The right user mental model is: **Hades sees what a static analyzer sees**, plus serialized state from Unity assets. It does not see runtime behavior. For projects heavy in DI, reflection, or runtime composition, the graph is informative but incomplete.

This is documented in user-facing materials. The agent itself surfaces the limitation when relevant. Trust depends on this honesty — overpromising completeness leads to wrong agent suggestions and broken trust.

### 2.10 Migration from UniClaude scanners

UniClaude has scene, prefab, script, shader, and ScriptableObject scanners. These produced a flat key-value index, not a typed graph. Migration involves:

1. Adapting each scanner's output format from flat-keyword to nodes-and-edges.
2. Adding GUID and fileID tracking (UniClaude scanners didn't preserve these consistently).
3. Adding type information (UniClaude scanners produced primarily textual output for keyword search).
4. Wiring scanners into the GraphBuilder rather than the keyword retriever.

The core scanning logic — opening scenes, walking hierarchies, parsing C# — is reused mostly unchanged. The output format and downstream consumers are what change.


---

## 3. Hades Charon

Charon is the observability layer. Every meaningful event in Hades — every MCP tool call, every graph build, every memory read, every action against Unity — is captured as a structured trace (graph *queries* are captured within the enclosing tool span rather than per-query; see §3.4.2). The traces serve two distinct purposes: internal debugging for ArcForge developers building Hades, and external visibility for users debugging their own AI workflows.

### 3.1 Why this layer matters

It is tempting to treat observability as a deferred feature, something to add later when the product matures. This is the wrong order for Hades. The reasoning was made explicit in the technical evaluations earlier in our planning: when an AI agent has the ability to modify project files, mistakes are not abstract. The agent breaks a prefab, the user reverts, and without traces, neither of us — the user nor we — can diagnose what happened.

Charon is therefore built from the start as core infrastructure, not as an add-on. The internal use case (debugging Hades during its own development) drives most design decisions, with the user-facing dashboard as a layered addition.

### 3.2 The trace model

The trace model follows OpenTelemetry conventions. A unit of work is a **span**. Spans nest: a parent span contains zero or more child spans, all of which logically happen within the parent's duration. A complete tree of nested spans rooted in a top-level span is a **trace**.

For Hades:

- A top-level span typically corresponds to a single MCP tool call from the agent client.
- Child spans within it cover sub-operations: the graph query that the tool ran, the memory file that was read, the trace event that was emitted, etc.
- Spans carry attributes (key-value pairs of metadata) and events (timestamped log entries within the span's duration).
- Spans are linked across processes when relevant (e.g., the agent client's request span links to the Unity Package's tool execution span via a trace ID).

#### 3.2.1 Span structure

Each span captures:

- `span_id` — unique within a trace
- `trace_id` — shared across all spans in the same trace
- `parent_span_id` — the immediate parent, or null for root spans
- `name` — descriptive name like `mcp.tool.find_prefabs_with_component` or `graph.build.incremental`
- `kind` — semantic category: `server` (top-level handler), `client` (outbound call), `internal` (in-process work)
- `start_time` and `end_time` — wall-clock timestamps
- `status` — `OK`, `ERROR`, `TIMEOUT`
- `attributes` — JSON object with operation-specific data
- `events` — array of `{timestamp, name, attributes}` entries

#### 3.2.2 Trace structure

A trace is materialized as a row in the `traces` table:

```sql
CREATE TABLE traces (
  trace_id TEXT PRIMARY KEY,
  root_span_name TEXT NOT NULL,
  start_time INTEGER NOT NULL,
  end_time INTEGER,
  status TEXT,                    -- OK, ERROR, TIMEOUT (SpanStatus enum; no IN_PROGRESS)
  total_duration_ms INTEGER,
  span_count INTEGER,
  attributes TEXT                 -- top-level trace attributes as JSON (always null in current build)
);

CREATE TABLE spans (
  span_id TEXT PRIMARY KEY,
  trace_id TEXT NOT NULL REFERENCES traces(trace_id) ON DELETE CASCADE,
  parent_span_id TEXT,            -- null for root span; references spans(span_id)
  name TEXT NOT NULL,
  kind TEXT NOT NULL,
  start_time INTEGER NOT NULL,
  end_time INTEGER,
  status TEXT,
  attributes TEXT,                -- JSON
  events TEXT                     -- JSON array (written at emit time; not read back on the C# read path)
);

CREATE INDEX idx_spans_trace ON spans(trace_id, start_time);
CREATE INDEX idx_spans_name ON spans(name);
CREATE INDEX idx_traces_start_time ON traces(start_time DESC);
CREATE INDEX idx_traces_status ON traces(status);
```

The denormalization is intentional: traces have a few summary fields lifted from their root span for fast filtering, while detailed span data lives in `spans`.

### 3.3 The emitter

The `CharonEmitter` is the C# class inside the Unity Package responsible for producing trace data. It exposes a fluent API:

```csharp
using (var span = Charon.StartSpan("mcp.tool.find_prefabs_with_component", SpanKind.Server))
{
    span.SetAttribute(SpanAttributes.ToolName, "find_prefabs_with_component");
    span.SetAttribute("component_type", componentType);

    // ... do work. The graph queries underneath are deliberately NOT sub-spanned —
    //     per-query graph.query.* spans were removed to avoid trace write-amplification
    //     (see §3.4.2). The API still supports child spans (graph BUILD ops use them).

    span.SetAttribute("results.count", results.Count);
    span.SetStatus(SpanStatus.Ok);
}
```

The emitter handles:

- Generating IDs (random bytes via `RandomNumberGenerator` — not time-orderable; ordering relies on the `start_time` column).
- Tracking the active span via `AsyncLocal<Span>` so child spans implicitly nest correctly.
- Buffering writes to avoid blocking work on disk I/O. A background task drains the buffer to SQLite every 500ms or when the buffer reaches 1000 spans, whichever comes first.
- **Crash behavior**: the buffer is in-memory (`ConcurrentQueue`). Spans not yet flushed at crash time are lost. Worst-case data loss is the last 500ms of spans. SQLite WAL mode handles database-level consistency across crashes but does not preserve the in-flight buffer.

#### 3.3.1 Cross-process trace IDs

When the agent client makes an MCP call, the trace ID needs to be threaded through. The MCP protocol does not have first-class trace context, so we use a custom header: `X-Hades-Trace-Id`. If present, the Unity Package uses it as the trace ID for the root span; if absent, it generates a new one. The agent client's plugin can be configured to inject this header (with a generated trace ID) for every tool call.

This enables end-to-end traces that span the agent's reasoning and Unity's execution, when the agent client cooperates. When it doesn't, traces are still complete on the Unity side but not linked to agent-side context.

### 3.4 What gets instrumented

The set of instrumented operations defines what is observable. Hades instruments:

#### 3.4.1 MCP tool calls

Every incoming MCP tool call creates a root span with:

- `name`: `mcp.tool.<tool_name>`
- `kind`: `Server`
- attributes: tool name and parameter values (written verbatim — no redaction currently applied) and client identifier. Attribute **keys** come from a single shared `SpanAttributes` constant (`tool_name`, `tool_input`, …) used by both the emitter and the Asphodel inference analyzers, so the two cannot drift — an earlier mismatch (emitter wrote `tool.name`, analyzers read `tool_name`) silently produced zero inferred patterns until the keys were unified (§4.6).
- child spans for any sub-operations the tool performs

#### 3.4.2 Graph queries

Earlier versions emitted a `graph.query.<operation>` span for **every** database query the graph layer issued. This was removed. A single traversal tool (`trace_dependencies`, `find_references_to`) issues one-to-two SQL statements **per node/edge visited**, so per-query spans amplified one tool call into thousands of trace rows — bloating `traces.db` and slowing the trace writer. The graph layer no longer emits per-query spans; the operation stays observable at the **tool** granularity (the enclosing `mcp.tool.*` span records the call, its parameters, its result count, and its latency), which is the level a user actually debugs at.

#### 3.4.3 Graph build operations

Full rebuilds and incremental updates emit:

- `name`: `graph.build.<kind>` (e.g., `graph.build.full_rebuild`, `graph.build.incremental`)
- attributes: assets affected, nodes created/updated/deleted, edges created/updated/deleted, total duration, scanner durations broken down by type

#### 3.4.4 Memory operations

Reads and writes to Asphodel:

- `name`: `memory.<operation>` (e.g., `memory.read.tier1`, `memory.write.tier1.proposal`)
- attributes: file path, content size, validation result (for self-validation checks)

#### 3.4.5 Lifecycle events

Editor lifecycle:

- `name`: `lifecycle.<event>` (e.g., `lifecycle.startup`, `lifecycle.domain_reload`, `lifecycle.shutdown`)
- attributes: duration, state before/after

These are useful for understanding "the agent saw stale data because a domain reload happened mid-session" type incidents.

#### 3.4.6 Errors

Anywhere a span's status is set to `ERROR`, structured error attributes are added:

- `error.kind` (e.g., `graph.timeout`, `unity.api_threading_violation`)
- `error.message`
- `error.stack` (truncated)

Errors are also flagged at the trace level so they're easy to filter in the dashboard.

### 3.5 Privacy and data handling

> **Status: Planned — not yet implemented as of v1.0.0.** The path redaction, content redaction, and export-controls behaviors described here are design targets. Currently, `tool.input` is written verbatim to trace attributes with no redaction. The local-only and retention-policy behaviors are real.

Traces can contain sensitive information: file paths, user-typed prompts (if forwarded by the agent client), code snippets, project structure. Because traces are local-first, this is controllable but still important.

Current behavior:

- **Local-only.** Traces are stored in `.arcforge/traces.db` and never transmitted.
- **Retention policy**: 30 days. Older traces are auto-pruned at Unity startup.
- **No redaction**: tool input parameters are stored verbatim. Path and content redaction are planned future features.

Planned (not yet implemented):

- **Path redaction**: configurable. Off by default (paths help debug). Can be enabled to replace project paths with hashes.
- **Content redaction**: file contents not captured by default.
- **Export controls**: explicit UI-gated export with filtering options.

### 3.6 The Charon dashboard

The dashboard is a separate Node.js process that reads `traces.db` and renders a local web UI on `http://127.0.0.1:<port>`. The server binds to port 0 (OS-assigned ephemeral port), which atomically allocates an available port with no TOCTOU race. The assigned port is communicated back to Unity via a temp file (the `HADES_PORT_FILE` environment variable points to the file path; the server writes the port number on startup). Unity polls for this file at 200ms intervals with a 6-second timeout, then opens the user's browser to the URL.

**Phase 2 lesson:** The original design specified sequential port scanning (try 7878, 7879, etc.), which has a TOCTOU race condition and an arbitrary port range limitation. OS-assigned ports eliminate both problems. Port file IPC replaced stdout event parsing because Unity's `OutputDataReceived` events do not fire reliably in Unity's process context.

The dashboard is started on demand:

- A menu item in the Unity Editor: `Hades: Open Charon Dashboard`. This launches the Node.js process and opens the user's browser to the URL. A second menu item, `Hades: Stop Charon Dashboard`, terminates the process.

The dashboard is local-first and does not require an internet connection.

#### 3.6.1 What the dashboard shows

The main views (shipped in v1.0.0):

- **Traces**: filterable, sortable list. Default view shows recent traces with their status and duration. Filters: time range, status (OK/ERROR), trace name pattern. Paginated at 50 per page (cap 200).
- **Trace detail**: waterfall view of the trace's spans. Click any span to see its attributes.
- **Memory**: view of current Asphodel memory state.
- **Proposals**: view of pending `propose_memory_update` proposals awaiting review.

Planned (not yet in v1.0.0):

- **Aggregations**: latency distribution per tool, error rate per tool, throughput over time.
- **Eval datasets**: sets of traces for regression testing.
- **Settings**: retention policy, redaction options, export controls.

#### 3.6.2 The dashboard's tech stack

The dashboard is intentionally simple: a Node.js Express server reads SQLite via `better-sqlite3`, serves a Single-Page Application built with React (or, if simpler, plain JavaScript + a templating library). The SPA renders trace data via D3 or a similar visualization library.

The choice of Node.js and React is for ecosystem consistency with the MCP server (also Node.js). It also makes the dashboard easy for users to extend or fork if they want different visualizations.

#### 3.6.3 Performance

The dashboard performs reasonably on traces databases up to a few GB. Beyond that, query latency on the trace list view becomes noticeable. Mitigations:

- Aggressive indexing on `traces.start_time DESC`, `traces.status`, etc.
- Pagination by default (50 traces per page).
- Lazy loading of span detail (don't fetch span data until the user clicks into a trace).
- Optional pre-aggregated rollup tables for the aggregations view.

### 3.7 The eval framework

A subset of Charon's value is in the eval framework: using accumulated traces to validate that changes (to skills, prompts, or models) don't regress behavior.

#### 3.7.1 Datasets

> **Status: Planned — not yet implemented as of v1.0.0.** The eval dataset schema exists in the database but the LLM-as-judge and full dataset workflow described below are design targets. The actual record/replay tools (`hades_regression_record` / `hades_regression_replay`) use the schema for tool-level snapshot replay — see §3.7.2.

A dataset is a curated set of tool-level snapshots with expected outputs. Datasets are stored in the same `traces.db`:

```sql
CREATE TABLE eval_datasets (
  dataset_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT,
  created_at INTEGER NOT NULL
);

CREATE TABLE eval_dataset_members (
  dataset_id TEXT NOT NULL REFERENCES eval_datasets(dataset_id) ON DELETE CASCADE,
  trace_id TEXT REFERENCES traces(trace_id) ON DELETE SET NULL,
  tool_name TEXT NOT NULL,
  input_json TEXT NOT NULL,
  expected_output_json TEXT NOT NULL,
  notes TEXT,
  PRIMARY KEY (dataset_id, tool_name, input_json)
);
```

#### 3.7.2 Replays

A replay takes a dataset and re-runs the scenarios against current Hades configuration. **Realistically, full deterministic AI replay is not possible** due to multiple sources of non-determinism: model versions change, prompts evolve, tools change, agent reasoning is sampled. This is an industry-wide limitation, not specific to Hades.

What is possible:

- **Tool-level deterministic replay**: given a fixed graph state and memory state, calling the same Hades MCP tool with the same arguments produces the same result. This is testable and deterministic.
- **Agent-level differential replay**: given a stored trace, run the new system against the same project state and human-compare the new behavior to the old. This is manual inspection, not automated verification.
- **Statistical regression detection**: across many replayed scenarios, aggregate metrics (acceptance rate, error rate, latency) reveal regressions that are not visible in any single replay.

The eval framework therefore positions itself primarily as:

1. **A tool-level test runner** — verifies that the deterministic parts (graph queries, memory reads, tool dispatches) produce expected outputs against curated inputs.
2. **A trace inspection workbench** — supports human-driven analysis of stored traces. Categorize, annotate, compare. The analyst is the final judge.
3. **A statistical aggregator** — reveals trends across hundreds of traces that no individual trace can show.

What it is not:

- A regression test suite that can automatically green/red an agent's behavior change.
- A way to "prove" a new prompt is better than an old one — only suggest it.
- A replacement for human judgment about whether an agent response is good.

This realistic framing shapes what we build. We invest in good annotation tooling and aggregation views, not in elaborate replay infrastructure that promises more than it can deliver.

#### 3.7.3 LLM-as-judge

> **Status: Planned — not yet implemented as of v1.0.0.** The behavior described here is a design target; the current build does not implement it.

For certain trace types (suggestion-and-outcome traces), we plan to use a separate LLM call to judge whether the agent's suggestion was good. This would require:

- Sending the trace context (prompts, responses, project structure summary) to a separate LLM endpoint.
- Receiving a structured judgment.
- Storing the judgment in the trace's attributes.

This feature would be opt-in because it sends data to an external LLM. When enabled, the user would configure which LLM endpoint to use (their own Claude API key, or another provider).

### 3.8 Internal use during Hades development

Because Charon is built first as an engineering necessity, the way we use it during Hades development matters. Specifically:

- Every Hades developer has Charon enabled at all times during development.
- When a bug is reported, the first ask is "share the traces from when this happened."
- We maintain an internal eval dataset of "canonical Unity scenarios" against which we replay all skill changes and graph-builder changes.
- Performance regressions are caught by aggregations across the dataset (latency at the 95th percentile, error rate, etc.).

This usage pattern shapes the dashboard design: features that are useful for engineering debugging — span detail with timestamps, error stack traces, attribute search — are prioritized over features that are pretty but less useful operationally.

### 3.9 Storage and retention

Traces grow. A heavy-use day might produce 10,000 spans, totaling tens of MB. Over weeks, the database can reach hundreds of MB.

Retention defaults:

- 30 days of traces by default. Older are auto-pruned at Unity startup via `PruneOlderThan`.
- Eval-dataset traces are exempt from auto-pruning regardless of age.
- **Trace-count cap at startup** (a row budget derived from `CharonMaxSizeMb` in EditorPrefs — default 500MB ≈ 128k traces at ~4 KB each, floored at 5000): `PruneToTraceCap` deletes the oldest traces beyond the budget and runs a cheap `PRAGMA wal_checkpoint(PASSIVE)`. It deliberately does **not** `VACUUM` — freed pages are reused by subsequent inserts so the file plateaus, and a synchronous `VACUUM` of a multi-GB `traces.db` could freeze editor startup. (This replaced the earlier size-based `EnforceSizeLimit`, which trimmed to ~90% of the byte cap and then ran `wal_checkpoint(TRUNCATE)` + `VACUUM`.)
- *(Planned — not yet implemented as of v1.0.0.)* A `hades-charon prune` CLI for manual pruning outside of Unity startup.

### 3.10 Edge cases

- **Trace explosion under bursty load**: the agent makes 1000 tool calls in a session. The buffer (`ConcurrentQueue`) absorbs bursts without blocking callers. The flush task drains on a 500ms timer or when 1000 spans accumulate. No backpressure to callers is applied in the current implementation.
- **Concurrent emitters from multiple Unity processes**: a common scenario — the user has multiple Unity instances open on different projects simultaneously. Each instance writes to its own per-project `traces.db` (per §1.8). There is no shared trace database, so SQLite's single-writer constraint is automatically satisfied within each project. Resource contention (CPU, disk I/O) scales with the number of concurrent instances but does not affect correctness.
- **Disk full**: if the trace database can't be written, the emitter logs the error and continues. There is no degraded "drop traces" mode or counter in the current implementation — disk-full conditions are surfaced via Unity console logs only. The hard 500MB size cap (§3.9) is the main guard against disk exhaustion. *(A formal drop-traces mode after repeated failures is planned — not yet implemented as of v1.0.0.)*
- **Clock skew**: timestamps come from the system clock. On most modern systems, NTP keeps this accurate. If the clock jumps backwards, span ordering can become incorrect. This is acceptable degradation, not a hard failure.


---

## 4. Hades Asphodel

Asphodel is the memory layer. It captures architectural decisions, established patterns, learned preferences, and historical context — the things that make a project a project, distinct from generic Unity development.

### 4.1 Design philosophy

Three design principles dominate Asphodel:

**Markdown is the storage format, not just the export format.** The decision to use markdown for memory is not a presentation choice; it is a structural choice. Memory files are the canonical representation. They are read directly by tools, written directly when proposing changes, and viewed directly by humans. There is no separate database that markdown is rendered from. This is the opposite of how most "memory systems" are built (vector stores with markdown as a presentation layer). The reasoning was made explicit in research: when an agent works on something for a long time, file-based memory often outperforms vector retrieval because the agent can read the entire memory file each session without complex retrieval infrastructure.

**Memory must self-validate.** Stale memory is worse than no memory. The system continuously checks memory claims against the graph: "patterns.md says we use ScriptableObject event channels — does the graph confirm this?" When inconsistencies are detected, they are surfaced rather than silently allowed.

**Asphodel mirrors decisions, it does not judge them.** This principle is critical and easy to overlook. If the team has a "bad" architectural pattern, Asphodel records it as a fact about the project. It does not flag it as bad. It does not refuse to capture it. It does not apply Hades's opinions about what good Unity architecture looks like. That is the role of the Skills layer — and even there, Skills inform the agent's recommendations rather than overriding the project's recorded decisions.

The neutrality principle has practical consequences. The agent reading `patterns.md` does not interpret entries as endorsed by anyone other than the team. If a pattern is technically suboptimal, that is the team's choice. The agent's job is to operate within the team's chosen patterns, not to argue against them. If the team wants to change patterns, that is a decision they make and record themselves. Hades is the recorder, not the architect.

This is what separates Asphodel from "AI architect" tools. Those tools have opinions about correct architecture and try to enforce them. Asphodel has no opinions. It is project-context infrastructure, not architecture-coaching infrastructure.

### 4.2 Tier 1 vs Tier 2

The memory system has two tiers, distinguished by who writes them and how they are versioned.

#### 4.2.1 Tier 1: Explicit memory

Tier 1 is human-curated. Files are written by:

- The developer directly editing in their text editor (markdown, can be edited anywhere).
- The agent proposing updates via MCP tool, with the developer reviewing and accepting.

Tier 1 files are git-tracked. They live alongside the codebase and travel with the project across team members. When a new developer joins, `git pull` brings the team's accumulated memory into their environment.

Files in Tier 1:

- `decisions.md` — architectural decisions and their context. Each entry: date, decision, alternatives considered, rationale.
- `patterns.md` — established patterns the project uses. Each entry: pattern name, description, examples (file paths in the project), enforcement (whether new code should use this).
- `conventions.md` — naming, structure, file organization conventions.
- `pitfalls.md` — known traps, historical bug patterns, things to avoid.
- `glossary.md` — domain-specific terminology used in the project.
- `intent.md` — the team's current focus, what they are working on, what they care about.

Each file follows a structured markdown format with YAML frontmatter for metadata:

```yaml
---
last_reviewed: 2026-05-09
last_validated_against_graph: 2026-05-09T10:30:00
validation_status: ok
---

# Architectural Decisions

### Vendored gilzoide/unity-sqlite-net for Graph Database Access

**Date:** 2026-05-11
**Status:** Active (supersedes previous Mono.Data.Sqlite approach)
**Scope:** Hades Graph (Section 2)

GraphDatabase.cs uses [gilzoide/unity-sqlite-net](https://github.com/gilzoide/unity-sqlite-net) (v1.3.2) — a Unity-specific port of praeclarum/sqlite-net that P/Invokes directly into platform-native sqlite3 binaries. The entire package is vendored into `ThirdParty/unity-sqlite-net/` for self-contained UPM distribution with no external dependencies.

**Key API patterns:**
- `SQLiteConnection` (auto-opens on construction, plain path — no URI prefix)
- `Execute(sql, args)` and `ExecuteScalar<T>(sql, args)` with positional `?` params
- `SQLitePreparedStatement` for complex reads (`Bind()`/`Step()`/`GetString()`/`GetLong()`)
- `ExecuteScript()` for multi-statement DDL
- `RunInTransaction(Action)` for transactional writes

**Previous approach (Mono.Data.Sqlite) — why it was replaced:** The bundled `Mono.Data.Sqlite.dll` and `System.Data.dll` were reference assemblies (stubs with empty method bodies) from Unity's `MonoBleedingEdge/lib/mono/unity/` directory. These compiled successfully but threw `InvalidProgramException` at runtime, causing all 55 database-touching tests to fail.

**Alternatives considered:**
- Mono.Data.Sqlite bundled DLLs (rejected: reference assembly stubs cause runtime InvalidProgramException)
- Microsoft.Data.Sqlite via NuGet (rejected: SQLitePCLRaw.Batteries_V2.Init() cannot find native binaries in Unity's Mono runtime)
- SQLite4Unity3d (rejected: unmaintained, outdated sqlite3 binaries)
- External UPM dependency on gilzoide/unity-sqlite-net (rejected: adds external dependency for consumers)

**Rationale:** gilzoide/unity-sqlite-net provides platform-native sqlite3 binaries (macOS, Windows, Linux, Android) with a clean C# API that still allows raw SQL for PRAGMAs, schema control, and complex graph queries. Vendoring ensures Hades remains a self-contained UPM package.

**Maintenance:** The vendored source lives in `ThirdParty/unity-sqlite-net/`. To update, clone the upstream repo, copy updated files, and verify license compliance. `ATTRIBUTION.md` in the vendored directory documents the source commit and licenses (MIT for sqlite-net, public domain for sqlite3).

### MCP Hub Architecture

**Date:** 2026-05-13 (supersedes "MCP Auto-Discovery with Standby Bridge" from 2026-05-11)
**Status:** Active
**Scope:** MCP Transport (Section 1.5)

Agent clients (Claude Code, Claude Desktop) connect to Unity's MCP server through a three-component architecture: Launcher → Hub → Unity Instance(s). This replaced the earlier bridge-based discovery model which had three known failure modes (server entry lost during compilation, `.mcp.json` scoped to wrong directory, `.mcp.json` not found from repo root).

The full architecture is documented in the **Plugin document** (`Documentation/arcforge-hades-plugin.md`, §3) and the **MCP Hub design spec** (`docs/superpowers/specs/2026-05-13-mcp-hub-design.md`). Key properties:

1. **Hub** — long-running Node.js HTTP server, one per machine. Maintains a registry of Unity instances, routes tool calls by project path matching, monitors instance health via heartbeats, buffers requests during domain reloads.
2. **Launcher** — thin stdio process that starts the Hub on demand and bridges stdio to HTTP. Zero npm dependencies. Two connectivity paths reach it:
   - **Plugin mode** (`--plugin-dir` for local installs; `/plugin install` for marketplace installs): the plugin's `.mcp.json` uses `${CLAUDE_PLUGIN_ROOT}/Bridge~/launcher/dist/index.js`
   - **Project auto-discovery**: `MCPClientConfig` writes a `.mcp.json` to the Unity project root pointing to `~/.arcforge/hades-hub/launcher.js` (the stable installed copy)

   Both paths launch the same launcher binary, which connects to the shared Hub.
3. **Unity registration** — Unity instances register/deregister with the Hub via HTTP instead of writing discovery files. Heartbeat every 30s. Breadcrumb files for when the Hub is offline.

**Key properties:**
- Order-independent startup (Claude Code before Unity: tools appear when Unity registers)
- Directory-independent (Hub routes by path matching, not by where `.mcp.json` lives)
- Resilient to compilation failures (Hub probes Unity's HTTP endpoint before marking stale)
- Zero npm runtime dependencies (both Hub and Launcher use only Node.js built-ins)
- Claude Desktop support via stable launcher copy at `~/.arcforge/hades-hub/launcher.js` with `hub-path.json` pointer to hub entry point

**v1.1 reliability hardening:**
- **Forgiving path matching** — the Hub canonicalizes registered and requested project paths (`realpath` + case-fold) before comparing; the Launcher resolves the real Unity project root by walking up from its cwd; and a **single-instance fallback** routes an otherwise-unidentifiable call (e.g. a launcher whose cwd resolved to `/`) when exactly one Unity is registered. This eliminated a class of post-reload "No Unity instance found for …" errors where the registered and requested paths were equivalent but not byte-identical.
- **Leak-proof auto-exit** — the Hub exits when idle based on the **time since last launcher activity**, not a launcher *count*. The count it previously used decremented only on an explicit disconnect — which an abruptly-killed launcher (editor restart, crash, sub-agent teardown) never sends — so it leaked and the Hub became "immortal": running for days, serving stale code, never picking up a new build. Time-based liveness lets a fresh build deploy on the next call once the old Hub idles out.
- **Exclusive spawn lock** — racing launchers acquire an `O_EXCL` lock (with stale-lock recovery) before starting the Hub, so only one wins; the losers wait for `hub.json` instead of spawning duplicate/zombie hubs.
- **Clean error forwarding** — forwarding a tool call to a Unity instance that has just begun a domain reload returns a retryable JSON-RPC error, not a raw `HTTP 500`.

**Runtime dependency:** Node.js is required for MCP connectivity (both Claude Code and Claude Desktop route through the Hub).

### SO Event Channels for Inter-System Communication

**Date:** 2026-01-15
**Status:** Active
**Scope:** Global

We chose ScriptableObject event channels over UnityEvents and direct references for inter-system communication. Each event channel is a SO asset that systems can subscribe to and raise events on.

**Alternatives considered:**
- UnityEvents (rejected: tight coupling between sender and receiver inspector references)
- Direct delegate-based events (rejected: no inspector visibility, harder to debug)
- Third-party event bus library (rejected: prefer fewer dependencies)

**Rationale:** SOs decouple sender from receiver, are inspector-visible, and survive scene loads naturally.

**Examples:**
- `Assets/Events/PlayerHealthChanged.asset`
- `Assets/Events/InventoryUpdated.asset`
- `Assets/Events/LevelLoaded.asset`

**Enforcement:** All new inter-system communication must use this pattern. Existing UnityEvents in legacy code should be migrated when touched.

### ProcessResolver for Cross-Platform Executable Resolution

**Date:** 2026-05-12
**Status:** Active
**Scope:** Editor infrastructure (used by Charon Dashboard, potentially all external process invocations)

`ProcessResolver.cs` provides four capabilities: (1) resolving the full path of an executable by name across platforms, (2) running synchronous commands with deadlock-safe I/O handling, (3) injecting a child `PATH` that includes the resolved executable's directory (`ApplyChildPath`), and (4) `NativeBuildEnv` which exposes C++20 build environment settings for Node 25 native addon builds.

**The problem:** Unity's `Process.Start` does not inherit the user's login shell PATH. On macOS/Linux, Node.js installed via nvm/fnm/Homebrew is invisible to Unity. On Windows, `where.exe` resolves differently than Unix `which`. Passing just `"node"` to `Process.Start` fails with "Cannot find the specified file."

**The solution:**
- `FindExecutable(string name)` — resolves via `$SHELL -lc "which {name}"` on macOS/Linux (uses the user's actual login shell from the `$SHELL` environment variable; falls back to `/bin/bash` if `$SHELL` is unset, non-existent, or not a POSIX shell), `cmd.exe /c where {name}` on Windows. Results cached per session in a static dictionary.
- `Run(string executable, string arguments, string workingDirectory, int timeoutMs)` — runs a resolved executable synchronously. Reads stderr asynchronously (`ReadToEndAsync()`) while reading stdout synchronously to prevent pipe buffer deadlock. Kills process on timeout.

**Key design decisions:**
- Login shell (`-lc`) is deliberate: non-login shells don't source `.bash_profile`/`.zprofile`/`.zshrc` where nvm/fnm inject their PATH modifications. Using `$SHELL` (often zsh on macOS) ensures the user's actual shell profile is sourced.
- Per-session caching (static dictionary) is appropriate because executable locations don't change within a Unity session. Cache is lost on domain reload (static field reset), which is acceptable.
- Stderr read is async to prevent deadlock when both stdout and stderr have data. This is a well-known .NET process I/O pattern.

**Alternatives considered:**
- Hardcoded paths (rejected: not portable across systems)
- Environment variable for node path (rejected: additional user configuration burden)
- Async process execution (rejected: unnecessary complexity for short-lived commands; long-lived processes like the dashboard don't redirect I/O)

**Maintenance:** `ProcessResolver` is used by `CharonDashboard.cs` and `EnsureDashboardBuilt()`. Any future external tool invocations from Unity Editor code should use `ProcessResolver` rather than raw `Process.Start`.

### OS-Assigned Port with Port File IPC for Dashboard

**Date:** 2026-05-12
**Status:** Active (supersedes sequential port scanning approach described in original §3.6)
**Scope:** Charon Dashboard (Section 3.6)

The dashboard server binds to port 0 (`app.listen(0, "127.0.0.1", ...)`), letting the OS atomically assign an available ephemeral port. The assigned port is communicated to Unity via a temp file: Unity sets the `HADES_PORT_FILE` environment variable to a unique temp path before launching the server; the server writes the port number to that file on startup; Unity polls for the file at 200ms intervals (30 attempts, 6-second timeout).

**Previous approach — why it was replaced:** The original design specified sequential port scanning (try 7878, 7879, etc.). This has two problems: (a) TOCTOU race — between checking port availability and binding, another process can claim it; (b) arbitrary range limitation — only ports 7878-7888 were tried, which is fragile. The original design also specified stdout event parsing (`OutputDataReceived`) for port communication, which does not fire reliably in Unity's process context.

**Key properties:**
- No TOCTOU race: OS port assignment is atomic.
- No arbitrary port range: any available ephemeral port works.
- No stdout/stderr redirection for the dashboard process: avoids pipe buffer issues with long-lived processes.
- Port file is cleaned up after reading (one-shot communication).
- PID is stored in `SessionState` for domain reload resilience (see §8.3.3).

**Alternatives considered:**
- Sequential port scanning (rejected: TOCTOU race, arbitrary range)
- stdout parsing via `OutputDataReceived` (rejected: unreliable in Unity's process context)
- Named pipes / Unix domain sockets (rejected: platform-specific complexity for a one-shot port number)
- Fixed port (rejected: prevents multi-instance per §1.8)
```

#### 4.2.2 Tier 2: Inferred memory

Tier 2 is auto-generated from observability traces. It captures patterns the system observes across sessions:

- "User accepted suggestions matching pattern X 47 times, rejected 3 times. Confidence 94%."
- "Suggestions involving Resources.Load were rejected 89% of the time. The project appears to use Addressables instead."
- "User mentions performance optimization in 30% of requests."

Tier 2 files live at `.arcforge/memory/inferred/` and are git-tracked alongside Tier 1 memory (`.gitignore` un-ignores `.arcforge/memory/`; the maintainer keeps inferred files committed). Teams that do not want behavioral inference in their repository can gitignore `inferred/` explicitly.

When confidence in an inferred pattern is high (configurable threshold, default 90% over a minimum sample size), the system can promote the pattern to Tier 1 — but always with developer review. The promotion is not automatic; it appears as a suggestion in a queue, the developer either approves (in which case the pattern is added to `patterns.md`) or dismisses.

### 4.3 The memory writer

Asphodel writes are gated. There are several paths through which memory gets written, each with different controls:

#### 4.3.1 Direct human edit

The developer opens `patterns.md` in their text editor, edits, saves. Asphodel notices the file change (via FileSystemWatcher) and updates the validation status. Direct edits are unrestricted — Asphodel does not impose schema validation that would prevent the developer from writing what they want.

#### 4.3.2 Agent proposal

The agent calls the MCP tool `propose_memory_update(file: string, content: string, rationale: string)`. The proposal does not modify the file directly; it is added to a "pending proposals" queue at `.arcforge/memory/proposals/`. The developer reviews proposals in the Charon dashboard or via a CLI command and approves or rejects each.

This design ensures the agent cannot silently rewrite the project's memory. Human review is mandatory.

#### 4.3.3 Inferred update

The Tier 2 system writes to inferred files automatically based on trace analysis. These updates are unrestricted because they are scoped to Tier 2 (auto-generated and clearly labeled as inferred). They are clearly labeled as inferred so a human reading the file knows the data is statistical.

Promotion from Tier 2 to Tier 1 goes through the proposal queue, same as agent proposals.

### 4.4 The memory reader

Memory is read in two patterns:

#### 4.4.1 Pre-injection at session start

When the agent client starts a new session, it reads a small summary of memory and injects it into the system prompt. The summary is generated by the MCP tool `get_memory_summary()`:

- Top decisions from `decisions.md` (most recent or most critical).
- Top patterns from `patterns.md`.
- Active intent from `intent.md`.
- Salient conventions from `conventions.md`.

The summary is intentionally brief — typically a few hundred tokens. It is meant to give the agent enough context that it can ask the right follow-up questions, not to dump the entire memory state.

#### 4.4.2 On-demand retrieval

When the agent needs more detail, it calls the MCP tool `recall_memory(query: string)`. This returns relevant memory file content (or specific sections) based on the query. The implementation is initially simple: keyword matching against memory file content. A future iteration may use semantic search.

This pattern matches modern AI agent practices: minimal upfront context, just-in-time retrieval. It avoids burning tokens on memory the current task doesn't need.

### 4.5 Self-validation

The most consequential mechanism in Asphodel is self-validation against the graph. Without it, memory drifts and becomes a liability.

#### 4.5.1 Who performs validation

This is the most important point of this section: **validation is performed by C# code inside the Hades Unity Package, not by the agent.** The distinction is critical because misunderstanding it leads to wrong assumptions about how the system behaves.

The roles are strictly separated:

- **C# code in the Hades Unity Package** parses validation rules from memory frontmatter, executes graph queries, compares results against expected outcomes, and writes results back into the memory file (frontmatter status + inline HTML comments). This is automatic, deterministic, and does not involve any AI.

- **The agent (Claude Code, etc.) does NOT perform validation.** When the agent reads memory through MCP tools, it receives memory content with validation results already embedded. The agent consumes the validation output; it does not produce it.

This separation matters for three reasons. First, it makes validation deterministic — the same project state always produces the same validation result, which is impossible if an LLM were doing the comparison. Second, it makes validation cheap — running queries in C# costs no tokens, while LLM-based validation would burn agent tokens on every check. Third, it makes validation always-current — automatic background runs keep memory validated whether or not the agent is active.

#### 4.5.2 The validation rules

Each memory entry can have associated validation rules. The rules are encoded as **inline `<!-- hades-validation … -->` HTML comments** in the markdown body (not YAML frontmatter — `ValidationRuleParser.cs` parses these comment blocks):

```markdown
<!-- hades-validation
query_type: exists
query: search_by_name(*Channel, ScriptableObject)
min_count: 3
failure_message: Pattern claims SO event channels are used but found fewer than 3 in the project.
-->
```

Supported query types dispatch to real graph tools (`search_by_name`, `find_nodes_by_type`). Object-literal syntax (e.g. `find_assets({…})`) is not supported.

When the C# validator runs:

1. Reads the memory file body and parses `<!-- hades-validation -->` comment blocks.
2. For each rule with a query, the query is executed against the current graph state.
3. The result is compared against `min_count`.
4. The validator updates the file's frontmatter (`validation_status: ok | warning | error`) and timestamp.
5. On mismatch, an inline HTML comment block is appended to the file describing the inconsistency.

The result is that the memory file itself becomes the authoritative record of validation state. Anyone reading the file — human or agent — sees the current validation status without any additional lookup.

Memory entries without validation rules are simply not validated. Validation is opt-in per-entry. This is correct: not all memory can be expressed as graph queries (preferences, philosophies, judgment-based norms), and forcing validation on everything would either fail or produce false signals.

#### 4.5.3 When validation runs

Three triggers, all initiated by C# code, all in the background:

- **On startup**: every memory file is validated when Unity opens. Stale claims are surfaced through frontmatter status updates. The user sees them next time they read the file or via the dashboard.
- **On significant graph change**: when a graph rebuild or large incremental update completes, affected memory rules are re-validated. "Affected" is determined by analyzing which queries touch the asset types that changed.
- **On demand**: the developer can run `Hades: Validate Memory` from the menu to force a full re-check.

All three run as C# operations in the Unity Package's main thread. None require the agent client to be connected.

#### 4.5.4 What happens on validation failure

A validation failure does not delete the memory entry. It marks it as `validation_status: warning` in the file's frontmatter and adds a comment block describing the inconsistency:

```markdown
<!-- 
HADES VALIDATION WARNING (2026-05-09):
This pattern claims SO event channels are used but the graph contains 
0 ScriptableObjects matching the pattern '*Channel'. 

Possible explanations:
- The pattern was removed from the project; consider updating this file.
- The pattern uses a different naming convention; update the validation rule.
- This is a new project that hasn't yet implemented this pattern.
-->
```

The agent reads these comments and uses them as context. If the agent is about to recommend the SO event channel pattern, but the validation warning shows it's not present, the agent can either suggest the pattern with caveats ("this is in your memory but not yet implemented in your code") or seek clarification.

### 4.6 Pattern detection (Tier 2 generation)

The Tier 2 inferred memory is produced by a background task that runs periodically against the trace database. The task looks for patterns:

- **Acceptance rate by suggestion shape**: classify suggestions by structural fingerprint (uses pattern X, modifies file Y, etc.) and compute acceptance rate per class.
- **Frequent topic identification**: cluster trace user inputs by topic and surface the most common.
- **Time-of-day patterns**: when does the user work, on what kinds of problems?
- **Failure correlations**: when suggestions fail (rejected or edited), what features are correlated?

These patterns are written to `inferred/observed_patterns.md`, `inferred/preferences.md`, etc. The task runs on every graph rebuild (triggered by `GraphBuilder.OnRebuildComplete`) — there is no daily timer or rate limiter in the current implementation.

> **Two bugs kept this loop dead until v1.1, both now fixed.** (1) The inference engine is constructed from `CharonEmitter.Database`, which Asphodel read *once* at init — and under the old undefined `[InitializeOnLoad]` order it usually ran before Charon had set that database, leaving the engine permanently null. The ordered bootstrap (§1.7) now initializes Charon before Asphodel. (2) The analyzers keyed on the `tool_name` span attribute, but the emitter wrote `tool.name`, so every analyzer's `ContainsKey("tool_name")` guard was always false and produced zero patterns. Emitter and analyzers now share the `SpanAttributes` constant (§3.4.1). With both fixed, the Charon→Asphodel inference loop produces patterns for the first time — the synthetic test fixtures (which had used the underscore key) were re-pointed at the production constant so the contract can't silently regress.

#### 4.6.1 Inference labeling discipline

A critical design constraint: **inferred patterns are never injected into agent context as authoritative**. They are clearly marked at every stage of their lifecycle.

In the markdown files themselves, every inferred entry has frontmatter and inline labeling:

```yaml
---
status: inferred
analyzer: AcceptanceRateAnalyzer
confidence: 0.93
sample_size: 67
first_observed: 2026-04-15
last_confirmed: 2026-05-08
promotion_status: pending  # pending | proposed | accepted | dismissed | deferred
---

INFERRED PATTERN (not confirmed by team)

**Apparent preference for minimal refactoring scope**

This pattern is inferred from observed user behavior and has NOT been 
confirmed as a team decision. The user has rejected suggestions involving 
large refactoring scope 47 of 50 times observed. This may indicate a 
preference, or may reflect the specific situations where those suggestions 
arose.

[evidence trace IDs...]
```

When the agent reads inferred memory, the labeling is preserved in the response. The agent treats inferred patterns as **observational hypotheses**, not **stated preferences**. A skill calibrated to use inferred memory does so with caveats: "I noticed you tend to prefer X — should I apply that here, or is this case different?"

This is meaningfully different from how Tier 1 memory is treated. Tier 1 entries are facts about the team's decisions (or at least, claims the team has made about itself). Tier 2 entries are statistical observations that may or may not reflect real preferences. Conflating them turns the system from helpful to creepy.

#### 4.6.2 The promotion lifecycle

When a Tier 2 entry's confidence and sample size cross thresholds (defaults: 90% confidence, 50 samples), the system creates a **promotion proposal** in the proposal queue. The proposal asks the user: "I've observed pattern X consistently. Should I add it to your patterns.md as a recorded team preference?"

The user's options (matching `PromotionStatus` enum values):

- **Accept** (`Accepted`): pattern moves to Tier 1 with explicit confirmation. The Tier 2 entry is archived.
- **Modify and accept**: user edits the proposed text before accepting. The status becomes `Accepted`.
- **Dismiss** (`Dismissed`): the pattern is marked dismissed in Tier 2. The system stops proposing it but keeps observing (in case the underlying behavior changes).
- **Defer** (`Deferred`): review later. The pattern stays in Tier 2 with a cooldown before re-proposing.

Initial state is `Pending`; moves to `Proposed` when surfaced to the user.

The promotion is never automatic. This is intentional. Inference is suggestion, not authority. The user holds the final word on what enters Tier 1.

The pattern-detection logic is open-source and inspectable. Users who don't trust the inferences can disable Tier 2 entirely; Hades remains useful with Tier 1 only.

#### 4.6.3 Graph-grounded convention inference (v1.2)

The pattern detection above reads **Charon traces** — it needs behavior to accumulate across sessions before it can say anything, and its acceptance-rate signal depends on outcome capture that is still thin. v1.2 adds a **second, independent Tier 2 producer** that sidesteps that entirely: `ConventionInferrer` (`Editor/Asphodel/Conventions/`) reads the **knowledge graph's structure** rather than traces. The graph already knows a project uses ScriptableObject event channels, Addressables, prefab variants, a naming pattern, or URP — so those conventions can be read straight off it, deterministically, from a single scan.

It is a sibling to `PatternInferenceEngine`: constructed in `AsphodeInitializer.Initialize()` alongside it, and invoked from the same `OnGraphRebuild` handler — but on its **own 60-second throttle** (`Hades_LastConventionTicks`), separate from the 30-minute trace-inference throttle, because a cheap read-only graph pass should keep conventions current without waiting half an hour.

**Six deterministic detectors** (`IConventionDetector`), each a graph query plus a prevalence→confidence heuristic returning `{Fired, Statement, Evidence, Confidence, TargetFile}`:

| Key | Signal | Tier-1 target |
|---|---|---|
| `event_channels` | `ScriptableObject`s whose `so_type` ends in `Channel`/`Event`, referenced by components via `references` edges (≥2 types, ≥3 referenced) | `patterns` |
| `asset_loading` | `AddressableGroup`/`AddressableEntry` node volume (≥10 entries) | `conventions` |
| `prefab_variants` | `PrefabVariant`:`Prefab` node ratio (>20%, ≥5 total) | `patterns` |
| `so_config` | `ScriptableObject` instances grouped by `so_type`, channels excluded (≥10 across ≥3 types) | `patterns` |
| `naming` | trailing-CamelCase-token buckets over **project-tier** `ScriptType` names (any suffix on ≥5 types) | `conventions` |
| `render_pipeline` | a `RenderPipelineAsset`'s `pipeline_type` (URP/HDRP); absent → built-in RP, does not fire | `conventions` |

**Self-validation — the defining property.** Each run reconciles `inferred/convention-{key}.md`: written when a detector fires, **deleted when it does not**. A convention is therefore re-derived from the current graph on every rebuild and cannot go stale — switch a project off SO channels and the entry retracts itself on the next scan. This is the one guarantee the trace-based analyzers cannot make.

**Reuses the promotion lifecycle (§4.6.2), with one difference.** Each fired convention becomes a promotion proposal through the same `.arcforge/memory/proposals/` queue the dashboard and `/hades:show-proposals` already read (via `MemoryManager.CreateProposal`, with a stable id `convention-{key}` so repeated rebuilds don't flood the queue). Unlike trace patterns, conventions do **not** wait on a 90%/50-sample threshold — a structural convention is evident from a single scan, so it is proposed immediately. Accept still writes to Tier 1 (`patterns.md`/`conventions.md`) carrying a stable `<!-- hades-convention:{key} -->` marker; promotion is never automatic.

**Dismissal memory.** Because a dashboard *reject* simply deletes the proposal file, "Dismiss → stop proposing" needs state the proposal store can't hold. A small C#-owned ledger `inferred/.conventions-state.json` (dot-prefixed, so the dashboard's `*.md` listing ignores it) records each key's lifecycle: a previously-pending proposal whose file is gone resolves to **promoted** (its marker is now in a Tier-1 file) or **dismissed** (marker absent). A dismissed convention is not re-proposed unless its confidence later rises by ≥0.2. And a **promoted convention that stops firing** emits a one-time `convention-stale-{key}` removal proposal — closing the self-validation loop even for confirmed entries.

**Known limitation — `Resources.Load` is invisible to the graph.** The scanners capture Addressables as nodes but do not capture runtime string-based `Resources.Load("path")` calls at all (no node, no edge). The `asset_loading` detector therefore reports **Addressables adoption only**; it cannot contrast Addressables-vs-Resources. Capturing Resources usage would require new scanning and is out of scope for v1.2.

### 4.7 Privacy and data handling

Memory in Tier 1 is git-tracked and shared with the team. The developer has full control over what goes in (directly via text editing or via approving proposals).

Memory in Tier 2 is git-tracked alongside Tier 1 (see §4.2.2). It contains aggregate behavioral data — what kinds of things the user accepts and rejects. This is sensitive in some senses (it reflects working habits) and innocuous in others (it's anonymized and statistical). Teams that do not want inferred behavioral data in their repository can add `.arcforge/memory/inferred/` to their project's `.gitignore`.

The user can:

- Disable Tier 2 entirely.
- Inspect Tier 2 files directly (they are markdown).
- Delete Tier 2 files at any time; they will regenerate from new traces.
- Export Tier 2 (e.g., to share with us for debugging).

### 4.8 Edge cases

- **Memory file deleted while running**: Asphodel detects the deletion (via `FileSystemWatcher`) and treats it as "this memory no longer exists." The deletion is logged; no active notification to the agent occurs — the file simply stops appearing in summaries and recall results. *(Active agent notification is planned — not yet implemented as of v1.0.0.)*
- **Memory file with invalid frontmatter**: parser logs a warning and skips that file. The rest of the system continues.
- **Conflicting rules between memory entries**: e.g., one entry says "use Pattern X," another says "Pattern X is deprecated." Asphodel does not resolve these automatically; both are surfaced to the agent, which then has to reason about the conflict (or asks the user).
- **Memory grows unboundedly**: no size check is enforced in the current implementation. A soft 50KB size limit with warning is planned but not yet implemented. *(Planned — not yet implemented as of v1.0.0.)*
- **Multi-developer memory churn**: two developers edit the same memory file in different branches, then merge. Standard git merge conflicts apply. Asphodel does not have its own conflict resolution; it relies on git's.
- **Validation queries become slow**: as the graph grows, some validation queries may become expensive. Validation has a per-query budget (default 1 second). Queries exceeding the budget are skipped with a warning.


---

## 5. Hades Skills

The Skills layer is the conceptually simplest of the four pillars but the one with the most direct user-visible impact. Where Graph, Charon, and Asphodel work behind the scenes, Skills are what the agent actively pulls from when reasoning about Unity-specific tasks.

### 5.1 What a skill is, technically

A skill, in Claude Code's terminology, is a markdown file with a specific structure:

```yaml
---
description: "Use when the user asks architectural questions about Unity projects: how should I structure X, what's the best way to handle Y, decisions about MonoBehaviours vs ScriptableObjects vs static classes."
---

# Unity Architect Skill

[markdown content with decision frameworks, examples, etc.]
```

The frontmatter declares when the skill should activate (the `description` is matched against the agent's current task). The skill name comes from the directory name, not the frontmatter. An optional `disable-model-invocation: true` field prevents automatic activation — useful for skills that should only run when the user explicitly invokes them. The body is markdown content that the agent reads when the skill activates.

Skills are loaded into the agent's context when their description matches the current task. This is automatic — the agent client's plugin system handles activation based on the description match.

### 5.2 Skill organization

Hades ships skills in tiers of specificity:

#### 5.2.1 Architecture decision skills

The high-level decision frameworks. These activate for "how should I X" type questions:

- `unity-architect` — top-level routing skill. Decides whether the question is about components, data modeling, scene architecture, prefab strategy, or performance, and routes to the appropriate sub-skill.
- `component-design` — when to use MonoBehaviour, ScriptableObject, plain C# class, static class. Composition vs inheritance. Component lifecycle.
- `data-modeling` — modeling project data: when to use SOs vs JSON vs runtime-only structures. Save/load implications.
- `scene-architecture` — bootstrap scenes, additive loading, scene management, single vs multi-scene setups.
- `prefab-architecture` — prefab vs prefab variant decisions, nested prefabs, override strategies.
- `unity-performance` — profiling-first approach, common bottlenecks, when to optimize, what tools to use.

#### 5.2.2 Workflow skills

These activate for procedural tasks:

- `scene-authoring` — how to create and modify scenes via the agent.
- `prefab-workflow` — creating, editing, instantiating prefabs.
- `animation-workflow` — Animator Controller, Animation, AnimationClip relationships.
- `unity-workflow` — general Unity Editor workflow patterns and automation.

#### 5.2.3 Domain skills (planned expansion)

The domain skills covering the gaps UniClaude lacked (implemented in Phase 4):

- `unity-ui` — UI Toolkit, uGUI, responsive layouts, dialog systems.
- `unity-networking` — Netcode for GameObjects, Mirror, Fishnet decision frameworks.
- `unity-ai-behavior` — state machines, behavior trees, GOAP, NavMesh.
- `unity-audio` — audio manager patterns, mixers, spatial audio.
- `unity-input` — new Input System, action maps, multi-device.
- `unity-shaders-urp` — URP-specific Shader Graph, render features.
- `unity-shaders-hdrp` — HDRP-specific Shader Graph, custom passes.
- `unity-vfx` — VFX Graph, particle systems.
- `unity-addressables` — Addressables vs Resources vs AssetBundles, async loading.
- `unity-ecs` — when to use ECS, Burst, hybrid approaches.
- `unity-testing` — EditMode vs PlayMode tests, what to test, mocking.

**Deferred to Phase 8+ (recipe skills — added based on user demand):**
- `unity-recipes` — common gameplay patterns: health, inventory, save, spawn waves.

#### 5.2.4 Review skills

Activate when the agent is asked to review code:

- `unity-reviewer` — severity-tiered review approach. Triages findings into critical/important/nice-to-have.

### 5.3 Skill content philosophy

The competitive analysis (Vision §7.1) identified that Nice-Wolf-Studio's 35-skill library has a key advantage over UniClaude's 10: it has heavy code examples throughout, while UniClaude's are mostly decision-frame prose.

Hades Skills aim for both: decision frames explain *when and why*, code examples show *how*. The format for each skill:

1. **When to apply**: 1-3 sentences describing the activation condition.
2. **Decision framework**: the actual reasoning the agent should perform. Often a decision tree or set of questions.
3. **Code examples**: concrete C# scaffolds showing the recommended approach. These can be quite long.
4. **Anti-examples**: code that shouldn't be written, with explanation.
5. **Cross-references**: pointers to other skills or to graph queries that provide project-specific context.

### 5.4 Integration with Graph and Asphodel

This is what differentiates Hades Skills from a generic skill library: every skill is aware of the project's actual state via Graph queries and the project's accumulated context via Asphodel reads.

#### 5.4.1 Pattern: skill checks graph state

A skill that recommends "use Pattern X" should first verify whether the project already uses Pattern X. The skill body might include:

```markdown
Before recommending the SO event channel pattern, check the graph:
- Call `find_components_using_pattern("ScriptableObjectChannel<T>")`
- If results > 0: the pattern is already in use; recommendations should reference 
  existing channels in `Assets/Events/`.
- If results == 0: the project doesn't use this pattern yet; recommendations 
  should introduce it carefully, considering migration cost.
```

The skill does not include "always recommend SO event channels." It includes "decide based on what's already in the project."

#### 5.4.2 Pattern: skill reads memory

A skill that produces architectural recommendations should first read the team's accumulated decisions:

```markdown
Before recommending an architectural approach, read Asphodel's `decisions.md`:
- Call `recall_memory("architecture")`.
- If a relevant decision is already recorded, align the recommendation with it 
  or surface the conflict explicitly.
- If no relevant decision exists, the recommendation is a candidate for 
  recording — propose adding it via `propose_memory_update`.
```

This pattern ensures that skills don't override project-specific choices with generic best practices.

#### 5.4.3 Pattern: skill writes back to memory

After producing a recommendation that the user accepts, the skill can propose a memory update reflecting the decision. This is how the project's memory grows organically over time.

```markdown
When you complete a significant architectural recommendation that the user 
accepts, propose a memory update to `decisions.md` capturing:
- The decision made
- Alternatives considered  
- The project context that informed the choice
The user will review and approve the memory update.
```

### 5.5 Skill versioning and compatibility

Skills are versioned via the plugin manifest. Each skill change increments the plugin's minor version.

> **Status: Planned — not yet implemented as of v1.0.0.** The `plugin.json` compatibility range declaration and the version-check logic described below are design targets; no such field exists in `plugin.json` and no version-check code is implemented.

When the agent client loads the plugin, it would check for compatibility:

- If the plugin's required MCP server version is higher than what's installed, the agent client warns and asks the user to update.
- If the MCP server's version is much newer than the plugin expects, the plugin still loads but a warning is shown.

This versioning model is a goal because skill behavior often depends on specific MCP tool signatures. Mismatched versions silently producing wrong behavior is the failure mode we are trying to avoid.

### 5.6 Distribution

Skills, commands, and MCP connectivity are bundled in the Hades repository, which serves as both a Unity Package and a Claude Code plugin. The full plugin structure — directory layout, manifest, `.mcp.json`, tilde-suffix convention, installation flow, marketplace compliance, and versioning — is documented in the **Plugin document** (`Documentation/arcforge-hades-plugin.md`). That document is the authoritative source for plugin packaging and distribution.

**Summary:** Users install with two commands (UPM git URL for Unity, `--plugin-dir` for local installs or `/plugin install` for marketplace installs in Claude Code). Skills and commands are installed at user scope, shared across all Unity projects. The MCP launcher starts automatically when the plugin is enabled.

On server start, Hades also copies skills to `~/.claude/skills/hades-*/` for Claude Desktop discovery. Claude Desktop does not use the plugin system; this copy step is its distribution path for skills.

### 5.7 Slash commands

The plugin includes six slash commands for explicit user control. See **Plugin document** §1.2 for the full catalog.


---

## 6. Integration: How the layers compose

The previous four chapters described each layer in isolation. This chapter explains how they work together. The integration is what differentiates Hades from running four independent tools side by side.

### 6.1 The integration principles

Three principles govern how the layers compose:

**Layered access, not direct coupling.** Charon doesn't reach into Asphodel's files. Asphodel doesn't query the trace database. They communicate through well-defined interfaces. This keeps each layer testable in isolation.

**Events flow upward, queries flow downward.** Lower layers (Graph) emit events when state changes. Upper layers (Skills) query lower layers when they need information. The asymmetry keeps the dependency graph clean.

**Failure is local.** If Charon stops emitting traces, Graph and Asphodel still function. If Asphodel's files are corrupt, Graph and Charon are unaffected. The layers degrade independently.

### 6.2 The event flow

> **Status: Planned — not yet implemented as of v1.0.0.** A named internal event bus (`Dictionary<string,List<Action<Event>>>`) does not exist. Integration is currently achieved via one C# event (`GraphBuilder.OnRebuildComplete`) that triggers Asphodel validation and inference, plus Charon span emission. The diagram below represents the intended design target, not the current wiring.

The planned event flow:

```
Graph                     Asphodel              Charon
  │                          │                    │
  │ graph.updated            │                    │
  ├────────────────────────►│                    │
  │                          │ memory.validated   │
  │                          ├────────────────►   │
  │ graph.query              │                    │
  ├──────────────────────────────────────────►   │
  │                          │ memory.read        │
  │                          ├────────────────►   │
  │                          │ memory.proposal    │
  │                          ├────────────────►   │
  │ scanner.completed        │                    │
  ├──────────────────────────────────────────►   │
  │                          │                    │
```

All events would go to Charon as trace spans. Some events would also be consumed by Asphodel for self-validation triggering. Currently, this wiring is a direct `GraphBuilder.OnRebuildComplete` callback rather than a named event bus.

### 6.3 The query flow

When the agent makes a tool call, the flow is:

```
Agent client (Claude Code)
   │
   │ MCP tool call: find_prefabs_with_component("PlayerHealth")
   ▼
Hades Plugin's MCP bridge process (Node.js)
   │
   │ HTTP POST /mcp/v1/tools/call
   ▼
Hades Unity Package (HttpTransport, on background thread)
   │
   │ Enqueue WorkItem onto main thread queue
   ▼
Hades Unity Package (Main thread, EditorApplication.update)
   │
   │ Charon: start root span
   │ MCPDispatcher: dispatch to FindPrefabsWithComponent tool
   ▼
FindPrefabsWithComponent tool
   │
   │ Charon: start child span
   │ Graph: query for prefabs containing component
   ▼
Hades Graph
   │
   │ Charon: start grandchild span
   │ SQLite: SELECT ... WHERE ...
   │ Charon: end grandchild span
   ▼
Result returned through the chain, with each span ended on the way back
   │
   ▼
Tool returns result to MCPDispatcher
   │
   │ Charon: end child span (graph query)
   │ MCPDispatcher returns wrapped result
   │ Charon: end root span (mcp tool call)
   ▼
HttpTransport: writes HTTP response
   │
   ▼
HTTP response returns through the bridge to the agent client
```

This flow looks complex but each step is well-defined. The Charon spans capture the full timeline so when something goes wrong, the trace shows exactly where.

### 6.4 Cross-layer feedback loops

The interesting integration behavior is the feedback loops that emerge when the layers run together over time.

#### 6.4.1 Loop 1: Graph evolution drives Asphodel updates

As the graph changes (project evolves), some changes are noteworthy enough to trigger memory considerations:

1. Graph rebuild adds new node type usage in the project (e.g., the project now uses Addressables when it didn't before).
2. `GraphBuilder.OnRebuildComplete` fires, which triggers Asphodel inference. *(The planned event bus would emit a `graph.pattern_emerged` event; currently the wiring is a direct C# callback.)*
3. Asphodel's pattern detector (in the Tier 2 generator) picks this up.
4. Tier 2 inferred memory updates: `inferred/observed_patterns.md` notes "Addressables usage detected, 12 instances."
5. After confidence threshold (more instances over time), promotion to Tier 1 is proposed.
6. The developer reviews the proposal in the Charon dashboard, accepts, and Tier 1 `patterns.md` gets a new entry.

This is how the project's memory grows organically. The developer doesn't have to manually document every architectural choice; the system observes and surfaces patterns for confirmation.

#### 6.4.2 Loop 2: Trace patterns drive skill calibration

As traces accumulate, certain patterns emerge:

1. Charon notes that suggestions from `unity-recipes` skill have a 60% rejection rate.
2. The eval framework flags this: regression below threshold.
3. Drilling into specific traces reveals that rejections cluster on inventory-related queries — the skill's inventory recipe doesn't match the project's patterns.
4. The skill is updated to consult the graph for existing inventory patterns before proposing a recipe.
5. Acceptance rate recovers.

This loop is not automatic — it requires us, the developers of Hades, to inspect traces and update skills. But the data needed to make the diagnosis is captured automatically.

#### 6.4.3 Loop 3: Memory invalidates graph assumptions

Sometimes memory contains information that contradicts the graph:

1. `decisions.md` says "we use Addressables for all level loading."
2. Graph shows the project still has scenes loaded via `Resources.Load`.
3. Asphodel's C# validator detects the discrepancy on its next run (startup, post-rebuild, or on demand) and writes a warning into the memory file's frontmatter and an inline HTML comment.
4. When the agent next reads `decisions.md` via MCP, it receives the file with the validation warning already embedded.
5. The agent surfaces the conflict to the user: "Your decisions document says Addressables, but the validation warning notes 3 places still use Resources. Should we migrate, or update the decision to reflect the mixed state?"
6. The user either migrates (graph updates next scan, validation re-runs and clears the warning) or updates the decision (memory file updated to acknowledge mixed state, validation passes).

This loop is what keeps memory honest. Without it, memory accumulates aspirational claims that bear no relationship to reality. Note that the comparison is done by C# validation code, not by the agent — the agent merely reacts to validation results that are already present in the file.

### 6.5 Integration test scenarios

These are the cross-layer behaviors that need to work correctly. They form the basis of integration testing during development:

- **End-to-end tool call**: agent calls a tool, graph returns data, Charon records the trace, response makes it back. Latency budget: < 100ms total for typical queries.
- **Graph update during tool call**: agent is in the middle of a tool call; an asset is modified in Unity. Graph update is queued but doesn't disrupt the in-flight call. After the call completes, the update is applied. Subsequent queries see new state.
- **Memory proposal lifecycle**: agent calls `propose_memory_update`. Proposal goes to queue. User reviews and accepts. File updated. Next session, agent reads new memory state.
- **Validation cascade**: graph rebuild happens. Memory validation triggers. Stale memory entry is flagged. Charon trace records the validation. Dashboard shows the warning.
- **Domain reload during operation**: Unity initiates a domain reload while a tool call is in flight. The tool call completes (LockReloadAssemblies prevents the reload). After completion, the reload proceeds. Hades resumes correctly.
- **Crash recovery**: Unity crashes. On reopen, the trace WAL is replayed, memory files are intact, graph is rebuilt or resumed from disk. No data corruption.

### 6.6 Configuration and customization

> **Status: Planned — not yet implemented as of v1.0.0.** The nested `graph:`/`charon:`/`mcp:` config blocks shown below are design targets. The only `.arcforge/config.yaml` reader currently implemented is Asphodel's flat `InferenceConfig` parser. Graph, Charon, and MCP settings live in Unity **EditorPrefs** (accessible via the Hades preferences panel), not in `config.yaml`. A `.arcforge/config.local.yaml` per-developer override file has no loader and is not currently gitignored.

Planned `.arcforge/config.yaml` schema (when implemented):

```yaml
graph:
  scanner_versions: auto    # or pin to specific versions
  deep_script_analysis: false  # enable Roslyn-based call graphs (planned)

charon:
  retention_days: 30
  max_size_mb: 500          # hard size cap; excess traces are trimmed at startup
  redact_paths: false       # planned
  redact_user_input: false  # planned

asphodel:
  enabled: true             # master switch for Tier 2 inference
  promotion_confidence: 0.90
  promotion_min_samples: 50
  validation_on_startup: true
  validation_query_budget_ms: 1000
```

Currently implemented Asphodel config keys (flat, read from `InferenceConfig`): `enabled`, `promotion_confidence`, `promotion_min_samples`.

### 6.7 Confidence modeling and graceful uncertainty

Hades is a system where wrong answers are worse than no answer. If the agent makes confident architectural recommendations based on stale or incomplete graph data, the user notices once or twice and stops trusting Hades. Trust, once lost, is hard to rebuild. Therefore the architecture treats uncertainty as first-class.

#### 6.7.1 Sources of uncertainty

Every layer can produce uncertain or incomplete data:

- **Graph**: stale due to in-progress rebuild, incomplete due to scanner failures (surfaced via per-scanner `scan_health` flags including `packages`), blind to dynamic patterns (per §2.9).
- **Memory**: claims may be unvalidated, contradicted by graph state, or outdated relative to recent code changes.
- **Charon-derived inference**: Tier 2 patterns are statistical, not absolute. Confidence varies with sample size.
- **Skills**: generic skills applied without project-specific verification produce generic answers.

#### 6.7.2 The confidence response pattern

Every MCP tool that can return uncertain data includes a `confidence` block in its response:

```json
{
  "result": [...],
  "confidence": {
    "level": "high|medium|low",
    "factors": [
      {"factor": "graph_freshness", "value": "rebuilding"},
      {"factor": "static_analysis_coverage", "value": "partial", "blind_spots": ["reflection", "runtime/string-based dispatch", "DI containers", "dynamic instantiation"]},
      {"factor": "package_scan", "value": "degraded"}
    ],
    "recommendations": [
      "consider retrying after rebuild completes (estimated 2-5 seconds)",
      "'No references' means none were statically detected; dynamic/runtime references are not visible to this tool. Check 'nested_by' before treating an asset as unused",
      "Package/external base types may be unindexed; supertypes/dependencies into packages may be missing"
    ]
  }
}
```

The agent receives this alongside the data. Skills are calibrated to read the confidence block and adapt the response: high confidence → assertive recommendations; medium → assertive but with stated caveats; low → exploratory tone with explicit "I'm not sure about X".

#### 6.7.3 Graceful degradation paths

Each layer has a defined behavior when uncertainty becomes unmanageable:

- **Graph rebuild in progress**: queries return current-best data with explicit "rebuilding" attribute. Agent decides to wait or proceed with caveat.
- **Package scan degraded**: `find_references_to` and `trace_dependencies` emit a `package_scan: degraded` confidence factor and a `supertypes_external_unresolved` count. Inheritance and supertype edges into package/precompiled types may be absent; the signals make this visible rather than silent.
- **Memory validation failure**: contradicting memory entries surface to the agent as "your memory says X, the project shows Y". Agent does not silently choose one.
- **Tier 2 inference low confidence**: never injected as authoritative pattern. Surfaced via `recall_memory(query)` — inferred memory files are returned with their `status: inferred` frontmatter preserved, clearly labeling them as statistical observations. *(A dedicated `recall_inferred` tool is planned — not yet implemented as of v1.0.0.)*
- **Skill applied without project context**: response includes "this is a generic recommendation; I did not verify it against your project's actual patterns. Consider checking..."

#### 6.7.4 The "I don't know" capability

A subtle but important behavior: tools must be able to return "I don't know" rather than "no results found." These mean different things:

- **No results found**: the query executed successfully and the answer is genuinely empty (e.g., "find all components named XYZ" → there are none).
- **I don't know**: the query couldn't execute reliably (e.g., the relevant assets are mid-scan, the graph is being rebuilt, the query encountered an internal error).

Conflating these is a known failure mode in AI tools. Hades distinguishes them explicitly. Tool responses include a `result_status` field: `complete | partial | uncertain | error`. The agent reads this and behaves differently in each case.

#### 6.7.5 Why this matters for the product

Without confidence modeling, Hades is a tool that occasionally lies confidently. With it, Hades is a tool that occasionally says "I'm not sure, here's why." The first version is unusable after a few mistakes. The second version is genuinely useful even when imperfect.

This design principle informs every tool implementation: **prefer accurate uncertainty over false certainty**.


---

## 7. Pipelines: end-to-end behavior walkthroughs

This chapter walks through twelve realistic scenarios in detail. Each pipeline shows the user intent, the step-by-step internal behavior (which tools fire, which graph queries run, which traces are emitted, which memory is read or written), the expected output, and what could go wrong.

The pipelines are organized from simple to complex:

- Pipelines 1-3: simple, single-tool interactions
- Pipelines 4-8: medium complexity, cross-layer
- Pipelines 9-11: complex, multi-session, learning over time
- Pipeline 12: failure mode showing observability value

Use these pipelines as the source of truth for "how should Hades actually behave." When implementing, the integration tests should exercise these pipelines.

---

### Pipeline 1: "Show me the scene structure"

**Complexity:** Simple
**User intent:** Get a quick understanding of the current scene's organization.

**The user types in Claude Code:**
> "What's the structure of MainMenu.unity?"

**Step-by-step:**

1. The agent recognizes this as a project-context query and decides to use Hades.
2. Agent calls Hades MCP tool `get_scene_summary(scene_path: "Assets/Scenes/MainMenu.unity")`.
3. Bridge process forwards the call over HTTP to the Unity Package's MCP server.
4. HttpTransport receives the request on a background thread, enqueues a WorkItem.
5. Main thread processes the WorkItem on next `EditorApplication.update`.
6. Charon starts root span: `mcp.tool.get_scene_summary`, attributes: `{scene_path: "Assets/Scenes/MainMenu.unity"}`.
7. Tool implementation queries the graph (within the root tool span — graph queries are not separately sub-spanned; see §3.4.2):
   - SQL query: find Scene node by path, find all GameObject nodes contained in it (via `contains` edges), grouped by hierarchy depth.
   - Returns ~12 top-level GameObjects with their components.
8. Tool formats the result as a structured response: scene name, GameObject count, top-level hierarchy, notable components (Camera, Canvas, AudioSource, etc.), referenced assets (materials, audio).
9. Charon root span ends with status OK, attributes: `{nodes_returned: 12, total_components: 47}`.
10. Response returns through the chain to the agent.
11. Agent presents the summary to the user.

**Expected output:**

> "MainMenu.unity contains 12 top-level GameObjects:
> - Main Camera (with AudioListener)
> - UI Canvas (with 8 child UI elements)
> - EventSystem
> - GameManager (with GameManager.cs script)
> - AudioSource for menu music
> - ... [etc]
>
> Notable referenced assets: MainMenuMusic.mp3 (audio), DefaultUI.mat (material)."

**Latency budget:** < 50ms total. Most time is in the SQL query and result formatting.

**What could go wrong:**

- The scene file is missing/deleted: tool returns error "scene not found at path."
- The scene has never been scanned: tool returns "scene exists but graph has no data yet; rebuild may be needed." Suggests `/hades:rebuild-graph`.
- The graph is mid-update: tool returns the current data with a "graph update in progress, results may be slightly stale" warning in attributes.

---

### Pipeline 2: "Find a specific component type"

**Complexity:** Simple
**User intent:** Locate all instances of a particular component in the project.

**The user types:**
> "Where do we use the Inventory component?"

**Step-by-step:**

1. Agent calls `find_prefabs_with_component(component_type: "Inventory")`.
2. Tool dispatches; Charon root span starts.
3. Graph query (single-hop): find `ScriptType` node named "Inventory", then find all `Component` nodes with `instance_of` edge to that type, then resolve each Component back to its containing GameObject and Prefab/Scene.
4. SQL: 
   ```sql
   WITH script AS (SELECT id FROM nodes WHERE type='ScriptType' AND name='Inventory')
   SELECT DISTINCT pf.* 
   FROM nodes pf
   JOIN edges contains ON contains.target_id = (anything reachable from pf via contains*)
   ...
   ```
   (Simplified; actual query uses recursive CTE for the containment traversal.)
5. Returns 4 prefabs and 1 scene that contain Inventory components.
6. Tool formats: each location with file path, GameObject name, line in script if relevant.
7. Charon root span ends.

**Expected output:**

> "The Inventory component is used in:
> - Assets/Prefabs/Player.prefab → on the 'PlayerCharacter' GameObject
> - Assets/Prefabs/NPC_Merchant.prefab → on 'Inventory_Holder' child
> - Assets/Prefabs/Chest_Loot.prefab → on the root
> - Assets/Prefabs/Player_Variant_Mage.prefab → on 'PlayerCharacter' (inherited from Player.prefab)
> - Assets/Scenes/TestScene.unity → on 'TestPlayer' GameObject"

**Latency budget:** < 30ms.

**What could go wrong:**

- Component name doesn't match any script: returns "no component type 'Inventory' found in project."
- Multiple types share the name (different namespaces): returns all matches with namespaces specified, asks user to disambiguate if needed.
- Project hasn't fully scanned scenes: warning in result.

---

### Pipeline 3: "Get the project summary"

**Complexity:** Simple
**User intent:** Quick high-level overview of the project.

**The user types:**
> "Tell me about this project."

**Step-by-step:**

1. Agent calls `get_project_summary(depth: "shallow")`.
2. Tool runs several quick aggregation queries:
   - Count of scenes, prefabs, scripts, ScriptableObjects, materials.
   - Active render pipeline (URP/HDRP/built-in) — from RenderPipelineAsset node.
   - Build target configuration — from BuildSettings node.
   - Top-level Assets/ subdirectories.
3. Tool also reads memory summary:
   - Charon span: `memory.read.summary`.
   - Asphodel reads `intent.md` and the top entries from `decisions.md` and `patterns.md`.
4. Tool combines into a structured response.
5. Charon root span ends.

**Expected output:**

> "Project summary:
> - 12 scenes, 47 prefabs (8 are variants), 156 scripts, 23 ScriptableObjects.
> - Render pipeline: URP 14.0.
> - Target platforms: Android, iOS.
> - Top directories: Assets/Scripts/ (Player, Enemy, UI, Audio), Assets/Prefabs/, Assets/Scenes/, Assets/Audio/, Assets/Materials/.
>
> From your memory:
> - Current focus (intent.md): Mobile performance optimization for Android.
> - Key decision (decisions.md): SO event channels for inter-system communication.
> - Active pattern (patterns.md): Object pooling for all spawned entities."

**Latency budget:** < 100ms (multiple queries).

**What could go wrong:**

- No memory files exist (new project): summary skips memory section.
- Render pipeline unidentifiable (custom SRP): falls back to "custom or unknown render pipeline."

---

### Pipeline 4: "Add a new system that integrates with existing patterns"

**Complexity:** Medium
**User intent:** Implement a feature that respects the project's established patterns.

**The user types:**
> "Add an inventory system that works with our existing patterns."

**Step-by-step:**

1. Agent reads system prompt context, which already includes Asphodel summary indicating SO event channels are the project's pattern.
2. Agent reasons: "I should check the actual graph state to confirm the pattern is in use, and find existing event channels and data containers."
3. Agent calls `find_components_using_pattern(pattern: "ScriptableObjectChannel<T>")`.
   - Charon root span 1.
   - Graph query returns 4 SO event channel instances.
4. Agent calls `find_components_using_pattern(pattern: "ItemConfig")` (a project-specific data container the agent suspects from memory).
   - Charon root span 2.
   - Graph query returns 12 ItemConfig SO instances.
5. Agent calls `recall_memory("inventory")` to check if there's prior decision context.
   - Charon root span 3.
   - Asphodel returns relevant entries (none specifically about inventory; closest is the pattern entry on SO event channels).
6. Agent now has enough context. Composes a response: "I see your project uses SO event channels (4 already exist) and ItemConfig SOs (12 items). I'll create an Inventory system that uses an InventoryChanged event channel and ItemConfig data."
7. Agent presents code: a new C# script `Inventory.cs` that subscribes to/raises an `InventoryChanged` SO event, references `ItemConfig` SOs.
8. Agent presents a new SO asset proposal: `InventoryChanged.asset` (a new event channel).
9. User reviews and accepts.
10. Agent uses the existing UniClaude-style action tools to:
    - Create the new script file.
    - Create the new SO asset.
    - Charon spans for each action.
11. After completion, agent calls `propose_memory_update` to add to `patterns.md`: "Inventory system uses the established SO event channel pattern."
12. Proposal queued for user review.

**Expected output:**

A working inventory system, integrated with project patterns, plus a memory proposal for the user to confirm.

**Latency budget:** This is a multi-turn interaction. The agent's reasoning takes seconds; tool calls take < 50ms each. Total wall-clock time depends on user review pace.

**What could go wrong:**

- The graph queries return empty (the project hasn't scanned thoroughly): agent surfaces the limitation and asks the user to confirm patterns manually.
- Memory proposal conflicts with existing entry: agent surfaces the conflict.
- File creation fails (permissions, conflict with existing file): tool returns error, agent reports and asks for guidance.

---

### Pipeline 5: "Investigate slow level loading"

**Complexity:** Medium
**User intent:** Diagnose a performance issue with project-specific context.

**The user types:**
> "Why does Level3 load so slowly?"

**Step-by-step:**

1. Agent calls `get_scene_summary("Assets/Scenes/Level3.unity", depth: "deep")`.
   - Charon root span.
   - Graph query returns: 47 GameObjects, 12 of which are prefab instances, 8 of those are heavy (have Renderer + RigidBody + scripts).
2. Agent calls `find_components_using_pattern("HeavyInitialization")` (a project-specific marker).
   - Returns components flagged as having expensive Awake/Start.
3. Agent calls `analyze_render_pipeline()`.
   - Returns: URP 14.0, with Volumetric Lighting feature enabled, 4 active render passes.
4. Agent calls `recall_memory("performance")`.
   - Asphodel returns prior decisions: "We use object pooling for spawned entities" and "Mobile target requires 60fps."
5. Agent reasons: 12 prefab instantiations at scene start, 8 have heavy initialization, mobile target with 60fps requirement, URP with volumetric lighting (expensive on mobile).
6. Agent presents diagnosis: "Three likely causes: (1) 12 prefab instantiations at scene start, none cached; (2) 8 components with heavy initialization in Awake; (3) Volumetric Lighting on mobile is expensive."
7. Agent presents recommendations: pool prefabs, defer expensive initialization, evaluate volumetric lighting necessity.
8. Each recommendation is grounded in the project's actual structure, not generic advice.

**Expected output:** Diagnosis with specific file references and project-aware recommendations.

**Latency budget:** Total interaction < 200ms for tool calls; agent reasoning is bounded by model latency.

**What could go wrong:**

- Some queries return partial data due to scan in progress: results are presented with caveats.
- The project doesn't match standard patterns the agent looks for: agent falls back to generic profiling advice with caveat.

---

### Pipeline 6: "Refactor this script for project consistency"

**Complexity:** Medium
**User intent:** Make a script align with the project's established style.

**The user types:**
> "Refactor PlayerController.cs to fit our project conventions."

**Step-by-step:**

1. Agent reads `PlayerController.cs` (using its standard file-read tool).
2. Agent calls `recall_memory("conventions")`.
   - Asphodel returns naming conventions, structure conventions, and patterns.
3. Agent calls `find_components_using_pattern("MonoBehaviour")` to sample existing components.
   - Looks at the structure of recently-modified components to learn the project's actual style.
4. Agent calls `recall_memory("patterns")`.
   - Returns: "SO event channels for inter-system communication" plus "All MonoBehaviours derive from BaseMonoBehaviour."
5. Agent identifies discrepancies between PlayerController.cs and the project's conventions:
   - Doesn't derive from BaseMonoBehaviour (project standard).
   - Uses UnityEvent for damage notification (should be SO event channel).
   - Field naming uses camelCase but project uses _camelCase for serialized fields.
6. Agent produces refactored version.
7. Agent presents diff to user.
8. User accepts.
9. File is updated.
10. Charon trace captures the entire interaction with all decisions and code changes.

**Expected output:** Refactored script that aligns with project standards.

**What could go wrong:**

- Memory says one thing but graph shows another: agent surfaces conflict before refactoring.
- Refactoring introduces breaking changes: agent flags this and asks for confirmation.
- The script has no clear conventions to align with (project early stage): agent applies general best practices and proposes a memory update for explicit conventions.

---

### Pipeline 7: "Check for inconsistencies between memory and codebase"

**Complexity:** Medium
**User intent:** Audit whether the project's documented patterns are actually being followed.

**The user types:**
> "Are there places in the code that don't follow our patterns?"

**Step-by-step:**

1. Agent calls `recall_memory("patterns")`.
   - Returns all documented patterns from `patterns.md`.
2. For each pattern, the agent uses available graph tools to look for counter-evidence (e.g., for "use SO event channels," it calls `find_components_using_pattern(pattern_name: "UnityEvent")` to find places using UnityEvent that should use SO channels). Note: there is no `inverse:` argument and no `find_violations` tool — the agent reasons about violations from positive query results.
3. Agent collects findings across all patterns.
4. Agent calls `validate_memory()` (empty arguments = validate all memory files).
   - This returns the system's own automatic validation results.
5. Agent combines its own findings and the automatic validation results into a report.

**Expected output:** Report of pattern violations with specific file locations and recommended fixes.

**Latency budget:** Multiple tool calls; total < 500ms for typical projects.

**What could go wrong:**

- Some patterns don't have corresponding violation-finding tools: agent reports those as "unable to automatically check."
- Many violations found: agent prioritizes by impact and offers to triage.

---

### Pipeline 8: "Review my pull request changes"

**Complexity:** Medium
**User intent:** Get a project-aware code review of recent changes.

**The user types:**
> "Review my changes to PlayerHealth.cs and EnemyAI.cs"

**Step-by-step:**

1. Agent reads both files.
2. Agent calls `get_recently_changed(hours: 24)` to confirm these are recent changes.
3. Agent calls `find_references_to("Assets/Scripts/Player/PlayerHealth.cs")`.
   - Returns: 3 prefabs, 2 scenes, 1 other script depend on PlayerHealth.
4. Agent calls `recall_memory("review")` and `recall_memory("conventions")` for review context.
5. Agent activates the `unity-reviewer` skill, which provides the severity-tiered review approach.
6. Agent produces review findings, organized by severity:
   - Critical: changes to PlayerHealth.cs that would break the EnemyAI integration via the OnDeath event.
   - Important: EnemyAI.cs uses `UnityEvent` instead of project's SO event channel pattern.
   - Nice-to-have: variable naming inconsistency.
7. Each finding is specific to the actual change in the actual project, not generic.

**Expected output:** Targeted review with project-aware critical findings.

**What could go wrong:**

- Files have heavy dependencies that would take long to fully analyze: agent provides shallow review and notes which dependencies were too deep to inspect.
- Review surfaces architectural concerns beyond review scope: agent flags but doesn't expand without permission.

---

### Pipeline 9: "Multi-session learning over time"

**Complexity:** Complex
**User intent:** This is not a single user request but a multi-session pattern.

**Setup: across 30 sessions over 2 weeks, the user works on the project. Hades observes and learns.**

**Session 1:**
- User asks for inventory system. Agent suggests using UnityEvent. User rejects.
- Charon trace records the rejection.
- Agent suggests SO event channel after re-reading memory. User accepts.

**Sessions 2-15:**
- Pattern: agent suggests UnityEvent ~3 more times across different feature requests. User rejects each time.
- Tier 2 inferred memory updates:
  ```
  inferred/observed_patterns.md
  - Pattern: "User rejects UnityEvent suggestions"
  - Confidence: rising (3/3 → 5/5 → 8/8)
  - Sample size: 8
  ```

**Session 16:**
- Confidence threshold (90% over 50 samples) reached.
  - Wait, actually we need 50 samples — adjust: when sample size hits 50 with 90%+ rejection, promote.
- After more sessions, eventually threshold met.
- Asphodel proposes a Tier 1 promotion: "User strongly prefers SO event channels over UnityEvent. Add to patterns.md?"
- User reviews proposal in Charon dashboard.
- User accepts.
- `patterns.md` gets new explicit entry.

**Session 17 onwards:**
- Agent reads `patterns.md` summary at session start.
- Agent never suggests UnityEvent again (unless user explicitly asks for it in some context).
- The project's memory has improved.

**Expected outcome:** Over time, Hades's behavior becomes increasingly aligned with the user's actual preferences without manual configuration.

**What could go wrong:**

- Confidence threshold too low: false patterns get promoted.
- Confidence threshold too high: real patterns never get promoted.
- User changes preference (now wants UnityEvent for some new feature): old inference becomes wrong; user can edit `patterns.md` directly to update.

---

### Pipeline 10: "Onboarding a new team member"

**Complexity:** Complex
**User intent:** A new developer joins the team and starts using Hades.

**Setup: project has been using Hades for 6 months. `decisions.md`, `patterns.md`, `conventions.md` accumulated. New dev clones the repo.**

**Day 1 of new developer:**

1. New dev clones the repo. `.arcforge/memory/` comes with the clone — Tier 1 decisions, plus any Tier 2 inferred patterns the team has committed (Tier 2 is git-tracked alongside Tier 1).
2. New dev opens Unity. Hades Unity Package is installed (from UPM, in the project's `Packages/manifest.json`). Hades scanner runs and builds the local graph from scratch (15-30 sec for medium project).
3. New dev opens Claude Code. Plugin is installed globally.
4. Plugin connects to Hades MCP server via the discovery file.
5. Agent client's first session reads memory summary at start.
6. New dev asks: "What's this project about?"
7. Agent has full memory context. Responds with project intent, architecture overview, key patterns, all from accumulated team memory.
8. New dev asks: "Add a quest system."
9. Agent uses the same patterns the team established. New dev's first contribution naturally aligns with team standards.

**Result:** New developer's AI assistant starts informed, not blank. Onboarding time to "code like the team" is reduced from weeks to first session.

**Caveat:** This relies on the team having actually maintained the Tier 1 memory. Empty memory means new developers get the same blank-slate experience as without Hades.

---

### Pipeline 11: "Validate change impact across project"

**Complexity:** Complex
**User intent:** Understand the full implications of a planned breaking change.

**The user types:**
> "I want to remove the deprecated `UseLegacyAuth()` method from AuthManager. What will break?"

**Step-by-step:**

1. Agent uses `search_by_name(name_pattern: "UseLegacyAuth")` and `find_references_to` to locate scripts that reference `AuthManager` or the method by name.
   - Note: there is no `find_method_callers` tool. Precise caller analysis would require Roslyn `calls` edges, which are planned but not implemented. The current approach relies on text-pattern search and reference traversal — less precise but available.
   - Returns approximately: ~5 scripts that mention the method.
2. Agent calls `trace_dependencies("Assets/Scripts/Auth/AuthManager.cs", max_depth: 3)`.
   - Returns assets that depend on AuthManager.
3. Agent calls `recall_memory("auth")`.
   - Returns relevant decisions: "We're migrating from legacy auth to OAuth in Q2 2026."
4. Agent combines: removing UseLegacyAuth will affect 4 scripts in 12 places, plus the migration decision aligns with this removal.
5. Agent produces impact report:
   - Direct callers (must update or remove): 4 scripts, 12 call sites.
   - Indirect dependencies: 7 prefabs that have components using AuthManager (must regression-test).
   - Memory context: this aligns with documented Q2 migration plan.
   - Recommended sequencing: update callers first, then remove method, then test affected prefabs.

**Expected output:** Comprehensive impact analysis grounded in project structure.

**What could go wrong:**

- Deep analysis disabled: results less precise; agent flags this.
- Some callers use reflection/dynamic dispatch: those aren't caught; agent warns about this limitation.

---

### Pipeline 12: "Diagnose a failed agent action"

**Complexity:** Failure mode walkthrough
**User intent:** Yesterday the agent did something wrong. Investigate.

**Setup: yesterday, the agent modified `Player.prefab` and broke its connection to a script. User reverted the change. Today, user wants to understand what happened.**

**The user types:**
> "Open the trace dashboard for yesterday's session where the prefab broke."

**Step-by-step:**

1. User runs `/hades:show-traces` in Claude Code.
2. Charon dashboard opens in browser.
3. User filters traces by date (yesterday) and trace name (e.g. `mcp.tool.prefab_edit_property` or `mcp.tool.prefab_apply_overrides`).
4. User finds the trace where the prefab modification happened.
5. User clicks into the trace. Sees the span tree (graph queries are recorded on the enclosing tool span, not as per-query child spans — §3.4.2):
   - Root span: `mcp.tool.prefab_edit_property(path: "Assets/Prefabs/Player.prefab", ...)`, whose attributes show the agent first checked references and got an **empty** result.
   - Child span: `unity.action.prefab_edit_property` — successful.
6. User notices the empty references result on the tool span. This is suspicious because Player.prefab is referenced from many scenes.
7. The span attributes show: `query.executed_at: 2026-05-08T14:30:21Z`, `graph.last_rebuild: 2026-05-08T14:30:18Z` (3 seconds before the query).
8. Diagnosis: a graph rebuild was in progress at the moment the agent queried. The rebuild had not yet processed Player.prefab's references when the query ran. The query returned empty, and the agent assumed no references existed.
9. Diagnosis pinpoints the bug: the graph layer should signal a query that a rebuild is in progress. **This fix has since shipped** — every query tool checks `IsRebuildInProgress()` and downgrades its response `ConfidenceBlock` to `graph_freshness: "rebuilding"` (§2.7.5), so the agent sees a low-confidence/stale signal instead of a confident empty result.
10. User reports this finding. The Hades team adds a regression test based on this trace and fixes the underlying issue.

**Expected output:** Root cause identified with full context, in minutes rather than hours.

**Why this matters:** Without Charon, this diagnosis would have been impossible. The agent's behavior would have been a black box. The user would have either lost trust in Hades or worked around the symptom without understanding the cause. With Charon, even subtle race conditions become diagnosable.

---


## 8. Failure modes and operational concerns

This chapter inventories the things that can go wrong and how the system handles them. Naming failures explicitly is the first step to handling them well; treating them as theoretical is how products ship with brittle behavior.

### 8.1 Unity lifecycle failures

#### 8.1.1 Domain reload mid-operation

**What happens:** Unity reloads its scripting domain. Common triggers: script recompilation, package import, project setting change. All in-memory state is lost and reconstructed.

**Risk to Hades:** The MCP server's HTTP listener can be torn down mid-request. In-flight tool calls would fail without recovery.

**Mitigation:** UniClaude's domain-reload-resilience pattern is reused. When the MCP server starts, it persists its port and PID into Unity's `SessionState` (which survives domain reloads). After reload, `[InitializeOnLoad]` rebinds the server on the same port and replays any state from `SessionState`.

`EditorApplication.LockReloadAssemblies()` is called at the start of every tool execution and released at the end. This blocks domain reload during in-flight operations. If the user triggers a reload (via script edit), Unity defers it until all locks are released.

**Edge case:** if the user force-quits Unity during a tool call, the lock is dropped and the next reload proceeds normally. The trace span for the interrupted call is left open — orphaned open spans are not reconciled on next startup. *(A startup recovery sweep to close orphaned spans with `status: TIMEOUT` is planned — not yet implemented as of v1.0.0.)*

#### 8.1.2 Play mode transition

**What happens:** User enters play mode. Most editor APIs are still available, but some restrictions apply. User exits play mode. Sometimes scenes are reset.

**Risk to Hades:** Asset state may differ between edit and play mode. Scanners running during play mode could capture transient runtime state.

**Mitigation:** *(Planned — not yet implemented as of v1.0.0.)* Pausing incremental graph updates during play mode and a configuration opt-in for play-mode updates are design targets; no play-mode handling exists in the current build.

Charon continues operating during play mode — observability of agent actions during play sessions is valuable.

#### 8.1.3 Unity crash

**What happens:** Unity crashes hard, OS kills the process, power outage, etc. No clean shutdown.

**Risk to Hades:** SQLite databases could be corrupted. Trace WAL not checkpointed. Memory file edits not flushed.

**Mitigation:**

- SQLite is in WAL mode with explicit checkpoint pragmas. After a crash, SQLite automatically replays the WAL on next open. Database integrity is preserved unless the disk itself is corrupted.
- Charon's trace buffer flushes every 500ms or 1000 spans. Worst-case data loss is the last 500ms of in-flight spans.
- Memory file writes use delete-then-move (write to temp file, move over original). This is not a fully atomic replace — there is a brief window where neither file exists. *(True atomic-rename + fsync is planned — not yet implemented as of v1.0.0.)*
- On startup, Hades verifies memory files parse and checks asset hashes for stale detection. *(Startup `PRAGMA integrity_check` on the SQLite databases and a "rebuild recommended" surface are planned — not yet implemented as of v1.0.0. A corrupt database currently throws unhandled at open.)*

#### 8.1.4 Editor freeze (long-running tool call)

**What happens:** A tool call takes longer than expected — graph rebuild on a very large project, scanner stuck in infinite loop, etc.

**Risk to Hades:** Unity Editor becomes unresponsive while the tool runs on the main thread.

**Mitigation:**

- Default 30-second timeout on all tool calls. After 30 seconds, the HTTP thread returns a timeout error to the client.
- The main thread continues processing the tool call — it may eventually succeed or fail naturally. Result is discarded if the client has timed out.
- For known long-running operations (full graph rebuild), the tool surfaces progress via `EditorApplication.DisplayProgressBar` so the user knows Unity is working, not frozen.
- The interactive incremental `.cs` scan — historically the most common per-save freeze — no longer runs on the main thread: it spawns the Node scanner off-thread and resolves the rest of the batch when the subprocess exits (§1.6, §2.4.3), so a script save keeps the editor responsive and a tool call during the scan gets a structured `busy` rather than a 30s timeout.
- *(Planned — not yet implemented as of v1.0.0.)* A `Hades: Cancel In-Flight Operations` menu command and scanner `CancellationToken` checks at work boundaries are design targets; they do not exist in the current build.

#### 8.1.5 Backgrounded editor starves the post-reload bootstrap (App Nap)

**What happens:** the editor is backgrounded and deeply App-Napped when a domain reload fires. `HadesBootstrap.Boot` runs on an `EditorApplication.delayCall`; under App Nap the editor-update tick that would *run* `Boot` is throttled, so `Boot` — and with it the MCP server's re-registration — is delayed indefinitely. The nap opt-out (`AppNapGuard`) is acquired *inside* `Boot`, so it can't engage while nap is starving the very tick that would acquire it.

**Risk to Hades:** after the reload the server never re-registers; tool calls return "No Unity instance found" until the editor is foregrounded. With the hub/routing issues (§8.5) resolved, this is the dominant remaining interaction friction.

**Mitigation:**

- **Recovery (current):** `wake-unity.sh` (or simply clicking the editor) foregrounds Unity, un-naps it, and lets the bootstrap tick fire — the server re-registers within a moment.
- **Fix direction (tracked, not yet shipped):** acquire the nap opt-out in the `[InitializeOnLoad]` static constructor — which runs synchronously during the reload, before any `delayCall` — held until `Boot` completes, closing the starve-the-tick window. The token-based `NSProcessInfo` activity assertion needs no update ticks to sustain, so it holds through the napped window.

### 8.2 Graph-level failures

#### 8.2.1 Scanner crashes on a specific asset

**What happens:** A scanner throws an exception while processing an asset. Could be malformed asset, edge case in Unity's API, or a scanner bug.

**Risk to Hades:** If unhandled, the entire build aborts. Worse: in incremental mode, the asset never gets re-scanned, and the graph is permanently stale for that asset.

**Mitigation:**

- Each scanner invocation is wrapped in try-catch. Exceptions are caught, logged, and scanning continues for other assets.
- The build itself continues. One bad asset doesn't halt scanning the rest.

*(Planned — not yet implemented as of v1.0.0.)* The following are design targets, not current behavior: emitting a `scan_status: failed` property on a node for the failed asset; a retry queue with 5-minute delay; a quarantine mechanism after 3 retries surfacing a user warning.

#### 8.2.2 Database corruption

**What happens:** Disk error, filesystem bug, or extreme edge case corrupts the SQLite database.

**Risk to Hades:** Queries fail, possibly silently returning wrong data.

**Mitigation:**

- `Hades: Rebuild Graph` command. The graph is recomputable from the project source — corruption is annoying but not catastrophic.
- If corruption recurs, hint at filesystem investigation (different machines? cloud-synced project directory?).

*(Planned — not yet implemented as of v1.0.0.)* Startup `PRAGMA integrity_check` with a "rebuild recommended" surface is a design target. Currently, a corrupt database throws unhandled at open.

#### 8.2.3 Graph and reality drift

**What happens:** The graph is technically valid but doesn't match the project state. Could happen if AssetPostprocessor doesn't fire (Unity bug), incremental update logic has a hole, or the user modifies files outside Unity.

**Risk to Hades:** Agents give wrong answers based on stale graph data.

**Mitigation:**

- *(Planned — not yet implemented as of v1.0.0.)* A periodic 5%-sample integrity check every 24 hours of editor time is a design target.
- Memory self-validation surfaces inconsistencies between memory claims and graph state. If the graph itself is wrong, this often surfaces as "memory says X, graph says Y" warnings.
- The user can run `/hades:rebuild-graph` at any time as recovery.

#### 8.2.4 Re-entry loop in asset postprocessor

**What happens:** Graph rebuild triggers scene scanning, which opens scene assets, which triggers Unity's `OnPostprocessAllAssets`, which re-enqueues graph work, creating an infinite loop. The Unity Editor freezes.

**Risk to Hades:** Complete editor freeze on large projects. Observed on a 55k-node project during first full rebuild.

**Mitigation:**

- `GraphUpdateHandler` exposes an `IsBusy` property (checks `BuildStatus != Idle`).
- `GraphAssetPostprocessor.OnPostprocessAllAssets` checks `IsBusy` and returns early if the graph is already being built.
- This breaks the re-entry cycle: rebuild → scan → postprocessor fires → sees busy → skips → rebuild continues normally.

**Phase 1 lesson:** This was not caught in unit tests because the re-entry only occurs on real projects with enough assets to trigger scene scanning during rebuild. First observed as an editor freeze on a production Unity project with 55k+ graph nodes.

#### 8.2.5 sqlite-net null binding

**What happens:** `SQLitePreparedStatement.Bind(index, null)` throws an exception in sqlite-net. Unlike ADO.NET, sqlite-net does not accept null string arguments in prepared statement bindings.

**Risk to Hades:** Query methods that accept optional filter parameters (e.g., `SearchByName(namePattern, typeFilter)`) crash when called with null arguments.

**Mitigation:**

- All optional parameters are coalesced to safe defaults before binding: `var pattern = namePattern ?? "%"`.
- sqlite-net uses 1-indexed `Bind()` parameters (unlike ADO.NET's 0-indexed), which is documented in the codebase and the architectural decision record.

**Phase 1 lesson:** Discovered during the SQLite migration from Mono.Data.Sqlite to gilzoide/unity-sqlite-net. The `SearchByName` method passed null directly to `Bind()`, which worked with Mono.Data.Sqlite but throws in sqlite-net. All 3 test failures during migration were variations of this API incompatibility.

### 8.3 Charon-level failures

#### 8.3.1 Trace database fills the disk

**What happens:** Heavy use over months without pruning. Trace database reaches GBs.

**Risk to Hades:** Disk full prevents writes. Hades emitter can't flush.

**Mitigation:**

- Default retention: 30 days. Auto-prune on startup.
- **Trace-count cap** (a row budget derived from the default 500MB): at startup, `PruneToTraceCap` deletes the oldest traces beyond the budget and runs a PASSIVE checkpoint — no `VACUUM`, which on a multi-GB `traces.db` could freeze startup. This is the main guard against unbounded growth.

*(Planned — not yet implemented as of v1.0.0.)* A soft warning at 1GB and a hard guard at 80%-disk-fill emitter drop-mode are design targets; they do not exist in the current build.

#### 8.3.2 Dashboard process crashes

**What happens:** The Charon dashboard Node.js process crashes or hangs.

**Risk to Hades:** User can't view traces.

**Mitigation:** Dashboard is independent of the Unity Package. Crash of dashboard doesn't affect graph, memory, or MCP server. User restarts dashboard via menu. Trace data is intact in the database.

#### 8.3.3 Dashboard process orphaned on domain reload

**What happens:** Unity recompiles scripts, tears down the AppDomain. Static fields holding the `Process` reference are lost. The dashboard Node.js process continues running but Unity no longer has a handle to stop it.

**Risk to Hades:** Orphaned dashboard processes accumulate. Port file stale. User confusion about running instances.

**Mitigation:** Dashboard PID is stored in `SessionState` (survives domain reloads but not Unity restarts). Static constructor reattaches via `Process.GetProcessById()` on domain reload. `EditorApplication.quitting` hook is re-registered. If the stored PID no longer exists (e.g., dashboard crashed between reloads), the stale session state is cleaned up.

**Phase 2 lesson:** This was discovered when the dashboard appeared to start successfully but could not be stopped after a script recompile. The pattern of storing PIDs in `SessionState` and reattaching in static constructors applies to any long-lived child process Hades may launch.

#### 8.3.4 Node.js not found on PATH

**What happens:** `Process.Start("node", ...)` fails with "Cannot find the specified file" because Unity's process environment does not inherit the user's login shell PATH (nvm, fnm, Homebrew paths are missing).

**Risk to Hades:** Dashboard cannot start. Affects macOS, Linux, and Windows differently depending on how Node.js was installed.

**Mitigation:** `ProcessResolver.FindExecutable("node")` resolves the full path via platform-specific shell commands (`bash -lc "which node"` on macOS/Linux, `cmd.exe /c where node` on Windows). The `-lc` flag on bash ensures login shell profile is sourced, picking up nvm/fnm PATH modifications. Results are cached per session.

**Phase 2 lesson:** This is a general Unity platform issue, not specific to the dashboard. Any external tool invocation from Unity Editor code must go through `ProcessResolver` rather than relying on PATH.

### 8.4 Asphodel-level failures

#### 8.4.1 Memory file with broken syntax

**What happens:** User edits `patterns.md`, accidentally breaks the YAML frontmatter or markdown structure.

**Risk to Hades:** Memory reads fail. Agent loses context.

**Mitigation:**

- Markdown parser is forgiving: most syntactic errors are tolerated, content is extracted best-effort.
- Frontmatter parser logs warning on malformed YAML, returns empty metadata.
- Validation surfaces the parse error in the dashboard so the user sees what's wrong.
- Memory operations gracefully degrade: missing or malformed entries are skipped, others are still available.

#### 8.4.2 Conflicting memory entries

**What happens:** Two memory files (or two sections of same file) make contradictory claims.

**Risk to Hades:** Agent gets conflicting context.

**Mitigation:**

- Asphodel does not auto-resolve conflicts. Both entries are surfaced to the agent.
- The agent sees the conflict and either reasons about it or asks the user.
- The Charon dashboard has a "Memory Conflicts" view that highlights detected contradictions.

#### 8.4.3 Validation queries become expensive

**What happens:** As the graph grows, some validation queries take a long time.

**Risk to Hades:** Validation slows down startup or blocks editor responsiveness.

**Mitigation:**

- Per-validation-query budget (default 1 second). Queries exceeding the budget are skipped with a warning.
- Validation runs are debounced and batched.
- Heavy validation is opt-out: a config option disables expensive validations for users who don't need them.

### 8.5 MCP/Communication-level failures

#### 8.5.1 Port collision

**What happens:** The requested port is already in use by another application.

**Risk to Hades:** MCP server can't start. Agent client can't connect.

**Mitigation:**

- The server always binds to an OS-assigned ephemeral port (default `Port=0`) — there is no fixed port to collide on.
- Selected port is registered with the Hub via `POST /api/register`, so the Hub always knows the current port.
- The Hub abstracts port changes entirely — clients never need to know Unity's port.

#### 8.5.2 Port changes across Unity restarts

**What happens:** Unity recompiles or restarts, and the MCP server binds to a different port. Any hardcoded port in client configs becomes stale.

**Risk to Hades:** Agent client fails to connect after Unity restart.

**Mitigation:**

- Clients never connect directly to Unity's port. They connect through the Launcher → Hub chain. The Hub maintains the current port for each registered Unity instance.
- On server start, Unity registers with the Hub via `POST /api/register` with the new port. The Hub immediately routes subsequent requests to the updated port.
- On server stop, Unity deregisters from the Hub. The Hub returns an appropriate error to clients until Unity re-registers.

**Phase 1 lesson:** This was discovered during real-project testing when Unity recompiled mid-session, changed port from 57171 to 57846, and broke the manually-configured Claude Desktop connection. It motivated the auto-discovery system, which evolved into the current Hub architecture.

#### 8.5.3 Client starts before Unity

**What happens:** The user opens Claude Code or Claude Desktop before opening Unity. The MCP server doesn't exist yet.

**Risk to Hades:** Agent client shows connection errors, requiring manual restart after Unity starts.

**Mitigation:**

- The Launcher starts the Hub on demand. With no registered Unity instances, tool calls return a JSON-RPC error (`-32000 No Unity instance found`) rather than succeeding — there is no separate "empty tools list" state.
- When Unity starts and registers with the Hub, subsequent tool calls are routed to it and succeed. (Note: there is no `tools/list_changed` push notification — the tool list reflects whatever Unity reports at the moment of each request. *Automatic catalog refresh via `list_changed` is Planned — not yet implemented as of v1.0.0.*)
- Claude Code and Claude Desktop do not need to be restarted once Unity is up; retrying the call after Unity registers succeeds.

#### 8.5.4 Claude Desktop vs Claude Code transport differences

**What happens:** Claude Code supports HTTP-based MCP servers (direct connection). Claude Desktop only supports stdio-based MCP servers (`command` + `args` in config).

**Risk to Hades:** A single transport strategy cannot serve both clients.

**Mitigation:**

- Both clients connect through the Launcher (stdio process), which bridges to the Hub over HTTP. For Claude Code, the Launcher is reached via two paths: (1) the plugin's `.mcp.json` (using `${CLAUDE_PLUGIN_ROOT}/Bridge~/launcher/dist/index.js`) when the plugin is installed via `--plugin-dir` (local installs) or `/plugin install` (marketplace installs), or (2) a project-level `.mcp.json` written by `MCPClientConfig` to the Unity project root, pointing to the stable installed copy at `~/.arcforge/hades-hub/launcher.js`. For Claude Desktop, Unity writes `claude_desktop_config.json` pointing to the same stable launcher path.
- The Launcher has zero npm dependencies — uses only Node.js built-ins. No `npx` or external tool required.
- Node.js is a runtime dependency for MCP connectivity. Without Node.js, neither Claude Code nor Claude Desktop can connect to Hades.

**Phase 1 lesson:** Claude Desktop's stdio-only constraint was discovered during integration testing. The initial assumption was that both clients could connect directly via HTTP URL — only Claude Code can. This led to the bridge architecture, which evolved into the current Hub model.

#### 8.5.5 Streamable HTTP endpoint compatibility

**What happens:** MCP clients may use different transport strategies (POST-first vs SSE-first) when connecting to HTTP endpoints.

**Risk to Hades:** Incompatible transport negotiation causes connection failures or rapid connect/disconnect cycles.

**Mitigation:**

- The Hades MCP server handles `POST /rpc` (JSON-RPC) on its endpoint, conforming to the MCP Streamable HTTP specification.
- The Hub forwards requests as `POST` to Unity's `/rpc` endpoint — no transport negotiation ambiguity.
- Direct HTTP clients can also connect to Unity's `/rpc` endpoint, which handles both `POST` (JSON-RPC) and `GET` (SSE).

**Phase 1 lesson:** The initial bridge used `mcp-remote` which POSTed to `/sse` (404), causing rapid connect/disconnect cycles. This was resolved by standardizing on `/rpc` for both methods, and later superseded entirely by the Hub architecture which eliminates `mcp-remote` from the stack.

#### 8.5.6 Agent client doesn't speak MCP correctly

**What happens:** Some clients have buggy MCP implementations or use non-standard extensions.

**Risk to Hades:** Tool calls fail or behave unexpectedly.

**Mitigation:**

- Strict MCP spec compliance on the server side.
- Integration tested with Claude Code and Claude Desktop via the bridge.
- Charon traces capture exact request/response payloads for diagnosis.

### 8.6 Performance degradation modes

#### 8.6.1 Very large project

**What happens:** Project has 100k+ assets, deep dependency chains, hundreds of scenes.

**Risk to Hades:** Build times exceed user patience. Queries become slow.

**Mitigation:**

- Configuration to enable "selective scanning": user designates which directories are scanned. The rest is treated as opaque.
- Flagship queries are index-backed (`idx_nodes_path` / `idx_nodes_name_type`) with lazy `NodeRecord.Properties` parsing, so they stay sub-10ms at 100k+ nodes instead of degrading into a full-table scan (§2.5–2.6); incremental `.cs` updates run off the main thread so a save on a large project doesn't freeze the editor (§1.6); and dropping the per-query Charon spans (§3.4.2) keeps a single traversal from writing thousands of trace rows.
- Aggregation views in the dashboard surface query latency distributions, helping identify hot paths.
- Optional pre-aggregated rollup tables for common queries (planned for v2).

#### 8.6.2 Pathological assets

**What happens:** A single asset has thousands of components, deeply nested prefabs, or other extreme structure. Scanning it takes minutes.

**Risk to Hades:** That asset's scan blocks others.

**Mitigation:**

- The Node.js subprocess has a 5-minute overall timeout.
- Parallel scanning of independent assets where Unity API permits.

*(Planned — not yet implemented as of v1.0.0.)* A per-scanner 60-second timeout, an asset tag-to-skip configuration in `config.yaml`, and selective-directory scanning are design targets.

### 8.7 Security and integrity

#### 8.7.1 Malicious memory content

**What happens:** Someone commits memory content designed to manipulate the agent (prompt injection via the project's memory files).

**Risk to Hades:** Agent reads adversarial content and behaves unpredictably.

**Mitigation:**

- This is fundamentally a problem at the agent client level (prompt injection is an industry-wide concern).
- Hades doesn't evaluate memory content; it just delivers it. Same risk as any documentation or comment in a codebase.
- Memory is git-tracked, so changes are reviewable like any other code change. Team norms for code review apply.

#### 8.7.2 Path traversal

**What happens:** A tool call tries to access files outside the project directory.

**Risk to Hades:** Information leak or unintended file modification.

**Mitigation:**

- All file operations go through `PathSandbox.cs` (inherited from UniClaude). Sandbox restricts paths to the project root; `.git/` writes are additionally blocked.
- Path normalization uses `Path.GetFullPath` — symlinks are not resolved before validation (symlink resolution is a planned improvement).
- *(Planned — not yet implemented as of v1.0.0.)* Logging path-escape attempts as Charon security events is a design target.

#### 8.7.3 Network access

**What happens:** Hades runs on localhost-only HTTP. What if another local user (multi-user system) connects?

**Risk to Hades:** Other users on the same machine could potentially read project data.

**Mitigation:**

- Server binds to `127.0.0.1` only, not `0.0.0.0`. Only local processes can connect.
- For multi-user systems, OS-level user separation already provides isolation.
- A future feature could add token-based authentication if needed for shared environments.

### 8.8 Recovery procedures

When something goes wrong, the user should have clear paths forward. Documented recovery procedures:

| Symptom | Likely cause | Recovery |
|---|---|---|
| "Agent is confused about my project" | Stale graph | `/hades:rebuild-graph` |
| "Agent didn't follow our pattern" | Memory inconsistency | Check Charon trace; review `patterns.md` |
| "Tool calls are timing out" | Long-running operation, large project | Increase timeout in config; check progress bar |
| "Unity is slow with Hades enabled" | Heavy debouncer load | Increase debounce delay; disable Tier 2 |
| "Dashboard won't load" | Trace database large | Run pruning; check disk space |
| "Memory file won't save" | Frontmatter syntax error | Check parser warnings in dashboard |
| "Agent ignores my memory entries" | Frontmatter not loaded | Restart Unity, check parse logs |
| "MCP tools disappear after Unity restart" | Hub lost Unity registration | Check `~/.arcforge/hades-hub/hub.json` PID is alive; Unity re-registers on next heartbeat. If Hub PID is dead, delete `hub.json` and restart Claude Code session |
| "Database integrity error" | Corruption | Backup `.arcforge/`, then rebuild |

These recovery procedures are part of user documentation, not buried in this technical document.

---

## 9. Open architectural questions

This document does not resolve everything. Below are the questions that remain genuinely open. Each will be addressed during development through experimentation, prototyping, or by accumulating evidence from real use. The list is intentionally explicit — known unknowns are healthier than unknown unknowns.

### 9.1 Database performance at scale

We have estimated SQLite performance based on similar projects (codebase-memory-mcp, GitNexus). Real-world Unity projects at the high end (100k+ assets) may stress SQLite differently because of the asset graph's particular shape (many edges between non-adjacent nodes).

**Open question:** Will SQLite query latency remain acceptable for realistic enterprise Unity projects, or will some queries need optimization (better indexes, query rewriting, materialized views)?

**Resolution path:** Benchmark on representative large project early. If problems emerge, the schema and query design have headroom for optimization without architectural change.

### 9.2 Domain reload disruption frequency

UniClaude's domain reload resilience is battle-tested for chat-based interactions. Hades has different patterns: more frequent tool calls, longer build operations.

**Open question:** Does the existing reload-resilience approach cope when reloads happen during graph builds rather than during chat interactions?

**Resolution path:** Stress test by triggering domain reloads during simulated load. Add additional state preservation if needed.

### 9.3 Tool API design (deferred)

The exact MCP tool surface is TBD per architectural decision (UniClaude's 70+ tools migrating in will set the style).

**Open question:** What is the right granularity of tools? Too few = agent constructs complex queries from primitives, wasting tokens. Too many = agent struggles to choose between near-duplicate tools.

**Resolution path:** Migrate UniClaude's tools first. Run integration tests. Adjust granularity based on observed agent behavior in traces.

### 9.4 Memory pattern detection algorithms

Tier 2 inferred memory relies on pattern detection over traces. The exact algorithms are unspecified beyond high-level concept.

**Open question:** What detection approach yields useful patterns without false positives? Statistical thresholds? Clustering? LLM-based summarization of trace clusters?

**Resolution path:** Start with simple statistical methods (frequency, acceptance rate by feature). Iterate based on what produces actionable Tier 1 promotions.

### 9.5 Eval framework usefulness

The eval framework is conceptually powerful but its actual utility depends on whether replays can be automated and whether eval datasets stay relevant as the project evolves.

**Open question:** Will the eval framework be useful enough to justify the engineering investment, or will it be primarily a manual inspection tool?

**Resolution path:** Use eval framework internally during Hades development. If it pays off for us, that's evidence it'll pay off for users. If not, deprioritize.

### 9.6 Agent client compatibility

Hades is designed for any MCP-compatible agent. In practice, the test target is Claude Code. Other clients (Cursor, Cline, Continue) implement MCP differently.

**Open question:** How much per-client adaptation is required? Will some Hades features only work with Claude Code?

**Resolution path:** Test with each major client during development. Document compatibility status. Prioritize Claude Code, support others where reasonable.

### 9.7 Anthropic marketplace submission

The official Anthropic plugin marketplace is a discoverability channel, not a delivery mechanism. Hades is fully functional without marketplace listing.

**Open question:** When is the right moment to submit? Too early risks rejection on insufficient maturity; too late forfeits months of default-discoverability.

**Resolution path:** Submit after v1.0 release when we have 3+ months of stable usage and at least one external community contribution. Does not block any development phase.

### 9.8 Charon dashboard scope

The dashboard provides trace inspection, aggregations, eval datasets. We could add more (memory inspector, graph visualizer, configuration editor).

**Open question:** What is the right scope for v1 dashboard vs deferred features?

**Resolution path:** Ship v1 with trace views and basic aggregations. Add features based on user feedback after release.

### 9.9 Build mode (CI) support

Some teams want to run scanners headless in CI to validate that the graph is consistent and to surface architectural drift.

**Open question:** Is Unity batch mode sufficient for our scanners, or do we need a separate non-Unity-dependent codepath?

**Resolution path:** Unity batch mode should work for most scanners. Validate during development. If gaps emerge, evaluate whether they're worth filling.

### 9.10 Multi-project workflows

Some developers work on multiple Unity projects, switching between them. Each has its own `.arcforge/` directory.

**Open question:** Do we need any cross-project features (shared skills config, shared eval datasets)? Or is per-project isolation sufficient?

**Resolution path:** Default is per-project isolation. Cross-project features only if demand emerges.

### 9.11 Long-term schema evolution

As Hades evolves, the graph schema will change. We have a migration mechanism (schema_version table), but migrations of large graph databases are not trivial.

**Open question:** What is our policy for schema migrations? When can we make breaking changes? How do we communicate this to users?

**Resolution path:** SemVer for the schema. Major schema changes require a full rebuild on user side, communicated in release notes. Minor changes are migration scripts that run automatically. Keep backward compatibility where reasonable.

### 9.12 Telemetry beyond local

Hades is local-first. But aggregate, anonymized telemetry could help us understand usage patterns and prioritize features.

**Open question:** Should we offer opt-in telemetry that sends anonymized usage data to ArcForge? If so, what is sent and how is it scrubbed?

**Resolution path:** Defer to post-launch. Initial release is purely local. Telemetry, if added, is strictly opt-in with full transparency about what's sent.

---

## 10. Closing

This architecture has been written with the assumption that engineers building Hades will return to it as a reference, not just read it once. To that end, here are the most important properties to remember:

**The graph is the foundation.** Everything else builds on it. Get the schema right; tolerate trade-offs in the layers above.

**Self-validation is what keeps the system honest.** Memory that drifts from reality is worse than no memory. Charon traces that don't capture failures are worse than no observability. Build the validation loops first; the value is in the feedback, not in the data.

**Reuse from UniClaude is substantial.** ~60% of runtime infrastructure is direct reuse. This is a feature, not a bug — battle-tested code is more valuable than greenfield reimplementation.

**Failure modes are first-class concerns.** Every layer has been considered for what can go wrong. The system degrades gracefully where it can and surfaces problems clearly where it can't.

**The integration is the moat.** Each layer alone is replicable. The interconnected behavior — graph emits events, memory validates against graph, traces feed memory inference, skills consult both — is what differentiates Hades.

The Roadmap document, which follows this one, translates this architecture into a sequence of buildable milestones. The Roadmap does not ask "should we build Hades?" — that is the Vision's question. It does not ask "how do the parts fit together?" — that is this document's question. It asks: "given Vision and Architecture, what is the path from zero to a shipped product?"

That is the next document.

---

*End of Architecture document.*
