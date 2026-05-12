import { describe, it, expect, beforeEach, afterEach } from "vitest";
import Database from "better-sqlite3";
import express from "express";
import { createTracesRouter } from "../src/api/traces.js";
import { TracesDB } from "../src/db.js";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import http from "http";

function createTestDb(dir: string): string {
  const dbPath = join(dir, "traces.db");
  const db = new Database(dbPath);
  db.pragma("journal_mode = WAL");

  db.exec(`
    CREATE TABLE IF NOT EXISTS traces (
      trace_id TEXT PRIMARY KEY,
      root_span_name TEXT NOT NULL,
      start_time INTEGER NOT NULL,
      end_time INTEGER,
      status TEXT,
      total_duration_ms INTEGER,
      span_count INTEGER,
      attributes TEXT
    );
    CREATE INDEX IF NOT EXISTS idx_traces_start_time ON traces(start_time DESC);

    CREATE TABLE IF NOT EXISTS spans (
      span_id TEXT PRIMARY KEY,
      trace_id TEXT NOT NULL REFERENCES traces(trace_id) ON DELETE CASCADE,
      parent_span_id TEXT,
      name TEXT NOT NULL,
      kind TEXT NOT NULL,
      start_time INTEGER NOT NULL,
      end_time INTEGER,
      status TEXT,
      attributes TEXT,
      events TEXT
    );
    CREATE INDEX IF NOT EXISTS idx_spans_trace ON spans(trace_id, start_time);
  `);

  db.prepare(`INSERT INTO traces VALUES (?, ?, ?, ?, ?, ?, ?, ?)`).run(
    "trace_001", "mcp.tool.hades_ping", 1000, 2000, "Ok", 1000, 1, null
  );
  db.prepare(`INSERT INTO spans VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`).run(
    "span_001", "trace_001", null, "mcp.tool.hades_ping", "Server", 1000, 2000, "Ok", '{"tool.name":"hades_ping"}', null
  );

  db.close();
  return dbPath;
}

function fetch(url: string): Promise<{ status: number; body: unknown }> {
  return new Promise((resolve, reject) => {
    http.get(url, (res) => {
      let data = "";
      res.on("data", (chunk) => (data += chunk));
      res.on("end", () => {
        resolve({ status: res.statusCode!, body: JSON.parse(data) });
      });
    }).on("error", reject);
  });
}

describe("traces API", () => {
  let tmpDir: string;
  let tracesDb: TracesDB;
  let server: http.Server;
  let port: number;

  beforeEach(async () => {
    tmpDir = mkdtempSync(join(tmpdir(), "charon-api-test-"));
    const dbPath = createTestDb(tmpDir);
    tracesDb = new TracesDB(dbPath);

    const app = express();
    app.use("/api", createTracesRouter(tracesDb));

    await new Promise<void>((resolve) => {
      server = app.listen(0, "127.0.0.1", () => {
        port = (server.address() as { port: number }).port;
        resolve();
      });
    });
  });

  afterEach(async () => {
    await new Promise<void>((resolve) => server.close(() => resolve()));
    tracesDb.close();
    rmSync(tmpDir, { recursive: true, force: true });
  });

  it("GET /api/traces returns trace list", async () => {
    const { status, body } = await fetch(`http://127.0.0.1:${port}/api/traces`);
    expect(status).toBe(200);
    const data = body as { traces: unknown[]; total: number };
    expect(data.traces).toHaveLength(1);
    expect(data.total).toBe(1);
  });

  it("GET /api/traces respects limit param", async () => {
    const { body } = await fetch(`http://127.0.0.1:${port}/api/traces?limit=0`);
    const data = body as { traces: unknown[] };
    expect(data.traces).toHaveLength(0);
  });

  it("GET /api/traces filters by status", async () => {
    const { body } = await fetch(`http://127.0.0.1:${port}/api/traces?status=Error`);
    const data = body as { traces: unknown[] };
    expect(data.traces).toHaveLength(0);
  });

  it("GET /api/traces/:id returns trace with spans", async () => {
    const { status, body } = await fetch(`http://127.0.0.1:${port}/api/traces/trace_001`);
    expect(status).toBe(200);
    const data = body as { trace: { trace_id: string }; spans: unknown[] };
    expect(data.trace.trace_id).toBe("trace_001");
    expect(data.spans).toHaveLength(1);
  });

  it("GET /api/traces/:id returns 404 for unknown", async () => {
    const { status } = await fetch(`http://127.0.0.1:${port}/api/traces/nonexistent`);
    expect(status).toBe(404);
  });
});
