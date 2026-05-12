import Database from "better-sqlite3";
export class TracesDB {
    db;
    constructor(dbPath) {
        this.db = new Database(dbPath, { readonly: true });
        this.db.pragma("journal_mode = WAL");
    }
    listTraces(opts) {
        const conditions = [];
        const params = [];
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
        return this.db.prepare(sql).all(...params);
    }
    getTrace(traceId) {
        const row = this.db.prepare("SELECT trace_id, root_span_name, start_time, end_time, status, total_duration_ms, span_count, attributes FROM traces WHERE trace_id = ?").get(traceId);
        return row ?? null;
    }
    getSpans(traceId) {
        const rows = this.db.prepare("SELECT span_id, trace_id, parent_span_id, name, kind, start_time, end_time, status, attributes, events FROM spans WHERE trace_id = ? ORDER BY start_time").all(traceId);
        return rows.map((row) => ({
            ...row,
            attributes: row.attributes ? JSON.parse(row.attributes) : null,
            events: row.events ? JSON.parse(row.events) : null,
        }));
    }
    countTraces(opts) {
        const conditions = [];
        const params = [];
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
        const row = this.db.prepare(sql).get(...params);
        return row.cnt;
    }
    close() {
        this.db.close();
    }
}
//# sourceMappingURL=db.js.map