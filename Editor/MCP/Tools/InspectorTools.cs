using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class InspectorTools
    {
        [MCPTool("inspector_select", "Select a GameObject in the Editor (highlights in Hierarchy and Inspector)")]
        public static MCPToolResult SelectGameObject(
            [MCPToolParam("GameObject name or hierarchy path", required: true)] string path)
        {
            var go = GameObjectResolver.FindByPath(path);
            if (go == null)
            {
                var rootNames = GetRootObjectNames();
                return MCPToolResult.Error(
                    $"GameObject not found: '{path}'. Root objects in scene: {string.Join(", ", rootNames)}");
            }

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);

            return MCPToolResult.Success(new
            {
                selected = GetPath(go),
                instanceId = go.GetInstanceID()
            });
        }

        [MCPTool("inspector_inspect", "Get full property dump of a GameObject — all components and their serialized properties")]
        public static MCPToolResult InspectGameObject(
            [MCPToolParam("GameObject name or hierarchy path", required: true)] string path)
        {
            var go = GameObjectResolver.FindByPath(path);
            if (go == null)
            {
                var rootNames = GetRootObjectNames();
                return MCPToolResult.Error(
                    $"GameObject not found: '{path}'. Root objects in scene: {string.Join(", ", rootNames)}");
            }

            var components = go.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c => DumpComponent(c))
                .ToArray();

            return MCPToolResult.Success(new
            {
                name = go.name,
                path = GetPath(go),
                active = go.activeSelf,
                layer = LayerMask.LayerToName(go.layer),
                tag = go.tag,
                isStatic = go.isStatic,
                transform = new
                {
                    position = Vec3(go.transform.position),
                    rotation = Vec3(go.transform.eulerAngles),
                    scale = Vec3(go.transform.localScale)
                },
                childCount = go.transform.childCount,
                children = Enumerable.Range(0, go.transform.childCount)
                    .Select(i => go.transform.GetChild(i).name)
                    .ToArray(),
                components
            });
        }

        static object DumpComponent(Component component)
        {
            var so = new SerializedObject(component);
            var properties = new List<object>();

            var iterator = so.GetIterator();
            if (iterator.NextVisible(true))
            {
                do
                {
                    properties.Add(new
                    {
                        name = iterator.name,
                        displayName = iterator.displayName,
                        type = iterator.propertyType.ToString(),
                        value = GetPropertyValue(iterator)
                    });
                } while (iterator.NextVisible(false));
            }

            return new
            {
                type = component.GetType().Name,
                fullType = component.GetType().FullName,
                enabled = IsComponentEnabled(component),
                properties
            };
        }

        static string GetPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue.ToString();
                case SerializedPropertyType.Boolean:
                    return prop.boolValue.ToString();
                case SerializedPropertyType.Float:
                    return prop.floatValue.ToString("G");
                case SerializedPropertyType.String:
                    return prop.stringValue ?? "";
                case SerializedPropertyType.Color:
                    return prop.colorValue.ToString();
                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue != null
                        ? $"{prop.objectReferenceValue.name} ({prop.objectReferenceValue.GetType().Name})"
                        : "(null)";
                case SerializedPropertyType.Enum:
                    return prop.enumDisplayNames != null && prop.enumValueIndex >= 0 &&
                           prop.enumValueIndex < prop.enumDisplayNames.Length
                        ? prop.enumDisplayNames[prop.enumValueIndex]
                        : prop.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2:
                    return prop.vector2Value.ToString();
                case SerializedPropertyType.Vector3:
                    return prop.vector3Value.ToString();
                case SerializedPropertyType.Vector4:
                    return prop.vector4Value.ToString();
                case SerializedPropertyType.Rect:
                    return prop.rectValue.ToString();
                case SerializedPropertyType.Bounds:
                    return prop.boundsValue.ToString();
                case SerializedPropertyType.Quaternion:
                    return prop.quaternionValue.eulerAngles.ToString();
                case SerializedPropertyType.LayerMask:
                    return prop.intValue.ToString();
                case SerializedPropertyType.ArraySize:
                    return prop.intValue.ToString();
                default:
                    return $"({prop.propertyType})";
            }
        }

        static bool IsComponentEnabled(Component component)
        {
            if (component is Behaviour behaviour)
                return behaviour.enabled;
            if (component is Renderer renderer)
                return renderer.enabled;
            if (component is Collider collider)
                return collider.enabled;
            return true;
        }

        // ── Helpers ──

        static string GetPath(GameObject go)
        {
            var path = go.name;
            var current = go.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }

        static object Vec3(Vector3 v) => new { x = v.x, y = v.y, z = v.z };

        static string[] GetRootObjectNames()
        {
            return SceneManager.GetActiveScene()
                .GetRootGameObjects()
                .Select(r => r.name)
                .ToArray();
        }
    }
}
