import { describe, it, expect, beforeEach } from "vitest";
import { Registry } from "../../hub/src/registry.js";

describe("Registry.findByProjectPath", () => {
  let registry: Registry;

  beforeEach(() => {
    registry = new Registry();
    registry.register({
      projectName: "FooGame",
      projectPath: "/Users/mike/Projects/MyRepo/FooGame",
      port: 12345,
      pid: 1,
    });
    registry.register({
      projectName: "BarGame",
      projectPath: "/Users/mike/Projects/BarGame",
      port: 12346,
      pid: 2,
    });
  });

  it("exact match returns the instance", () => {
    const result = registry.findByProjectPath(
      "/Users/mike/Projects/MyRepo/FooGame"
    );
    expect(result?.projectName).toBe("FooGame");
  });

  it("parent match: CWD is parent of project path", () => {
    const result = registry.findByProjectPath("/Users/mike/Projects/MyRepo");
    expect(result?.projectName).toBe("FooGame");
  });

  it("parent match: picks longest (most specific) match", () => {
    registry.register({
      projectName: "NestedGame",
      projectPath: "/Users/mike/Projects/MyRepo/Sub/NestedGame",
      port: 12347,
      pid: 3,
    });
    const result = registry.findByProjectPath(
      "/Users/mike/Projects/MyRepo/Sub"
    );
    expect(result?.projectName).toBe("NestedGame");
  });

  it("child match: CWD is child of project path", () => {
    const result = registry.findByProjectPath(
      "/Users/mike/Projects/BarGame/Packages/com.foo.bar"
    );
    expect(result?.projectName).toBe("BarGame");
  });

  it("manifest match: CWD matches a file: package reference", () => {
    registry.register({
      projectName: "AlphaProject",
      projectPath: "/Users/mike/Documents/alpha",
      port: 12348,
      pid: 4,
      manifestPackages: ["/Users/mike/Projects/Hades"],
    });
    const result = registry.findByProjectPath("/Users/mike/Projects/Hades");
    expect(result?.projectName).toBe("AlphaProject");
  });

  it("no match returns null", () => {
    const result = registry.findByProjectPath("/completely/different/path");
    expect(result).toBeNull();
  });

  it("skips stale instances", () => {
    registry.markStale("/Users/mike/Projects/MyRepo/FooGame");
    const result = registry.findByProjectPath(
      "/Users/mike/Projects/MyRepo/FooGame"
    );
    expect(result).toBeNull();
  });

  it("includes transient instances (they are temporarily unavailable, not gone)", () => {
    registry.deregister({
      projectPath: "/Users/mike/Projects/MyRepo/FooGame",
      transient: true,
    });
    const result = registry.findByProjectPath(
      "/Users/mike/Projects/MyRepo/FooGame"
    );
    expect(result?.projectName).toBe("FooGame");
    expect(result?.status).toBe("transient");
  });

  // The two beforeEach instances mean the single-instance fallback never fires here, so
  // "no match returns null" above still holds with 2+ instances.
  it("does NOT single-instance-fallback when 2+ instances and no match", () => {
    expect(registry.findByProjectPath("/")).toBeNull();
  });
});

describe("Registry single-instance fallback", () => {
  it("routes to the only instance when nothing matches (e.g. launcher cwd is '/')", () => {
    const registry = new Registry();
    registry.register({
      projectName: "Only",
      projectPath: "/Users/mike/Projects/Only",
      port: 1,
      pid: 1,
    });
    expect(registry.findByProjectPath("/").projectName).toBe("Only");
    expect(registry.findByProjectPath("/some/unrelated/dir").projectName).toBe("Only");
  });

  it("does not fall back to a stale-only instance", () => {
    const registry = new Registry();
    registry.register({
      projectName: "Stale",
      projectPath: "/Users/mike/Projects/Stale",
      port: 1,
      pid: 1,
    });
    registry.markStale("/Users/mike/Projects/Stale");
    expect(registry.findByProjectPath("/")).toBeNull();
  });
});

describe("Registry path canonicalization", () => {
  it("resolves symlinks so a symlinked query matches the real registered path", async () => {
    const fs = await import("node:fs");
    const os = await import("node:os");
    const path = await import("node:path");

    const real = fs.mkdtempSync(path.join(os.tmpdir(), "hades-real-"));
    const link = path.join(os.tmpdir(), `hades-link-${Date.now()}`);
    fs.symlinkSync(real, link);
    try {
      const registry = new Registry();
      registry.register({ projectName: "Sym", projectPath: real, port: 1, pid: 1 });
      // Query via the symlink — should canonicalize to `real` and match.
      expect(registry.findByProjectPath(link)?.projectName).toBe("Sym");
    } finally {
      fs.unlinkSync(link);
      fs.rmSync(real, { recursive: true, force: true });
    }
  });
});
