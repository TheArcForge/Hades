import { describe, it, expect } from "vitest";
import { Registry } from "../../hub/src/registry.js";

const AUTO_EXIT_MS = 60_000;

describe("Registry.isIdle (auto-exit liveness)", () => {
  it("is NOT idle while a Unity instance is registered", () => {
    const r = new Registry();
    r.register({ projectName: "U", projectPath: "/u", port: 1, pid: 1 });
    // even far in the future, a registered instance keeps it alive
    expect(r.isIdle(AUTO_EXIT_MS, Date.now() + 10_000_000)).toBe(false);
  });

  it("is NOT idle while there is recent launcher activity (no instances)", () => {
    const r = new Registry();
    r.noteLauncherActivity();
    expect(r.isIdle(AUTO_EXIT_MS, Date.now())).toBe(false);
  });

  it("IS idle when no instances and no launcher activity within the window", () => {
    const r = new Registry();
    r.noteLauncherActivity();
    expect(r.isIdle(AUTO_EXIT_MS, Date.now() + 2 * AUTO_EXIT_MS)).toBe(true);
  });

  it("a leaked launcher count does NOT keep it immortal (the 5-day-zombie bug)", () => {
    const r = new Registry();
    r.launcherConnect(); // count = 1, never disconnected (launcher killed abruptly)
    // isEmpty() would still report false here (count > 0) — the old immortal-hub path.
    expect(r.isEmpty()).toBe(false);
    // isIdle correctly reports idle past the activity window, so the hub can auto-exit.
    expect(r.isIdle(AUTO_EXIT_MS, Date.now() + 2 * AUTO_EXIT_MS)).toBe(true);
  });
});
