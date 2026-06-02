#!/usr/bin/env bash
# wake-unity.sh — recover a stalled Hades MCP connection (macOS only).
#
# Symptom this fixes: an MCP call returns "Server hades unavailable" or
# "No Unity instance found" even though the Unity Editor process is alive.
# Cause: across a domain reload (recompile) or a deep App Nap, Unity's main
# thread is napped, so the post-reload re-registration (EditorApplication
# .delayCall -> MCPServer.Start) never gets the one tick it needs. The
# background heartbeat keeps an already-running server registered, but it
# cannot bootstrap a torn-down one — that needs the main thread to tick.
#
# Bringing Unity to the foreground un-naps the main thread, which lets it
# re-register within a moment. To avoid stealing the user's focus, this
# captures the current frontmost app and restores it afterward.
#
# Usage: wake-unity.sh [dwell_seconds]   (default dwell: 2)

set -u
DWELL="${1:-2}"

PREV=$(osascript -e 'tell application "System Events" to name of first application process whose frontmost is true' 2>/dev/null)
echo "wake-unity: captured frontmost = ${PREV:-<unknown>}"

osascript -e 'tell application "Unity" to activate' 2>/dev/null
echo "wake-unity: activated Unity, dwelling ${DWELL}s to let it re-register"
sleep "$DWELL"

if [ -n "${PREV:-}" ]; then
  osascript -e "tell application \"System Events\" to set frontmost of process \"$PREV\" to true" 2>/dev/null
  echo "wake-unity: restored focus to $PREV"
else
  echo "wake-unity: no previous app captured; leaving focus on Unity"
fi
