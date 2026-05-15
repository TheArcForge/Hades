import { createInterface } from "node:readline";
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import http from "node:http";
const HUB_DIR = path.join(process.env.HOME ?? process.env.USERPROFILE ?? "", ".arcforge", "hades-hub");
const HUB_JSON_PATH = path.join(HUB_DIR, "hub.json");
const HUB_ENTRY = findHubEntry();
function findHubEntry() {
    // 1. Relative to launcher source (works in dev: Bridge~/launcher/dist/ → Bridge~/hub/dist/)
    const relative = path.resolve(path.dirname(new URL(import.meta.url).pathname), "..", "..", "hub", "dist", "index.js");
    if (fs.existsSync(relative))
        return relative;
    // 2. Path file written by Unity with the absolute path to the hub entry point
    const pathFile = path.join(HUB_DIR, "hub-path.json");
    if (fs.existsSync(pathFile)) {
        try {
            const data = JSON.parse(fs.readFileSync(pathFile, "utf8"));
            if (data.hubEntry && fs.existsSync(data.hubEntry))
                return data.hubEntry;
        }
        catch {
            // ignore corrupt file
        }
    }
    // If none found, return relative and let it fail with a clear error at startup
    return relative;
}
const PROJECT_PATH = process.cwd();
const HUB_STARTUP_TIMEOUT_MS = 5000;
function readHubJson() {
    try {
        if (!fs.existsSync(HUB_JSON_PATH))
            return null;
        const data = JSON.parse(fs.readFileSync(HUB_JSON_PATH, "utf8"));
        return { port: data.port, pid: data.pid };
    }
    catch {
        return null;
    }
}
function isProcessAlive(pid) {
    try {
        process.kill(pid, 0);
        return true;
    }
    catch {
        return false;
    }
}
async function probeHealth(port) {
    return new Promise((resolve) => {
        const req = http.get(`http://127.0.0.1:${port}/health`, { timeout: 2000 }, (res) => {
            res.resume();
            resolve(res.statusCode === 200);
        });
        req.on("error", () => resolve(false));
        req.on("timeout", () => {
            req.destroy();
            resolve(false);
        });
    });
}
function startHub() {
    process.stderr.write("[hades-launcher] Starting hub...\n");
    const child = spawn("node", [HUB_ENTRY], {
        detached: true,
        stdio: "ignore",
        env: { ...process.env },
    });
    child.unref();
}
async function ensureHub() {
    // Check if hub is already running
    const existing = readHubJson();
    if (existing && isProcessAlive(existing.pid)) {
        const healthy = await probeHealth(existing.port);
        if (healthy)
            return existing.port;
    }
    // Clean up stale hub.json
    if (existing && !isProcessAlive(existing.pid)) {
        try {
            fs.unlinkSync(HUB_JSON_PATH);
        }
        catch {
            // ignore
        }
    }
    // Start the hub
    startHub();
    // Wait for hub.json to appear with a live process
    const deadline = Date.now() + HUB_STARTUP_TIMEOUT_MS;
    while (Date.now() < deadline) {
        await new Promise((r) => setTimeout(r, 200));
        const info = readHubJson();
        if (info && isProcessAlive(info.pid)) {
            const healthy = await probeHealth(info.port);
            if (healthy)
                return info.port;
        }
    }
    throw new Error("Hub failed to start within timeout");
}
function httpPost(port, urlPath, body, headers) {
    return new Promise((resolve, reject) => {
        const req = http.request({
            hostname: "127.0.0.1",
            port,
            path: urlPath,
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "Content-Length": Buffer.byteLength(body),
                ...headers,
            },
            timeout: 30_000,
        }, (res) => {
            let data = "";
            res.on("data", (chunk) => (data += chunk));
            res.on("end", () => resolve(data));
        });
        req.on("error", reject);
        req.on("timeout", () => {
            req.destroy();
            reject(new Error("Request timeout"));
        });
        req.write(body);
        req.end();
    });
}
async function main() {
    let hubPort;
    try {
        hubPort = await ensureHub();
    }
    catch (err) {
        process.stderr.write(`[hades-launcher] ${err}\n`);
        process.exit(1);
    }
    process.stderr.write(`[hades-launcher] Connected to hub on port ${hubPort}\n`);
    // Register as launcher
    await httpPost(hubPort, "/api/launcher/connect", "{}");
    // Bridge stdin → hub → stdout
    const rl = createInterface({ input: process.stdin });
    rl.on("line", async (line) => {
        if (!line.trim())
            return;
        try {
            const response = await httpPost(hubPort, "/rpc", line, {
                "X-Hades-Project": PROJECT_PATH,
            });
            if (response) {
                process.stdout.write(response + "\n");
            }
        }
        catch (err) {
            // Hub might have died — try to restart
            try {
                process.stderr.write("[hades-launcher] Hub connection lost, restarting...\n");
                hubPort = await ensureHub();
                await httpPost(hubPort, "/api/launcher/connect", "{}");
                const response = await httpPost(hubPort, "/rpc", line, {
                    "X-Hades-Project": PROJECT_PATH,
                });
                if (response) {
                    process.stdout.write(response + "\n");
                }
            }
            catch (retryErr) {
                const errorResponse = JSON.stringify({
                    jsonrpc: "2.0",
                    id: null,
                    error: { code: -32000, message: `Hub error: ${retryErr}` },
                });
                process.stdout.write(errorResponse + "\n");
            }
        }
    });
    rl.on("close", async () => {
        try {
            await httpPost(hubPort, "/api/launcher/disconnect", "{}");
        }
        catch {
            // best effort
        }
        process.exit(0);
    });
}
main();
//# sourceMappingURL=index.js.map