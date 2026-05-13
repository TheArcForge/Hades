---
name: hades-rebuild-graph
description: "Trigger a full graph rebuild from scratch"
---

Trigger a full graph rebuild:

1. Call `hades_rebuild_graph` to start the rebuild.
2. Report the result to the user: how many nodes/edges were built, how long it took.
3. If the rebuild fails, report the error clearly.

Note: Full rebuilds can take 10-60 seconds on large projects. The graph remains queryable during rebuild but results may be stale until it completes.
