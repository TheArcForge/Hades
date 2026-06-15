/**
 * Resolves the Unity project root the launcher belongs to by walking UP from `cwd` until it
 * finds a Unity project (marked by `ProjectSettings/ProjectVersion.txt`). This fixes the case
 * where Claude Code spawned the launcher in a subdirectory of the project, so `process.cwd()`
 * alone wouldn't exact-match the registered project root.
 *
 * Falls back to `cwd` when no project is found (e.g. cwd is "/" or outside any project) — in
 * that case the hub's single-instance fallback routes the call when only one Unity is open.
 */
export declare function resolveProjectPath(cwd: string): string;
