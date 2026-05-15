export const HEARTBEAT_STALE_MS = 90_000; // 3 missed 30s heartbeats
export const TRANSIENT_TIMEOUT_MS = 30_000;
export const STALE_PURGE_MS = 300_000; // 5 minutes
export async function checkStaleInstances(registry, probe) {
    const now = Date.now();
    for (const instance of registry.getAll()) {
        if (instance.status === "transient") {
            if (instance.transientSince &&
                now - instance.transientSince > TRANSIENT_TIMEOUT_MS) {
                const alive = await probe(instance.port);
                if (alive) {
                    registry.markHealthy(instance.projectPath);
                }
                else {
                    registry.markStale(instance.projectPath);
                }
            }
            continue;
        }
        if (instance.status === "stale") {
            if (now - instance.lastHeartbeat > HEARTBEAT_STALE_MS + STALE_PURGE_MS) {
                registry.remove(instance.projectPath);
            }
            continue;
        }
        // healthy — check for missed heartbeats
        if (now - instance.lastHeartbeat > HEARTBEAT_STALE_MS) {
            const alive = await probe(instance.port);
            if (alive) {
                registry.markHealthy(instance.projectPath);
            }
            else {
                registry.markStale(instance.projectPath);
            }
        }
    }
}
export async function probeUnityInstance(port) {
    try {
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 3000);
        const res = await fetch(`http://127.0.0.1:${port}/rpc`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                jsonrpc: "2.0",
                id: "probe",
                method: "initialize",
                params: {},
            }),
            signal: controller.signal,
        });
        clearTimeout(timeout);
        return res.ok;
    }
    catch {
        return false;
    }
}
//# sourceMappingURL=heartbeat.js.map