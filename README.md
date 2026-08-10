# Hades

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![CI](https://github.com/TheArcForge/Hades/actions/workflows/ci.yml/badge.svg)](https://github.com/TheArcForge/Hades/actions/workflows/ci.yml)
[![MCP compatible](https://img.shields.io/badge/MCP-compatible-blue)](https://modelcontextprotocol.io)

Hades is a standalone macOS menu-bar app. Its .NET core builds a queryable knowledge graph of a Unity project — every scene, prefab, script, and asset, and the dependencies between them — and serves it to AI agents over MCP: 32 tools, 22 skills, 6 commands. Everything runs locally.

## Status

Pre-release, internal testing. Apple Silicon (arm64) only — there is no universal build. The app is unsigned and un-notarized.

## Pieces

| Path | What it is |
|---|---|
| `Shell~` | The macOS menu-bar app. Launches and supervises the core. |
| `App~` | The .NET core — the knowledge graph, the MCP server, project and migration management. |
| `Plugin~` | The Unity-side plugin. The app installs it into a project's `Assets/Hades`. Optional — only needed for live-Editor features (scene/prefab editing, play mode, console, tests). |
| `Plugin-ClaudeCode~` | The Claude Code plugin — skills, commands, and the `.mcp.json` that connects to the app over HTTP. |

## Install

See [Documentation/InternalTesting-Install.md](Documentation/InternalTesting-Install.md).

## Architecture

See [Documentation/Architecture.md](Documentation/Architecture.md).

## Migrating from v1.2

The app detects an existing v1.2 install (Unity package + in-Editor MCP server + Node bridge) and offers to migrate its project memory and clean up the old install.

## License

MIT
