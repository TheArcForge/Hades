import { readFileSync, existsSync } from 'fs';
import { basename } from 'path';

// Direct port of ScriptScanner.cs regexes
const NamespaceRegex = /namespace\s+([\w.]+)/;

const TypeRegex = /(?:public|internal|private|protected)?\s*(?:abstract|sealed|static|partial)?\s*(?:class|struct|interface|enum)\s+(\w+)(?:<[^>]+>)?(?:\s*:\s*([^{]+))?/g;

const MethodRegex = /\s+(?:(?:public|private|protected|internal|static|virtual|override|abstract|async|sealed)\s+)*(?:[\w<>\[\], \t]+)\s+(\w+)\s*\(([^)]*)\)/;

const MaxScanLines = 20000;

const KEYWORDS = new Set([
  'if', 'for', 'while', 'switch', 'foreach', 'catch', 'using', 'return',
  'new', 'get', 'set', 'typeof', 'nameof'
]);

/**
 * Get 1-based line number for a character index within content.
 */
function getLineNumber(content, charIndex) {
  let line = 1;
  for (let i = 0; i < charIndex && i < content.length; i++) {
    if (content[i] === '\n') line++;
  }
  return line;
}

/**
 * Find the 1-based line number where the type's closing brace is.
 * Mirrors C# FindTypeEndLine: starts scanning from startLine (1-based),
 * counts braces until the opening brace's matching close is found.
 */
function findTypeEndLine(lines, startLine) {
  let braceCount = 0;
  let foundOpen = false;
  for (let i = startLine - 1; i < lines.length; i++) {
    for (const c of lines[i]) {
      if (c === '{') { braceCount++; foundOpen = true; }
      if (c === '}') braceCount--;
      if (foundOpen && braceCount === 0) return i + 1;
    }
  }
  return lines.length;
}

/**
 * Parse a C# source file and return { nodes, edges }.
 *
 * Node shape: { type, name, path, id, sourceRange?, properties? }
 * Edge shape: { type, sourceId, targetId }
 *
 * id is a local integer: 0 = Script node, then incrementing per type/method.
 */
export function parseFile(filePath) {
  const nodes = [];
  const edges = [];

  if (!existsSync(filePath)) {
    return { nodes, edges };
  }

  const content = readFileSync(filePath, 'utf8');
  const lines = content.split('\n');

  if (lines.length > MaxScanLines) {
    // Safety guard: return Script node only
    const scriptNode = { type: 'Script', name: basename(filePath), path: filePath, id: 0 };
    nodes.push(scriptNode);
    return { nodes, edges };
  }

  let nextId = 0;

  // Script node (id=0)
  const scriptNode = { type: 'Script', name: basename(filePath), path: filePath, id: nextId++ };
  nodes.push(scriptNode);

  // Extract namespace
  const nsMatch = NamespaceRegex.exec(content);
  const currentNamespace = nsMatch ? nsMatch[1] : '';

  // Find all type declarations
  const typeRegex = new RegExp(TypeRegex.source, 'g');
  let typeMatch;
  while ((typeMatch = typeRegex.exec(content)) !== null) {
    const typeName = typeMatch[1];
    const baseTypesRaw = typeMatch[2] ? typeMatch[2].trim() : null;

    const lineNumber = getLineNumber(content, typeMatch.index);
    const endLine = findTypeEndLine(lines, lineNumber);

    const typeNode = {
      type: 'ScriptType',
      name: typeName,
      path: filePath,
      id: nextId++,
      sourceRange: `${filePath}:${lineNumber}:${endLine}`,
      properties: { namespace: currentNamespace }
    };

    if (baseTypesRaw) {
      const baseTypes = baseTypesRaw.split(',').map(b => b.trim()).filter(b => b.length > 0);
      if (baseTypes.length > 0) {
        // Strip generics: "MonoBehaviour" from "MonoBehaviour where T : MonoBehaviour" etc.
        // The C# code does baseTypes[0].Split('<')[0].Trim()
        const primaryBase = baseTypes[0].split('<')[0].trim();
        typeNode.properties.base_type = primaryBase;
        if (baseTypes.length > 1) {
          typeNode.properties.interfaces = baseTypes.slice(1).map(b => b.trim());
        }
      }
    }

    nodes.push(typeNode);
    edges.push({ type: 'defines', sourceId: scriptNode.id, targetId: typeNode.id });

    // Find methods within this type's line range
    for (let li = lineNumber - 1; li < endLine && li < lines.length; li++) {
      const methodMatch = MethodRegex.exec(lines[li]);
      if (!methodMatch) continue;

      const methodName = methodMatch[1];
      if (KEYWORDS.has(methodName)) continue;

      const methodNode = {
        type: 'ScriptMethod',
        name: methodName,
        path: filePath,
        id: nextId++,
        sourceRange: `${filePath}:${li + 1}`,
        properties: { parameters: methodMatch[2].trim() }
      };
      nodes.push(methodNode);
      edges.push({ type: 'defines', sourceId: typeNode.id, targetId: methodNode.id });
    }
  }

  return { nodes, edges };
}
