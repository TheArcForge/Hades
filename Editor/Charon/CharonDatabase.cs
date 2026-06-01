// Editor/Charon/CharonDatabase.cs
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using SQLite;

namespace ArcForge.Hades.Editor.Charon
{
    public class CharonDatabase : IDisposable
    {
        readonly SQLiteConnection _connection;
        bool _disposed;

        public CharonDatabase(string dbPath)
        {
            var dir = System.IO.Path.GetDirectoryName(dbPath);
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            _connection = new SQLiteConnection(dbPath);
            ApplyPragmas();
            InitializeSchema();
        }

        void ApplyPragmas()
        {
            _connection.ExecuteScalar<string>("PRAGMA journal_mode = WAL;");
            _connection.Execute("PRAGMA synchronous = NORMAL;");
            _connection.Execute("PRAGMA busy_timeout = 5000;");
            _connection.Execute("PRAGMA cache_size = -16384;");
            _connection.Execute("PRAGMA temp_store = MEMORY;");
            _connection.Execute("PRAGMA foreign_keys = ON;");
        }

        void InitializeSchema()
        {
            _connection.ExecuteScript(@"
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
                CREATE INDEX IF NOT EXISTS idx_traces_status ON traces(status);

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
                CREATE INDEX IF NOT EXISTS idx_spans_name ON spans(name);

                CREATE TABLE IF NOT EXISTS eval_datasets (
                    dataset_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT,
                    created_at INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS eval_dataset_members (
                    dataset_id TEXT NOT NULL REFERENCES eval_datasets(dataset_id) ON DELETE CASCADE,
                    trace_id TEXT REFERENCES traces(trace_id) ON DELETE SET NULL,
                    tool_name TEXT NOT NULL,
                    input_json TEXT NOT NULL,
                    expected_output_json TEXT NOT NULL,
                    notes TEXT,
                    PRIMARY KEY (dataset_id, tool_name, input_json)
                );
            ");
        }

        public bool TableExists(string tableName)
        {
            var count = _connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?;", tableName);
            return count > 0;
        }

        public void InsertTrace(TraceRecord trace)
        {
            _connection.Execute(@"
                INSERT OR REPLACE INTO traces (trace_id, root_span_name, start_time, end_time, status, total_duration_ms, span_count, attributes)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?);",
                trace.TraceId,
                trace.RootSpanName,
                trace.StartTime,
                trace.EndTime.HasValue ? (object)trace.EndTime.Value : null,
                trace.Status.ToString(),
                trace.TotalDurationMs.HasValue ? (object)trace.TotalDurationMs.Value : null,
                trace.SpanCount,
                trace.AttributesJson);
        }

        public void InsertSpan(SpanRecord span)
        {
            _connection.Execute(@"
                INSERT OR REPLACE INTO spans (span_id, trace_id, parent_span_id, name, kind, start_time, end_time, status, attributes, events)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                span.SpanId,
                span.TraceId,
                span.ParentSpanId,
                span.Name,
                span.Kind.ToString(),
                span.StartTime,
                span.EndTime.HasValue ? (object)span.EndTime.Value : null,
                span.Status.ToString(),
                span.AttributesJson,
                span.EventsJson);
        }

        public void InsertSpans(List<SpanRecord> spans)
        {
            _connection.RunInTransaction(() =>
            {
                foreach (var span in spans)
                    InsertSpan(span);
            });
        }

        /// <summary>
        /// Runs <paramref name="action"/> inside a single transaction. Lets the
        /// emitter batch an entire flush (many trace upserts + span inserts) into
        /// one commit instead of dozens, which avoids stacking autocheckpoint
        /// passes against a large file on the main thread. Nested calls (e.g.
        /// InsertSpans) use savepoints, so this composes safely.
        /// </summary>
        public void RunInTransaction(Action action)
        {
            _connection.RunInTransaction(action);
        }

        public TraceRecord GetTrace(string traceId)
        {
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT trace_id, root_span_name, start_time, end_time, status, total_duration_ms, span_count, attributes FROM traces WHERE trace_id = ?;"))
            {
                stmt.Bind(1, traceId);
                if (stmt.Step() == SQLite3.Result.Row)
                    return ReadTraceFromStatement(stmt);
                return null;
            }
        }

        public List<SpanRecord> GetSpansByTraceId(string traceId)
        {
            var results = new List<SpanRecord>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT span_id, trace_id, parent_span_id, name, kind, start_time, end_time, status, attributes, events FROM spans WHERE trace_id = ? ORDER BY start_time;"))
            {
                stmt.Bind(1, traceId);
                while (stmt.Step() == SQLite3.Result.Row)
                    results.Add(ReadSpanFromStatement(stmt));
            }
            return results;
        }

        public List<TraceRecord> ListTraces(int limit, string statusFilter = null, string namePattern = null)
        {
            var results = new List<TraceRecord>();
            var sql = "SELECT trace_id, root_span_name, start_time, end_time, status, total_duration_ms, span_count, attributes FROM traces";
            var conditions = new List<string>();
            var args = new List<object>();

            if (statusFilter != null)
            {
                conditions.Add("status = ?");
                args.Add(statusFilter);
            }
            if (namePattern != null)
            {
                conditions.Add("root_span_name LIKE ?");
                args.Add(namePattern);
            }

            if (conditions.Count > 0)
                sql += " WHERE " + string.Join(" AND ", conditions);

            sql += " ORDER BY start_time DESC LIMIT ?;";
            args.Add(limit);

            using (var stmt = new SQLitePreparedStatement(_connection, sql))
            {
                for (int i = 0; i < args.Count; i++)
                {
                    if (args[i] is int intVal)
                        stmt.Bind(i + 1, intVal);
                    else if (args[i] is long longVal)
                        stmt.Bind(i + 1, longVal);
                    else
                        stmt.Bind(i + 1, args[i]?.ToString() ?? "");
                }

                while (stmt.Step() == SQLite3.Result.Row)
                    results.Add(ReadTraceFromStatement(stmt));
            }
            return results;
        }

        public void UpdateTraceEnd(string traceId, long endTime, SpanStatus status, int spanCount)
        {
            var startTime = _connection.ExecuteScalar<long>(
                "SELECT start_time FROM traces WHERE trace_id = ?;", traceId);
            var durationMs = endTime - startTime;

            _connection.Execute(@"
                UPDATE traces SET end_time = ?, status = ?, total_duration_ms = ?, span_count = ?
                WHERE trace_id = ?;",
                endTime, status.ToString(), durationMs, spanCount, traceId);
        }

        public int PruneOlderThan(int retentionDays)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds();
            return _connection.Execute("DELETE FROM traces WHERE start_time < ?;", cutoff);
        }

        /// <summary>
        /// Enforces a hard on-disk size cap for the trace DB. Time-based pruning
        /// alone let traces.db grow into the multi-GB range on a large, heavily-used
        /// project; this is the backstop.
        /// When the file exceeds <paramref name="maxBytes"/>, drops the oldest
        /// traces (spans cascade-delete) down to ~90% of the budget in a single
        /// pass, then checkpoints and VACUUMs to actually reclaim disk space.
        /// Returns the number of traces deleted. Runs at startup, never mid-rebuild.
        /// </summary>
        public int EnforceSizeLimit(string dbPath, long maxBytes)
        {
            if (maxBytes <= 0) return 0;

            long sizeBytes;
            try { sizeBytes = new System.IO.FileInfo(dbPath).Length; }
            catch { return 0; }

            if (sizeBytes <= maxBytes) return 0;

            var total = _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM traces;");
            if (total == 0) return 0;

            // Estimate how many of the newest traces fit in ~90% of the budget and
            // drop the rest. Trace size is roughly uniform, so scaling by the
            // size ratio gets us under the cap in one pass without per-row probing.
            long keep = (long)(total * ((double)maxBytes / sizeBytes) * 0.9);
            long toDelete = total - keep;
            if (toDelete <= 0) return 0;

            var deleted = _connection.Execute(@"
                DELETE FROM traces WHERE trace_id IN (
                    SELECT trace_id FROM traces ORDER BY start_time ASC LIMIT ?);", toDelete);

            if (deleted > 0)
            {
                _connection.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
                _connection.Execute("VACUUM;");
            }
            return deleted;
        }

        public void InsertEvalDataset(EvalDataset dataset)
        {
            _connection.Execute(@"
                INSERT OR REPLACE INTO eval_datasets (dataset_id, name, description, created_at)
                VALUES (?, ?, ?, ?);",
                dataset.DatasetId, dataset.Name, dataset.Description, dataset.CreatedAt);
        }

        public EvalDataset GetEvalDataset(string datasetId)
        {
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT dataset_id, name, description, created_at FROM eval_datasets WHERE dataset_id = ?;"))
            {
                stmt.Bind(1, datasetId);
                if (stmt.Step() == SQLite3.Result.Row)
                {
                    return new EvalDataset
                    {
                        DatasetId = stmt.GetString(0),
                        Name = stmt.GetString(1),
                        Description = stmt.GetString(2),
                        CreatedAt = stmt.GetLong(3)
                    };
                }
                return null;
            }
        }

        public List<EvalDataset> ListEvalDatasets()
        {
            var results = new List<EvalDataset>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT dataset_id, name, description, created_at FROM eval_datasets ORDER BY created_at DESC;"))
            {
                while (stmt.Step() == SQLite3.Result.Row)
                {
                    results.Add(new EvalDataset
                    {
                        DatasetId = stmt.GetString(0),
                        Name = stmt.GetString(1),
                        Description = stmt.GetString(2),
                        CreatedAt = stmt.GetLong(3)
                    });
                }
            }
            return results;
        }

        public void InsertEvalDatasetMember(EvalDatasetMember member)
        {
            _connection.Execute(@"
                INSERT OR REPLACE INTO eval_dataset_members (dataset_id, trace_id, tool_name, input_json, expected_output_json, notes)
                VALUES (?, ?, ?, ?, ?, ?);",
                member.DatasetId, member.TraceId, member.ToolName, member.InputJson, member.ExpectedOutputJson, member.Notes);
        }

        public List<EvalDatasetMember> GetEvalDatasetMembers(string datasetId)
        {
            var results = new List<EvalDatasetMember>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT dataset_id, trace_id, tool_name, input_json, expected_output_json, notes FROM eval_dataset_members WHERE dataset_id = ?;"))
            {
                stmt.Bind(1, datasetId);
                while (stmt.Step() == SQLite3.Result.Row)
                {
                    results.Add(new EvalDatasetMember
                    {
                        DatasetId = stmt.GetString(0),
                        TraceId = stmt.GetString(1),
                        ToolName = stmt.GetString(2),
                        InputJson = stmt.GetString(3),
                        ExpectedOutputJson = stmt.GetString(4),
                        Notes = stmt.GetString(5)
                    });
                }
            }
            return results;
        }

        public void DeleteEvalDataset(string datasetId)
        {
            _connection.Execute("DELETE FROM eval_dataset_members WHERE dataset_id = ?;", datasetId);
            _connection.Execute("DELETE FROM eval_datasets WHERE dataset_id = ?;", datasetId);
        }

        TraceRecord ReadTraceFromStatement(SQLitePreparedStatement stmt)
        {
            var statusStr = stmt.GetString(4);
            SpanStatus status;
            if (!Enum.TryParse(statusStr, true, out status))
                status = SpanStatus.Unset;

            return new TraceRecord
            {
                TraceId = stmt.GetString(0),
                RootSpanName = stmt.GetString(1),
                StartTime = stmt.GetLong(2),
                EndTime = stmt.GetString(3) != null ? (long?)stmt.GetLong(3) : null,
                Status = status,
                SpanCount = stmt.GetString(6) != null ? (int)stmt.GetLong(6) : 0,
                AttributesJson = stmt.GetString(7)
            };
        }

        SpanRecord ReadSpanFromStatement(SQLitePreparedStatement stmt)
        {
            var statusStr = stmt.GetString(7);
            SpanStatus status;
            if (!Enum.TryParse(statusStr, true, out status))
                status = SpanStatus.Unset;

            var kindStr = stmt.GetString(4);
            SpanKind kind;
            if (!Enum.TryParse(kindStr, true, out kind))
                kind = SpanKind.Internal;

            var record = new SpanRecord
            {
                SpanId = stmt.GetString(0),
                TraceId = stmt.GetString(1),
                ParentSpanId = stmt.GetString(2),
                Name = stmt.GetString(3),
                Kind = kind,
                StartTime = stmt.GetLong(5),
                EndTime = stmt.GetString(6) != null ? (long?)stmt.GetLong(6) : null,
                Status = status
            };

            var attrsJson = stmt.GetString(8);
            if (attrsJson != null)
            {
                var attrs = JsonConvert.DeserializeObject<Dictionary<string, string>>(attrsJson);
                if (attrs != null)
                    foreach (var kv in attrs)
                        record.Attributes[kv.Key] = kv.Value;
            }

            return record;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connection?.Close();
        }
    }
}
