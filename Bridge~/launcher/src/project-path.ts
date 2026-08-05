import fs from "node:fs";
import path from "node:path";

/**
 * Walks UP from `cwd` looking for a Unity project (marked by
 * `ProjectSettings/ProjectVersion.txt`). Returns null when none is found — e.g. cwd is "/" or
 * sits outside any project.
 *
 * The upward walk fixes the case where Claude Code spawned the launcher in a subdirectory of the
 * project, so `process.cwd()` alone wouldn't exact-match the registered project root.
 *
 * Callers that need to distinguish "found a project" from "gave up" must use this rather than
 * resolveProjectPath: the hub-dir resolution chain only uses the project-local hub when a real
 * project root was found.
 */
export function findProjectRoot(cwd: string): string | null {
  let dir = cwd;
  for (let i = 0; i < 40; i++) {
    if (fs.existsSync(path.join(dir, "ProjectSettings", "ProjectVersion.txt"))) {
      return dir;
    }
    const parent = path.dirname(dir);
    if (parent === dir) break; // reached the filesystem root
    dir = parent;
  }
  return null;
}

/**
 * Resolves the Unity project root the launcher belongs to, falling back to `cwd` when no project
 * is found — in that case the hub's single-instance fallback routes the call when only one Unity
 * is open. This is the value sent as the X-Hades-Project header.
 */
export function resolveProjectPath(cwd: string): string {
  return findProjectRoot(cwd) ?? cwd;
}
