# Release Pipeline

How Hades is tested, validated, and shipped. This document covers CI, versioning, pre-release checklist, Anthropic plugin submission, and step-by-step instructions for AI agents preparing a release.

---

## 1. CI overview

Three GitHub Actions workflows across two repositories.

### Main repo (`TheArcForge/Hades`)

**`ci.yml`** — runs on every push and PR to `main`.

Two parallel jobs:
- **Bridge tests** — installs dependencies and runs Vitest in `Bridge~/`, then builds TypeScript to verify compilation.
- **Scanner tests** — installs dependencies and runs the split Jest suite in `Scanner~/` (unit tests first, integration tests second, separated due to tree-sitter native addon conflicts).

Purpose: catch regressions before merging.

**`release.yml`** — runs when a version tag (`v*`) is pushed.

Sequential steps:
1. Builds Bridge from TypeScript to JavaScript.
2. Runs `scripts/sync-plugin.sh` to produce the plugin repo content.
3. Validates the output: 22 skills, 6 commands, valid JSON, no leaked Unity files, and presence of `Bridge~/launcher/dist/index.js` and `Bridge~/hub/dist/index.js`.
4. Clones `TheArcForge/hades-plugin` using the `PLUGIN_REPO_TOKEN` secret.
5. Copies the validated plugin content into the clone.
6. Updates `marketplace.json` version to match the tag.
7. Commits, tags, and pushes to the plugin repo.

Purpose: one tag push on the main repo automatically ships the plugin repo.

**Secret required:** `PLUGIN_REPO_TOKEN` — a GitHub Personal Access Token with `repo` scope, stored in the main repo's GitHub Settings > Secrets > Actions. This gives the CI runner write access to the plugin repo. The token is never stored in files — GitHub injects it at runtime and masks it in logs.

### Plugin repo (`TheArcForge/hades-plugin`)

**`validate.yml`** — runs on every push and PR to `main`.

Checks:
- All required files present (plugin.json, .mcp.json, Bridge dist, Scanner source, README, LICENSE, CLAUDE.md).
- `plugin.json` and `.mcp.json` are valid JSON.
- `.mcp.json` uses `${CLAUDE_PLUGIN_ROOT}` paths.
- Exactly 22 skills and 6 commands.
- No `.meta`, `.cs`, or `node_modules` leaked in.

Purpose: guard rail against sync bugs or accidental direct edits.

---

## 2. Version locations

Three files track the product version and must stay in lockstep on every release:

| File | Repo | What it controls |
|---|---|---|
| `package.json` → `version` | Main | Unity Package Manager version |
| `.claude-plugin/plugin.json` → `version` | Main (synced to plugin) | Claude Code plugin version |
| `.claude-plugin/marketplace.json` → `plugins[0].version` | Plugin only | Self-hosted marketplace entry |

The release workflow automatically updates `marketplace.json` in the plugin repo to match the tag. The other two must be bumped manually in the main repo before tagging.

**Internal component versions (bump only if the component changed this release):**

| File | Repo | Current | Purpose |
|---|---|---|---|
| `Bridge~/package.json` → `version` | Main | 1.1.0 | Bridge workspace version |
| `Bridge~/hub/package.json` → `version` | Main | 1.1.0 | Hub component version |
| `Scanner~/package.json` → `version` | Main | 1.1.0 | Scanner component version |

These track internal API changes independently of the product version. **Policy:** leave them untouched on a release that didn't change them, but if a component changed substantially this release, bump it to the product version so the two don't silently diverge. (v1.1.0 bumped all three — the hub/launcher reliability overhaul and the scanner's meta-constants.)

**Version constants in source (check each release):**

| Location | Reports as | Notes |
|---|---|---|
| `Bridge~/launcher/src/index.ts` → `SERVER_VERSION` | launcher MCP `serverInfo.version` | a plain constant — **bump manually**, then rebuild Bridge so `launcher/dist` reflects it |
| `Editor/MCP/MCPDispatcher.cs` → `serverInfo.version` | Unity MCP server `initialize` | **resolves dynamically** from the package manifest (`PackageInfo.FindForAssembly`) as of v1.1.0 — no manual bump needed |

**Build invariant — the launcher must stay a single bundled file.** `Bridge~/launcher` builds to one self-contained `dist/index.js` via esbuild (`--bundle`). `EnsureStableLauncher` (`Editor/Core/MCPClientConfig.cs`) copies only that one file to the stable location inside the resolved hub directory (`<projectRoot>/.arcforge/hades-hub/launcher.js` by default, or `~/.arcforge/hades-hub/launcher.js` in global hub scope), so any relative sibling import would crash the launcher at startup with `ERR_MODULE_NOT_FOUND` (this was the v1.1.0 install regression — the launcher had been split into `tsc`-emitted modules without updating the copy routine). Guarded by `Bridge~/tests/launcher/bundle.test.ts`; do not switch the launcher back to a multi-file `tsc` emit without also updating the copy routine.

---

## 3. Pre-release checklist

Complete all items before creating a release tag.

### Code

- [ ] All changes committed and pushed to `main`
- [ ] CI passes on `main` (both Bridge and Scanner tests)
- [ ] No known regressions from prior phases

### Versions

- [ ] `package.json` version bumped
- [ ] `.claude-plugin/plugin.json` version bumped
- [ ] Both match the intended release tag (e.g., `1.0.0` for tag `v1.0.0`)

### Documentation

- [ ] `CHANGELOG.md` updated with all changes since last release
- [ ] `README.md` (main repo) reflects current install flow and features
- [ ] `scripts/plugin-README.md` reflects current plugin install flow
- [ ] `Documentation/arcforge-hades-roadmap.md` — phase status updated, version history updated
- [ ] `Documentation/arcforge-hades-architecture.md` — any architectural changes reflected
- [ ] `Documentation/arcforge-hades-plugin.md` — plugin structure, install flow, skill/command counts current
- [ ] `docs/plugin-publish-pipeline.md` — expected counts still accurate

### Plugin sync

- [ ] Bridge is built (`cd Bridge~ && npm run build`)
- [ ] Sync script runs cleanly: `bash scripts/sync-plugin.sh /path/to/hades-plugin`
- [ ] Plugin repo validation passes (all checks from plugin-publish-pipeline.md §2)

### Final

- [ ] Tag created: `git tag vX.Y.Z`
- [ ] Tag pushed: `git push origin vX.Y.Z`
- [ ] Release workflow completes successfully
- [ ] Plugin repo has matching tag and content
- [ ] GitHub Releases created on both repos

---

## 4. Anthropic plugin submission

### Documentation links

| Topic | URL |
|---|---|
| Create plugins | https://code.claude.com/docs/en/plugins |
| Plugin reference (full schema) | https://code.claude.com/docs/en/plugins-reference |
| Discover and install plugins | https://code.claude.com/docs/en/discover-plugins |
| Plugin marketplaces | https://code.claude.com/docs/en/plugin-marketplaces |
| Plugin manifest JSON schema | https://json.schemastore.org/claude-code-plugin-manifest.json |
| Official plugin registry | https://github.com/anthropics/claude-plugins-official |
| Community plugin registry | https://github.com/anthropics/claude-plugins-community |

### Submission process

1. Verify plugin passes `claude plugin validate /path/to/plugin --strict`
2. Submit via `claude.ai/settings/plugins/submit` or `platform.claude.com/plugins/submit`
3. Automated safety screening runs first
4. Manual review follows if automation passes
5. Plugin pinned to specific commit SHA on approval
6. Public catalog sync may take 1+ day after approval

### Compliance rules

- `.claude-plugin/plugin.json` must exist and pass validation
- `README.md` must document purpose, installation, and usage
- `LICENSE` file required (Hades uses MIT)
- No hardcoded secrets — use `userConfig` with `sensitive: true` for tokens
- No orphan background processes — all processes must exit cleanly (Hub auto-exits after 60s idle)
- No fixed port conflicts — use dynamic port allocation (Hub uses OS-assigned ports)
- No writes to `~/.claude.json` or `~/.claude/settings.json`
- No `hooks`, `mcpServers`, or `permissionMode` in plugin agents

### Current install paths

Before marketplace acceptance, users install via self-hosted marketplace:
```
/plugin marketplace add TheArcForge/hades-plugin
/plugin install hades
```

After marketplace acceptance:
```
/plugin install hades
```

Both paths coexist — the self-hosted marketplace remains as an alternative.

---

## 5. AI agent release preparation guide

When asked to prepare a release, follow these steps exactly. Report each result to the user. Do not proceed to tagging without explicit user approval.

### Step 1: Verify tests pass

```bash
cd Bridge~ && npm ci && npm test && npm run build && cd ..
cd Scanner~ && npm ci && npm test && cd ..
```

Report: which tests passed, which failed, any warnings.

### Step 2: Check version consistency

```bash
PACKAGE_V=$(python3 -c "import json; print(json.load(open('package.json'))['version'])")
PLUGIN_V=$(python3 -c "import json; print(json.load(open('.claude-plugin/plugin.json'))['version'])")
echo "package.json: $PACKAGE_V"
echo "plugin.json:  $PLUGIN_V"
```

Report: current versions, whether they match, whether they match the intended release version.

If versions need bumping, update both files and report the change.

### Step 3: Verify plugin sync

```bash
npm run build --prefix Bridge~
bash scripts/sync-plugin.sh /path/to/hades-plugin
```

Then run the full validation suite on the plugin repo:

```bash
cd /path/to/hades-plugin

# Required files
test -f .claude-plugin/plugin.json && echo "PASS: plugin.json" || echo "FAIL: plugin.json"
test -f .mcp.json && echo "PASS: .mcp.json" || echo "FAIL: .mcp.json"
test -f README.md && echo "PASS: README" || echo "FAIL: README"
test -f LICENSE && echo "PASS: LICENSE" || echo "FAIL: LICENSE"
test -f CLAUDE.md && echo "PASS: CLAUDE.md" || echo "FAIL: CLAUDE.md"
test -f Bridge~/launcher/dist/index.js && echo "PASS: launcher dist" || echo "FAIL: launcher dist"
test -f Bridge~/hub/dist/index.js && echo "PASS: hub dist" || echo "FAIL: hub dist"
test -f Scanner~/index.js && echo "PASS: Scanner" || echo "FAIL: Scanner"

# Counts
SKILLS=$(find skills -name "SKILL.md" | wc -l | tr -d ' ')
CMDS=$(ls commands/*.md 2>/dev/null | wc -l | tr -d ' ')
echo "Skills: $SKILLS (expected 22)"
echo "Commands: $CMDS (expected 6)"

# Excluded files
METAS=$(find . -name "*.meta" -not -path "./.git/*" | wc -l | tr -d ' ')
CS=$(find . -name "*.cs" -not -path "./.git/*" | wc -l | tr -d ' ')
NM=$(find . -name "node_modules" -type d -not -path "./.git/*" | wc -l | tr -d ' ')
echo "Meta files: $METAS (expected 0)"
echo "CS files: $CS (expected 0)"
echo "node_modules: $NM (expected 0)"

# JSON validity
python3 -c "import json; json.load(open('.claude-plugin/plugin.json'))" && echo "PASS: plugin.json valid" || echo "FAIL: plugin.json invalid"
grep -q 'CLAUDE_PLUGIN_ROOT' .mcp.json && echo "PASS: CLAUDE_PLUGIN_ROOT" || echo "FAIL: CLAUDE_PLUGIN_ROOT"

# Version match
PLUGIN_V=$(python3 -c "import json; print(json.load(open('.claude-plugin/plugin.json'))['version'])")
MARKET_V=$(python3 -c "import json; print(json.load(open('.claude-plugin/marketplace.json'))['plugins'][0]['version'])")
echo "Plugin version: $PLUGIN_V"
echo "Marketplace version: $MARKET_V"
[ "$PLUGIN_V" = "$MARKET_V" ] && echo "PASS: versions match" || echo "FAIL: version mismatch"
```

Report: all results in a table. Flag any failures.

### Step 4: Check documentation

Read and verify these files are up to date:

1. `CHANGELOG.md` — has an entry for the new version with all changes
2. `README.md` — install instructions, feature counts, version references are current
3. `scripts/plugin-README.md` — plugin install instructions are current
4. `Documentation/arcforge-hades-roadmap.md` — phase statuses and version history reflect reality
5. `Documentation/arcforge-hades-architecture.md` — no stale references
6. `Documentation/arcforge-hades-plugin.md` — skill/command counts, install flow, compliance checklist

Report: which docs are current, which need updates, what specifically is stale.

### Step 5: Report to user

Present a summary:

```
## Release readiness report for vX.Y.Z

### Tests
- Bridge: PASS/FAIL
- Scanner: PASS/FAIL

### Versions
- package.json: X.Y.Z ✓/✗
- plugin.json: X.Y.Z ✓/✗
- marketplace.json: X.Y.Z ✓/✗

### Plugin validation
- Required files: N/N ✓
- Skills: 22 ✓/✗
- Commands: 6 ✓/✗
- Excluded files: 0 ✓/✗
- JSON valid: ✓/✗

### Documentation
- CHANGELOG: current/needs update
- README: current/needs update
- Plugin README: current/needs update
- Roadmap: current/needs update
- Architecture: current/needs update
- Plugin doc: current/needs update

### Blocking issues
- [list any failures]

### Ready to tag: YES/NO
```

Do NOT create tags or push without explicit user approval.
