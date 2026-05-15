import fs from "node:fs";
import path from "node:path";
import { createHubServer } from "./server.js";
import { checkStaleInstances, probeUnityInstance, } from "./heartbeat.js";
const HUB_DIR = path.join(process.env.HOME ?? process.env.USERPROFILE ?? "", ".arcforge", "hades-hub");
const HUB_JSON_PATH = path.join(HUB_DIR, "hub.json");
const PENDING_DIR = path.join(HUB_DIR, "pending");
const AUTO_EXIT_MS = 60_000;
const HEARTBEAT_CHECK_INTERVAL_MS = 15_000;
let autoExitStart = null;
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
    // Auto-exit check
    setInterval(() => {
        if (hub.registry.isEmpty()) {
            if (autoExitStart === null) {
                autoExitStart = Date.now();
            }
            else if (Date.now() - autoExitStart >= AUTO_EXIT_MS) {
                process.stderr.write("[hades-hub] No connections for 60s, exiting.\n");
                hub.close().then(() => {
                    deleteHubJson();
                    process.exit(0);
                });
            }
        }
        else {
            autoExitStart = null;
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