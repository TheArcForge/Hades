# Security Policy

## Supported Versions

Only the latest release is supported with security updates.

| Version     | Supported |
|-------------|-----------|
| 2.0.0       | Yes       |
| Earlier     | No        |

## Reporting a Vulnerability

**Preferred:** Open a [GitHub Security Advisory](https://github.com/TheArcForge/Hades/security/advisories/new) on this repository. This keeps the report private until a fix is available.

**Alternative:** Contact the project maintainers directly via GitHub.

### What to expect

- Acknowledgment within 72 hours
- Fix timeline depends on severity — critical issues are prioritized

### What qualifies

- Vulnerabilities in the .NET core, the app, or the control API
- Path traversal or unauthorized file access via MCP tools
- Credential or sensitive data exposure
- Process injection or code execution via crafted tool inputs

### What doesn't qualify

- Issues in Unity Editor itself (report to Unity)
- Issues in Claude Code itself (report to Anthropic)
- Theoretical attacks requiring prior local machine access

## Architecture Note

Hades runs entirely locally. All MCP communication is over localhost (`127.0.0.1`). There are no cloud services, no telemetry, and no remote connections. The app's MCP server binds to `127.0.0.1:7823` (localhost only) and is not accessible from other machines.
