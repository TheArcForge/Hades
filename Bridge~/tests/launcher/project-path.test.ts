import { describe, it, expect, afterEach } from "vitest";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { resolveProjectPath } from "../../launcher/src/project-path.js";

describe("resolveProjectPath", () => {
  const made: string[] = [];

  function makeProject(): string {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "hades-unity-"));
    made.push(root);
    fs.mkdirSync(path.join(root, "ProjectSettings"));
    fs.writeFileSync(
      path.join(root, "ProjectSettings", "ProjectVersion.txt"),
      "m_EditorVersion: 6000.0.0f1\n"
    );
    return root;
  }

  afterEach(() => {
    for (const d of made.splice(0)) fs.rmSync(d, { recursive: true, force: true });
  });

  it("returns the project root when cwd IS the project root", () => {
    const root = makeProject();
    expect(resolveProjectPath(root)).toBe(root);
  });

  it("walks up from a subdirectory to the project root", () => {
    const root = makeProject();
    const sub = path.join(root, "Assets", "Scripts", "Player");
    fs.mkdirSync(sub, { recursive: true });
    expect(resolveProjectPath(sub)).toBe(root);
  });

  it("falls back to cwd when no Unity project is found (e.g. '/')", () => {
    expect(resolveProjectPath("/")).toBe("/");
  });
});
