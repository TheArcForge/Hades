using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class ComponentTools
    {
        // ── Tools ──

        [MCPTool("component_add", "Add a component to a GameObject by type name (supports undo)")]
        public static MCPToolResult AddComponent(
            [MCPToolParam("GameObject name or path (e.g. 'Canvas/Panel/Button')", required: true)] string game_object_path,
            [MCPToolParam("Component type name (e.g. 'BoxCollider', 'Rigidbody')", required: true)] string type_name)
        {
            var go = FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var type = FindComponentType(type_name);
            if (type == null)
                return MCPToolResult.Error($"Component type not found: '{type_name}'. Ensure the type name is correct and the assembly containing it is loaded.");

            Undo.AddComponent(go, type);
            return MCPToolResult.Success(new { added = type.Name, to = GetPath(go) });
        }

        [MCPTool("component_find", "Find all GameObjects with a specific component type")]
        public static MCPToolResult FindComponents(
            [MCPToolParam("Component type name (e.g. 'Camera', 'UIDocument')", required: true)] string type_name)
        {
            var type = FindComponentType(type_name);
            if (type == null)
                return MCPToolResult.Error($"Component type not found: '{type_name}'. Ensure the type name is correct and the assembly containing it is loaded.");

            var components = UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None);
            var results = components
                .Select(c => c as Component)
                .Where(c => c != null)
                .Select(c => new { name = c.gameObject.name, path = GetPath(c.gameObject) })
                .ToArray();

            return MCPToolResult.Success(results);
        }

        [MCPTool("component_remove", "Remove a component from a GameObject by type name (supports undo)")]
        public static MCPToolResult RemoveComponent(
            [MCPToolParam("GameObject name or path (e.g. 'Canvas/Panel/Button')", required: true)] string game_object_path,
            [MCPToolParam("Component type name to remove (e.g. 'BoxCollider')", required: true)] string type_name)
        {
            var go = FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var type = FindComponentType(type_name);
            if (type == null)
                return MCPToolResult.Error($"Component type not found: '{type_name}'. Ensure the type name is correct and the assembly containing it is loaded.");

            var component = go.GetComponent(type);
            if (component == null)
            {
                var existing = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToArray();
                return MCPToolResult.Error(
                    $"Component '{type_name}' not found on GameObject '{GetPath(go)}'. " +
                    $"Existing components: {string.Join(", ", existing)}");
            }

            Undo.DestroyObjectImmediate(component);
            return MCPToolResult.Success(new { removed = type.Name, from = GetPath(go) });
        }

        [MCPTool("component_get_all", "List all components on a GameObject with type names and enabled state")]
        public static MCPToolResult GetComponents(
            [MCPToolParam("GameObject name or path (e.g. 'Canvas/Panel/Button')", required: true)] string game_object_path)
        {
            var go = FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var components = go.GetComponents<Component>()
                .Where(c => c != null)
                .Select(c =>
                {
                    var behaviour = c as Behaviour;
                    return new
                    {
                        type = c.GetType().Name,
                        enabled = behaviour != null ? (bool?)behaviour.enabled : null
                    };
                })
                .ToArray();

            return MCPToolResult.Success(new { gameObject = GetPath(go), components });
        }

        [MCPTool("component_get_property", "Read a serialized property value from a component")]
        public static MCPToolResult GetComponentProperty(
            [MCPToolParam("GameObject name or path", required: true)] string game_object_path,
            [MCPToolParam("Component type name (e.g. 'Transform')", required: true)] string type_name,
            [MCPToolParam("Serialized property name (e.g. 'm_LocalPosition')", required: true)] string property_name)
        {
            var go = FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var type = FindComponentType(type_name);
            if (type == null)
                return MCPToolResult.Error($"Component type not found: '{type_name}'.");

            var component = go.GetComponent(type);
            if (component == null)
            {
                var existing = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToArray();
                return MCPToolResult.Error(
                    $"Component '{type_name}' not found on '{GetPath(go)}'. " +
                    $"Existing components: {string.Join(", ", existing)}");
            }

            var serializedObject = new SerializedObject(component);
            var resolvedName = ResolvePropertyName(serializedObject, property_name, out var resolveError);
            if (resolvedName == null)
                return MCPToolResult.Error(resolveError);
            var property = serializedObject.FindProperty(resolvedName);

            var value = GetSerializedPropertyValue(property);
            return MCPToolResult.Success(new
            {
                gameObject = GetPath(go),
                component = type_name,
                property = property_name,
                propertyType = property.propertyType.ToString(),
                value
            });
        }

        [MCPTool("component_set_property", "Set a serialized property value on a component")]
        public static MCPToolResult SetComponentProperty(
            [MCPToolParam("GameObject name or path", required: true)] string game_object_path,
            [MCPToolParam("Component type name (e.g. 'Transform')", required: true)] string type_name,
            [MCPToolParam("Serialized property name (e.g. 'm_LocalPosition')", required: true)] string property_name,
            [MCPToolParam("Value to set (string, number, bool, or JSON for Vector3/Color)", required: true)] string value)
        {
            var go = FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var type = FindComponentType(type_name);
            if (type == null)
                return MCPToolResult.Error($"Component type not found: '{type_name}'.");

            var component = go.GetComponent(type);
            if (component == null)
            {
                var existing = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToArray();
                return MCPToolResult.Error(
                    $"Component '{type_name}' not found on '{GetPath(go)}'. " +
                    $"Existing components: {string.Join(", ", existing)}");
            }

            var serializedObject = new SerializedObject(component);
            var resolvedName = ResolvePropertyName(serializedObject, property_name, out var resolveError);
            if (resolvedName == null)
                return MCPToolResult.Error(resolveError);
            var property = serializedObject.FindProperty(resolvedName);

            try
            {
                SetSerializedPropertyValue(property, value);
                serializedObject.ApplyModifiedProperties();
            }
            catch (Exception ex)
            {
                return MCPToolResult.Error($"Failed to set property '{property_name}': {ex.Message}");
            }

            return MCPToolResult.Success(new
            {
                gameObject = GetPath(go),
                component = type_name,
                property = property_name,
                newValue = value
            });
        }

        [MCPTool("component_set_properties", "Set multiple serialized properties across multiple GameObjects and components in a single batch call. " +
            "Accepts a JSON array where each entry specifies a GameObject, component type, and a dictionary of property name-value pairs.")]
        public static MCPToolResult SetProperties(
            [MCPToolParam("JSON array of operations. Each: { gameObject (required), component (required), " +
                "properties: { propertyName: value } }", required: true)] string operations_json)
        {
            PropertyOperationDef[] ops;
            try
            {
                ops = JsonConvert.DeserializeObject<PropertyOperationDef[]>(operations_json);
            }
            catch (Exception ex)
            {
                return MCPToolResult.Error($"Invalid JSON: {ex.Message}");
            }

            if (ops == null || ops.Length == 0)
                return MCPToolResult.Success(new { results = new object[0], errors = new object[0], summary = "0 operations processed" });

            var results = new List<object>();
            var errors = new List<object>();

            Undo.IncrementCurrentGroup();

            foreach (var op in ops)
            {
                var go = FindGameObject(op.GameObject);
                if (go == null)
                {
                    errors.Add(new { gameObject = op.GameObject, error = $"GameObject not found: '{op.GameObject}'" });
                    continue;
                }

                var type = FindComponentType(op.Component);
                if (type == null)
                {
                    errors.Add(new { gameObject = op.GameObject, component = op.Component,
                        error = $"Component type not found: '{op.Component}'" });
                    continue;
                }

                var component = go.GetComponent(type);
                if (component == null)
                {
                    var existing = go.GetComponents<Component>()
                        .Where(c => c != null)
                        .Select(c => c.GetType().Name)
                        .ToArray();
                    errors.Add(new { gameObject = op.GameObject, component = op.Component,
                        error = $"Component '{op.Component}' not found on '{GetPath(go)}'. Existing: {string.Join(", ", existing)}" });
                    continue;
                }

                var serializedObject = new SerializedObject(component);
                int propsSet = 0;
                var propErrors = new List<string>();

                if (op.Properties != null)
                {
                    foreach (var kvp in op.Properties)
                    {
                        var resolvedKey = ResolvePropertyName(serializedObject, kvp.Key, out var resolveErr);
                        if (resolvedKey == null)
                        {
                            propErrors.Add($"{kvp.Key}: {resolveErr}");
                            continue;
                        }
                        var property = serializedObject.FindProperty(resolvedKey);

                        try
                        {
                            // Normalize JToken to string for SetSerializedPropertyValue
                            string valueStr;
                            if (kvp.Value.Type == Newtonsoft.Json.Linq.JTokenType.String)
                                valueStr = kvp.Value.Value<string>();
                            else
                                valueStr = kvp.Value.ToString(Newtonsoft.Json.Formatting.None);

                            SetSerializedPropertyValue(property, valueStr);
                            propsSet++;
                        }
                        catch (Exception ex)
                        {
                            propErrors.Add($"{kvp.Key}: {ex.Message}");
                        }
                    }
                    if (propsSet > 0)
                        serializedObject.ApplyModifiedProperties();
                }

                var result = new Dictionary<string, object>
                {
                    { "gameObject", op.GameObject },
                    { "component", op.Component },
                    { "propertiesSet", propsSet }
                };
                if (propErrors.Count > 0)
                    result["errors"] = propErrors;

                results.Add(result);
            }

            var totalProps = 0;
            foreach (var r in results)
            {
                if (r is Dictionary<string, object> dict && dict.ContainsKey("propertiesSet"))
                    totalProps += (int)dict["propertiesSet"];
            }

            Undo.SetCurrentGroupName($"Set Properties: {totalProps} properties across {results.Count} operations");

            return MCPToolResult.Success(new
            {
                results,
                errors,
                summary = $"{totalProps} properties set across {results.Count} operation(s), {errors.Count} error(s)"
            });
        }

        [MCPTool("component_list_properties", "List all serialized properties on a component with " +
            "both serialized names and display names, types, and current values")]
        public static MCPToolResult ListComponentProperties(
            [MCPToolParam("GameObject name or path", required: true)] string game_object_path,
            [MCPToolParam("Component type name (e.g. 'Camera', 'Transform')", required: true)] string type_name)
        {
            var go = FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            var type = FindComponentType(type_name);
            if (type == null)
                return MCPToolResult.Error($"Component type not found: '{type_name}'.");

            var component = go.GetComponent(type);
            if (component == null)
            {
                var existing = go.GetComponents<Component>()
                    .Where(c => c != null).Select(c => c.GetType().Name).ToArray();
                return MCPToolResult.Error(
                    $"Component '{type_name}' not found on '{GetPath(go)}'. " +
                    $"Existing components: {string.Join(", ", existing)}");
            }

            var serializedObject = new SerializedObject(component);
            var properties = new List<object>();
            var iterator = serializedObject.GetIterator();
            if (iterator.NextVisible(true))
            {
                do
                {
                    properties.Add(new
                    {
                        serializedName = iterator.name,
                        displayName = iterator.displayName,
                        type = iterator.propertyType.ToString(),
                        currentValue = GetSerializedPropertyValue(iterator)
                    });
                } while (iterator.NextVisible(false));
            }

            return MCPToolResult.Success(new
            {
                gameObject = GetPath(go),
                component = type_name,
                properties,
                count = properties.Count
            });
        }

        // ── Helpers ──

        internal static GameObject FindGameObject(string path)
        {
            return GameObjectResolver.FindByPath(path);
        }

        static MCPToolResult GameObjectNotFoundError(string path)
        {
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects()
                .Select(go => go.name)
                .ToArray();
            return MCPToolResult.Error(
                $"GameObject not found: '{path}'. " +
                $"Root objects in scene: {string.Join(", ", roots)}");
        }

        internal static Type FindComponentType(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Try fully-qualified name first
                    var type = assembly.GetType(typeName);
                    if (type != null && typeof(Component).IsAssignableFrom(type))
                        return type;

                    // Try simple name match
                    foreach (var t in assembly.GetTypes())
                    {
                        if (t.Name == typeName && typeof(Component).IsAssignableFrom(t))
                            return t;
                    }
                }
                catch (System.Reflection.ReflectionTypeLoadException) { }
            }
            return null;
        }

        internal static string GetPath(GameObject go)
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

        static string GetSerializedPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue.ToString();
                case SerializedPropertyType.Boolean:
                    return prop.boolValue.ToString();
                case SerializedPropertyType.Float:
                    return prop.floatValue.ToString();
                case SerializedPropertyType.String:
                    return prop.stringValue;
                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    return $"{{\"r\":{c.r},\"g\":{c.g},\"b\":{c.b},\"a\":{c.a}}}";
                case SerializedPropertyType.ObjectReference:
                    var obj = prop.objectReferenceValue;
                    return obj != null ? obj.name : "null";
                case SerializedPropertyType.Enum:
                    return prop.enumNames.Length > prop.enumValueIndex && prop.enumValueIndex >= 0
                        ? prop.enumNames[prop.enumValueIndex]
                        : prop.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2:
                    var v2 = prop.vector2Value;
                    return $"{{\"x\":{v2.x},\"y\":{v2.y}}}";
                case SerializedPropertyType.Vector3:
                    var v3 = prop.vector3Value;
                    return $"{{\"x\":{v3.x},\"y\":{v3.y},\"z\":{v3.z}}}";
                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    return $"{{\"x\":{v4.x},\"y\":{v4.y},\"z\":{v4.z},\"w\":{v4.w}}}";
                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    return $"{{\"x\":{r.x},\"y\":{r.y},\"width\":{r.width},\"height\":{r.height}}}";
                case SerializedPropertyType.Bounds:
                    var b = prop.boundsValue;
                    return $"{{\"center\":{{\"x\":{b.center.x},\"y\":{b.center.y},\"z\":{b.center.z}}}," +
                           $"\"size\":{{\"x\":{b.size.x},\"y\":{b.size.y},\"z\":{b.size.z}}}}}";
                case SerializedPropertyType.Quaternion:
                    var q = prop.quaternionValue;
                    return $"{{\"x\":{q.x},\"y\":{q.y},\"z\":{q.z},\"w\":{q.w}}}";
                case SerializedPropertyType.Vector2Int:
                    var v2i = prop.vector2IntValue;
                    return $"{{\"x\":{v2i.x},\"y\":{v2i.y}}}";
                case SerializedPropertyType.Vector3Int:
                    var v3i = prop.vector3IntValue;
                    return $"{{\"x\":{v3i.x},\"y\":{v3i.y},\"z\":{v3i.z}}}";
                case SerializedPropertyType.RectInt:
                    var ri = prop.rectIntValue;
                    return $"{{\"x\":{ri.x},\"y\":{ri.y},\"width\":{ri.width},\"height\":{ri.height}}}";
                case SerializedPropertyType.BoundsInt:
                    var bi = prop.boundsIntValue;
                    return $"{{\"center\":{{\"x\":{bi.position.x},\"y\":{bi.position.y},\"z\":{bi.position.z}}}," +
                           $"\"size\":{{\"x\":{bi.size.x},\"y\":{bi.size.y},\"z\":{bi.size.z}}}}}";
                case SerializedPropertyType.LayerMask:
                    return prop.intValue.ToString();
                default:
                    return $"<unsupported:{prop.propertyType}>";
            }
        }

        internal static void SetSerializedPropertyValue(SerializedProperty prop, string value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = int.Parse(value);
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = bool.Parse(value);
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = float.Parse(value);
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = value;
                    break;
                case SerializedPropertyType.Color:
                    prop.colorValue = ParseColor(value);
                    break;
                case SerializedPropertyType.Vector2:
                    prop.vector2Value = ParseVector2(value);
                    break;
                case SerializedPropertyType.Vector3:
                    prop.vector3Value = ParseVector3(value);
                    break;
                case SerializedPropertyType.Vector4:
                    prop.vector4Value = ParseVector4(value);
                    break;
                case SerializedPropertyType.Quaternion:
                    var qv = ParseVector4(value);
                    prop.quaternionValue = new Quaternion(qv.x, qv.y, qv.z, qv.w);
                    break;
                case SerializedPropertyType.Rect:
                    prop.rectValue = ParseRect(value);
                    break;
                case SerializedPropertyType.Vector2Int:
                    var v2 = ParseVector2(value);
                    prop.vector2IntValue = new Vector2Int(Mathf.RoundToInt(v2.x), Mathf.RoundToInt(v2.y));
                    break;
                case SerializedPropertyType.Vector3Int:
                    var v3 = ParseVector3(value);
                    prop.vector3IntValue = new Vector3Int(Mathf.RoundToInt(v3.x), Mathf.RoundToInt(v3.y), Mathf.RoundToInt(v3.z));
                    break;
                case SerializedPropertyType.Enum:
                    if (int.TryParse(value, out var enumIndex))
                    {
                        prop.enumValueIndex = enumIndex;
                    }
                    else
                    {
                        var idx = Array.IndexOf(prop.enumNames, value);
                        if (idx < 0)
                            throw new ArgumentException($"Invalid enum value '{value}'. Valid values: {string.Join(", ", prop.enumNames)}");
                        prop.enumValueIndex = idx;
                    }
                    break;
                case SerializedPropertyType.LayerMask:
                    prop.intValue = int.Parse(value);
                    break;
                case SerializedPropertyType.ObjectReference:
                    if (string.IsNullOrEmpty(value) || value == "null")
                    {
                        prop.objectReferenceValue = null;
                    }
                    else
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(value);
                        if (asset == null)
                            throw new ArgumentException($"Asset not found at path: '{value}'");
                        prop.objectReferenceValue = asset;
                    }
                    break;
                default:
                    throw new ArgumentException($"Unsupported property type: {prop.propertyType}");
            }
        }

        static string[] ListSerializedPropertyNames(SerializedObject serializedObject)
        {
            var names = new System.Collections.Generic.List<string>();
            var iterator = serializedObject.GetIterator();
            if (iterator.NextVisible(true))
            {
                do
                {
                    names.Add(iterator.name);
                } while (iterator.NextVisible(false));
            }
            return names.ToArray();
        }

        static string[] ListSerializedPropertyNamesWithTypes(SerializedObject serializedObject)
        {
            var entries = new System.Collections.Generic.List<string>();
            var iterator = serializedObject.GetIterator();
            if (iterator.NextVisible(true))
            {
                do
                {
                    entries.Add($"{iterator.name} ({iterator.propertyType})");
                } while (iterator.NextVisible(false));
            }
            return entries.ToArray();
        }

        internal static string ResolvePropertyName(SerializedObject so, string input, out string errorMessage)
        {
            errorMessage = null;

            // 1. Try exact match first (existing behavior)
            var prop = so.FindProperty(input);
            if (prop != null)
                return input;

            // 2. Build display name map
            var displayToPath = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            var normalizedToPath = new Dictionary<string, List<string>>();
            var iterator = so.GetIterator();
            if (iterator.NextVisible(true))
            {
                do
                {
                    var path = iterator.propertyPath;
                    var display = iterator.displayName;

                    // Display name (case-insensitive)
                    if (!displayToPath.ContainsKey(display))
                        displayToPath[display] = path;

                    // Normalized key
                    var normalized = NormalizePropertyName(display);
                    if (!normalizedToPath.ContainsKey(normalized))
                        normalizedToPath[normalized] = new List<string>();
                    normalizedToPath[normalized].Add(path);
                } while (iterator.NextVisible(false));
            }

            // 3. Try case-insensitive display name match
            if (displayToPath.TryGetValue(input, out var displayMatch))
                return displayMatch;

            // 4. Try normalized match
            var normalizedInput = NormalizePropertyName(input);
            if (normalizedToPath.TryGetValue(normalizedInput, out var candidates))
            {
                if (candidates.Count == 1)
                    return candidates[0];

                errorMessage = $"Ambiguous property name '{input}'. Matches: " +
                               string.Join(", ", candidates);
                return null;
            }

            // 5. Not found — list valid properties
            var validProps = ListSerializedPropertyNamesWithTypes(so);
            errorMessage = $"Property '{input}' not found on {so.targetObject.GetType().Name}. " +
                           $"Valid properties: {string.Join(", ", validProps)}";
            return null;
        }

        static string NormalizePropertyName(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var ch in name)
            {
                if (ch != ' ' && ch != '_' && ch != '-')
                    sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        static Vector3 ParseVector3(string value)
        {
            // Simple JSON parsing without dependency on JSON library
            var x = ExtractJsonFloat(value, "x");
            var y = ExtractJsonFloat(value, "y");
            var z = ExtractJsonFloat(value, "z");
            return new Vector3(x, y, z);
        }

        static Vector2 ParseVector2(string value)
        {
            var x = ExtractJsonFloat(value, "x");
            var y = ExtractJsonFloat(value, "y");
            return new Vector2(x, y);
        }

        static Vector4 ParseVector4(string value)
        {
            var x = ExtractJsonFloat(value, "x");
            var y = ExtractJsonFloat(value, "y");
            var z = ExtractJsonFloat(value, "z");
            var w = ExtractJsonFloat(value, "w");
            return new Vector4(x, y, z, w);
        }

        static Color ParseColor(string value)
        {
            var r = ExtractJsonFloat(value, "r");
            var g = ExtractJsonFloat(value, "g");
            var b = ExtractJsonFloat(value, "b");
            var a = ExtractJsonFloat(value, "a");
            return new Color(r, g, b, a);
        }

        static Rect ParseRect(string value)
        {
            var x = ExtractJsonFloat(value, "x");
            var y = ExtractJsonFloat(value, "y");
            var w = ExtractJsonFloat(value, "width");
            var h = ExtractJsonFloat(value, "height");
            return new Rect(x, y, w, h);
        }

        static float ExtractJsonFloat(string json, string key)
        {
            // Find "key": or "key" :
            var keyPattern = $"\"{key}\"";
            var idx = json.IndexOf(keyPattern, StringComparison.Ordinal);
            if (idx < 0)
                throw new ArgumentException($"Key '{key}' not found in JSON: {json}");

            // Move past the key and colon
            idx += keyPattern.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':'))
                idx++;

            // Extract the number
            var start = idx;
            while (idx < json.Length && (char.IsDigit(json[idx]) || json[idx] == '.' || json[idx] == '-' || json[idx] == 'e' || json[idx] == 'E' || json[idx] == '+'))
                idx++;

            var numberStr = json.Substring(start, idx - start);
            return float.Parse(numberStr, System.Globalization.CultureInfo.InvariantCulture);
        }

        // ── Data Models ──

        class PropertyOperationDef
        {
            [JsonProperty("gameObject")] public string GameObject;
            [JsonProperty("component")] public string Component;
            [JsonProperty("properties")] public Dictionary<string, Newtonsoft.Json.Linq.JToken> Properties;
        }
    }
}
