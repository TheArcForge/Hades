import { readFileSync, existsSync } from 'fs';
import { basename } from 'path';
import Parser from 'tree-sitter';
import CSharp from 'tree-sitter-c-sharp';

function createParser() {
  const p = new Parser();
  p.setLanguage(CSharp);
  return p;
}

const MaxScanLines = 20000;

const BUILTIN_TYPES = new Set([
  'void', 'bool', 'byte', 'sbyte', 'short', 'ushort', 'int', 'uint',
  'long', 'ulong', 'float', 'double', 'decimal', 'char', 'string',
  'object', 'dynamic', 'var', 'nint', 'nuint',
]);

/**
 * Walk all descendants of a node and yield each.
 */
function* walk(node) {
  yield node;
  for (const child of node.namedChildren) {
    yield* walk(child);
  }
}

/**
 * Extract the text name from a type node.
 * Handles: identifier, generic_name, qualified_name, nullable_type, array_type.
 * Returns null for predefined_type (builtins) or implicit_type (var).
 */
function getTypeName(typeNode) {
  if (!typeNode) return null;
  switch (typeNode.type) {
    case 'predefined_type':
    case 'implicit_type':
      return null; // builtin or var
    case 'identifier':
      return typeNode.text;
    case 'generic_name': {
      // e.g. List<T> — just the outer name
      const nameChild = typeNode.namedChildren.find(c => c.type === 'identifier');
      return nameChild ? nameChild.text : typeNode.text;
    }
    case 'qualified_name': {
      // e.g. System.Collections.Generic.List — take the last segment
      const nameChild = typeNode.childForFieldName('name');
      return nameChild ? nameChild.text : typeNode.text;
    }
    case 'nullable_type': {
      const inner = typeNode.namedChildren[0];
      return getTypeName(inner);
    }
    case 'array_type': {
      const inner = typeNode.childForFieldName('type');
      return getTypeName(inner);
    }
    default:
      return null;
  }
}

/**
 * Add a reference if the type name is not a builtin.
 */
function addRef(refs, sourceTypeName, typeName, referenceKind) {
  if (!typeName) return;
  if (BUILTIN_TYPES.has(typeName)) return;
  refs.push({ sourceTypeName, targetTypeName: typeName, referenceKind });
}

/**
 * Extract type arguments from a generic_name or type_argument_list node.
 */
function extractTypeArgs(node, refs, sourceTypeName) {
  const typeArgList = node.namedChildren.find(c => c.type === 'type_argument_list');
  if (!typeArgList) return;
  for (const argChild of typeArgList.namedChildren) {
    const argName = getTypeName(argChild);
    addRef(refs, sourceTypeName, argName, 'generic_arg');
    // If the arg itself is generic, recurse
    if (argChild.type === 'generic_name') {
      extractTypeArgs(argChild, refs, sourceTypeName);
    }
  }
}

/**
 * Extract namespace name from a namespace_declaration node.
 * Returns qualified name as string.
 */
function getNamespaceName(nsNode) {
  const nameNode = nsNode.childForFieldName('name');
  return nameNode ? nameNode.text : '';
}

/**
 * Collect all code references from within a class declaration subtree.
 */
function collectReferences(classNode, sourceTypeName) {
  const refs = [];

  for (const node of walk(classNode)) {
    switch (node.type) {
      case 'field_declaration': {
        // e.g. private HealthBar healthBar;
        const varDecl = node.namedChildren.find(c => c.type === 'variable_declaration');
        if (varDecl) {
          const typeNode = varDecl.childForFieldName('type');
          if (typeNode) {
            if (typeNode.type === 'generic_name') {
              // e.g. List<DamageModifier> — add the container as field ref (skip List itself if desired)
              // Per spec: field type → 'field', type args → 'generic_arg'
              extractTypeArgs(typeNode, refs, sourceTypeName);
            } else {
              const typeName = getTypeName(typeNode);
              addRef(refs, sourceTypeName, typeName, 'field');
            }
          }
        }
        break;
      }

      case 'parameter': {
        // method parameters
        const typeNode = node.childForFieldName('type');
        if (typeNode) {
          if (typeNode.type === 'generic_name') {
            const outerName = getTypeName(typeNode);
            addRef(refs, sourceTypeName, outerName, 'parameter');
            extractTypeArgs(typeNode, refs, sourceTypeName);
          } else {
            const typeName = getTypeName(typeNode);
            addRef(refs, sourceTypeName, typeName, 'parameter');
          }
        }
        break;
      }

      case 'object_creation_expression': {
        // new EffectController()
        const typeNode = node.childForFieldName('type');
        if (typeNode) {
          const typeName = getTypeName(typeNode);
          addRef(refs, sourceTypeName, typeName, 'constructor');
        }
        break;
      }

      case 'cast_expression': {
        // (DamageModifier)x
        const typeNode = node.childForFieldName('type');
        if (typeNode) {
          const typeName = getTypeName(typeNode);
          addRef(refs, sourceTypeName, typeName, 'cast');
        }
        break;
      }

      case 'attribute': {
        // [RequireComponent(...)]
        const nameNode = node.childForFieldName('name');
        if (nameNode) {
          const typeName = getTypeName(nameNode) ?? nameNode.text;
          addRef(refs, sourceTypeName, typeName, 'attribute');
        }
        break;
      }

      case 'method_declaration': {
        // return type
        const returnNode = node.childForFieldName('returns');
        if (returnNode && returnNode.type !== 'predefined_type' && returnNode.type !== 'implicit_type') {
          if (returnNode.type === 'generic_name') {
            // e.g. List<T> return type — skip the T type parameter, just note the outer
            const outerName = getTypeName(returnNode);
            // Only add if not a type parameter (we don't know type params statically easily)
            // For now add as return_type
            addRef(refs, sourceTypeName, outerName, 'return_type');
          } else {
            const typeName = getTypeName(returnNode);
            addRef(refs, sourceTypeName, typeName, 'return_type');
          }
        }
        break;
      }

      case 'local_declaration_statement': {
        // var or typed local variable
        const varDecl = node.namedChildren.find(c => c.type === 'variable_declaration');
        if (varDecl) {
          const typeNode = varDecl.childForFieldName('type');
          if (typeNode && typeNode.type !== 'implicit_type' && typeNode.type !== 'predefined_type') {
            if (typeNode.type === 'generic_name') {
              const outerName = getTypeName(typeNode);
              addRef(refs, sourceTypeName, outerName, 'local_var');
              extractTypeArgs(typeNode, refs, sourceTypeName);
            } else {
              const typeName = getTypeName(typeNode);
              addRef(refs, sourceTypeName, typeName, 'local_var');
            }
          }
        }
        break;
      }
    }
  }

  return refs;
}

/**
 * Parse a C# source file using tree-sitter.
 * Returns { nodes, edges, codeReferences }.
 *
 * Node shape: { type, name, path, id, sourceRange?, properties? }
 * Edge shape: { type, sourceId, targetId }
 * codeReference shape: { sourceTypeName, targetTypeName, referenceKind }
 */
export function parseFile(filePath) {
  const nodes = [];
  const edges = [];
  const codeReferences = [];

  if (!existsSync(filePath)) {
    return { nodes, edges, codeReferences };
  }

  const content = readFileSync(filePath, 'utf8');
  const lineCount = content.split('\n').length;

  if (lineCount > MaxScanLines) {
    const scriptNode = { type: 'Script', name: basename(filePath), path: filePath, id: 0 };
    nodes.push(scriptNode);
    return { nodes, edges, codeReferences };
  }

  const tree = createParser().parse(content);
  const root = tree.rootNode;

  let nextId = 0;

  // Script node (id=0)
  const scriptNode = { type: 'Script', name: basename(filePath), path: filePath, id: nextId++ };
  nodes.push(scriptNode);

  // Walk the AST looking for namespace and class declarations
  function processNode(node, currentNamespace) {
    switch (node.type) {
      case 'namespace_declaration':
      case 'file_scoped_namespace_declaration': {
        const ns = getNamespaceName(node);
        for (const child of node.namedChildren) {
          processNode(child, ns);
        }
        break;
      }

      case 'declaration_list':
      case 'compilation_unit': {
        for (const child of node.namedChildren) {
          processNode(child, currentNamespace);
        }
        break;
      }

      case 'class_declaration':
      case 'struct_declaration':
      case 'interface_declaration': {
        const nameNode = node.childForFieldName('name');
        const typeName = nameNode ? nameNode.text : null;
        if (!typeName) break;

        const startLine = node.startPosition.row + 1;
        const endLine = node.endPosition.row + 1;

        const typeNode = {
          type: 'ScriptType',
          name: typeName,
          path: filePath,
          id: nextId++,
          sourceRange: `${filePath}:${startLine}:${endLine}`,
          properties: { namespace: currentNamespace }
        };

        // Extract base type and interfaces from base_list
        const baseListNode = node.namedChildren.find(c => c.type === 'base_list');
        if (baseListNode) {
          // base_list children: identifiers and generic_names
          const baseItems = baseListNode.namedChildren
            .filter(c => c.type === 'identifier' || c.type === 'generic_name')
            .map(c => getTypeName(c))
            .filter(Boolean);

          if (baseItems.length > 0) {
            typeNode.properties.base_type = baseItems[0];
            if (baseItems.length > 1) {
              typeNode.properties.interfaces = baseItems.slice(1);
            }
          }
        }

        nodes.push(typeNode);
        edges.push({ type: 'defines', sourceId: scriptNode.id, targetId: typeNode.id });

        // Extract methods
        const bodyNode = node.childForFieldName('body');
        if (bodyNode) {
          for (const member of bodyNode.namedChildren) {
            if (member.type === 'method_declaration') {
              const methodNameNode = member.childForFieldName('name');
              const methodName = methodNameNode ? methodNameNode.text : null;
              if (!methodName) continue;

              const paramsNode = member.childForFieldName('parameters');
              const paramsText = paramsNode ? paramsNode.text.slice(1, -1).trim() : '';

              const methodStartLine = member.startPosition.row + 1;

              const methodNode = {
                type: 'ScriptMethod',
                name: methodName,
                path: filePath,
                id: nextId++,
                sourceRange: `${filePath}:${methodStartLine}`,
                properties: { parameters: paramsText }
              };
              nodes.push(methodNode);
              edges.push({ type: 'defines', sourceId: typeNode.id, targetId: methodNode.id });
            }
          }
        }

        // Collect cross-file references for this type
        const refs = collectReferences(node, typeName);
        codeReferences.push(...refs);

        break;
      }

      default:
        // Recurse into other nodes (e.g. using directives, etc.)
        for (const child of node.namedChildren) {
          processNode(child, currentNamespace);
        }
        break;
    }
  }

  processNode(root, '');

  return { nodes, edges, codeReferences };
}
