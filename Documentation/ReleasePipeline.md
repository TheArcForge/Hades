# Release Pipeline

How Hades is tested, validated, and shipped. This document covers CI, versioning, pre-release checklist, Anthropic plugin submission, and step-by-step instructions for AI agents preparing a release, plus (section 6) building and distributing the Hades.app menu-bar shell itself via DMG and Homebrew cask.

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
1. Runs `scripts/sync-plugin.sh` to assemble `Plugin-ClaudeCode~/` — the current Claude Code
   plugin — into the plugin repo content. No build step: the plugin is a static manifest,
   skills, commands, and an HTTP `.mcp.json`, so there is nothing to compile first.
2. Validates the output: 22 skills, 6 commands, a valid `plugin.json`, an `.mcp.json` with a
   plugin-root HTTP `hades` entry at `http://127.0.0.1:7823/mcp` (no `mcpServers` wrapper), no
   leaked `.meta`/`.cs`/`node_modules`, and no `Bridge~`/`Scanner~` (the retired v1.2 shape).
3. Clones `TheArcForge/hades-plugin` using the `PLUGIN_REPO_TOKEN` secret.
4. Copies the validated plugin content into the clone.
5. Updates `marketplace.json` version to match the tag.
6. Commits, tags, and pushes to the plugin repo.

Purpose: one tag push on the main repo automatically ships the current Claude Code plugin
(`Plugin-ClaudeCode~/`) — not the retired v1.2 shape (Bridge dist, Scanner source, a
stdio-launcher `.mcp.json`) it shipped before `sync-plugin.sh` and this workflow were repointed.

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

> **Required follow-up, not done here.** The checks above describe the *retired v1.2* shape —
> Bridge dist, Scanner source, and a `${CLAUDE_PLUGIN_ROOT}`-relative `.mcp.json` (the stdio
> launcher form). Now that this repo's `release.yml` ships `Plugin-ClaudeCode~/` instead, the
> synced content has no `Bridge~`/`Scanner~` directories at all, and `.mcp.json` is a
> plugin-root HTTP entry (`http://127.0.0.1:7823/mcp`) with no `${CLAUDE_PLUGIN_ROOT}`
> substitution anywhere in it. The next real sync will make `validate.yml` fail red, checking
> for files that no longer exist. `validate.yml` lives in the *other* repo
> (`TheArcForge/hades-plugin`), which this repo's tooling cannot see or edit — someone with
> access to that repo needs to update it to match this new shape before (or immediately after)
> the next tag push.

---

## 2. Version locations

> **`release.yml`'s description in Section 1 now reflects the current release flow** — it was
> repointed at `Plugin-ClaudeCode~/`, and its own note there flags the one known gap
> (`hades-plugin`'s `validate.yml` not yet updated to match). **Sections 2–5 still describe the
> v1.2 release flow** — the Unity package, the Node bridge, and the pre-`Plugin-ClaudeCode~/`
> shape of the `TheArcForge/hades-plugin` marketplace submission — and have not been revisited
> yet; treat their Bridge/Scanner/`.claude-plugin`-at-repo-root details as stale until they are.
> Section 6 covers the standalone macOS app that replaces all of it.
>
> **The root `.claude-plugin/plugin.json` and `.mcp.json` referenced below no longer exist at the
> repo root.** They now live under `Legacy~/` — a root manifest made this checkout installable as a
> plugin, which silently served the retired ~90-tool in-Editor surface instead of the app's 32. The
> current Claude Code plugin is `Plugin-ClaudeCode~/`, and its version lives in
> `Plugin-ClaudeCode~/.claude-plugin/plugin.json`.

Three files track the product version and must stay in lockstep on every release:

| File | Repo | What it controls |
|---|---|---|
| `package.json` → `version` | Main | Unity Package Manager version |
| `Legacy~/claude-plugin/plugin.json` → `version` | Main (v1.2, retired) | Former Claude Code plugin version |
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

**Build invariant — the launcher must stay a single bundled file.** `Bridge~/launcher` builds to one self-contained `dist/index.js` via esbuild (`--bundle`). `EnsureStableLauncher` (`Editor/Core/MCPClientConfig.cs`) copies only that one file to the per-machine stable location (`~/.arcforge/hades-hub/launcher.js`), so any relative sibling import would crash the launcher at startup with `ERR_MODULE_NOT_FOUND` (this was the v1.1.0 install regression — the launcher had been split into `tsc`-emitted modules without updating the copy routine). Guarded by `Bridge~/tests/launcher/bundle.test.ts`; do not switch the launcher back to a multi-file `tsc` emit without also updating the copy routine.

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
- [ ] `Documentation/Retired/arcforge-hades-roadmap.md` — phase status updated, version history updated
- [ ] `Documentation/Retired/arcforge-hades-architecture.md` — any architectural changes reflected
- [ ] `Documentation/Retired/arcforge-hades-plugin.md` — plugin structure, install flow, skill/command counts current
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
4. `Documentation/Retired/arcforge-hades-roadmap.md` — phase statuses and version history reflect reality
5. `Documentation/Retired/arcforge-hades-architecture.md` — no stale references
6. `Documentation/Retired/arcforge-hades-plugin.md` — skill/command counts, install flow, compliance checklist

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

---

## 6. Hades.app distribution (Shell~/HadesApp)

A separate pipeline from sections 1-5 above: this covers the macOS menu-bar shell app
(`Shell~/HadesApp`), not the Bridge/Scanner/plugin units. Plan 14 ("Distribution, Phase One")
Tasks 7 and 9.

### 6.1 Current signing status - measured, not assumed

Verified on the machine this was written on:

| Check | Command | Result |
|---|---|---|
| Signing identity | `security find-identity -v -p codesigning` | **0 valid identities** |
| Notarization credentials | (needs the certificate above first; `notarytool` credential setup was never attempted) | none stored |
| Gatekeeper assessment of the ad-hoc-signed app | `spctl -a -vv --type execute Hades.app` | **rejected** |
| Team identifier | `codesign -dv Hades.app` | **TeamIdentifier=not set** |

Notarization is impossible until an Apple Developer Program membership and a Developer ID
Application certificate exist. See section 6.5 for exactly what to do once they do.

### 6.2 The channel matters more than the signature

Gatekeeper only blocks **quarantined** files. `com.apple.quarantine` is applied by apps that opt
in to marking their downloads - browsers, Mail, Messages, AirDrop - and *not* by `curl`, `git
clone`, or Homebrew.

| Channel | Result for today's unsigned app |
|---|---|
| DMG downloaded in a browser | Quarantined -> *"Apple could not verify..."* -> System Settings, see 6.3 |
| **Homebrew cask** | Not quarantined -> **launches with no prompt** |

Measured directly (mount the DMG, copy `Hades.app` to `/Applications` the same way Homebrew Cask's
`Artifact::Moved#move` does for a fresh install - a plain file copy, no quarantine attribute
involved anywhere in that path - then launch via `open`, the same LaunchServices path a user's
double-click takes): `xattr -l` on the installed app shows `com.apple.provenance` but no
`com.apple.quarantine`, and the app launches with no Gatekeeper dialog, despite `spctl` itself
still separately reporting `rejected` (a static policy check that runs regardless of quarantine
state - not the same thing as what actually happens on launch). This is why **the Homebrew cask
is the recommended install path** until a certificate exists, not a workaround.

### 6.3 Installing Hades today: two paths, documented honestly

**Path A - Homebrew cask (recommended).** No Gatekeeper prompt, `brew upgrade` works once a tap is
published (not done yet - see 6.6).

```
brew install --cask hades   # once a tap exists and is added; see 6.7 for how this was verified today
```

The cask's `caveats` block states plainly that Hades is unsigned. `brew uninstall hades` removes
the app only; `brew uninstall --zap hades` (or `brew zap hades`) additionally removes
`~/Library/Application Support/Hades` and `~/Library/Preferences/com.arcforge.hades.shell.plist` -
see `Casks/hades.rb` for the exact list and section 6.6 for why it stops there.

**Path B - DMG (alternative). Not frictionless - do not present it as if it were.** A DMG
downloaded through a browser is quarantined, so opening it hits Gatekeeper. macOS 15 removed the
right-click > Open shortcut, so this is now the only way past it:

1. Try to open Hades.app (double-click, or drag to Applications and open it there). macOS refuses
   with *"Apple could not verify that 'Hades' is free of malware that may harm your Mac or
   compromise your privacy."* There is no "Open Anyway" button on this dialog itself.
2. Open **System Settings > Privacy & Security**.
3. Scroll down to the Security section. A line reading something like *"'Hades' was blocked to
   protect your Mac"* appears, with an **Open Anyway** button next to it.
4. Click **Open Anyway**. Authenticate (password or Touch ID) when prompted.
5. Open Hades.app again. A second dialog appears, this time with a real **Open** button. Click it.

A self-signed certificate does not shorten this: Gatekeeper only trusts Apple-issued Developer ID
certificates, so an unsigned app hits the same flow whether or not it carries an ad-hoc signature.

### 6.4 Building the DMG

`Shell~/HadesApp/scripts/build-dmg.sh` builds `Hades.app` (via the existing `build-app.sh` - run
from any directory; `build-dmg.sh` handles the `cd` to `Shell~/HadesApp` that `build-app.sh`'s own
`xcodebuild -scheme` resolution requires internally), stages it with an `Applications` symlink for
drag-to-install, and produces a DMG under `Shell~/HadesApp/DerivedData/dmg/`.

It never emits an unsigned DMG silently. Exactly one of two flags is required:

```
# Phase 1, today - explicit, loudly labeled unsigned build:
Shell~/HadesApp/scripts/build-dmg.sh Release --allow-unsigned

# Phase 2, once a certificate exists - signs, notarizes, staples, and re-verifies:
Shell~/HadesApp/scripts/build-dmg.sh Release \
  --sign "Developer ID Application: NAME (TEAMID)" \
  --notarize-profile hades-notary
```

Calling it with neither flag fails immediately, before any build work happens, with:

```
build-dmg.sh: no signing identity and no notarization credentials were given, and
--allow-unsigned was not passed either.

This script will not silently produce an unsigned DMG - an unsigned build that reaches a user by
looking like a normal release is exactly what notarization exists to prevent, and reaching a user
is the whole purpose of a DMG. Give it real signing inputs, or say explicitly that you accept an
unsigned build:

  Signed and notarized (needs a Developer ID Application certificate - none exists on this
  machine today; `security find-identity -v -p codesigning` reports 0 valid identities. See
  "Signed release, step by step" in Documentation/ReleasePipeline.md for the one-time setup):
    build-dmg.sh Release --sign "Developer ID Application: NAME (TEAMID)" --notarize-profile PROFILE

  Deliberately unsigned (Phase 1, today - Gatekeeper still flags this DMG if it is ever
  quarantined, e.g. downloaded through a browser; the Homebrew cask is the install path that
  avoids that entirely - see Documentation/ReleasePipeline.md):
    build-dmg.sh Release --allow-unsigned

Check for a certificate with: security find-identity -v -p codesigning
```

Passing only one of `--sign` / `--notarize-profile`, or passing `--allow-unsigned` together with
either, also fails loudly with its own specific, actionable message - see `build-dmg.sh` itself.
Both flags have env var equivalents (`HADES_DMG_SIGN_IDENTITY`, `HADES_DMG_NOTARIZE_PROFILE`,
`HADES_DMG_ALLOW_UNSIGNED=1`) for CI use.

### 6.5 Signed release, step by step (for once a certificate exists)

Nothing here is exercised on this machine - there is no certificate to exercise it with. Written
now so it can be followed later without rediscovering any of it.

1. **Certificate type: "Developer ID Application"** - not "Developer ID Installer" (that signs
   `.pkg` installers; Hades ships as a DMG, not a pkg) and not an App Store distribution
   certificate (Hades does not ship through the Mac App Store). Requires an active Apple Developer
   Program membership.
2. **Obtain it**: Xcode > Settings > Accounts > select the team > Manage Certificates > "+" >
   Developer ID Application. (Or via developer.apple.com/account > Certificates, IDs & Profiles,
   with a CSR generated in Keychain Access, if Xcode-managed signing is not being used.) Confirm it
   is present with `security find-identity -v -p codesigning` - it should list at least one
   identity where today it lists zero.
3. **notarytool credentials**: generate an app-specific password at appleid.apple.com (Sign-In and
   Security > App-Specific Passwords) - not the Apple ID account password. Find the Team ID on the
   membership page at developer.apple.com/account, or in the certificate itself. Then store a
   keychain profile once:
   ```
   xcrun notarytool store-credentials "hades-notary" \
     --apple-id "you@example.com" \
     --team-id "TEAMID" \
     --password "the-app-specific-password"
   ```
   `hades-notary` here is a local keychain profile name, not sent anywhere - it is what
   `build-dmg.sh --notarize-profile` should be given from then on.
4. **Run the build**: `build-dmg.sh Release --sign "Developer ID Application: NAME (TEAMID)"
   --notarize-profile hades-notary`. This signs with hardened runtime and a secure timestamp,
   builds the DMG, submits it via `xcrun notarytool submit --wait`, and on acceptance staples the
   ticket with `xcrun stapler staple` automatically - nothing further to run by hand for a normal
   success.
5. **If notarization is rejected**, the script stops (does not staple, says so) and names the
   `notarytool log` command with the actual submission id to inspect why.
6. **Gatekeeper assessment** (Plan 14 Task 9's "Gatekeeper assessment test"; also what
   `build-dmg.sh` itself runs as its last step on the signed path):
   ```
   spctl -a -t open --context context:primary-signature -v Hades-X.Y.Z.dmg   # the DMG
   spctl -a -vv -t exec /Applications/Hades.app                              # the installed app
   ```
   Expect `accepted`, `source=Notarized Developer ID` on both - contrast with today's measured
   `rejected` for the unsigned build (section 6.1).
7. Update `Casks/hades.rb`: replace `sha256 :no_check` with the real checksum
   (`shasum -a 256 Hades-X.Y.Z.dmg`) once the DMG is actually uploaded to the `url` it names.

### 6.6 The Homebrew cask (`Casks/hades.rb`)

**Layout**: `Casks/hades.rb` at the main repo's root, not a separate `homebrew-hades` tap repo -
one source of truth versioned with the app (same reasoning section 2 above already applies to
`package.json` / `plugin.json` / `marketplace.json`), and `Casks/<token>.rb` at a tap's root is the
standard layout either way, so nothing structural changes if a dedicated `homebrew-hades` tap is
ever published later for the shorter `brew tap TheArcForge/hades` form. That publication step -
and tapping this repo directly under its real name - is deliberately not done as part of this
work: it is outward-facing and needs the user's explicit go-ahead.

**`url` is not live yet.** There is no release workflow that uploads `Shell~/HadesApp`'s DMG as a
GitHub Release asset (the existing `release.yml` in section 1 only covers Bridge/Scanner/the
plugin repo). `Casks/hades.rb` names the intended eventual location and uses `sha256 :no_check`
until a real artifact exists there - see step 7 in section 6.5 for replacing it.

**`zap` removes exactly two things**: `~/Library/Application Support/Hades` (the app-data root -
`Hades.Core.Storage.AppPaths` on the .NET side, `HadesControl.Discovery` on the Swift side, both
default here) and `~/Library/Preferences/com.arcforge.hades.shell.plist` (the app's own
`UserDefaults`, confirmed via `Shell~/HadesApp/Sources/HadesApp/Onboarding/
OnboardingCompletionTracking.swift`). **It never touches a project's own `.arcforge/memory/`** -
that directory lives inside the user's own Unity project repositories (e.g.
`~/Projects/<their-project>/.arcforge/memory/`), never under `~/Library`, so it is structurally
outside anything `zap` names; it is the user's authored work, not this app's, and the two zap
entries above cannot reach it regardless of which project(s) the user has open.

A plain `brew uninstall hades` (no `--zap`) removes only the app bundle - Homebrew Cask never runs
`zap` stanzas unless the user explicitly asks for it (`--zap`, or a separate `brew zap`). Both
paths above survive a plain uninstall by design; this is standard Homebrew behavior, not a Hades
particularity.

**Known gap this cask does not (and cannot cleanly) address**: if the user ever enables Hades'
"Launch at Login," that is registered via `SMAppService.mainApp` (`Shell~/HadesApp/Sources/
HadesApp/ShellFacts/LaunchAtLoginService.swift`), which macOS manages outside any single
discoverable file - there is nothing a `zap trash:` stanza can safely target for it without
risking unrelated login items. Turning "Launch at Login" off in Hades itself before uninstalling
is the clean way to clear that registration; this is called out here rather than silently ignored.

### 6.7 Testing the cask locally, today

Verified on this machine, Homebrew 6.0.15: `brew install --cask` **refuses a loose local `.rb`
file** ("Homebrew requires casks to be in a tap"), and `brew tap <user>/<repo> <path>` requires
`<path>` to already be a git repository - it shells out to `git clone` directly. `brew tap-new`,
Homebrew's own suggested fix, provisions that repository by running `git init` / `git add` / `git
commit` itself (confirmed by reading `dev-cmd/tap-new.rb`).

For most engineers this is a non-issue: run `brew tap-new local/hades-test`, copy `Casks/hades.rb`
in with `url` pointed at a local `file:///.../Hades-X.Y.Z-unsigned.dmg` path and `sha256` set to
that file's real checksum (`shasum -a 256`), `brew tap local/hades-test <path>`, `brew install
--cask local/hades-test/hades`, then `brew untap local/hades-test` and delete the scratch directory
when done.

That path was not available while verifying this under a standing "never run git, even indirectly"
constraint. What was actually run instead, and why it is a faithful substitute: Homebrew Cask's
`app` stanza (`cask/artifact/moved.rb`, `Moved#move`) installs a fresh app with a plain
`FileUtils.move`/copy into `/Applications` - no quarantine attribute is involved anywhere in that
path, matching how the DMG itself was never quarantined (it was never downloaded through a
browser). Mounting the DMG and `cp -R`-ing `Hades.app` into `/Applications` by hand exercises that
exact mechanism. Launching the result via `open` (the same LaunchServices path a double-click or
Spotlight launch takes - directly executing the binary inside `Contents/MacOS/` would *not* count,
since that bypasses LaunchServices/Gatekeeper entirely) and confirming the process actually starts
is the real measurement in section 6.2. `rm -rf /Applications/Hades.app` reproduces a plain `brew
uninstall`; the `zap` paths were reasoned from source and demonstrated against an isolated copy
rather than deleted for real, since the real `~/Library/Application Support/Hades` and
`~/Library/Preferences/com.arcforge.hades.shell.plist` belong to whichever Hades instance is
actually running on the machine doing the testing.

One more thing worth knowing before repeating this - **historical as of section 6.9 below, still
true for a Debug build or an unbundled `swift run`, no longer true for a Release build**:
**`Shell~/HadesApp/Sources/HadesApp/AppDelegate.swift`'s `makeConfiguration()` resolves its own
project path from `#filePath` - resolved at compile time, not at runtime.** A Hades.app built by
`build-app.sh Debug` - installed anywhere, DMG or cask, back when the DMG/cask only ever packaged a
Debug-shaped bundle - shelled out to `dotnet run --project <that checkout>/App~/src/Hades.Server
--no-launch-profile` on launch unconditionally (its own doc comment named this a deliberate,
temporary placeholder: *"Spec #4 (distribution) replaces `dotnet run` against source with a
self-contained published binary embedded in the app bundle"*). Concretely, this meant:
- The app was **not self-contained** - a real recipient's Mac needed the .NET SDK and this exact
  source checkout at this exact path to run it at all. Distributing a DMG/cask to anyone else did
  not produce a working app; only the packaging and Gatekeeper-channel behavior this plan covers
  were ready. **This is fixed for Release builds - see section 6.9.** `build-dmg.sh` always builds
  Release, so every DMG/cask produced today is self-contained; only a local `build-app.sh Debug`
  (day-to-day Swift-side iteration) still needs the SDK and this checkout, and says so loudly when
  it falls back (section 6.9's own logging example).
- Testing a second instance on the **same** machine a live instance is already running on
  (assuming the same checkout - the case here) means both `dotnet run`-based processes ultimately
  target the exact same `App~/src/Hades.Server` project. Verified empirically before relying on it:
  running a second `dotnet run` against that project while the first stayed up did not rebuild
  anything (nothing had changed - MSBuild's incremental check fast-paths straight to execution) and
  did not disturb the live instance's port or process. Isolating **both** `ASPNETCORE_URLS` (away
  from the live port and any other in-use port) and `HADES_HOME` (away from the live app-data root,
  which also backs the control API's own discovery file - not just the MCP port) for any test
  instance is still the right precaution regardless, and is what was actually done here - see
  `open`'s own `--env` flag. Section 6.9's own standalone proof for the embedded core follows the
  same discipline: an isolated port and an isolated `HADES_HOME`, never the live app's own.

### 6.8 A mistake made while verifying this, disclosed rather than hidden

The first `brew tap` call issued while testing section 6.7 auto-updated Homebrew itself and
refreshed `homebrew/core`, `homebrew/cask`, and the user's own `steipete/tap` before failing on the
git-clone step - `HOMEBREW_NO_AUTO_UPDATE=1` should have been set before the first `brew` call of
the session, not after. This was not reverted (there is no clean "downgrade Homebrew" operation,
and attempting one would risk more disruption than the update itself). No formulae or casks were
installed, upgraded, or removed beyond that index refresh, and the failed tap attempt itself left
no files behind (`git clone` failed before creating anything under `Library/Taps/`).

### 6.9 Self-contained core embedding (Spec #4) - implemented

Section 6.7's own note about `dotnet run` against source described the ONLY behavior that existed
at the time. It no longer describes a **Release** build. `scripts/build-app.sh Release` - and
therefore `scripts/build-dmg.sh`, which always builds Release regardless of what `build-app.sh`
itself defaults to - now publishes `Hades.Server` self-contained and embeds it in the bundle before
signing. `build-app.sh Debug` and an unbundled `swift run` still behave exactly as section 6.7
describes - see "Keeping the dev path" below for why that split is deliberate, not an oversight.

**The build is Apple Silicon (arm64) only.** This is stated here in prose, not just in the DMG
volume name (`Hades $VERSION (Apple Silicon)` / `... (Unsigned, Apple Silicon)`, `build-dmg.sh`)
and the unsigned build's own README: **a Hades.app built today will not run on an Intel Mac.** See
"osx-arm64 only, not universal" below for why, and what a universal build would actually require.

**Decisions, each with its reasoning:**

- **Self-contained, not framework-dependent.** The entire point of this work is that a recipient
  has neither the .NET SDK nor any .NET runtime installed - framework-dependent publish still needs
  a matching shared runtime present system-wide, which does not solve that problem at all.
  `dotnet publish -r osx-arm64 --self-contained true` is what `build-app.sh` runs.
- **osx-arm64 only, not universal.** This machine builds on Apple Silicon (`dotnet --info` -> RID
  `osx-arm64`). A universal (arm64+x64) build needs two full self-contained publishes merged with
  `lipo` per native file (the managed IL is architecture-neutral and would be shared, but every
  native `.dylib` and the apphost itself would need merging) - real, currently-unverified work with
  no Intel Mac and no CI runner available to prove a merge did not silently break something. Picking
  arm64-only and labeling it everywhere a recipient would see it was judged safer than guessing at a
  universal build with no way to test the x64 half.
- **`Contents/Resources/HadesServer/`, not `Contents/MacOS/`.** The published core is not one
  auxiliary executable - measured at 376 files (the apphost, ~360 managed/native files,
  `Hades.Server.deps.json`/`Hades.Server.runtimeconfig.json`, and friends). `Contents/MacOS` is the
  bundle's conventional home for actual entry points - `HadesApp` itself, plus the one small
  `HadesCoreReaper` helper that `Bundle.main.url(forAuxiliaryExecutable:)` already finds there - not
  for an entire embedded runtime tree. `Contents/Resources` is the conventional bundle location for
  exactly that kind of bulk embedded content. Code signing does not favor either location -
  `codesign --deep` walks both identically, confirmed below - so bundle hygiene was the deciding
  factor, not a signing constraint.
- **Untrimmed, no ReadyToRun.** Neither `PublishTrimmed` nor `PublishReadyToRun` is passed.
  Trimming was considered and rejected, not just skipped by default: this core loads Roslyn
  (`Microsoft.CodeAnalysis.CSharp`), `Microsoft.Data.Sqlite`/`SQLitePCLRaw` (P/Invoke plus ADO.NET
  provider-factory reflection), and `System.Text.Json`, underneath `Microsoft.NET.Sdk.Web` (whose
  minimal-API surface is not fully trim-safe either) - all reflection-adjacent in ways a trimmer
  cannot fully prove safe by static analysis, and this pass had no budget to exhaustively exercise
  all 32 MCP tools plus every Roslyn/SQLite/JSON path under a trimmed build to find out empirically.
  A build that launches but silently breaks one specific reflection-only path is a worse outcome
  than the size cost of not trimming. ReadyToRun was measured, not just skipped: cold start (process
  launch to a real, authenticated `/control/ping` 200 response) came in at **973ms** (discovery file
  written at 840ms) - see "Measured sizes and timing" below - against `CoreSupervisor`'s 15-second
  `pingTimeout` budget. There is no real startup problem here for R2R to solve, so it was not added.

**Keeping the dev path.** `dotnet publish --self-contained` takes real time even incrementally -
paying it on every `build-app.sh Debug` during ordinary Swift-side iteration would tax the dev loop
for a step Debug does not need. So the publish/embed step is Release-only; a Debug build's
`Contents/Resources` has no `HadesServer/` at all, and `AppDelegate.makeConfiguration()` falls back
to the original `dotnet run --project <repo>/App~/src/Hades.Server --no-launch-profile`, exactly as
before. That fallback is never silent: an `os.Logger` (subsystem `com.arcforge.hades.shell`,
category `CoreLaunch`) logs which branch ran, on every launch - confirmed with `log show`:

```
[com.arcforge.hades.shell:CoreLaunch] No bundled core at Contents/Resources/HadesServer/Hades.Server
(looked in <path>) - falling back to `dotnet run --project <repo>/App~/src/Hades.Server
--no-launch-profile`. This needs the .NET SDK and this exact source checkout on THIS machine;
expected for a build-app.sh Debug build or an unbundled swift run, never for a distributed
Hades.app.
```

and, symmetrically, when the bundled core is present and used:

```
[com.arcforge.hades.shell:CoreLaunch] Launching bundled self-contained core: <path>
```

**Measured sizes and timing** (this machine, this build, `dotnet` 10.0.301):

| | |
|---|---|
| `HadesApp.app`, Release, before embedding | ~3.4 MB |
| `Contents/Resources/HadesServer/` (376 files) | ~134 MB |
| `HadesApp.app`, Release, after embedding | **~137 MB** |
| `Hades-0.1.0-unsigned.dmg` (UDZO-compressed) | **~58.7 MB** |
| Cold start: process launch -> discovery file written | 840 ms |
| Cold start: process launch -> `/control/ping` answers 200 | 973 ms |

**Code signing - verified, not assumed.** `codesign --force --deep --sign -` (`build-app.sh`'s
existing ad-hoc step, unchanged in its invocation) turned out to already sign every nested Mach-O it
finds under `Contents/Resources/HadesServer/` correctly, with no separate pre-signing loop needed -
confirmed by inspecting the actual result, not by reading `codesign`'s man page and assuming one
way or the other. The freshly-built `Hades.Server` apphost came out ad-hoc signed
(`flags=0x2(adhoc)`, `TeamIdentifier=not set`), while the native runtime dylibs Microsoft ships in
the runtime pack (`libcoreclr.dylib`, `libSystem.Native.dylib`, and 12 others - 14 in total) kept
THEIR OWN pre-existing Developer ID signature untouched (`TeamIdentifier=UBF8T346G9`, hardened
runtime, secure timestamp) - `codesign --deep` does not clobber content that already carries a
valid signature. `codesign --verify --deep --strict --verbose=2` against the resulting bundle:

```
Hades.app: valid on disk
Hades.app: satisfies its Designated Requirement
```

`build-app.sh` now runs this verification itself immediately after signing, as a hard failure (not
an informational `|| true`) - see its own header comment - so a future change that breaks nested
signing fails the build immediately rather than shipping a bundle whose signature only turns out to
be broken later, at notarization or launch time.

**Proof it runs standalone.** The `.app` from a completed `build-app.sh Release` was copied to a
location outside this repository entirely, then launched with an isolated `HADES_HOME` and
`ASPNETCORE_URLS` - never port 7823 (the live app's own port) and never the real application-data
root - so as not to disturb anything already running:

- `HadesCoreReaper`'s own argv (visible via `ps`) showed it invoking
  `<copied bundle>/Contents/Resources/HadesServer/Hades.Server` directly, by absolute path - no
  `dotnet`, no `/usr/bin/env`, no `PATH` search of any kind, exactly what `posix_spawn` requires.
- The running `Hades.Server` process's own image path (`ps`) was inside the copied bundle - nowhere
  near `App~/src` - the concrete, observable difference from the OLD `dotnet run --project
  <checkout>/App~/src/Hades.Server` invocation, which always names the checkout explicitly in its
  own argv.
- The discovery file (`<HADES_HOME>/control.token`) appeared quickly after launch; `/control/ping`
  and `/control/settings` both answered `200 OK` using the real bearer token read back from that
  file - the exact mechanism `CoreSupervisor.canPing()` itself uses to decide a spawn succeeded.
- A real MCP `initialize` handshake against the isolated port answered
  `{"result":{"protocolVersion":"2025-06-18",...,"serverInfo":{"name":"Hades.Server",...}}}` over
  SSE - the full ASP.NET Core + MCP SDK stack answering correctly, not just "a process exists and
  holds a port".
- Killing the test instance by its exact pid exercised `HadesCoreReaper`'s parent-death cleanup
  (its own class doc comment) against the new core unchanged: the whole process tree (app, reaper,
  core) tore down completely, and the test port was free immediately after.

A test against a literally unreachable checkout (renaming or hiding `/Users/mike/Projects/Hades`
itself, or `#filePath`'s own compile-time value pointing nowhere) was not attempted - that would
reach well outside this task's file scope and risk the live app already running from that same
checkout. The evidence above - an independent copy, launched with the checkout's own discovery
state deliberately excluded, whose `HadesCoreReaper` argv never names the checkout at all - is the
strongest test available without that risk, and directly shows the shipped path has no runtime
dependency on the checkout: the fallback code that WOULD reference it is never even reached.

**Test baselines - unchanged.** `swift test` in all three `Shell~` packages, run after every change
above: HadesControl 66, HadesSupervision 10, HadesApp 198 - all passing, exactly matching the
pre-existing baseline.

---

## 7. Pre-release: deleting the v1.2 tree

**Decided, not yet executed.** `Editor/`, `Tests/`, `ThirdParty/`, `Fixtures~/`, `package.json`, and
`Editor/Core/AppNapGuard.cs` with them - the entire v1.2 Unity package - is being kept in the repo
until **after the internal testing round, but before release**. It is the only working v1.2
reference for testing migration (spec #4 §5) against a real install; deleting it earlier would mean
testing that migration against nothing.

**Two things break the moment it goes. Handle both in the same commit as the deletion, not after:**

1. **`App~/tests/Hades.Core.Tests/Indexing/RealProjectIndexSmokeTest.cs` goes red.** It asserts
   `150 < FilesScanned < 230` against a real Unity project on this machine
   (`/Users/mike/Projects/Hades-Unity-Client`), a window measured 2026-08-01 as 107 `Editor/` + 65
   `Tests/` + 10 `ThirdParty/` files pulled in through that project's local `file:` package
   reference to this repo, plus 16 files in the project's own `Assets/`. Once `Editor/`, `Tests/`,
   and `ThirdParty/` are gone, the scan drops to roughly that 16-file `Assets/`-only floor - which
   the test's own comment already names as the *lower* regression bound today (`"below ~150 →
   local-package resolution broke (Assets/ alone yields 16)"`). Post-deletion that floor is no
   longer a regression signal; it is the new correct answer. Re-baseline the window and the
   comment's file-count accounting in the same commit that deletes the tree, against whatever the
   real project actually scans to once the package is gone - don't leave it red, and don't widen
   the window blind without re-measuring.
2. **The user's live v1.2 install stops working.** Their Unity Editor loads this repo as a local
   `file:` package via `Packages/manifest.json`, so removing `package.json` breaks that resolution
   the instant this commit lands. That is not a bug to fix - it is the intended end of the v1.2
   install this tree exists to keep testable - but it means confirming migration testing is
   actually done before this commit goes in, not after.

**`Editor/Core/AppNapGuard.cs` goes only as part of this same deletion, never separately or early.**
`HadesBootstrap` acquires it in a static constructor; removing it alone breaks the legacy Editor's
compile while the rest of `Editor/` - and the user's live v1.2 install - still depends on that
Editor working.
