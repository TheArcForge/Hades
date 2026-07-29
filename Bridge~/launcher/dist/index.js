// src/index.ts
import { createInterface } from "node:readline";
import { spawn } from "node:child_process";
import fs4 from "node:fs";
import path3 from "node:path";
import http from "node:http";

// src/project-path.ts
import fs from "node:fs";
import path from "node:path";
function findProjectRoot(cwd) {
  let dir = cwd;
  for (let i = 0; i < 40; i++) {
    if (fs.existsSync(path.join(dir, "ProjectSettings", "ProjectVersion.txt"))) {
      return dir;
    }
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
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

// src/hub-dir.ts
import fs3 from "node:fs";
import path2 from "node:path";
var ENV_HUB_DIR = "HADES_HUB_DIR";
var CONFIG_FILE_NAME = "config.local.yaml";
var ARCFORGE_DIR_NAME = ".arcforge";
var HUB_DIR_NAME = "hades-hub";
var HUB_SCOPE_KEY = "hub_scope";
function defaultReadFile(filePath) {
  try {
    return fs3.readFileSync(filePath, "utf8");
  } catch {
    return null;
  }
}
function readHubScope(arcforgeDir, readFile) {
  const raw = readFile(path2.join(arcforgeDir, CONFIG_FILE_NAME));
  if (raw === null) return "local";
  for (const line of raw.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith("#")) continue;
    const colonIdx = trimmed.indexOf(":");
    if (colonIdx <= 0) continue;
    if (trimmed.slice(0, colonIdx).trim() !== HUB_SCOPE_KEY) continue;
    return trimmed.slice(colonIdx + 1).trim().toLowerCase() === "global" ? "global" : "local";
  }
  return "local";
}
function resolveHubDir(opts) {
  const override = opts.env[ENV_HUB_DIR];
  if (override && override.trim()) return override.trim();
  const home = opts.env.HOME ?? opts.env.USERPROFILE ?? "";
  const globalDir = path2.join(home, ARCFORGE_DIR_NAME, HUB_DIR_NAME);
  if (!opts.projectRoot) return globalDir;
  const arcforgeDir = path2.join(opts.projectRoot, ARCFORGE_DIR_NAME);
  if (readHubScope(arcforgeDir, opts.readFile) === "global") return globalDir;
  return path2.join(arcforgeDir, HUB_DIR_NAME);
}

// src/index.ts
var PROJECT_ROOT = findProjectRoot(process.cwd());
var HUB_DIR = resolveHubDir({
  env: process.env,
  projectRoot: PROJECT_ROOT,
  readFile: defaultReadFile
});
var HUB_JSON_PATH = path3.join(HUB_DIR, "hub.json");
var HUB_ENTRY = findHubEntry();
function findHubEntry() {
  const relative = path3.resolve(
    path3.dirname(new URL(import.meta.url).pathname),
    "..",
    "..",
    "hub",
    "dist",
    "index.js"
  );
  if (fs4.existsSync(relative)) return relative;
  const pathFile = path3.join(HUB_DIR, "hub-path.json");
  if (fs4.existsSync(pathFile)) {
    try {
      const data = JSON.parse(fs4.readFileSync(pathFile, "utf8"));
      if (data.hubEntry && fs4.existsSync(data.hubEntry)) return data.hubEntry;
    } catch {
    }
  }
  return relative;
}
var PROJECT_PATH = PROJECT_ROOT ?? process.cwd();
var HUB_STARTUP_TIMEOUT_MS = 15e3;
var PROTOCOL_VERSION = "2024-11-05";
var SERVER_NAME = "hades";
var SERVER_VERSION = "1.1.0";
function readHubJson() {
  try {
    if (!fs4.existsSync(HUB_JSON_PATH)) return null;
    const data = JSON.parse(fs4.readFileSync(HUB_JSON_PATH, "utf8"));
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
    // Hand the resolved dir down explicitly. If the hub re-derived it from $HOME it could
    // disagree with this launcher and publish hub.json where nobody is looking.
    env: { ...process.env, [ENV_HUB_DIR]: HUB_DIR }
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
      fs4.unlinkSync(HUB_JSON_PATH);
    } catch {
    }
  }
  try {
    fs4.mkdirSync(HUB_DIR, { recursive: true });
  } catch {
  }
  const lockPath = path3.join(HUB_DIR, "hub.lock");
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
