# Hades — Claude Code Plugin

> **In the underworld of your Unity project, nothing is hidden from Hades.**

This is the Claude Code plugin half of [**Hades**](https://github.com/TheArcForge/Hades) — Unity-aware AI infrastructure that gives your agent a queryable knowledge graph of your project. It packages the skills, commands, and MCP connectivity that let Claude Code talk to the standalone Hades app over HTTP.

> **Looking for what Hades is and why?** Start at the [main repository](https://github.com/TheArcForge/Hades). This repo is a generated artifact — see the note at the bottom.

## What this plugin provides

| Component | Count | Description |
|---|---|---|
| **Skills** | 22 | Architecture decisions, workflow guidance, domain expertise (networking, audio, UI, shaders, ECS, testing, and more) |
| **Commands** | 6 | `/hades:status`, `/hades:rebuild-graph`, `/hades:show-traces`, `/hades:validate-memory`, `/hades:show-proposals`, `/hades:export-traces` |
| **MCP Server** | 32 tools | Connects Claude Code directly over HTTP to the standalone Hades app (`http://127.0.0.1:7823/mcp`) — no local process for the plugin to manage |

## Prerequisites

- **macOS** — currently the only tested platform. Windows and Linux are untested; reports welcome.
- **Hades.app** — the standalone app must be installed and running (separate from this plugin; see the [main repo](https://github.com/TheArcForge/Hades)). It serves the MCP connection this plugin declares.
- **Claude Code** — install from [claude.ai/download](https://claude.ai/download)
- **Unity Editor** — only needed for live-Editor features (scene/prefab editing, play mode, console, test running). Graph queries, memory, and traces work without it. The Unity-side integration is optional and installed by Hades.app itself into your project — there is no separate package to install by hand.

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

1. Launch Hades.app and add your Unity project (see the [main repo](https://github.com/TheArcForge/Hades)).
2. `cd` into your Unity project directory in your terminal.
3. Start Claude Code: `claude`
4. Check the connection: `/hades:status`

Skills activate automatically based on context. All 32 MCP tools are available once Hades.app is running; tools that need a live Unity Editor also need the Editor open and the Unity-side integration installed for that project.

## How it connects

```
Claude Code → HTTP → Hades.app (http://127.0.0.1:7823/mcp)
```

This plugin's `.mcp.json` declares a single HTTP connection straight to the standalone Hades app — there is no local process for the plugin itself to start or manage.

All communication is local. No cloud services, no telemetry.

## Troubleshooting

| Symptom | Fix |
|---|---|
| No tools appear | Is Hades.app running? Has this project been added in the app? |
| `/hades:status` not recognized | Plugin not installed. Re-run the install command. |
| Live-Editor tools (scene/prefab edits, console, tests) don't respond | Is the Unity Editor open, with the Unity-side integration installed for this project? |
| Tools stop responding after a Unity recompile | Bring Unity to the foreground briefly to let it re-register — see this plugin's `CLAUDE.md`. |

## About this repository

This repository is **generated** from the [TheArcForge/Hades](https://github.com/TheArcForge/Hades) source repo and published here for the Claude Code marketplace. **Do not submit pull requests here** — open issues and PRs against the [main repository](https://github.com/TheArcForge/Hades) instead. See its `CONTRIBUTING.md` for details.

## License

MIT
