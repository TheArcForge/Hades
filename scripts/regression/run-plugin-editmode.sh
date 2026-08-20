#!/usr/bin/env bash
#
# Runs UnityPlugin's own EditMode test suite (UnityPlugin/Tests/Editor/*.cs - PrefabCommandsTests,
# SceneManageCommandsTests, ReloadGateCriticalSuite, etc.) in a real, throwaway Unity Editor
# batchmode process. These tests exercise UnityPlugin/Assets/Hades' own C# directly through Unity's
# Test Framework/NUnit - no Hades.Server app, no MCP wire protocol, no live project involved -
# but nothing in `dotnet test` can run them (UnityPlugin only compiles inside a Unity Editor), so
# without this script every pin inside UnityPlugin/Tests/Editor is a regression test nobody runs.
#
# Usage:
#   scripts/regression/run-plugin-editmode.sh
#
# Unity is located automatically (newest version under the Hub's install location); override with
# $UNITY_BIN if you want a specific one. macOS only, matching the rest of Hades' Unity tooling
# (see Core/scripts/e2e-editor-attach.sh's own note on this).
#
# What it does:
#   1. Creates a throwaway scratch directory (mktemp) and builds a MINIMAL Unity project skeleton
#      directly inside it - Assets/, Assets/Tests/ (see below for why this exact name matters),
#      and a hand-written Packages/manifest.json. No `-createProject` pass; the skeleton is built
#      by hand so the manifest can stay minimal instead of inheriting Unity's full default set.
#   2. Copies UnityPlugin/Assets/Hades (the plugin source under test) into the scratch project's own
#      Assets/Hades, and UnityPlugin/Tests/Editor (the tests themselves - SceneCommandsTests,
#      PrefabApplyCommandsTests, ReloadGateCriticalSuite, ...) into Assets/Tests/Editor, mirroring
#      their real relative layout so Hades.Tests.Editor.asmdef's own reference to the "Hades"
#      assembly still resolves. Plain file copies - Unity mints its own .meta files for both on
#      first import, same as any other newly-added folder (see e2e-editor-attach.sh's identical
#      note for the runtime-only copy it does).
#   3. Runs Unity in batchmode with -runTests -testPlatform EditMode against the scratch project,
#      writing NUnit3 XML results and a log inside the SAME scratch directory.
#   4. Parses results.xml for total/passed/failed, prints a one-line verdict, and exits non-zero
#      on any failure (or if Unity never produced results.xml at all). The scratch directory is
#      deleted on success; left in place (path printed) on failure, for inspection.
#
# Recorded baseline: 384/384 EditMode tests passing. The count may have grown since - a higher
# total with 0 failed is still a pass; only a lower total or any failure is news.
set -u

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PLUGIN_ASSETS="$REPO_ROOT/UnityPlugin/Assets/Hades"
PLUGIN_TESTS="$REPO_ROOT/UnityPlugin/Tests/Editor"

RUN_TIMEOUT_SECS="${HADES_PLUGIN_EDITMODE_TIMEOUT_SECS:-1800}"

log()  { echo "[plugin-editmode] $*"; }

# ------------------------------------------------------------------------------- locate Unity
#
# Newest version directory under the Hub's install location, by version-sort of the directory
# names themselves (e.g. "6000.3.2f1") - not a hardcoded pin, so this keeps working after a Unity
# upgrade. $UNITY_BIN always wins outright when set, checked first.
UNITY_BIN="${UNITY_BIN:-}"
if [ -z "$UNITY_BIN" ]; then
    HUB_EDITORS_DIR="/Applications/Unity/Hub/Editor"
    if [ -d "$HUB_EDITORS_DIR" ]; then
        NEWEST_VERSION="$(ls -1 "$HUB_EDITORS_DIR" 2>/dev/null | sort -V | tail -1)"
        if [ -n "$NEWEST_VERSION" ]; then
            UNITY_BIN="$HUB_EDITORS_DIR/$NEWEST_VERSION/Unity.app/Contents/MacOS/Unity"
        fi
    fi
fi

if [ -z "$UNITY_BIN" ] || [ ! -x "$UNITY_BIN" ]; then
    echo "[plugin-editmode] Unity not found (looked under /Applications/Unity/Hub/Editor/*/Unity.app," >&2
    echo "[plugin-editmode] and \$UNITY_BIN is not set to an executable). Install Unity via Unity Hub," >&2
    echo "[plugin-editmode] or set UNITY_BIN=/path/to/Unity.app/Contents/MacOS/Unity and re-run." >&2
    exit 2
fi
log "Unity: $UNITY_BIN"

# ------------------------------------------------------------------------------- scratch project

SCRATCH="$(mktemp -d /tmp/hades-plugin-editmode-XXXXXX)"

cleanup_on_success() {
    if [ -n "${HADES_PLUGIN_EDITMODE_KEEP:-}" ]; then
        log "HADES_PLUGIN_EDITMODE_KEEP set - keeping scratch project at $SCRATCH"
    else
        rm -rf "$SCRATCH"
    fi
}

fail() {
    echo "" >&2
    echo "[plugin-editmode] FAIL: $*" >&2
    echo "[plugin-editmode] scratch project left at: $SCRATCH" >&2
    echo "[plugin-editmode]   log:     $SCRATCH/unity.log" >&2
    echo "[plugin-editmode]   results: $SCRATCH/results.xml (if it exists)" >&2
    if [ -f "$SCRATCH/unity.log" ]; then
        echo "[plugin-editmode] ---- last 60 lines of unity.log ----" >&2
        tail -60 "$SCRATCH/unity.log" >&2
    fi
    exit 1
}

log "scratch project: $SCRATCH"

mkdir -p "$SCRATCH/Assets"
# MUST exist before Unity ever runs a test: SceneCommandsTests.cs's own SceneTestFixtures.ResetScene
# calls EditorSceneManager.SaveScene(scene, "Assets/Tests/_HadesCommandTestsScratchScene.unity"),
# and Unity's SaveScene does not create missing intermediate directories - every fixture-using test
# (the large majority of the suite) fails at setup without this, measured as 247 failures the one
# time this step was skipped while developing this script.
mkdir -p "$SCRATCH/Assets/Tests"
mkdir -p "$SCRATCH/Packages"

# Minimal, hand-written manifest - deliberately NOT Unity's own `-createProject` default (which
# pulls in a much larger package set this plugin does not need: URP, Timeline, the Input System,
# Visual Scripting, ...). Verified against Unity's OWN bundled default project template
# (ProjectTemplates/com.unity.template.3d-cross-platform-*.tgz inside this exact Unity.app) that
# built-in engine modules (Physics, Animation, UI, ...) need NO separate `com.unity.modules.*`
# manifest entry on this Unity version - they are implicitly available - so "the modules Unity
# needs" beyond the two packages below turned out to be none. com.unity.ugui is required
# explicitly (UnityPlugin code uses UnityEngine.UI); com.unity.test-framework is required to run
# EditMode tests at all. Both versions below are the exact ones bundled with this Unity release's
# own default template, so they resolve from Unity's local package cache with no network access.
#
# **NEVER add a `"com.arcforge.hades": "file:<path-to-this-repo>"` entry here**, however tempting
# a shortcut it looks like for pulling in the plugin code. A `file:` dependency makes Unity Package
# Manager resolve and COMPILE THE REPO ROOT IN PLACE as that package's content - which is this
# repo's own retired v1.2 tree (the root-level Editor/ directory, kept only for migration
# detection, never meant to compile again) AND, far worse, makes Unity start writing .meta files
# throughout the REAL checkout, not just this scratch copy - silently violating the one hard rule
# every Hades tool in this repo shares (never generate .meta files outside a throwaway scratch
# project). The explicit file-copy step below (UnityPlugin/Assets/Hades -> Assets/Hades) is the only
# sanctioned way to get the plugin source into this scratch project.
cat > "$SCRATCH/Packages/manifest.json" << 'EOF'
{
  "dependencies": {
    "com.unity.test-framework": "1.4.2",
    "com.unity.ugui": "2.0.0"
  }
}
EOF

if [ ! -d "$PLUGIN_ASSETS" ]; then
    fail "Missing $PLUGIN_ASSETS - is UnityPlugin present alongside Core in this checkout?"
fi
if [ ! -d "$PLUGIN_TESTS" ]; then
    fail "Missing $PLUGIN_TESTS - is UnityPlugin/Tests/Editor present in this checkout?"
fi

mkdir -p "$SCRATCH/Assets/Hades"
cp -R "$PLUGIN_ASSETS/." "$SCRATCH/Assets/Hades/"
log "copied UnityPlugin/Assets/Hades -> $SCRATCH/Assets/Hades"

# Mirrors UnityPlugin/Tests/Editor's own relative position (a sibling of Assets/Hades in the real
# repo) so Hades.Tests.Editor.asmdef's reference to the "Hades" assembly (Hades.asmdef's own
# `name`) still resolves unchanged - only the common ancestor (UnityPlugin/... vs this scratch
# project's Assets/...) differs, never the relative shape between the two asmdefs.
mkdir -p "$SCRATCH/Assets/Tests/Editor"
cp -R "$PLUGIN_TESTS/." "$SCRATCH/Assets/Tests/Editor/"
log "copied UnityPlugin/Tests/Editor -> $SCRATCH/Assets/Tests/Editor"

# ------------------------------------------------------------------------------- run EditMode tests

log "starting Unity batchmode EditMode run (timeout ${RUN_TIMEOUT_SECS}s; first import can take minutes)"
"$UNITY_BIN" -batchmode -projectPath "$SCRATCH" -runTests -testPlatform EditMode \
    -testResults "$SCRATCH/results.xml" -logFile "$SCRATCH/unity.log" -nographics &
UNITY_PID=$!

deadline=$((SECONDS + RUN_TIMEOUT_SECS))
while kill -0 "$UNITY_PID" 2>/dev/null; do
    if [ "$SECONDS" -ge "$deadline" ]; then
        kill -9 "$UNITY_PID" >/dev/null 2>&1
        fail "Unity did not finish within ${RUN_TIMEOUT_SECS}s and was killed. A stuck modal dialog" \
             "(see Documentation/Installing.md's F17 note) or a package resolution/" \
             "compile failure are the usual causes - check unity.log above. Retry with a larger " \
             "HADES_PLUGIN_EDITMODE_TIMEOUT_SECS if this machine is just slow."
    fi
    sleep 5
done
wait "$UNITY_PID" 2>/dev/null
unity_exit=$?
log "Unity process exited (code $unity_exit) - verdict comes from results.xml, not this code"

# ------------------------------------------------------------------------------- verdict

if [ ! -f "$SCRATCH/results.xml" ]; then
    fail "Unity exited without writing results.xml - the run likely never reached the test phase (compile error, package resolution failure, or a crash). See unity.log above."
fi

parsed_totals="$(python3 - "$SCRATCH/results.xml" << 'PYEOF'
import sys
import xml.etree.ElementTree as ET

try:
    root = ET.parse(sys.argv[1]).getroot()
except ET.ParseError:
    print("ERR ERR ERR")
    sys.exit(0)

# NUnit3 (Unity's EditMode/PlayMode -testResults format): totals live as attributes on the root
# <test-run> element, not by counting <test-case> nodes ourselves (which would also have to
# reimplement NUnit's own pass/fail/skip/inconclusive classification).
print(root.get("total", "0"), root.get("passed", "0"), root.get("failed", "0"))
PYEOF
)"
read -r total passed failed <<< "$parsed_totals"

if [ "$total" = "ERR" ]; then
    fail "results.xml exists but is not valid XML - see $SCRATCH/results.xml directly."
fi

log "RESULT: total=$total passed=$passed failed=$failed"

if [ "$failed" != "0" ] || [ "$total" = "0" ]; then
    fail "$failed of $total EditMode test(s) failed (or none ran at all). Full results: $SCRATCH/results.xml"
fi

log "PASS: $passed/$total EditMode tests passed"
cleanup_on_success
exit 0
