export class Registry {
    instances = new Map();
    _launcherCount = 0;
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
    }
    launcherDisconnect() {
        if (this._launcherCount > 0)
            this._launcherCount--;
    }
    isEmpty() {
        return this.instances.size === 0 && this._launcherCount === 0;
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
        return null;
    }
}
function normalizePath(p) {
    return p.replace(/\/+$/, "");
}
//# sourceMappingURL=registry.js.map