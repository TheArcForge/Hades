# Contributing to Hades

## Prerequisites

- Unity 6000.0+
- Node.js 20+
- Claude Code (latest)

## Setup

```bash
git clone https://github.com/TheArcForge/Hades.git
cd Hades

# Build the Bridge (Hub + Launcher)
cd Bridge~ && npm ci && npm run build && cd ..

# Install Scanner dependencies
cd Scanner~ && npm ci && cd ..
```

Open your Unity project and add the package via **Package Manager → Add package from disk**, selecting `package.json` at the repo root.

## Project Structure

| Path | Contents |
|---|---|
| `Editor/` | Unity C# code — Graph, Charon, Asphodel, MCP server |
| `Bridge~/` | Node.js Hub + Launcher (TypeScript → compiled JS) |
| `Scanner~/` | Node.js C# parser (tree-sitter based) |
| `skills/` | 22 Claude Code skills (Markdown) |
| `commands/` | 6 slash commands (Markdown) |
| `Plugin-ClaudeCode~/` | The Claude Code plugin — manifest, `.mcp.json`, and copies of the skills and commands above |
| `Legacy~/` | Retired v1.2 delivery files, kept for reference only (see its README) |
| `Documentation/` | Architecture docs, install guides, release pipeline. `Retired/` holds retired v1.2 docs (see its README) |

Directories with a tilde suffix (`Bridge~/`, `Scanner~/`, `Plugin-ClaudeCode~/`, `Legacy~/`) are invisible to Unity's asset pipeline by design.

**The repository root is deliberately not an installable Claude Code plugin.** Its manifest and
`.mcp.json` used to live here and pointed at the retired in-Editor server, so pointing Claude Code
at this checkout silently loaded the wrong generation of Hades. Install `Plugin-ClaudeCode~/`
instead; a test enforces that the root stays un-installable.

## Running Tests

All tests must pass before submitting a PR.

```bash
# Bridge tests
cd Bridge~ && npm test

# Scanner tests
cd Scanner~ && npm test
```

For Unity tests: open **Window → General → Test Runner** and run both EditMode and PlayMode suites.

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
- **Don't add new npm runtime dependencies to Bridge** — it is zero-dependencies by design

## Contributing Skills

Skills live in `skills/<name>/SKILL.md`. Each file must have YAML frontmatter with at minimum a `description` field. See any existing skill for the expected format.

## Reporting Bugs

Open a [GitHub Issue](https://github.com/TheArcForge/Hades/issues) and include:

- Unity version
- Node.js version
- OS and version
- Steps to reproduce
