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

        const int CurrentSchemaVersion = 4;

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
                    updated_at INTEGER NOT NULL,
                    owner_guid TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_nodes_type ON nodes(type);
                CREATE INDEX IF NOT EXISTS idx_nodes_guid ON nodes(guid);
                CREATE INDEX IF NOT EXISTS idx_nodes_path ON nodes(path);
                CREATE INDEX IF NOT EXISTS idx_nodes_parent ON nodes(parent_node_id);
                CREATE INDEX IF NOT EXISTS idx_nodes_name_type ON nodes(name, type);
                CREATE INDEX IF NOT EXISTS idx_nodes_tier ON nodes(tier);
                CREATE INDEX IF NOT EXISTS idx_nodes_owner_guid ON nodes(owner_guid);
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
                    properties TEXT,
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

            if (fromVersion < 3)
            {
                // pending_edges gains a `properties` column so a deferred (forward-reference)
                // edge keeps its {field}/{addressable} enrichment through ResolvePendingEdges.
                // Previously deferral dropped properties, so addressable AssetReference edges
                // resolved with no flag and ~15% of all reference edges lost their `field`.
                // ADD COLUMN is safe here: every pending_edges read uses an explicit column
                // list (no positional SELECT *). Guarded for the case where the < 2 path above
                // already recreated the table with the new column.
                try { _connection.Execute("ALTER TABLE pending_edges ADD COLUMN properties TEXT;"); }
                catch { /* column already present (table recreated by the < 2 migration) */ }
                RecordSchemaVersion(3);

                // Existing edges resolved before this migration keep their NULL properties
                // until the next full rebuild repopulates them through the fixed path.
            }

            if (fromVersion < 4)
            {
                // owner_guid: every node records the asset that owns it, so deletion-by-owner
                // is total (root + all children) and uniform across scenes/prefabs/scripts/meta.
                // The graph is a rebuildable cache; recreate the node-bearing tables so the
                // fresh column order matches CreateAllTables, then let the empty-graph check in
                // CheckStartupSync (GetNodeCount() == 0 -> RebuildParallel) repopulate owner_guid
                // through the new write path. No in-place backfill — old NULL-guid children have
                // no recoverable owner without re-scanning anyway.
                _connection.ExecuteScript(@"
                    DROP TABLE IF EXISTS edges;
                    DROP TABLE IF EXISTS nodes;
                    DROP TABLE IF EXISTS scanned_assets;
                    DROP TABLE IF EXISTS pending_invalidations;
                ");

                CreateAllTables();
                RecordSchemaVersion(4);
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
            var rows = _connection.Execute(@"
                INSERT OR IGNORE INTO nodes (type, tier, guid, file_id, parent_node_id, name, path, source_range, properties, created_at, updated_at, owner_guid)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
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
                now,
                node.OwnerGuid);

            // OR IGNORE: a duplicate (guid, file_id) is skipped rather than throwing. This happens
            // when two assets share a GUID (Unity ignores one copy — the lantern duplicate-GUID
            // case); previously the unique-index violation threw and aborted the whole rebuild
            // mid-scan, leaving a partial graph. Reuse the existing node's id so edges still
            // resolve to it, and the rebuild continues.
            if (rows == 0 && node.Guid != null)
            {
                var existing = FindNodeByGuid(node.Guid, node.FileId);
                if (existing != null) return existing.Id;
            }

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

        /// <summary>
        /// Batch-inserts edges with ONE reused prepared statement (bind+step+reset per row)
        /// instead of a fresh Execute (re-prepare + finalize) per edge. A full rebuild resolves
        /// millions of edges; the per-edge re-prepare was a large write cost. Call inside a
        /// transaction.
        /// </summary>
        public void InsertEdgesBatch(
            System.Collections.Generic.IReadOnlyList<(long sourceId, long targetId, string type, string propertiesJson)> edges)
        {
            if (edges == null || edges.Count == 0) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "INSERT OR IGNORE INTO edges (source_id, target_id, type, properties, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?);"))
            {
                foreach (var e in edges)
                {
                    stmt.Bind(1, e.sourceId);
                    stmt.Bind(2, e.targetId);
                    stmt.Bind(3, e.type);
                    stmt.Bind(4, e.propertiesJson);
                    stmt.Bind(5, now);
                    stmt.Bind(6, now);
                    stmt.Step();
                    stmt.Reset();
                }
            }
        }

        /// <summary>Batch-inserts pending edges with one reused prepared statement.</summary>
        public void InsertPendingEdgesBatch(
            System.Collections.Generic.IReadOnlyList<(long sourceNodeId, string edgeType, string targetTypeName, string targetNamespace, string sourceAssetGuid, string properties)> rows)
        {
            if (rows == null || rows.Count == 0) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "INSERT INTO pending_edges (source_node_id, edge_type, target_type_name, target_namespace, source_asset_guid, properties, created_at) VALUES (?, ?, ?, ?, ?, ?, ?);"))
            {
                foreach (var r in rows)
                {
                    stmt.Bind(1, r.sourceNodeId);
                    stmt.Bind(2, r.edgeType);
                    stmt.Bind(3, r.targetTypeName);
                    stmt.Bind(4, r.targetNamespace);
                    stmt.Bind(5, r.sourceAssetGuid);
                    stmt.Bind(6, r.properties);
                    stmt.Bind(7, now);
                    stmt.Step();
                    stmt.Reset();
                }
            }
        }

        /// <summary>Batch-deletes pending edges by id with one reused prepared statement.</summary>
        public void DeletePendingEdgesBatch(System.Collections.Generic.IEnumerable<long> ids)
        {
            if (ids == null) return;
            using (var stmt = new SQLitePreparedStatement(_connection,
                "DELETE FROM pending_edges WHERE id = ?;"))
            {
                foreach (var id in ids)
                {
                    stmt.Bind(1, id);
                    stmt.Step();
                    stmt.Reset();
                }
            }
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

        /// <summary>
        /// Deletes an asset's FULL node set: every node whose owner_guid matches, which
        /// includes the root asset node (it owns itself) and all sub-object children
        /// (GameObjects/Components/ScriptTypes/ScriptMethods). Edges cascade via
        /// ON DELETE CASCADE. This is the canonical incremental-delete path; it replaces
        /// the old DeleteNodesByGuid, which removed only the guid-bearing root and leaked
        /// NULL-guid children on every re-scan.
        /// </summary>
        public void DeleteNodesByOwnerGuid(string ownerGuid)
        {
            _connection.Execute("DELETE FROM nodes WHERE owner_guid = ?;", ownerGuid);
        }

        /// <summary>
        /// Returns the inbound edges that target an asset's guid-bearing node(s), captured BEFORE
        /// the asset is deleted in an incremental update. DeleteNodesByGuid removes only the
        /// guid-bearing node (Material / Prefab-root / Scene-root / SO / Script / Texture); its
        /// ON DELETE CASCADE then drops these inbound edges. The sources are overwhelmingly
        /// NULL-guid component nodes (never deleted by guid), so their ids stay valid and the edge
        /// can be re-pointed at the recreated target after the re-scan. Returns the target's
        /// file_id to re-resolve the exact recreated node.
        /// </summary>
        public List<(long sourceId, string edgeType, string properties, long? targetFileId)>
            GetInboundEdgesToGuid(string guid)
        {
            var results = new List<(long, string, string, long?)>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT e.source_id, e.type, e.properties, tn.file_id " +
                "FROM edges e JOIN nodes tn ON tn.id = e.target_id WHERE tn.guid = ?;"))
            {
                stmt.Bind(1, guid);
                while (stmt.Step() == SQLite3.Result.Row)
                {
                    long? targetFileId = stmt.GetString(3) != null ? stmt.GetLong(3) : (long?)null;
                    results.Add((stmt.GetLong(0), stmt.GetString(1), stmt.GetString(2), targetFileId));
                }
            }
            return results;
        }

        /// <summary>True if a node row with this id still exists. Used to drop captured inbound
        /// edges whose source was itself re-scanned (its old node id is gone) — that source
        /// rebuilds its own outbound edge, so restoring would be redundant/stale.</summary>
        public bool NodeExists(long id)
        {
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT 1 FROM nodes WHERE id = ? LIMIT 1;"))
            {
                stmt.Bind(1, id);
                return stmt.Step() == SQLite3.Result.Row;
            }
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
            // 6: name, 7: path, 8: source_range, 9: properties, 10: created_at,
            // 11: updated_at, 12: owner_guid
            var node = new NodeRecord(stmt.GetString(1), stmt.GetString(3))
            {
                Id = stmt.GetLong(0),
                FileId = stmt.GetString(4) != null ? (long?)stmt.GetLong(4) : null,
                ParentNodeId = stmt.GetString(5) != null ? (long?)stmt.GetLong(5) : null,
                Name = stmt.GetString(6),
                Path = stmt.GetString(7),
                SourceRange = stmt.GetString(8),
                OwnerGuid = stmt.GetString(12)
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

        /// <summary>
        /// Exact-path lookup using idx_nodes_path. Replaces the SearchByName(null, null)
        /// full-table scan + full NodeRecord materialization that the reference/dependency
        /// tools used to resolve an asset path — O(log n) index probe instead of O(n).
        /// Returns every node stored at this exact path (root asset node plus any
        /// co-located ScriptType/ScriptMethod nodes), matching the old filter's result set.
        ///
        /// ORDER BY name reproduces SearchByName's ordering exactly: trace_dependencies starts
        /// its traversal from results[0], and for a .cs file the ScriptType (named after the
        /// stem, e.g. "Player") must sort before the Script ("Player.cs") so the traversal —
        /// and the external-pending-edge honesty signal — keys off the type node, not the file
        /// node whose only outbound edge ('defines') is excluded from dependency traversal.
        /// </summary>
        public List<NodeRecord> FindNodesByPath(string path)
        {
            var results = new List<NodeRecord>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT * FROM nodes WHERE path = ? ORDER BY name;"))
            {
                stmt.Bind(1, path);
                while (stmt.Step() == SQLite3.Result.Row)
                    results.Add(ReadNodeFromStatement(stmt));
            }
            return results;
        }

        /// <summary>
        /// All nodes with an exact (name, type) — uses idx_nodes_name_type instead of loading
        /// every node of a type and filtering on Name in C#. Case-sensitive, matching the
        /// previous FindNodesByType(...).Where(n =&gt; n.Name == name) behavior.
        /// </summary>
        public List<NodeRecord> FindNodesByNameAndTypeAll(string name, string type)
        {
            var results = new List<NodeRecord>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT * FROM nodes WHERE name = ? AND type = ?;"))
            {
                stmt.Bind(1, name);
                stmt.Bind(2, type);
                while (stmt.Step() == SQLite3.Result.Row)
                    results.Add(ReadNodeFromStatement(stmt));
            }
            return results;
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

        // Edge types that represent structural containment/declaration within a file or asset.
        // These are NOT dependencies — they describe the internal shape of the source node,
        // not what it depends on. Excluded from dependency traversal by default.
        private static readonly HashSet<string> DefaultExcludedDependencyEdgeTypes =
            new HashSet<string>(System.StringComparer.Ordinal) { "defines" };

        /// <summary>
        /// Edge types that describe structural shape or ownership — they are NEVER a "real"
        /// reference from one asset to another for the purposes of <c>find_references_to</c>.
        ///
        /// Policy:
        ///   defines       — Script/ScriptType declares a child type or method.
        ///   contains      — Prefab/Scene/GameObject contains a child object or component.
        ///   nests_prefab  — Prefab structurally nests another prefab as a child instance.
        ///
        /// Intentionally NOT in this set (= treated as real referrers):
        ///   references, uses_*, instance_of, code_references — direct asset/code references.
        ///   inherits_from, implements  — C# inheritance is a real referrer for script targets;
        ///       for prefab targets, inherits_from (variant→base) is filtered separately in
        ///       FindReferencesTo using target-type-aware logic.
        ///   instantiates  — (Task C4) scene instantiating a prefab is a real referrer.
        ///   addressable_for — (Task D1) addressables entry pointing at an asset is a real referrer.
        /// </summary>
        public static readonly HashSet<string> StructuralEdgeTypes =
            new HashSet<string>(System.StringComparer.Ordinal) { "defines", "contains", "nests_prefab" };

        /// <summary>
        /// Traverses forward dependency edges from <paramref name="startNodeId"/> up to
        /// <paramref name="maxDepth"/> hops. Structural-containment edges (by default:
        /// <c>defines</c>) are excluded so that a Script/ScriptType's own declared methods
        /// do not appear as dependencies.
        /// </summary>
        /// <param name="startNodeId">Node to start from.</param>
        /// <param name="maxDepth">Maximum BFS depth.</param>
        /// <param name="excludedEdgeTypes">
        /// Set of edge types to skip during traversal. Pass <c>null</c> to use the default
        /// exclusion set (<c>defines</c>). Pass an empty set to follow all edge types.
        /// </param>
        public List<NodeRecord> TraverseDependencies(long startNodeId, int maxDepth = 5,
            HashSet<string> excludedEdgeTypes = null)
        {
            var excluded = excludedEdgeTypes ?? DefaultExcludedDependencyEdgeTypes;

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
                    if (excluded.Contains(edge.Type)) continue;
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

        /// <summary>
        /// Returns all source nodes that have an incoming edge to <paramref name="targetNodeId"/>,
        /// excluding edges whose type is in <paramref name="excludedEdgeTypes"/>.
        /// Pass <see cref="StructuralEdgeTypes"/> to filter structural/containment edges, or a
        /// superset that additionally excludes context-specific edge types (e.g. <c>inherits_from</c>
        /// for prefab targets where variants should not count as direct referrers).
        /// </summary>
        public List<NodeRecord> FindNodesWithEdgeTo(long targetNodeId, HashSet<string> excludedEdgeTypes)
        {
            // Fetch all referrers with edge type so we can post-filter in C#
            // (SQLite does not support array bind parameters).
            var results = new List<NodeRecord>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT n.*, e.type as edge_type FROM nodes n JOIN edges e ON n.id = e.source_id WHERE e.target_id = ?;"))
            {
                stmt.Bind(1, targetNodeId);
                while (stmt.Step() == SQLite3.Result.Row)
                {
                    // nodes.* has 13 columns (0-12: id, type, tier, guid, file_id,
                    // parent_node_id, name, path, source_range, properties,
                    // created_at, updated_at, owner_guid). e.type as edge_type is column 13.
                    // NOTE: this trailing index must be bumped whenever the nodes schema
                    // gains a column (it just bit us once — owner_guid shifted it from 12).
                    var edgeType = stmt.GetString(13);
                    if (excludedEdgeTypes == null || !excludedEdgeTypes.Contains(edgeType))
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

        /// <summary>
        /// Returns all distinct non-null GUIDs for nodes in the given tier.
        /// Used for stale-node reconciliation after a successful package scan.
        /// </summary>
        public List<string> GetDistinctGuidsForTier(string tier)
        {
            return _connection.QueryScalars<string>(
                "SELECT DISTINCT guid FROM nodes WHERE tier = ? AND guid IS NOT NULL;", tier);
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

        /// <summary>
        /// Bulk-loads guid → node id (lowest id per guid, matching FindNodeByGuid's
        /// "ORDER BY id LIMIT 1"). One indexed scan instead of a per-edge query.
        /// Used by ResolvePendingEdges to resolve large pending-edge sets in memory —
        /// the per-edge SQLite lookups were O(P) main-thread round-trips that pinned the
        /// editor for tens of minutes on large graphs.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, long> LoadGuidToNodeIdMap()
        {
            var map = new System.Collections.Generic.Dictionary<string, long>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT guid, MIN(id) FROM nodes WHERE guid IS NOT NULL GROUP BY guid;"))
            {
                while (stmt.Step() == SQLite3.Result.Row)
                {
                    var guid = stmt.GetString(0);
                    if (!string.IsNullOrEmpty(guid))
                        map[guid] = stmt.GetLong(1);
                }
            }
            return map;
        }

        /// <summary>
        /// Bulk-loads ScriptType name → (lowest node id, kind). Mirrors
        /// FindNodeByNameAndType(name, "ScriptType") (first match) using the lowest id for
        /// determinism; 'kind' is read from the node's properties JSON (class/struct/
        /// interface/enum/record), needed to classify extends_or_implements edges.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, (long Id, string Kind)> LoadScriptTypeByNameMap()
        {
            var map = new System.Collections.Generic.Dictionary<string, (long Id, string Kind)>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT name, id, properties FROM nodes WHERE type = 'ScriptType' ORDER BY id;"))
            {
                while (stmt.Step() == SQLite3.Result.Row)
                {
                    var name = stmt.GetString(0);
                    // ORDER BY id ensures the first row seen for a name is the lowest id; keep it.
                    if (string.IsNullOrEmpty(name) || map.ContainsKey(name)) continue;
                    map[name] = (stmt.GetLong(1), ExtractKindFromProperties(stmt.GetString(2)));
                }
            }
            return map;
        }

        static string ExtractKindFromProperties(string propertiesJson)
        {
            if (string.IsNullOrEmpty(propertiesJson)) return null;
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(propertiesJson);
                return o["kind"]?.ToString();
            }
            catch { return null; }
        }

        // --- Pending edges ---

        public void InsertPendingEdge(long sourceNodeId, string edgeType, string targetTypeName,
            string targetNamespace, string sourceAssetGuid, string properties = null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _connection.Execute(@"
                INSERT INTO pending_edges (source_node_id, edge_type, target_type_name, target_namespace, source_asset_guid, properties, created_at)
                VALUES (?, ?, ?, ?, ?, ?, ?);",
                sourceNodeId, edgeType, targetTypeName, targetNamespace, sourceAssetGuid, properties, now);
        }

        public List<Models.PendingEdge> GetPendingEdges()
        {
            var results = new List<Models.PendingEdge>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT id, source_node_id, edge_type, target_type_name, target_namespace, source_asset_guid, properties, created_at FROM pending_edges;"))
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
                        Properties = stmt.GetString(6),
                        CreatedAt = stmt.GetLong(7)
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
            var sql = "SELECT id, source_node_id, edge_type, target_type_name, target_namespace, source_asset_guid, properties, created_at "
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
                        Properties = stmt.GetString(6),
                        CreatedAt = stmt.GetLong(7)
                    });
                }
            }
            return results;
        }

        /// <summary>
        /// Returns pending edges whose source is the given node. Used by honesty-signal
        /// logic to count unresolved supertype/dependency edges that point at known-external
        /// targets (precompiled BCL/Unity types) — the caller uses
        /// <c>GraphBuilder.IsKnownExternalTarget</c> to classify them.
        /// </summary>
        public List<Models.PendingEdge> GetPendingEdgesForNode(long sourceNodeId)
        {
            var results = new List<Models.PendingEdge>();
            using (var stmt = new SQLitePreparedStatement(_connection,
                "SELECT id, source_node_id, edge_type, target_type_name, target_namespace, source_asset_guid, properties, created_at " +
                "FROM pending_edges WHERE source_node_id = ?;"))
            {
                stmt.Bind(1, sourceNodeId);
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
                        Properties = stmt.GetString(6),
                        CreatedAt = stmt.GetLong(7)
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

        // Test-only: restore the singleton to a prior value captured before a test ran.
        // Called from [TearDown] to undo the side-effect of constructing a temp DB in tests.
        // Nothing in production code calls this method.
        internal static void RestoreInstanceForTests(GraphDatabase savedInstance)
        {
            _instance = savedInstance;
        }
    }
}
