---
name: hades-show-traces
description: "Open the Charon trace dashboard in a browser"
---

Open the Charon dashboard for the user:

1. Call `hades_charon_status` to check if the dashboard is running and get its URL.
2. If the dashboard is running, tell the user the URL to open in their browser.
3. If the dashboard is not running, instruct the user to open it from Unity via the menu: **Hades > Open Charon Dashboard**.

The dashboard shows:
- Trace list with filters (date, status, name pattern)
- Trace detail with span tree visualization
- Span attribute inspection
