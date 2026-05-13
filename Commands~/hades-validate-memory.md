---
name: hades-validate-memory
description: "Run validation across all memory files and report results"
---

Validate all Asphodel memory files:

1. Call `validate_memory` to trigger validation across all Tier 1 memory files.
2. Report results grouped by file:
   - Files with all rules passing (OK)
   - Files with warnings (validation rule failures — memory claims don't match graph state)
   - Files with errors (parse failures, missing files)
3. For each warning, summarize what the validation rule expected vs what the graph showed.

If there are warnings, suggest the user review the flagged entries — they may represent drift between documented decisions and actual project state.
