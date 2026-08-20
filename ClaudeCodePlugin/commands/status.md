---
name: hades-status
description: "Show current Hades server status, known projects, and memory summary"
---

Show the user a concise status dashboard by calling these tools:

1. Call `hades_status` to get:
   - Server version
   - Every project Hades knows, with its handle, name, and path
   - The default project (only set when exactly one project is known)

2. Call `get_memory_summary` (for the relevant project) to get:
   - Whether the project has any recorded memory
   - Its authored documents (conventions, decisions, etc.) with size and last-reviewed date

Present the results as a formatted summary. `get_memory_summary` does not include pending proposals — use `/hades:show-proposals` for those. If Hades knows more than one project and `hades_status` reports no default, ask which project before calling `get_memory_summary`.
