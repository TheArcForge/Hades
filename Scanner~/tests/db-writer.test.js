import { describe, test, expect, beforeEach, afterEach } from '@jest/globals';
import { mkdtempSync, rmSync } from 'fs';
import { join } from 'path';
import { tmpdir } from 'os';
import Database from 'better-sqlite3';
import { DbWriter } from '../src/db-writer.js';

let tempDir;
let dbPath;
let writer;

beforeEach(() => {
  tempDir = mkdtempSync(join(tmpdir(), 'hades-db-'));
  dbPath = join(tempDir, 'graph.db');
  writer = new DbWriter(dbPath);
});

afterEach(() => {
  writer.close();
  rmSync(tempDir, { recursive: true, force: true });
});

// Helper: open db read-only for verification
function openReadOnly() {
  return new Database(dbPath, { readonly: true });
}

describe('DbWriter', () => {
  describe('schema creation', () => {
    test('creates all tables matching Hades schema', () => {
      const db = openReadOnly();
      const tables = db.prepare(
        "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"
      ).all().map(r => r.name);
      db.close();

      expect(tables).toContain('nodes');
      expect(tables).toContain('edges');
      expect(tables).toContain('pending_edges');
      expect(tables).toContain('scanned_assets');
      expect(tables).toContain('graph_metadata');
    });

    test('creates all indexes on nodes', () => {
      const db = openReadOnly();
      const indexes = db.prepare(
        "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='nodes' ORDER BY name"
      ).all().map(r => r.name);
      db.close();

      expect(indexes).toContain('idx_nodes_type');
      expect(indexes).toContain('idx_nodes_guid');
      expect(indexes).toContain('idx_nodes_path');
      expect(indexes).toContain('idx_nodes_parent');
      expect(indexes).toContain('idx_nodes_name_type');
      expect(indexes).toContain('idx_nodes_tier');
      expect(indexes).toContain('idx_nodes_guid_fileid');
    });

    test('creates all indexes on edges', () => {
      const db = openReadOnly();
      const indexes = db.prepare(
        "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='edges' ORDER BY name"
      ).all().map(r => r.name);
      db.close();

      expect(indexes).toContain('idx_edges_source_type');
      expect(indexes).toContain('idx_edges_target_type');
      expect(indexes).toContain('idx_edges_type');
      expect(indexes).toContain('idx_edges_unique');
    });

    test('WAL journal mode is set', () => {
      const db = openReadOnly();
      const row = db.prepare('PRAGMA journal_mode').get();
      db.close();
      expect(row.journal_mode).toBe('wal');
    });
  });

  describe('insertNode', () => {
    test('writes a node with correct columns and returns row id', () => {
      const id = writer.insertNode({
        type: 'MonoBehaviour',
        name: 'PlayerController',
        path: 'Assets/Scripts/PlayerController.cs',
        guid: 'abc123',
        tier: 'project',
      });

      expect(typeof id).toBe('number');
      expect(id).toBeGreaterThan(0);

      const db = openReadOnly();
      const row = db.prepare('SELECT * FROM nodes WHERE id = ?').get(id);
      db.close();

      expect(row.type).toBe('MonoBehaviour');
      expect(row.name).toBe('PlayerController');
      expect(row.path).toBe('Assets/Scripts/PlayerController.cs');
      expect(row.guid).toBe('abc123');
      expect(row.tier).toBe('project');
      expect(typeof row.created_at).toBe('number');
      expect(row.created_at).toBeGreaterThan(0);
      expect(row.updated_at).toBe(row.created_at);
    });

    test('uses default tier when not specified', () => {
      const id = writer.insertNode({ type: 'Class', name: 'Foo' });
      const db = openReadOnly();
      const row = db.prepare('SELECT tier FROM nodes WHERE id = ?').get(id);
      db.close();
      expect(row.tier).toBe('project');
    });

    test('setTier changes the default tier', () => {
      writer.setTier('engine');
      const id = writer.insertNode({ type: 'Class', name: 'Bar' });
      const db = openReadOnly();
      const row = db.prepare('SELECT tier FROM nodes WHERE id = ?').get(id);
      db.close();
      expect(row.tier).toBe('engine');
    });

    test('stores optional fields: fileId, parentNodeId, sourceRange, properties', () => {
      const parentId = writer.insertNode({ type: 'File', name: 'root.cs' });
      const propsJson = JSON.stringify({ isAbstract: true });
      const id = writer.insertNode({
        type: 'Method',
        name: 'Update',
        fileId: 42,
        parentNodeId: parentId,
        sourceRange: '10:1-20:1',
        properties: propsJson,
      });

      const db = openReadOnly();
      const row = db.prepare('SELECT * FROM nodes WHERE id = ?').get(id);
      db.close();

      expect(row.file_id).toBe(42);
      expect(row.parent_node_id).toBe(parentId);
      expect(row.source_range).toBe('10:1-20:1');
      expect(row.properties).toBe(propsJson);
    });

    test('timestamps are Unix epoch seconds', () => {
      const before = Math.floor(Date.now() / 1000);
      const id = writer.insertNode({ type: 'T', name: 'n' });
      const after = Math.floor(Date.now() / 1000);

      const db = openReadOnly();
      const row = db.prepare('SELECT created_at FROM nodes WHERE id = ?').get(id);
      db.close();

      expect(row.created_at).toBeGreaterThanOrEqual(before);
      expect(row.created_at).toBeLessThanOrEqual(after);
    });
  });

  describe('insertEdge', () => {
    test('writes an edge between two nodes', () => {
      const src = writer.insertNode({ type: 'Class', name: 'A' });
      const tgt = writer.insertNode({ type: 'Class', name: 'B' });
      writer.insertEdge(src, tgt, 'Inherits');

      const db = openReadOnly();
      const row = db.prepare('SELECT * FROM edges WHERE source_id = ? AND target_id = ?').get(src, tgt);
      db.close();

      expect(row.type).toBe('Inherits');
      expect(row.source_id).toBe(src);
      expect(row.target_id).toBe(tgt);
      expect(typeof row.created_at).toBe('number');
    });

    test('stores optional properties JSON', () => {
      const src = writer.insertNode({ type: 'Class', name: 'A' });
      const tgt = writer.insertNode({ type: 'Class', name: 'B' });
      const props = JSON.stringify({ weight: 1 });
      writer.insertEdge(src, tgt, 'Uses', props);

      const db = openReadOnly();
      const row = db.prepare('SELECT properties FROM edges WHERE source_id = ? AND target_id = ?').get(src, tgt);
      db.close();

      expect(row.properties).toBe(props);
    });

    test('duplicate edge (same source, target, type) does not throw — upsert/ignore', () => {
      const src = writer.insertNode({ type: 'Class', name: 'A' });
      const tgt = writer.insertNode({ type: 'Class', name: 'B' });
      expect(() => {
        writer.insertEdge(src, tgt, 'Ref');
        writer.insertEdge(src, tgt, 'Ref');
      }).not.toThrow();
    });
  });

  describe('recordScannedAsset', () => {
    test('writes hash, version and scanned_at', () => {
      const before = Math.floor(Date.now() / 1000);
      writer.recordScannedAsset('guid-001', 'abc123hash', 2);
      const after = Math.floor(Date.now() / 1000);

      const db = openReadOnly();
      const row = db.prepare('SELECT * FROM scanned_assets WHERE guid = ?').get('guid-001');
      db.close();

      expect(row.content_hash).toBe('abc123hash');
      expect(row.scanner_version).toBe(2);
      expect(row.scanned_at).toBeGreaterThanOrEqual(before);
      expect(row.scanned_at).toBeLessThanOrEqual(after);
    });

    test('upserts existing asset record', () => {
      writer.recordScannedAsset('guid-002', 'hash-v1', 1);
      writer.recordScannedAsset('guid-002', 'hash-v2', 2);

      const db = openReadOnly();
      const rows = db.prepare('SELECT * FROM scanned_assets WHERE guid = ?').all('guid-002');
      db.close();

      expect(rows.length).toBe(1);
      expect(rows[0].content_hash).toBe('hash-v2');
      expect(rows[0].scanner_version).toBe(2);
    });
  });

  describe('getScannedAssets', () => {
    test('returns a Map of cached assets by guid', () => {
      writer.recordScannedAsset('g1', 'hash1', 1);
      writer.recordScannedAsset('g2', 'hash2', 3);
      writer.recordScannedAsset('g3', 'hash3', 2);

      const result = writer.getScannedAssets(['g1', 'g3', 'g-missing']);

      expect(result).toBeInstanceOf(Map);
      expect(result.size).toBe(2);
      expect(result.get('g1')).toEqual({ contentHash: 'hash1', scannerVersion: 1 });
      expect(result.get('g3')).toEqual({ contentHash: 'hash3', scannerVersion: 2 });
      expect(result.has('g-missing')).toBe(false);
      expect(result.has('g2')).toBe(false);
    });

    test('returns empty Map for empty input', () => {
      const result = writer.getScannedAssets([]);
      expect(result).toBeInstanceOf(Map);
      expect(result.size).toBe(0);
    });
  });

  describe('insertPendingEdge', () => {
    test('stores a pending edge for later resolution', () => {
      writer.insertPendingEdge(10, 'TypeReference', 'UnityEngine.Transform', 'UnityEngine', 'src-guid-abc');

      const db = openReadOnly();
      const row = db.prepare('SELECT * FROM pending_edges').get();
      db.close();

      expect(row.source_node_id).toBe(10);
      expect(row.edge_type).toBe('TypeReference');
      expect(row.target_type_name).toBe('UnityEngine.Transform');
      expect(row.target_namespace).toBe('UnityEngine');
      expect(row.source_asset_guid).toBe('src-guid-abc');
      expect(typeof row.created_at).toBe('number');
    });

    test('allows null targetNamespace and sourceAssetGuid', () => {
      expect(() => {
        writer.insertPendingEdge(5, 'Uses', 'SomeClass', null, null);
      }).not.toThrow();

      const db = openReadOnly();
      const row = db.prepare('SELECT * FROM pending_edges').get();
      db.close();

      expect(row.target_namespace).toBeNull();
      expect(row.source_asset_guid).toBeNull();
    });
  });

  describe('deleteNodesByGuid', () => {
    test('removes nodes with matching guid', () => {
      writer.insertNode({ type: 'Class', name: 'A', guid: 'del-guid' });
      writer.insertNode({ type: 'Class', name: 'B', guid: 'del-guid' });
      writer.insertNode({ type: 'Class', name: 'C', guid: 'keep-guid' });

      writer.deleteNodesByGuid('del-guid');

      const db = openReadOnly();
      const deleted = db.prepare("SELECT * FROM nodes WHERE guid = 'del-guid'").all();
      const kept = db.prepare("SELECT * FROM nodes WHERE guid = 'keep-guid'").all();
      db.close();

      expect(deleted.length).toBe(0);
      expect(kept.length).toBe(1);
    });
  });

  describe('deletePendingEdgesBySourceAsset', () => {
    test('removes pending edges for a given source asset guid', () => {
      writer.insertPendingEdge(1, 'Ref', 'ClassA', null, 'asset-guid-1');
      writer.insertPendingEdge(2, 'Ref', 'ClassB', null, 'asset-guid-1');
      writer.insertPendingEdge(3, 'Ref', 'ClassC', null, 'asset-guid-2');

      writer.deletePendingEdgesBySourceAsset('asset-guid-1');

      const db = openReadOnly();
      const remaining = db.prepare('SELECT * FROM pending_edges').all();
      db.close();

      expect(remaining.length).toBe(1);
      expect(remaining[0].source_asset_guid).toBe('asset-guid-2');
    });
  });

  describe('setMetadata / getMetadata', () => {
    test('stores and retrieves a metadata value', () => {
      writer.setMetadata('scanner_version', '3');
      expect(writer.getMetadata('scanner_version')).toBe('3');
    });

    test('returns null for missing key', () => {
      expect(writer.getMetadata('nonexistent')).toBeNull();
    });

    test('upserts existing key', () => {
      writer.setMetadata('key', 'v1');
      writer.setMetadata('key', 'v2');
      expect(writer.getMetadata('key')).toBe('v2');
    });
  });

  describe('runInTransaction', () => {
    test('commits on success', () => {
      writer.runInTransaction(() => {
        writer.insertNode({ type: 'Class', name: 'TxNode' });
      });

      const db = openReadOnly();
      const rows = db.prepare("SELECT * FROM nodes WHERE name = 'TxNode'").all();
      db.close();

      expect(rows.length).toBe(1);
    });

    test('rolls back on error', () => {
      expect(() => {
        writer.runInTransaction(() => {
          writer.insertNode({ type: 'Class', name: 'RollbackNode' });
          throw new Error('intentional failure');
        });
      }).toThrow('intentional failure');

      const db = openReadOnly();
      const rows = db.prepare("SELECT * FROM nodes WHERE name = 'RollbackNode'").all();
      db.close();

      expect(rows.length).toBe(0);
    });
  });
});

describe('owner_guid', () => {
  test('insertNode persists owner_guid and deleteByOwnerGuid removes the whole set', () => {
    const root = writer.insertNode({
      type: 'Script', guid: 'script_guid', name: 'Player', path: 'Assets/Player.cs',
      ownerGuid: 'script_guid',
    });
    writer.insertNode({
      type: 'ScriptType', name: 'Player', fileId: root, ownerGuid: 'script_guid',
    });

    const dbBefore = openReadOnly();
    expect(dbBefore.prepare("SELECT COUNT(*) c FROM nodes WHERE owner_guid = 'script_guid'").get().c).toBe(2);
    dbBefore.close();

    writer.deleteByOwnerGuid('script_guid');

    const db = openReadOnly();
    const count = db.prepare("SELECT COUNT(*) c FROM nodes WHERE owner_guid = 'script_guid'").get().c;
    db.close();
    expect(count).toBe(0);
  });
});

describe('insertMetaAssets', () => {
  test('writes owner_guid and a sentinel scanned_assets row', () => {
    writer.insertMetaAssets([
      { guid: 'tex_guid', name: 'hero', path: 'Assets/hero.png', type: 'Texture' },
    ]);

    const db = openReadOnly();
    const node = db.prepare("SELECT guid, owner_guid FROM nodes WHERE guid = 'tex_guid'").get();
    const scanned = db.prepare("SELECT content_hash FROM scanned_assets WHERE guid = 'tex_guid'").get();
    db.close();

    expect(node.owner_guid).toBe('tex_guid');
    expect(scanned.content_hash).toBe('meta');
  });
});
