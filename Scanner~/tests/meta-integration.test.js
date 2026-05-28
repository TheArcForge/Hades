import { describe, test, expect, beforeEach, afterEach } from '@jest/globals';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';
import { mkdtempSync, rmSync, writeFileSync, mkdirSync } from 'fs';
import { tmpdir } from 'os';
import { DbWriter } from '../src/db-writer.js';
import { scanMetaFiles, getSupportedExtensions } from '../src/meta-scanner.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

describe('MetaScanner integration', () => {
  let dbPath, db, tmpDir;

  beforeEach(() => {
    tmpDir = mkdtempSync(join(tmpdir(), 'hades-meta-'));
    dbPath = join(tmpDir, 'graph.db');
    db = new DbWriter(dbPath);
  });

  afterEach(() => {
    db.close();
    rmSync(tmpDir, { recursive: true, force: true });
  });

  test('meta-scan inserts asset nodes and they are queryable', () => {
    const assetsDir = join(tmpDir, 'Assets', 'Textures');
    mkdirSync(assetsDir, { recursive: true });
    writeFileSync(join(assetsDir, 'hero.png'), '');
    writeFileSync(join(assetsDir, 'hero.png.meta'),
      'fileFormatVersion: 2\nguid: aabb00112233445566778899aabbccdd\nTextureImporter:\n');

    const assets = scanMetaFiles([join(tmpDir, 'Assets')], ['.png']);
    expect(assets.length).toBe(1);

    db.insertMetaAssets(assets);

    const rows = db.query('SELECT * FROM nodes WHERE guid = ?', 'aabb00112233445566778899aabbccdd');
    expect(rows.length).toBe(1);
    expect(rows[0].type).toBe('Texture');
    expect(rows[0].name).toBe('hero');
  });

  test('meta-scan skips assets whose guid already exists', () => {
    const assetsDir = join(tmpDir, 'Assets');
    mkdirSync(assetsDir, { recursive: true });
    writeFileSync(join(assetsDir, 'dup.png'), '');
    writeFileSync(join(assetsDir, 'dup.png.meta'),
      'fileFormatVersion: 2\nguid: 11223344556677889900aabbccddeeff\nTextureImporter:\n');

    const assets = scanMetaFiles([assetsDir], ['.png']);
    db.insertMetaAssets(assets);
    db.insertMetaAssets(assets); // second call — should not duplicate

    const rows = db.query('SELECT * FROM nodes WHERE guid = ?', '11223344556677889900aabbccddeeff');
    expect(rows.length).toBe(1);
  });
});
