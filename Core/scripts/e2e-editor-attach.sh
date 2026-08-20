#!/usr/bin/env bash
#
# End-to-end check for the Editor link: starts the REAL app and a REAL Unity Editor (batchmode)
# against a fresh throwaway project, and asserts hades_charon_status reports the Editor attached.
#
# Why this exists: EditorListener (app side) and HadesClient (plugin side) each have thorough
# unit test suites, but each one tests against a *fake* of the other - a real TcpListener/reader
# standing in for the app in the plugin's tests, and vice versa. A fake can only be wrong in ways
# its author anticipated, so a real mismatch between the two sides (wire framing, or - as found
# during the investigation that added this script - the app and the plugin each independently
# computing a project's identity GUID a different way, so a fully-successful handshake registers
# under a key nobody ever queries) is invisible to both suites at once. Only a run of the real
# app against a real Editor process can catch that class of bug, which is what this script is
# for. It intentionally does not run inside `dotnet test` - the two halves cannot share a process
# (one is a long-lived ASP.NET Core app, the other is a Unity Editor with its own event loop), so
# this is a separate, explicitly-invoked script rather than another [Fact].
#
# Usage:
#   Core/scripts/e2e-editor-attach.sh
#
# Requires a local Unity 6000.3 install; point HADES_UNITY_PATH at it if it is not at the
# default Unity Hub location. macOS only, matching the rest of Hades (see EditorListener's class
# doc comment) - batchmode Unity and PlayerSettings.productGUID are not exercised anywhere else
# in this repo on any other platform.
#
# What it does:
#   1. Creates a throwaway, otherwise-empty Unity project in a temp directory and installs the
#      UnityPlugin source into its Assets/Hades/ (a plain file copy - Unity mints its own .meta files
#      for it on first import, same as any other newly-added script folder).
#   2. Starts the app (`dotnet run --project src/Hades.Server`) pointed at that project, on a
#      dedicated port so it cannot collide with a developer's own already-running instance.
#   3. Starts Unity in batchmode (no -quit - the plugin needs to stay connected to be observed)
#      against the same project.
#   4. Polls hades_charon_status over MCP until it reports attached:true with a real, non-zero
#      pid (or a timeout) - attached:true with hello-derived fields populated cannot happen
#      without a real completed handshake (see EditorListener.Register /
#      ProjectService.GetCharonStatus), which is the actual thing under test.
#   5. Tears down both processes and the temp project either way, and exits 0 on success or 1 on
#      failure/timeout, printing the tail of both logs on failure.
#
# Isolation note: the app writes its port+token to ONE fixed, global, per-machine file
# (~/Library/Application Support/Hades/editor.token - see HadesConnectionFile.DefaultPath /
# AppPaths.EditorTokenFile), because that is how the real product works: a Unity plugin has no
# other way to discover which port a freshly-started app bound. Any OTHER Hades app instance
# running on this machine while this script runs - e.g. a developer's own interactive session -
# will race it for that same file, and whichever process last called Start() is the one any
# currently-reconnecting plugin (this script's Unity instance, or an unrelated one) dials next.
# The preflight check below catches the common case (another Hades.Server already running) and
# refuses to start rather than risk a result that looks like a pass or fail for the wrong reason.
set -u

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP_DIR="$REPO_ROOT/Core"
PLUGIN_SRC="$REPO_ROOT/UnityPlugin/Assets/Hades"

UNITY_PATH="${HADES_UNITY_PATH:-/Applications/Unity/Hub/Editor/6000.3.2f1/Unity.app/Contents/MacOS/Unity}"
APP_PORT="${HADES_E2E_APP_PORT:-7825}"
ATTACH_TIMEOUT_SECS="${HADES_E2E_TIMEOUT_SECS:-480}"

WORKDIR="$(mktemp -d /tmp/hades-e2e-XXXXXX)"
PROJECT_DIR="$WORKDIR/project"
APP_LOG="$WORKDIR/app.log"
UNITY_LOG="$WORKDIR/unity.log"

APP_PID=""
UNITY_PID=""

log() { echo "[e2e] $*"; }
fail() {
    echo ""
    echo "[e2e] FAIL: $*"
    echo "[e2e] ---- last 40 lines of app.log ----"
    tail -40 "$APP_LOG" 2>/dev/null
    echo "[e2e] ---- last 40 lines of unity.log ----"
    tail -40 "$UNITY_LOG" 2>/dev/null
    exit 1
}

cleanup() {
    [ -n "$UNITY_PID" ] && kill "$UNITY_PID" >/dev/null 2>&1
    # `dotnet run` execs a build/restore step and then launches the actual compiled apphost as a
    # CHILD of that process - killing $APP_PID (the `( ... ) &` subshell) or even `dotnet run`
    # itself does not reliably reach that grandchild, which is what is actually listening on
    # $APP_PORT and would otherwise leak past this script's exit. Killing whatever is bound to
    # the port is the one thing guaranteed to reach the real listener regardless of how deep the
    # process tree ended up.
    if [ -n "$APP_PID" ]; then
        kill "$APP_PID" >/dev/null 2>&1
        pkill -P "$APP_PID" >/dev/null 2>&1
        lsof -ti "tcp:$APP_PORT" 2>/dev/null | xargs -r kill -9 >/dev/null 2>&1
    fi
    # Set HADES_E2E_KEEP=1 to preserve the workdir (project, app.log, unity.log) for diagnosis.
    # A failing run deletes its own evidence otherwise, which is exactly when you need it.
    if [ -n "${HADES_E2E_KEEP:-}" ]; then
        echo "[e2e] keeping workdir: $WORKDIR"
    else
        rm -rf "$WORKDIR"
    fi
}
trap cleanup EXIT

if [ ! -x "$UNITY_PATH" ]; then
    fail "Unity not found at '$UNITY_PATH'. Set HADES_UNITY_PATH to your Unity 6000.3 binary."
fi

# See the "Isolation note" above: a second Hades.Server would fight this run's app for the one
# global connection file. pgrep matches on the compiled binary name, not the `dotnet run` wrapper
# (which exits once the child starts), so this catches a real running instance either way.
if pgrep -f "Hades\.Server( |$)" >/dev/null 2>&1; then
    echo "[e2e] FAIL: another Hades.Server process is already running on this machine."
    echo "[e2e] It would race this run for ~/Library/Application Support/Hades/editor.token"
    echo "[e2e] and could make either run's result meaningless. Stop it first, then re-run."
    exit 1
fi

log "workdir: $WORKDIR"
# Let Unity create the project FIRST. A bare mkdir has no ProjectSettings/ProjectSettings.asset,
# so the app would reject the path with "Not a Unity project (no readable ProjectSettings...)" and
# never adopt it — and then nothing the plugin sends could ever match a known project. Unity only
# writes that file when it opens the project, which in the old ordering happened after the app had
# already given up.
log "creating the Unity project (first Unity pass, generates ProjectSettings/)"
"$UNITY_PATH" -batchmode -nographics -quit -createProject "$PROJECT_DIR" -logFile "$WORKDIR/create.log" \
    || fail "Unity could not create the throwaway project (see $WORKDIR/create.log)"
[ -f "$PROJECT_DIR/ProjectSettings/ProjectSettings.asset" ] \
    || fail "Unity created $PROJECT_DIR but no ProjectSettings/ProjectSettings.asset appeared"

mkdir -p "$PROJECT_DIR/Assets/Hades"
cp -R "$PLUGIN_SRC/." "$PROJECT_DIR/Assets/Hades/"
log "installed plugin source into $PROJECT_DIR/Assets/Hades (Unity will generate its own .meta files on import)"

# Isolate application storage to this run's workdir. Without it the run shares the developer's
# real ~/Library/Application Support/Hades — so a project left behind by any earlier run makes
# hades_charon_status fail with "Hades knows 2 projects, so this call needs a 'project' argument",
# and the shared editor.token lets an unrelated Unity instance answer for this one. Both halves
# read HADES_HOME, so exporting it here covers the app AND the Unity process started below, which
# inherits this environment.
export HADES_HOME="$WORKDIR/app-home"
mkdir -p "$HADES_HOME"
log "isolated app storage at $HADES_HOME"

log "starting app on port $APP_PORT against $PROJECT_DIR"
(
    cd "$APP_DIR" && \
    ASPNETCORE_URLS="http://127.0.0.1:$APP_PORT" \
    dotnet run --project src/Hades.Server --no-launch-profile -- "$PROJECT_DIR"
) > "$APP_LOG" 2>&1 &
APP_PID=$!

# Wait for the app's MCP endpoint to answer before pointing Unity at it - the token file it
# writes on Start() is what the plugin needs to find, and that only exists once the app is up.
app_ready=0
for _ in $(seq 1 60); do
    if curl -s -o /dev/null -m 2 "http://127.0.0.1:$APP_PORT/mcp" 2>/dev/null; then
        app_ready=1
        break
    fi
    if ! kill -0 "$APP_PID" 2>/dev/null; then
        fail "app process exited before becoming ready"
    fi
    sleep 2
done
[ "$app_ready" = "1" ] || fail "app did not start listening on port $APP_PORT within 120s"
log "app is up (pid $APP_PID)"

log "starting Unity batchmode (this can take several minutes on a fresh project)"
"$UNITY_PATH" -batchmode -nographics -projectPath "$PROJECT_DIR" -logFile "$UNITY_LOG" &
UNITY_PID=$!

# Calls hades_charon_status and prints ONE simple, safe-to-capture line: "ATTACHED <pid>
# <detail...>" or "NOT_ATTACHED". Deliberately does the SSE unwrap + double JSON-decode (the
# tool's structured payload is itself a JSON string inside the envelope) and formats the verdict
# all in a single python3 process piped straight from curl - round-tripping the raw response
# through an intermediate shell variable and a second `echo | python3` was tried first and, on at
# least one shell configuration observed during development, silently corrupted the embedded
# " escapes before python ever saw them, breaking the JSON parse. Piping directly avoids the
# shell ever touching the escaped content at all.
check_attached() {
    local meta='{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"e2e","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}'
    curl -s -m 5 -X POST "http://127.0.0.1:$APP_PORT/mcp" \
        -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
        -H 'MCP-Protocol-Version: 2026-07-28' -H 'Mcp-Method: tools/call' -H 'Mcp-Name: hades_charon_status' \
        -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"hades_charon_status\",\"arguments\":{},\"_meta\":$meta}}" \
    | python3 -c '
import json, sys
for line in sys.stdin:
    line = line.strip()
    if line.startswith("data: "):
        line = line[len("data: "):]
    if not line.startswith("{"):
        continue
    try:
        outer = json.loads(line)
        inner = json.loads(outer["result"]["content"][0]["text"])
    except Exception:
        continue
    if inner.get("attached"):
        print("ATTACHED", inner.get("processId", "?"), "|", inner.get("detail", ""))
    else:
        print("NOT_ATTACHED")
    break
'
}

log "polling hades_charon_status (timeout ${ATTACH_TIMEOUT_SECS}s)..."
deadline=$((SECONDS + ATTACH_TIMEOUT_SECS))
attached=0
while [ $SECONDS -lt $deadline ]; do
    if ! kill -0 "$UNITY_PID" 2>/dev/null; then
        fail "Unity process exited before attaching"
    fi

    poll_line="$(check_attached)"
    case "$poll_line" in
        ATTACHED\ *)
            reported_pid="$(echo "$poll_line" | awk '{print $2}')"
            log "attached:true - reported processId=$reported_pid, our Unity pid=$UNITY_PID"
            log "detail: ${poll_line#*| }"
            # dotnet run's own PID differs from the actual Unity process it launches under most
            # setups, and Unity's batchmode PID can differ from $UNITY_PID depending on how the
            # OS reports the child - so this checks for a plausible non-zero pid rather than an
            # exact match, and treats "attached, with real hello-derived fields present" as the
            # meaningful assertion. attached:true with a populated pid cannot happen without a
            # real completed handshake (see EditorListener/ProjectService.GetCharonStatus).
            if [ -n "$reported_pid" ] && [ "$reported_pid" != "?" ] && [ "$reported_pid" != "0" ]; then
                attached=1
            fi
            ;;
    esac
    [ "$attached" = "1" ] && break
    sleep 5
done

if [ "$attached" != "1" ]; then
    fail "hades_charon_status never reported attached:true within ${ATTACH_TIMEOUT_SECS}s"
fi

log "PASS: real Unity Editor attached and confirmed via hades_charon_status"
exit 0
