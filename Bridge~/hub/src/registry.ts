import {
  InstanceEntry,
  RegisterRequest,
  DeregisterRequest,
  HeartbeatRequest,
} from "./types.js";

export class Registry {
  private instances = new Map<string, InstanceEntry>();
  private _launcherCount = 0;

  get launcherCount(): number {
    return this._launcherCount;
  }

  register(req: RegisterRequest): void {
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

  deregister(req: DeregisterRequest): void {
    if (req.transient) {
      const instance = this.instances.get(req.projectPath);
      if (instance) {
        instance.status = "transient";
        instance.transientSince = Date.now();
      }
    } else {
      this.instances.delete(req.projectPath);
    }
  }

  heartbeat(req: HeartbeatRequest): boolean {
    const instance = this.instances.get(req.projectPath);
    if (!instance) return false;

    instance.lastHeartbeat = Date.now();
    instance.port = req.port;
    instance.pid = req.pid;
    return true;
  }

  get(projectPath: string): InstanceEntry | null {
    return this.instances.get(projectPath) ?? null;
  }

  getAll(): InstanceEntry[] {
    return Array.from(this.instances.values());
  }

  markStale(projectPath: string): void {
    const instance = this.instances.get(projectPath);
    if (instance) {
      instance.status = "stale";
    }
  }

  markHealthy(projectPath: string): void {
    const instance = this.instances.get(projectPath);
    if (instance) {
      instance.status = "healthy";
      instance.lastHeartbeat = Date.now();
      instance.transientSince = null;
    }
  }

  remove(projectPath: string): void {
    this.instances.delete(projectPath);
  }

  launcherConnect(): void {
    this._launcherCount++;
  }

  launcherDisconnect(): void {
    if (this._launcherCount > 0) this._launcherCount--;
  }

  isEmpty(): boolean {
    return this.instances.size === 0 && this._launcherCount === 0;
  }

  instanceCount(): number {
    return this.instances.size;
  }

  findByProjectPath(cwd: string): InstanceEntry | null {
    const normalizedCwd = normalizePath(cwd);
    const active = this.getAll().filter((i) => i.status !== "stale");

    // 1. Exact match
    const exact = active.find(
      (i) => normalizePath(i.projectPath) === normalizedCwd
    );
    if (exact) return exact;

    // 2. Parent match: CWD is a parent of a registered projectPath
    const parentMatches = active.filter((i) =>
      normalizePath(i.projectPath).startsWith(normalizedCwd + "/")
    );
    if (parentMatches.length > 0) {
      parentMatches.sort(
        (a, b) =>
          normalizePath(b.projectPath).length -
          normalizePath(a.projectPath).length
      );
      return parentMatches[0];
    }

    // 3. Child match: CWD is a child of a registered projectPath
    const childMatch = active.find((i) =>
      normalizedCwd.startsWith(normalizePath(i.projectPath) + "/")
    );
    if (childMatch) return childMatch;

    // 4. Manifest match: CWD matches a file: package path
    const manifestMatch = active.find((i) =>
      i.manifestPackages?.some((pkg) => normalizePath(pkg) === normalizedCwd)
    );
    if (manifestMatch) return manifestMatch;

    return null;
  }
}

function normalizePath(p: string): string {
  return p.replace(/\/+$/, "");
}
