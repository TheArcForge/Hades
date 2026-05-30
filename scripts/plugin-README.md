# Hades — Claude Code Plugin

> **In the underworld of your Unity project, nothing is hidden from Hades.**

This is the Claude Code plugin half of [**Hades**](https://github.com/TheArcForge/Hades) — Unity-aware AI infrastructure that gives your agent a queryable knowledge graph of your project. It packages the skills, commands, and MCP connectivity that let Claude Code talk to the Hades server running inside Unity Editor.

> **Looking for what Hades is and why?** Start at the [main repository](https://github.com/TheArcForge/Hades). This repo is a generated artifact — see the note at the bottom.

## What this plugin provides

| Component | Count | Description |
|---|---|---|
| **Skills** | 22 | Architecture decisions, workflow guidance, domain expertise (networking, audio, UI, shaders, ECS, testing, and more) |
| **Commands** | 6 | `/hades:status`, `/hades:rebuild-graph`, `/hades:show-traces`, `/hades:validate-memory`, `/hades:show-proposals`, `/hades:export-traces` |
| **MCP Server** | 89 tools | Connects Claude Code to the Hades MCP server running inside Unity Editor |

## Prerequisites

- **Node.js 20+** — the MCP launcher and hub are Node.js processes
- **Claude Code** — install from [claude.ai/download](https://claude.ai/download)
- **Hades Unity Package** — installed in your Unity project (separate from this plugin; see the [main repo](https://github.com/TheArcForge/Hades))

## Installation

### Option A: Persistent install (recommended)

Register the Hades marketplace and install the plugin:

```
/plugin marketplace add TheArcForge/hades-plugin
/plugin install hades
```

This persists across sessions — you only do it once.

### Option B: Per-session

```bash
claude --plugin-dir /path/to/hades-plugin
```

This loads the plugin for a single session only.

### Verify

Run `claude plugin validate /path/to/hades-plugin` — you should see "Validation passed".

## Usage

1. Open your Unity project (with the Hades Unity Package installed).
2. `cd` into your Unity project directory in your terminal.
3. Start Claude Code: `claude`
4. Check the connection: `/hades:status`

Skills activate automatically based on context. All 89 MCP tools are available when Unity is running.

## How it connects

```
Claude Code → stdio → Launcher → HTTP → Hub → HTTP → Unity Editor
```

The **Launcher** (in `Bridge~/launcher/`) starts automatically with each Claude Code session. It connects to the **Hub** (in `Bridge~/hub/`), a lightweight HTTP server that runs once per machine and routes tool calls to the correct Unity Editor instance. The Hub auto-exits after 60 seconds with no connected sessions.

All communication is local. No cloud services, no telemetry.

## Troubleshooting

| Symptom | Fix |
|---|---|
| No tools appear | Is Unity running? Did you `cd` into the Unity project dir? |
| `/hades:status` not recognized | Plugin not installed. Re-run the install command. |
| Tools disappear after recompile | Wait ~10 seconds — Hub buffers during Unity domain reload. |
| Hub won't start | Run `node --version` to verify Node.js 20+. |

## About this repository

This repository is **generated** from the [TheArcForge/Hades](https://github.com/TheArcForge/Hades) source repo and published here for the Claude Code marketplace. **Do not submit pull requests here** — open issues and PRs against the [main repository](https://github.com/TheArcForge/Hades) instead. See its `CONTRIBUTING.md` for details.

## License

MIT
