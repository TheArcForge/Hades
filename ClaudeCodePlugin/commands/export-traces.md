---
name: hades-export-traces
description: "Export Charon trace data for analysis or sharing"
---

Help the user export trace data. The database location depends on what they have installed for this project:

1. **Standalone app (v2, the default today):** the SQLite file is in Hades' app storage, not the project folder. The path is platform-specific:
   - **macOS:** `~/Library/Application Support/Hades/projects/<productGUID>/traces.db`
   - **Windows:** `%LOCALAPPDATA%\Hades\projects\<productGUID>\traces.db`

   The `project` handle `hades_status` returns for this project is that productGUID.
2. **Legacy Unity package (v1.2), if still installed in this project:** the SQLite file is at `<project>/.arcforge/traces.db`, inside the project itself.

Either way:
- The database file can be opened with any SQLite client (DB Browser for SQLite, DBeaver, sqlite3 CLI).
- For sharing: the user can copy the trace database file. It contains all trace and span data.

Tables of interest:
- `traces` — top-level trace records (one per MCP tool call)
- `spans` — individual spans within traces (nested operations)

Privacy note: traces may contain file paths and query parameters from the project. Review before sharing externally.
