import { describe, test, expect } from '@jest/globals';
import { resolveGuid } from '../src/meta-resolver.js';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const fixtures = join(__dirname, 'fixtures');

describe('resolveGuid', () => {
  test('extracts 32-char hex GUID from .meta file', () => {
    const csPath = join(fixtures, 'PlayerController.cs');
    expect(resolveGuid(csPath)).toBe('a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6');
  });

  test('returns null when .meta file has no guid line', () => {
    const csPath = join(fixtures, 'NoGuid.cs');
    expect(resolveGuid(csPath)).toBeNull();
  });

  test('returns null when .meta file does not exist', () => {
    expect(resolveGuid('/tmp/nonexistent.cs')).toBeNull();
  });
});
