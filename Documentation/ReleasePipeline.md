# Release Pipeline

How Hades is tested, validated, and shipped. This document covers CI, versioning, pre-release checklist, Anthropic plugin submission, and step-by-step instructions for AI agents preparing a release, plus (section 6) building and distributing the Hades.app menu-bar shell itself via DMG and Homebrew cask.

**Start at section 8 to actually ship a release.** It is the current, concrete, top-to-bottom v2 checklist and supersedes the step-by-step mechanics in sections 2-5 (which describe the retired v1.2 flow - see the banner at the top of section 2). Section 6 holds the detailed build/signing/cask reasoning section 8 points back to rather than repeats.

---

## 1. CI overview

Three GitHub Actions workflows across two repositories.

### Main repo (`TheArcForge/Hades`)

**`ci.yml`** — runs on every push and PR to `main`.

Three parallel jobs:
- **Bridge tests** — installs dependencies and runs Vitest in `Bridge~/`, then builds TypeScript to verify compilation.
- **Scanner tests** — installs dependencies and runs the split Jest suite in `Scanner~/` (unit tests first, integration tests second, separated due to tree-sitter native addon conflicts).
- **`dotnet-tests`** ("App (.NET) Tests") — runs `dotnet test` against the .NET core in `App~/`.

The Swift (`swift test` in `Shell~/HadesControl`, `Shell~/HadesSupervision`, `Shell~/HadesApp`),
Unity plugin EditMode (`scripts/regression/run-plugin-editmode.sh`), and e2e
(`scripts/regression/hades_suite.py`) suites all run outside `ci.yml` — see §5 for the current
verification commands.

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

**Current (v2) version-stamp sites — not covered by anything above.** The app core, shell, and
Unity plugin do not use `package.json`, `Legacy~`, or any Bridge/Scanner file for their own
versions; these are the sites that actually carry the shipped version today:

| Location | Reports as | Notes |
|---|---|---|
| `App~/src/Hades.Server/Mcp/HadesTools.cs` → `ServerVersion` | MCP server version (`hades_status`, `initialize`) | plain constant — bump manually |
| `Shell~/HadesApp/scripts/build-app.sh` → Info.plist `CFBundleShortVersionString` / `CFBundleVersion` | App bundle version | kept in lockstep with `ServerVersion` above; `build-dmg.sh` derives the DMG's filename from this plist |
| `Plugin~/Assets/Hades/Runtime/HadesBoot.cs` → `PluginVersion` | Unity plugin version (sent in the `Hello` handshake) | independent version line from the app; two test mirrors (`CharonStatusTests.cs`, `Control/ProjectsTests.cs`) kept in sync with it |

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
- [ ] `Documentation/Architecture.md` — reflects current app/plugin versions and tool count
- [ ] `Documentation/Installing.md` — install steps, version checks, and known issues current
- [ ] `Documentation/RegressionCoverage.md` — issue → test traceability current with the latest round

### Plugin sync

- [ ] `Plugin-ClaudeCode~/` content is current (static skills/commands — no build step)
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
- No orphan background processes — the plugin spawns none; its `.mcp.json` points at the already-running app
- No fixed port conflicts — the plugin declares an HTTP endpoint to the app at `127.0.0.1:7823` and binds no port of its own
- No writes to `~/.claude.json` or `~/.claude/settings.json`
- No `hooks`, `mcpServers`, or `permissionMode` in plugin agents

### Current install paths

> **Not current.** This subsection describes the intended shape of Anthropic marketplace
> submission, not today's working install path. As of this internal testing round, the
> self-hosted marketplace (`TheArcForge/hades-plugin`) has not been resynced to the current
> plugin — it still serves the retired v1.2 plugin (Node stdio launcher, closer to 90 tools
> than 32). Do not point testers or users at `/plugin marketplace add` today. The working path
> is `claude --plugin-dir <path>/Plugin-ClaudeCode~` — see
> `Documentation/Installing.md`.

Before marketplace acceptance, the plan is for users to install via the self-hosted marketplace:
```
/plugin marketplace add TheArcForge/hades-plugin
/plugin install hades
```

After marketplace acceptance:
```
/plugin install hades
```

Both paths are intended to coexist — the self-hosted marketplace remaining as an alternative —
once the self-hosted one is actually resynced to the current plugin (see warning above).

---

## 5. AI agent release preparation guide

When asked to prepare a release, follow these steps exactly. Report each result to the user. Do not proceed to tagging without explicit user approval.

### Step 1: Verify tests pass

```bash
# .NET (~1863 tests)
cd App~ && HADES_HOME=$(mktemp -d) dotnet test

# Swift (70 / 14 / 211 tests)
cd Shell~/HadesControl && swift test
cd Shell~/HadesSupervision && swift test
cd Shell~/HadesApp && swift test

# Unity plugin EditMode (384 tests, batchmode)
scripts/regression/run-plugin-editmode.sh

# e2e (25 cases)
python3 scripts/regression/hades_suite.py --url http://127.0.0.1:7823/mcp
```

Report: which suites passed, which failed, any warnings or deviations.

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
7. `Documentation/Architecture.md` — current app/plugin versions and tool count
8. `Documentation/Installing.md` — install steps, version checks, and known issues current
9. `Documentation/RegressionCoverage.md` — issue → test traceability current with the latest round

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
| **Homebrew cask** | **Quarantined** -> same prompt. Corrected 2026-08-18, see below. |
| **`install.sh` (curl)** | Not quarantined -> launches with no prompt |

**Corrected 2026-08-18. The earlier version of this section was wrong about Homebrew**, and the
error is worth recording because of how it happened. The original measurement never ran Homebrew:
under a standing "never run git" constraint (see 6.7), a hand `cp -R` of `Hades.app` into
`/Applications` was substituted as a faithful stand-in for Cask's `Artifact::Moved#move`. That
substitution is faithful for the *copy* step and only the copy step - it skips Homebrew's
**download** step, which is where the quarantine attribute is actually applied. Reading
`Cask::Quarantine.check_quarantine_support` (which returns `:quarantine_unavailable`) reinforced
the wrong conclusion. A source read is not a measurement.

What a real `brew tap-new` + `brew install --cask` actually produces, measured end to end:

| Artifact | `com.apple.quarantine` |
|---|---|
| Source DMG, built locally by `build-dmg.sh` | absent |
| Homebrew's cached download of that same DMG | `0281;...;5DBE0458-...` |
| `Hades.app` after `brew install --cask` | `0381;...;5DBE0458-...`, user-approved bit clear |
| The same DMG fetched by plain `curl` over HTTPS | absent (only `com.apple.provenance`) |

Homebrew stamps the attribute on its own download; the app then inherits it (same UUID) when
copied out of the mounted image. `--no-quarantine` has been **removed** from Homebrew entirely
(`brew install --cask --no-quarantine` -> `Error: invalid option`), so there is no supported
opt-out. Homebrew also intends to drop Gatekeeper-failing casks from the official `homebrew/cask`
tap on 2026-09-01, though maintainers explicitly point unsigned apps at self-hosted taps, so a
third-party tap remains usable.

The general principle in this section still holds - `curl` and `git clone` do not quarantine - it
simply does not extend to Homebrew. **`install.sh` at the repo root is the frictionless path**
until a certificate exists; Homebrew's value would be install/upgrade management, not avoiding
Gatekeeper.

### 6.3 Installing Hades today: three paths, documented honestly

**Path A - `install.sh` (recommended).** The only route with no Gatekeeper prompt, because `curl`
does not set `com.apple.quarantine` (6.2 above, measured).

```
curl -fsSL https://raw.githubusercontent.com/TheArcForge/Hades/main/install.sh | bash
```

It pins the version and its SHA-256, verifies the download before installing, refuses on Intel or
macOS < 14, refuses to run under `sudo`, and refuses to replace a running Hades. Its two
maintenance points are the `VERSION` and `SHA256` constants at the top, bumped per release from
the artifact actually attached to the release. It gives up what a package manager provides:
no `upgrade`, no `uninstall` - the script prints the uninstall commands instead.

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
7. Update `install.sh`'s `VERSION` and `SHA256` constants from the DMG actually uploaded
   (`shasum -a 256 Hades-X.Y.Z.dmg`). Once signed, revisit Homebrew - see 6.6.

### 6.6 Homebrew - evaluated, not used

**There is no cask in this repo.** `Casks/hades.rb` existed until 2026-08-18 and was deleted; the
reasoning is preserved here so nobody rebuilds it without knowing what it costs. `git log --diff-filter=D
-- Casks/` recovers the file if a signed release ever makes Homebrew worth revisiting.

Why it was dropped:

- **Homebrew quarantines its downloads** (6.2, measured end to end), so a cask install of an
  unsigned app is blocked on first launch exactly like a browser download. `--no-quarantine` has
  been removed from Homebrew, so there is no supported opt-out. Homebrew's remaining value is
  install/upgrade/uninstall management - not avoiding Gatekeeper, which is what it was chosen for.
- **The cask was unreachable.** No tap was ever published, so the only way to reach it was tapping
  the main repo, which handed the user `sha256 :no_check` - an unverified install - against a
  `url` that 404s. That is a trap, not a distribution channel.
- **It was an untracked version location.** It carried its own `version` string that section 2's
  lockstep list never named and no test pinned, so it was guaranteed to drift.
- **61% of it was comments** explaining why it existed and why it could not be used yet. That prose
  was wrong once already, in the worst possible place: `caveats` is printed to the user by Homebrew
  during install.

If Homebrew is revisited after signing, useful facts measured while evaluating it:

- Tap naming: `brew tap owner/name` resolves to `github.com/owner/homebrew-name`. Naming the repo
  `homebrew-tap` yields `brew install --cask thearcforge/tap/hades`; naming it `homebrew-hades`
  yields the stuttering `thearcforge/hades/hades`.
- `brew tap` does a **full clone** (no `--depth` anywhere in `tap.rb`), so putting a cask in the
  main repo makes every user clone the whole history for one file.
- Homebrew intends to drop Gatekeeper-failing casks from the official `homebrew/cask` tap on
  2026-09-01, but maintainers explicitly point unsigned apps at self-hosted taps, so a third-party
  tap stays viable either way.

### 6.7 Verifying install.sh locally

`install.sh` is the shipped install path (6.3 Path A). To exercise it without touching
`/Applications` or requiring a published release, copy it and rewrite three things: `URL` to a
`file://` path pointing at a locally built DMG, `INSTALL_DIR` to a scratch directory, and drop the
`--proto '=https'` guard that (correctly) refuses `file://` in the real script.

What that run must show, all four confirmed on 2026-08-18 against `Hades-2.0.0-unsigned.dmg`:

1. `sha256 OK`, then a successful install into the scratch directory.
2. **No quarantine attribute on the installed bundle** - the whole point of the curl path.
3. `codesign -v` still valid on the installed app. This is why the script uses `ditto` rather than
   `cp -R`: `cp -R` mangles bundle extended attributes and breaks the signature, producing launch
   failures that are miserable to diagnose.
4. A deliberately corrupted `SHA256` constant makes it refuse, printing both digests.

Also verify the refusals by inspection or by running on the relevant hardware: Intel, macOS < 14,
`sudo`, and a running Hades each abort with an actionable message rather than a partial install.

### 6.8 A mistake made while verifying this, disclosed rather than hidden

The first `brew tap` call issued while evaluating Homebrew (6.6) auto-updated Homebrew itself and
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
| `Hades-2.0.0-beta.3-unsigned.dmg` (UDZO-compressed) | **~58.7 MB** |
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
above: HadesControl 70, HadesSupervision 14, HadesApp 211 - all passing, exactly matching the
pre-existing baseline.

---

## 7. Pre-release: deleting the v1.2 tree

**Executed 2026-08-18, as part of 2.0.0 release prep.** The entire v1.2 Unity package - `Editor/`
(including `Editor/MCP/AppNapGuard.cs`; an earlier draft of this section said `Editor/Core/`, which
was wrong), `Tests/`, `ThirdParty/`, `Fixtures~/`, and the root `package.json` - plus the retired
Node stack (`Bridge~/`, `Scanner~/`) is gone: 560 tracked files. It had been kept as the only
working v1.2 reference for testing migration (spec #4 §5) against a real install; the product owner
chose to end that install rather than hold the release for it.

Every path was checked for live references first. The one that mattered: `ThirdParty/`'s
`Gilzoide.SqliteNet` was referenced only by the two asmdefs deleted alongside it - `App~` uses the
`Microsoft.Data.Sqlite` NuGet package, and `Plugin~` does not use SQLite at all.

**What broke, and how each was handled in the same change:**

1. **`.github/workflows/ci.yml`** ran `bridge-tests` and `scanner-tests` against trees that no
   longer exist. Both jobs removed; `dotnet-tests` untouched.
2. **`RealProjectIndexSmokeTest.cs` was re-baselined by measurement, not prediction.** Its
   `150 < FilesScanned < 230` window only held because `Editor/`+`Tests/`+`ThirdParty/` were pulled
   into the fixture project through its local `file:` package reference. This section previously
   predicted the scan would fall to "roughly that 16-file `Assets/`-only floor." **That prediction
   was wrong**: measured after removal, the project scans to **45 files / 64 types**, because its own
   `Assets/` grew in the weeks since the 2026-08-01 measurement. The window is now
   `25 < FilesScanned < 120` with `TypesFound > 40`. This is the argument for re-measuring instead
   of reasoning from a recorded number - the doc's arithmetic went stale before the code did.
   `RealProjectBinaryAssetIndexSmokeTest.cs` needed no assertion change (already scoped to
   `Assets/`), but its doc-comment described the package's second texture as a present fact and was
   moved to past tense.
3. **The user's live v1.2 install ended.** Their Unity project loaded this repo as a local `file:`
   package, so the `com.arcforge.hades` entry was removed from that project's `Packages/manifest.json`
   and `packages-lock.json` - and its orphaned `testables` entry with it - **before** this tree was
   deleted, so the project was never left pointing at an unresolvable package.

---

## 8. Current release procedure (v2)

The authoritative, current, top-to-bottom checklist for shipping a v2 release - run this section in
order. It supersedes sections 2-5 above for *execution* (those describe the retired v1.2 flow, per
the banner at the top of section 2); section 6 holds the detailed build/signing/cask reasoning this
section points back to rather than repeats. Both open variables this document previously flagged
are now resolved by product decision: **distribution is Homebrew, v1 ships unsigned** (no Apple
Developer ID certificate / no notarization - signing is future work), and **the
`TheArcForge/hades-plugin` marketplace will be republished with the current plugin at release** -
step 5 below covers exactly what that republish requires and its one known blocker.

### 8.1 Stamp the version - in lockstep

Bump these together to the release version (`X.Y.Z`, e.g. `2.0.0`):

| Site | Field |
|---|---|
| `App~/src/Hades.Server/Mcp/HadesTools.cs` | `ServerVersion` constant |
| `Shell~/HadesApp/scripts/build-app.sh` | Info.plist `CFBundleShortVersionString` |
| `Shell~/HadesApp/scripts/build-app.sh` | Info.plist `CFBundleVersion` (build number - bump too) |

`build-dmg.sh` (8.3 below) reads `CFBundleShortVersionString` back out of the already-built `.app`
to name the DMG - rebuild the app after bumping `build-app.sh`, before running `build-dmg.sh`, or
the DMG filename carries the stale version.

The Unity plugin carries its own, independent version line (section 2 above documents this - it is
not the product version and is not expected to match it). Bump it only if `Plugin~` itself changed
this release; if you do, its two test mirrors must move with it or their own pinning tests fail:

| Site | Field |
|---|---|
| `Plugin~/Assets/Hades/Runtime/HadesBoot.cs` | `PluginVersion` constant |
| `App~/tests/Hades.Server.Tests/CharonStatusTests.cs` | `RealAppPluginVersion` mirror constant |
| `App~/tests/Hades.Server.Tests/Control/ProjectsTests.cs` | `RealAppPluginVersion` mirror constant |

### 8.2 Full verification gate

```bash
cd App~ && HADES_HOME=$(mktemp -d) dotnet test
cd Shell~/HadesControl && swift test
cd Shell~/HadesSupervision && swift test
cd Shell~/HadesApp && swift test
scripts/regression/run-plugin-editmode.sh
python3 scripts/regression/hades_suite.py --url http://127.0.0.1:7823/mcp
```

The last command needs Hades.app already running, a real Unity project already added and indexed
in it (its assertions anchor against real graph content, not a fixture-only run), and a live Unity
Editor attached for the editor-dependent cases (`--no-editor` restricts it to protocol+graph cases
only and skips those). Expected counts per suite and what each one actually pins:
`Documentation/RegressionCoverage.md`.

### 8.3 Build

```bash
Shell~/HadesApp/scripts/build-dmg.sh Release --allow-unsigned
```

→ `Shell~/HadesApp/DerivedData/dmg/Hades-X.Y.Z-unsigned.dmg`. `--allow-unsigned` is deliberate, not
a placeholder flag - v1 has no Apple Developer ID certificate, so this is the only build this repo
can produce today (6.1, 6.4 above).

### 8.4 Publish

1. Create the GitHub Release on `TheArcForge/Hades` for tag `vX.Y.Z`; attach the DMG built in 8.3
   as a release asset.
2. Update `install.sh`'s `VERSION` and `SHA256` constants from the uploaded DMG, and confirm the
   URL it names resolves (`curl -fsSL -I` it). Until that release asset exists, the script 404s -
   that is the one thing publishing has to get right for the documented install path to work.
3. Verify `install.sh` end to end against the published release on a Mac that has never had Hades
   installed. Expect no Gatekeeper prompt (curl does not quarantine - 6.2) and `codesign -v` valid
   on the installed bundle. There is no cask to publish; Homebrew was evaluated and dropped (6.6).

### 8.5 Plugin / marketplace sync

1. Pushing tag `vX.Y.Z` runs `.github/workflows/release.yml`, which runs `scripts/sync-plugin.sh`
   and pushes the current `Plugin-ClaudeCode~/` content to `TheArcForge/hades-plugin` (section 1
   above).
2. **Known blocker - handle this as its own step, before or immediately after the push, not as a
   footnote discovered later:** `TheArcForge/hades-plugin`'s own `.github/workflows/validate.yml`
   still checks for the retired v1.2 shape (Bridge dist, Scanner source, a `.mcp.json` requiring
   `${CLAUDE_PLUGIN_ROOT}`-relative paths) and will fail red on this sync, because the synced
   content has none of that anymore. A drop-in replacement already exists in this repo at
   `Documentation/hades-plugin-validate.yml` - someone with push access to `TheArcForge/hades-plugin`
   must copy it over that repo's `.github/workflows/validate.yml`. This repo's own tooling cannot do
   this step; it has no access to that repo beyond the sync push itself.
3. Confirm the synced marketplace actually serves the current plugin, not the retired one - the
   same check `Documentation/Installing.md`'s "Confirm you're testing the new Hades"
   section already walks a tester through: tool count (32, not ~90) and `hades_status`'s `version`
   field.

### 8.6 Post-publish

1. Once 8.5.3 confirms the marketplace is current, flip install guidance from "local `--plugin-dir`
   only" back to the marketplace path everywhere it currently says otherwise: `scripts/plugin-README.md`,
   `Documentation/Installing.md`, this file's own section 4 "Current install paths",
   and `Shell~/HadesApp/Sources/HadesApp/Onboarding/Views/OnboardingClaudeCodeStepView.swift`.
2. Re-verify the marketplace install end to end: `/plugin marketplace add TheArcForge/hades-plugin`
   → `/plugin install hades` → `/mcp` reports `hades` at 32 tools over the HTTP URL, not a `node`
   command.
