// Editor/Graph/GraphDatabase.cs
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using SQLite;
using ArcForge.Hades.Editor.Graph.Models;

namespace ArcForge.Hades.Editor.Graph
{
    public class GraphDatabase : IDisposable
    {
        static GraphDatabase _instance;
        public static GraphDatabase Instance => _instance;

        readonly SQLiteConnection _connection;
        bool _disposed;

        const int CurrentSchemaVersion = 1;

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
            // Future migrations go here
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

        public long InsertNode(NodeRecord node)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _connection.Execute(@"
                INSERT INTO nodes (type, guid, file_id, parent_node_id, name, path, source_range, properties, created_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                node.Type,
                node.Guid,
                node.FileId.HasValue ? (object)node.FileId.Value : null,
                node.ParentNodeId.HasValue ? (object)node.ParentNodeId.Value : null,
                node.Name,
                node.Path,
                node.SourceRange,
                node.PropertiesJson,
                now,
                now);

            return _connection.ExecuteScalar<long>("SELECT last_insert_rowid();");
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

            return _connection.ExecuteScalar<long>("SELECT last_insert_rowid();");
        }

        public NodeRecord FindNodeByGuid(string guid, long? fileId = null)
        {
            if (fileId.HasValue)
            {
                using (var stmt = new SQLitePreparedStatement(_connection,
                    "SELECT * FROM nodes WHERE guid = ? AND file_id = ?;"))
                {
                    stmt.Bind(1, guid);
                    stmt.Bind(2, fileId.Value);
                    if (stmt.Step() == SQLite3.Result.Row)
                        return ReadNodeFromStatement(stmt);
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
                        return ReadNodeFromStatement(stmt);
                    return null;
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
            var results = new List<NodeRecord>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT * FROM nodes WHERE type = ?;"))
            {
                stmt.Bind(1, type);
                while (stmt.Step() == SQLite3.Result.Row)
                    results.Add(ReadNodeFromStatement(stmt));
            }
            return results;
        }

        public List<EdgeInfo> FindEdgesFrom(long sourceId, string type = null)
        {
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
            return results;
        }

        public List<EdgeInfo> FindEdgesTo(long targetId, string type = null)
        {
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
            return results;
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
            // col 0: id, 1: type, 2: guid, 3: file_id, 4: parent_node_id,
            // 5: name, 6: path, 7: source_range, 8: properties
            var node = new NodeRecord(stmt.GetString(1), stmt.GetString(2))
            {
                Id = stmt.GetLong(0),
                FileId = stmt.GetString(3) != null ? (long?)stmt.GetLong(3) : null,
                ParentNodeId = stmt.GetString(4) != null ? (long?)stmt.GetLong(4) : null,
                Name = stmt.GetString(5),
                Path = stmt.GetString(6),
                SourceRange = stmt.GetString(7)
            };
            var propsJson = stmt.GetString(8);
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
            // e.*: 0: id, 1: source_id, 2: target_id, 3: type, 4: properties
            // joined: 7: target_type, 8: target_name, 9: target_path
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

        public List<NodeRecord> GetRecentlyChanged(int hours)
        {
            var results = new List<NodeRecord>();
            var cutoff = DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeSeconds();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT * FROM nodes WHERE updated_at >= ? ORDER BY updated_at DESC;"))
            {
                stmt.Bind(1, cutoff);
                while (stmt.Step() == SQLite3.Result.Row)
                    results.Add(ReadNodeFromStatement(stmt));
            }
            return results;
        }

        public List<NodeRecord> TraverseDependencies(long startNodeId, int maxDepth = 5)
        {
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
            return result;
        }

        public List<NodeRecord> FindNodesWithEdgeTo(long targetNodeId, string edgeType = null)
        {
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
            return results;
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connection?.Close();
            if (_instance == this) _instance = null;
        }
    }
}
