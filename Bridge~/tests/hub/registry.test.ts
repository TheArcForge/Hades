import { describe, it, expect, beforeEach } from "vitest";
import { Registry } from "../../hub/src/registry.js";

describe("Registry", () => {
  let registry: Registry;

  beforeEach(() => {
    registry = new Registry();
  });

  describe("register", () => {
    it("adds a new instance as healthy", () => {
      registry.register({
        projectName: "FooGame",
        projectPath: "/path/to/FooGame",
        port: 12345,
        pid: 9876,
      });

      const instance = registry.get("/path/to/FooGame");
      expect(instance).not.toBeNull();
      expect(instance!.status).toBe("healthy");
      expect(instance!.port).toBe(12345);
    });

    it("updates existing instance on re-register", () => {
      registry.register({
        projectName: "FooGame",
        projectPath: "/path/to/FooGame",
        port: 12345,
        pid: 9876,
      });
      registry.register({
        projectName: "FooGame",
        projectPath: "/path/to/FooGame",
        port: 12346,
        pid: 9876,
      });

      const instance = registry.get("/path/to/FooGame");
      expect(instance!.port).toBe(12346);
      expect(instance!.status).toBe("healthy");
    });

    it("transitions transient to healthy on re-register", () => {
      registry.register({
        projectName: "FooGame",
        projectPath: "/path/to/FooGame",
        port: 12345,
        pid: 9876,
      });
      registry.deregister({ projectPath: "/path/to/FooGame", transient: true });
      expect(registry.get("/path/to/FooGame")!.status).toBe("transient");

      registry.register({
        projectName: "FooGame",
        projectPath: "/path/to/FooGame",
        port: 12345,
        pid: 9876,
      });
      expect(registry.get("/path/to/FooGame")!.status).toBe("healthy");
    });
  });

  describe("deregister", () => {
    it("marks instance transient when transient=true", () => {
      registry.register({
        projectName: "FooGame",
        projectPath: "/path/to/FooGame",
        port: 12345,
        pid: 9876,
      });
      registry.deregister({ projectPath: "/path/to/FooGame", transient: true });

      const instance = registry.get("/path/to/FooGame");
      expect(instance!.status).toBe("transient");
      expect(instance!.transientSince).not.toBeNull();
    });

    it("removes instance when transient=false", () => {
      registry.register({
        projectName: "FooGame",
        projectPath: "/path/to/FooGame",
        port: 12345,
        pid: 9876,
      });
      registry.deregister({
        projectPath: "/path/to/FooGame",
        transient: false,
      });

      expect(registry.get("/path/to/FooGame")).toBeNull();
    });
  });

  describe("heartbeat", () => {
    it("updates lastHeartbeat timestamp", () => {
      registry.register({
        projectName: "FooGame",
        projectPath: "/path/to/FooGame",
        port: 12345,
        pid: 9876,
      });
      const before = registry.get("/path/to/FooGame")!.lastHeartbeat;

      registry.heartbeat({
        projectPath: "/path/to/FooGame",
        port: 12345,
        pid: 9876,
      });
      const after = registry.get("/path/to/FooGame")!.lastHeartbeat;

      expect(after).toBeGreaterThanOrEqual(before);
    });

    it("updates port if changed", () => {
      registry.register({
        projectName: "FooGame",
        projectPath: "/path/to/FooGame",
        port: 12345,
        pid: 9876,
      });
      registry.heartbeat({
        projectPath: "/path/to/FooGame",
        port: 12346,
        pid: 9876,
      });

      expect(registry.get("/path/to/FooGame")!.port).toBe(12346);
    });

    it("ignores heartbeat for unknown instance", () => {
      const result = registry.heartbeat({
        projectPath: "/unknown/path",
        port: 12345,
        pid: 9876,
      });
      expect(result).toBe(false);
    });
  });

  describe("getAll", () => {
    it("returns all registered instances", () => {
      registry.register({
        projectName: "FooGame",
        projectPath: "/path/to/Foo",
        port: 12345,
        pid: 1,
      });
      registry.register({
        projectName: "BarGame",
        projectPath: "/path/to/Bar",
        port: 12346,
        pid: 2,
      });

      expect(registry.getAll()).toHaveLength(2);
    });
  });

  describe("launcher tracking", () => {
    it("tracks launcher connections", () => {
      expect(registry.launcherCount).toBe(0);
      registry.launcherConnect();
      expect(registry.launcherCount).toBe(1);
      registry.launcherConnect();
      expect(registry.launcherCount).toBe(2);
      registry.launcherDisconnect();
      expect(registry.launcherCount).toBe(1);
    });

    it("does not go below zero", () => {
      registry.launcherDisconnect();
      expect(registry.launcherCount).toBe(0);
    });
  });

  describe("isEmpty", () => {
    it("returns true when no instances and no launchers", () => {
      expect(registry.isEmpty()).toBe(true);
    });

    it("returns false when instance registered", () => {
      registry.register({
        projectName: "Foo",
        projectPath: "/path",
        port: 1,
        pid: 1,
      });
      expect(registry.isEmpty()).toBe(false);
    });

    it("returns false when launcher connected", () => {
      registry.launcherConnect();
      expect(registry.isEmpty()).toBe(false);
    });
  });
});
