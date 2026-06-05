import { describe, test, expect, beforeEach, afterEach } from '@jest/globals';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'fs';
import { join } from 'path';
import { tmpdir } from 'os';
import Database from 'better-sqlite3';
import { scan } from '../index.js';
import { DbWriter } from '../src/db-writer.js';

let tempDir;
let dbPath;

beforeEach(() => {
  tempDir = mkdtempSync(join(tmpdir(), 'hades-integration-'));
  dbPath = join(tempDir, 'graph.db');
});

afterEach(() => {
  rmSync(tempDir, { recursive: true, force: true });
});

// ─── Fixture helper ──────────────────────────────────────────────────────────

function createFixtureProject(root) {
  const scripts = join(root, 'Assets', 'Scripts');
  mkdirSync(scripts, { recursive: true });

  writeFileSync(join(scripts, 'Foo.cs'), `
namespace MyGame {
  public class Foo : MonoBehaviour {
    public void DoThing(int x) {}
  }
}
`);
  writeFileSync(join(scripts, 'Foo.cs.meta'), 'fileFormatVersion: 2\nguid: aaaa1111bbbb2222cccc3333dddd4444\n');

  writeFileSync(join(scripts, 'Bar.cs'), `
namespace MyGame {
  public class Bar : ScriptableObject {
    public void Init() {}
  }
}
`);
  writeFileSync(join(scripts, 'Bar.cs.meta'), 'fileFormatVersion: 2\nguid: eeee5555ffff6666aaaa7777bbbb8888\n');
}

// Helper: open DB read-only for verification
function openReadOnly(path) {
  return new Database(path, { readonly: true });
}

// ─── Tests ───────────────────────────────────────────────────────────────────

describe('integration: full scan', () => {
  test('scans 2 .cs files, writes nodes, edges, scanned_assets, and pending_edges', async () => {
    const projectRoot = join(tempDir, 'project');
    mkdirSync(projectRoot, { recursive: true });
    createFixtureProject(projectRoot);

    const dirs = [join(projectRoot, 'Assets', 'Scripts')];
    const writer = new DbWriter(dbPath);

    const code = await scan({
      db: writer,
      mode: 'full',
      dirs,
      projectRoot,
      scannerVersion: 2,
      tier: 'project',
    });

    writer.close();

    expect(code).toBe(0);

    const db = openReadOnly(dbPath);

    // Should have Script nodes with the right GUIDs
    const scriptNodes = db.prepare("SELECT * FROM nodes WHERE type = 'Script'").all();
    expect(scriptNodes.length).toBe(2);

    const guids = scriptNodes.map(n => n.guid).sort();
    expect(guids).toContain('aaaa1111bbbb2222cccc3333dddd4444');
    expect(guids).toContain('eeee5555ffff6666aaaa7777bbbb8888');

    // Should have ScriptType nodes
    const typeNodes = db.prepare("SELECT * FROM nodes WHERE type = 'ScriptType'").all();
    expect(typeNodes.length).toBe(2);
    const typeNames = typeNodes.map(n => n.name).sort();
    expect(typeNames).toContain('Foo');
    expect(typeNames).toContain('Bar');

    // Should have ScriptMethod nodes
    const methodNodes = db.prepare("SELECT * FROM nodes WHERE type = 'ScriptMethod'").all();
    expect(methodNodes.length).toBeGreaterThanOrEqual(2);
    const methodNames = methodNodes.map(n => n.name);
    expect(methodNames).toContain('DoThing');
    expect(methodNames).toContain('Init');

    // Should have scanned_assets entries
    const assets = db.prepare('SELECT * FROM scanned_assets').all();
    expect(assets.length).toBe(2);
    const assetGuids = assets.map(a => a.guid).sort();
    expect(assetGuids).toContain('aaaa1111bbbb2222cccc3333dddd4444');
    expect(assetGuids).toContain('eeee5555ffff6666aaaa7777bbbb8888');
    expect(assets[0].scanner_version).toBe(2);

    // Should have defines edges (Script→ScriptType, ScriptType→ScriptMethod)
    const edges = db.prepare("SELECT * FROM edges WHERE type = 'defines'").all();
    expect(edges.length).toBeGreaterThanOrEqual(4); // 2 script→type + 2 type→method

    // Should have pending_edges for base types (neutral extends_or_implements)
    const pendingEdges = db.prepare('SELECT * FROM pending_edges').all();
    expect(pendingEdges.length).toBeGreaterThanOrEqual(2); // at least one per file

    const pendingTypes = pendingEdges.map(p => p.edge_type);
    expect(pendingTypes).toContain('extends_or_implements');

    const pendingTargets = pendingEdges.map(p => p.target_type_name).sort();
    expect(pendingTargets).toContain('MonoBehaviour');
    expect(pendingTargets).toContain('ScriptableObject');

    db.close();
  });

  test('Script nodes carry correct GUIDs from .meta files', () => {
    const projectRoot = join(tempDir, 'project');
    mkdirSync(projectRoot, { recursive: true });
    createFixtureProject(projectRoot);

    const dirs = [join(projectRoot, 'Assets', 'Scripts')];
    const writer = new DbWriter(dbPath);

    scan({
      db: writer,
      mode: 'full',
      dirs,
      projectRoot,
      scannerVersion: 2,
      tier: 'project',
    });

    writer.close();

    const db = openReadOnly(dbPath);
    const fooScript = db.prepare("SELECT * FROM nodes WHERE type='Script' AND guid='aaaa1111bbbb2222cccc3333dddd4444'").get();
    const barScript = db.prepare("SELECT * FROM nodes WHERE type='Script' AND guid='eeee5555ffff6666aaaa7777bbbb8888'").get();
    db.close();

    expect(fooScript).toBeDefined();
    expect(barScript).toBeDefined();
  });
});

describe('integration: warm scan (cache hit)', () => {
  test('second scan with no changes does not duplicate nodes', async () => {
    const projectRoot = join(tempDir, 'project');
    mkdirSync(projectRoot, { recursive: true });
    createFixtureProject(projectRoot);

    const dirs = [join(projectRoot, 'Assets', 'Scripts')];

    // First scan
    const writer1 = new DbWriter(dbPath);
    await scan({ db: writer1, mode: 'full', dirs, projectRoot, scannerVersion: 2, tier: 'project' });
    writer1.close();

    const db1 = openReadOnly(dbPath);
    const nodeCount1 = db1.prepare('SELECT COUNT(*) as c FROM nodes').get().c;
    db1.close();

    // Second scan — same files, same hashes
    const writer2 = new DbWriter(dbPath);
    await scan({ db: writer2, mode: 'full', dirs, projectRoot, scannerVersion: 2, tier: 'project' });
    writer2.close();

    const db2 = openReadOnly(dbPath);
    const nodeCount2 = db2.prepare('SELECT COUNT(*) as c FROM nodes').get().c;
    db2.close();

    expect(nodeCount2).toBe(nodeCount1);
  });
});

describe('integration: incremental scan', () => {
  test('re-scans only the modified file and updates its nodes', async () => {
    const projectRoot = join(tempDir, 'project');
    mkdirSync(projectRoot, { recursive: true });
    createFixtureProject(projectRoot);

    const dirs = [join(projectRoot, 'Assets', 'Scripts')];
    const fooGuid = 'aaaa1111bbbb2222cccc3333dddd4444';

    // Full scan first
    const writer1 = new DbWriter(dbPath);
    await scan({ db: writer1, mode: 'full', dirs, projectRoot, scannerVersion: 2, tier: 'project' });
    writer1.close();

    const db1 = openReadOnly(dbPath);
    const barNodeCount1 = db1.prepare("SELECT COUNT(*) as c FROM nodes WHERE guid='eeee5555ffff6666aaaa7777bbbb8888'").get().c;
    db1.close();

    // Modify Foo.cs — add a new method
    writeFileSync(join(projectRoot, 'Assets', 'Scripts', 'Foo.cs'), `
namespace MyGame {
  public class Foo : MonoBehaviour {
    public void DoThing(int x) {}
    public void NewMethod() {}
  }
}
`);

    // Incremental scan for Foo's GUID only
    const writer2 = new DbWriter(dbPath);
    await scan({
      db: writer2,
      mode: 'incremental',
      dirs,
      projectRoot,
      scannerVersion: 2,
      guids: [fooGuid],
      tier: 'project',
    });
    writer2.close();

    const db2 = openReadOnly(dbPath);

    // Foo's nodes should be updated — should have NewMethod now
    const fooMethods = db2.prepare(
      "SELECT n.name FROM nodes n JOIN nodes s ON s.id = n.file_id WHERE s.guid = ? AND n.type = 'ScriptMethod'"
    ).all(fooGuid);
    const fooMethodNames = fooMethods.map(n => n.name);
    expect(fooMethodNames).toContain('NewMethod');

    // Bar's nodes should be unchanged
    const barNodeCount2 = db2.prepare("SELECT COUNT(*) as c FROM nodes WHERE guid='eeee5555ffff6666aaaa7777bbbb8888'").get().c;
    expect(barNodeCount2).toBe(barNodeCount1);

    db2.close();
  });

  test('incremental scan on unchanged file does not re-write nodes', async () => {
    const projectRoot = join(tempDir, 'project');
    mkdirSync(projectRoot, { recursive: true });
    createFixtureProject(projectRoot);

    const dirs = [join(projectRoot, 'Assets', 'Scripts')];
    const fooGuid = 'aaaa1111bbbb2222cccc3333dddd4444';

    // Full scan
    const writer1 = new DbWriter(dbPath);
    await scan({ db: writer1, mode: 'full', dirs, projectRoot, scannerVersion: 2, tier: 'project' });
    writer1.close();

    const db1 = openReadOnly(dbPath);
    const countBefore = db1.prepare('SELECT COUNT(*) as c FROM nodes').get().c;
    db1.close();

    // Incremental scan — file unchanged
    const writer2 = new DbWriter(dbPath);
    await scan({
      db: writer2,
      mode: 'incremental',
      dirs,
      projectRoot,
      scannerVersion: 2,
      guids: [fooGuid],
      tier: 'project',
    });
    writer2.close();

    const db2 = openReadOnly(dbPath);
    const countAfter = db2.prepare('SELECT COUNT(*) as c FROM nodes').get().c;
    db2.close();

    expect(countAfter).toBe(countBefore);
  });
});

describe('integration: incremental edge erosion + node leak (fix #5)', () => {
  test('re-scanning a referenced .cs preserves inbound code_references and does not leak old type nodes', async () => {
    const projectRoot = join(tempDir, 'project');
    const scripts = join(projectRoot, 'Assets', 'Scripts');
    mkdirSync(scripts, { recursive: true });

    const fooGuid = 'aaaa1111bbbb2222cccc3333dddd4444';
    const barGuid = 'eeee5555ffff6666aaaa7777bbbb8888';

    writeFileSync(join(scripts, 'Foo.cs'),
      'namespace MyGame {\n  public class Foo {\n    public void DoThing() {}\n  }\n}\n');
    writeFileSync(join(scripts, 'Foo.cs.meta'), 'fileFormatVersion: 2\nguid: ' + fooGuid + '\n');
    writeFileSync(join(scripts, 'Bar.cs'),
      'namespace MyGame {\n  public class Bar {\n    public void Init() {}\n  }\n}\n');
    writeFileSync(join(scripts, 'Bar.cs.meta'), 'fileFormatVersion: 2\nguid: ' + barGuid + '\n');

    const dirs = [scripts];

    // Full scan.
    const writer1 = new DbWriter(dbPath);
    await scan({ db: writer1, mode: 'full', dirs, projectRoot, scannerVersion: 2, tier: 'project' });
    writer1.close();

    // Simulate what C# ResolvePendingEdges does after a full build: a RESOLVED inbound
    // code_references edge from Bar's type to Foo's type (with properties).
    let rw = new Database(dbPath);
    const oldFooType = rw.prepare("SELECT id FROM nodes WHERE type='ScriptType' AND name='Foo'").get();
    const oldFooScript = rw.prepare("SELECT id FROM nodes WHERE type='Script' AND guid=?").get(fooGuid);
    const barType = rw.prepare("SELECT id FROM nodes WHERE type='ScriptType' AND name='Bar'").get();
    rw.prepare(
      "INSERT INTO edges (source_id, target_id, type, properties, created_at, updated_at) " +
      "VALUES (?, ?, 'code_references', '{\"reference_kind\":\"field\"}', 0, 0)"
    ).run(barType.id, oldFooType.id);
    rw.close();

    // Modify Foo.cs and incrementally re-scan ONLY Foo (the referenced file).
    writeFileSync(join(scripts, 'Foo.cs'),
      'namespace MyGame {\n  public class Foo {\n    public void DoThing() {}\n    public void NewThing() {}\n  }\n}\n');
    const writer2 = new DbWriter(dbPath);
    await scan({ db: writer2, mode: 'incremental', dirs, projectRoot, scannerVersion: 2, guids: [fooGuid], tier: 'project' });
    writer2.close();

    const db = openReadOnly(dbPath);

    // (a) No node leak: exactly ONE Foo ScriptType, re-created with a new id.
    //     Before the fix, deleteNodesByGuid left the NULL-guid old type node behind → 2.
    const fooTypes = db.prepare("SELECT id FROM nodes WHERE type='ScriptType' AND name='Foo'").all();
    expect(fooTypes.length).toBe(1);
    const newFooTypeId = fooTypes[0].id;
    expect(newFooTypeId).not.toBe(oldFooType.id);

    // (b) No stranded nodes still linked to the deleted old Script node.
    const stranded = db.prepare('SELECT COUNT(*) c FROM nodes WHERE file_id = ?').get(oldFooScript.id);
    expect(stranded.c).toBe(0);

    // (c) The inbound reference survives AND is re-pointed at the live Foo type (by name),
    //     with its properties intact. Before the fix it was cascade-deleted / stranded.
    const refs = db.prepare(
      "SELECT target_id, properties FROM edges WHERE type='code_references' AND source_id = ?"
    ).all(barType.id);
    expect(refs.length).toBe(1);
    expect(refs[0].target_id).toBe(newFooTypeId);
    expect(refs[0].properties).toBe('{"reference_kind":"field"}');

    db.close();
  });
});
