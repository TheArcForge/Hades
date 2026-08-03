# Spec — Project-Local Hades Installation

**Status:** Draft, pending review
**Date:** 2026-07-29
**Problem owner:** Paolo Oranges

## 1. Problem

Installing Hades via Unity Package Manager scatters state across the machine. A single Unity
project cannot own its complete Hades installation, so a workspace is not self-describing and
two projects on one machine share a control plane.

Current global state, exhaustive:

| # | Artifact | Location | Written by |
|---|---|---|---|
| 1 | `hub.json`, `hub.lock`, `pending/` — hub discovery (port + pid) | `~/.arcforge/hades-hub/` | `Bridge~/launcher/src/index.ts:9`, `Bridge~/hub/src/index.ts:10`, `Editor/MCP/HubClient.cs:22` |
| 2 | `launcher.js` — stable launcher copy | `~/.arcforge/hades-hub/` | `MCPClientConfig.EnsureStableLauncher` (`:27`) |
| 3 | `hub-path.json` — absolute path back into the workspace | `~/.arcforge/hades-hub/` | `MCPClientConfig.WriteHubPath` (`:319`) |
| 4 | `hades-*/SKILL.md` — skill copies | `~/.claude/skills/` | `MCPClientConfig.InstallSkillsForDesktop` (`:272`) |
| 5 | `mcpServers.hades` | `~/Library/Application Support/Claude/claude_desktop_config.json` | `MCPClientConfig.UpdateClaudeDesktopConfig` (`:67`) |
| 6 | Port, Enabled, AutoStart, LogLevel, ReloadStrategy, ReloadTimeout, Charon\* | Unity `EditorPrefs` (machine-global, shared across all projects on a Unity version) | `Editor/Core/HadesSettings.cs` |

Already correctly project-local, unchanged by this work: `.arcforge/graph.db`,
`.arcforge/traces.db`, `.arcforge/memory/`, `.mcp.json`, `CLAUDE.md`.

### 1.1 Root cause

`HUB_DIR` is a hardcoded `$HOME`-relative constant in three places. Items 2 and 3 exist only to
give the HOME-rooted hub a stable entry point, and the project's `.mcp.json` points into HOME
only because item 2 lives there. Collapse the hub dir and items 1–3 collapse with it.

The architecture doc (§207) already asserts "Hades is fully project-scoped… no shared state
between instances". That is true of the **data plane** and false of the **control plane**. This
spec makes the assertion true.

### 1.2 Related bug (must fix regardless)

`MCPClientConfig.FindPackageLauncherDir` (`:338`) and `FindPackageSkillsDir` (`:303`) resolve the
package as `<project>/Packages/com.arcforge.hades`, which only exists for an **embedded** package.
The documented install path (Package Manager → *Add package from git URL*) resolves to
`Library/PackageCache/com.arcforge.hades@<hash>`, so both methods return `null` and the launcher
copy and skill install silently no-op. The rest of the codebase already uses the correct API —
`PackageInfo.FindForAssembly(...).resolvedPath` (`GraphBuilder.cs:858`, `CharonDashboard.cs:75`).

## 2. Goals

1. A Unity project can hold its entire Hades installation inside its own workspace, with no
   dependency on `$HOME` state, and this is the **default**.
2. The local/global choice is user-visible and switchable from the Unity Editor menu.
3. All Hades settings become project-scoped, so two projects no longer share them.
4. Existing installations keep working; no silent behaviour change that breaks a live setup.

## 3. Non-goals

- Removing the shared-hub capability. Global mode stays fully supported.
- Changing the hub's wire protocol, MCP tool surface, or the `.arcforge` data-plane layout.
- Making the Claude Code plugin channel project-local. It already uses `${CLAUDE_PLUGIN_ROOT}`
  and is out of scope.
- Migrating `.arcforge/config.yaml` (git-tracked, team-shared) semantics. This spec only adds a
  per-developer local file.

## 4. Design

### 4.1 Hub directory resolution

One resolution chain, implemented identically in three processes (Unity C#, launcher, hub):

1. `HADES_HUB_DIR` environment variable, if set and non-empty.
2. `<projectRoot>/.arcforge/hades-hub/` when hub scope is `local` **and** `projectRoot` resolves.
3. `~/.arcforge/hades-hub/` otherwise.

Rationale per rung:

- Rung 1 is the explicit override and the seam that makes the chain unit-testable without
  touching a real `$HOME`.
- Rung 2 is the new default.
- Rung 3 preserves today's behaviour and is the automatic fallback when `projectRoot` cannot be
  determined. This matters for a real case: a launcher whose `cwd` is a `file:`-referenced
  package repo *outside* the Unity project. That case is routed today by
  `Registry.findByProjectPath`'s `manifestPackages` match (`registry.ts:148`); with a local hub
  the launcher cannot see the project's hub dir, so it correctly falls through to global.

`projectRoot` resolution is already solved on both sides:

- Unity: `PathSandbox.ProjectRoot`.
- Launcher: `resolveProjectPath(cwd)` (`project-path.ts:13`) walks up for
  `ProjectSettings/ProjectVersion.txt`.

The hub process does **not** re-derive anything. The launcher passes `HADES_HUB_DIR` explicitly
in the spawn env (`launcher/src/index.ts:94` already forwards `process.env`), so the hub reads
rung 1 or falls back to rung 3. This removes any chance of launcher and hub disagreeing.

### 4.2 Configuration file

New per-developer, project-local file: `<projectRoot>/.arcforge/config.local.yaml`.

This is the file the architecture doc already reserves as a design target (§1883, currently
"no loader and is not gitignored"). Format is the same flat `key: value` dialect the existing
`InferenceConfig.LoadFromDirectory` parser reads, so no YAML dependency is introduced on either
side of the language boundary.

| Key | Type | Default | Replaces |
|---|---|---|---|
| `hub_scope` | `local` \| `global` | `local` | *(new)* |
| `skills_scope` | `local` \| `global` | `local` | *(new)* |
| `desktop_integration` | bool | `false` | *(new)* |
| `mcp_port` | int | `0` | `Hades_MCP_Port` |
| `mcp_enabled` | bool | `true` | `Hades_MCP_Enabled` |
| `mcp_auto_start` | bool | `true` | `Hades_MCP_AutoStart` |
| `mcp_log_level` | int | `1` | `Hades_MCP_LogLevel` |
| `domain_reload_strategy` | `auto` \| `manual` | `auto` | `Hades_MCP_ReloadStrategy` |
| `reload_timeout_seconds` | int | `120` | `Hades_MCP_ReloadTimeout` |
| `charon_enabled` | bool | `true` | `Hades_MCP_CharonEnabled` |
| `charon_retention_days` | int | `30` | `Hades_MCP_CharonRetentionDays` |
| `charon_max_size_mb` | int | `500` | `Hades_MCP_CharonMaxSizeMb` |

Only `hub_scope` is read outside C#. The launcher's reader parses that one key and ignores the
rest, keeping the TypeScript side to roughly fifteen lines with no new dependency in the esbuild
bundle (the single-file-bundle invariant guarded by `Bridge~/tests/launcher/bundle.test.ts` must
hold — the reader goes in a sibling module that esbuild inlines, not a runtime import).

Missing file, missing key, and unparseable value all fall back to the defaults above. A missing
file is the normal state for a fresh clone and must never warn.

### 4.3 Settings surface

No preferences panel exists today; the architecture doc's reference to one (§1883) is
aspirational. This spec adds:

- `Editor/Core/HadesPreferences.cs` — a `SettingsProvider` at **Project Settings → Hades**,
  covering every key in §4.2. Writes go through `HadesConfig`.
- A `Hades/Settings…` menu item that opens that page, so the local/global toggle is reachable
  from the Hades menu as required.

Hub scope and skills scope render as an explicit two-option control with an inline note that
changing hub scope takes effect after the next Claude Code session restart (the launcher reads
the file at process start).

### 4.4 Launcher and skills placement

- Launcher stable copy moves to `<hubDir>/launcher.js`, i.e. it follows the hub dir. In local
  mode that is `<projectRoot>/.arcforge/hades-hub/launcher.js`.
- `hub-path.json` is written next to it, unchanged in content — it still points at the resolved
  package's `Bridge~/hub/dist/index.js`. In local mode that path stays inside the workspace
  (`Library/PackageCache/...`), so nothing escapes it.
- Project `.mcp.json` points at the resolved `<hubDir>/launcher.js`, written **project-relative**
  when the launcher is inside the project — `.arcforge/hades-hub/launcher.js` in local mode.
  Claude Code discovers `.mcp.json` in the directory it was started from and spawns the server
  with that directory as cwd, so the relative form resolves to the same file while keeping one
  developer's home directory out of the file. Global hub scope (or `HADES_HUB_DIR` pointing
  outside the project) has no relative form, so it stays absolute. Still gitignored
  (`.gitignore:53`) and rewritten on every server start, which self-heals a package version bump.
- Skills install to `<projectRoot>/.claude/skills/hades-*` when `skills_scope` is `local`
  (Claude Code reads project-scoped skills) and to `~/.claude/skills/hades-*` when `global`
  (required for Claude Desktop, which does not read project-scoped skills).

### 4.5 Package path resolution

Replace the naive `Packages/com.arcforge.hades` guess in both `FindPackageLauncherDir` and
`FindPackageSkillsDir` with `PackageInfo.FindForAssembly(typeof(...).Assembly)?.resolvedPath`,
matching `GraphBuilder.cs:858`. Keep the dev-repo fallback (`PathSandbox.ProjectRoot`) for
running from source. This fixes §1.2 and is a prerequisite for local mode to work at all on a
git-URL install.

### 4.6 Claude Desktop config

`claude_desktop_config.json` under `$HOME` has no project-local equivalent — Claude Desktop is a
single global application with one config file. When Desktop integration is on, this is a
**documented** exception: the one piece of Hades state that cannot be contained in a workspace.
See §7.

Because that write happens on every Unity start even for a user who never opens Claude Desktop,
`desktop_integration` gates it. When `false`, `UpdateClaudeDesktopConfig` is skipped entirely and
Hades writes nothing outside the workspace — provided `skills_scope` is also `local`. Those two
keys set to `local`/`false` is the fully isolated configuration, and §7 documents that pairing
explicitly.

> **Follow-up (post-Task 20).** The default is `false`, not the `true` this section originally
> specified. Verification on a real project showed that "preserving current behaviour" was the
> wrong goal for this key, on two counts. First, defaulting it on means the default installation
> still writes outside the workspace, which contradicts the headline claim of the whole change —
> §6 item 7 has to *opt out* to demonstrate isolation. Second, and decisively, the entry written
> under a local hub cannot work: Claude Desktop spawns the launcher from a directory outside the
> project, so `findProjectRoot` returns `null` and `resolveHubDir` falls through to
> `$HOME/.arcforge/hades-hub` (rung 3), while a local-scope Unity publishes `hub.json` into the
> project's own hub dir. Desktop's launcher finds no hub, spawns an orphan, and Unity never joins
> it. Defaulting on would therefore have shipped a config entry that is inert in the default
> configuration. Claude Desktop needs `hub_scope: global` today; the fix — writing
> `env: { HADES_HUB_DIR: <resolved hub dir> }` into the Desktop entry, which is rung 1 — is
> designed and recorded in Roadmap §15, along with the `X-Hades-Project` edge it must settle
> first. Preferences warns when Desktop integration is on against a local hub.

An existing `mcpServers.hades` entry is left in place when the key is turned off. Hades removing
a Desktop config entry it did not exclusively own is riskier than leaving a stale one, and the
entry is harmless: it points at a launcher that starts a hub on demand.

### 4.7 Migration

**Settings (item 6) — prompt, then import.** On first load in a project where
`.arcforge/config.local.yaml` is absent and at least one `Hades_MCP_*` EditorPrefs key is
present, prompt once:

> Hades found existing settings stored globally in this Unity install. Import them into this
> project? — **Import** / **Use defaults**

Either way the file is created, so the prompt never repeats. EditorPrefs keys are left in place
(they may still be in use by another project that has not been migrated) and are no longer read
after the file exists.

**Legacy hub dir (items 1–3) — one-time notice, no move, no delete.** Nothing in
`~/.arcforge/hades-hub/` is worth moving:

- `launcher.js` and `hub-path.json` are regenerated on every server start.
- `hub.json`, `hub.lock`, and `pending/` are **live runtime state** of a possibly-running hub
  process. Moving them would corrupt discovery for any other project currently using that hub.

Nor can Hades safely delete the directory: it cannot know whether another project on the machine
still depends on it. So local mode stops using it and shows an informational notice once:

> Hades now keeps its hub inside this project (`.arcforge/hades-hub/`). The old shared folder at
> `~/.arcforge/hades-hub/` is no longer used by this project. Other Unity projects may still be
> using it — it is safe to delete only once every project has been updated.
> — **OK** / **Open Folder**

Shown when all three hold: hub scope resolved to `local`, `~/.arcforge/hades-hub/` exists, and
the shown-flag is unset. Dismissal is recorded, then never shown again.

The shown-flag is the one piece of state that stays in **EditorPrefs**
(`Hades_LegacyHubNoticeShown`), deliberately: the fact it records is machine-global. Storing it
in `config.local.yaml` would re-show the notice once per project, for a folder that is shared
across all of them. Scope of the flag matches scope of the fact.

## 5. Files touched

New:

- `Bridge~/launcher/src/hub-dir.ts` — resolution chain (§4.1) + flat-config reader for `hub_scope`
- `Editor/Core/HadesConfig.cs` — read/write `.arcforge/config.local.yaml`, flat `key: value`
- `Editor/Core/HadesPreferences.cs` — `SettingsProvider` + `Hades/Settings…` menu item
- `Editor/Core/HadesPaths.cs` — the §4.1 hub-dir resolver, single C# source of truth shared by
  `HubClient` and `MCPClientConfig`
- `Editor/Core/LegacyHubNotice.cs` — one-time notice (§4.7)
- `Bridge~/tests/launcher/hub-dir.test.ts`
- `Tests/Editor/HadesConfigTests.cs`
- `Tests/Editor/HadesPathsTests.cs`

Modified:

- `Bridge~/launcher/src/index.ts` — use `resolveHubDir`; pass `HADES_HUB_DIR` in the hub spawn env
- `Bridge~/hub/src/index.ts` — read `HADES_HUB_DIR`, `$HOME` fallback only
- `Editor/MCP/HubClient.cs` — `HubDir` via the shared resolver instead of the `$HOME` constant
- `Editor/Core/MCPClientConfig.cs` — hub-dir resolver; `PackageInfo.resolvedPath` (§4.5);
  skills scope (§4.4); launcher copy destination
- `Editor/Core/HadesSettings.cs` — back onto `HadesConfig`, keeping the current public API so
  `MCPServer.cs:21`/`:55`/`:90` and `CharonInitializer.cs:18` need no changes
- `Editor/Asphodel/Inference/InferenceConfig.cs` — reuse the extracted flat parser (behaviour
  unchanged; it keeps reading `config.yaml`, not `config.local.yaml`)
- `.gitignore` — add `.arcforge/config.local.yaml` and `.arcforge/hades-hub/`
- `Documentation/getting-started.md`, `Documentation/troubleshooting.md`,
  `Documentation/arcforge-hades-architecture.md` (§207, §1883, §2701, §2812)

## 6. Verification

Automated:

- `hub-dir.test.ts`: env override wins; local scope yields `<projectRoot>/.arcforge/hades-hub`;
  unresolvable project root falls back to `$HOME`; `hub_scope: global` falls back to `$HOME`;
  missing and malformed config files both yield the local default.
- `bundle.test.ts` (existing) must still pass — proves the new module is inlined and the
  single-file launcher invariant holds.
- Existing `Bridge~/tests/hub/*` and `Tests/Editor/MCPServerIntegrationTests.cs` must pass
  unchanged, proving the `HadesSettings` API is preserved.
- `HadesConfigTests.cs`: round-trip every key; defaults on missing file; defaults on garbage
  values; EditorPrefs import path.

Manual, on a real git-URL UPM install (this is the path §1.2 shows is currently broken, so it
cannot be skipped):

1. Fresh Unity project, install from git URL, open. Confirm
   `<projectRoot>/.arcforge/hades-hub/launcher.js` exists and `~/.arcforge/hades-hub/` is not
   created or touched.
2. `.mcp.json` points inside the workspace. `claude` in the project dir → `/hades:status`
   reports a connected hub.
3. Confirm the hub process is a child keyed on the project-local dir (`hub.json` written there).
4. Two projects open simultaneously, both local: two hubs, each seeing exactly one instance,
   tool calls routed to the correct editor.
5. Switch one project to `hub_scope: global` via Project Settings → Hades, restart the Claude
   Code session, confirm it joins the HOME hub and still routes correctly.
6. `skills_scope: local` → skills land in `<projectRoot>/.claude/skills/`; Claude Code lists
   them. Switch to `global` → they land in `~/.claude/skills/`.
7. Defaults only — `hub_scope: local`, `skills_scope: local`, `desktop_integration: false` — with
   no config file written by hand: restart Unity and confirm, by timestamp, that nothing under
   `$HOME` is created or modified. This is the headline claim of the whole change and is the one
   check that must not be skipped. (It runs on the shipped defaults now that
   `desktop_integration` defaults to `false`; it originally required opting out first.)
8. Set `desktop_integration: true` → the Desktop config gains the `mcpServers.hades` entry with
   the absolute launcher path. Flip it back to `false` → the existing entry is left untouched
   (§4.6). With `hub_scope: local` the entry is expected *not* to connect; pair it with
   `hub_scope: global` + `skills_scope: global` for an end-to-end Desktop check.
9. Legacy notice: with `~/.arcforge/hades-hub/` present, it appears once on first local-mode boot
   and never again, including after a domain reload and after an editor restart. With the folder
   absent it never appears.

## 7. Documentation deliverables

- `getting-started.md`: new "Installation scope" section — local is the default and what that
  means; how to switch; the `HADES_HUB_DIR` override.
- Explicit statement of the two remaining non-isolatable pieces, and why: Claude Desktop's
  `claude_desktop_config.json` (§4.6), and `~/.claude/skills/` when `skills_scope: global`.
- `troubleshooting.md`: update the hub-recovery row (`architecture.md:2812` currently hardcodes
  `~/.arcforge/hades-hub/hub.json`) to cover both scopes; add manual cleanup of the legacy global
  dir (§4.7).
- `architecture.md`: correct §207's project-scoped claim to describe the control plane too;
  replace §1883's "no loader" note; update §2701's launcher-path description.

## 8. Risks

| Risk | Mitigation |
|---|---|
| One hub process per open project instead of one shared | Accepted. Hub is a small node process and auto-exits after 60s idle (`hub/src/index.ts:17`). |
| Cross-project routing for `file:`-referenced package repos breaks in local mode | Automatic `$HOME` fallback (§4.1 rung 3) plus the `HADES_HUB_DIR` override. Called out in docs. |
| Launcher and hub disagree on hub dir | Launcher passes `HADES_HUB_DIR` to the hub explicitly; the hub never re-derives (§4.1). |
| Existing users silently repointed to a new empty hub dir on upgrade | Nothing persistent lives in the old dir (§4.7), so the switch is invisible apart from a new hub process. Release notes call it out. |
| New module breaks the single-file launcher bundle | `bundle.test.ts` is an existing regression guard and is in the verification set. |

## 9. Decisions

All resolved; no open questions.

| # | Decision | Where |
|---|---|---|
| 1 | Local scope is the default, with `$HOME` as automatic fallback, plus a Unity Editor menu toggle for local vs global | §4.1, §4.3 |
| 2 | Skills: local default, global available via setting | §4.4 |
| 3 | Claude Desktop config stays global and documented, gated by `desktop_integration` — defaulting to **off**, so the shipped defaults write nothing outside the project | §4.6 |
| 4 | EditorPrefs → project-local settings is in scope | §4.2, §4.3 |
| 5 | Legacy global hub dir: one-time informational notice, no move, no delete | §4.7 |
