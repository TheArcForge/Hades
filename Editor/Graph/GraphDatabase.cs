// Editor/Graph/GraphDatabase.cs
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using SQLite;
using ArcForge.Hades.Editor.Charon;
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Graph
{
    public class GraphDatabase : IDisposable
    {
        static GraphDatabase _instance;
        public static GraphDatabase Instance => _instance;

        readonly SQLiteConnection _connection;
        bool _disposed;

        const int CurrentSchemaVersion = 2;

        public GraphDatabase(string dbPath)
        {
            var dir = System.IO.Path.GetDirectoryName(dbPath);
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            _connection = new SQLiteConnection(dbPath);

            ApplyPragmas();
            InitializeSchema();

            _instance = this;
        }

        void ApplyPragmas()
        {
            // journal_mode returns the new mode; use ExecuteScalar to consume the result row
            _connection.ExecuteScalar<string>("PRAGMA journal_mode = WAL;");
            _connection.Execute("PRAGMA synchronous = NORMAL;");
            _connection.Execute("PRAGMA busy_timeout = 5000;");
            _connection.Execute("PRAGMA cache_size = -65536;");
            _connection.Execute("PRAGMA temp_store = MEMORY;");
            _connection.Execute("PRAGMA mmap_size = 268435456;");
            _connection.Execute("PRAGMA foreign_keys = ON;");
            // Bound WAL growth between full checkpoints: fire automatic PASSIVE
            // checkpoints as the log accumulates (1000 pages ≈ 4 MB) and cap the
            // on-disk WAL file size after checkpoint. The explicit TRUNCATE at
            // rebuild finalize (Checkpoint()) is retained for a hard reset.
            _connection.Execute("PRAGMA wal_autocheckpoint = 1000;");
            _connection.Execute("PRAGMA journal_size_limit = 67108864;");
        }

        void InitializeSchema()
        {
            if (!TableExists("schema_version"))
            {
                CreateAllTables();
                RecordSchemaVersion(CurrentSchemaVersion);
            }
            else
            {
                var currentVersion = _connection.ExecuteScalar<long>("SELECT MAX(version) FROM schema_version;");
                if (currentVersion < CurrentSchemaVersion)
                    MigrateSchema(currentVersion);
            }
        }

        void CreateAllTables()
        {
            _connection.ExecuteScript(@"
                CREATE TABLE IF NOT EXISTS nodes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    type TEXT NOT NULL,
                    tier TEXT NOT NULL DEFAULT 'project',
                    guid TEXT,
                    file_id INTEGER,
                    parent_node_id INTEGER REFERENCES nodes(id),
                    name TEXT,
                    path TEXT,
                    source_range TEXT,
                    properties TEXT,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_nodes_type ON nodes(type);
                CREATE INDEX IF NOT EXISTS idx_nodes_guid ON nodes(guid);
                CREATE INDEX IF NOT EXISTS idx_nodes_path ON nodes(path);
                CREATE INDEX IF NOT EXISTS idx_nodes_parent ON nodes(parent_node_id);
                CREATE INDEX IF NOT EXISTS idx_nodes_name_type ON nodes(name, type);
                CREATE INDEX IF NOT EXISTS idx_nodes_tier ON nodes(tier);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_nodes_guid_fileid ON nodes(guid, file_id) WHERE guid IS NOT NULL;

                CREATE TABLE IF NOT EXISTS edges (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    source_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
                    target_id INTEGER NOT NULL REFERENCES nodes(id) ON DELETE CASCADE,
                    type TEXT NOT NULL,
                    properties TEXT,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_edges_source_type ON edges(source_id, type);
                CREATE INDEX IF NOT EXISTS idx_edges_target_type ON edges(target_id, type);
                CREATE INDEX IF NOT EXISTS idx_edges_type ON edges(type);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_edges_unique ON edges(source_id, target_id, type);

                CREATE TABLE IF NOT EXISTS pending_edges (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    source_node_id INTEGER NOT NULL,
                    edge_type TEXT NOT NULL,
                    target_type_name TEXT NOT NULL,
                    target_namespace TEXT,
                    source_asset_guid TEXT,
                    created_at INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_pending_edges_target ON pending_edges(target_type_name);
                CREATE INDEX IF NOT EXISTS idx_pending_edges_source_asset ON pending_edges(source_asset_guid);

                CREATE TABLE IF NOT EXISTS schema_version (
                    version INTEGER PRIMARY KEY,
                    applied_at INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS scanned_assets (
                    guid TEXT PRIMARY KEY,
                    content_hash TEXT NOT NULL,
                    scanned_at INTEGER NOT NULL,
                    scanner_version INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS pending_invalidations (
                    guid TEXT PRIMARY KEY,
                    invalidated_at INTEGER NOT NULL,
                    reason TEXT
                );

                CREATE TABLE IF NOT EXISTS graph_metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
            ");
        }

        void RecordSchemaVersion(int version)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _connection.Execute($"INSERT INTO schema_version (version, applied_at) VALUES ({version}, {now});");
        }

        void MigrateSchema(long fromVersion)
        {
            if (fromVersion < 2)
            {
                // Graph is a rebuildable cache — safest migration is to recreate tables
                // with correct column ordering. ALTER TABLE ADD COLUMN puts columns at the
                // end which breaks our positional SELECT * reads.
                _connection.ExecuteScript(@"
                    DROP TABLE IF EXISTS edges;
                    DROP TABLE IF EXISTS nodes;
                    DROP TABLE IF EXISTS scanned_assets;
                    DROP TABLE IF EXISTS pending_invalidations;
                ");

                CreateAllTables();
                RecordSchemaVersion(2);

                // Graph will be fully rebuilt on next startup via CheckStartupSync
            }
        }

        public bool TableExists(string tableName)
        {
            var count = _connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?;", tableName);
            return count > 0;
        }

        public void SetMetadata(string key, string value)
        {
            _connection.Execute(
                "INSERT OR REPLACE INTO graph_metadata (key, value) VALUES (?, ?);", key, value);
        }

        public string GetMetadata(string key)
        {
            var results = _connection.QueryScalars<string>(
                "SELECT value FROM graph_metadata WHERE key = ?;", key);
            return results.Count > 0 ? results[0] : null;
        }

        public bool IsRebuildInProgress()
        {
            var value = GetMetadata("current_operation");
            return value != null;
        }

        public void SetCurrentOperation(string kind, string[] affectedGuids = null)
        {
            var op = new
            {
                kind,
                started_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                affected_guids = affectedGuids ?? new string[0]
            };
            SetMetadata("current_operation", JsonConvert.SerializeObject(op));
        }

        public void ClearCurrentOperation()
        {
            _connection.Execute("DELETE FROM graph_metadata WHERE key = 'current_operation';");
        }

        /// <summary>
        /// Forces a WAL checkpoint, flushing the write-ahead log back into the main
        /// database file and truncating the WAL. After a large rebuild the WAL can
        /// grow to hundreds of MB; if we let SQLite auto-checkpoint it lazily on the
        /// next write, that write blocks the main thread for minutes with no progress
        /// bar (the "mystery freeze" from the field report). Calling this explicitly
        /// while a progress bar is still visible keeps the freeze observable and
        /// bounded. TRUNCATE mode also resets the WAL file size to zero on disk.
        /// </summary>
        public void Checkpoint()
        {
            _connection.ExecuteScalar<string>("PRAGMA wal_checkpoint(TRUNCATE);");
        }

        /// <summary>
        /// Passive WAL checkpoint: integrates committed WAL frames — including those
        /// written by the out-of-process Node scanner — into the main database file
        /// through this connection, without truncating the WAL or blocking writers.
        /// Called right after the scanner subprocess exits so its project-script
        /// frames are durably merged before the C# side does any further writes or
        /// the final TRUNCATE checkpoint; otherwise that TRUNCATE can discard the
        /// not-yet-integrated frames, dropping every project script on a full rebuild.
        /// </summary>
        public void CheckpointPassive()
        {
            _connection.ExecuteScalar<string>("PRAGMA wal_checkpoint(PASSIVE);");
        }

        // --- Low-level SQL helpers (public for GraphBuilder, tools, and tests) ---

        public T ExecuteScalar<T>(string sql, params object[] args)
        {
            return _connection.ExecuteScalar<T>(sql, args);
        }

        public int Execute(string sql, params object[] args)
        {
            return _connection.Execute(sql, args);
        }

        public void RunInTransaction(Action action)
        {
            _connection.RunInTransaction(action);
        }

        // --- Node & Edge CRUD ---

        public long InsertNode(NodeRecord node, string tier = "project")
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _connection.Execute(@"
                INSERT INTO nodes (type, tier, guid, file_id, parent_node_id, name, path, source_range, properties, created_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                node.Type,
                tier,
                node.Guid,
                node.FileId.HasValue ? (object)node.FileId.Value : null,
                node.ParentNodeId.HasValue ? (object)node.ParentNodeId.Value : null,
                node.Name,
                node.Path,
                node.SourceRange,
                node.PropertiesJson,
                now,
                now);

            // Direct C call instead of a fresh "SELECT last_insert_rowid()" statement.
            // A full rebuild on a large project inserts hundreds of thousands of nodes;
            // preparing+stepping+finalizing a throwaway statement for each one was an
            // equal number of redundant statements. This reads the value straight from
            // the connection handle.
            return SQLite3.LastInsertRowid(_connection.Handle);
        }

        public long InsertEdge(long sourceId, long targetId, string type, string propertiesJson = null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _connection.Execute(@"
                INSERT OR IGNORE INTO edges (source_id, target_id, type, properties, created_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?);",
                sourceId,
                targetId,
                type,
                propertiesJson,
                now,
                now);

            // See InsertNode: avoid a throwaway last_insert_rowid() statement per edge.
            return SQLite3.LastInsertRowid(_connection.Handle);
        }

        public NodeRecord FindNodeByGuid(string guid, long? fileId = null)
        {
            using (var span = CharonEmitter.StartSpan("graph.query.find_by_guid", SpanKind.Internal))
            {
                span.SetAttribute("query.guid", guid ?? "");
                if (fileId.HasValue) span.SetAttribute("query.file_id", fileId.Value);

                if (fileId.HasValue)
                {
                    using (var stmt = new SQLitePreparedStatement(_connection,
                        "SELECT * FROM nodes WHERE guid = ? AND file_id = ?;"))
                    {
                        stmt.Bind(1, guid);
                        stmt.Bind(2, fileId.Value);
                        if (stmt.Step() == SQLite3.Result.Row)
                        {
                            span.SetAttribute("results.count", 1L);
                            return ReadNodeFromStatement(stmt);
                        }
                        span.SetAttribute("results.count", 0L);
                        return null;
                    }
                }
                else
                {
                    using (var stmt = new SQLitePreparedStatement(_connection,
                        "SELECT * FROM nodes WHERE guid = ? ORDER BY id LIMIT 1;"))
                    {
                        stmt.Bind(1, guid);
                        if (stmt.Step() == SQLite3.Result.Row)
                        {
                            span.SetAttribute("results.count", 1L);
                            return ReadNodeFromStatement(stmt);
                        }
                        span.SetAttribute("results.count", 0L);
                        return null;
                    }
                }
            }
        }

        public NodeRecord FindNodeById(long id)
        {
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT * FROM nodes WHERE id = ?;"))
            {
                stmt.Bind(1, id);
                if (stmt.Step() == SQLite3.Result.Row)
                    return ReadNodeFromStatement(stmt);
                return null;
            }
        }

        public List<NodeRecord> FindNodesByType(string type)
        {
            using (var span = CharonEmitter.StartSpan("graph.query.find_by_type", SpanKind.Internal))
            {
                span.SetAttribute("query.type", type);

                var results = new List<NodeRecord>();
                using (var stmt = new SQLitePreparedStatement(_connection,
                    "SELECT * FROM nodes WHERE type = ?;"))
                {
                    stmt.Bind(1, type);
                    while (stmt.Step() == SQLite3.Result.Row)
                        results.Add(ReadNodeFromStatement(stmt));
                }

                span.SetAttribute("results.count", (long)results.Count);
                return results;
            }
        }

        public List<EdgeInfo> FindEdgesFrom(long sourceId, string type = null)
        {
            using (var span = CharonEmitter.StartSpan("graph.query.find_edges_from", SpanKind.Internal))
            {
                span.SetAttribute("query.source_id", sourceId);
                if (type != null) span.SetAttribute("query.edge_type", type);

                var results = new List<EdgeInfo>();
                if (type != null)
                {
                    using (var stmt = new SQLitePreparedStatement(_connection,
                        "SELECT e.*, n.type as target_type, n.name as target_name, n.path as target_path FROM edges e JOIN nodes n ON e.target_id = n.id WHERE e.source_id = ? AND e.type = ?;"))
                    {
                        stmt.Bind(1, sourceId);
                        stmt.Bind(2, type);
                        while (stmt.Step() == SQLite3.Result.Row)
                            results.Add(ReadEdgeInfoFromStatement(stmt));
                    }
                }
                else
                {
                    using (var stmt = new SQLitePreparedStatement(_connection,
                        "SELECT e.*, n.type as target_type, n.name as target_name, n.path as target_path FROM edges e JOIN nodes n ON e.target_id = n.id WHERE e.source_id = ?;"))
                    {
                        stmt.Bind(1, sourceId);
                        while (stmt.Step() == SQLite3.Result.Row)
                            results.Add(ReadEdgeInfoFromStatement(stmt));
                    }
                }

                span.SetAttribute("results.count", (long)results.Count);
                return results;
            }
        }

        public List<EdgeInfo> FindEdgesTo(long targetId, string type = null)
        {
            using (var span = CharonEmitter.StartSpan("graph.query.find_edges_to", SpanKind.Internal))
            {
                span.SetAttribute("query.target_id", targetId);
                if (type != null) span.SetAttribute("query.edge_type", type);

                var results = new List<EdgeInfo>();
                if (type != null)
                {
                    using (var stmt = new SQLitePreparedStatement(_connection,
                        "SELECT e.*, n.type as target_type, n.name as target_name, n.path as target_path FROM edges e JOIN nodes n ON e.source_id = n.id WHERE e.target_id = ? AND e.type = ?;"))
                    {
                        stmt.Bind(1, targetId);
                        stmt.Bind(2, type);
                        while (stmt.Step() == SQLite3.Result.Row)
                            results.Add(ReadEdgeInfoFromStatement(stmt));
                    }
                }
                else
                {
                    using (var stmt = new SQLitePreparedStatement(_connection,
                        "SELECT e.*, n.type as target_type, n.name as target_name, n.path as target_path FROM edges e JOIN nodes n ON e.source_id = n.id WHERE e.target_id = ?;"))
                    {
                        stmt.Bind(1, targetId);
                        while (stmt.Step() == SQLite3.Result.Row)
                            results.Add(ReadEdgeInfoFromStatement(stmt));
                    }
                }

                span.SetAttribute("results.count", (long)results.Count);
                return results;
            }
        }

        public void DeleteNodesByGuid(string guid)
        {
            _connection.Execute("DELETE FROM nodes WHERE guid = ?;", guid);
        }

        public void UpdateNodePath(long nodeId, string newPath)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _connection.Execute("UPDATE nodes SET path = ?, updated_at = ? WHERE id = ?;",
                newPath, now, nodeId);
        }

        public void RecordScannedAsset(string guid, string contentHash, int scannerVersion)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _connection.Execute(@"
                INSERT OR REPLACE INTO scanned_assets (guid, content_hash, scanned_at, scanner_version)
                VALUES (?, ?, ?, ?);",
                guid, contentHash, now, scannerVersion);
        }

        public string GetScannedAssetHash(string guid)
        {
            var results = _connection.QueryScalars<string>(
                "SELECT content_hash FROM scanned_assets WHERE guid = ?;", guid);
            return results.Count > 0 ? results[0] : null;
        }

        public int? GetScannedAssetScannerVersion(string guid)
        {
            var results = _connection.QueryScalars<int>(
                "SELECT scanner_version FROM scanned_assets WHERE guid = ?;", guid);
            if (results.Count == 0) return null;
            return results[0];
        }

        NodeRecord ReadNodeFromStatement(SQLitePreparedStatement stmt)
        {
            // col 0: id, 1: type, 2: tier, 3: guid, 4: file_id, 5: parent_node_id,
            // 6: name, 7: path, 8: source_range, 9: properties
            var node = new NodeRecord(stmt.GetString(1), stmt.GetString(3))
            {
                Id = stmt.GetLong(0),
                FileId = stmt.GetString(4) != null ? (long?)stmt.GetLong(4) : null,
                ParentNodeId = stmt.GetString(5) != null ? (long?)stmt.GetLong(5) : null,
                Name = stmt.GetString(6),
                Path = stmt.GetString(7),
                SourceRange = stmt.GetString(8)
            };
            var propsJson = stmt.GetString(9);
            if (propsJson != null) node.PropertiesJson = propsJson;
            return node;
        }

        public class EdgeInfo
        {
            public long Id { get; set; }
            public long SourceNodeId { get; set; }
            public long TargetNodeId { get; set; }
            public string Type { get; set; }
            public string PropertiesJson { get; set; }
            public string TargetType { get; set; }
            public string TargetName { get; set; }
            public string TargetPath { get; set; }
        }

        EdgeInfo ReadEdgeInfoFromStatement(SQLitePreparedStatement stmt)
        {
            // e.*: 0: id, 1: source_id, 2: target_id, 3: type, 4: properties, 5: created_at, 6: updated_at
            // joined n.*: 7: target_type, 8: target_name, 9: target_path
            return new EdgeInfo
            {
                Id = stmt.GetLong(0),
                SourceNodeId = stmt.GetLong(1),
                TargetNodeId = stmt.GetLong(2),
                Type = stmt.GetString(3),
                PropertiesJson = stmt.GetString(4),
                TargetType = stmt.GetString(7),
                TargetName = stmt.GetString(8),
                TargetPath = stmt.GetString(9)
            };
        }

        // --- Query Methods ---

        public List<NodeRecord> SearchByName(string namePattern, string typeFilter = null)
        {
            using (var span = CharonEmitter.StartSpan("graph.query.search_by_name", SpanKind.Internal))
            {
                span.SetAttribute("query.pattern", namePattern ?? "%");
                if (typeFilter != null) span.SetAttribute("query.type_filter", typeFilter);

                var results = new List<NodeRecord>();
                var pattern = namePattern ?? "%";
                if (typeFilter != null)
                {
                    using (var stmt = new SQLitePreparedStatement(_connection,
                        "SELECT * FROM nodes WHERE name LIKE ? AND type = ? ORDER BY name;"))
                    {
                        stmt.Bind(1, pattern);
                        stmt.Bind(2, typeFilter);
                        while (stmt.Step() == SQLite3.Result.Row)
                            results.Add(ReadNodeFromStatement(stmt));
                    }
                }
                else
                {
                    using (var stmt = new SQLitePreparedStatement(_connection,
                        "SELECT * FROM nodes WHERE name LIKE ? ORDER BY name;"))
                    {
                        stmt.Bind(1, pattern);
                        while (stmt.Step() == SQLite3.Result.Row)
                            results.Add(ReadNodeFromStatement(stmt));
                    }
                }

                span.SetAttribute("results.count", (long)results.Count);
                return results;
            }
        }

        public List<NodeRecord> SearchByNameAdvanced(
            string namePattern, string typeFilter = null,
            string pathPrefix = null, string matchMode = "contains")
        {
            var results = new List<NodeRecord>();
            var sql = new System.Text.StringBuilder("SELECT * FROM nodes WHERE 1=1");
            var bindings = new List<(int pos, string val)>();
            int paramIndex = 1;

            string nameValue;
            switch (matchMode?.ToLowerInvariant() ?? "contains")
            {
                case "exact":
                    sql.Append(" AND name = ?");
                    nameValue = namePattern;
                    break;
                case "prefix":
                    sql.Append(" AND name LIKE ?");
                    nameValue = namePattern + "%";
                    break;
                default:
                    sql.Append(" AND name LIKE ?");
                    nameValue = "%" + namePattern + "%";
                    break;
            }
            bindings.Add((paramIndex++, nameValue));

            if (!string.IsNullOrEmpty(typeFilter))
            {
                sql.Append(" AND type = ?");
                bindings.Add((paramIndex++, typeFilter));
            }

            if (!string.IsNullOrEmpty(pathPrefix))
            {
                sql.Append(" AND path LIKE ?");
                bindings.Add((paramIndex++, pathPrefix + "%"));
            }

            sql.Append(" ORDER BY name LIMIT 200");

            using (var stmt = new SQLitePreparedStatement(_connection, sql.ToString()))
            {
                foreach (var (pos, val) in bindings)
                    stmt.Bind(pos, val);
                while (stmt.Step() == SQLite3.Result.Row)
                    results.Add(ReadNodeFromStatement(stmt));
            }
            return results;
        }

        public List<NodeRecord> FindChildNodes(long parentId, string childType)
        {
            var results = new List<NodeRecord>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT n.* FROM nodes n JOIN edges e ON n.id = e.target_id WHERE e.source_id = ? AND e.type = 'defines' AND n.type = ?;"))
            {
                stmt.Bind(1, parentId);
                stmt.Bind(2, childType);
                while (stmt.Step() == SQLite3.Result.Row)
                    results.Add(ReadNodeFromStatement(stmt));
            }
            return results;
        }

        public long GetNodeCount(string type = null)
        {
            if (type != null)
                return _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM nodes WHERE type = ?;", type);
            return _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM nodes;");
        }

        public long GetEdgeCount(string type = null)
        {
            if (type != null)
                return _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM edges WHERE type = ?;", type);
            return _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM edges;");
        }

        public long GetPendingEdgeCount()
        {
            return _connection.ExecuteScalar<long>("SELECT COUNT(*) FROM pending_edges;");
        }

        public List<NodeRecord> GetRecentlyChanged(int hours, int limit = 1000)
        {
            var results = new List<NodeRecord>();
            var cutoff = DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeSeconds();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT * FROM nodes WHERE updated_at >= ? ORDER BY updated_at DESC LIMIT ?;"))
            {
                stmt.Bind(1, cutoff);
                stmt.Bind(2, limit);
                while (stmt.Step() == SQLite3.Result.Row)
                    results.Add(ReadNodeFromStatement(stmt));
            }
            return results;
        }

        public List<NodeRecord> TraverseDependencies(long startNodeId, int maxDepth = 5)
        {
            using (var span = CharonEmitter.StartSpan("graph.query.traverse_dependencies", SpanKind.Internal))
            {
                span.SetAttribute("query.start_node_id", startNodeId);
                span.SetAttribute("query.max_depth", (long)maxDepth);

                var visited = new HashSet<long>();
                var result = new List<NodeRecord>();
                var queue = new Queue<(long nodeId, int depth)>();
                queue.Enqueue((startNodeId, 0));
                visited.Add(startNodeId);

                while (queue.Count > 0)
                {
                    var (currentId, depth) = queue.Dequeue();
                    if (depth >= maxDepth) continue;

                    var edges = FindEdgesFrom(currentId);
                    foreach (var edge in edges)
                    {
                        if (visited.Contains(edge.TargetNodeId)) continue;
                        visited.Add(edge.TargetNodeId);

                        var targetNode = FindNodeById(edge.TargetNodeId);
                        if (targetNode != null)
                        {
                            result.Add(targetNode);
                            queue.Enqueue((edge.TargetNodeId, depth + 1));
                        }
                    }
                }

                span.SetAttribute("results.count", (long)result.Count);
                return result;
            }
        }

        public List<NodeRecord> FindNodesWithEdgeTo(long targetNodeId, string edgeType = null)
        {
            using (var span = CharonEmitter.StartSpan("graph.query.find_nodes_with_edge_to", SpanKind.Internal))
            {
                span.SetAttribute("query.target_node_id", targetNodeId);
                if (edgeType != null) span.SetAttribute("query.edge_type", edgeType);

                var results = new List<NodeRecord>();
                if (edgeType != null)
                {
                    using (var stmt = new SQLitePreparedStatement(_connection,
                        "SELECT n.* FROM nodes n JOIN edges e ON n.id = e.source_id WHERE e.target_id = ? AND e.type = ?;"))
                    {
                        stmt.Bind(1, targetNodeId);
                        stmt.Bind(2, edgeType);
                        while (stmt.Step() == SQLite3.Result.Row)
                            results.Add(ReadNodeFromStatement(stmt));
                    }
                }
                else
                {
                    using (var stmt = new SQLitePreparedStatement(_connection,
                        "SELECT n.* FROM nodes n JOIN edges e ON n.id = e.source_id WHERE e.target_id = ?;"))
                    {
                        stmt.Bind(1, targetNodeId);
                        while (stmt.Step() == SQLite3.Result.Row)
                            results.Add(ReadNodeFromStatement(stmt));
                    }
                }

                span.SetAttribute("results.count", (long)results.Count);
                return results;
            }
        }

        public Dictionary<string, long> GetTypeCounts()
        {
            var counts = new Dictionary<string, long>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT type, COUNT(*) as cnt FROM nodes GROUP BY type;"))
            {
                while (stmt.Step() == SQLite3.Result.Row)
                    counts[stmt.GetString(0)] = stmt.GetLong(1);
            }
            return counts;
        }

        /// <summary>
        /// Per-type node counts scoped to a single tier (e.g. "project"). Lets callers
        /// report a scope-consistent view that excludes seeded builtin and package-tier
        /// nodes rather than mixing scoped and unscoped counts in one summary.
        /// </summary>
        public Dictionary<string, long> GetTypeCounts(string tier)
        {
            var counts = new Dictionary<string, long>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT type, COUNT(*) as cnt FROM nodes WHERE tier = ? GROUP BY type;"))
            {
                stmt.Bind(1, tier);
                while (stmt.Step() == SQLite3.Result.Row)
                    counts[stmt.GetString(0)] = stmt.GetLong(1);
            }
            return counts;
        }

        // --- Tier-aware operations ---

        /// <summary>
        /// Deletes all nodes (and their cascade-deleted edges) for a given tier.
        /// Used during project rebuild to preserve package-tier nodes.
        /// </summary>
        public int DeleteNodesByTier(string tier)
        {
            return _connection.Execute("DELETE FROM nodes WHERE tier = ?;", tier);
        }

        public long GetNodeCount(string type, string tier)
        {
            return _connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM nodes WHERE type = ? AND tier = ?;", type, tier);
        }

        // --- Name-based lookups for edge resolution ---

        /// <summary>
        /// Finds a ScriptType node by exact name, optionally filtered by namespace property.
        /// Used for resolving inheritance/implements edges by type name.
        /// </summary>
        public NodeRecord FindNodeByNameAndType(string name, string nodeType)
        {
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT * FROM nodes WHERE name = ? AND type = ? LIMIT 1;"))
            {
                stmt.Bind(1, name);
                stmt.Bind(2, nodeType);
                if (stmt.Step() == SQLite3.Result.Row)
                    return ReadNodeFromStatement(stmt);
                return null;
            }
        }

        // --- Pending edges ---

        public void InsertPendingEdge(long sourceNodeId, string edgeType, string targetTypeName,
            string targetNamespace, string sourceAssetGuid)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _connection.Execute(@"
                INSERT INTO pending_edges (source_node_id, edge_type, target_type_name, target_namespace, source_asset_guid, created_at)
                VALUES (?, ?, ?, ?, ?, ?);",
                sourceNodeId, edgeType, targetTypeName, targetNamespace, sourceAssetGuid, now);
        }

        public List<Models.PendingEdge> GetPendingEdges()
        {
            var results = new List<Models.PendingEdge>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT id, source_node_id, edge_type, target_type_name, target_namespace, source_asset_guid, created_at FROM pending_edges;"))
            {
                while (stmt.Step() == SQLite3.Result.Row)
                {
                    results.Add(new Models.PendingEdge
                    {
                        Id = stmt.GetLong(0),
                        SourceNodeId = stmt.GetLong(1),
                        EdgeType = stmt.GetString(2),
                        TargetTypeName = stmt.GetString(3),
                        TargetNamespace = stmt.GetString(4),
                        SourceAssetGuid = stmt.GetString(5),
                        CreatedAt = stmt.GetLong(6)
                    });
                }
            }
            return results;
        }

        /// <summary>
        /// Returns only the pending edges whose source asset GUID is in the given set.
        /// Used by incremental updates to scope resolution to the changed assets
        /// (O(changed)) instead of scanning the entire pending_edges table (O(graph)).
        /// </summary>
        public List<Models.PendingEdge> GetPendingEdgesBySourceAssets(IReadOnlyCollection<string> sourceAssetGuids)
        {
            var results = new List<Models.PendingEdge>();
            if (sourceAssetGuids == null || sourceAssetGuids.Count == 0) return results;

            var placeholderArr = new string[sourceAssetGuids.Count];
            for (int p = 0; p < placeholderArr.Length; p++) placeholderArr[p] = "?";
            var placeholders = string.Join(",", placeholderArr);
            var sql = "SELECT id, source_node_id, edge_type, target_type_name, target_namespace, source_asset_guid, created_at "
                      + "FROM pending_edges WHERE source_asset_guid IN (" + placeholders + ");";

            using (var stmt = new SQLitePreparedStatement(_connection, sql))
            {
                int i = 1;
                foreach (var guid in sourceAssetGuids)
                    stmt.Bind(i++, guid);

                while (stmt.Step() == SQLite3.Result.Row)
                {
                    results.Add(new Models.PendingEdge
                    {
                        Id = stmt.GetLong(0),
                        SourceNodeId = stmt.GetLong(1),
                        EdgeType = stmt.GetString(2),
                        TargetTypeName = stmt.GetString(3),
                        TargetNamespace = stmt.GetString(4),
                        SourceAssetGuid = stmt.GetString(5),
                        CreatedAt = stmt.GetLong(6)
                    });
                }
            }
            return results;
        }

        public void DeletePendingEdge(long id)
        {
            _connection.Execute("DELETE FROM pending_edges WHERE id = ?;", id);
        }

        public void DeletePendingEdgesBySourceAsset(string sourceAssetGuid)
        {
            _connection.Execute("DELETE FROM pending_edges WHERE source_asset_guid = ?;", sourceAssetGuid);
        }

        /// <summary>
        /// Builds an in-memory map of all existing node GUIDs to their row IDs.
        /// Used by GraphBuilder to avoid per-edge DB lookups during scanning.
        /// </summary>
        public Dictionary<string, long> BuildNodeGuidMap()
        {
            var map = new Dictionary<string, long>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT id, guid, file_id FROM nodes WHERE guid IS NOT NULL;"))
            {
                while (stmt.Step() == SQLite3.Result.Row)
                {
                    var id = stmt.GetLong(0);
                    var guid = stmt.GetString(1);
                    var fileIdStr = stmt.GetString(2);
                    long fileId = fileIdStr != null ? stmt.GetLong(2) : 0;
                    var key = $"{guid}:{fileId}";
                    map[key] = id;
                }
            }
            return map;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connection?.Close();
            if (_instance == this) _instance = null;
        }
    }
}
