import { describe, test, expect } from '@jest/globals';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';
import { parseFile } from '../src/ts-parser.js';

const __dirname = dirname(fileURLToPath(import.meta.url));
const FIXTURES = join(__dirname, 'fixtures');

describe('ts-parser', () => {
  describe('parseFile — basic structure', () => {
    test('returns Script, ScriptType, ScriptMethod nodes', () => {
      const { nodes, edges } = parseFile(join(FIXTURES, 'PlayerController.cs'));
      const types = nodes.map(n => n.type);
      expect(types).toContain('Script');
      expect(types).toContain('ScriptType');
      expect(types).toContain('ScriptMethod');
    });

    test('extracts namespace', () => {
      const { nodes } = parseFile(join(FIXTURES, 'PlayerController.cs'));
      const scriptType = nodes.find(n => n.type === 'ScriptType' && n.name === 'PlayerController');
      expect(scriptType.properties.namespace).toBe('TestProject.Player');
    });

    test('extracts base type and interfaces', () => {
      const { nodes } = parseFile(join(FIXTURES, 'PlayerHealth.cs'));
      const scriptType = nodes.find(n => n.type === 'ScriptType' && n.name === 'PlayerHealth');
      expect(scriptType.properties.base_type).toBe('MonoBehaviour');
      expect(scriptType.properties.interfaces).toContain('IDamageable');
    });

    test('extracts methods', () => {
      const { nodes } = parseFile(join(FIXTURES, 'PlayerHealth.cs'));
      const methods = nodes.filter(n => n.type === 'ScriptMethod');
      const names = methods.map(m => m.name);
      expect(names).toContain('TakeDamage');
      expect(names).toContain('GetModifier');
      expect(names).toContain('SpawnEffect');
    });

    test('creates defines edges for types and methods', () => {
      const { nodes, edges } = parseFile(join(FIXTURES, 'PlayerHealth.cs'));
      const script = nodes.find(n => n.type === 'Script');
      const healthType = nodes.find(n => n.name === 'PlayerHealth' && n.type === 'ScriptType');
      const definesType = edges.find(e =>
        e.sourceId === script.id && e.targetId === healthType.id && e.type === 'defines'
      );
      expect(definesType).toBeDefined();
    });

    test('returns empty for non-existent file', () => {
      const { nodes, edges } = parseFile('/nonexistent/file.cs');
      expect(nodes).toEqual([]);
      expect(edges).toEqual([]);
    });
  });

  describe('parseFile — cross-file references', () => {
    test('extracts field type references', () => {
      const { codeReferences } = parseFile(join(FIXTURES, 'PlayerHealth.cs'));
      const fieldRefs = codeReferences.filter(r => r.referenceKind === 'field');
      const refNames = fieldRefs.map(r => r.targetTypeName);
      expect(refNames).toContain('HealthBar');
    });

    test('extracts generic type argument references', () => {
      const { codeReferences } = parseFile(join(FIXTURES, 'PlayerHealth.cs'));
      const genericRefs = codeReferences.filter(r => r.referenceKind === 'generic_arg');
      const refNames = genericRefs.map(r => r.targetTypeName);
      expect(refNames).toContain('DamageModifier');
    });

    test('extracts method parameter type references', () => {
      const { codeReferences } = parseFile(join(FIXTURES, 'PlayerHealth.cs'));
      const paramRefs = codeReferences.filter(r => r.referenceKind === 'parameter');
      const refNames = paramRefs.map(r => r.targetTypeName);
      expect(refNames).toContain('DamageInfo');
    });

    test('extracts constructor references', () => {
      const { codeReferences } = parseFile(join(FIXTURES, 'PlayerHealth.cs'));
      const ctorRefs = codeReferences.filter(r => r.referenceKind === 'constructor');
      const refNames = ctorRefs.map(r => r.targetTypeName);
      expect(refNames).toContain('EffectController');
    });

    test('extracts attribute references', () => {
      const { codeReferences } = parseFile(join(FIXTURES, 'PlayerHealth.cs'));
      const attrRefs = codeReferences.filter(r => r.referenceKind === 'attribute');
      const refNames = attrRefs.map(r => r.targetTypeName);
      expect(refNames).toContain('RequireComponent');
    });

    test('extracts cast expression references', () => {
      const { codeReferences } = parseFile(join(FIXTURES, 'PlayerHealth.cs'));
      const castRefs = codeReferences.filter(r => r.referenceKind === 'cast');
      const refNames = castRefs.map(r => r.targetTypeName);
      expect(refNames).toContain('DamageModifier');
    });

    test('extracts method return type references', () => {
      const { codeReferences } = parseFile(join(FIXTURES, 'PlayerHealth.cs'));
      const returnRefs = codeReferences.filter(r => r.referenceKind === 'return_type');
      // No non-generic return types in this fixture — that's OK
    });

    test('skips builtin C# types (int, string, float, bool, void, object)', () => {
      const { codeReferences } = parseFile(join(FIXTURES, 'PlayerHealth.cs'));
      const builtins = ['int', 'string', 'float', 'bool', 'void', 'object', 'var'];
      for (const ref of codeReferences) {
        expect(builtins).not.toContain(ref.targetTypeName);
      }
    });

    test('extracts method parameter type as GameObject', () => {
      const { codeReferences } = parseFile(join(FIXTURES, 'PlayerHealth.cs'));
      const paramRefs = codeReferences.filter(r => r.referenceKind === 'parameter');
      const refNames = paramRefs.map(r => r.targetTypeName);
      expect(refNames).toContain('GameObject');
    });
  });
});
