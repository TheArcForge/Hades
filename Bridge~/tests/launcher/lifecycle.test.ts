import { describe, it, expect } from "vitest";
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import http from "node:http";

const HUB_DIR = path.join(
  process.env.HOME ?? process.env.USERPROFILE ?? "",
  ".arcforge",
  "hades-hub"
);
const HUB_JSON_PATH = path.join(HUB_DIR, "hub.json");
const HUB_ENTRY = path.resolve(
  __dirname,
  "..",
  "..",
  "hub",
  "dist",
  "index.js"
);

function readHubJson(): { port: number; pid: number } | null {
  try {
    return JSON.parse(fs.readFileSync(HUB_JSON_PATH, "utf8"));
  } catch {
    return null;
  }
}

function httpGet(port: number, urlPath: string): Promise<string> {
  return new Promise((resolve, reject) => {
    http
      .get(`http://127.0.0.1:${port}${urlPath}`, { timeout: 2000 }, (res) => {
        let data = "";
        res.on("data", (chunk: string) => (data += chunk));
        res.on("end", () => resolve(data));
      })
      .on("error", reject);
  });
}

describe("Launcher → Hub lifecycle", () => {
  it("hub starts, writes hub.json, and responds to /health", async () => {
    // Clean up any existing hub
    try {
      const old = readHubJson();
      if (old) process.kill(old.pid, "SIGTERM");
    } catch {
      // ignore
    }
    try {
      fs.unlinkSync(HUB_JSON_PATH);
    } catch {
      // ignore
    }

    // Start hub directly
    const hub = spawn("node", [HUB_ENTRY], {
      detached: true,
      stdio: "ignore",
    });
    hub.unref();

    // Wait for hub.json
    const deadline = Date.now() + 5000;
    let hubInfo: { port: number; pid: number } | null = null;
    while (Date.now() < deadline) {
      await new Promise((r) => setTimeout(r, 200));
      hubInfo = readHubJson();
      if (hubInfo) break;
    }

    expect(hubInfo).not.toBeNull();
    expect(hubInfo!.port).toBeGreaterThan(0);
    expect(hubInfo!.pid).toBeGreaterThan(0);

    // Check health
    const health = JSON.parse(await httpGet(hubInfo!.port, "/health"));
    expect(health.status).toBe("ok");
    expect(health.instances).toBeGreaterThanOrEqual(0);

    // Clean up
    process.kill(hubInfo!.pid, "SIGTERM");
  }, 10_000);
});
