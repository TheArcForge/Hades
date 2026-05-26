import { describe, test, expect } from '@jest/globals';
import { discoverCsFiles } from '../src/discovery.js';
import { mkdtempSync, mkdirSync, writeFileSync } from 'fs';
import { join } from 'path';
import { tmpdir } from 'os';

describe('discoverCsFiles', () => {
  let tempDir;

  beforeEach(() => {
    tempDir = mkdtempSync(join(tmpdir(), 'discovery-'));
  });

  test('finds .cs files recursively in given dirs', () => {
    const dir = join(tempDir, 'Assets', 'Scripts');
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, 'Foo.cs'), 'class Foo {}');
    writeFileSync(join(dir, 'Bar.cs'), 'class Bar {}');
    writeFileSync(join(dir, 'readme.txt'), 'not a cs file');

    const files = discoverCsFiles([join(tempDir, 'Assets')]);
    expect(files.length).toBe(2);
    expect(files.every(f => f.endsWith('.cs'))).toBe(true);
  });

  test('handles multiple directories', () => {
    const dir1 = join(tempDir, 'DirA');
    const dir2 = join(tempDir, 'DirB');
    mkdirSync(dir1, { recursive: true });
    mkdirSync(dir2, { recursive: true });
    writeFileSync(join(dir1, 'A.cs'), 'class A {}');
    writeFileSync(join(dir2, 'B.cs'), 'class B {}');

    const files = discoverCsFiles([dir1, dir2]);
    expect(files.length).toBe(2);
  });

  test('returns empty array for non-existent directory', () => {
    const files = discoverCsFiles(['/tmp/nonexistent-dir-xyz']);
    expect(files).toEqual([]);
  });

  test('skips non-.cs files', () => {
    mkdirSync(join(tempDir, 'src'), { recursive: true });
    writeFileSync(join(tempDir, 'src', 'Foo.cs'), 'class Foo {}');
    writeFileSync(join(tempDir, 'src', 'Foo.cs.meta'), 'guid: abc');
    writeFileSync(join(tempDir, 'src', 'readme.md'), '# readme');

    const files = discoverCsFiles([join(tempDir, 'src')]);
    expect(files.length).toBe(1);
    expect(files[0]).toContain('Foo.cs');
  });
});
