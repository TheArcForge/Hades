# Hades Architecture (v2)

This is the first version-controlled description of the system Hades actually ships. The design
record for v2 lives in `docs/superpowers/specs/*.md` — six documents (overview, app-core, unity
plugin, mac shell, distribution, plus a superseded pre-standalone bootstrap design) — but `docs/`
is gitignored, so none of it survives a fresh clone. This file does. It will drift from the specs
over time; where it drifts from the *code*, the code is right and this file is stale — file an
issue against this document, not against the behavior.

Everything below was checked against the source tree in `App~/`, `Shell~/`, and `Plugin~/`, not
copied from the specs. Several numbers were measured directly (a live `tools/list` call against a
running core, the size of an actual Release build on disk) rather than assumed. Where the specs
and the shipped code disagree, that is called out explicitly rather than smoothed over — this
project corrects itself in writing (see the "Corrected 2026-08-0x" notes throughout the specs
themselves), and this document follows that habit.

Current version: `0.1.0` (`Shell~/HadesApp/scripts/build-app.sh`'s `Info.plist`). Pre-1.0, moving
fast. Treat anything here as "true when written," not "true forever."

---

## 1. Overview

Hades indexes Unity projects into a SQLite knowledge graph and serves it to AI coding agents over
MCP, with optional live control of a running Unity Editor. It is three cooperating processes, not
one:

- A **.NET core** (`App~/src/Hades.Server`) that owns all state and does all the deciding.
- A **SwiftUI menu-bar app** (`Shell~/HadesApp`) that spawns, supervises, and displays the core.
- A **Unity Editor plugin** (`Plugin~/Assets/Hades`) that the app installs into a project and that
  executes Editor-API work the core cannot do itself.

This is a rewrite. v1.0–v1.2 shipped as a Unity Package Manager package with a Node.js stdio
launcher and hub process; that architecture is described in
`Documentation/Retired/arcforge-hades-*.md` (retired alongside this document's publication) and is
not what runs today. The one v1.2
idea that survives is memory (`.arcforge/memory/`), which v2 imports on sight — see §7.3.

## 2. The shape: three processes

```
                ┌────────────────────────────────────┐
                │  AI agents                         │
                │  (Claude Code, other MCP clients)  │
                └────────────────────────────────────┘
                                  │
                  Streamable HTTP (MCP), fixed port
                      http://127.0.0.1:7823/mcp
                                  ▼
              ┌───────────────────────────────────────┐
              │  Hades core                           │
              │  App~/src/Hades.Server                │
              │  (.NET 10, headless)                  │
              │                                       │
              │  MCP endpoint (32 tools)              │
              │  control API (bearer token)           │
              │  knowledge graph (SQLite)             │
              │  Roslyn + Unity-YAML indexers         │
              │  memory (Asphodel) · traces (Charon)  │
              │  editor + lease registries            │
              └───────────────────────────────────────┘

                                  │
          supervised (spawn/kill) by, via HadesCoreReaper —
               dialed into over a loopback socket by —
                                  ▼

                      ┌───────────────────────┐
                      │  Hades.app            │
                      │  Shell~/HadesApp      │
                      │  (Swift 6 / SwiftUI)  │
                      └───────────────────────┘
             spawns + supervises the core; reads it back
         through a separate local control API (bearer token)

            ┌───────────────────────────────────────────┐
            │  Unity Editor plugin                      │
            │  Plugin~/Assets/Hades                     │
            │  (installed by the app into the project)  │
            └───────────────────────────────────────────┘
               dials out to the core over loopback TCP
           (token + Hello handshake) — Unity never listens

                                  │
                                  ▼
                 ~/Library/Application Support/Hades/
                       projects/<productGUID>/
                    graph.db · traces.db · memory/
```

### 2.1 The core — `App~/src/Hades.Server`

.NET 10, headless, ASP.NET Core minimal-API host (`App~/src/Hades.Server/Program.cs`). It is the
brain: every other process is a thin client of it. It owns the MCP endpoint, the knowledge graph,
both indexers, memory, traces, the editor registry, the lease registry, and the control API.

The MCP endpoint binds `127.0.0.1:7823` — fixed, not Kestrel's bare default. This is deliberate,
not an oversight: `McpBinding.ResolveBindUrl` (`App~/src/Hades.Server/Mcp/McpBinding.cs`) picks
7823 explicitly, because Kestrel's own default (`:5000`) is already bound by macOS ControlCenter's
AirPlay Receiver out of the box on current Macs — a spawned core with no override would die on
every launch. If 7823 is itself occupied, the core does not silently rebind: `McpBinding.Run`
catches the bind failure and raises `McpPortBindException` with the exact port, why it's fixed, and
the `lsof` command to find what's squatting on it (`McpBinding.DescribePortInUseFailure`). A
plugin's MCP server declaration is static (`http://127.0.0.1:7823/mcp` in the Claude Code plugin's
`.mcp.json` — see §8), so silently moving the port would leave that declaration pointing at
nothing. `ASPNETCORE_URLS`, when a caller has already set one (tests, CI, `launchSettings.json`,
the E2E scripts), still overrides — the fixed default only applies absent an explicit choice.

The **control API** is a second, separate loopback listener (`ControlListener`,
`App~/src/Hades.Server/Control/ControlListener.cs`) for the Swift shell and a future `hades` CLI —
not a second route on the MCP endpoint. Different consumer, different trust boundary, different
lifecycle: it binds an OS-assigned ephemeral port (not 7823) and writes its own bearer token to a
discovery file the shell reads. It is also started later than the MCP endpoint, on purpose —
`app.Lifetime.ApplicationStarted.Register(controlListener.Start)` in `Program.cs` gates it behind
every hosted service (including the real Kestrel bind) succeeding, closing a race where
`/control/ping` could answer during the brief window before a core whose MCP bind was about to fail
had actually failed.

Storage root: `~/Library/Application Support/Hades/` (`AppPaths.DefaultRoot`,
`App~/src/Hades.Core/Storage/AppPaths.cs`, via .NET's `Environment.SpecialFolder.ApplicationData` —
confirmed empirically on this machine rather than assumed, since that API resolves differently on
other platforms). `HADES_HOME` overrides it end to end (`Program.cs`, `Hades.Cli/Program.cs`,
`Shell~/HadesSupervision`'s `CoreSupervisor`), which is what lets tests and multiple local instances
avoid sharing one project store. Per-project state lives under `projects/<productGUID>/` — see §7
for what's authored versus derived there.

### 2.2 The shell — `Shell~/HadesApp`

Swift 6 / SwiftUI, macOS 14+. It does not decide anything; it spawns, supervises, and renders the
core (see the governing rule in §3). It is a menu-bar app (`LSUIElement`, no Dock icon) that also
opens a full window for projects, memory, traces, and settings.

Spawning goes through an intermediate process, not directly: `CoreSupervisor.spawnOnce`
(`Shell~/HadesSupervision/Sources/HadesSupervision/CoreSupervisor.swift`) launches
**`HadesCoreReaper`** (`Shell~/HadesSupervision/Sources/HadesCoreReaper/main.swift`) as its own
child, and the reaper launches the real core — `Hades.Server` in a Release build, or `dotnet run`
in Debug (see §8) — as *its* own child. The reaper exists for one reason: if the app is
force-quit (`SIGKILL`), nothing inside the app gets to run any cleanup code at all, so a spawned
core cannot be cleaned up *by* the app — it has to be cleaned up by something still alive after the
app is gone. Mechanism, read straight from the source's own header comment:

- The reaper's `ppid` at birth is the app's `pid` (`CoreSupervisor` spawns it directly).
- It `posix_spawn`s the core with `POSIX_SPAWN_SETPGROUP` into its *own* process group, so the
  core — and anything the core forks, however deep (`dotnet run` forks at least one further child)
  — inherits that group rather than the app's.
- It polls `getppid()` every 250ms. When the app dies, by any means, the kernel reparents the now-
  orphaned reaper to `launchd`, which changes what `getppid()` returns; the reaper compares against
  the *original* value, so it doesn't need to know or guess the new one.
- On that change, or on `SIGTERM` from `CoreSupervisor.stop()`, it sends `SIGTERM` to the whole
  process group, waits one second, then `SIGKILL`s the group — one `kill(-pgid, …)` call reaches
  the core and everything under it, without the reaper needing to know the core's port or walk a
  process tree.
- It also exits if the core exits **on its own**, so `CoreSupervisor`'s restart logic has exactly
  one trigger ("the process I spawned is gone") regardless of which direction the death came from.

`CoreSupervisor` adopts an already-running core if one answers `/control/ping` at the address in
the discovery file, rather than spawning a duplicate — `Ownership.adopted` cores are never killed
by `stop()`, only cores this instance actually spawned. If spawning is needed and the core dies
unexpectedly, restart uses exponential backoff (1, 2, 4, 8, 16 seconds, capped, default 5 attempts —
`Configuration.defaultBackoff`). A `minimumStableUptime` of 3 seconds resets the attempt budget only
after a spawn has proven itself stable; this closes a measured bug (Plan 13 Task 8) where a core
that answered one ping and then died moments later got a *fresh* 5-attempt budget on every single
death, so `maxRestartAttempts` never actually bound — 49 spawn attempts in 75 seconds, observed
live, before the fix.

Quitting `Hades.app` is safe from Claude Code's side: the MCP Streamable HTTP transport
auto-reconnects with backoff on its own, so relaunching the app resumes a session Claude Code never
needed to restart.

### 2.3 The plugin — `Plugin~/Assets/Hades`

A drop-in, Editor-only folder with zero third-party assemblies — confirmed structurally, not just
by policy: there is no `HttpListener`, `TcpListener`, or bound socket anywhere under `Plugin~/`.
`HadesClient` (`Plugin~/Assets/Hades/Transport/HadesClient.cs`) only ever constructs a `TcpClient`
and calls `.Connect(...)` — outbound only. **"Unity dials out; it never listens" is a structural
property of this codebase, not a promise.**

The app writes this folder; nothing else does. `PluginInstaller`
(`App~/src/Hades.Core/Editors/PluginInstaller.cs`) installs or updates `Assets/Hades/` in a target
project from resources **embedded inside the core's own compiled binary** — not read off disk at
install time. `Hades.Core.csproj` embeds every file under
`Plugin~/Assets/Hades/{Contract,Runtime,Tools}` plus the `.asmdef` as `<EmbeddedResource>` entries,
and `PluginInstaller.Files` is the
explicit, fixed manifest mapping each embedded resource back to its `Assets/Hades/...` destination
path. This is why a notarized `.app` with no visible source checkout can still install a working
plugin into a stranger's project: the bytes travel inside the same DLL as the code that writes them,
so they survive `dotnet publish`, a move to a different directory, or being bundled into
`Contents/Resources/`. Like the rest of this codebase, the installer writes no `.meta` files and mints no
GUIDs — Unity regenerates both on its next refresh.

The `Contract/` sources specifically come from `Hades.Contract/Wire/` (the sources the *app itself*
compiled against), not from the separately-maintained `Plugin~/Assets/Hades/Contract/` copy used
for day-to-day Unity-side development — confirmed byte-identical between the two locations today,
but the embedding is what makes "the app always ships the contract it actually compiled" true by
construction rather than by developer discipline. This is the "shared contract sources, compiled
into each side, never shipped as a DLL" decision: `EditorConnectionInfo.cs`, `Hello.cs`,
`JsonRpc.cs`, and `MiniJson.cs` are the same four files on both sides of the wire.

On connect, the handshake (`EditorListener`, `App~/src/Hades.Core/Editors/EditorListener.cs`) is:
one raw line carrying a token (read from a 0600 file the app writes fresh on every listener start;
mismatch closes the connection before anything is parsed as JSON), one line of JSON `Hello`
(project GUID, path, Unity version, plugin version, process id), then JSON-RPC one message per
line for the rest of the session. `HadesBoot` (`Plugin~/Assets/Hades/Runtime/HadesBoot.cs`) is the
`[InitializeOnLoad]` entry point that builds and sends that `Hello` on every load — including every
post-domain-reload reload, since `[InitializeOnLoad]` reruns its static constructor each time and a
reload wipes all prior managed state anyway. It explicitly refuses to run inside Unity's asset-
import worker processes (`AssetDatabase.IsAssetImportWorkerProcess()`) — without that guard, a
worker's registration silently replaces the real Editor's in the app's registry (measured: three
established connections for one project without the guard), and a worker never drains its own
main-thread queue, so the app would report the *real*, idle Editor as busy forever. The project GUID
in that `Hello` comes from a direct regex read of `ProjectSettings/ProjectSettings.asset` —
deliberately not the live `PlayerSettings.productGUID` API, which was measured to return a
different, transformed string for the same project than what's actually persisted to disk, which
would register the Editor under a key the app's own file-based reader never looks up.

### 2.4 Reaching it: MCP and the control API

AI agents talk to the core only — never to the shell, never directly to the plugin. Streamable HTTP,
`POST http://127.0.0.1:7823/mcp`. Protocol negotiation is SDK-provided, not something Hades
configures itself, and the shipped SDK speaks revisions `2024-11-05` through `2025-11-25` — verified
live against a running core: an `initialize` requesting `2026-07-28` is rejected with exactly that
supported list, and `2025-11-25` negotiates cleanly. (The specs were written targeting `2026-07-28`;
the SDK in the lockfile doesn't offer it yet — a small spec-vs-code drift of the same kind §5 flags.) Both listeners
(MCP and control) bind loopback only and validate `Origin` per the MCP specification's requirement
for local servers (`App~/src/Hades.Server/Mcp/OriginValidation.cs`,
`App~/src/Hades.Server/Control/ControlAuth.cs`).

MCP *roots* — the mechanism a client would normally use to tell a server which project it means —
are deprecated as of this same spec revision (SEP-2577), and the SDK marks the API obsolete
(`RootsRouter.cs`, `HadesTools.cs`). Hades never adopted them for per-call routing; every Editor-
bound tool instead takes an explicit `project` handle parameter (from `hades_status`), omittable
only when exactly one project is known.

## 3. Governing rules

These are enforced, not aspirational — each has a concrete mechanism cited below.

| Rule | What it means | Where |
|---|---|---|
| **Swift renders, .NET decides** | No business logic in the shell. Every piece of state it shows and every action it offers is served by the core's control API. | The shell's `Views/`, `MainWindow/`, and view models read `HadesControl` DTOs (`Shell~/HadesControl/Sources/HadesControl/DTOs.swift`) and nothing else. |
| **…except OS facts only the shell can observe** | A headless .NET process cannot ask macOS about launch-at-login state or thermal pressure — a real API gap, not a design choice for convenience. | `Shell~/HadesApp/Sources/HadesApp/ShellFacts/` — `LaunchAtLoginService.swift` (`SMAppService.mainApp`), `ResourceGuardReader.swift` / `ThermalStateDisplay.swift` (`ProcessInfo.thermalState`, `isLowPowerModeEnabled`). Named, narrow, and commented as the one exception to the rule above. |
| **Fixed port, never silently rebound** | 7823 or a loud, actionable failure — never a silent fallback to a different port. | `McpBinding.cs` (§2.1). This is a direct lesson from v1.2: dynamic ports are why `hub.json`, PID-liveness probes, and breadcrumb files had to exist at all. |
| **Authored vs. derived data** | `memory/*.md` is authored and irreplaceable; `graph.db`, `traces.db`, `memory-index.db` are derived and freely rebuildable. | `App~/src/Hades.Core/Storage/AppPaths.cs` — each accessor's own doc comment states which class it is. `GraphSchema.Apply` (`App~/src/Hades.Core/Graph/GraphSchema.cs`) treats a schema bump as drop-and-recreate, exactly because nothing authored lives there. |
| **Degrade, don't refuse, on Editor operations** | A version-mismatched plugin still connects and serves what it can, with a warning — never a hard refusal. | `PluginVersionSkew` / `PluginVersionComparison.Classify` (`App~/src/Hades.Core/Editors/PluginVersionSkew.cs`): `Same` / `Minor` / `Major` / `Unknown` buckets, all degrading; only the warning's wording changes. |
| **Unity dials out; it never listens** | Deletes an entire failure class: a domain reload becomes a dropped socket and a reconnect, not an unreachable listener. | Structural — see §2.3. |
| **No hanging state** | Every lock is a lease with a TTL; every lease has independent release paths; every spawned process has a death path. | The reload gate (§6) and `HadesCoreReaper` (§2.2) are the two clearest instances. |

## 4. The knowledge graph

### 4.1 Storage and schema

One SQLite database per project, `graph.db`, schema version 4
(`App~/src/Hades.Core/Graph/GraphSchema.cs`): a `nodes` table, an `edges` table, and a `file_state`
table (per-file mtime + size, for incremental reindexing). Because the graph is entirely derived
data, migration policy is "if the version differs, drop and recreate" — there is no incremental
schema migration machinery, on purpose; nothing authored is ever at risk from it.

### 4.2 The two indexers

Two indexers, doing genuinely different jobs, feeding the same tables:

**`ScriptIndexer`** (`App~/src/Hades.Core/Indexing/ScriptIndexer.cs`) walks `.cs` files through
`RoslynScriptScanner` (`App~/src/Hades.Core/Scanning/RoslynScriptScanner.cs`). This is
**syntax-only** Roslyn — `CSharpSyntaxTree.ParseText`, no `CSharpCompilation`, no semantic model, no
assembly references — which keeps it fast and independent of whether the project currently compiles.
It walks the parsed tree for `BaseTypeDeclarationSyntax` nodes (class/struct/interface/enum/record)
and writes **nodes only**; there is no call anywhere in this file to write an edge.

Preprocessor state matters here, and is reconstructed rather than ignored, by `ProjectDefines`
(`App~/src/Hades.Core/Projects/ProjectDefines.cs`), in four layers, cheapest and most certain first:

1. `UNITY_EDITOR` — unconditional; Hades only ever runs as an editor-time indexer.
2. The Unity version ladder (`UNITY_6000`, `UNITY_6000_3`, …, `UNITY_6000_0_OR_NEWER` through the
   current minor), derived from `ProjectSettings/ProjectVersion.txt`.
3. `scriptingDefineSymbols` from `ProjectSettings.asset`, `Standalone` build-target group only —
   the group Hades itself always runs alongside, regardless of what platform the project ships to.
4. Every `versionDefines` entry in every asmdef, resolved against what's actually installed
   (`Packages/packages-lock.json`, preferred over `manifest.json` because transitive dependencies —
   e.g. `com.unity.burst`, pulled in only by `com.unity.entities` — appear solely in the lock file).

This exists because parsing with no defines at all makes Roslyn evaluate every `#if` as false and
silently drop the guarded code — a real, previously-shipped defect that dropped 64 declarations
behind a single `#if UNITY_EDITOR` in a real project. The honest limit, stated in `ProjectDefines`'s
own doc comment and worth repeating here: Hades does not track which files belong to which asmdef,
so the whole resolved define set is unioned **project-wide** rather than applied per-assembly — a
symbol true for one assembly is treated as true everywhere. `ProjectSummary.AppliedDefines` reports
the resolved set explicitly so this approximation is visible to a caller instead of silent.

**`AssetIndexer`** (`App~/src/Hades.Core/Indexing/AssetIndexer.cs`) walks Unity's YAML formats
through a hand-rolled reader (`App~/src/Hades.Core/Unity/UnityYamlReader.cs` and neighbors) and
**does** write edges — `{fileID, guid, type}` reference triples, keyed on `to_guid`, covering
scene/prefab hierarchy, `m_Script` → MonoBehaviour resolution, and prefab-variant/nested-prefab
modification chains.

Exactly **six** extensions are indexed: **`.cs .unity .prefab .asset .mat .controller`** — the
sweep's own list, `ProjectSweeper.IndexableExtensions`
(`App~/src/Hades.Core/Observation/ProjectSweeper.cs`). `AssetIndexer.cs`'s own array is the five
YAML formats (everything but `.cs`, which `ScriptIndexer` covers). Nothing else is walked for
content (`.meta` files are read only for their GUID).

### 4.3 What is not indexed

This is a design principle in this codebase, not a disclaimer bolted on afterward — the specs
themselves are full of "measured, and it turned out false" corrections, and this section follows
that same habit. Stated plainly:

- **No node types for textures, models, audio clips, or fonts.** The graph has no representation
  for them at all — they are invisible, not merely unqueryable.
- **No Addressables support.** There is no code anywhere in `App~/src` that reads an Addressables
  group or entry.
- **No C#-to-C# code references.** `ScriptIndexer` writes zero edges (above); `find_references_to`
  walks only the `edges` table `AssetIndexer` populates, so it answers "what Unity *assets*
  reference this by GUID" — never "what code calls this method" or "what types reference this
  type." The gap runs deeper than that sentence implies: `RoslynScriptScanner` never descends into
  method bodies at all, only type declarations, so there is no method-level call graph to query even
  in principle today.
- **No method-level declarations as graph nodes.** A class is a node; its methods are not.
- **The string-lookup blind spot.** `GameObject.Find`, `CompareTag`, `SetTrigger`, `Resources.Load`
  — anything Unity resolves by string at runtime — is invisible to a graph built entirely from GUIDs
  and syntactic type declarations. Renaming a GameObject that a `GameObject.Find("Player")` call
  depends on produces no signal in this graph whatsoever.

## 5. The tool surface

Exactly **32** MCP tools — confirmed two independent ways: a `grep` over every
`[McpServerTool(Name = "…")]` attribute in `App~/src/Hades.Server/Mcp/*.cs`, and a live
`tools/list` call against the core actually running on this machine (port 7823), which returned 32
tool names identical to the source-tree list.

| Group | Tools |
|---|---|
| Status & registry | `hades_ping`, `hades_status`, `hades_charon_status`, `hades_rebuild_graph` |
| Graph & disk reads | `search_by_name`, `find_references_to`, `find_unset_references`, `trace_dependencies`, `graph_query`, `get_project_summary`, `get_scene_summary`, `get_recently_changed`, `inspect_asset` |
| Memory (Asphodel) | `get_memory_summary`, `recall_memory`, `propose_memory_update`, `validate_memory` |
| Settings | `project_settings` |
| Editor — batch authoring (mutating) | `scene_apply`, `prefab_apply`, `material_apply`, `animation_apply`, `asset_manage`, `scene_manage`, `project_settings_apply` |
| Editor — live state & project-level | `inspector_inspect`, `project_recompile_scripts`, `project_run_tests`, `project_get_console_log`, `project_get_test_results`, `script_editing_session`, `hades_regression` |

Sixteen `[McpServerToolType]` classes register these 32 tools (`Program.cs`'s `.WithTools<T>()`
chain).

**The consolidation.** These 32 replace what was, briefly, ~90 one-call-per-API-operation tools. A
live `tools/list` measured on 2026-08-03 recorded 90 tools at 123,511 bytes of serialized
definitions — roughly 33,381 tokens, paid on every session before the first user message, and
already larger than Claude Code's own ~25,000-token cap on a single tool *response*
(`docs/superpowers/plans/2026-08-03-tool-consolidation.md`). Consolidation replaced imperative,
one-API-call tools with declarative *apply* tools: a batch of operations, one wire call, one Undo
group, one atomic-per-item result. `scene_apply` is the clearest example — it folds 13 former tools
(`scene_create_gameobject`, `scene_create_primitive`, `scene_delete_gameobject`,
`scene_reparent_gameobject`, `scene_rename_gameobject`, `scene_setup`, `component_add`,
`component_remove`, `component_set_property`, `component_set_properties`, `reference_set`,
`event_add_listener`, `event_remove_listener`) plus `inspector_select`'s "select" capability into
one tool with an `op` field whose vocabulary is camelCase and validated before any wire call is
made: `create`, `addComponent`, `setProperties`, `setReference`, `removeComponent`, `addListener`,
`removeListener`, `delete`, `reparent`, `rename`, `select` (`SceneApplyTool.ValidOps`,
`App~/src/Hades.Server/Mcp/SceneApplyTool.cs`). An unrecognized `op` rejects the whole call before
any wire round trip; a recognized op with a missing required field is a per-operation failure the
Unity-side plugin reports, with the rest of the batch still applying. Ordering is preserved within
one call, so a later operation can act on a GameObject an earlier operation in the *same* call just
created — and the whole batch is one Unity Undo group, so one Cmd+Z reverts it all. `prefab_apply`,
`material_apply`, `animation_apply`, `asset_manage`, `scene_manage`, and `project_settings_apply`
follow the same declarative-batch shape for their own domains. Two tools remain merged-by-action
rather than merged-by-batch: `script_editing_session` (`action='begin'|'end'`, replacing the former
standalone `BeginScriptEditing`/`EndScriptEditing`) and `hades_regression`
(`action='start'|'stop'|'replay'`, replacing three former tools) — both send the exact same
underlying wire methods their predecessors did.

**A spec-vs-code note, since it's the kind of thing worth flagging rather than smoothing over:** the
2026-08-01 overview and app-core specs both call tool consolidation an explicit non-goal —
`docs/superpowers/specs/2026-08-01-hades-standalone-overview-design.md` §6 lists "Tool-surface
consolidation… not done here," and its decision D8 says to "port the existing ~90 tools as-is."
`docs/backlog/tool-surface-consolidation.md` goes further and calls the 90-tool surface "a known
divergence from Anthropic's published guidance, carried on purpose." Two days later, a plan
(`docs/superpowers/plans/2026-08-03-tool-consolidation.md`) reversed that call and executed the
consolidation in full. The code today — 32 tools, batch `apply` operations, camelCase op vocabularies
— reflects the *plan*, not the specs' original sequencing decision. If you read the specs before
the code, expect this mismatch.

## 6. Editor safety

### 6.1 The reload gate

`ReloadGate` (`Plugin~/Assets/Hades/Runtime/ReloadGate.cs`) is the sole permitted caller of
`EditorApplication.Lock/UnlockReloadAssemblies` in this codebase — every other call site would be a
bug. Held/released state is a single nullable lease, not a counter, which makes lock-nesting
unrepresentable rather than merely discouraged (the previous, pre-rewrite implementation had ten
call sites, two locks against eight unlocks, and could drive Unity's native counter negative).

Four release paths, each independently sufficient:

1. **Explicit release.** `script_editing_session` with `action='end'`
   (`App~/src/Hades.Server/Mcp/EditorProjectTools.cs`) sends `project.end_script_editing`, which
   calls `ReloadGate.Release`. Idempotent — calling `'end'` with nothing held, or after a lease has
   already expired, calls `Unlock()` zero times and still reports success.
2. **Socket disconnect.** `ReloadGate.ReleaseOnDisconnect` is wired as `HadesClient`'s
   `onDisconnected` callback (`HadesBoot.cs`) — the background I/O thread notices a dropped
   connection even while the main thread is busy, and enqueues a release ahead of all other queued
   work.
3. **Plugin-side TTL watchdog.** A background `Timer` (200ms poll,
   `ReloadGate.DefaultTtlPollInterval`) checks lease expiry off the main thread — Unity throws calling the lock
   API off-thread, measured directly — and defers the actual `Unlock()` onto `MainThreadPump` for
   the next tick. Default TTL is 30 seconds (`ReloadGate.DefaultTtl`); renewing (`'begin'` again
   before `'end'`) extends it. A lease held past 10 seconds (`HeldWarningThreshold`) gets exactly one
   Unity-console warning per continuous hold — the one place this plugin is deliberately loud, because
   reconnects are routine and silent, but a stuck reload lock is not routine and must not be silent.
4. **Boot reconciliation.** Every Editor start and every post-domain-reload reconstruction of
   `ReloadGate` checks a `SessionState` flag (which survives a reload but not an Editor restart) and
   force-releases, unconditionally, if it was left set — closing any leak from an instance torn down
   mid-hold.

App-side belief about what's held is tracked separately, in `LeaseRegistry`
(`App~/src/Hades.Core/Editors/LeaseRegistry.cs`) — and only ever *believed*, never assumed: every
entry originates from the plugin's own answer to `lease.acquire`/`lease.renew`, never from what the
app itself requested (the plugin might not honor a requested TTL verbatim). Entries self-expire on
read — `Get`/`All` evict anything past its own recorded expiry with no network round trip — and are
reconciled against the plugin's live answer on every reconnect (`ReconcileAsync`).
`script_editing_session` is the only MCP tool in the whole server that calls
`LeaseRegistry.RecordHeld`/`Clear`.

The recompile path a released lease triggers, in order: release the lease, if held →
`AssetDatabase.Refresh()` → `TriggerRecompile()` (`CompilationPipeline.RequestScriptCompilation()`) —
`Plugin~/Assets/Hades/Tools/ProjectCommands.cs`. Refresh runs before recompile specifically so a
brand-new `.cs` file with no `.meta` yet actually gets imported first.

### 6.2 Three states, never collapsed to one

Before sending any command to an attached Editor, `ProjectService.GetCharonStatus` decides which of
three states applies (`App~/src/Hades.Core/Editors/EditorProxy.cs`): **`no_editor`** (nothing
attached), **`editor_busy`** (attached, but the main thread hasn't answered a probe — reported with
what Unity's doing and for how long), or the call simply executes. This distinction exists because
the plugin's I/O loop runs on a background thread independent of Unity's main thread — a busy main
thread still answers keepalives, so "busy" and "gone" are never confused.

### 6.3 The ack gap

One window neither the lease system nor the three-state check can close: the plugin executes a
mutation, writes its response, and the socket dies before the app reads it — a domain reload's
exact shape, and mutations frequently trigger one. `EditorProxy.SendCommandAsync` resolves this by
**verification, not bookkeeping** — there is no idempotency ledger, no request-id replay table,
anywhere in this class. An optional `AckGapVerifier` delegate can re-check project state (re-index
the affected asset and look) to decide whether the interrupted call actually applied. As read for
this document, that extension point exists but no MCP tool currently supplies one, so every
interrupted mutation gets the same honest answer, verbatim from source: *"interrupted, state
unverified, re-query before retrying."*

## 7. Memory, traces, and migration

### 7.1 Memory (Asphodel)

`memory/*.md` under a project's app-storage directory is authored and never auto-deleted — plain
markdown with YAML frontmatter, specifically so it stays hand-editable and a future repo-sync is a
directory copy rather than an export pipeline (`AppPaths.MemoryDir`'s own doc comment). Search is
served by a **derived**, rebuildable FTS5 index with BM25 ranking
(`App~/src/Hades.Core/Memory/MemoryIndex.cs`) — replacing what was previously naive substring
matching with no scoring.

There is exactly one write path for a model, and it does not write to the authored files directly:
`propose_memory_update` (`App~/src/Hades.Server/Mcp/MemoryTools.cs`) writes only to the proposal
queue. `get_memory_summary`, `recall_memory`, and `validate_memory` are the other three memory
tools, and all three are reads. A human accepts a proposal into Tier-1 memory; nothing automated
ever does.

Convention inference — v1.2's `ConventionInferrer` and `PatternInferenceEngine`, which used to
populate `inferred/*.md` automatically from graph structure and trace behavior — has **no producer
in v2 at all**. This is a deliberate retirement (app-core spec §2.1), not an oversight: the
trace-behavior half never produced value even before the rewrite (starved of outcome-capture data
it depended on, per `docs/superpowers/specs/2026-06-17-inferred-conventions-design.md` §1), and
re-hosting the graph-grounded detectors on the new .NET graph wasn't judged worth it for a feature
nothing else in the rewrite depends on. Existing `inferred/*.md` documents from a v1.2 project are
still imported and still readable through the proposal queue (§7.3) — nothing authored is lost —
but nothing new is ever generated. Reconsider if users notice and ask for it back.

### 7.2 Traces (Charon)

Every MCP tool call is traced, success or failure — `Program.cs`'s `CallToolFilters` wraps the SDK's
own dispatch to every registered tool, not just a fallback path. Retention runs on its own timer,
off the request path entirely: an hourly sweep (`new Timer(_ => …, null, TimeSpan.FromMinutes(10),
TimeSpan.FromHours(1))`) prunes anything older than 7 days (`TraceRetentionDays = 7`,
`App~/src/Hades.Server/Program.cs`). A failed sweep — a locked file, a full disk — is logged and
swallowed, never brings the server down. The justification is in the code itself: unbounded growth
isn't hypothetical, one real project's `traces.db` reached 1,631 spans under modest use.

### 7.3 Migrating from v1.2

Three classes, each restricted to exactly what it's allowed to touch:

- **`V12Detector`** (`App~/src/Hades.Core/Migration/V12Detector.cs`) — read-only, always. It never
  opens `.arcforge/traces.db` or `.arcforge/graph.db` — existence alone (`File.Exists`) is the whole
  signal, because either can be a live SQLite file under a v1.2 install still running alongside
  migration. It classifies a project's `CLAUDE.md` into `Absent` / `Marked` (a well-formed
  `<!-- HADES:START -->`/`<!-- HADES:END -->` pair) / `Unmarked` — and deliberately does **not**
  try to tell "Hades wrote this file wholesale, pre-markers" apart from "the user wrote this file
  themselves," because nothing reliable distinguishes them; both get the identical "ask, never
  delete" treatment downstream.
- **`V12Importer`** (`App~/src/Hades.Core/Migration/V12Importer.cs`) — additive only, never touches
  the source project. Memory import delegates to `MemoryStore.ImportFromArcforge` rather than
  reimplementing it, specifically to avoid the two-diverging-implementations bug class this
  project has already shipped once (a hand-duplicated `graph.db` schema and divergent C#/TypeScript
  memory-merge logic, both cited in the specs as reasons for this rewrite). v1.2's `inferred/` and
  `proposals/` subdirectories both flatten into the single v2 `memory/proposals/` directory;
  `proposals/` is processed first and claims any colliding filename, and `inferred/`'s copy of that
  same name is reported **skipped**, never silently overwritten. Traces import copies `traces.db`
  byte-for-byte, including `-wal`/`-shm` sidecars, and never opens it as a database. `graph.db` is
  never a migration target at all — schema and ownership differ; it's rebuilt instead.
- **`V12Cleanup`** (`App~/src/Hades.Core/Migration/V12Cleanup.cs`) — the only one of the three
  allowed to delete or rewrite anything, and only ever in the source project or the user's home
  directory, never in app storage. **Five independent methods** — `CleanClaudeMd`, `CleanManifest`,
  `CleanMcpConfig`, `CleanClaudeDesktopConfig`, `CleanHadesHub` — each takes its own `proceed`
  boolean with no default and returns its own result. There is deliberately no `CleanupAll`: calling
  one never performs another, and refusing one never blocks the rest. JSON edits are byte-level
  splices located with `Utf8JsonReader`, never parse-and-reserialize, so removing one entry never
  reformats a file the user didn't ask to have reformatted.

## 8. Distribution

Three install units, each with one job: **`Hades.app`** (DMG, core + shell), the **Claude Code
plugin** (`hades@arcforge` — 22 skills under `skills/`, 6 `/hades:*` commands under `commands/`, and
the static MCP server declaration), and **`Assets/Hades/`** (written by the app itself, per project,
optional — §2.3). What is *not* an install unit anymore: the UPM package, `Packages/manifest.json`,
`.mcp.json`, a `CLAUDE.md` block, `claude_desktop_config.json`, and Node.js.

A Release build (`Shell~/HadesApp/scripts/build-app.sh Release`) publishes `Hades.Server`
self-contained for `osx-arm64` (`dotnet publish -r osx-arm64 --self-contained true`) into
`Contents/Resources/HadesServer/`. Measured directly against an actual local Release build on this machine:
376 files, 134 MB for `HadesServer/` alone, 137 MB for the whole `.app` bundle. It is deliberately
**untrimmed** — `PublishTrimmed` is never passed — because the core loads Roslyn
(`Microsoft.CodeAnalysis.CSharp`), SQLite (`Microsoft.Data.Sqlite`/SQLitePCLRaw, P/Invoke plus
ADO.NET provider-factory reflection), and `System.Text.Json`, all reflection-adjacent, under an
ASP.NET Core minimal-API surface that isn't fully trim-safe either; trimming risks a build that
compiles and launches fine while silently breaking one specific tool or SQLite path.

Signing today is **ad-hoc only**: `codesign --force --deep --sign -`, then verified with `codesign
--verify --deep --strict` as a hard build-failing check, not an informational one — 15 nested
Mach-O files inside `HadesServer/` (the apphost plus 14 native runtime dylibs, out of 376 total
files) each need their own valid signature for the outer bundle's signature to hold at all.
**Debug** builds carry no embedded core at all: `AppDelegate`
(`Shell~/HadesApp/Sources/HadesApp/AppDelegate.swift`) falls back to `dotnet run --project
<repo>/App~/src/Hades.Server --no-launch-profile` against a live source checkout, and logs exactly
that fact and why — this path needs the .NET SDK and this exact source tree present, so it's never
what a distributed `Hades.app` does.

The DMG is **unsigned today, by explicit and refusable choice** — `build-dmg.sh` will not produce a
DMG at all without either `--allow-unsigned` or a real `--sign`/`--notarize-profile` pair; there is
no silent default either way. The unsigned path labels itself as such in the volume name, the
filename, and a README written into the DMG itself. A real Developer-ID-signed, notarized path is
already implemented (hardened runtime, `notarytool submit --wait`, `stapler staple`, a `spctl`
assessment) but unexercised — no Developer ID Application certificate exists yet on the machine this
was built on. The Homebrew cask (`Casks/hades.rb`) will be the lower-friction path: Homebrew doesn't
mark downloads quarantined, so Gatekeeper's "unidentified developer" prompt never fires for a cask
install the way it does for the same DMG downloaded through a browser (measured, not assumed). But
the cask is **not installable today** — its `url` points at a GitHub release asset that has never
been published, so `brew install --cask hades` fails for anyone but the machine that built it;
internal testing hands out the DMG directly instead. Notarization itself is deferred to that later,
signed release — not blocking today's distribution.
