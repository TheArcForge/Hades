import fs from "node:fs";
import path from "node:path";

export const ENV_HUB_DIR = "HADES_HUB_DIR";
export const CONFIG_FILE_NAME = "config.local.yaml";

const ARCFORGE_DIR_NAME = ".arcforge";
const HUB_DIR_NAME = "hades-hub";
const HUB_SCOPE_KEY = "hub_scope";

export type HubScope = "local" | "global";
export type ReadFile = (filePath: string) => string | null;

export interface ResolveHubDirOptions {
  env: NodeJS.ProcessEnv;
  /** Project root, or null when no Unity project was found. See findProjectRoot. */
  projectRoot: string | null;
  readFile: ReadFile;
}

/** Reads a file, returning null for anything unreadable — missing, permission denied, a dir. */
export function defaultReadFile(filePath: string): string | null {
  try {
    return fs.readFileSync(filePath, "utf8");
  } catch {
    return null;
  }
}

/**
 * Reads just `hub_scope` out of .arcforge/config.local.yaml.
 *
 * Deliberately a hand-rolled reader for a single key rather than a YAML dependency: the launcher
 * ships as a zero-dependency esbuild bundle. Mirrors HadesConfig.Parse on the C# side — flat
 * `key: value`, blank/comment/colonless lines skipped. Anything unexpected yields "local", which
 * is the documented default.
 */
export function readHubScope(arcforgeDir: string, readFile: ReadFile): HubScope {
  const raw = readFile(path.join(arcforgeDir, CONFIG_FILE_NAME));
  if (raw === null) return "local";

  let value: string | null = null;

  for (const line of raw.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith("#")) continue;

    const colonIdx = trimmed.indexOf(":");
    if (colonIdx <= 0) continue;

    if (trimmed.slice(0, colonIdx).trim() !== HUB_SCOPE_KEY) continue;

    // Last occurrence of a duplicated key wins — must match HadesConfig.Parse on the C# side.
    value = trimmed.slice(colonIdx + 1).trim();
  }

  return value?.toLowerCase() === "global" ? "global" : "local";
}

/**
 * Resolves the hub rendezvous directory — where hub.json (port + pid) is published.
 *
 * Must stay rung-for-rung identical to HadesPaths.ResolveHubDir in the Unity assembly:
 *   1. HADES_HUB_DIR env var
 *   2. <projectRoot>/.arcforge/hades-hub   when hub_scope is local
 *   3. $HOME/.arcforge/hades-hub           otherwise, and when projectRoot is unknown
 *
 * Rung 3 is load-bearing, not legacy dead weight: a launcher whose cwd is a `file:`-referenced
 * package repo OUTSIDE the Unity project cannot see the project's hub dir, and must reach the
 * shared hub so Registry.findByProjectPath's manifestPackages match still routes it.
 */
export function resolveHubDir(opts: ResolveHubDirOptions): string {
  const override = opts.env[ENV_HUB_DIR];
  if (override && override.trim()) return override.trim();

  const home = opts.env.HOME ?? opts.env.USERPROFILE ?? "";
  const globalDir = path.join(home, ARCFORGE_DIR_NAME, HUB_DIR_NAME);

  if (!opts.projectRoot) return globalDir;

  const arcforgeDir = path.join(opts.projectRoot, ARCFORGE_DIR_NAME);
  if (readHubScope(arcforgeDir, opts.readFile) === "global") return globalDir;

  return path.join(arcforgeDir, HUB_DIR_NAME);
}
