# Contributing to Hades

## Prerequisites

- .NET 10 SDK
- Xcode + Swift toolchain (macOS)
- Unity 6000.x (only for the Unity plugin)
- Python 3 (only for the e2e regression suite)
- Claude Code (latest)

## Setup

```bash
git clone https://github.com/TheArcForge/Hades.git
cd Hades

# Build the .NET core
dotnet build App~

# Build the macOS app (builds the Swift shell and embeds the published .NET server)
Shell~/HadesApp/scripts/build-app.sh
```

The Unity plugin is optional (only needed for live-Editor features) and isn't added via Package Manager anymore — install it from the running app via **Projects → Install Plugin**.

## Project Structure

| Path | Contents |
|---|---|
| `Shell~/` | SwiftUI macOS app (`HadesApp`) + Swift packages `HadesControl`, `HadesSupervision` |
| `App~/` | .NET 10 core — `Hades.Server`, `Hades.Core`, `Hades.Contract`, `Hades.Cli` (tests under `App~/tests`) |
| `Plugin~/` | Unity Editor plugin (C#, v1.4.0) — optional, dials out to the app over a local socket |
| `skills/` | 22 Claude Code skills (Markdown) |
| `commands/` | 6 slash commands (Markdown) |
| `Plugin-ClaudeCode~/` | The Claude Code plugin — manifest, `.mcp.json`, and copies of the skills and commands above |
| `Legacy~/` | Retired v1.2 delivery files, kept for reference only (see its README) |
| `Documentation/` | Architecture docs, install guides, release pipeline. `Retired/` holds retired v1.2 docs (see its README) |

Directories with a tilde suffix (`Shell~/`, `App~/`, `Plugin~/`, `Plugin-ClaudeCode~/`, `Legacy~/`) are invisible to Unity's asset pipeline by design.

**The repository root is deliberately not an installable Claude Code plugin.** Its manifest and
`.mcp.json` used to live here and pointed at the retired in-Editor server, so pointing Claude Code
at this checkout silently loaded the wrong generation of Hades. Install `Plugin-ClaudeCode~/`
instead; a test enforces that the root stays un-installable.

## Running Tests

All tests must pass before submitting a PR.

```bash
# .NET core (~1863 tests; isolate with HADES_HOME=$(mktemp -d))
dotnet test App~

# Swift — run in each of Shell~/HadesControl, Shell~/HadesSupervision, Shell~/HadesApp (70 / 14 / 211 tests)
swift test

# Unity plugin EditMode, batchmode (384 tests)
scripts/regression/run-plugin-editmode.sh

# End-to-end (25 cases; needs the app running with a project registered)
python3 scripts/regression/hades_suite.py --url http://127.0.0.1:7823/mcp
```

See `Documentation/RegressionCoverage.md` for the per-issue regression coverage map.

## How to Contribute

1. Fork the repo and create a branch off `main`
2. Keep each PR focused on one concern
3. Follow the existing code style in the file you're editing
4. Add tests for new functionality
5. Update `Documentation/` if you're changing user-facing behavior
6. Open a PR against `main`

## What NOT to Do

- **Never edit the plugin repo** (`TheArcForge/hades-plugin`) directly — it is auto-synced from this repo and any changes will be overwritten
- **Never generate `.meta` files or GUIDs** — Unity manages these automatically
- **Never commit `node_modules/`**

## Contributing Skills

Skills live in `skills/<name>/SKILL.md`. Each file must have YAML frontmatter with at minimum a `description` field. See any existing skill for the expected format.

## Reporting Bugs

Open a [GitHub Issue](https://github.com/TheArcForge/Hades/issues) and include:

- Unity version
- Hades app version (`2.0.0`, from `hades_status`)
- OS and version
- Steps to reproduce
