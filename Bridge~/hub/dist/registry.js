import fs from "node:fs";
export class Registry {
    instances = new Map();
    _launcherCount = 0;
    // Wall-clock of the last launcher request (connect or /rpc). Hub liveness is keyed on
    // THIS, not _launcherCount: an abruptly-killed launcher never POSTs /api/launcher/disconnect,
    // so the count leaks and previously kept the hub immortal (never auto-exiting, never
    // picking up new code). Seeded to "now" so a just-started hub isn't instantly idle.
    _lastLauncherActivity = Date.now();
    get launcherCount() {
        return this._launcherCount;
    }
    register(req) {
        const now = Date.now();
        const existing = this.instances.get(req.projectPath);
        this.instances.set(req.projectPath, {
            projectName: req.projectName,
            projectPath: req.projectPath,
            port: req.port,
            pid: req.pid,
            registeredAt: existing?.registeredAt ?? now,
            lastHeartbeat: now,
            status: "healthy",
            transientSince: null,
            manifestPackages: req.manifestPackages,
        });
    }
    deregister(req) {
        if (req.transient) {
            const instance = this.instances.get(req.projectPath);
            if (instance) {
                instance.status = "transient";
                instance.transientSince = Date.now();
            }
        }
        else {
            this.instances.delete(req.projectPath);
        }
    }
    heartbeat(req) {
        const instance = this.instances.get(req.projectPath);
        if (!instance)
            return false;
        instance.lastHeartbeat = Date.now();
        instance.port = req.port;
        instance.pid = req.pid;
        return true;
    }
    get(projectPath) {
        return this.instances.get(projectPath) ?? null;
    }
    getAll() {
        return Array.from(this.instances.values());
    }
    markStale(projectPath) {
        const instance = this.instances.get(projectPath);
        if (instance) {
            instance.status = "stale";
        }
    }
    markHealthy(projectPath) {
        const instance = this.instances.get(projectPath);
        if (instance) {
            instance.status = "healthy";
            instance.lastHeartbeat = Date.now();
            instance.transientSince = null;
        }
    }
    remove(projectPath) {
        this.instances.delete(projectPath);
    }
    launcherConnect() {
        this._launcherCount++;
        this._lastLauncherActivity = Date.now();
    }
    launcherDisconnect() {
        if (this._launcherCount > 0)
            this._launcherCount--;
    }
    /** Record any launcher request (connect or /rpc forward), so an actively-used hub stays
     * alive without relying on a disconnect notification that abrupt exits never send. */
    noteLauncherActivity() {
        this._lastLauncherActivity = Date.now();
    }
    isEmpty() {
        return this.instances.size === 0 && this._launcherCount === 0;
    }
    /** Auto-exit gate: no Unity instances AND no launcher activity within `autoExitMs`. Unlike
     * isEmpty() this is robust to leaked launcher counts, so the hub stops being immortal. */
    isIdle(autoExitMs, now = Date.now()) {
        return this.instances.size === 0 && now - this._lastLauncherActivity > autoExitMs;
    }
    instanceCount() {
        return this.instances.size;
    }
    findByProjectPath(cwd) {
        const normalizedCwd = normalizePath(cwd);
        const active = this.getAll().filter((i) => i.status !== "stale");
        // 1. Exact match
        const exact = active.find((i) => normalizePath(i.projectPath) === normalizedCwd);
        if (exact)
            return exact;
        // 2. Parent match: CWD is a parent of a registered projectPath
        const parentMatches = active.filter((i) => normalizePath(i.projectPath).startsWith(normalizedCwd + "/"));
        if (parentMatches.length > 0) {
            parentMatches.sort((a, b) => normalizePath(b.projectPath).length -
                normalizePath(a.projectPath).length);
            return parentMatches[0];
        }
        // 3. Child match: CWD is a child of a registered projectPath
        const childMatch = active.find((i) => normalizedCwd.startsWith(normalizePath(i.projectPath) + "/"));
        if (childMatch)
            return childMatch;
        // 4. Manifest match: CWD matches a file: package path
        const manifestMatch = active.find((i) => i.manifestPackages?.some((pkg) => normalizePath(pkg) === normalizedCwd));
        if (manifestMatch)
            return manifestMatch;
        // 5. Single-instance fallback: nothing matched, but if exactly ONE instance is
        // registered (and it's active) it's unambiguous — route to it. Handles a launcher that
        // can't identify its project (e.g. cwd is "/") when only one Unity is open. Keyed on the
        // total registered count, NOT active count: with a second (e.g. reloading/stale) instance
        // present there are two projects in play, so querying one must never route to the other.
        if (this.instances.size === 1 && active.length === 1)
            return active[0];
        return null;
    }
}
function normalizePath(p) {
    let resolved = p;
    try {
        // Resolve symlinks so a symlinked cwd matches a real registered project path (and
        // vice-versa). Unity registers a lexical Path.GetFullPath; the launcher's cwd is
        // already realpath-resolved by getcwd — canonicalizing both sides closes that gap.
        resolved = fs.realpathSync(p);
    }
    catch {
        // Path not on disk (moved/deleted project, or a synthetic test path) — use as given.
    }
    // Strip trailing slashes, but never collapse the root "/" to "" — an empty cwd makes the
    // parent-match `startsWith(cwd + "/")` match every absolute path.
    resolved = resolved.replace(/\/+$/, "") || "/";
    // macOS (APFS) and Windows default to case-insensitive filesystems; compare case-folded.
    return process.platform === "win32" || process.platform === "darwin"
        ? resolved.toLowerCase()
        : resolved;
}
//# sourceMappingURL=registry.js.map