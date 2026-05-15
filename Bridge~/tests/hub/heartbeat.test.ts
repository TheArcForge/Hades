import { describe, it, expect, beforeEach, vi, afterEach } from "vitest";
import { Registry } from "../../hub/src/registry.js";
import {
  checkStaleInstances,
  HEARTBEAT_STALE_MS,
  STALE_PURGE_MS,
} from "../../hub/src/heartbeat.js";

describe("checkStaleInstances", () => {
  let registry: Registry;

  beforeEach(() => {
    registry = new Registry();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("marks instance stale after missed heartbeats when probe fails", async () => {
    registry.register({
      projectName: "Foo",
      projectPath: "/path/foo",
      port: 12345,
      pid: 1,
    });

    // Advance past stale threshold
    vi.advanceTimersByTime(HEARTBEAT_STALE_MS + 1000);

    const probeInstance = vi.fn().mockResolvedValue(false);
    await checkStaleInstances(registry, probeInstance);

    expect(registry.get("/path/foo")!.status).toBe("stale");
  });

  it("keeps instance healthy when probe succeeds despite missed heartbeats", async () => {
    registry.register({
      projectName: "Foo",
      projectPath: "/path/foo",
      port: 12345,
      pid: 1,
    });

    vi.advanceTimersByTime(HEARTBEAT_STALE_MS + 1000);

    const probeInstance = vi.fn().mockResolvedValue(true);
    await checkStaleInstances(registry, probeInstance);

    expect(registry.get("/path/foo")!.status).toBe("healthy");
  });

  it("does not probe instances within heartbeat window", async () => {
    registry.register({
      projectName: "Foo",
      projectPath: "/path/foo",
      port: 12345,
      pid: 1,
    });

    // Don't advance time — heartbeat is fresh
    const probeInstance = vi.fn();
    await checkStaleInstances(registry, probeInstance);

    expect(probeInstance).not.toHaveBeenCalled();
  });

  it("transitions transient to stale after transient timeout", async () => {
    registry.register({
      projectName: "Foo",
      projectPath: "/path/foo",
      port: 12345,
      pid: 1,
    });
    registry.deregister({ projectPath: "/path/foo", transient: true });

    // Advance past transient timeout (30s)
    vi.advanceTimersByTime(31_000);

    const probeInstance = vi.fn().mockResolvedValue(false);
    await checkStaleInstances(registry, probeInstance);

    expect(registry.get("/path/foo")!.status).toBe("stale");
  });

  it("purges stale instances after purge timeout", async () => {
    registry.register({
      projectName: "Foo",
      projectPath: "/path/foo",
      port: 12345,
      pid: 1,
    });

    // Make stale first
    vi.advanceTimersByTime(HEARTBEAT_STALE_MS + 1000);
    const probeInstance = vi.fn().mockResolvedValue(false);
    await checkStaleInstances(registry, probeInstance);
    expect(registry.get("/path/foo")!.status).toBe("stale");

    // Advance past purge timeout
    vi.advanceTimersByTime(STALE_PURGE_MS + 1000);
    await checkStaleInstances(registry, probeInstance);

    expect(registry.get("/path/foo")).toBeNull();
  });
});
