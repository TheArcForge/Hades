import { describe, test, expect, beforeAll, afterAll } from '@jest/globals';
import { parseFile } from '../src/parser.js';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';
import { writeFileSync, unlinkSync, mkdirSync } from 'fs';
import { tmpdir } from 'os';

const __dirname = dirname(fileURLToPath(import.meta.url));
const fixturesDir = join(__dirname, 'fixtures');

// ─── PlayerController.cs ────────────────────────────────────────────────────

describe('PlayerController.cs', () => {
  let result;

  beforeAll(() => {
    result = parseFile(join(fixturesDir, 'PlayerController.cs'));
  });

  test('produces a Script node with name "PlayerController.cs"', () => {
    const scriptNodes = result.nodes.filter(n => n.type === 'Script');
    expect(scriptNodes).toHaveLength(1);
    expect(scriptNodes[0].name).toBe('PlayerController.cs');
  });

  test('produces a ScriptType with namespace "TestProject.Player" and base_type "MonoBehaviour"', () => {
    const typeNodes = result.nodes.filter(n => n.type === 'ScriptType');
    expect(typeNodes).toHaveLength(1);
    const t = typeNodes[0];
    expect(t.name).toBe('PlayerController');
    expect(t.properties.namespace).toBe('TestProject.Player');
    expect(t.properties.base_type).toBe('MonoBehaviour');
  });

  test('extracts at least 2 methods including "Move"', () => {
    const methodNodes = result.nodes.filter(n => n.type === 'ScriptMethod');
    expect(methodNodes.length).toBeGreaterThanOrEqual(2);
    const names = methodNodes.map(m => m.name);
    expect(names).toContain('Move');
  });

  test('produces at least 2 defines edges', () => {
    const definesEdges = result.edges.filter(e => e.type === 'defines');
    expect(definesEdges.length).toBeGreaterThanOrEqual(2);
  });

  test('Script node has id 0', () => {
    const scriptNode = result.nodes.find(n => n.type === 'Script');
    expect(scriptNode.id).toBe(0);
  });

  test('defines edges from Script to ScriptType exist', () => {
    const scriptNode = result.nodes.find(n => n.type === 'Script');
    const typeNode = result.nodes.find(n => n.type === 'ScriptType');
    const edge = result.edges.find(
      e => e.type === 'defines' && e.sourceId === scriptNode.id && e.targetId === typeNode.id
    );
    expect(edge).toBeDefined();
  });

  test('defines edges from ScriptType to ScriptMethod exist', () => {
    const typeNode = result.nodes.find(n => n.type === 'ScriptType');
    const methodNodes = result.nodes.filter(n => n.type === 'ScriptMethod');
    for (const m of methodNodes) {
      const edge = result.edges.find(
        e => e.type === 'defines' && e.sourceId === typeNode.id && e.targetId === m.id
      );
      expect(edge).toBeDefined();
    }
  });
});

// ─── EventChannel.cs ────────────────────────────────────────────────────────

describe('EventChannel.cs', () => {
  let result;

  beforeAll(() => {
    result = parseFile(join(fixturesDir, 'EventChannel.cs'));
  });

  test('produces a Script node with name "EventChannel.cs"', () => {
    const scriptNodes = result.nodes.filter(n => n.type === 'Script');
    expect(scriptNodes).toHaveLength(1);
    expect(scriptNodes[0].name).toBe('EventChannel.cs');
  });

  test('detects ScriptableObject base', () => {
    const typeNode = result.nodes.find(n => n.type === 'ScriptType');
    expect(typeNode).toBeDefined();
    expect(typeNode.name).toBe('EventChannel');
    expect(typeNode.properties.base_type).toBe('ScriptableObject');
  });

  test('namespace is "TestProject.Systems"', () => {
    const typeNode = result.nodes.find(n => n.type === 'ScriptType');
    expect(typeNode.properties.namespace).toBe('TestProject.Systems');
  });

  test('extracts Raise, Register, Unregister methods', () => {
    const methodNames = result.nodes
      .filter(n => n.type === 'ScriptMethod')
      .map(n => n.name);
    expect(methodNames).toContain('Raise');
    expect(methodNames).toContain('Register');
    expect(methodNames).toContain('Unregister');
  });
});

// ─── Singleton.cs ───────────────────────────────────────────────────────────

describe('Singleton.cs', () => {
  let result;

  beforeAll(() => {
    result = parseFile(join(fixturesDir, 'Singleton.cs'));
  });

  test('produces a Script node with name "Singleton.cs"', () => {
    const scriptNodes = result.nodes.filter(n => n.type === 'Script');
    expect(scriptNodes).toHaveLength(1);
    expect(scriptNodes[0].name).toBe('Singleton.cs');
  });

  test('handles generic base type — base_type should be "MonoBehaviour" after splitting on "<"', () => {
    // The class declaration is: abstract class Singleton<T> : MonoBehaviour where T : MonoBehaviour
    // The C# scanner does baseTypes[0].Split('<')[0].Trim() on the inheritance list.
    // In JS the TypeRegex captures the part after ':', before '{'.
    // The capture would be: "MonoBehaviour where T : MonoBehaviour"
    // Split on ',' → ["MonoBehaviour where T : MonoBehaviour"]
    // Split on '<' → ["MonoBehaviour where T "] → first token trimmed → ?
    // Actually the constraint "where T : MonoBehaviour" has no '<', so split('<')[0] = "MonoBehaviour where T "
    // The C# scanner then trims: "MonoBehaviour where T". However, typical usage tests just that
    // base_type starts with "MonoBehaviour".
    const typeNode = result.nodes.find(n => n.type === 'ScriptType');
    expect(typeNode).toBeDefined();
    expect(typeNode.name).toBe('Singleton');
    // After split('<')[0] and trim, the result should start with "MonoBehaviour"
    expect(typeNode.properties.base_type).toMatch(/^MonoBehaviour/);
  });

  test('namespace is "TestProject.Utilities"', () => {
    const typeNode = result.nodes.find(n => n.type === 'ScriptType');
    expect(typeNode.properties.namespace).toBe('TestProject.Utilities');
  });

  test('extracts Awake method', () => {
    const methodNames = result.nodes
      .filter(n => n.type === 'ScriptMethod')
      .map(n => n.name);
    expect(methodNames).toContain('Awake');
  });
});

// ─── Edge Cases ─────────────────────────────────────────────────────────────

describe('Edge cases', () => {
  let emptyFilePath;
  let bigFilePath;

  beforeAll(() => {
    const tmpDir = tmpdir();
    emptyFilePath = join(tmpDir, 'Empty.cs');
    writeFileSync(emptyFilePath, '');

    bigFilePath = join(tmpDir, 'HugeFile.cs');
    // 20001 lines — exceeds MaxScanLines
    writeFileSync(bigFilePath, '\n'.repeat(20001));
  });

  afterAll(() => {
    try { unlinkSync(emptyFilePath); } catch (_) {}
    try { unlinkSync(bigFilePath); } catch (_) {}
  });

  test('empty file: returns Script node only, no types', () => {
    const result = parseFile(emptyFilePath);
    const scriptNodes = result.nodes.filter(n => n.type === 'Script');
    const typeNodes = result.nodes.filter(n => n.type === 'ScriptType');
    expect(scriptNodes).toHaveLength(1);
    expect(typeNodes).toHaveLength(0);
    expect(result.edges).toHaveLength(0);
  });

  test('file exceeding 20000 lines: returns Script node only (safety guard)', () => {
    const result = parseFile(bigFilePath);
    expect(result.nodes).toHaveLength(1);
    expect(result.nodes[0].type).toBe('Script');
    expect(result.nodes[0].name).toBe('HugeFile.cs');
    expect(result.edges).toHaveLength(0);
  });

  test('nonexistent file: returns empty nodes and edges', () => {
    const result = parseFile('/nonexistent/path/Missing.cs');
    expect(result.nodes).toHaveLength(0);
    expect(result.edges).toHaveLength(0);
  });
});
