# Contributing to Hades

## Prerequisites

- .NET 10 SDK
- Xcode + Swift toolchain (macOS)
- Unity 6000.x (only for the Unity plugin)
- Python 3 (only for the e2e regression suite)
- Claude Code (latest)

On **Windows**, additionally:

- **Developer Mode on** — Settings → System → For developers. Six tests create directory symlinks,
  which needs `SeCreateSymbolicLinkPrivilege`; without it they fail with *"A required privilege is
  not held by the client"*. This is a prerequisite for the suite, not a product requirement, and CI
  does not need it because GitHub's `windows-latest` runners already run elevated.
- **WiX 7** (`dotnet tool install --global wix`), only if you are building the MSI.

## Setup

```bash
git clone https://github.com/TheArcForge/Hades.git
cd Hades

# Build the .NET core
dotnet build Core

# Build the macOS app (builds the Swift shell and embeds the published .NET server)
Mac/HadesApp/scripts/build-app.sh
```

The Unity plugin is optional (only needed for live-Editor features) and isn't added via Package Manager anymore — install it from the running app via **Projects → Install Plugin**.

## Project Structure

| Path | Contents |
|---|---|
| `Mac/` | SwiftUI macOS app (`HadesApp`) + Swift packages `HadesControl`, `HadesSupervision` |
| `Core/` | .NET 10 core — `Hades.Server`, `Hades.Core`, `Hades.Contract`, `Hades.Cli` (tests under `Core/tests`) |
| `UnityPlugin/` | Unity Editor plugin (C#, v1.4.0) — optional, dials out to the app over a local socket |
| `ClaudeCodePlugin/` | The Claude Code plugin — manifest, `.mcp.json`, 22 skills, 6 commands. Source of truth for the `hades-plugin` repo, which is generated from it |
| `Documentation/` | Architecture docs, install guides, release pipeline. `Retired/` holds retired v1.2 docs (see its README) |

These directories used to carry a `~` suffix, which hides them from Unity's asset pipeline. That
mattered while the repo was itself a Unity package; it no longer is — Hades is a macOS app, and the
Unity plugin reaches a project by being installed into it, not by the project consuming this repo.
The suffixes were dropped accordingly, and with them the last `.meta` files: **this repository now
contains none at all.** The Unity plugin ships source only — `PluginInstaller` writes no `.meta`
(it says so in its own doc comment), and Unity generates them in the user's project on import,
which is where they belong.

**The repository root is deliberately not an installable Claude Code plugin.** Its manifest and
`.mcp.json` used to live here and pointed at the retired in-Editor server, so pointing Claude Code
at this checkout silently loaded the wrong generation of Hades. Install `ClaudeCodePlugin/`
instead; a test enforces that the root stays un-installable.

## Running Tests

All tests must pass before submitting a PR.

```bash
# .NET core (1,961 tests on macOS; isolate with HADES_HOME=$(mktemp -d))
dotnet test Core

# Swift — run in each of Mac/HadesControl, Mac/HadesSupervision, Mac/HadesApp (81 / 14 / 213 tests)
swift test

# Unity plugin EditMode, batchmode (384 tests)
scripts/regression/run-plugin-editmode.sh

# End-to-end (25 cases; needs the app running with a project registered)
python3 scripts/regression/hades_suite.py --url http://127.0.0.1:7823/mcp
```

**Platform-specific tests are filtered, not skipped.** xUnit 2.9.3 has no dynamic skip, so tests that
can only run on one OS carry a `Platform` trait and each platform filters out what it cannot run
(see `Core/tests/Hades.Core.Tests/PlatformTraits.cs`). A filtered test is not reported at all, which
beats the early-return convention xUnit would report as *passed*:

```powershell
# Windows — .NET core plus the Windows shell and supervision suites (2,222 together)
dotnet test Core --filter "Platform!=Unix"
dotnet test Windows\HadesWindows.slnx --filter "Platform!=Unix"
```

```bash
# macOS — the mirror image
dotnet test Core --filter "Platform!=Windows"
```

See `Documentation/RegressionCoverage.md` for the per-issue regression coverage map.

## How to Contribute

1. Fork the repo and create a branch off `main`
2. Keep each PR focused on one concern
3. Follow the existing code style in the file you're editing
4. Add tests for new functionality
5. Update `Documentation/` if you're changing user-facing behavior
6. Open a PR against `main`

Merged pull requests are credited by GitHub handle in the `CHANGELOG.md` entry for the release they
ship in, and in the merge commit. You don't need to add this yourself.

## What NOT to Do

- **Never edit the plugin repo** (`TheArcForge/hades-plugin`) directly — it is auto-synced from this repo and any changes will be overwritten
- **Never hand-write `.meta` files or invent GUIDs, and do not commit `.meta` files at all.** This
  repository ships none. The app writes `UnityPlugin/Assets/Hades` into a user's project as source,
  and Unity generates the `.meta` files there on import — the only place they should exist.
  `PluginInstaller` states the same rule in its own doc comment.
- **Never commit `node_modules/`**

## Contributing Skills

Skills live in `skills/<name>/SKILL.md`. Each file must have YAML frontmatter with at minimum a `description` field. See any existing skill for the expected format.

## Reporting Bugs

Open a [GitHub Issue](https://github.com/TheArcForge/Hades/issues) and include:

- Unity version
- Hades app version (`2.0.0`, from `hades_status`)
- OS and version
- Steps to reproduce
