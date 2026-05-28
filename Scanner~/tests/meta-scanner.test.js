import { describe, test, expect } from '@jest/globals';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';
import { scanMetaFiles } from '../src/meta-scanner.js';

const __dirname = dirname(fileURLToPath(import.meta.url));
const FIXTURES = join(__dirname, 'fixtures');

describe('scanMetaFiles', () => {
  test('discovers .png.meta and returns Texture node', () => {
    const results = scanMetaFiles([join(FIXTURES, 'textures')], ['.png']);
    const hero = results.find(r => r.name === 'hero');
    expect(hero).toBeDefined();
    expect(hero).toEqual({
      guid: 'aa11bb22cc33dd44ee55ff6677889900',
      name: 'hero',
      path: expect.stringContaining('textures/hero.png'),
      type: 'Texture',
    });
  });

  test('discovers .fbx.meta and returns Model node', () => {
    const results = scanMetaFiles([join(FIXTURES, 'models')], ['.fbx']);
    expect(results.length).toBe(1);
    expect(results[0]).toEqual({
      guid: 'ff00ee11dd22cc33bb44aa5566778899',
      name: 'enemy',
      path: expect.stringContaining('models/enemy.fbx'),
      type: 'Model',
    });
  });

  test('skips files with extensions not in filter', () => {
    const results = scanMetaFiles([join(FIXTURES, 'textures')], ['.fbx']);
    expect(results.length).toBe(0);
  });

  test('scans multiple directories', () => {
    const results = scanMetaFiles(
      [join(FIXTURES, 'textures'), join(FIXTURES, 'models')],
      ['.png', '.fbx']
    );
    // textures dir has hero.png and no-guid.png; only hero has a guid
    const guids = results.map(r => r.guid);
    expect(guids).toContain('aa11bb22cc33dd44ee55ff6677889900');
    expect(guids).toContain('ff00ee11dd22cc33bb44aa5566778899');
    expect(results.length).toBe(2);
  });

  test('handles non-existent directory gracefully', () => {
    const results = scanMetaFiles(['/nonexistent/path'], ['.png']);
    expect(results.length).toBe(0);
  });

  test('skips .meta files without guid line', () => {
    const results = scanMetaFiles([join(FIXTURES, 'textures')], ['.png']);
    const noGuid = results.find(r => r.name === 'no-guid');
    expect(noGuid).toBeUndefined();
  });
});
