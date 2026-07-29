import { describe, it, expect } from "vitest";
import path from "node:path";
import {
  resolveHubDir,
  readHubScope,
  ENV_HUB_DIR,
  CONFIG_FILE_NAME,
} from "../../launcher/src/hub-dir.js";

const HOME = "/Users/tester";
const PROJECT = "/Work/MyGame";
const GLOBAL = path.join(HOME, ".arcforge", "hades-hub");
const LOCAL = path.join(PROJECT, ".arcforge", "hades-hub");

/** A readFile stub: maps absolute path -> contents. Anything else reads as missing. */
function files(map: Record<string, string> = {}) {
  return (p: string) => (p in map ? map[p] : null);
}

const CONFIG_PATH = path.join(PROJECT, ".arcforge", CONFIG_FILE_NAME);

describe("readHubScope", () => {
  const arcforge = path.join(PROJECT, ".arcforge");

  it("defaults to local when the config file is missing", () => {
    expect(readHubScope(arcforge, files())).toBe("local");
  });

  it("defaults to local when the key is absent", () => {
    expect(readHubScope(arcforge, files({ [CONFIG_PATH]: "mcp_port: 51234\n" }))).toBe("local");
  });

  it("reads global", () => {
    expect(readHubScope(arcforge, files({ [CONFIG_PATH]: "hub_scope: global\n" }))).toBe("global");
  });

  it("reads local explicitly", () => {
    expect(readHubScope(arcforge, files({ [CONFIG_PATH]: "hub_scope: local\n" }))).toBe("local");
  });

  it("is case-insensitive", () => {
    expect(readHubScope(arcforge, files({ [CONFIG_PATH]: "hub_scope: GLOBAL\n" }))).toBe("global");
  });

  it("ignores comment lines", () => {
    expect(
      readHubScope(arcforge, files({ [CONFIG_PATH]: "# hub_scope: global\n" }))
    ).toBe("local");
  });

  it("defaults to local on an unrecognised value", () => {
    expect(readHubScope(arcforge, files({ [CONFIG_PATH]: "hub_scope: sideways\n" }))).toBe("local");
  });

  it("tolerates CRLF line endings", () => {
    expect(
      readHubScope(arcforge, files({ [CONFIG_PATH]: "mcp_port: 1\r\nhub_scope: global\r\n" }))
    ).toBe("global");
  });

  it("ignores lines without a colon", () => {
    expect(readHubScope(arcforge, files({ [CONFIG_PATH]: "garbage\n" }))).toBe("local");
  });
});

describe("resolveHubDir", () => {
  it("prefers the env override over everything", () => {
    const dir = resolveHubDir({
      env: { HOME, [ENV_HUB_DIR]: "/custom/hub" },
      projectRoot: PROJECT,
      readFile: files(),
    });
    expect(dir).toBe("/custom/hub");
  });

  it("trims the env override", () => {
    const dir = resolveHubDir({
      env: { HOME, [ENV_HUB_DIR]: "  /custom/hub  " },
      projectRoot: PROJECT,
      readFile: files(),
    });
    expect(dir).toBe("/custom/hub");
  });

  it("ignores a whitespace-only env override", () => {
    const dir = resolveHubDir({
      env: { HOME, [ENV_HUB_DIR]: "   " },
      projectRoot: PROJECT,
      readFile: files(),
    });
    expect(dir).toBe(LOCAL);
  });

  it("defaults to the project-local hub dir", () => {
    const dir = resolveHubDir({ env: { HOME }, projectRoot: PROJECT, readFile: files() });
    expect(dir).toBe(LOCAL);
  });

  it("uses the global dir when hub_scope is global", () => {
    const dir = resolveHubDir({
      env: { HOME },
      projectRoot: PROJECT,
      readFile: files({ [CONFIG_PATH]: "hub_scope: global\n" }),
    });
    expect(dir).toBe(GLOBAL);
  });

  it("falls back to the global dir when no project root was found", () => {
    const dir = resolveHubDir({ env: { HOME }, projectRoot: null, readFile: files() });
    expect(dir).toBe(GLOBAL);
  });

  it("uses USERPROFILE when HOME is absent", () => {
    const dir = resolveHubDir({
      env: { USERPROFILE: HOME },
      projectRoot: null,
      readFile: files(),
    });
    expect(dir).toBe(GLOBAL);
  });

  it("falls back to the local dir on a malformed config file", () => {
    const dir = resolveHubDir({
      env: { HOME },
      projectRoot: PROJECT,
      readFile: files({ [CONFIG_PATH]: "  not: [valid\n" }),
    });
    expect(dir).toBe(LOCAL);
  });
});
