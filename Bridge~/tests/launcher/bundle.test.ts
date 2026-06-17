import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";

// Regression guard for the Update-1 install bug: the launcher is copied as a SINGLE file
// (index.js -> launcher.js) to a stable per-machine location by EnsureStableLauncher
// (Editor/Core/MCPClientConfig.cs). For that single-file copy to work, the built launcher
// must be a self-contained bundle with NO relative sibling imports — otherwise the copied
// file dies at startup with ERR_MODULE_NOT_FOUND because project-path.js / spawn-lock.js
// aren't alongside it. The launcher build bundles with esbuild precisely to hold this invariant.
const DIST = path.resolve(__dirname, "..", "..", "launcher", "dist", "index.js");

describe("launcher dist is a self-contained bundle", () => {
  it("dist/index.js exists (build ran)", () => {
    expect(fs.existsSync(DIST)).toBe(true);
  });

  it("has no relative imports (would break the single-file stable-launcher copy)", () => {
    const code = fs.readFileSync(DIST, "utf8");
    const relImports = [...code.matchAll(/\bfrom\s+["']\.[^"']*["']/g)].map((m) => m[0]);
    expect(
      relImports,
      `launcher bundle must inline all local modules; found relative imports: ${relImports.join(", ")}`
    ).toEqual([]);
  });

  it("dist holds only the single bundled entry", () => {
    const jsFiles = fs
      .readdirSync(path.dirname(DIST))
      .filter((f) => f.endsWith(".js"));
    expect(jsFiles).toEqual(["index.js"]);
  });
});
