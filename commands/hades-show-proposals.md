---
name: hades-show-proposals
description: "Show pending memory update proposals"
---

Show the user their pending memory update proposals:

1. Call `hades_charon_status` to check if the dashboard is running.
2. If running, direct the user to the Proposals view in the dashboard for accept/edit/reject UI.
3. If not running, instruct the user to open the dashboard from Unity: **Hades > Open Charon Dashboard**, then navigate to the Proposals tab.

Proposals are memory updates suggested by the agent during conversations. They sit in `.arcforge/memory/proposals/` until the user reviews them in the dashboard.
