# Hades

> **In the underworld of your Unity project, nothing is hidden from Hades. And now, nothing is hidden from your AI agent.**

Hades is Unity-aware AI infrastructure for Claude Code. It builds a queryable knowledge graph of your entire Unity project — every scene, prefab, script, asset, and dependency — so your AI agent *knows* your project's structure instead of guessing at it. Out of the box you get 89 MCP tools, 22 skills, and 6 commands. Everything runs locally, and everything is version-controllable.

## Know, don't guess

Most AI tools **search and predict**: they grep for text that looks relevant and let the model infer the rest. The answers are probabilistic — and often wrong in ways you can't see.

Hades lets your agent **know and analyze**. When it asks "what references `PlayerController`," it reads a structural fact from the graph, not a guess from scattered snippets. Dependency analysis traces real edges. Ask the same question twice, get the same answer. One graph query replaces a dozen file reads — and the agent never makes you explain your project twice.

## What Hades gives your AI agent

| Layer | What it does |
|-------|-------------|
| **Graph** | A semantic knowledge graph of your Unity project — scenes, prefabs, scripts, assets, and their dependencies. The agent sees your project's structure, not just its files. |
| **Charon** | Full observability — every tool call, graph query, and memory operation is traced. Inspect via the local dashboard (**Hades > Open Charon Dashboard** in Unity). |
| **Asphodel** | Persistent project memory in version-controlled markdown (`.arcforge/memory/`). Capture decisions, patterns, and conventions once; the agent reads them for context-aware advice every session. |
| **22 Skills** | Architecture decisions, workflow guidance, and domain expertise — networking, audio, UI, shaders, ECS, testing, and more. |
| **89 MCP Tools** | 21 graph/charon/memory tools + 68 editor-action tools (scenes, components, prefabs, materials, animation, assets). |
| **6 Commands** | `/hades:status`, `/hades:rebuild-graph`, `/hades:show-traces`, `/hades:validate-memory`, `/hades:show-proposals`, `/hades:export-traces` |

## See it in action

Open Claude Code from your Unity project directory and ask:

```
Tell me about this project
```
The agent uses the graph to give a project-specific overview — not a generic summary.

```
Where do we use PlayerController?
```
Structural search across scenes, prefabs, and scripts — not just text grep.

```
I want to remove OldNetworkManager. What would break?
```
Dependency analysis that traces references through the full project graph *before* you change anything.

## Prerequisites

- **Unity 6000.0+**
- **Node.js 20+**
- **Claude Code** (or any MCP-compatible agent client)

## Installation

### Step 1: Unity Package

**From git URL:**

In Unity's Package Manager, click **Add package from git URL** and enter:

```
https://github.com/TheArcForge/Hades.git
```

**From local folder (for testing or offline use):**

In Unity's Package Manager, click **Add package from disk...** and select the `package.json` inside your local Hades folder.

> On first open after install, Hades automatically builds the project knowledge graph. This takes 10–45 seconds depending on project size.

### Step 2: Claude Code Plugin

**Option A: Persistent install (recommended)**

```
/plugin marketplace add TheArcForge/hades-plugin
/plugin install hades
```

**Option B: Per-session**

```bash
claude --plugin-dir /path/to/hades-plugin
```

That's it. Open Claude Code from your Unity project directory and the tools are available immediately.

> **First time?** See the full [Getting Started](Documentation/getting-started.md) guide for a step-by-step walkthrough with verification at each step.

## How it works

Claude Code connects over stdio to a lightweight launcher, which routes HTTP requests to the Hades Hub, which in turn forwards tool calls to the correct Unity Editor instance. The Hub runs once per machine and handles multi-project routing automatically. All data stays local — no cloud services, no telemetry, no vendor lock-in. See [`Documentation/arcforge-hades-architecture.md`](Documentation/arcforge-hades-architecture.md) for full architectural details.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| No tools appear in Claude Code | Is Unity running? Check `~/.arcforge/hades-hub/hub.json` for a registered instance. |
| Tools disappear after recompile | Wait ~10 seconds — the Hub buffers tool calls during Unity's domain reload. |
| Wrong project receives tool calls | Launch Claude Code from the correct project directory. |
| Project info seems stale | Run `/hades:rebuild-graph` to regenerate the knowledge graph. |

See [`Documentation/troubleshooting.md`](Documentation/troubleshooting.md) for the full troubleshooting guide.

## Documentation

- [Architecture](Documentation/arcforge-hades-architecture.md) — system design, data flow, component responsibilities
- [Plugin Manifest](Documentation/arcforge-hades-plugin.md) — tool and skill reference
- [Roadmap](Documentation/arcforge-hades-roadmap.md) — development phases and status
- [Vision](Documentation/arcforge-hades-vision.md) — long-term goals and design philosophy

## License

MIT
