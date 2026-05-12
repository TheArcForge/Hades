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
export interface ListTracesOptions {
    limit: number;
    offset?: number;
    status?: string;
    namePattern?: string;
}
export declare class TracesDB {
    private db;
    constructor(dbPath: string);
    listTraces(opts: ListTracesOptions): TraceRow[];
    getTrace(traceId: string): TraceRow | null;
    getSpans(traceId: string): SpanRow[];
    countTraces(opts: {
        status?: string;
        namePattern?: string;
    }): number;
    close(): void;
}
