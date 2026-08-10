---
name: hades-show-traces
description: "Point the user to Hades' trace explorer"
---

Direct the user to the trace explorer. Two possible UIs, depending on what they have installed for this project:

1. **Standalone app (v2, the default today):** open the Hades app (menu bar icon → **Open Hades**) and select **Traces** in the sidebar. Filter by project, tool, outcome, and duration; drill into a call's span detail.
2. **Legacy Unity package (v1.2), if still installed in this project:** **Hades > Open Charon Dashboard** from Unity's menu bar opens a browser-based trace dashboard reading this project's `.arcforge/traces.db` directly.

If unsure which the user has, ask, or default to describing (1) — every v2 Claude Code plugin install has the standalone app; the Unity menu item only exists if the legacy package is also still installed, which Claude cannot check from this session.

Neither path is reachable through an MCP tool call — `hades_charon_status` reports Unity Editor attachment (is a live Editor connected, is it busy), not trace or dashboard state. There is no MCP tool that returns trace records; one of the two UIs above is the only way to see them.
