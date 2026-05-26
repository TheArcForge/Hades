import { describe, test, expect } from '@jest/globals';
import { computeContentHash } from '../src/hasher.js';
import { writeFileSync, unlinkSync, mkdtempSync } from 'fs';
import { join } from 'path';
import { tmpdir } from 'os';

describe('computeContentHash', () => {
  let tempDir;

  beforeEach(() => {
    tempDir = mkdtempSync(join(tmpdir(), 'hasher-'));
  });

  test('produces lowercase hex MD5 matching C# ComputeContentHash', () => {
    const filePath = join(tempDir, 'empty.txt');
    writeFileSync(filePath, '');
    expect(computeContentHash(filePath)).toBe('d41d8cd98f00b204e9800998ecf8427e');
  });

  test('hashes file content correctly', () => {
    const filePath = join(tempDir, 'hello.txt');
    writeFileSync(filePath, 'Hello, World!');
    expect(computeContentHash(filePath)).toBe('65a8e27d8879283831b664bd8b7f0ad4');
  });

  test('returns empty string for non-existent file', () => {
    expect(computeContentHash('/tmp/does-not-exist-xyz.txt')).toBe('');
  });
});
