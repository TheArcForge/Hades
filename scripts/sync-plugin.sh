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

# Plugin manifest and MCP config (.mcp.json from a tracked template, not the
# gitignored machine-specific runtime file at the repo root)
cp -R "$REPO_ROOT/.claude-plugin" "$TARGET/"
cp "$REPO_ROOT/scripts/plugin-mcp.json" "$TARGET/.mcp.json"

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

# Skills and Commands
rsync -a "$REPO_ROOT/skills/" "$TARGET/skills/"
rsync -a "$REPO_ROOT/commands/" "$TARGET/commands/"

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
echo "  Skills:   $(ls -d "$TARGET/skills/"*/ 2>/dev/null | wc -l | tr -d ' ')"
echo "  Commands: $(ls "$TARGET/commands/"*.md 2>/dev/null | wc -l | tr -d ' ')"
echo "  Bridge:   launcher/dist + hub/dist (zero deps)"
echo "  Scanner:  source only (npm install needed for runtime)"
