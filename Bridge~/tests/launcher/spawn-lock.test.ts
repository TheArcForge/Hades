import { describe, it, expect, afterEach } from "vitest";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { acquireSpawnLock, releaseSpawnLock } from "../../launcher/src/spawn-lock.js";

describe("spawn lock", () => {
  const made: string[] = [];
  function lockPath(): string {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "hades-lock-"));
    made.push(dir);
    return path.join(dir, "hub.lock");
  }
  afterEach(() => {
    for (const d of made.splice(0)) fs.rmSync(d, { recursive: true, force: true });
  });

  it("first acquire succeeds; a second concurrent acquire fails", () => {
    const lp = lockPath();
    const fd = acquireSpawnLock(lp);
    expect(fd).not.toBeNull();
    expect(acquireSpawnLock(lp)).toBeNull(); // held by the first
    releaseSpawnLock(fd as number, lp);
  });

  it("acquire succeeds again after release", () => {
    const lp = lockPath();
    const fd1 = acquireSpawnLock(lp);
    releaseSpawnLock(fd1 as number, lp);
    const fd2 = acquireSpawnLock(lp);
    expect(fd2).not.toBeNull();
    releaseSpawnLock(fd2 as number, lp);
  });

  it("steals a stale lock left by a crashed spawner", () => {
    const lp = lockPath();
    fs.writeFileSync(lp, ""); // leftover lock
    const old = Date.now() / 1000 - 60; // 60s ago, past the stale threshold
    fs.utimesSync(lp, old, old);
    const fd = acquireSpawnLock(lp);
    expect(fd).not.toBeNull(); // stolen
    releaseSpawnLock(fd as number, lp);
  });
});
