import Database from "better-sqlite3";

export interface TraceRow {
  trace_id: string;
  root_span_name: string;
  start_time: number;
  end_time: number | null;
  status: string | null;
  total_duration_ms: number | null;
  span_count: number | null;
  attributes: string | null;
}

export interface SpanRow {
  span_id: string;
  trace_id: string;
  parent_span_id: string | null;
  name: string;
  kind: string;
  start_time: number;
  end_time: number | null;
  status: string | null;
  attributes: Record<string, string> | null;
  events: unknown[] | null;
}

interface SpanRowRaw {
  span_id: string;
  trace_id: string;
  parent_span_id: string | null;
  name: string;
  kind: string;
  start_time: number;
  end_time: number | null;
  status: string | null;
  attributes: string | null;
  events: string | null;
}

export interface ListTracesOptions {
  limit: number;
  offset?: number;
  status?: string;
  namePattern?: string;
}

export class TracesDB {
  private db: Database.Database;

  constructor(dbPath: string) {
    this.db = new Database(dbPath, { readonly: true });
    this.db.pragma("journal_mode = WAL");
  }

  listTraces(opts: ListTracesOptions): TraceRow[] {
    const conditions: string[] = [];
    const params: unknown[] = [];

    if (opts.status) {
      conditions.push("status = ?");
      params.push(opts.status);
    }
    if (opts.namePattern) {
      conditions.push("root_span_name LIKE ?");
      params.push(opts.namePattern);
    }

    let sql = "SELECT trace_id, root_span_name, start_time, end_time, status, total_duration_ms, span_count, attributes FROM traces";
    if (conditions.length > 0) {
      sql += " WHERE " + conditions.join(" AND ");
    }
    sql += " ORDER BY start_time DESC LIMIT ? OFFSET ?";
    params.push(opts.limit);
    params.push(opts.offset ?? 0);

    return this.db.prepare(sql).all(...params) as TraceRow[];
  }

  getTrace(traceId: string): TraceRow | null {
    const row = this.db.prepare(
      "SELECT trace_id, root_span_name, start_time, end_time, status, total_duration_ms, span_count, attributes FROM traces WHERE trace_id = ?"
    ).get(traceId) as TraceRow | undefined;
    return row ?? null;
  }

  getSpans(traceId: string): SpanRow[] {
    const rows = this.db.prepare(
      "SELECT span_id, trace_id, parent_span_id, name, kind, start_time, end_time, status, attributes, events FROM spans WHERE trace_id = ? ORDER BY start_time"
    ).all(traceId) as SpanRowRaw[];

    return rows.map((row) => ({
      ...row,
      attributes: row.attributes ? JSON.parse(row.attributes) : null,
      events: row.events ? JSON.parse(row.events) : null,
    }));
  }

  countTraces(opts: { status?: string; namePattern?: string }): number {
    const conditions: string[] = [];
    const params: unknown[] = [];

    if (opts.status) {
      conditions.push("status = ?");
      params.push(opts.status);
    }
    if (opts.namePattern) {
      conditions.push("root_span_name LIKE ?");
      params.push(opts.namePattern);
    }

    let sql = "SELECT COUNT(*) as cnt FROM traces";
    if (conditions.length > 0) {
      sql += " WHERE " + conditions.join(" AND ");
    }

    const row = this.db.prepare(sql).get(...params) as { cnt: number };
    return row.cnt;
  }

  close(): void {
    this.db.close();
  }
}
