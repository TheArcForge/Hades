# Hades

Unity-aware AI infrastructure for Claude Code. Provides a semantic knowledge graph, observability, persistent memory, and project-aware skills.

## Status

Phase 0 — Foundation. Infrastructure only; no user-facing features yet.

## Requirements

- Unity 6000.0+
- Node.js 20+
- Claude Code (or any MCP-compatible agent client)

## Installation

### Unity Package (via UPM git URL)

In Unity Package Manager, add package from git URL:

```
https://github.com/TheArcForge/Hades.git
```

### Bridge Setup

```bash
cd Bridge~
npm install
npm run build
```

## Repository Structure

- `Editor/` — Unity Editor C# code (MCP server, tools, infrastructure)
- `Tests/Editor/` — Unity Test Runner tests (NUnit)
- `Bridge~/` — Node.js MCP stdio bridge (tilde-folder, Unity-ignored)
- `Fixtures~/` — Synthetic Unity project for integration tests
- `.claude-plugin/` — Claude Code plugin manifest
- `.github/workflows/` — CI pipeline

## Architecture

Hades runs an MCP server inside the Unity Editor. Agent clients connect via a thin Node.js bridge that translates stdio MCP protocol to HTTP requests against the Unity server. The bridge reads `.arcforge/server.json` to discover the server port dynamically.

## License

MIT
