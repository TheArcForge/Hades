// src/index.ts
import { createInterface } from "node:readline";
import { spawn } from "node:child_process";
import fs3 from "node:fs";
import path2 from "node:path";
import http from "node:http";

// src/project-path.ts
import fs from "node:fs";
import path from "node:path";
function resolveProjectPath(cwd) {
  let dir = cwd;
  for (let i = 0; i < 40; i++) {
    if (fs.existsSync(path.join(dir, "ProjectSettings", "ProjectVersion.txt"))) {
      return dir;
    }
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return cwd;
}

// src/spawn-lock.ts
import fs2 from "node:fs";
var STALE_LOCK_MS = 2e4;
function acquireSpawnLock(lockPath) {
  try {
    return fs2.openSync(lockPath, "wx");
  } catch {
    try {
      const age = Date.now() - fs2.statSync(lockPath).mtimeMs;
      if (age > STALE_LOCK_MS) {
        fs2.unlinkSync(lockPath);
        return fs2.openSync(lockPath, "wx");
      }
    } catch {
    }
    return null;
  }
}
function releaseSpawnLock(fd, lockPath) {
  try {
    fs2.closeSync(fd);
  } catch {
  }
  try {
    fs2.unlinkSync(lockPath);
  } catch {
  }
}

// src/index.ts
var HUB_DIR = path2.join(
  process.env.HOME ?? process.env.USERPROFILE ?? "",
  ".arcforge",
  "hades-hub"
);
var HUB_JSON_PATH = path2.join(HUB_DIR, "hub.json");
var HUB_ENTRY = findHubEntry();
function findHubEntry() {
  const relative = path2.resolve(
    path2.dirname(new URL(import.meta.url).pathname),
    "..",
    "..",
    "hub",
    "dist",
    "index.js"
  );
  if (fs3.existsSync(relative)) return relative;
  const pathFile = path2.join(HUB_DIR, "hub-path.json");
  if (fs3.existsSync(pathFile)) {
    try {
      const data = JSON.parse(fs3.readFileSync(pathFile, "utf8"));
      if (data.hubEntry && fs3.existsSync(data.hubEntry)) return data.hubEntry;
    } catch {
    }
  }
  return relative;
}
var PROJECT_PATH = resolveProjectPath(process.cwd());
var HUB_STARTUP_TIMEOUT_MS = 15e3;
var PROTOCOL_VERSION = "2024-11-05";
var SERVER_NAME = "hades";
var SERVER_VERSION = "1.1.0";
function readHubJson() {
  try {
    if (!fs3.existsSync(HUB_JSON_PATH)) return null;
    const data = JSON.parse(fs3.readFileSync(HUB_JSON_PATH, "utf8"));
    return { port: data.port, pid: data.pid };
  } catch {
    return null;
  }
}
function isProcessAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}
async function probeHealth(port) {
  return new Promise((resolve) => {
    const req = http.get(
      `http://127.0.0.1:${port}/health`,
      { timeout: 2e3 },
      (res) => {
        res.resume();
        resolve(res.statusCode === 200);
      }
    );
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
    env: { ...process.env }
  });
  child.unref();
}
async function ensureHub() {
  const existing = readHubJson();
  if (existing && isProcessAlive(existing.pid)) {
    const healthy = await probeHealth(existing.port);
    if (healthy) return existing.port;
  }
  if (existing && !isProcessAlive(existing.pid)) {
    try {
      fs3.unlinkSync(HUB_JSON_PATH);
    } catch {
    }
  }
  try {
    fs3.mkdirSync(HUB_DIR, { recursive: true });
  } catch {
  }
  const lockPath = path2.join(HUB_DIR, "hub.lock");
  const lockFd = acquireSpawnLock(lockPath);
  try {
    if (lockFd !== null) startHub();
    const deadline = Date.now() + HUB_STARTUP_TIMEOUT_MS;
    while (Date.now() < deadline) {
      await new Promise((r) => setTimeout(r, 200));
      const info = readHubJson();
      if (info && isProcessAlive(info.pid)) {
        const healthy = await probeHealth(info.port);
        if (healthy) return info.port;
      }
    }
    throw new Error("Hub failed to start within timeout");
  } finally {
    if (lockFd !== null) releaseSpawnLock(lockFd, lockPath);
  }
}
function httpPost(port, urlPath, body, headers) {
  return new Promise((resolve, reject) => {
    const req = http.request(
      {
        hostname: "127.0.0.1",
        port,
        path: urlPath,
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Content-Length": Buffer.byteLength(body),
          ...headers
        },
        timeout: 3e4
      },
      (res) => {
        if (res.statusCode && (res.statusCode < 200 || res.statusCode >= 300)) {
          res.resume();
          reject(new Error(`HTTP ${res.statusCode}`));
          return;
        }
        let data = "";
        res.on("data", (chunk) => data += chunk);
        res.on("end", () => resolve(data));
      }
    );
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
      serverInfo: { name: SERVER_NAME, version: SERVER_VERSION }
    }
  };
  return JSON.stringify(response);
}
async function main() {
  let hubPort = null;
  let hubReady = false;
  let hubPromise = null;
  async function getHubPort() {
    if (hubReady && hubPort !== null) return hubPort;
    if (!hubPromise) {
      hubPromise = ensureHub().then((port) => {
        hubPort = port;
        hubReady = true;
        process.stderr.write(
          `[hades-launcher] Connected to hub on port ${port}
`
        );
        return httpPost(port, "/api/launcher/connect", "{}").then(() => port);
      });
    }
    return hubPromise;
  }
  const rl = createInterface({ input: process.stdin });
  getHubPort().catch((err) => {
    process.stderr.write(`[hades-launcher] Hub startup failed: ${err}
`);
  });
  rl.on("line", (line) => {
    handleLine(line).catch((err) => {
      process.stderr.write(`[hades-launcher] Fatal: ${err}
`);
      process.exit(1);
    });
  });
  async function handleLine(line) {
    if (!line.trim()) return;
    let parsed;
    try {
      parsed = JSON.parse(line);
    } catch {
      return;
    }
    if (parsed.method === "initialize") {
      const response = handleInitializeLocally(parsed);
      process.stdout.write(response + "\n");
      return;
    }
    if (parsed.method === "notifications/initialized") {
      return;
    }
    try {
      const port = await getHubPort();
      const response = await httpPost(port, "/rpc", line, {
        "X-Hades-Project": PROJECT_PATH
      });
      if (response) {
        process.stdout.write(response + "\n");
      }
    } catch (err) {
      try {
        process.stderr.write(
          "[hades-launcher] Hub connection lost, restarting...\n"
        );
        hubReady = false;
        hubPromise = null;
        const port = await getHubPort();
        const response = await httpPost(port, "/rpc", line, {
          "X-Hades-Project": PROJECT_PATH
        });
        if (response) {
          process.stdout.write(response + "\n");
        }
      } catch (retryErr) {
        const errorResponse = JSON.stringify({
          jsonrpc: "2.0",
          id: parsed.id ?? null,
          error: { code: -32e3, message: `Hub error: ${retryErr}` }
        });
        process.stdout.write(errorResponse + "\n");
      }
    }
  }
  rl.on("close", async () => {
    if (hubReady && hubPort !== null) {
      try {
        await httpPost(hubPort, "/api/launcher/disconnect", "{}");
      } catch {
      }
    }
    process.exit(0);
  });
}
main();
