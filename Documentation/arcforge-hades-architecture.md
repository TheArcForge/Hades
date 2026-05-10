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

Hades is composed of three runtime processes and one passive artifact set:

1. **The Unity Editor process**, augmented by the Hades Unity Package. The Unity Package adds C# code that runs inside the editor and is responsible for: building and maintaining the project knowledge graph; emitting observability events; handling memory file I/O; and serving as the in-process MCP server that exposes Hades capabilities.

2. **The agent client process**. This is Claude Code, Cursor, Cline, Continue, or another MCP-compatible coding agent. Hades does not provide this process; it consumes whichever agent the user already runs. The agent client is a separate process from Unity and connects to the Hades MCP server over HTTP/SSE on localhost.

3. **The Charon dashboard process**. A small Node.js web server that reads the trace database and renders a local web UI. Started and stopped on demand by the user. Optional in the strict sense; without it, traces still accumulate, but they are not human-inspectable.

4. **The artifact set**. A collection of files within the Unity project's directory that persist Hades state across sessions. These include the graph database (`.arcforge/graph.db`), the trace database (`.arcforge/traces.db`), and the memory directory (`.arcforge/memory/`). Some are gitignored by default (graph and traces, since they are machine-specific or potentially noisy), others are git-tracked (Tier 1 memory, since it is project knowledge meant to travel with the project).

There is no fourth runtime process for an "ArcForge backend." Hades is local-first by design. There are no servers Anthropic or ArcForge operate on behalf of the user. There is no telemetry transmitted to a vendor. The architecture is entirely client-side.

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
│  │ MCP Server (HTTP/SSE on localhost) ←── tools exposed   │ │
│  └────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
                              │
                    HTTP/SSE on localhost
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│             Agent Client (Claude Code, etc.)                │
│         Loads Hades plugin: skills + MCP config             │
│       Calls Hades tools as part of agent reasoning          │
└─────────────────────────────────────────────────────────────┘

                    ────── separately ──────
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│             Charon Dashboard (Node.js, on demand)           │
│       Reads trace database → renders local web UI           │
└─────────────────────────────────────────────────────────────┘
```

The Unity Package is the heart of the system. It owns the data, it owns the introspection, it owns the integration with the Unity Editor's lifecycle. The agent client is the consumer of capabilities. The Charon dashboard is a side-channel viewer for accumulated data.

The data flow is asymmetric. Most reads come from the agent client into the Unity Package via MCP. Most writes happen inside the Unity Package as it reacts to project changes. Memory is the exception: the agent can propose memory updates, but those are written through the Unity Package after explicit acceptance.

### 1.3 The four logical components

Within the Unity Package and its supporting infrastructure, there are four logical components that map to the Vision's four pillars:

**Hades Graph** — the project knowledge graph. Built and maintained by C# scanners running inside the editor; persisted to a SQLite database at `.arcforge/graph.db`; queried by MCP tools that translate agent intent into SQL.

**Hades Charon** — observability. Trace events emitted from every MCP tool call, every graph query, every memory operation. Persisted to a SQLite database at `.arcforge/traces.db`. Visualizable via the Charon dashboard.

**Hades Asphodel** — memory. Markdown files at `.arcforge/memory/` providing both Tier 1 (explicit, human-curated, git-tracked) and Tier 2 (inferred, auto-generated, gitignored). Read by MCP tools that inject relevant memory into agent context.

**Hades Skills** — distributed via the Claude Code plugin. Not technically part of the Unity Package; lives in the agent client's plugin directory. But integrated with the other three layers: skills query Graph state and Asphodel context to give project-specific guidance.

These four components share infrastructure (the MCP server, the editor lifecycle hooks, the Charon emitter) and each contributes its own specialized data and tools. The next four chapters detail each component independently. Chapter 6 covers their integration.

### 1.4 Reuse from UniClaude

A substantial portion of Hades's runtime infrastructure is reused from UniClaude. Specifically:

- **`MCPServer.cs`** — the in-process HTTP server inside the Unity Editor, started via `[InitializeOnLoad]`. Battle-tested in UniClaude and survives Unity's lifecycle quirks (domain reload, assembly reload, play mode transitions).
- **`HttpTransport`** — the HTTP/SSE transport layer on localhost.
- **`MCPDispatcher`** — reflection-based discovery of methods decorated with `[MCPTool]`, parameter mapping via `[MCPToolParam]`, response wrapping via `MCPToolResult`.
- **Main Thread Bridge** — `ConcurrentQueue<WorkItem>` that funnels HTTP requests onto Unity's main thread, drained by `EditorApplication.update`. Required because Unity APIs are not thread-safe.
- **Domain Reload Resilience** — server state (port, PID) persisted in `SessionState` so it survives Unity's assembly reloads. `IDomainReloadStrategy` with `EditorApplication.LockReloadAssemblies()` to prevent reloads mid-tool-execution.
- **Path Sandboxing** — `PathSandbox.cs` ensures all file operations happen within the project root or the `.arcforge/` directory. No accidental writes outside.
- **Tool primitives** — the 75+ MCP tools UniClaude shipped are migration candidates. They will be ported to Hades's tool API style, with most of them retained.

What changes from UniClaude:

- **No Node.js Sidecar.** UniClaude had a separate Node.js process running the Anthropic Agent SDK, which called the MCP server's `/rpc` endpoint via custom JSON-RPC. Hades does not embed the Agent SDK and therefore does not need a sidecar. The agent client is external (Claude Code), and it speaks the standard MCP protocol directly to Hades's MCP server.
- **No chat UI.** UniClaude exposed a chat window in the Unity Editor as the user's primary interaction surface. Hades has no chat UI; the user interacts through their agent client.
- **MCP-compliant transport.** UniClaude used custom JSON-RPC over HTTP. Hades uses the MCP protocol's official transport (HTTP/SSE per the spec). The `MCPServer.cs` infrastructure is upgraded to expose MCP-compliant endpoints, but the underlying threading model and lifecycle handling stay identical.

The reuse is significant. We estimate approximately 60% of Hades's runtime infrastructure code is direct reuse from UniClaude with small adaptations, primarily around the MCP transport layer.

### 1.5 The communication backbone

Communication between the agent client and the Unity Package happens over **HTTP/SSE on localhost**. This is the same transport pattern UniClaude proved out, adapted to MCP-compliant message format.

Specifically:

- The Unity Package's MCP server listens on `http://localhost:<port>` where `<port>` is dynamically chosen at startup (default range 7780-7790; if all are busy, a random ephemeral port is used).
- The chosen port is written to a discovery file at `.arcforge/server.json` so the agent client can find it. This file is gitignored.
- The agent client reads `.arcforge/server.json` from the Unity project's working directory (resolved by the agent client's CWD or a config option), then connects to the discovered port.
- Standard MCP protocol messages flow over the connection: `initialize`, `tools/list`, `tools/call`, etc.
- For long-running operations or streamed responses, Server-Sent Events (SSE) is used.

This pattern was chosen over alternatives (file-based handoff, named pipes, Unix sockets) for the following reasons:

- HTTP/SSE is platform-portable. It works identically on Windows, macOS, and Linux without conditional code.
- The MCP protocol specifies HTTP and SSE as primary transports. Using them aligns Hades with the broader MCP ecosystem and ensures compatibility with all MCP clients, not just Claude Code.
- HTTP infrastructure (request handling, error codes, content negotiation) is mature and well-understood. Custom IPC primitives would require reinventing this.
- The localhost-only constraint provides enough security for the threat model (other processes on the same machine are not adversaries; the user owns the machine).

Performance is sufficient. The overhead of HTTP/SSE on localhost is on the order of 1-2ms per request, which is negligible compared to the time the agent itself takes to reason about responses (hundreds of milliseconds to multiple seconds). Even for high-frequency tool calls, the transport is not the bottleneck.

The trade-off is that running a Unity-internal HTTP server adds a small lifecycle complexity: it needs to start with the editor, survive domain reloads, and shut down cleanly. UniClaude's `MCPServer.cs` already handles all three correctly, so this complexity is absorbed.

### 1.6 The threading model

Unity APIs are not thread-safe. Most Unity calls (`AssetDatabase`, `SerializedObject`, `EditorApplication`, scene manipulation) must run on Unity's main thread. HTTP requests, however, arrive on background threads (the .NET HTTP listener runs on its own thread pool).

The bridge between these two worlds is the **Main Thread Bridge** pattern, inherited from UniClaude:

1. An HTTP request arrives on a background thread (let's call it `T_http`).
2. `T_http` parses the request, identifies the tool to call, and constructs a `WorkItem` describing the operation.
3. `T_http` enqueues the `WorkItem` onto a `ConcurrentQueue<WorkItem>` and blocks on a per-WorkItem `ManualResetEventSlim`.
4. On every `EditorApplication.update` tick (called by Unity on the main thread), the queue is drained: each `WorkItem` is executed (Unity APIs are now safe to call), the result is stored on the `WorkItem`, and the event is signaled.
5. `T_http` wakes up, reads the result from the `WorkItem`, and writes the HTTP response.

This design has these properties:

- All Unity API calls happen on the main thread.
- HTTP threads can serve multiple concurrent requests (up to the .NET HTTP listener's pool size).
- Each request has a 30-second timeout. If a request takes longer (e.g., a graph rebuild on a large project), the HTTP thread returns a timeout error while the main thread continues processing. The result is discarded when it eventually arrives.
- Domain reloads are blocked during in-flight requests via `EditorApplication.LockReloadAssemblies()`. This prevents Unity from reloading assemblies mid-operation, which would corrupt state.

The threading model is robust but adds latency. Worst-case, a request waits up to 16ms for the next `EditorApplication.update` tick (Unity's default frame rate). This is acceptable for the use cases Hades supports.

### 1.7 The lifecycle

A typical day in the life of Hades:

1. User opens the Unity Editor. `[InitializeOnLoad]` triggers Hades startup. The MCP server begins listening on a chosen port. The graph is loaded from disk (or rebuilt if the disk version is missing or outdated).
2. The graph is brought up to date with any project changes that happened while Unity was closed. This can take seconds to a minute on a large project.
3. The Charon emitter starts logging events.
4. The user opens their agent client (Claude Code). The Hades plugin is already installed; its MCP config points to the discovery file.
5. The agent client reads `.arcforge/server.json`, finds the port, and connects to the Hades MCP server. Standard MCP `initialize` handshake completes.
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
- **Independent MCP servers.** Each Unity instance starts its own MCP server on its own port. The default port range (7780-7790) accommodates ~10 simultaneous instances; beyond that, ephemeral ports are used. Two instances of Hades will never conflict because each writes its own discovery file at `<project>/.arcforge/server.json`.
- **Discovery file scoping.** The discovery file lives within the project directory. The agent client (Claude Code, etc.) resolves the discovery file via its current working directory, which is the project the agent is operating on. This automatically routes the right agent to the right Hades instance.
- **Independent dashboards.** When the user launches the Charon dashboard from Unity instance A, the dashboard process is scoped to Project A's traces database and uses port 7878. If the user launches a dashboard from Unity instance B, it gets the next available port (7879, 7880, etc.) and reads Project B's traces. The two dashboards run simultaneously without interference. The user can have multiple browser tabs open, one per project.
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

Hades Graph models both representations in a single coherent schema. Every asset is a node. Every GameObject within an asset is a node. Every Component within a GameObject is a node. Edges connect them with typed relationships: `contains`, `references`, `inherits_from` (for prefab variants), `instantiates`, `uses_material`, and so on.

This unified view is what allows queries like "find all prefabs that reference a deprecated script" to compose naturally. The query traverses asset edges (prefab references script) using the same machinery as "find all GameObjects in this scene that have a Light component" (which traverses runtime edges).

### 2.2 The schema

The schema is implemented as a SQLite database with two primary tables and several supporting tables.

#### 2.2.1 The `nodes` table

```sql
CREATE TABLE nodes (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  type TEXT NOT NULL,                   -- node type discriminator
  guid TEXT,                            -- Unity GUID for asset nodes; NULL otherwise
  file_id INTEGER,                      -- Unity fileID for sub-objects within an asset
  parent_node_id INTEGER REFERENCES nodes(id),  -- for runtime hierarchy
  name TEXT,                            -- human-readable name
  path TEXT,                            -- for asset nodes, the asset path
  source_range TEXT,                    -- for script nodes, file:line range as JSON
  properties TEXT,                      -- additional type-specific properties as JSON
  created_at INTEGER NOT NULL,          -- unix timestamp
  updated_at INTEGER NOT NULL
);

CREATE INDEX idx_nodes_type ON nodes(type);
CREATE INDEX idx_nodes_guid ON nodes(guid);
CREATE INDEX idx_nodes_path ON nodes(path);
CREATE INDEX idx_nodes_parent ON nodes(parent_node_id);
CREATE UNIQUE INDEX idx_nodes_guid_fileid ON nodes(guid, file_id) WHERE guid IS NOT NULL;
```

The `type` column is a string discriminator. Valid values are documented in the next subsection.

The `guid` and `file_id` together uniquely identify any asset or sub-object within an asset. For top-level assets, `file_id` is typically the Unity main object's fileID (often a well-known constant like `100100000` for prefab roots). For sub-objects (a Component within a prefab, a GameObject within a scene), `file_id` is the local identifier Unity assigns to that sub-object.

The `parent_node_id` provides the runtime hierarchy: a Component's parent is its GameObject, a GameObject's parent is its parent GameObject (or the scene/prefab root). This is duplicative with the `contains` edge type (described below) but is denormalized into the node table for fast hierarchy traversal queries.

The `properties` column is a JSON blob holding type-specific data. This is where flexibility lives: a Material node might have `{"shader": "URP/Lit", "color": "0xFF0000"}`, while a Component node might have `{"is_enabled": true, "execution_order": 100}`. Application code knows what schema to expect for each type.

The `source_range` column applies only to script-related nodes. When a node represents a class, method, or field within a C# file, this column captures the `file:start_line:end_line` location for navigation purposes.

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
-- Tracks the version of the graph schema, for migrations
CREATE TABLE schema_version (
  version INTEGER PRIMARY KEY,
  applied_at INTEGER NOT NULL
);

-- Tracks which assets have been scanned, with their content hash
-- Used to detect what needs re-scanning after Unity reopens
CREATE TABLE scanned_assets (
  guid TEXT PRIMARY KEY,
  content_hash TEXT NOT NULL,           -- hash of the asset file at last scan
  scanned_at INTEGER NOT NULL,
  scanner_version INTEGER NOT NULL      -- so we can re-scan if scanner changed
);

-- Tracks pending invalidations for the lazy-update mode (currently unused, reserved)
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
-- Example keys: "last_full_rebuild_at", "last_incremental_at", "build_count", "unity_version"
```

#### 2.2.4 Node types

The set of node types is closed (not arbitrary strings) and is extended only via schema migration. Currently defined:

**Asset-level types:**

- `Scene` — a `.unity` scene asset
- `Prefab` — a `.prefab` asset
- `PrefabVariant` — a prefab whose root is marked as a variant of another prefab
- `Script` — a `.cs` source file
- `ScriptType` — a class or struct defined within a script (substructure of `Script`)
- `ScriptMethod` — a method within a `ScriptType` (substructure)
- `ScriptableObject` — a `.asset` file containing a ScriptableObject instance
- `ScriptableObjectType` — a class inheriting from `UnityEngine.ScriptableObject`
- `Material` — a `.mat` asset
- `Shader` — a `.shader` or `.shadergraph` asset
- `Texture` — image assets (png, jpg, tga, etc.)
- `AudioClip` — audio assets
- `AnimationClip` — `.anim` assets
- `AnimatorController` — `.controller` assets
- `Mesh` — model assets (a mesh extracted from a model asset)
- `Model` — `.fbx`, `.obj`, etc.
- `RenderPipelineAsset` — URP/HDRP/custom SRP asset
- `AddressableGroup` — addressable group definition
- `AddressableEntry` — individual addressable entry within a group
- `BuildSettings` — project's build settings (singleton)
- `PhysicsSettings` — physics settings (singleton)
- `InputSettings` — input system settings (singleton)
- `Asset` — generic catch-all for other asset types

**Runtime-level types:**

- `GameObject` — a GameObject within a scene or prefab
- `Component` — a Component on a GameObject
- `ComponentField` — a serialized field on a Component (substructure)

**Project-level types:**

- `Project` — singleton root node, parent of nothing in the runtime sense, but a useful anchor for global queries

This list will expand over time. Adding a new type requires a schema migration that records the new type in a `node_types` reference table (not shown above; reserved for future use). Code paths that handle nodes must accept unknown types gracefully — log and skip rather than crash.

#### 2.2.5 Edge types

Similarly closed set. Currently defined:

**Containment:**

- `contains` — a parent contains a child. Scene contains GameObjects. GameObject contains Components. Prefab contains its GameObject tree. AddressableGroup contains AddressableEntries.

**Reference:**

- `references` — a serialized reference between objects. Component → ScriptableObject. Component → another GameObject. Scene → Prefab (via instantiation). Properties JSON describes the field name.

**Type relationships:**

- `instance_of` — links instance node to its type node. Component instance → ScriptType. ScriptableObject instance → ScriptableObjectType.
- `inherits_from` — type-level inheritance. PrefabVariant → Prefab base. ScriptType → ScriptType base.

**Asset relationships:**

- `uses_material` — Component (Renderer, etc.) → Material
- `uses_shader` — Material → Shader
- `uses_texture` — Material → Texture
- `uses_mesh` — Component (MeshFilter, etc.) → Mesh
- `uses_animation` — AnimatorController → AnimationClip
- `uses_audio` — AudioSource → AudioClip
- `uses_render_pipeline` — Project → RenderPipelineAsset

**Build relationships:**

- `included_in_build` — Scene → BuildSettings (with build index in properties)
- `addressable_for` — AddressableEntry → Asset

**Script-level:**

- `defines` — Script → ScriptType. ScriptType → ScriptMethod.
- `calls` — ScriptMethod → ScriptMethod. (Optional, only populated if Roslyn analysis is enabled; expensive.)

This list, like node types, is closed and expanded via migration.

### 2.3 The scanners

The scanners are the C# code inside the Unity Package that read the project and write the graph. There is one scanner per asset type, plus a coordinator that orchestrates them.

#### 2.3.1 The coordinator: `GraphBuilder`

`GraphBuilder` is the entry point. It exposes two main operations:

```csharp
public class GraphBuilder
{
    public void RebuildAll();                    // full rebuild from scratch
    public void UpdateAssets(string[] guids);    // incremental update for specific assets
    public BuildStatus GetStatus();              // current build state
}
```

A full rebuild walks `AssetDatabase.GetAllAssetPaths()`, dispatches each asset to the scanner registered for its type, and collects results into a transaction-batched write to the SQLite database. On a medium project (10k assets), this takes 15-45 seconds depending on machine speed.

An incremental update receives a list of GUIDs whose assets have changed. For each, it removes the existing node-and-subgraph from the database, re-scans the asset, and inserts the new nodes and edges. This is typically sub-second for individual asset updates.

#### 2.3.2 Scanner interface

Each asset-type scanner implements:

```csharp
public interface IAssetScanner
{
    string SupportedAssetType { get; }       // e.g., "Prefab"
    int Version { get; }                      // bumps when scanner output changes
    
    ScanResult Scan(string assetPath, AssetDatabase db);
}

public class ScanResult
{
    public List<NodeRecord> Nodes;
    public List<EdgeRecord> Edges;
    public List<ScanWarning> Warnings;
}
```

The scanner is given the asset path. It returns the nodes and edges that should exist in the graph as a result of that asset, plus any warnings (e.g., "this prefab has missing references"). The coordinator merges these results into the database.

#### 2.3.3 The individual scanners

**`SceneScanner`** scans a scene asset, walks the GameObject hierarchy, and produces a `Scene` node with `contains` edges to each top-level GameObject. For each GameObject, it produces a `GameObject` node with `contains` edges to its Components. For each Component, it produces a `Component` node with edges to: its `ScriptType` (`instance_of`), any referenced GameObjects (`references`), any referenced assets (`references`, `uses_material`, etc.), and so on.

The scanner has two operational modes depending on whether the scene is currently open in the editor:

- **Open-scene mode (preferred)**: if `EditorSceneManager.GetSceneAt()` finds the target scene already loaded, the scanner walks its in-memory hierarchy directly. No file I/O, no scene loading. This is the fast path used during incremental updates after `sceneSaved` events — Unity already has the scene in memory because the user just saved it.
- **Closed-scene mode (fallback)**: if the scene is not open, the scanner uses `EditorSceneManager.OpenScene()` in additive mode, walks the hierarchy, and closes the scene without saving. This is the slow path, used for full rebuilds or for scenes the user hasn't touched in this session.

The distinction matters significantly for performance. Open-scene mode is sub-second per scene; closed-scene mode is 1-3 seconds per scene because Unity has to deserialize the entire scene file. By preferring open-scene mode whenever possible, the typical incremental update on a saved scene is fast enough to be invisible to the user.

For full rebuilds that must process many closed scenes, the scanner runs in batches with progress reported through `EditorUtility.DisplayProgressBar`. Scenes are processed sequentially because `OpenScene` operates on the global scene state — concurrent opens would conflict.

A second optimization: when scanning multiple scenes in a row in closed-scene mode, the scanner detects "scene reopen storms" (>5 scenes opened/closed in 10 seconds) and switches to a **single-scene-at-a-time strategy with cached results** to avoid Unity's scene-management overhead from accumulating.

**`PrefabScanner`** is similar to `SceneScanner` and follows the same two-mode pattern. For prefabs currently open in the prefab stage (detected via `PrefabStageUtility.GetCurrentPrefabStage()`), the scanner walks the in-memory state directly. For other prefabs, it uses `PrefabUtility.LoadPrefabContents()` to load and `PrefabUtility.UnloadPrefabContents()` to release. It detects prefab variants by checking `PrefabUtility.GetCorrespondingObjectFromOriginalSource()` on the prefab root. For variants, it produces a `PrefabVariant` node and an `inherits_from` edge to the base prefab. Override information is recorded in the edge properties.

**`ScriptScanner`** parses C# files to extract types and methods. By default, it uses lightweight Roslyn (or a faster regex-based fallback) to identify class names, namespaces, base classes, and method signatures. Optional deep mode uses full Roslyn semantic analysis to extract method-call relationships, which populates the `calls` edge type. Deep mode is opt-in because it is expensive on large projects.

When deep mode is enabled, the following safeguards apply:

- **Per-file timeout**: 5 seconds default. If Roslyn semantic analysis on a single file exceeds this, the file is processed in shallow mode for this run and flagged in a `deep_mode_skipped` attribute on the resulting node. Configurable via `graph.deep_analysis_timeout_ms`.
- **Per-rebuild memory budget**: 1GB default for the entire deep analysis pass. Roslyn's `Compilation` objects can balloon on large projects with many references; the budget caps total in-flight allocations. When the budget is exceeded, deep mode automatically downgrades to shallow for the remainder of the rebuild and surfaces a warning in the dashboard.
- **Compilation reuse**: rather than constructing a new `Compilation` per file, the scanner builds one `Compilation` per assembly and reuses it across all files in that assembly. This is the difference between "tractable" and "intractable" performance on large projects.
- **Diagnostic log**: every deep analysis decision (succeeded, timed out, skipped due to budget) is logged with file path and duration. Users hitting performance issues can inspect the log to see which files are problematic.
- **Assembly filter**: by default, deep mode only analyzes assemblies in `Assets/` (user code). Package and built-in assemblies are skipped because they don't typically need call-graph analysis from Hades's perspective. Configurable via `graph.deep_analysis_assemblies`.

In practice, deep mode is most useful for medium-sized projects (10k-50k LOC of user C#) where the call graph is genuinely informative. For very large projects, the safeguards prevent runaway resource usage at the cost of incomplete `calls` edge coverage.

**`ScriptableObjectScanner`** distinguishes the type and the instance. For each `ScriptableObject` derivative type discovered (via `TypeCache.GetTypesDerivedFrom<ScriptableObject>()`), it creates a `ScriptableObjectType` node. For each `.asset` file containing a ScriptableObject instance, it loads the asset, identifies its type, creates a `ScriptableObject` node with an `instance_of` edge to the type node, and records serialized field values in properties.

**`MaterialScanner`** extracts shader, color, texture references, and rendering-pipeline-specific properties.

**`ShaderScanner`** distinguishes legacy shaders, surface shaders, and Shader Graph assets. For Shader Graph, it can extract input/output properties; for legacy shaders, it parses the `.shader` file for property declarations.

**`AddressablesScanner`** reads the addressable settings asset and produces `AddressableGroup` and `AddressableEntry` nodes with appropriate edges.

**`ProjectSettingsScanner`** reads the various `ProjectSettings/*.asset` files and produces singleton nodes for `BuildSettings`, `PhysicsSettings`, `InputSettings`, etc.

**`RenderPipelineScanner`** detects which render pipeline is active and produces a `RenderPipelineAsset` node with its features and quality settings.

Other scanners (Texture, AudioClip, AnimationClip, AnimatorController, Mesh, Model) are mostly metadata extraction with limited graph relationships.

#### 2.3.4 Scanner versioning

Each scanner has a `Version` integer. When a scanner's output format changes (e.g., we add new edge types it emits), the version is bumped. The `scanned_assets` table records the scanner version that produced each asset's data. On startup, if the registered scanner version is higher than the recorded version, the asset is automatically re-scanned.

This is the safety net for graph correctness when scanners evolve.

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
4. For imports and modifications: re-scan via the appropriate scanner.
5. The re-scan produces new nodes and edges. Compare with existing nodes/edges for the asset. Compute diffs.
6. Apply diffs in a single SQLite transaction: `INSERT` new nodes, `UPDATE` changed nodes, `DELETE` removed nodes, similarly for edges.
7. Update `scanned_assets` table with new content hash.
8. Emit a `graph.updated` event via Charon describing what changed.

The diff-based update is more complex than a wipe-and-rewrite per asset, but it preserves node IDs across updates. This matters because edges from other assets may reference these nodes; re-creating them with new IDs would require re-scanning every asset that links to them. The diff approach is significantly faster for large projects.

#### 2.4.4 Failure modes and recovery

Incremental updates can drift out of sync with reality. Possible causes:

- A Unity event fires but the handler crashes silently.
- A scanner has a bug that produces wrong output for some edge case.
- The user modifies files outside Unity (e.g., editing a `.unity` file in a text editor).
- Unity is closed while an update is in flight.

The system has multiple safety nets:

- **Periodic full validation**: every 24 hours of accumulated editor time, a background check compares `scanned_assets.content_hash` against actual file hashes for a sample of 5% of assets. Mismatches trigger re-scans.
- **Manual rebuild command**: `Hades: Rebuild Graph` menu option triggers a full rebuild. Documented as the recovery action for "the agent seems confused about my project."
- **Stale-on-startup detection**: when Unity opens, every asset's hash is checked against the recorded `scanned_assets.content_hash`. Mismatched assets are queued for re-scan during startup.
- **Scanner version check**: as described in 2.3.4, scanner version mismatch triggers re-scan.

These are belt-and-suspenders. The expected normal behavior is that incremental updates stay perfectly synchronized; the safety nets are there for the failure cases.

### 2.5 Querying the graph

The graph is queried through MCP tools. The tools translate agent intent into SQL and return structured results.

#### 2.5.1 Tool API philosophy

Per the architectural decision in the planning phase (hybrid approach), Hades exposes:

- **A small number of granular tools** for the most common queries. These are well-documented, deterministic, and easy for the agent to choose correctly.
- **A general-purpose query tool** as an escape hatch for cases the granular tools don't cover. This tool accepts a structured query expression rather than raw SQL, to keep the abstraction at the right level.

The exact tool set is TBD pending migration of UniClaude's existing 70+ tools to the Hades style. The migration sets the standard. Below is the planned high-level shape.

#### 2.5.2 Granular query tools (planned)

Examples of expected tools (final names and signatures TBD during migration):

- `get_project_summary(depth: shallow|medium|deep)` — returns a structured summary of the project: counts, render pipeline, key directories.
- `find_components_using_pattern(pattern_name: string)` — finds all components matching a known structural pattern (e.g., "ScriptableObjectChannel<T>"). Patterns are pre-defined.
- `find_references_to(target_path: string)` — finds all assets and components that reference a given asset.
- `trace_dependencies(asset_path: string, max_depth: int)` — recursively follows references from an asset.
- `find_orphan_scripts()` — scripts not referenced anywhere.
- `find_prefabs_with_component(component_type: string)` — locate all prefabs containing a given component type.
- `get_scene_summary(scene_path: string)` — high-level overview of a scene's structure.
- `get_prefab_inheritance(prefab_path: string)` — variant chain for a prefab.
- `analyze_render_pipeline()` — current pipeline, custom features, render features.
- `search_by_name(name_pattern: string, type_filter: string)` — search across nodes by name.
- `get_recently_changed(hours: int)` — assets changed in the last N hours.

Each tool has a clear input schema, a clear output schema, and clear documentation in the MCP `tools/list` response.

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

Empirical estimates (to be validated during development):

| Project size | Asset count | Full rebuild | Incremental (single asset) |
|---|---|---|---|
| Small | < 1k | 2-5 sec | < 100ms |
| Medium | 1k-10k | 5-30 sec | < 200ms |
| Large | 10k-50k | 30-120 sec | 200-500ms |
| Very large | 50k-200k | 2-8 min | 500ms-2sec |
| Enterprise | > 200k | varies | varies |

Most of the build time is in scene and prefab scanners (which open assets). Script scanning is fast in shallow mode, slow in deep mode (Roslyn semantic analysis).

#### 2.6.2 Query performance

For the schema and indexes described:

| Query type | Expected latency |
|---|---|
| Lookup by GUID | < 1ms |
| Lookup by path | < 1ms |
| List all nodes of a type | 1-10ms |
| Find references (one-hop) | 1-5ms |
| Find dependencies (5-hop traversal) | 10-50ms |
| Full-text search across names | 5-20ms (with FTS5 index) |
| Project-wide aggregations | 10-100ms |

These are generally well below the latency of agent reasoning, so the graph is unlikely to be the bottleneck in agent interactions.

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
PRAGMA journal_mode = WAL;          -- Write-Ahead Logging: readers don't block writers
PRAGMA synchronous = NORMAL;        -- safer than OFF, faster than FULL; OK for our durability needs
PRAGMA busy_timeout = 5000;         -- wait up to 5 sec for locks before erroring
PRAGMA cache_size = -65536;         -- 64MB page cache (negative = KB)
PRAGMA temp_store = MEMORY;         -- temp tables in memory, not on disk
PRAGMA mmap_size = 268435456;       -- 256MB memory-mapped I/O
PRAGMA foreign_keys = ON;           -- enforce ON DELETE CASCADE
```

`WAL` mode is the most consequential. Without it, SQLite uses rollback journaling, where readers block during writes. With WAL, readers see a consistent snapshot from before the in-progress write, and writes append to a separate WAL file that is checkpointed back to the main DB periodically. This is the precondition that makes the entire Hades concurrency model viable.

`busy_timeout = 5000` handles the rare case where a lock is held longer than expected (e.g., a complex multi-statement transaction). Instead of immediately failing with `SQLITE_BUSY`, the call waits up to 5 seconds. Beyond 5 seconds, the call errors and the caller can retry or surface the issue.

`synchronous = NORMAL` is a deliberate trade-off. `FULL` fsyncs after every transaction (safest, slowest). `OFF` skips fsyncs entirely (fastest, can lose recent writes on crash). `NORMAL` fsyncs at WAL checkpoints, which is the right balance: a power loss might lose the last few seconds of writes, but the database itself stays consistent. Hades's data is recomputable from the project source, so losing a few seconds of incremental updates is recoverable; corrupting the database is not.

#### 2.7.2 Properties

With this configuration:

- Multiple readers can read concurrently from any thread.
- One writer at a time (the GraphBuilder, on the main thread). Writers never wait on readers.
- Readers see a consistent snapshot, not affected by in-flight writes.
- Write throughput is limited by disk fsync at WAL checkpoint boundaries, not by individual writes. For the volume of writes Hades does (handfuls of nodes/edges per asset update), this is not a bottleneck.

#### 2.7.3 Consistency guarantees

- After `GraphBuilder.UpdateAssets()` returns, all subsequent queries see the new state.
- During an in-flight update, queries see the previous state.
- There is no staleness window where queries can see partial updates within a single transaction.
- Across multiple transactions (e.g., a large rebuild that splits writes into batches), readers may see intermediate states. The build coordinator wraps related batches in a single logical operation tagged in `graph_metadata` so the agent can detect "rebuild in progress" and respond accordingly.

#### 2.7.4 Locking behavior

- Writes acquire SQLite's exclusive lock briefly, on the order of microseconds. During this window, other writers block; readers do not.
- The Main Thread Bridge ensures writes are serialized at the application level — only one update is processed at a time. Even if multiple sources trigger updates concurrently, they queue.
- WAL checkpoints happen automatically (default: every 1000 pages of WAL). Checkpoints briefly hold a lock that blocks new writers; readers continue. Checkpoint duration is sub-millisecond for typical Hades load.

#### 2.7.5 The "rebuild in progress" signal

Per the failure scenario in Pipeline 12, queries during a graph rebuild can return partial data. To prevent silent staleness:

The `graph_metadata` table holds a row with key `current_operation`:

```
key                     value
current_operation       null  -- normal state
current_operation       '{"kind":"rebuild","started_at":1715240000,"affected_guids":[...]}'  -- mid-rebuild
```

Every query tool checks this row before executing. If a rebuild is in progress and the query touches affected GUIDs, the response includes a warning attribute: `"graph_state": "rebuilding", "affected_assets": ["guid1", "guid2"], "consider_retry_after_ms": 2000`.

The agent reads this attribute and either retries (for short rebuilds) or proceeds with explicit acknowledgment ("graph rebuild is in progress, results may be incomplete; here is what I see now").

This is the mechanism that prevents the failure mode from Pipeline 12: queries during rebuild no longer return empty silently; they return empty with explicit "this is incomplete" signaling.

### 2.8 Edge cases and known gotchas

Issues that the design must handle:

- **Missing references**: a prefab references a script, the script is deleted. The prefab now has a "missing reference" placeholder. The scanner detects this and creates a `Component` node with a `references_missing` flag in properties.
- **Circular prefab references**: prefab A contains prefab B, prefab B contains prefab A. Unity allows this in some configurations. The scanner detects cycles and emits a warning but does not crash.
- **Nested prefabs**: prefab A contains an instance of prefab B as a sub-object. The scanner produces nodes for B's sub-objects with appropriate `contains` edges, plus an `instantiates` edge from the GameObject in A to the prefab asset B.
- **Prefab variants with deep override chains**: variant V1 inherits from variant V2 inherits from base prefab P. The `inherits_from` edges form a chain. Override information is recorded at each level.
- **Multi-scene setups**: scenes loaded additively. The graph captures all referenced scenes via build settings. Runtime additive loading is captured if it goes through `BuildSettings.scenes` or addressables.
- **GUID collisions**: extraordinarily rare but possible if a project imports an asset package that uses the same GUIDs as existing assets. The scanner detects collisions and logs warnings.
- **`.meta` file desync**: if a file's content hash doesn't match its `.meta`, Unity's behavior is undefined. The scanner records both hashes in node properties for debugging.
- **Deleted-but-referenced assets**: an asset is deleted but other assets still reference it (Unity creates "missing" placeholders). The scanner records these as `references_missing` for visibility.

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

Where the scanner can detect that a static reference is incomplete (e.g., a Component with a missing reference, an Addressables call with a non-literal key), it emits explicit signals:

- Nodes have `analysis_completeness: full | partial | runtime_only` properties.
- Edges have `confidence: high | medium | low` properties where applicable.
- Detected dynamic patterns (reflection calls, addressable loads, etc.) produce `dynamic_dispatch_marker` nodes that are visible to the agent.

The agent, when asked questions whose answers depend on these blind spots, surfaces the limitation: "I see UseLegacyAuth is called from 4 places statically, but I notice the codebase uses reflection in 3 spots that might invoke it dynamically. Static analysis cannot detect those."

#### 2.9.3 Future runtime instrumentation

The boundaries described above are the limits of static analysis. A future Hades version may add **runtime instrumentation** — hooks that capture actual relationships during play mode (which addressables actually loaded, which systems actually processed which entities, which DI bindings actually resolved). This would supplement the static graph with runtime evidence.

This is explicitly out of scope for v1. Static graph is hard enough; runtime instrumentation is a substantially larger undertaking. But the graph schema is designed to accommodate it later: edges have a `evidence_source: static | runtime | both` property already reserved.

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

Charon is the observability layer. Every meaningful event in Hades — every MCP tool call, every graph query, every memory read, every action against Unity — is captured as a structured trace. The traces serve two distinct purposes: internal debugging for ArcForge developers building Hades, and external visibility for users debugging their own AI workflows.

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
- `name` — descriptive name like `mcp.tool.find_prefabs_with_component` or `graph.query.execute`
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
  status TEXT,                    -- OK, ERROR, TIMEOUT, IN_PROGRESS
  total_duration_ms INTEGER,
  span_count INTEGER,
  user_outcome TEXT,              -- accepted, rejected, edited (set later)
  user_outcome_set_at INTEGER,
  attributes TEXT                 -- top-level trace attributes as JSON
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
  events TEXT                     -- JSON array
);

CREATE INDEX idx_spans_trace ON spans(trace_id, start_time);
CREATE INDEX idx_spans_name ON spans(name);
CREATE INDEX idx_traces_start_time ON traces(start_time DESC);
CREATE INDEX idx_traces_status ON traces(status);
CREATE INDEX idx_traces_outcome ON traces(user_outcome);
```

The denormalization is intentional: traces have a few summary fields lifted from their root span for fast filtering, while detailed span data lives in `spans`.

### 3.3 The emitter

The `CharonEmitter` is the C# class inside the Unity Package responsible for producing trace data. It exposes a fluent API:

```csharp
using (var span = Charon.StartSpan("mcp.tool.find_prefabs_with_component", SpanKind.Server))
{
    span.SetAttribute("component_type", componentType);
    
    // ... do work ...
    
    using (var childSpan = Charon.StartSpan("graph.query.execute", SpanKind.Internal))
    {
        childSpan.SetAttribute("query.kind", "find_prefabs_with_component");
        // ... query the graph ...
        childSpan.SetAttribute("results.count", results.Count);
    }
    
    span.SetAttribute("results.count", results.Count);
    span.SetStatus(SpanStatus.Ok);
}
```

The emitter handles:

- Generating IDs (using a snowflake-style scheme for time-orderability).
- Tracking the active span via `AsyncLocal<Span>` so child spans implicitly nest correctly.
- Buffering writes to avoid blocking work on disk I/O. A background task drains the buffer to SQLite every 500ms or when the buffer reaches 1000 spans, whichever comes first.
- Handling crashes: spans are written to a write-ahead log first, and the WAL is checkpointed to the main DB periodically. Even if Unity crashes mid-trace, completed spans are not lost.

#### 3.3.1 Cross-process trace IDs

When the agent client makes an MCP call, the trace ID needs to be threaded through. The MCP protocol does not have first-class trace context, so we use a custom header: `X-Hades-Trace-Id`. If present, the Unity Package uses it as the trace ID for the root span; if absent, it generates a new one. The agent client's plugin can be configured to inject this header (with a generated trace ID) for every tool call.

This enables end-to-end traces that span the agent's reasoning and Unity's execution, when the agent client cooperates. When it doesn't, traces are still complete on the Unity side but not linked to agent-side context.

### 3.4 What gets instrumented

The set of instrumented operations defines what is observable. Hades instruments:

#### 3.4.1 MCP tool calls

Every incoming MCP tool call creates a root span with:

- `name`: `mcp.tool.<tool_name>`
- `kind`: `Server`
- attributes: tool name, parameter values (PII-redacted if configured), client identifier (which agent client called us)
- child spans for any sub-operations the tool performs

#### 3.4.2 Graph queries

Every database query that the graph layer issues — whether from a tool or from internal scanning — emits a span:

- `name`: `graph.query.<operation>` (e.g., `graph.query.find_references`)
- `kind`: `Internal`
- attributes: query kind, parameter values, result count, latency

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

Traces can contain sensitive information: file paths, user-typed prompts (if forwarded by the agent client), code snippets, project structure. Because traces are local-first, this is controllable but still important.

Defaults:

- **Local-only by default.** Traces are stored in `.arcforge/traces.db` and never transmitted unless the user explicitly exports.
- **Path redaction**: configurable. Off by default (paths help debug). Can be enabled to replace project paths with hashes.
- **Content redaction**: file contents are not captured by default. Only metadata about content (hash, size, type) is stored. This prevents traces from accumulating sensitive code.
- **Retention policy**: configurable. Default is 30 days. Older traces are pruned by a background task that runs on Unity startup.
- **Export controls**: the user can export traces (e.g., to share with us for debugging). Export goes through an explicit UI action, never silently. Exports can be filtered (date range, trace ID, scrubbed of paths, etc.).

The eval dataset feature (described in 3.7) operates on traces that the user has explicitly opted into. By default, eval-datasets-from-production is opt-in.

### 3.6 The Charon dashboard

The dashboard is a separate Node.js process that reads `traces.db` and renders a local web UI on `http://localhost:<port>`. The default port is 7878; if it is already in use (e.g., because the user has another dashboard running for a different project per §1.8), the process tries 7879, 7880, etc., until it finds a free one. The chosen port is reported back to the user via the launch command output and via the Unity Editor menu item that opened it. It is started on demand:

- A menu item in the Unity Editor: `Hades: Open Charon Dashboard`. This launches the Node.js process and opens the user's browser to the URL.
- A CLI command: `hades-charon` (installed by the Unity Package's setup wizard). Useful for users who prefer terminal.

The dashboard is local-first and does not require an internet connection.

#### 3.6.1 What the dashboard shows

The main views:

- **Trace list**: filterable, sortable. Default view shows recent traces with their status, duration, and outcome. Filters: time range, status (OK/ERROR), trace name pattern, outcome.
- **Trace detail**: a flame graph or waterfall view of the trace's spans. Click any span to see its attributes and events. Useful for understanding what happened during a single agent interaction.
- **Aggregations**: latency distribution per tool, error rate per tool, throughput over time. Useful for identifying performance regressions.
- **Eval datasets**: sets of traces marked as "this is canonical behavior we want to preserve" or "this was a failure that shouldn't recur." Used for regression testing.
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

A dataset is a curated set of traces, each tagged with an expected outcome. Datasets are stored in the same `traces.db` with extra tagging:

```sql
CREATE TABLE eval_datasets (
  dataset_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT,
  created_at INTEGER NOT NULL
);

CREATE TABLE eval_dataset_members (
  dataset_id TEXT REFERENCES eval_datasets(dataset_id),
  trace_id TEXT REFERENCES traces(trace_id),
  expected_outcome TEXT,                -- accepted, rejected, custom
  expected_attributes TEXT,             -- JSON, for custom assertion
  notes TEXT,
  PRIMARY KEY (dataset_id, trace_id)
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

For certain trace types (suggestion-and-outcome traces), we use a separate LLM call to judge whether the agent's suggestion was good. This requires:

- Sending the trace context (prompts, responses, project structure summary) to a separate LLM endpoint.
- Receiving a structured judgment.
- Storing the judgment in the trace's attributes.

This feature is opt-in because it sends data to an external LLM. When enabled, the user configures which LLM endpoint to use (their own Claude API key, or another provider).

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

- 30 days of traces by default. Older are auto-pruned at startup.
- Eval-dataset traces are exempt from auto-pruning regardless of age.
- Manual pruning: `hades-charon prune --older-than 7d` for explicit control.
- Compression: SQLite supports compression of certain payload types via VACUUM. Not used by default; available if the database grows uncomfortably large.

### 3.10 Edge cases

- **Trace explosion under bursty load**: the agent makes 1000 tool calls in a session. The buffer absorbs short bursts; sustained load eventually backpressures (the emitter will block briefly waiting for the buffer drain). Users notice as a small latency increase, not as failure.
- **Concurrent emitters from multiple Unity processes**: a common scenario — the user has multiple Unity instances open on different projects simultaneously. Each instance writes to its own per-project `traces.db` (per §1.8). There is no shared trace database, so SQLite's single-writer constraint is automatically satisfied within each project. Resource contention (CPU, disk I/O) scales with the number of concurrent instances but does not affect correctness.
- **Disk full**: if the trace database can't be written, the emitter fails the write and increments an internal error counter. After 100 consecutive failures, Charon goes into a degraded "drop traces" mode and surfaces a warning. Trace data is lossy in this mode but Hades does not crash.
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
```

#### 4.2.2 Tier 2: Inferred memory

Tier 2 is auto-generated from observability traces. It captures patterns the system observes across sessions:

- "User accepted suggestions matching pattern X 47 times, rejected 3 times. Confidence 94%."
- "Suggestions involving Resources.Load were rejected 89% of the time. The project appears to use Addressables instead."
- "User mentions performance optimization in 30% of requests."

Tier 2 files live at `.arcforge/memory/inferred/` and are gitignored by default. They are noisy, frequently updated, and not directly informative for humans (they are essentially statistical summaries).

When confidence in an inferred pattern is high (configurable threshold, default 90% over a minimum sample size), the system can promote the pattern to Tier 1 — but always with developer review. The promotion is not automatic; it appears as a suggestion in a queue, the developer either approves (in which case the pattern is added to `patterns.md`) or dismisses.

### 4.3 The memory writer

Asphodel writes are gated. There are several paths through which memory gets written, each with different controls:

#### 4.3.1 Direct human edit

The developer opens `patterns.md` in their text editor, edits, saves. Asphodel notices the file change (via FileSystemWatcher) and updates the validation status. Direct edits are unrestricted — Asphodel does not impose schema validation that would prevent the developer from writing what they want.

#### 4.3.2 Agent proposal

The agent calls the MCP tool `propose_memory_update(file: string, content: string, rationale: string)`. The proposal does not modify the file directly; it is added to a "pending proposals" queue at `.arcforge/memory/proposals/`. The developer reviews proposals in the Charon dashboard or via a CLI command and approves or rejects each.

This design ensures the agent cannot silently rewrite the project's memory. Human review is mandatory.

#### 4.3.3 Inferred update

The Tier 2 system writes to inferred files automatically based on trace analysis. These updates are unrestricted because they are scoped to Tier 2 (gitignored, auto-generated). They are clearly labeled as inferred so a human reading the file knows the data is statistical.

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

Each memory entry can have associated validation rules. The rules are encoded as graph queries in YAML frontmatter:

```yaml
---
patterns:
  - name: "SO Event Channels"
    validation:
      query_type: "exists"
      query: "find_assets({type: 'ScriptableObject', name_pattern: '*Channel'})"
      min_count: 3
      validation_failure_message: "Pattern claims SO event channels are used but found fewer than 3 in the project."
---
```

When the C# validator runs:

1. Reads the memory file and parses frontmatter.
2. For each rule with a query, the query is executed against the current graph state.
3. The result is compared against the expected outcome encoded in the rule.
4. The validator updates the file's frontmatter (`validation_status: ok | warning | error`) and timestamp.
5. On mismatch, an inline HTML comment is added to the file describing the inconsistency.

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

These patterns are written to `inferred/observed_patterns.md`, `inferred/preferences.md`, etc. The task runs daily and is rate-limited to avoid overhead.

#### 4.6.1 Inference labeling discipline

A critical design constraint: **inferred patterns are never injected into agent context as authoritative**. They are clearly marked at every stage of their lifecycle.

In the markdown files themselves, every inferred entry has frontmatter and inline labeling:

```yaml
---
status: inferred
confidence: 0.93
sample_size: 67
first_observed: 2026-04-15
last_confirmed: 2026-05-08
promotion_status: candidate  # candidate | proposed | promoted | dismissed
---

# INFERRED PATTERN (not confirmed by team)

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

The user's options:

- **Accept**: pattern moves to Tier 1 with explicit confirmation. The Tier 2 entry is archived.
- **Modify and accept**: user edits the proposed text before accepting. Useful when the inference is approximately right but needs refinement.
- **Dismiss**: the pattern is marked dismissed in Tier 2. The system stops proposing it but keeps observing (in case the underlying behavior changes).
- **Defer**: review later. The pattern stays as Tier 2 candidate until acted on.

The promotion is never automatic. This is intentional. Inference is suggestion, not authority. The user holds the final word on what enters Tier 1.

The pattern-detection logic is open-source and inspectable. Users who don't trust the inferences can disable Tier 2 entirely; Hades remains useful with Tier 1 only.

### 4.7 Privacy and data handling

Memory in Tier 1 is git-tracked and shared with the team. The developer has full control over what goes in (directly via text editing or via approving proposals).

Memory in Tier 2 is local and gitignored by default. It contains aggregate behavioral data — what kinds of things the user accepts and rejects. This is sensitive in some senses (it reflects working habits) and innocuous in others (it's anonymized and statistical).

The user can:

- Disable Tier 2 entirely.
- Inspect Tier 2 files directly (they are markdown).
- Delete Tier 2 files at any time; they will regenerate from new traces.
- Export Tier 2 (e.g., to share with us for debugging).

### 4.8 Edge cases

- **Memory file deleted while running**: Asphodel detects the deletion and treats it as "this memory no longer exists." The agent is informed via an updated summary.
- **Memory file with invalid frontmatter**: parser logs a warning and skips that file. The rest of the system continues.
- **Conflicting rules between memory entries**: e.g., one entry says "use Pattern X," another says "Pattern X is deprecated." Asphodel does not resolve these automatically; both are surfaced to the agent, which then has to reason about the conflict (or asks the user).
- **Memory grows unboundedly**: each memory file has a soft size limit (default 50KB). Above this, a warning is surfaced suggesting archival. The user can override the limit.
- **Multi-developer memory churn**: two developers edit the same memory file in different branches, then merge. Standard git merge conflicts apply. Asphodel does not have its own conflict resolution; it relies on git's.
- **Validation queries become slow**: as the graph grows, some validation queries may become expensive. Validation has a per-query budget (default 1 second). Queries exceeding the budget are skipped with a warning.


---

## 5. Hades Skills

The Skills layer is the conceptually simplest of the four pillars but the one with the most direct user-visible impact. Where Graph, Charon, and Asphodel work behind the scenes, Skills are what the agent actively pulls from when reasoning about Unity-specific tasks.

### 5.1 What a skill is, technically

A skill, in Claude Code's terminology, is a markdown file with a specific structure:

```yaml
---
name: unity-architect
description: |
  Use when the user asks architectural questions about Unity projects: 
  "how should I structure X", "what's the best way to handle Y", 
  decisions about MonoBehaviours vs ScriptableObjects vs static classes.
allowed_tools: ["Bash", "Read", "Grep", "Glob"]
---

# Unity Architect Skill

[markdown content with decision frameworks, examples, etc.]
```

The frontmatter declares when the skill should activate (the description is matched against the agent's current task) and what tools the skill is allowed to invoke. The body is markdown content that the agent reads when the skill activates.

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

#### 5.2.3 Domain skills (planned expansion)

The new skills the Vision identified as gaps in UniClaude:

- `unity-ui` — UI Toolkit, uGUI, responsive layouts, dialog systems.
- `unity-networking` — Netcode for GameObjects, Mirror, Fishnet decision frameworks.
- `unity-ai-behavior` — state machines, behavior trees, GOAP, NavMesh.
- `unity-audio` — audio manager patterns, mixers, spatial audio.
- `unity-input` — new Input System, action maps, multi-device.
- `unity-shaders` — Shader Graph, VFX Graph, particle systems, render features.
- `unity-addressables` — Addressables vs Resources vs AssetBundles, async loading.
- `unity-recipes` — common gameplay patterns: health, inventory, save, spawn waves.
- `unity-ecs` — when to use ECS, Burst, hybrid approaches.
- `unity-testing` — EditMode vs PlayMode tests, what to test, mocking.

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

Skills are versioned via the plugin manifest. Each skill change increments the plugin's minor version. The plugin's `plugin.json` declares its compatibility range with Hades MCP server versions.

When the agent client loads the plugin, it checks for compatibility:

- If the plugin's required MCP server version is higher than what's installed, the agent client warns and asks the user to update.
- If the MCP server's version is much newer than the plugin expects, the plugin still loads but a warning is shown.

This versioning model is essential because skill behavior often depends on specific MCP tool signatures. Mismatched versions silently producing wrong behavior is the failure mode we are trying to avoid.

### 5.6 Distribution

Skills ship as a Claude Code plugin via the Hades self-published marketplace at `arcforge/hades-marketplace` (per Vision §7.5). Users install with:

```
/plugin marketplace add arcforge/hades-marketplace
/plugin install hades
```

The plugin contents:

```
hades-plugin/
├── .claude-plugin/
│   └── plugin.json              # plugin metadata and compatibility info
├── skills/
│   ├── unity-architect/
│   │   ├── SKILL.md            # main skill content
│   │   └── references/         # supplementary materials
│   │       ├── decision-tree.md
│   │       └── examples.md
│   ├── component-design/
│   │   └── SKILL.md
│   └── ... (other skills)
├── commands/
│   ├── hades-status.md         # /hades:status command
│   ├── hades-rebuild.md        # /hades:rebuild-graph command
│   └── hades-traces.md         # /hades:show-traces command  
├── .mcp.json                    # MCP server config: connects to local Hades
└── README.md
```

The `.mcp.json` declares the connection to the Hades MCP server:

```json
{
  "mcpServers": {
    "hades": {
      "command": "node",
      "args": ["${HADES_INSTALL_PATH}/dist/mcp-stdio-bridge.js"],
      "env": {
        "HADES_PROJECT_PATH": "${CWD}",
        "HADES_DISCOVERY_FILE": "${CWD}/.arcforge/server.json"
      }
    }
  }
}
```

The bridge process (Node.js) reads the discovery file, connects to the Unity-side HTTP MCP server, and proxies MCP messages over stdio to the agent client. This indirection allows the agent client to use stdio transport (which is its default) while the Unity Package uses HTTP (which fits its lifecycle better).

#### 5.6.1 Repository strategy

Development uses a single monorepo (`arcforge/hades`) which is the Unity Package itself — installable directly via UPM git URL. The Bridge (`Bridge~/`), plugin manifest (`.claude-plugin/`), fixtures (`Fixtures~/`), and CI all coexist here. The tilde-suffixed directories are ignored by Unity's asset pipeline but tracked in git.

The second repository (`arcforge/hades-marketplace`) is introduced in two stages:

- **Phase 1:** Create the marketplace repo as an empty skeleton — placeholder `plugin.json`, valid `.mcp.json` pointing at the bridge, no skills. This establishes the distribution channel and lets early adopters install the plugin even before skills exist.
- **Phase 4:** Populate the marketplace repo with migrated UniClaude skills and new skill content. At this point the repo becomes a real distribution artifact with CI validation for manifest correctness and skill structure.

During development, skill drafts may live in the monorepo for convenience (e.g., under a `skills~/` directory). The marketplace repo is the shipping vehicle, not the authoring environment.

### 5.7 Slash commands

In addition to skills (which activate based on context matching), the plugin includes slash commands that the user can invoke explicitly:

- `/hades:status` — shows current Hades state: graph version, last build time, trace count, memory file count.
- `/hades:rebuild-graph` — triggers a full graph rebuild. Used when the user suspects the graph is stale.
- `/hades:show-traces` — opens the Charon dashboard.
- `/hades:validate-memory` — runs validation across all memory files.
- `/hades:propose-memory <file> <content>` — explicit way to propose a memory update.
- `/hades:export-traces` — exports traces in the configured format.

These provide explicit user control over Hades behavior, complementing the implicit behavior driven by skill activation.


---

## 6. Integration: How the layers compose

The previous four chapters described each layer in isolation. This chapter explains how they work together. The integration is what differentiates Hades from running four independent tools side by side.

### 6.1 The integration principles

Three principles govern how the layers compose:

**Layered access, not direct coupling.** Charon doesn't reach into Asphodel's files. Asphodel doesn't query the trace database. They communicate through well-defined interfaces. This keeps each layer testable in isolation.

**Events flow upward, queries flow downward.** Lower layers (Graph) emit events when state changes. Upper layers (Skills) query lower layers when they need information. The asymmetry keeps the dependency graph clean.

**Failure is local.** If Charon stops emitting traces, Graph and Asphodel still function. If Asphodel's files are corrupt, Graph and Charon are unaffected. The layers degrade independently.

### 6.2 The event flow

The system has an internal event bus inside the Unity Package. Events flow as follows:

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

All events go to Charon as trace spans. Some events are also consumed by Asphodel for self-validation triggering.

The event bus implementation is straightforward — a `Dictionary<string, List<Action<Event>>>` keyed by event name. Subscribers register handlers, publishers invoke them. The bus runs on the main thread; subscribers don't need to be thread-safe.

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
2. Graph emits `graph.pattern_emerged` event.
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

Hades has user-configurable options. These live in `.arcforge/config.yaml`:

```yaml
graph:
  scanner_versions: auto    # or pin to specific versions
  deep_script_analysis: false  # enable Roslyn-based call graphs
  rebuild_threshold_ms: 30000  # max acceptable rebuild time before falling back

charon:
  retention_days: 30
  redact_paths: false
  redact_user_input: false  # whether to scrub user prompts from traces
  dashboard_port: 7878

asphodel:
  tier2_enabled: true
  promotion_confidence: 0.90
  promotion_min_samples: 50
  validation_on_startup: true
  validation_query_budget_ms: 1000

mcp:
  port_range: [7780, 7790]
  request_timeout_ms: 30000
```

This file is git-tracked. Teams can share configuration. Per-developer overrides go to `.arcforge/config.local.yaml`, which is gitignored.

### 6.7 Confidence modeling and graceful uncertainty

Hades is a system where wrong answers are worse than no answer. If the agent makes confident architectural recommendations based on stale or incomplete graph data, the user notices once or twice and stops trusting Hades. Trust, once lost, is hard to rebuild. Therefore the architecture treats uncertainty as first-class.

#### 6.7.1 Sources of uncertainty

Every layer can produce uncertain or incomplete data:

- **Graph**: stale due to in-progress rebuild, incomplete due to scanner failures, blind to dynamic patterns (per §2.9).
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
      {"factor": "static_analysis_coverage", "value": "partial", "blind_spots": ["reflection", "addressables_by_key"]}
    ],
    "recommendations": [
      "consider retrying after rebuild completes (estimated 2-5 seconds)",
      "manually verify reflection-based code paths"
    ]
  }
}
```

The agent receives this alongside the data. Skills are calibrated to read the confidence block and adapt the response: high confidence → assertive recommendations; medium → assertive but with stated caveats; low → exploratory tone with explicit "I'm not sure about X".

#### 6.7.3 Graceful degradation paths

Each layer has a defined behavior when uncertainty becomes unmanageable:

- **Graph rebuild in progress**: queries return current-best data with explicit "rebuilding" attribute. Agent decides to wait or proceed with caveat.
- **Memory validation failure**: contradicting memory entries surface to the agent as "your memory says X, the project shows Y". Agent does not silently choose one.
- **Tier 2 inference low confidence**: never injected as authoritative pattern. Available via explicit `recall_inferred(query)` tool, where the response is clearly labeled as "INFERRED, sample size N, confidence X".
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
7. Tool implementation queries the graph:
   - Charon child span: `graph.query.scene_summary`.
   - SQL query: find Scene node by path, find all GameObject nodes contained in it (via `contains` edges), grouped by hierarchy depth.
   - Returns ~12 top-level GameObjects with their components.
   - Child span ends.
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
2. For each pattern, agent calls a corresponding `find_violations` tool (e.g., for "use SO event channels," it calls `find_components_using_pattern("UnityEvent", inverse: true)` to find places using UnityEvent that should use SO channels).
3. Agent collects violations across all patterns.
4. Agent calls `validate_memory_against_graph()` (a meta-tool that runs all memory validations).
   - This returns the system's own automatic validation results.
5. Agent combines manual and automatic results into a report.

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
2. Agent calls `find_recently_changed(hours: 24)` to confirm these are recent changes.
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

1. New dev clones the repo. `.arcforge/memory/` (Tier 1) comes with the clone. Tier 2 directory is gitignored, so empty.
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

1. Agent calls `find_method_callers(class: "AuthManager", method: "UseLegacyAuth")`.
   - This is the kind of query that requires the deep script analysis (Roslyn `calls` edges).
   - If deep analysis is enabled, returns precise callers: 4 scripts, 12 call sites.
   - If deep analysis is disabled, returns a less precise set via text search: ~5 scripts that mention the method.
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
3. User filters traces by date (yesterday) and trace name (`mcp.tool.modify_prefab` or similar).
4. User finds the trace where the prefab modification happened.
5. User clicks into the trace. Sees the full span tree:
   - Root span: `mcp.tool.modify_prefab(path: "Assets/Prefabs/Player.prefab", change: {...})`
   - Child span: `graph.query.find_references(asset: "Assets/Prefabs/Player.prefab")` — this returned empty.
   - Child span: `unity.action.modify_prefab` — successful.
6. User notices the empty result on the references query. This is suspicious because Player.prefab is referenced from many scenes.
7. User clicks the empty-result span. Attributes show: `query.executed_at: 2026-05-08T14:30:21Z`, `graph.last_rebuild: 2026-05-08T14:30:18Z` (3 seconds before the query).
8. Diagnosis: a graph rebuild was in progress at the moment the agent queried. The rebuild had not yet processed Player.prefab's references when the query ran. The query returned empty, and the agent assumed no references existed.
9. Diagnosis pinpoints the bug: the graph layer should have indicated to the query that the rebuild was in progress for the relevant assets, but it didn't. The fix is to add a "rebuild status" check before queries that would be misleading if returning partial data.
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

**Edge case:** if the user force-quits Unity during a tool call, the lock is dropped and the next reload proceeds normally. The trace span for the interrupted call is left open with `status: TIMEOUT` after a recovery sweep on next startup.

#### 8.1.2 Play mode transition

**What happens:** User enters play mode. Most editor APIs are still available, but some restrictions apply. User exits play mode. Sometimes scenes are reset.

**Risk to Hades:** Asset state may differ between edit and play mode. Scanners running during play mode could capture transient runtime state.

**Mitigation:** Hades pauses incremental graph updates during play mode by default. The graph reflects the edit-mode state. If the user enters play mode and the project is modified there (via runtime inspector edits), those changes are not reflected in the graph until exit. A configuration option allows opt-in graph updates during play mode for users who want it.

Charon continues operating during play mode — observability of agent actions during play sessions is valuable.

#### 8.1.3 Unity crash

**What happens:** Unity crashes hard, OS kills the process, power outage, etc. No clean shutdown.

**Risk to Hades:** SQLite databases could be corrupted. Trace WAL not checkpointed. Memory file edits not flushed.

**Mitigation:**

- SQLite is in WAL mode with default checkpoint frequency. After a crash, SQLite automatically replays the WAL on next open. Database integrity is preserved unless the disk itself is corrupted.
- Charon's trace buffer flushes every 500ms or 1000 spans. Worst-case data loss is the last 500ms of traces.
- Memory file writes use atomic rename (write to temp file, fsync, rename over original). Either the old or new content exists, never partial.
- On startup, Hades runs a recovery check: integrity-check the SQLite databases, verify memory files parse, rebuild if necessary.

#### 8.1.4 Editor freeze (long-running tool call)

**What happens:** A tool call takes longer than expected — graph rebuild on a very large project, scanner stuck in infinite loop, etc.

**Risk to Hades:** Unity Editor becomes unresponsive while the tool runs on the main thread.

**Mitigation:**

- Default 30-second timeout on all tool calls. After 30 seconds, the HTTP thread returns a timeout error to the client.
- The main thread continues processing the tool call — it may eventually succeed or fail naturally. Result is discarded if the client has timed out.
- For known long-running operations (full graph rebuild), the tool surfaces progress via `EditorApplication.DisplayProgressBar` so the user knows Unity is working, not frozen.
- A `Hades: Cancel In-Flight Operations` menu command allows the user to forcibly cancel main-thread operations. Implementation uses `CancellationToken` checks at scanner work boundaries.

### 8.2 Graph-level failures

#### 8.2.1 Scanner crashes on a specific asset

**What happens:** A scanner throws an exception while processing an asset. Could be malformed asset, edge case in Unity's API, or a scanner bug.

**Risk to Hades:** If unhandled, the entire build aborts. Worse: in incremental mode, the asset never gets re-scanned, and the graph is permanently stale for that asset.

**Mitigation:**

- Each scanner invocation is wrapped in try-catch. Exceptions are caught, logged, and the scanner is marked as failed for that asset.
- Failed assets get a `Component` node with `scan_status: failed` and the exception message in properties. The agent can see this and report "I can't analyze this asset; the scanner failed."
- Failed assets are added to a retry queue. After 5 minutes, they are re-attempted. After 3 retries, they are quarantined and surfaced in a warning to the user.
- The build itself continues. One bad asset doesn't halt scanning the rest.

#### 8.2.2 Database corruption

**What happens:** Disk error, filesystem bug, or extreme edge case corrupts the SQLite database.

**Risk to Hades:** Queries fail, possibly silently returning wrong data.

**Mitigation:**

- On startup, run `PRAGMA integrity_check`. If failure detected, surface to user: "graph database may be corrupted, rebuild recommended."
- Provide easy `Hades: Rebuild Graph` command. The graph is recomputable from the project source — corruption is annoying but not catastrophic.
- If corruption recurs, hint at filesystem investigation (different machines? cloud-synced project directory?).

#### 8.2.3 Graph and reality drift

**What happens:** The graph is technically valid but doesn't match the project state. Could happen if AssetPostprocessor doesn't fire (Unity bug), incremental update logic has a hole, or the user modifies files outside Unity.

**Risk to Hades:** Agents give wrong answers based on stale graph data.

**Mitigation:**

- Periodic 5%-sample integrity check (every 24 hours of editor time) compares `scanned_assets.content_hash` to actual file hashes. Mismatches trigger re-scan.
- Memory self-validation surfaces inconsistencies between memory claims and graph state. If the graph itself is wrong, this often surfaces as "memory says X, graph says Y" warnings.
- The user can run `/hades:rebuild-graph` at any time as recovery.

### 8.3 Charon-level failures

#### 8.3.1 Trace database fills the disk

**What happens:** Heavy use over months without pruning. Trace database reaches GBs.

**Risk to Hades:** Disk full prevents writes. Hades emitter can't flush. Eventually backpressures all operations.

**Mitigation:**

- Default retention: 30 days. Auto-prune on startup.
- Soft warning at 1GB trace database size: dashboard surfaces a notification.
- Hard guard at 80% disk fill: emitter switches to drop-traces mode rather than blocking. Trace data is sacrificed to keep Hades functional.

#### 8.3.2 Dashboard process crashes

**What happens:** The Charon dashboard Node.js process crashes or hangs.

**Risk to Hades:** User can't view traces.

**Mitigation:** Dashboard is independent of the Unity Package. Crash of dashboard doesn't affect graph, memory, or MCP server. User restarts dashboard. Trace data is intact in the database.

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

**What happens:** The default port range (7780-7790) is all in use by other applications.

**Risk to Hades:** MCP server can't start. Agent client can't connect.

**Mitigation:**

- Falls back to a random ephemeral port if the default range is unavailable.
- Selected port is written to the discovery file so the agent client finds it.
- Diagnostic command `/hades:status` reports the current port.

#### 8.5.2 Discovery file out of sync

**What happens:** The discovery file points to a port that's no longer in use (e.g., Unity crashed without writing a new file, then restarted on a different port).

**Risk to Hades:** Agent client fails to connect.

**Mitigation:**

- Discovery file is written atomically on every port change.
- Discovery file includes a PID; agent client validates the PID is alive before trusting the port.
- If validation fails, agent client re-reads the file periodically (every 5 seconds during failed connections).

#### 8.5.3 Agent client doesn't speak MCP correctly

**What happens:** Some clients have buggy MCP implementations or use non-standard extensions.

**Risk to Hades:** Tool calls fail or behave unexpectedly.

**Mitigation:**

- Strict MCP spec compliance on the server side.
- Extensive integration tests with major clients (Claude Code, Cursor, Cline, Continue).
- Charon traces capture exact request/response payloads for diagnosis.

### 8.6 Performance degradation modes

#### 8.6.1 Very large project

**What happens:** Project has 100k+ assets, deep dependency chains, hundreds of scenes.

**Risk to Hades:** Build times exceed user patience. Queries become slow.

**Mitigation:**

- Configuration to enable "selective scanning": user designates which directories are scanned. The rest is treated as opaque.
- Aggregation views in the dashboard surface query latency distributions, helping identify hot paths.
- Optional pre-aggregated rollup tables for common queries (planned for v2).

#### 8.6.2 Pathological assets

**What happens:** A single asset has thousands of components, deeply nested prefabs, or other extreme structure. Scanning it takes minutes.

**Risk to Hades:** That asset's scan blocks others.

**Mitigation:**

- Per-scanner-invocation timeout (default 60 seconds). Asset is marked as failed if it exceeds.
- User can tag assets to skip scanning (configuration in `.arcforge/config.yaml`).
- Parallel scanning of independent assets where Unity API permits.

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

- All file operations go through `PathSandbox.cs` (inherited from UniClaude). Sandbox restricts paths to the project root and `.arcforge/` directories.
- Symlinks are resolved before validation.
- Attempts to escape are logged as security events in Charon.

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
| "Random port keeps changing" | PID validation failing | Check `.arcforge/server.json` PID alive |
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

### 9.7 Plugin marketplace timing

Self-published marketplace ships from day 1. Submission to official Anthropic catalog requires accumulated traction.

**Open question:** When is the right moment to submit? Too early risks rejection on insufficient maturity; too late forfeits months of default-discoverability.

**Resolution path:** Self-publish at v1 release. Submit when we have 3+ months of stable usage and at least one external community contribution.

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
