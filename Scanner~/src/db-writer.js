import Database from 'better-sqlite3';

const PRAGMAS = `
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;
PRAGMA cache_size = -65536;
PRAGMA temp_store = MEMORY;
PRAGMA mmap_size = 268435456;
PRAGMA foreign_keys = ON;
`;

const SCHEMA = `
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
    properties TEXT,
    created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_pending_edges_target ON pending_edges(target_type_name);
CREATE INDEX IF NOT EXISTS idx_pending_edges_source_asset ON pending_edges(source_asset_guid);

CREATE TABLE IF NOT EXISTS scanned_assets (
    guid TEXT PRIMARY KEY,
    content_hash TEXT NOT NULL,
    scanned_at INTEGER NOT NULL,
    scanner_version INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS graph_metadata (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
`;

export class DbWriter {
  /** @type {import('better-sqlite3').Database} */
  #db;
  #defaultTier = 'project';

  // Prepared statements (lazily populated after _ensureTables)
  #stmtInsertNode;
  #stmtInsertEdge;
  #stmtRecordScannedAsset;
  #stmtInsertPendingEdge;
  #stmtDeleteNodesByGuid;
  #stmtDeletePendingEdgesBySourceAsset;
  #stmtSetMetadata;
  #stmtGetMetadata;

  constructor(dbPath) {
    this.#db = new Database(dbPath);
    this.#applyPragmas();
    this.#ensureTables();
    this.#prepareStatements();
  }

  #applyPragmas() {
    this.#db.exec(PRAGMAS);
  }

  #ensureTables() {
    this.#db.exec(SCHEMA);
  }

  #prepareStatements() {
    this.#stmtInsertNode = this.#db.prepare(`
      INSERT INTO nodes (type, tier, guid, file_id, parent_node_id, name, path, source_range, properties, created_at, updated_at)
      VALUES (@type, @tier, @guid, @fileId, @parentNodeId, @name, @path, @sourceRange, @properties, @createdAt, @updatedAt)
    `);

    this.#stmtInsertEdge = this.#db.prepare(`
      INSERT OR IGNORE INTO edges (source_id, target_id, type, properties, created_at, updated_at)
      VALUES (@sourceId, @targetId, @type, @properties, @createdAt, @updatedAt)
    `);

    this.#stmtRecordScannedAsset = this.#db.prepare(`
      INSERT INTO scanned_assets (guid, content_hash, scanned_at, scanner_version)
      VALUES (@guid, @contentHash, @scannedAt, @scannerVersion)
      ON CONFLICT(guid) DO UPDATE SET
        content_hash = excluded.content_hash,
        scanned_at = excluded.scanned_at,
        scanner_version = excluded.scanner_version
    `);

    this.#stmtInsertPendingEdge = this.#db.prepare(`
      INSERT INTO pending_edges (source_node_id, edge_type, target_type_name, target_namespace, source_asset_guid, created_at)
      VALUES (@sourceNodeId, @edgeType, @targetTypeName, @targetNamespace, @sourceAssetGuid, @createdAt)
    `);

    this.#stmtDeleteNodesByGuid = this.#db.prepare(`
      DELETE FROM nodes WHERE guid = ?
    `);

    this.#stmtDeletePendingEdgesBySourceAsset = this.#db.prepare(`
      DELETE FROM pending_edges WHERE source_asset_guid = ?
    `);

    this.#stmtSetMetadata = this.#db.prepare(`
      INSERT INTO graph_metadata (key, value) VALUES (@key, @value)
      ON CONFLICT(key) DO UPDATE SET value = excluded.value
    `);

    this.#stmtGetMetadata = this.#db.prepare(`
      SELECT value FROM graph_metadata WHERE key = ?
    `);
  }

  /** Sets the default tier for subsequent insertNode calls. */
  setTier(tier) {
    this.#defaultTier = tier;
  }

  /**
   * Inserts a node and returns its row id.
   * @param {{ type: string, name?: string, path?: string, guid?: string, fileId?: number,
   *            parentNodeId?: number, tier?: string, sourceRange?: string, properties?: string }} params
   * @returns {number}
   */
  insertNode({ type, name, path, guid, fileId, parentNodeId, tier, sourceRange, properties } = {}) {
    const now = Math.floor(Date.now() / 1000);
    const result = this.#stmtInsertNode.run({
      type,
      tier: tier ?? this.#defaultTier,
      guid: guid ?? null,
      fileId: fileId ?? null,
      parentNodeId: parentNodeId ?? null,
      name: name ?? null,
      path: path ?? null,
      sourceRange: sourceRange ?? null,
      properties: properties ?? null,
      createdAt: now,
      updatedAt: now,
    });
    return result.lastInsertRowid;
  }

  /**
   * Inserts an edge between two node ids. Silently ignores duplicates.
   * @param {number} sourceId
   * @param {number} targetId
   * @param {string} type
   * @param {string|null} [propertiesJson]
   */
  insertEdge(sourceId, targetId, type, propertiesJson = null) {
    const now = Math.floor(Date.now() / 1000);
    this.#stmtInsertEdge.run({
      sourceId,
      targetId,
      type,
      properties: propertiesJson ?? null,
      createdAt: now,
      updatedAt: now,
    });
  }

  /**
   * Records a scanned asset (upserts on guid).
   * @param {string} guid
   * @param {string} contentHash
   * @param {number} scannerVersion
   */
  recordScannedAsset(guid, contentHash, scannerVersion) {
    const now = Math.floor(Date.now() / 1000);
    this.#stmtRecordScannedAsset.run({
      guid,
      contentHash,
      scannedAt: now,
      scannerVersion,
    });
  }

  /**
   * Returns a Map of guid → { contentHash, scannerVersion } for the requested guids.
   * @param {string[]} guids
   * @returns {Map<string, { contentHash: string, scannerVersion: number }>}
   */
  getScannedAssets(guids) {
    const result = new Map();
    if (!guids || guids.length === 0) return result;

    // Use a dynamic IN clause — build once with placeholders
    const placeholders = guids.map(() => '?').join(',');
    const stmt = this.#db.prepare(
      `SELECT guid, content_hash, scanner_version FROM scanned_assets WHERE guid IN (${placeholders})`
    );
    const rows = stmt.all(...guids);
    for (const row of rows) {
      result.set(row.guid, { contentHash: row.content_hash, scannerVersion: row.scanner_version });
    }
    return result;
  }

  /**
   * Inserts a pending edge to be resolved later.
   * @param {number} sourceNodeId
   * @param {string} edgeType
   * @param {string} targetTypeName
   * @param {string|null} targetNamespace
   * @param {string|null} sourceAssetGuid
   */
  insertPendingEdge(sourceNodeId, edgeType, targetTypeName, targetNamespace, sourceAssetGuid) {
    const now = Math.floor(Date.now() / 1000);
    this.#stmtInsertPendingEdge.run({
      sourceNodeId,
      edgeType,
      targetTypeName,
      targetNamespace: targetNamespace ?? null,
      sourceAssetGuid: sourceAssetGuid ?? null,
      createdAt: now,
    });
  }

  /**
   * Deletes all nodes whose guid matches (cascades to edges via ON DELETE CASCADE).
   * @param {string} guid
   */
  deleteNodesByGuid(guid) {
    this.#stmtDeleteNodesByGuid.run(guid);
  }

  /**
   * Deletes all pending edges originating from the given asset guid.
   * @param {string} guid
   */
  deletePendingEdgesBySourceAsset(guid) {
    this.#stmtDeletePendingEdgesBySourceAsset.run(guid);
  }

  /**
   * Row id of the guid-bearing Script node for a file, or null. Needed before an incremental
   * re-scan to (a) reach the file's NULL-guid ScriptType/ScriptMethod nodes by file_id and
   * (b) capture inbound edges before they cascade away.
   * @param {string} guid
   * @returns {number|null}
   */
  getScriptNodeIdByGuid(guid) {
    const row = this.#db
      .prepare("SELECT id FROM nodes WHERE guid = ? AND type = 'Script' LIMIT 1")
      .get(guid);
    return row ? row.id : null;
  }

  /**
   * Captures inbound edges targeting a file's Script node (guid-bearing) and its ScriptType
   * nodes (NULL-guid, linked by file_id), BEFORE the file is re-scanned. Those target nodes are
   * deleted on re-scan (see deleteFileNodes); without capture+restore, references to them from
   * OTHER, unchanged files (code_references / inherits_from / implements, plus instance_of /
   * references to the Script) are cascade-deleted and never recreated — the C# analogue of the
   * Unity-side edge erosion. Re-point after re-scan: Script targets by guid (stable), ScriptType
   * targets by name.
   * @param {number} scriptNodeId
   * @returns {Array<{ sourceId:number, edgeType:string, properties:string|null, targetType:string, targetName:string }>}
   */
  captureInboundToFile(scriptNodeId) {
    return this.#db
      .prepare(
        "SELECT e.source_id AS sourceId, e.type AS edgeType, e.properties AS properties, " +
          "tn.type AS targetType, tn.name AS targetName " +
          "FROM edges e JOIN nodes tn ON tn.id = e.target_id " +
          "WHERE (tn.id = ? OR tn.file_id = ?) AND tn.type IN ('Script','ScriptType')"
      )
      .all(scriptNodeId, scriptNodeId);
  }

  /**
   * Deletes a file's FULL node set on re-scan: the NULL-guid ScriptType/ScriptMethod nodes
   * (linked by file_id = the Script node's id) plus the guid-bearing Script node itself.
   * deleteNodesByGuid alone matched only the Script node, leaking the type/method nodes and
   * stranding their inbound edges on the now-orphaned old node. Cascades to edges.
   * @param {string} guid
   * @param {number|null} scriptNodeId
   */
  deleteFileNodes(guid, scriptNodeId) {
    if (scriptNodeId != null) {
      this.#db.prepare('DELETE FROM nodes WHERE file_id = ?').run(scriptNodeId);
    }
    this.#stmtDeleteNodesByGuid.run(guid);
  }

  /**
   * True if a node row with this id still exists. Used to drop captured inbound edges whose
   * source file was itself re-scanned (its old node id is gone) — that file re-emits its own
   * outbound edges via pending resolution, so restoring would be redundant/stale.
   * @param {number} id
   * @returns {boolean}
   */
  nodeExists(id) {
    return !!this.#db.prepare('SELECT 1 FROM nodes WHERE id = ? LIMIT 1').get(id);
  }

  /**
   * Bulk-insert asset nodes from MetaScanner results in a single transaction.
   * Skips assets whose guid already exists in the nodes table.
   * @param {Array<{ guid: string, name: string, path: string, type: string }>} assets
   */
  insertMetaAssets(assets) {
    const stmtCheck = this.#db.prepare('SELECT 1 FROM nodes WHERE guid = ? LIMIT 1');
    const insert = this.#stmtInsertNode;
    const tier = this.#defaultTier;
    const now = Math.floor(Date.now() / 1000);

    const tx = this.#db.transaction((items) => {
      for (const asset of items) {
        if (stmtCheck.get(asset.guid)) continue;

        insert.run({
          type: asset.type,
          tier,
          guid: asset.guid,
          fileId: null,
          parentNodeId: null,
          name: asset.name,
          path: asset.path,
          sourceRange: null,
          properties: JSON.stringify({ source: 'meta' }),
          createdAt: now,
          updatedAt: now,
        });
      }
    });
    tx(assets);
  }

  /**
   * Run a raw SELECT query (for testing only).
   * @param {string} sql
   * @param {...any} params
   * @returns {any[]}
   */
  query(sql, ...params) {
    return this.#db.prepare(sql).all(...params);
  }

  /**
   * Upserts a metadata key/value pair.
   * @param {string} key
   * @param {string} value
   */
  setMetadata(key, value) {
    this.#stmtSetMetadata.run({ key, value });
  }

  /**
   * Returns the value for a metadata key, or null if not found.
   * @param {string} key
   * @returns {string|null}
   */
  getMetadata(key) {
    const row = this.#stmtGetMetadata.get(key);
    return row ? row.value : null;
  }

  /**
   * Runs fn inside a transaction. Rolls back if fn throws.
   * @param {() => void} fn
   */
  runInTransaction(fn) {
    const tx = this.#db.transaction(fn);
    tx();
  }

  /** Closes the database connection. */
  close() {
    if (this.#db && this.#db.open) {
      this.#db.close();
    }
  }
}
