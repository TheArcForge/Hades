import { describe, it, expect, beforeEach, afterEach } from "vitest";
import Database from "better-sqlite3";
import { TracesDB } from "../src/db.js";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";

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

  db.prepare(`INSERT INTO traces (trace_id, root_span_name, start_time, end_time, status, total_duration_ms, span_count)
    VALUES (?, ?, ?, ?, ?, ?, ?)`).run("trace_aaa", "mcp.tool.hades_ping", 1000, 2000, "Ok", 1000, 2);

  db.prepare(`INSERT INTO traces (trace_id, root_span_name, start_time, end_time, status, total_duration_ms, span_count)
    VALUES (?, ?, ?, ?, ?, ?, ?)`).run("trace_bbb", "mcp.tool.search_by_name", 3000, 4500, "Error", 1500, 3);

  db.prepare(`INSERT INTO traces (trace_id, root_span_name, start_time, end_time, status, total_duration_ms, span_count)
    VALUES (?, ?, ?, ?, ?, ?, ?)`).run("trace_ccc", "lifecycle.startup", 5000, 5100, "Ok", 100, 1);

  db.prepare(`INSERT INTO spans (span_id, trace_id, parent_span_id, name, kind, start_time, end_time, status, attributes, events)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`).run("span_1", "trace_aaa", null, "mcp.tool.hades_ping", "Server", 1000, 2000, "Ok", '{"tool.name":"hades_ping"}', null);

  db.prepare(`INSERT INTO spans (span_id, trace_id, parent_span_id, name, kind, start_time, end_time, status, attributes, events)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`).run("span_2", "trace_aaa", "span_1", "graph.query.find_by_type", "Internal", 1100, 1500, "Ok", '{"query.type":"Scene","results.count":"3"}', null);

  db.close();
  return dbPath;
}

describe("TracesDB", () => {
  let tmpDir: string;
  let dbPath: string;
  let tracesDb: TracesDB;

  beforeEach(() => {
    tmpDir = mkdtempSync(join(tmpdir(), "charon-test-"));
    dbPath = createTestDb(tmpDir);
    tracesDb = new TracesDB(dbPath);
  });

  afterEach(() => {
    tracesDb.close();
    rmSync(tmpDir, { recursive: true, force: true });
  });

  it("listTraces returns traces in reverse chronological order", () => {
    const traces = tracesDb.listTraces({ limit: 10 });
    expect(traces).toHaveLength(3);
    expect(traces[0].trace_id).toBe("trace_ccc");
    expect(traces[2].trace_id).toBe("trace_aaa");
  });

  it("listTraces respects limit", () => {
    const traces = tracesDb.listTraces({ limit: 2 });
    expect(traces).toHaveLength(2);
  });

  it("listTraces filters by status", () => {
    const traces = tracesDb.listTraces({ limit: 10, status: "Error" });
    expect(traces).toHaveLength(1);
    expect(traces[0].trace_id).toBe("trace_bbb");
  });

  it("listTraces filters by name pattern", () => {
    const traces = tracesDb.listTraces({ limit: 10, namePattern: "%ping%" });
    expect(traces).toHaveLength(1);
    expect(traces[0].root_span_name).toBe("mcp.tool.hades_ping");
  });

  it("getTrace returns a single trace", () => {
    const trace = tracesDb.getTrace("trace_aaa");
    expect(trace).not.toBeNull();
    expect(trace!.root_span_name).toBe("mcp.tool.hades_ping");
    expect(trace!.total_duration_ms).toBe(1000);
    expect(trace!.span_count).toBe(2);
  });

  it("getTrace returns null for unknown id", () => {
    const trace = tracesDb.getTrace("nonexistent");
    expect(trace).toBeNull();
  });

  it("getSpans returns spans for a trace ordered by start_time", () => {
    const spans = tracesDb.getSpans("trace_aaa");
    expect(spans).toHaveLength(2);
    expect(spans[0].name).toBe("mcp.tool.hades_ping");
    expect(spans[0].parent_span_id).toBeNull();
    expect(spans[1].name).toBe("graph.query.find_by_type");
    expect(spans[1].parent_span_id).toBe("span_1");
  });

  it("getSpans returns empty array for unknown trace", () => {
    const spans = tracesDb.getSpans("nonexistent");
    expect(spans).toHaveLength(0);
  });

  it("getSpans parses attributes JSON", () => {
    const spans = tracesDb.getSpans("trace_aaa");
    expect(spans[0].attributes).toEqual({ "tool.name": "hades_ping" });
    expect(spans[1].attributes).toEqual({ "query.type": "Scene", "results.count": "3" });
  });

  it("countTraces returns total matching count", () => {
    const total = tracesDb.countTraces({});
    expect(total).toBe(3);
  });

  it("countTraces filters by status", () => {
    const total = tracesDb.countTraces({ status: "Ok" });
    expect(total).toBe(2);
  });

  it("countTraces filters by name pattern", () => {
    const total = tracesDb.countTraces({ namePattern: "%ping%" });
    expect(total).toBe(1);
  });
});
