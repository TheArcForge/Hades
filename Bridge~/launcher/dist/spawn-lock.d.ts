/**
 * Exclusive spawn lock so only one launcher starts the hub when several race at once
 * (e.g. multiple Claude Code sessions opening together). Returns an open fd on success, or
 * null if another launcher currently holds it (caller should wait for hub.json instead of
 * spawning a second, orphaned hub). A stale lock (crashed spawner) is stolen.
 */
export declare function acquireSpawnLock(lockPath: string): number | null;
export declare function releaseSpawnLock(fd: number, lockPath: string): void;
