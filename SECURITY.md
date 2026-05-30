# Security Policy

## Supported Versions

Only the latest release is supported with security updates.

| Version | Supported |
|---------|-----------|
| 0.9.x   | Yes       |
| < 0.9   | No        |

## Reporting a Vulnerability

**Preferred:** Open a [GitHub Security Advisory](https://github.com/TheArcForge/Hades/security/advisories/new) on this repository. This keeps the report private until a fix is available.

**Alternative:** Contact the project maintainers directly via GitHub.

### What to expect

- Acknowledgment within 72 hours
- Fix timeline depends on severity — critical issues are prioritized

### What qualifies

- Vulnerabilities in the MCP server, Hub, Launcher, or Scanner
- Path traversal or unauthorized file access via MCP tools
- Credential or sensitive data exposure
- Process injection or code execution via crafted tool inputs

### What doesn't qualify

- Issues in Unity Editor itself (report to Unity)
- Issues in Claude Code itself (report to Anthropic)
- Theoretical attacks requiring prior local machine access

## Architecture Note

Hades runs entirely locally. All MCP communication is over localhost (`127.0.0.1`). There are no cloud services, no telemetry, and no remote connections. The Hub binds to localhost only and is not accessible from other machines.
