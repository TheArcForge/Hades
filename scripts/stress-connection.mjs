#!/usr/bin/env node
// Connection stress harness — hammers the agent→hub→Unity tool-call path with a cheap call
// while triggering domain reloads, and scores every response. Turns "Unity awkwardness" into
// numbers: response-class distribution + recovery time after a reload.
//
// Usage: node stress-connection.mjs [--seconds N] [--reload-at S[,S,...]] [--project PATH]
import http from "node:http";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const args = process.argv.slice(2);
const opt = (name, def) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : def;
};
const SECONDS = Number(opt("--seconds", "45"));
const RELOAD_AT = opt("--reload-at", "3").split(",").map(Number);
const hubJson = JSON.parse(
  fs.readFileSync(path.join(os.homedir(), ".arcforge", "hades-hub", "hub.json"), "utf8")
);
const HUB_PORT = Number(opt("--hub-port", String(hubJson.port)));
const PROJECT = opt("--project", "/Users/mike/Projects/Hades-Unity-Client");

function rpc(name, args = {}, timeoutMs = 8000) {
  const body = JSON.stringify({
    jsonrpc: "2.0",
    id: Math.floor(Math.random() * 1e9),
    method: "tools/call",
    params: { name, arguments: args },
  });
  const start = Date.now();
  return new Promise((resolve) => {
    const req = http.request(
      {
        hostname: "127.0.0.1",
        port: HUB_PORT,
        path: "/rpc",
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Content-Length": Buffer.byteLength(body),
          "X-Hades-Project": PROJECT,
        },
        timeout: timeoutMs,
      },
      (res) => {
        let data = "";
        res.on("data", (c) => (data += c));
        res.on("end", () => resolve({ cls: classify(res.statusCode, data), ms: Date.now() - start }));
      }
    );
    req.on("error", (e) =>
      resolve({ cls: e.code === "ECONNREFUSED" ? "refused" : "error", ms: Date.now() - start })
    );
    req.on("timeout", () => {
      req.destroy();
      resolve({ cls: "timeout", ms: Date.now() - start });
    });
    req.write(body);
    req.end();
  });
}

function classify(status, body) {
  if (status === 500) return "http500";
  let j;
  try { j = JSON.parse(body); } catch { return "unparseable"; }
  const text = j?.result?.content?.[0]?.text ?? "";
  if (j.error?.message?.includes("No Unity instance")) return "no-instance";
  if (j.error?.message?.toLowerCase().includes("reloading")) return "reloading";
  if (j.error) return "error";
  if (text.includes("rebuild_in_progress") || text.includes('"busy"')) return "busy";
  if (j.result) return "ok";
  return "other";
}

const log = []; // { t, cls, ms }
const t0 = Date.now();
const reloadsFired = new Set();

async function tick() {
  const now = (Date.now() - t0) / 1000;
  for (const r of RELOAD_AT) {
    if (now >= r && !reloadsFired.has(r)) {
      reloadsFired.add(r);
      process.stderr.write(`\n[t=${now.toFixed(1)}s] >>> triggering domain reload (EditMode run)\n`);
      // EditMode test runs ALWAYS trigger a domain reload (unlike a no-op recompile).
      rpc("project_run_tests", { filter: "StartupBusyGateTests", test_mode: "EditMode" }, 5000);
    }
  }
  const { cls, ms } = await rpc("hades_ping");
  log.push({ t: now, cls, ms });
  process.stderr.write(cls === "ok" ? "." : `[${cls}]`);
}

const interval = setInterval(tick, 300);
setTimeout(() => {
  clearInterval(interval);
  report();
}, SECONDS * 1000);

function report() {
  const counts = {};
  for (const e of log) counts[e.cls] = (counts[e.cls] || 0) + 1;
  const total = log.length;
  const okLatencies = log.filter((e) => e.cls === "ok").map((e) => e.ms).sort((a, b) => a - b);
  const p50 = okLatencies[Math.floor(okLatencies.length * 0.5)] ?? 0;
  const p95 = okLatencies[Math.floor(okLatencies.length * 0.95)] ?? 0;

  // Recovery: for each reload, gap from the reload time to the next 'ok'.
  const recoveries = [];
  for (const r of RELOAD_AT) {
    const after = log.filter((e) => e.t >= r);
    const firstBad = after.find((e) => e.cls !== "ok");
    if (!firstBad) continue;
    const firstOkAfterBad = after.find((e) => e.t > firstBad.t && e.cls === "ok");
    if (firstOkAfterBad) recoveries.push(+(firstOkAfterBad.t - r).toFixed(1));
    else recoveries.push("NEVER (stuck for run)");
  }

  process.stderr.write("\n\n========== STRESS SCORECARD ==========\n");
  process.stderr.write(`hub :${HUB_PORT}  project ${PROJECT}\n`);
  process.stderr.write(`duration ${SECONDS}s, ${total} calls, reloads @ ${RELOAD_AT.join("s, ")}s\n\n`);
  process.stderr.write("response classes:\n");
  for (const [k, v] of Object.entries(counts).sort((a, b) => b[1] - a[1]))
    process.stderr.write(`  ${k.padEnd(12)} ${v}  (${((v / total) * 100).toFixed(0)}%)\n`);
  process.stderr.write(`\nok latency: p50 ${p50}ms, p95 ${p95}ms\n`);
  process.stderr.write(`recovery after reload(s): ${recoveries.join(", ")}\n`);
  const cleanPct = (((counts.ok || 0) + (counts.busy || 0) + (counts.reloading || 0)) / total) * 100;
  process.stderr.write(`\nCLEAN responses (ok/busy/reloading): ${cleanPct.toFixed(0)}%\n`);
  process.stderr.write(`CONFUSING failures (refused/500/timeout/no-instance): ${(100 - cleanPct).toFixed(0)}%\n`);
  process.stderr.write("======================================\n");
}
