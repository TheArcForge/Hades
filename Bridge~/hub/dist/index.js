import fs from "node:fs";
import path from "node:path";
import { createHubServer } from "./server.js";
import { checkStaleInstances, probeUnityInstance, } from "./heartbeat.js";
// Handed down by the launcher that spawned this hub (see startHub). The $HOME fallback covers a
// hub started by hand or by an older launcher. The hub deliberately does NOT re-derive this from
// a project root: only the launcher knows which project it was invoked for.
const HUB_DIR = process.env.HADES_HUB_DIR?.trim() ||
    path.join(process.env.HOME ?? process.env.USERPROFILE ?? "", ".arcforge", "hades-hub");
const HUB_JSON_PATH = path.join(HUB_DIR, "hub.json");
const PENDING_DIR = path.join(HUB_DIR, "pending");
const AUTO_EXIT_MS = 60_000;
const HEARTBEAT_CHECK_INTERVAL_MS = 15_000;
function writeHubJson(port) {
    if (!fs.existsSync(HUB_DIR)) {
        fs.mkdirSync(HUB_DIR, { recursive: true });
    }
    const info = {
        port,
        pid: process.pid,
        startedAt: Date.now(),
    };
    const tmpPath = HUB_JSON_PATH + ".tmp";
    fs.writeFileSync(tmpPath, JSON.stringify(info, null, 2));
    fs.renameSync(tmpPath, HUB_JSON_PATH);
}
function deleteHubJson() {
    try {
        if (fs.existsSync(HUB_JSON_PATH))
            fs.unlinkSync(HUB_JSON_PATH);
    }
    catch {
        // best effort
    }
}
async function main() {
    const hub = await createHubServer();
    // Load breadcrumbs
    if (fs.existsSync(PENDING_DIR)) {
        const files = fs.readdirSync(PENDING_DIR).filter((f) => f.endsWith(".json"));
        for (const file of files) {
            try {
                const data = JSON.parse(fs.readFileSync(path.join(PENDING_DIR, file), "utf8"));
                hub.registry.register(data);
                fs.unlinkSync(path.join(PENDING_DIR, file));
                process.stderr.write(`[hades-hub] Loaded pending: ${data.projectName}\n`);
            }
            catch {
                // Skip corrupt breadcrumbs
            }
        }
    }
    writeHubJson(hub.port);
    process.stderr.write(`[hades-hub] Listening on 127.0.0.1:${hub.port}\n`);
    // Heartbeat monitor
    setInterval(async () => {
        await checkStaleInstances(hub.registry, probeUnityInstance);
    }, HEARTBEAT_CHECK_INTERVAL_MS);
    // Auto-exit check. isIdle() already encodes the 60s window (no instances AND no launcher
    // activity within it) and is robust to leaked launcher counts, so the old two-stage
    // autoExitStart timer — and the immortal-hub bug it caused — are gone.
    setInterval(() => {
        if (hub.registry.isIdle(AUTO_EXIT_MS)) {
            process.stderr.write("[hades-hub] No instances or launcher activity for 60s, exiting.\n");
            hub.close().then(() => {
                deleteHubJson();
                process.exit(0);
            });
        }
    }, HEARTBEAT_CHECK_INTERVAL_MS);
    const shutdown = () => {
        hub.close().then(() => {
            deleteHubJson();
            process.exit(0);
        });
    };
    process.on("SIGTERM", shutdown);
    process.on("SIGINT", shutdown);
}
main();
//# sourceMappingURL=index.js.map