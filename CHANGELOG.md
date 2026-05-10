# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/).

## [0.1.0] - 2026-05-10

### Added

- MCP server running inside Unity Editor with HTTP transport
- Main thread bridge (ConcurrentQueue + EditorApplication.update)
- Attribute-based tool discovery ([MCPTool], [MCPToolParam])
- Domain reload resilience (AutoReloadStrategy with assembly locking)
- Path sandbox for secure file operations
- Discovery file mechanism (.arcforge/server.json)
- EditorPrefs-backed settings (HadesSettings)
- `hades_ping` diagnostic tool
- Node.js stdio-to-HTTP bridge
- Unity Test Runner tests (NUnit)
- Bridge tests (Vitest)
- CI pipeline (GitHub Actions)
- Synthetic fixture project for integration tests
- Claude Code plugin manifest
