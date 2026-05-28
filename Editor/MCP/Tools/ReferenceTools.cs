using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class ReferenceTools
    {
        [MCPTool("reference_set", "Set an object reference field on a component. " +
            "Provide either target_path (scene hierarchy path) or target_asset_path (asset path), not both. " +
            "Use target_component_type to reference a specific component on the target GameObject.")]
        public static MCPToolResult SetReference(
            [MCPToolParam("GameObject name or hierarchy path", required: true)] string game_object_path,
            [MCPToolParam("Component type name (e.g. 'GameManager')", required: true)] string component_type,
            [MCPToolParam("Serialized property name (e.g. '_scoreText', 'm_ConnectedBody')", required: true)] string property_name,
            [MCPToolParam("Target scene GameObject path (e.g. 'Canvas/ScoreText')")] string target_path = null,
            [MCPToolParam("Target asset path (e.g. 'Assets/Sprites/hero.png')")] string target_asset_path = null,
            [MCPToolParam("Component type on target to reference (e.g. 'Rigidbody', 'Text'). " +
                "Omit to reference the GameObject itself.")] string target_component_type = null)
        {
            bool hasTargetPath = !string.IsNullOrEmpty(target_path);
            bool hasAssetPath = !string.IsNullOrEmpty(target_asset_path);
            if (!hasTargetPath && !hasAssetPath)
                return MCPToolResult.Error(
                    "Must provide either target_path (scene object) or target_asset_path (asset). Neither was provided.");
            if (hasTargetPath && hasAssetPath)
                return MCPToolResult.Error(
                    "Provide either target_path or target_asset_path, not both.");

            var go = ComponentTools.FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var type = ComponentTools.FindComponentType(component_type);
            if (type == null)
                return MCPToolResult.Error($"Component type not found: '{component_type}'.");

            var component = go.GetComponent(type);
            if (component == null)
            {
                var existing = go.GetComponents<Component>()
                    .Where(c => c != null).Select(c => c.GetType().Name).ToArray();
                return MCPToolResult.Error(
                    $"Component '{component_type}' not found on '{ComponentTools.GetPath(go)}'. " +
                    $"Existing components: {string.Join(", ", existing)}");
            }

            var so = new SerializedObject(component);
            var prop = so.FindProperty(property_name);
            if (prop == null)
            {
                // Try to grow array if this is an array element path like "myArray.Array.data[0]"
                prop = ResolveOrGrowArrayElement(so, property_name);
            }
            if (prop == null)
            {
                var validProps = ListObjectReferenceProperties(so);
                return MCPToolResult.Error(
                    $"Property '{property_name}' not found on {component_type}. " +
                    $"ObjectReference properties: {string.Join(", ", validProps)}");
            }
            if (prop.propertyType != SerializedPropertyType.ObjectReference)
                return MCPToolResult.Error(
                    $"Property '{property_name}' is type {prop.propertyType}, not ObjectReference.");

            UnityEngine.Object targetObj;
            string targetDescription;

            if (hasTargetPath)
            {
                var targetGO = GameObjectResolver.FindByPath(target_path);
                if (targetGO == null)
                    return GameObjectNotFoundError(target_path);

                if (!string.IsNullOrEmpty(target_component_type))
                {
                    var targetType = ComponentTools.FindComponentType(target_component_type);
                    if (targetType == null)
                        return MCPToolResult.Error($"Target component type not found: '{target_component_type}'.");

                    var targetComp = targetGO.GetComponent(targetType);
                    if (targetComp == null)
                    {
                        var availableComps = targetGO.GetComponents<Component>()
                            .Where(c => c != null).Select(c => c.GetType().Name).ToArray();
                        return MCPToolResult.Error(
                            $"Component '{target_component_type}' not found on '{target_path}'. " +
                            $"Available: {string.Join(", ", availableComps)}");
                    }
                    targetObj = targetComp;
                    targetDescription = $"{target_path} ({target_component_type})";
                }
                else
                {
                    targetObj = targetGO;
                    targetDescription = target_path;
                }
            }
            else
            {
                var fieldType = GetObjectReferenceFieldType(prop);
                var resolveErr = ResolveAsset(target_asset_path, fieldType, out targetObj);
                if (resolveErr != null)
                    return MCPToolResult.Error(resolveErr);
                targetDescription = target_asset_path;
            }

            if (hasTargetPath)
            {
                var fieldType2 = GetObjectReferenceFieldType(prop);
                if (fieldType2 != null && !fieldType2.IsInstanceOfType(targetObj))
                    return MCPToolResult.Error(
                        $"Type mismatch: field '{property_name}' expects {fieldType2.Name}, " +
                        $"but target is {targetObj.GetType().Name}. " +
                        (string.IsNullOrEmpty(target_component_type)
                            ? $"Try specifying target_component_type to reference a component instead of the GameObject."
                            : ""));
            }

            Undo.RecordObject(component, $"MCP Set Reference {property_name}");
            prop.objectReferenceValue = targetObj;
            so.ApplyModifiedProperties();

            return MCPToolResult.Success(new
            {
                gameObject = ComponentTools.GetPath(go),
                component = component_type,
                property = property_name,
                target = targetDescription,
                targetType = targetObj.GetType().Name
            });
        }

        [MCPTool("reference_get", "Get the current value of an object reference field on a component")]
        public static MCPToolResult GetReference(
            [MCPToolParam("GameObject name or hierarchy path", required: true)] string game_object_path,
            [MCPToolParam("Component type name", required: true)] string component_type,
            [MCPToolParam("Serialized property name", required: true)] string property_name)
        {
            var go = ComponentTools.FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var type = ComponentTools.FindComponentType(component_type);
            if (type == null)
                return MCPToolResult.Error($"Component type not found: '{component_type}'.");

            var component = go.GetComponent(type);
            if (component == null)
                return MCPToolResult.Error($"Component '{component_type}' not found on '{ComponentTools.GetPath(go)}'.");

            var so = new SerializedObject(component);
            var prop = so.FindProperty(property_name);
            if (prop == null)
                return MCPToolResult.Error($"Property '{property_name}' not found on {component_type}.");
            if (prop.propertyType != SerializedPropertyType.ObjectReference)
                return MCPToolResult.Error($"Property '{property_name}' is type {prop.propertyType}, not ObjectReference.");

            var obj = prop.objectReferenceValue;
            if (obj == null)
            {
                return MCPToolResult.Success(new
                {
                    gameObject = ComponentTools.GetPath(go),
                    component = component_type,
                    property = property_name,
                    value = (string)null,
                    isNull = true
                });
            }

            var assetPath = AssetDatabase.GetAssetPath(obj);
            return MCPToolResult.Success(new
            {
                gameObject = ComponentTools.GetPath(go),
                component = component_type,
                property = property_name,
                value = obj.name,
                type = obj.GetType().Name,
                assetPath = string.IsNullOrEmpty(assetPath) ? null : assetPath,
                isNull = false
            });
        }

        [MCPTool("reference_find_unset", "Find all unset (null) object reference fields on a GameObject's components")]
        public static MCPToolResult FindUnsetReferences(
            [MCPToolParam("GameObject name or hierarchy path", required: true)] string game_object_path,
            [MCPToolParam("Component type to scan (omit to scan all components)")] string component_type = null)
        {
            var go = ComponentTools.FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var components = new List<Component>();
            if (!string.IsNullOrEmpty(component_type))
            {
                var type = ComponentTools.FindComponentType(component_type);
                if (type == null)
                    return MCPToolResult.Error($"Component type not found: '{component_type}'.");
                var comp = go.GetComponent(type);
                if (comp == null)
                    return MCPToolResult.Error($"Component '{component_type}' not found on '{ComponentTools.GetPath(go)}'.");
                components.Add(comp);
            }
            else
            {
                components.AddRange(go.GetComponents<Component>().Where(c => c != null));
            }

            var unset = new List<object>();
            foreach (var comp in components)
            {
                var so = new SerializedObject(comp);
                var iterator = so.GetIterator();
                if (iterator.Next(true))
                {
                    do
                    {
                        if (iterator.depth > 1) continue;
                        if (iterator.propertyType == SerializedPropertyType.ObjectReference
                            && iterator.objectReferenceValue == null
                            && iterator.name != "m_Script")
                        {
                            var fieldType = GetObjectReferenceFieldType(iterator);
                            unset.Add(new
                            {
                                component = comp.GetType().Name,
                                property = iterator.name,
                                expectedType = fieldType?.Name ?? "Object"
                            });
                        }
                    } while (iterator.Next(false));
                }
            }

            return MCPToolResult.Success(new
            {
                gameObject = ComponentTools.GetPath(go),
                unsetReferences = unset,
                count = unset.Count
            });
        }

        // ── Helpers ──

        static MCPToolResult GameObjectNotFoundError(string path)
        {
            var roots = SceneManager.GetActiveScene().GetRootGameObjects()
                .Select(r => r.name).ToArray();
            return MCPToolResult.Error(
                $"GameObject not found: '{path}'. Root objects in scene: {string.Join(", ", roots)}");
        }

        static string[] ListObjectReferenceProperties(SerializedObject so)
        {
            var props = new List<string>();
            var iterator = so.GetIterator();
            if (iterator.NextVisible(true))
            {
                do
                {
                    if (iterator.propertyType == SerializedPropertyType.ObjectReference)
                        props.Add(iterator.name);
                } while (iterator.NextVisible(false));
            }
            return props.ToArray();
        }

        static SerializedProperty ResolveOrGrowArrayElement(SerializedObject so, string propertyName)
        {
            // Match pattern: "fieldName.Array.data[N]"
            var arrayDataIdx = propertyName.IndexOf(".Array.data[", StringComparison.Ordinal);
            if (arrayDataIdx < 0) return null;

            var arrayFieldName = propertyName.Substring(0, arrayDataIdx);
            var bracketStart = propertyName.IndexOf('[', arrayDataIdx);
            var bracketEnd = propertyName.IndexOf(']', bracketStart);
            if (bracketStart < 0 || bracketEnd < 0) return null;

            var indexStr = propertyName.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
            if (!int.TryParse(indexStr, out var targetIndex)) return null;

            var arrayProp = so.FindProperty(arrayFieldName);
            if (arrayProp == null || !arrayProp.isArray) return null;

            // Grow array to accommodate the target index
            while (arrayProp.arraySize <= targetIndex)
            {
                arrayProp.InsertArrayElementAtIndex(arrayProp.arraySize);
            }
            so.ApplyModifiedProperties();

            // Re-fetch after growth
            return so.FindProperty(propertyName);
        }

        static string ResolveAsset(string assetPath, Type targetType, out UnityEngine.Object mainAsset)
        {
            mainAsset = null;

            // Check for explicit sub-asset syntax: "Assets/Sprites/Sheet.png::SpriteName"
            var delimIdx = assetPath.IndexOf("::", StringComparison.Ordinal);
            string actualPath = delimIdx >= 0 ? assetPath.Substring(0, delimIdx) : assetPath;
            string subAssetName = delimIdx >= 0 ? assetPath.Substring(delimIdx + 2) : null;

            var loaded = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(actualPath);
            if (loaded == null)
                return $"Asset not found at path: '{actualPath}'.";

            // Explicit sub-asset name requested
            if (subAssetName != null)
            {
                var allAssets = AssetDatabase.LoadAllAssetsAtPath(actualPath);
                foreach (var a in allAssets)
                {
                    if (a != null && a.name == subAssetName)
                    {
                        mainAsset = a;
                        return null;
                    }
                }
                var names = allAssets.Where(a => a != null && a != loaded)
                    .Select(a => $"{a.name} ({a.GetType().Name})").ToArray();
                return $"Sub-asset '{subAssetName}' not found in '{actualPath}'. " +
                       $"Available sub-assets: {(names.Length > 0 ? string.Join(", ", names) : "none")}";
            }

            // Main asset matches target type — use it directly
            if (targetType == null || targetType.IsInstanceOfType(loaded))
            {
                mainAsset = loaded;
                return null;
            }

            // Auto-resolve: find a sub-asset matching the target type
            var subAssets = AssetDatabase.LoadAllAssetsAtPath(actualPath)
                .Where(a => a != null && targetType.IsInstanceOfType(a))
                .ToArray();

            if (subAssets.Length == 1)
            {
                mainAsset = subAssets[0];
                return null;
            }

            if (subAssets.Length > 1)
            {
                var subNames = subAssets.Select(a => a.name).ToArray();
                return $"Multiple {targetType.Name} sub-assets found in '{actualPath}': " +
                       $"{string.Join(", ", subNames)}. Use '::SubAssetName' syntax to specify which one " +
                       $"(e.g. '{actualPath}::{subNames[0]}').";
            }

            // No matching sub-asset and main asset doesn't match target type
            return $"Type mismatch: field expects {targetType.Name}, but '{actualPath}' is {loaded.GetType().Name} " +
                   $"and contains no {targetType.Name} sub-assets.";
        }

        static Type GetObjectReferenceFieldType(SerializedProperty prop)
        {
            var targetObject = prop.serializedObject.targetObject;
            if (targetObject == null) return null;

            var objType = targetObject.GetType();
            var fieldInfo = objType.GetField(prop.name,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (fieldInfo != null) return fieldInfo.FieldType;

            // Fallback for built-in Unity types: map m_FieldName → fieldName property
            var propName = prop.name;
            if (propName.StartsWith("m_") && propName.Length > 2)
            {
                var csName = char.ToLower(propName[2]) + propName.Substring(3);
                var propInfo = objType.GetProperty(csName,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public);
                if (propInfo != null) return propInfo.PropertyType;
            }

            return null;
        }
    }
}
