import { createInterface } from "node:readline";
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import http from "node:http";
const HUB_DIR = path.join(process.env.HOME ?? process.env.USERPROFILE ?? "", ".arcforge", "hades-hub");
const HUB_JSON_PATH = path.join(HUB_DIR, "hub.json");
const HUB_ENTRY = findHubEntry();
function findHubEntry() {
    const relative = path.resolve(path.dirname(new URL(import.meta.url).pathname), "..", "..", "hub", "dist", "index.js");
    if (fs.existsSync(relative))
        return relative;
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
    return relative;
}
const PROJECT_PATH = process.cwd();
const HUB_STARTUP_TIMEOUT_MS = 15000;
const PROTOCOL_VERSION = "2024-11-05";
const SERVER_NAME = "hades";
const SERVER_VERSION = "0.9.1";
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
    const existing = readHubJson();
    if (existing && isProcessAlive(existing.pid)) {
        const healthy = await probeHealth(existing.port);
        if (healthy)
            return existing.port;
    }
    if (existing && !isProcessAlive(existing.pid)) {
        try {
            fs.unlinkSync(HUB_JSON_PATH);
        }
        catch {
            // ignore
        }
    }
    startHub();
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
            if (res.statusCode && (res.statusCode < 200 || res.statusCode >= 300)) {
                res.resume();
                reject(new Error(`HTTP ${res.statusCode}`));
                return;
            }
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
function handleInitializeLocally(request) {
    const response = {
        jsonrpc: "2.0",
        id: request.id,
        result: {
            protocolVersion: PROTOCOL_VERSION,
            capabilities: { tools: {} },
            serverInfo: { name: SERVER_NAME, version: SERVER_VERSION },
        },
    };
    return JSON.stringify(response);
}
async function main() {
    let hubPort = null;
    let hubReady = false;
    let hubPromise = null;
    async function getHubPort() {
        if (hubReady && hubPort !== null)
            return hubPort;
        if (!hubPromise) {
            hubPromise = ensureHub().then((port) => {
                hubPort = port;
                hubReady = true;
                process.stderr.write(`[hades-launcher] Connected to hub on port ${port}\n`);
                return httpPost(port, "/api/launcher/connect", "{}").then(() => port);
            });
        }
        return hubPromise;
    }
    // Attach stdin immediately — do NOT wait for Hub
    const rl = createInterface({ input: process.stdin });
    // Start Hub connection eagerly in background
    getHubPort().catch((err) => {
        process.stderr.write(`[hades-launcher] Hub startup failed: ${err}\n`);
    });
    rl.on("line", (line) => {
        handleLine(line).catch((err) => {
            process.stderr.write(`[hades-launcher] Fatal: ${err}\n`);
            process.exit(1);
        });
    });
    async function handleLine(line) {
        if (!line.trim())
            return;
        let parsed;
        try {
            parsed = JSON.parse(line);
        }
        catch {
            return;
        }
        // Answer initialize locally — no Hub round-trip needed
        if (parsed.method === "initialize") {
            const response = handleInitializeLocally(parsed);
            process.stdout.write(response + "\n");
            return;
        }
        // notifications/initialized — acknowledge silently, no response needed
        if (parsed.method === "notifications/initialized") {
            return;
        }
        // All other messages (tools/list, tools/call) need the Hub
        try {
            const port = await getHubPort();
            const response = await httpPost(port, "/rpc", line, {
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
                hubReady = false;
                hubPromise = null;
                const port = await getHubPort();
                const response = await httpPost(port, "/rpc", line, {
                    "X-Hades-Project": PROJECT_PATH,
                });
                if (response) {
                    process.stdout.write(response + "\n");
                }
            }
            catch (retryErr) {
                const errorResponse = JSON.stringify({
                    jsonrpc: "2.0",
                    id: parsed.id ?? null,
                    error: { code: -32000, message: `Hub error: ${retryErr}` },
                });
                process.stdout.write(errorResponse + "\n");
            }
        }
    }
    rl.on("close", async () => {
        if (hubReady && hubPort !== null) {
            try {
                await httpPost(hubPort, "/api/launcher/disconnect", "{}");
            }
            catch {
                // best effort
            }
        }
        process.exit(0);
    });
}
main();
//# sourceMappingURL=index.js.map