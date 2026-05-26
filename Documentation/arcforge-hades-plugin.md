# ArcForge Hades — Plugin Document

## 0. About this document

This document is the authoritative reference for Hades as a Claude Code plugin. It defines what the plugin contains, how it's structured, how the MCP connectivity works, how users install it, and how it ships to the Anthropic marketplace.

Other documents in this suite reference this one rather than maintaining their own plugin sections:

- **Vision** (§7.5) — distribution strategy overview, defers plugin detail here
- **Architecture** (§1.5, §5.6) — MCP backbone and skills distribution, defers plugin detail here
- **Roadmap** (§9.5) — marketplace submission timing, defers plugin requirements here

---

## 1. What the plugin contains

Hades is delivered as a single repository that is simultaneously a Unity Package (UPM) and a Claude Code plugin. The plugin aspect comprises:

### 1.1 Skills (22)

Skills are markdown files in `Skills~/` that activate based on context matching. They provide Unity-specific decision frameworks, code patterns, and architectural guidance.

**Architecture decision skills (6):**
- `unity-architect` — top-level routing: components, data, scenes, prefabs, performance
- `component-design` — MonoBehaviour vs ScriptableObject vs plain class decisions
- `data-modeling` — modeling project data: SOs vs JSON vs runtime structures
- `scene-architecture` — bootstrap scenes, additive loading, scene management
- `prefab-architecture` — prefab vs variant decisions, nested prefabs, overrides
- `unity-performance` — profiling-first approach, common bottlenecks

**Workflow skills (3):**
- `scene-authoring` — creating and modifying scenes via the agent
- `prefab-workflow` — creating, editing, instantiating prefabs
- `animation-workflow` — Animator Controller, Animation, AnimationClip relationships

**Domain skills (11):**
- `unity-ui` — UI Toolkit, uGUI, responsive layouts
- `unity-networking` — Netcode, Mirror, Fishnet decision frameworks
- `unity-ai-behavior` — state machines, behavior trees, GOAP, NavMesh
- `unity-audio` — audio manager patterns, mixers, spatial audio
- `unity-input` — new Input System, action maps, multi-device
- `unity-shaders-urp` — URP-specific Shader Graph, render features
- `unity-shaders-hdrp` — HDRP-specific Shader Graph, render features
- `unity-vfx` — VFX Graph, particle systems
- `unity-addressables` — Addressables vs Resources vs AssetBundles
- `unity-ecs` — ECS, Burst, hybrid approaches
- `unity-testing` — EditMode vs PlayMode tests, mocking strategies

**Review skills (1):**
- `unity-reviewer` — severity-tiered code review for Unity projects

**Code quality skill (1):**
- `unity-workflow` — general Unity development workflow guidance

Skills are installed at user scope — available across all Unity projects on the machine. They integrate with Graph (querying project state) and Asphodel (reading team decisions) when the MCP server is connected, and fall back to general guidance when it's not.

### 1.2 Slash commands (6)

Commands in `Commands~/` are user-invocable slash commands:

- `/hades:status` — current Hades state: graph version, trace count, memory file count
- `/hades:rebuild-graph` — triggers a full graph rebuild
- `/hades:show-traces` — opens the Charon dashboard
- `/hades:validate-memory` — runs validation across all memory files
- `/hades:show-proposals` — shows pending memory proposals
- `/hades:export-traces` — exports traces in configured format

### 1.3 MCP server

The MCP server provides 89 tools across five categories: Graph queries, Charon traces, Asphodel memory, core diagnostics, and editor-action tools (scene, component, prefab, material, animation, asset, and project management). The server runs inside the Unity Editor (C#) and is connected to Claude Code through the Hub architecture described in §3.

---

## 2. Plugin structure

### 2.1 Directory layout

```
Hades/                          (repository root = Unity Package root)
├── .claude-plugin/
│   └── plugin.json             # Plugin manifest
├── .mcp.json                   # MCP server declaration (launcher)
├── Skills~/                    # 22 skills (tilde-suffix: invisible to Unity)
│   ├── unity-architect/
│   │   └── SKILL.md
│   ├── component-design/
│   │   └── SKILL.md
│   └── ... (20 more)
├── Commands~/                  # 6 slash commands (tilde-suffix: invisible to Unity)
│   ├── hades-status.md
│   ├── hades-rebuild-graph.md
│   └── ... (4 more)
├── Bridge~/                    # Node.js hub + launcher (tilde-suffix: invisible to Unity)
│   ├── hub/                    # Hub server (zero npm runtime dependencies)
│   │   ├── src/
│   │   │   ├── index.ts
│   │   │   ├── server.ts
│   │   │   ├── registry.ts
│   │   │   ├── heartbeat.ts
│   │   │   ├── mcp-handler.ts
│   │   │   └── types.ts
│   │   ├── dist/               # Compiled JS output
│   │   ├── package.json
│   │   └── tsconfig.json
│   ├── launcher/               # Stdio launcher (zero npm dependencies)
│   │   ├── src/
│   │   │   └── index.ts
│   │   ├── dist/               # Compiled JS output
│   │   └── tsconfig.json
│   ├── tests/
│   ├── tsconfig.json
│   └── vitest.config.ts
├── Scanner~/                   # Node.js .cs file scanner (tilde-suffix: invisible to Unity)
│   ├── src/
│   │   ├── parser.js           # Regex-based C# parser (namespace, type, method)
│   │   ├── meta-resolver.js    # GUID extraction from .meta files
│   │   ├── hasher.js           # MD5 content hashing
│   │   ├── db-writer.js        # better-sqlite3 wrapper (graph.db writes)
│   │   ├── discovery.js        # Recursive .cs file finder
│   │   └── worker.js           # worker_threads for parallel parsing
│   ├── tests/
│   ├── index.js                # CLI entry point
│   └── package.json            # better-sqlite3 + Jest dependencies
├── Editor/                     # Unity C# code
│   ├── MCP/
│   │   ├── MCPServer.cs
│   │   ├── HubClient.cs        # C# HTTP client for hub API
│   │   ├── MCPDispatcher.cs
│   │   ├── Transport/
│   │   │   └── HttpTransport.cs
│   │   └── DomainReload/
│   │       └── AutoReloadStrategy.cs
│   ├── Core/
│   │   ├── MCPClientConfig.cs   # Claude Desktop config + project .mcp.json auto-discovery
│   │   ├── PathSandbox.cs
│   │   └── HadesSettings.cs
│   ├── Graph/
│   ├── Charon/
│   └── Asphodel/
├── package.json                # Unity Package manifest (com.arcforge.hades)
├── CHANGELOG.md
├── LICENSE                     # MIT
└── README.md
```

### 2.2 Tilde-suffix convention

`Skills~/`, `Commands~/`, `Bridge~/`, and `Scanner~/` use Unity's tilde-suffix convention. Unity's asset pipeline ignores directories ending in `~`, but they remain tracked in git and accessible to Claude Code's plugin system. This allows a single repository to serve both ecosystems without asset import conflicts.

### 2.3 Plugin manifest

`.claude-plugin/plugin.json`:

```json
{
  "name": "arcforge-hades",
  "version": "0.6.0",
  "description": "Unity-aware AI infrastructure for Claude Code: project knowledge graph, observability, memory, and 22 skills.",
  "author": { "name": "ArcForge" },
  "license": "MIT",
  "homepage": "https://github.com/TheArcForge/Hades",
  "repository": "https://github.com/TheArcForge/Hades",
  "keywords": ["unity", "game-development", "mcp", "knowledge-graph"],
  "skills": "./Skills~/",
  "commands": "./Commands~/"
}
```

MCP servers are declared in the separate `.mcp.json` file rather than inline in `plugin.json`. This avoids a known Claude Code bug where inline `mcpServers` in `plugin.json` can be silently dropped during manifest parsing (GitHub issue #16143).

### 2.4 Plugin `.mcp.json`

`.mcp.json` at repository root:

```json
{
  "mcpServers": {
    "hades": {
      "command": "node",
      "args": ["${CLAUDE_PLUGIN_ROOT}/Bridge~/launcher/dist/index.js"]
    }
  }
}
```

This declares the launcher as a standard stdio MCP server. Claude Code spawns it automatically when the plugin is enabled. The launcher manages the Hub lifecycle (see §3).

### 2.5 Dual-identity packaging

The same repository has two `package.json` files serving different ecosystems:

- **Root `package.json`**: Unity Package manifest (`com.arcforge.hades`). Declares UPM metadata, Unity version compatibility, and assembly definitions.
- **`Bridge~/hub/package.json`**: Node.js package for the Hub server. The hub has zero npm runtime dependencies — it uses only Node.js built-ins.

These coexist without conflict because Unity ignores `Bridge~/` (tilde-suffix) and npm ignores the root `package.json` (different schema).

---

## 3. MCP connectivity architecture

### 3.1 Overview

Three components connect Claude Code to Unity's Hades tools:

```
Claude Code ←(stdio)→ Launcher ←(HTTP)→ Hub ←(HTTP)→ Unity Instance(s)
                                          ↑
                                    long-running
                                    dynamic port
                                    one per machine
```

The full MCP Hub design specification is in `docs/superpowers/specs/2026-05-13-mcp-hub-design.md`. This section summarizes the architecture; the spec is authoritative for implementation detail.

### 3.2 Hub

A long-running Node.js HTTP server. One per machine, shared across all Claude Code sessions and Unity instances. Source lives in `Bridge~/hub/`.

**Responsibilities:**
- Implements the MCP protocol (Streamable HTTP transport) facing Claude Code
- Maintains a registry of connected Unity instances keyed by project path
- Routes tool calls to the correct Unity instance based on the session's working directory
- Validates instance liveness via heartbeat monitoring
- Buffers requests briefly during Unity domain reloads (up to 10 seconds)
- Auto-exits after 60 seconds of no connected launchers and no registered Unity instances

**Port:** Dynamically assigned. The hub writes `{ port, pid, startedAt }` to `~/.arcforge/hades-hub/hub.json` for the launcher to find.

### 3.3 Launcher

A thin stdio process spawned by Claude Code as declared in `.mcp.json`. Source lives in `Bridge~/launcher/`. Zero external dependencies — uses only Node.js built-ins.

**Behavior:**
1. Reads `~/.arcforge/hades-hub/hub.json` for hub port and PID
2. If hub is not running: spawns it as a detached background process, waits up to 5s
3. Registers with hub as a connected launcher session
4. Bridges stdio ↔ HTTP: reads JSON-RPC from stdin, POSTs to hub, writes response to stdout
5. On stdin close: deregisters from hub, exits

**Failure recovery:** If the hub dies mid-session, the launcher detects the failed HTTP call, respawns the hub, and retries once.

### 3.4 Unity-side server

The existing `MCPServer.cs` HTTP server inside Unity. Modified to register with the hub instead of managing discovery files.

**Lifecycle:**
- On start: registers with hub via `POST /api/register`
- Every 30s: heartbeat via `POST /api/heartbeat`
- On domain reload (before): deregisters with `transient: true` (hub buffers requests)
- On domain reload (after): re-registers (hub resumes request forwarding)
- On quit: deregisters with `transient: false`
- If hub is not running: writes breadcrumb to `~/.arcforge/hades-hub/pending/` for next hub start

### 3.5 Project path routing

The hub routes tool calls to the correct Unity instance by matching the Claude Code session's working directory to registered instances:

1. **Exact match** — CWD equals a registered project path
2. **Parent match** — CWD is a parent of a registered path (handles repo-root case)
3. **Child match** — CWD is a child of a registered path (handles package-source case)
4. **Manifest match** — registered instance's `manifest.json` references CWD as a `file:` package
5. **No match** — returns error listing available instances

This routing solves the `.mcp.json` scoping problems (Known Issues #2 and #4 in the Roadmap) by making MCP connectivity directory-independent.

### 3.6 Tool catalog

When no Unity instances are connected, `tools/list` returns an empty list. Tools appear when Unity connects and disappear when it disconnects, via MCP `tools/list_changed` notifications.

### 3.6a Initialize response and agent instructions

The MCP `initialize` response includes an `instructions` field containing agent guidance — a short prose description of the Hades server and how to use its tools. This field is part of the MCP spec and is surfaced to the agent at session start, before any tool calls. It tells the agent which tools exist, what the server does, and any conventions it should follow.

This matters for the plugin because it is the agent's first signal that Hades tools are available and how to use them. When no Unity instance is connected (empty tools list), the instructions still describe what the server is — the agent knows to wait or prompt the user to open Unity.

### 3.7 Runtime state

```
~/.arcforge/
  hades-hub/
    hub.json              # { port, pid, startedAt } — current hub location
    hub-path.json         # { hubEntry } — absolute path to hub entry point (written by Unity)
    launcher.js           # Stable launcher copy for Claude Desktop
    pending/              # Breadcrumb files from Unity when hub was offline
      {hash}.json
```

This directory is a cross-process coordination point between Unity (C#) and the hub (Node.js). It uses Hades's own namespace (`~/.arcforge/`), not Claude Code's plugin data directory. Unity must find the hub independently of Claude Code's plugin system, which makes `~/.arcforge/` the right location.

### 3.8 Lifecycle scenarios

| Scenario | Behavior |
|---|---|
| Claude Code starts before Unity | Launcher starts hub. Tools list is empty. Unity registers later, tools appear via `list_changed`. |
| Unity domain reload | Unity deregisters as transient. Hub buffers requests up to 10s. Unity re-registers after reload. |
| Unity compilation error | Hub heartbeat monitor probes Unity's HTTP endpoint directly before marking stale. If HTTP listener is alive (background thread), instance stays healthy. |
| Laptop sleep/wake | All processes resume. Hub checks heartbeats. Unity instances respond normally. |
| Multiple Unity instances | Each registers with its project path. Hub routes by matching CWD. |
| Hub crash | Launcher detects failed HTTP call, respawns hub. Unity re-registers on next heartbeat. |
| Session ends | Launcher deregisters. Hub auto-exits after 60s if nothing else is connected. |
| Plugin update | Running hub serves from V8 memory. Auto-exits eventually. Next session starts fresh hub from new plugin root. |

### 3.9 What this architecture replaced

The Hub architecture replaces the previous per-project discovery model:

- ~~`~/.arcforge/servers/{name}-{hash}.json`~~ — server registry files (replaced by hub registry)
- ~~`{project}/.mcp.json`~~ — per-project Claude Code config (replaced by plugin `.mcp.json`)
- ~~`~/.arcforge/mcp-bridge.js`~~ — standalone bridge script (replaced by launcher)
- ~~`MCPClientConfig.WriteClaudeCodeConfig()`~~ — replaced by `WriteProjectMcpJson()`, which writes `.mcp.json` to the Unity project root pointing to `~/.arcforge/hades-hub/launcher.js`; Claude Code auto-discovers MCP when launched from the project directory. The old behavior was removed; a targeted replacement was added.
- ~~`MCPClientConfig.OnServerStop()` file deletion~~ — server entry cleanup (replaced by hub deregistration)

The previous model had three known issues documented in the Roadmap (§10): server entry lost during compilation failures, `.mcp.json` not found from wrong directory, and `.mcp.json` scoped to Unity project only. The Hub architecture resolves all three.

---

## 4. Installation

### 4.1 End-to-end install experience

Two steps — one per ecosystem:

**Step 1: Unity Package (per-project)**

Add via Unity Package Manager git URL:
```
https://github.com/TheArcForge/Hades.git
```

This installs the C# Editor Package: Graph scanner, Charon observability, Asphodel memory, MCP server. The setup wizard runs on first import and configures Claude Desktop if installed.

**Step 2: Claude Code Plugin (per-user)**

Two install methods:

- **From GitHub (future/marketplace):**
  ```
  /plugin install hades@TheArcForge/Hades
  ```

- **From local folder:**
  ```
  claude --plugin-dir /path/to/hades-plugin
  ```
  `--plugin-dir` is per-session — it must be passed each time Claude Code starts. Skills and commands are available for that session only.

This installs skills, commands, and the MCP launcher at user scope. Skills are available across all Unity projects. The MCP server declared in `.mcp.json` starts automatically on each Claude Code session.

Step 1 is per-project (each Unity project installs the package). Step 2 is per-user (once installed, skills work everywhere). The setup wizard from Step 1 prompts the user to complete Step 2 if the plugin is not yet installed.

### 4.2 What each step provides

| Capability | Step 1 (UPM) | Step 2 (Plugin) |
|---|---|---|
| Graph queries | Requires | — |
| Charon traces | Requires | — |
| Asphodel memory | Requires | — |
| MCP server (tools) | Requires | — |
| MCP connectivity (project-scoped) | Provides | — |
| MCP connectivity (global, any directory) | — | Requires |
| Skills (22) | — | Requires |
| Slash commands (6) | — | Requires |

Both steps are needed for the full experience. Skills alone (Step 2 only) provide general Unity guidance without project-specific context. The Unity Package alone (Step 1 only) provides project-scoped MCP access only — tools work when Claude Code is launched from the Unity project directory.

**Note on MCP access:** Installing the Unity Package (Step 1) writes `.mcp.json` to the Unity project root, so Claude Code auto-discovers MCP tools when launched from that directory. The plugin (Step 2) adds skills and enables MCP access from **any** directory — not just the project root. If you need MCP tools to work from unrelated directories (e.g., a separate repo or home directory), install the plugin. Claude Desktop is unaffected (it uses `claude_desktop_config.json` written by Unity directly).

**Note on `--plugin-dir` (local install):** When Step 2 is done via `claude --plugin-dir`, skills and commands are only available for that session. They must be re-passed on every Claude Code start and do not appear in `/plugin list`.

**Auto-generated files on MCP server start:** `MCPClientConfig.OnServerStart` now performs two additional distribution steps automatically:

- **CLAUDE.md** — written to the Unity project root on every server start. This file provides Claude Code with project-specific context (Hades version, available tools, and project conventions) without requiring the plugin to be installed.
- **Skills copy** — 22 skills are copied to `~/.claude/skills/hades-*/` on every server start. This makes skills available to Claude Desktop users who cannot use the plugin system. Skills are refreshed automatically on each Unity MCP server start.

These mechanisms allow partial capability even without `/plugin install` or `--plugin-dir`: a Claude Code session launched from the Unity project directory gets CLAUDE.md context, and Claude Desktop users get skills via the copy path.

For a step-by-step guide covering both install paths, see [`Documentation/getting-started.md`](getting-started.md).

### 4.3 Claude Desktop

Claude Desktop does not use the plugin system. For Claude Desktop users, Unity's `MCPClientConfig` writes to `claude_desktop_config.json` on server start:

```json
{
  "mcpServers": {
    "hades": {
      "command": "node",
      "args": ["~/.arcforge/hades-hub/launcher.js"]
    }
  }
}
```

The config points to a **stable launcher copy** at `~/.arcforge/hades-hub/launcher.js`, not the UPM cache path (which changes on package updates). Unity copies the launcher there on every server start.

The stable launcher needs to locate the hub, but the hub is a multi-file Node.js app that can't be deployed as a single copy. Instead, Unity writes a **pointer file** (`hub-path.json`) containing the absolute path to the hub entry point at its original package location:

```json
{ "hubEntry": "/path/to/Hades/Bridge~/hub/dist/index.js" }
```

The launcher's hub-finding priority chain:
1. Relative path `../../hub/dist/index.js` — works when running from the plugin directory (Claude Code)
2. `hub-path.json` — works when running from the stable location (Claude Desktop)

Both the pointer file and the launcher copy are refreshed every time Unity's MCP server starts, so package path changes self-heal automatically.

---

## 5. Anthropic marketplace

### 5.1 Strategy

The Anthropic plugin marketplace (`platform.claude.com/plugins/submit`) is a discoverability channel, not a delivery mechanism. Submission gives visibility in `/plugin search` results. The actual plugin is always installed from the Hades GitHub repository.

Marketplace submission is deferred until after Phase 5, when Hades is production-ready (v1.0). Submitting earlier risks rejection on polish issues and wastes review cycles.

### 5.2 Compliance checklist

| Requirement | Status |
|---|---|
| Plugin manifest at `.claude-plugin/plugin.json` | Pass |
| MCP servers declared in `.mcp.json` | Pass |
| All paths relative to plugin root, using `${CLAUDE_PLUGIN_ROOT}` | Pass |
| No writes to `~/.claude.json` or `~/.claude/settings.json` | Pass |
| No fixed port conflicts between parallel sessions | Pass (dynamic port) |
| No orphan background processes | Pass (hub auto-exits after 60s idle) |
| No `hooks`/`mcpServers`/`permissionMode` in plugin agents | Pass (no agents shipped) |
| Public GitHub repository | Pass |
| MIT license | Pass |
| Working README with setup instructions | Pass |
| Accurate skill count and descriptions | Pass (22 skills, 6 commands) |
| Version in `plugin.json` matches CHANGELOG | Must verify at submission time |

### 5.3 Standalone plugin repository

Anthropic's marketplace may expect a repo containing only plugin files, not a full Unity Package with C# source and DLLs. If required, create a lightweight `arcforge/hades-plugin` repo that mirrors the plugin-relevant subset:

```
arcforge/hades-plugin/
├── .claude-plugin/plugin.json
├── .mcp.json
├── Skills~/
├── Commands~/
├── Bridge~/
├── Scanner~/
├── CLAUDE.md
├── LICENSE
└── README.md
```

`scripts/sync-plugin.sh` in the main repo produces this directory structure. The source templates for `README.md` and `CLAUDE.md` live at `scripts/plugin-README.md` and `scripts/plugin-CLAUDE.md` respectively.

CI syncs from the main repo on release tags. Only create this if marketplace guidelines require it — until then, the main repo serves both roles.

### 5.4 What marketplace listing does not change

- Install flow remains two steps (UPM + `/plugin install` or `--plugin-dir` for local installs)
- MCP server still runs inside Unity Editor
- Skills still benefit from the Unity Package being installed (Graph/Asphodel tools)
- The launcher/hub architecture is invisible to the marketplace — it's an implementation detail of the MCP server

---

## 6. Versioning

### 6.1 Plugin version

The `version` field in `plugin.json` follows semver. It tracks the plugin aspect (skills, commands, MCP server interface):

- **Patch**: skill content fixes, command improvements
- **Minor**: new skills, new commands, MCP tool additions
- **Major**: breaking MCP tool signature changes, removed skills

### 6.2 Unity Package version

The `version` field in the root `package.json` tracks the Unity Package aspect (C# code, Graph schema, Charon, Asphodel). These two versions evolve independently.

### 6.3 Compatibility

Skills may reference MCP tools by name. If a skill references a tool that doesn't exist in the current MCP server version, the tool call fails gracefully — the agent sees the error and adapts. No formal version compatibility check is implemented in v1.

Future consideration: `plugin.json` could declare a `"minMcpVersion"` field. The hub could check this against the Unity instance's reported version on registration and surface a warning if mismatched.

---

## 7. Troubleshooting

### 7.1 No Hades tools available in Claude Code

**Symptoms:** `/hades:status` returns an error. No Hades MCP tools in the session.

**Check which connectivity path applies:**

**If Claude Code was launched from the Unity project directory:**
1. Does `.mcp.json` exist at the Unity project root? If not, reopen Unity — `MCPClientConfig.WriteProjectMcpJson()` writes it on MCP server start.
2. Is Unity running with Hades? Check Unity console for "Hades MCP server started" log.
3. Is the MCP server connected? Run `/mcp` — look for `hades` server status. If "failed", check `~/.arcforge/hades-hub/hub.json` — does it exist? Is the PID alive?

**If Claude Code was launched from a different directory (e.g., another repo, home directory):**
1. Is the plugin installed? Run `/plugin list` — look for `arcforge-hades`. Without the plugin, Claude Code has no way to discover or connect to the Hub from an unrelated directory. Run `/plugin install hades@TheArcForge/Hades` to fix.
2. Once the plugin is installed, follow checks 2–3 above.

**If Unity is running but tools are empty:** the hub may not have matched your working directory to the Unity project. Run a tool to see the error message listing available instances, or launch Claude Code from within the Unity project directory.

### 7.2 Tools disappear after Unity recompile

**Symptoms:** Tools were working, then stopped after editing a C# file.

**Cause:** Domain reload in progress. The hub buffers requests for up to 10 seconds. If compilation succeeds, tools return automatically. If compilation fails (errors in Hades assemblies), the `[InitializeOnLoad]` constructor doesn't fire and the server doesn't re-register.

**Fix:** Fix the compilation error in Unity. The server will restart and re-register on next successful compile.

### 7.3 Wrong Unity project receives tool calls

**Symptoms:** Running two Unity projects. Tool calls go to the wrong one.

**Cause:** The hub matched your Claude Code working directory to a different Unity instance.

**Fix:** Launch Claude Code from within the correct Unity project directory (or a parent of it). The hub matches by directory path hierarchy.

### 7.4 Hub won't start

**Symptoms:** Launcher reports "failed to start hub" or times out.

**Check:**
1. Is Node.js available? Run `node --version` in terminal.
2. Is another process blocking the port? Check `~/.arcforge/hades-hub/hub.json` for a stale PID. If the PID is dead, delete `hub.json` and retry.
3. Can the launcher find the hub? Check `~/.arcforge/hades-hub/hub-path.json` — does it exist and point to a valid file? If not, reopen Unity to trigger `MCPClientConfig` which writes this file.
4. Are the hub files compiled? Check `Bridge~/hub/dist/index.js` exists. If not, run `npm run build` in `Bridge~/`.

---

## 8. Closing

This document consolidates all plugin-related concerns that were previously scattered across the Vision (§7.5), Architecture (§1.5, §5.6), and Roadmap (§9.5) documents. Those sections now cross-reference this document as the authoritative source.

The plugin design serves two goals simultaneously:
1. **For users:** two-step install (or per-session via `--plugin-dir`), automatic MCP connectivity, 22 skills available everywhere
2. **For Anthropic marketplace:** compliant packaging, no fixed ports, no orphan processes, no config file manipulation

The Hub architecture (detailed in the MCP Hub design spec) resolves the three known MCP connectivity issues while supporting the full range of real-world scenarios: Claude Code before Unity, Unity compilation errors, sleep/wake, multiple instances, nested project directories, and package source development.
