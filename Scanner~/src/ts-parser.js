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
 * aliasMap (optional): maps alias identifiers to their target outer type names.
 */
function addRef(refs, sourceTypeName, typeName, referenceKind, aliasMap) {
  if (!typeName) return;
  if (BUILTIN_TYPES.has(typeName)) return;
  const resolved = (aliasMap && aliasMap.has(typeName)) ? aliasMap.get(typeName) : typeName;
  if (!resolved) return;
  if (BUILTIN_TYPES.has(resolved)) return;
  // de-dup check
  if (refs.some(r => r.sourceTypeName === sourceTypeName && r.targetTypeName === resolved && r.referenceKind === referenceKind)) return;
  refs.push({ sourceTypeName, targetTypeName: resolved, referenceKind });
}

/**
 * Extract type arguments from a generic_name or type_argument_list node.
 */
function extractTypeArgs(node, refs, sourceTypeName, aliasMap) {
  const typeArgList = node.namedChildren.find(c => c.type === 'type_argument_list');
  if (!typeArgList) return;
  for (const argChild of typeArgList.namedChildren) {
    const argName = getTypeName(argChild);
    addRef(refs, sourceTypeName, argName, 'generic_arg', aliasMap);
    // If the arg itself is generic, recurse
    if (argChild.type === 'generic_name') {
      extractTypeArgs(argChild, refs, sourceTypeName, aliasMap);
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
 * aliasMap: Map<aliasName, resolvedOuterTypeName> built from file-level using aliases.
 */
function collectReferences(classNode, sourceTypeName, aliasMap) {
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
              extractTypeArgs(typeNode, refs, sourceTypeName, aliasMap);
            } else {
              const typeName = getTypeName(typeNode);
              addRef(refs, sourceTypeName, typeName, 'field', aliasMap);
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
            addRef(refs, sourceTypeName, outerName, 'parameter', aliasMap);
            extractTypeArgs(typeNode, refs, sourceTypeName, aliasMap);
          } else {
            const typeName = getTypeName(typeNode);
            addRef(refs, sourceTypeName, typeName, 'parameter', aliasMap);
          }
        }
        break;
      }

      case 'object_creation_expression': {
        // new EffectController()
        const typeNode = node.childForFieldName('type');
        if (typeNode) {
          const typeName = getTypeName(typeNode);
          addRef(refs, sourceTypeName, typeName, 'constructor', aliasMap);
        }
        break;
      }

      case 'cast_expression': {
        // (DamageModifier)x
        const typeNode = node.childForFieldName('type');
        if (typeNode) {
          const typeName = getTypeName(typeNode);
          addRef(refs, sourceTypeName, typeName, 'cast', aliasMap);
        }
        break;
      }

      case 'attribute': {
        // [RequireComponent(...)]
        const nameNode = node.childForFieldName('name');
        if (nameNode) {
          const typeName = getTypeName(nameNode) ?? nameNode.text;
          addRef(refs, sourceTypeName, typeName, 'attribute', aliasMap);
        }
        break;
      }

      case 'property_declaration': {
        // e.g. public IList<Widget> Items { get; }
        const typeNode = node.childForFieldName('type');
        if (typeNode) {
          if (typeNode.type === 'generic_name') {
            const outerName = getTypeName(typeNode);
            addRef(refs, sourceTypeName, outerName, 'property', aliasMap);
            extractTypeArgs(typeNode, refs, sourceTypeName, aliasMap);
          } else {
            const typeName = getTypeName(typeNode);
            addRef(refs, sourceTypeName, typeName, 'property', aliasMap);
          }
        }
        break;
      }

      case 'method_declaration': {
        // return type
        const returnNode = node.childForFieldName('returns');
        if (returnNode && returnNode.type !== 'predefined_type' && returnNode.type !== 'implicit_type') {
          if (returnNode.type === 'generic_name') {
            const outerName = getTypeName(returnNode);
            addRef(refs, sourceTypeName, outerName, 'return_type', aliasMap);
            // Also capture the type arguments of a generic return type
            extractTypeArgs(returnNode, refs, sourceTypeName, aliasMap);
          } else {
            const typeName = getTypeName(returnNode);
            addRef(refs, sourceTypeName, typeName, 'return_type', aliasMap);
          }
        }
        break;
      }

      case 'invocation_expression': {
        // e.g. ServiceLocator.GetService<MyService>()
        // Only capture TYPE ARGUMENTS from generic method calls — not the method name itself.
        const calleeNode = node.namedChildren[0];
        if (calleeNode) {
          let genericName = null;
          if (calleeNode.type === 'generic_name') {
            genericName = calleeNode;
          } else if (calleeNode.type === 'member_access_expression') {
            // foo.Bar<T>() — the 'name' field is the generic_name
            const nameField = calleeNode.childForFieldName('name');
            if (nameField && nameField.type === 'generic_name') {
              genericName = nameField;
            }
          }
          if (genericName) {
            extractTypeArgs(genericName, refs, sourceTypeName, aliasMap);
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
              addRef(refs, sourceTypeName, outerName, 'local_var', aliasMap);
              extractTypeArgs(typeNode, refs, sourceTypeName, aliasMap);
            } else {
              const typeName = getTypeName(typeNode);
              addRef(refs, sourceTypeName, typeName, 'local_var', aliasMap);
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

  // Build a file-scoped alias map from `using Alias = Some.Namespace.Target;` directives.
  // Map<aliasIdentifier, resolvedOuterTypeName>
  // A using_directive is an alias when it has 2+ named children and the first is an identifier.
  const aliasMap = new Map();
  for (const child of root.namedChildren) {
    if (child.type !== 'using_directive') continue;
    const nc = child.namedChildren;
    if (nc.length < 2) continue;
    if (nc[0].type !== 'identifier') continue;
    // nc[0] is the alias; nc[1] is the target (qualified_name or identifier)
    const aliasName = nc[0].text;
    const targetNode = nc[1];
    const targetOuter = getTypeName(targetNode);
    if (aliasName && targetOuter) {
      aliasMap.set(aliasName, targetOuter);
    }
  }

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
      case 'interface_declaration':
      case 'record_declaration':
      case 'record_struct_declaration':
      case 'enum_declaration': {
        const nameNode = node.childForFieldName('name');
        const typeName = nameNode ? nameNode.text : null;
        if (!typeName) break;

        const startLine = node.startPosition.row + 1;
        const endLine = node.endPosition.row + 1;

        // Determine kind from declaration node type
        let kind;
        switch (node.type) {
          case 'interface_declaration':    kind = 'interface'; break;
          case 'enum_declaration':         kind = 'enum';      break;
          case 'struct_declaration':       kind = 'struct';    break;
          case 'record_struct_declaration':kind = 'struct';    break;
          case 'record_declaration':       kind = 'record';    break;
          default:                         kind = 'class';     break;
        }

        const typeNode = {
          type: 'ScriptType',
          name: typeName,
          path: filePath,
          id: nextId++,
          sourceRange: `${filePath}:${startLine}:${endLine}`,
          properties: { namespace: currentNamespace, kind }
        };

        // Extract supertypes from base_list as neutral edges (not applicable to enums).
        // Each entry is { name, genericArgs: string[] }.
        // Generic args from base types are also emitted as code references so the
        // graph captures e.g. Repository<TEntity>'s dependency on TEntity.
        if (node.type !== 'enum_declaration') {
          const baseListNode = node.namedChildren.find(c => c.type === 'base_list');
          if (baseListNode) {
            const supertypes = [];
            for (const child of baseListNode.namedChildren) {
              if (child.type !== 'identifier' && child.type !== 'generic_name') continue;
              const outerName = getTypeName(child);
              if (!outerName) continue;
              // Collect generic type args (for codeReferences), and record their names
              const genericArgs = [];
              if (child.type === 'generic_name') {
                const typeArgList = child.namedChildren.find(c => c.type === 'type_argument_list');
                if (typeArgList) {
                  for (const argChild of typeArgList.namedChildren) {
                    const argName = getTypeName(argChild);
                    if (argName) genericArgs.push(argName);
                  }
                }
              }
              supertypes.push(genericArgs.length > 0 ? { name: outerName, genericArgs } : { name: outerName });
            }
            if (supertypes.length > 0) {
              typeNode.properties.supertypes = supertypes;
            }
          }
        }

        nodes.push(typeNode);
        edges.push({ type: 'defines', sourceId: scriptNode.id, targetId: typeNode.id });

        // Extract methods and recurse into nested type declarations
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
            } else if (
              member.type === 'class_declaration' ||
              member.type === 'struct_declaration' ||
              member.type === 'interface_declaration' ||
              member.type === 'enum_declaration' ||
              member.type === 'record_declaration' ||
              member.type === 'record_struct_declaration'
            ) {
              processNode(member, currentNamespace);
            }
          }
        }

        // Collect cross-file references for this type
        const refs = collectReferences(node, typeName, aliasMap);
        // Also emit generic_arg refs for type arguments on base types
        // (collectReferences does not walk base_list items directly)
        if (typeNode.properties.supertypes) {
          for (const st of typeNode.properties.supertypes) {
            if (st.genericArgs) {
              for (const argName of st.genericArgs) {
                addRef(refs, typeName, argName, 'generic_arg', aliasMap);
              }
            }
          }
        }
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
