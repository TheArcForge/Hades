---
name: hades-validate-memory
description: "Validate every Asphodel memory file and report results"
---

Validate memory against the live project graph:

1. Call `validate_memory` to check every authored memory document for backtick-quoted `.cs` paths that no longer exist in the graph.
2. Report results grouped by document: which document references which missing path.
3. If results were truncated, say so and suggest a higher `limit`.

This only catches broken script-path references — not stale prose, outdated decisions, or any other kind of drift. If there are no results, say memory has no dangling `.cs` references, not that everything in it was confirmed accurate.
