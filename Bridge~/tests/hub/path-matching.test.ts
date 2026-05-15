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
});
