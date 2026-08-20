#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TARGET="${1:-$REPO_ROOT/../hades-plugin}"
TARGET="$(cd "$(dirname "$TARGET")" 2>/dev/null && pwd)/$(basename "$TARGET")"

PLUGIN_SRC="$REPO_ROOT/ClaudeCodePlugin"

# Validate the plugin source is present. Nothing to build — ClaudeCodePlugin is a static
# manifest, skills, commands, and an HTTP .mcp.json; there's no compile step before syncing it.
if [[ ! -f "$PLUGIN_SRC/.claude-plugin/plugin.json" ]] || \
   [[ ! -f "$PLUGIN_SRC/.mcp.json" ]]; then
  echo "ERROR: $PLUGIN_SRC is missing its manifest or .mcp.json. Nothing to sync." >&2
  exit 1
fi

echo "Syncing plugin to: $TARGET"

# Clean target, preserving .git/ and plugin-repo-only files
PRESERVE_DIR=""
if [[ -d "$TARGET" ]]; then
  PRESERVE_DIR=$(mktemp -d)
  # Back up plugin-repo-only files before cleaning
  for f in .github .gitignore .gitattributes CONTRIBUTING.md; do
    if [[ -e "$TARGET/$f" ]]; then
      cp -R "$TARGET/$f" "$PRESERVE_DIR/"
    fi
  done
  if [[ -f "$TARGET/.claude-plugin/marketplace.json" ]]; then
    mkdir -p "$PRESERVE_DIR/.claude-plugin"
    cp "$TARGET/.claude-plugin/marketplace.json" "$PRESERVE_DIR/.claude-plugin/"
  fi
  find "$TARGET" -mindepth 1 -maxdepth 1 ! -name '.git' -exec rm -rf {} +
fi
mkdir -p "$TARGET"

# Plugin manifest, .mcp.json, skills, and commands — all sourced from ClaudeCodePlugin/, the
# current Claude Code plugin. It is already a complete, tested, installable plugin in its own
# right (internal testers point `claude --plugin-dir` straight at it — see
# Documentation/Installing.md), so syncing it is just copying that same tree into
# the separate hades-plugin repo checkout for marketplace distribution.
#
# This used to assemble from four different places instead: a manifest since removed (see
# (see its README), Bridge~ dist, Scanner~ source, and scripts/plugin-mcp.json - which packaged
# the retired v1.2 plugin shape (a stdio launcher spawning a local Node process, matched by a
# generated .mcp.json with an "mcpServers" wrapper). ClaudeCodePlugin/ replaces all four: it
# has no local process to build or ship, only a static HTTP .mcp.json pointing at the standalone
# Documentation/Retired/root-plugin-manifest.md) - none of which this script reads any more.
# .DS_Store is gitignored here but sync-plugin.sh copies from the WORKING TREE, not from git - a
# local run would otherwise push Finder's droppings into the plugin repo. CI checks out clean, so
# this only ever matters for a human running the sync by hand, which is exactly when it is missed.
rsync -a --exclude='*.meta' --exclude='.DS_Store' "$PLUGIN_SRC/" "$TARGET/"

# Restore plugin-repo-only files
if [[ -n "$PRESERVE_DIR" ]]; then
  for f in .github .gitignore .gitattributes CONTRIBUTING.md; do
    if [[ -e "$PRESERVE_DIR/$f" ]]; then
      cp -R "$PRESERVE_DIR/$f" "$TARGET/"
    fi
  done
  if [[ -f "$PRESERVE_DIR/.claude-plugin/marketplace.json" ]]; then
    cp "$PRESERVE_DIR/.claude-plugin/marketplace.json" "$TARGET/.claude-plugin/"
  fi
  rm -rf "$PRESERVE_DIR"
fi

# Root files (not part of ClaudeCodePlugin/ itself)
cp "$REPO_ROOT/LICENSE" "$TARGET/"
cp "$REPO_ROOT/scripts/plugin-README.md" "$TARGET/README.md"
cp "$REPO_ROOT/scripts/plugin-CLAUDE.md" "$TARGET/CLAUDE.md"

# Summary
echo ""
echo "Plugin synced:"
echo "  Skills:   $(ls -d "$TARGET/skills/"*/ 2>/dev/null | wc -l | tr -d ' ')"
echo "  Commands: $(ls "$TARGET/commands/"*.md 2>/dev/null | wc -l | tr -d ' ')"
echo "  MCP:      HTTP, no local process (ClaudeCodePlugin/.mcp.json)"
