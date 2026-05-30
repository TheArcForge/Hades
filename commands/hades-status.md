---
name: hades-status
description: "Show current Hades graph state, server status, and memory summary"
---

Show the user a concise status dashboard by calling these tools:

1. Call `hades_status` to get:
   - Graph state (node count, edge count, last build time, schema version)
   - Server status (running, port, uptime)
   - Whether a rebuild is in progress

2. Call `get_memory_summary` to get:
   - Memory file count and names
   - Validation status (ok/warning/error counts)
   - Pending proposal count

Present the results as a formatted summary. If any component is unavailable (server not running, database not initialized), say so clearly rather than erroring.
