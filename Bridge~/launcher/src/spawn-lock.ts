import fs from "node:fs";

// A hub spawn should complete (write hub.json) well within this. If a lock is older, the
// previous spawner crashed mid-spawn — steal it so we don't deadlock forever.
const STALE_LOCK_MS = 20_000;

/**
 * Exclusive spawn lock so only one launcher starts the hub when several race at once
 * (e.g. multiple Claude Code sessions opening together). Returns an open fd on success, or
 * null if another launcher currently holds it (caller should wait for hub.json instead of
 * spawning a second, orphaned hub). A stale lock (crashed spawner) is stolen.
 */
export function acquireSpawnLock(lockPath: string): number | null {
  try {
    return fs.openSync(lockPath, "wx"); // O_CREAT | O_EXCL — fails if the lock exists
  } catch {
    try {
      const age = Date.now() - fs.statSync(lockPath).mtimeMs;
      if (age > STALE_LOCK_MS) {
        fs.unlinkSync(lockPath);
        return fs.openSync(lockPath, "wx");
      }
    } catch {
      // Raced with another process removing/replacing the lock — treat as not acquired.
    }
    return null;
  }
}

export function releaseSpawnLock(fd: number, lockPath: string): void {
  try {
    fs.closeSync(fd);
  } catch {
    // already closed
  }
  try {
    fs.unlinkSync(lockPath);
  } catch {
    // already removed
  }
}
