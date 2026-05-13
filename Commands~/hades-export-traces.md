---
name: hades-export-traces
description: "Export Charon trace data for analysis or sharing"
---

Help the user export trace data:

1. The Charon trace database is a SQLite file located alongside the project's `.arcforge/` directory.
2. The database file can be opened with any SQLite client (DB Browser for SQLite, DBeaver, sqlite3 CLI).
3. For sharing: the user can copy the trace database file. It contains all trace and span data.

Tables of interest:
- `traces` — top-level trace records (one per MCP tool call)
- `spans` — individual spans within traces (nested operations)

Privacy note: traces may contain file paths and query parameters from the project. Review before sharing externally.
