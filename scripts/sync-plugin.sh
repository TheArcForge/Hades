#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TARGET="${1:-$REPO_ROOT/../hades-plugin}"
TARGET="$(cd "$(dirname "$TARGET")" 2>/dev/null && pwd)/$(basename "$TARGET")"

# Validate Bridge is built
if [[ ! -f "$REPO_ROOT/Bridge~/launcher/dist/index.js" ]] || \
   [[ ! -f "$REPO_ROOT/Bridge~/hub/dist/index.js" ]]; then
  echo "ERROR: Bridge not built. Run 'npm run build' in Bridge~/ first." >&2
  exit 1
fi

echo "Syncing plugin to: $TARGET"

# Clean target, preserving .git/ if it exists
if [[ -d "$TARGET" ]]; then
  find "$TARGET" -mindepth 1 -maxdepth 1 ! -name '.git' -exec rm -rf {} +
fi
mkdir -p "$TARGET"

# Plugin manifest and MCP config
cp -R "$REPO_ROOT/.claude-plugin" "$TARGET/"
cp "$REPO_ROOT/.mcp.json" "$TARGET/"

# Skills and Commands
rsync -a "$REPO_ROOT/Skills~/" "$TARGET/Skills~/"
rsync -a "$REPO_ROOT/Commands~/" "$TARGET/Commands~/"

# Bridge — compiled output only (zero runtime deps)
mkdir -p "$TARGET/Bridge~/launcher/dist" "$TARGET/Bridge~/hub/dist"
cp "$REPO_ROOT/Bridge~/launcher/dist/"* "$TARGET/Bridge~/launcher/dist/"
cp "$REPO_ROOT/Bridge~/hub/dist/"* "$TARGET/Bridge~/hub/dist/"

# Scanner — source only (no tests, no node_modules)
mkdir -p "$TARGET/Scanner~/src"
cp "$REPO_ROOT/Scanner~/index.js" "$TARGET/Scanner~/"
cp "$REPO_ROOT/Scanner~/package.json" "$TARGET/Scanner~/"
cp "$REPO_ROOT/Scanner~/package-lock.json" "$TARGET/Scanner~/"
cp "$REPO_ROOT/Scanner~/src/"*.js "$TARGET/Scanner~/src/"

# Root files
cp "$REPO_ROOT/LICENSE" "$TARGET/"
cp "$REPO_ROOT/scripts/plugin-README.md" "$TARGET/README.md"
cp "$REPO_ROOT/scripts/plugin-CLAUDE.md" "$TARGET/CLAUDE.md"

# Summary
echo ""
echo "Plugin synced:"
echo "  Skills:   $(ls -d "$TARGET/Skills~/"*/ 2>/dev/null | wc -l | tr -d ' ')"
echo "  Commands: $(ls "$TARGET/Commands~/"*.md 2>/dev/null | wc -l | tr -d ' ')"
echo "  Bridge:   launcher/dist + hub/dist (zero deps)"
echo "  Scanner:  source only (npm install needed for runtime)"
