---
name: hades-show-proposals
description: "Show pending Asphodel memory update proposals"
---

Direct the user to their pending memory update proposals. Two possible UIs, depending on what they have installed for this project:

1. **Standalone app (v2, the default today):** open the Hades app (menu bar icon → **Open Hades**), select **Memory** in the sidebar, then switch to the **Proposals** tab.
2. **Legacy Unity package (v1.2), if still installed in this project:** **Hades > Open Charon Dashboard** from Unity's menu bar opens a browser dashboard with its own Proposals tab, reading this project's `.arcforge/` data directly.

If unsure which the user has, ask, or default to describing (1) — every v2 Claude Code plugin install has the standalone app; the Unity menu item only exists if the legacy package is also still installed, which Claude cannot check from this session.

There is no MCP tool that lists pending proposals — `propose_memory_update` only writes them, and `get_memory_summary`/`recall_memory` explicitly skip `memory/proposals/`. One of the two UIs above is the only way to review them.
