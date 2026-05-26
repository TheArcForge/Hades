using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class MaterialTools
    {
        // ── Tools ──

        [MCPTool("material_create", "Create a new material asset at the specified path with the given shader")]
        public static MCPToolResult CreateMaterial(
            [MCPToolParam("Project-relative path for the new material (e.g. 'Assets/Materials/MyMat.mat')", required: true)] string path,
            [MCPToolParam("Shader name (e.g. 'Standard', 'Unlit/Color'). Defaults to 'Standard'")] string shaderName = "Standard")
        {
            if (string.IsNullOrEmpty(shaderName))
                shaderName = "Standard";

            var shader = Shader.Find(shaderName);
            if (shader == null)
                return MCPToolResult.Error($"Shader '{shaderName}' not found. Ensure the shader name is correct and the shader is included in the project.");

            EnsureFolderExists(Path.GetDirectoryName(path));

            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();

            return MCPToolResult.Success(new
            {
                path,
                shader = shaderName,
                created = true
            });
        }

        [MCPTool("material_set_property", "Set a shader property on a material asset (float, int, color, vector, or texture)")]
        public static MCPToolResult SetMaterialProperty(
            [MCPToolParam("Project-relative path to the material asset", required: true)] string materialPath,
            [MCPToolParam("Shader property name (e.g. '_Color', '_Glossiness')", required: true)] string propertyName,
            [MCPToolParam("Value to set (number, JSON color/vector, hex color, or texture asset path)", required: true)] string value,
            [MCPToolParam("Type hint: 'float', 'int', 'color', 'vector', or 'texture'. Auto-detected if omitted.")] string propertyType = null)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null)
                return MCPToolResult.Error($"Material not found at path: '{materialPath}'.");

            try
            {
                Undo.RecordObject(mat, $"MCP Set Material Property {propertyName}");
                ApplyMaterialProperty(mat, propertyName, value, propertyType);
                EditorUtility.SetDirty(mat);
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex)
            {
                return MCPToolResult.Error($"Failed to set property '{propertyName}': {ex.Message}");
            }

            return MCPToolResult.Success(new
            {
                materialPath,
                property = propertyName,
                value,
                propertyType = propertyType ?? "auto"
            });
        }

        [MCPTool("material_get_properties", "List all shader properties of a material with their types and current values")]
        public static MCPToolResult GetMaterialProperties(
            [MCPToolParam("Project-relative path to the material asset", required: true)] string materialPath)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null)
                return MCPToolResult.Error($"Material not found at path: '{materialPath}'.");

            var shader = mat.shader;
            var propertyCount = ShaderUtil.GetPropertyCount(shader);
            var properties = new List<object>();

            for (int i = 0; i < propertyCount; i++)
            {
                var propName = ShaderUtil.GetPropertyName(shader, i);
                var propType = ShaderUtil.GetPropertyType(shader, i);
                var desc = ShaderUtil.GetPropertyDescription(shader, i);
                object currentValue = GetMaterialPropertyValue(mat, propName, propType);

                properties.Add(new
                {
                    name = propName,
                    type = propType.ToString(),
                    description = desc,
                    value = currentValue
                });
            }

            return MCPToolResult.Success(new
            {
                materialPath,
                shader = shader.name,
                properties
            });
        }

        [MCPTool("material_assign", "Assign a material to a Renderer component on a GameObject (supports material slots)")]
        public static MCPToolResult AssignMaterial(
            [MCPToolParam("GameObject name or hierarchy path (e.g. 'Canvas/Panel')", required: true)] string gameObjectPath,
            [MCPToolParam("Project-relative path to the material asset", required: true)] string materialPath,
            [MCPToolParam("Material slot index (0-based, default '0')")] string slot = "0")
        {
            var go = ComponentTools.FindGameObject(gameObjectPath);
            if (go == null)
                return MCPToolResult.Error($"GameObject not found: '{gameObjectPath}'.");

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return MCPToolResult.Error($"No Renderer component found on '{ComponentTools.GetPath(go)}'. Add a MeshRenderer, SkinnedMeshRenderer, or other Renderer component first.");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null)
                return MCPToolResult.Error($"Material not found at path: '{materialPath}'.");

            int slotIndex = 0;
            if (!string.IsNullOrEmpty(slot) && !int.TryParse(slot, out slotIndex))
                return MCPToolResult.Error($"Invalid slot index: '{slot}'. Must be an integer.");

            var mats = renderer.sharedMaterials;
            if (slotIndex < 0 || slotIndex >= mats.Length)
                return MCPToolResult.Error($"Slot index {slotIndex} is out of range. Renderer has {mats.Length} material slot(s).");

            Undo.RecordObject(renderer, $"Assign Material '{mat.name}' to '{ComponentTools.GetPath(go)}'");
            mats[slotIndex] = mat;
            renderer.sharedMaterials = mats;
            EditorUtility.SetDirty(renderer);

            return MCPToolResult.Success(new
            {
                gameObject = ComponentTools.GetPath(go),
                renderer = renderer.GetType().Name,
                slot = slotIndex,
                material = mat.name,
                materialPath
            });
        }

        [MCPTool("material_duplicate", "Duplicate a material asset to a new path, preserving all properties")]
        public static MCPToolResult DuplicateMaterial(
            [MCPToolParam("Project-relative path of the source material", required: true)] string sourcePath,
            [MCPToolParam("Project-relative destination path for the duplicate", required: true)] string destPath)
        {
            var sourceMat = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);
            if (sourceMat == null)
                return MCPToolResult.Error($"Source material not found at path: '{sourcePath}'.");

            EnsureFolderExists(Path.GetDirectoryName(destPath));

            bool copied = AssetDatabase.CopyAsset(sourcePath, destPath);
            if (!copied)
                return MCPToolResult.Error($"Failed to copy material from '{sourcePath}' to '{destPath}'.");

            AssetDatabase.Refresh();

            return MCPToolResult.Success(new
            {
                source = sourcePath,
                destination = destPath,
                duplicated = true
            });
        }

        [MCPTool("material_swap_shader", "Change the shader on an existing material, preserving compatible properties")]
        public static MCPToolResult SwapShader(
            [MCPToolParam("Project-relative path to the material asset", required: true)] string materialPath,
            [MCPToolParam("New shader name (e.g. 'Unlit/Color', 'Universal Render Pipeline/Lit')", required: true)] string shaderName)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null)
                return MCPToolResult.Error($"Material not found at path: '{materialPath}'.");

            var shader = Shader.Find(shaderName);
            if (shader == null)
                return MCPToolResult.Error($"Shader '{shaderName}' not found. Ensure the shader name is correct and the shader is included in the project.");

            var previousShader = mat.shader.name;
            Undo.RecordObject(mat, "MCP Swap Shader");
            mat.shader = shader;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();

            return MCPToolResult.Success(new
            {
                materialPath,
                previousShader,
                newShader = shaderName
            });
        }

        // ── Helpers ──

        static void ApplyMaterialProperty(Material mat, string propertyName, string value, string typeHint)
        {
            var hint = typeHint?.ToLowerInvariant();

            // Explicit type hint wins
            if (hint == "float")
            {
                mat.SetFloat(propertyName, float.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            if (hint == "int")
            {
                mat.SetInt(propertyName, int.Parse(value));
                return;
            }
            if (hint == "color")
            {
                mat.SetColor(propertyName, ParseColor(value));
                return;
            }
            if (hint == "vector")
            {
                mat.SetVector(propertyName, ParseVector4(value));
                return;
            }
            if (hint == "texture")
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture>(value);
                if (tex == null)
                    throw new ArgumentException($"Texture asset not found at path: '{value}'");
                mat.SetTexture(propertyName, tex);
                return;
            }

            // Auto-detect from the shader property type
            var shader = mat.shader;
            int propCount = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < propCount; i++)
            {
                if (ShaderUtil.GetPropertyName(shader, i) != propertyName)
                    continue;

                var propType = ShaderUtil.GetPropertyType(shader, i);
                switch (propType)
                {
                    case ShaderUtil.ShaderPropertyType.Float:
                    case ShaderUtil.ShaderPropertyType.Range:
                        mat.SetFloat(propertyName, float.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
                        // Handle Standard shader rendering mode keywords for _Mode
                        if (propertyName == "_Mode")
                            ApplyStandardShaderRenderingMode(mat, (int)float.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
                        return;

                    case ShaderUtil.ShaderPropertyType.Color:
                        mat.SetColor(propertyName, ParseColor(value));
                        return;

                    case ShaderUtil.ShaderPropertyType.Vector:
                        mat.SetVector(propertyName, ParseVector4(value));
                        return;

                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        var tex = AssetDatabase.LoadAssetAtPath<Texture>(value);
                        if (tex == null)
                            throw new ArgumentException($"Texture asset not found at path: '{value}'");
                        mat.SetTexture(propertyName, tex);
                        return;

                    default:
                        throw new ArgumentException($"Unsupported shader property type: {propType}");
                }
            }

            // Property not found on shader — try float as a fallback for custom/runtime properties
            if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var floatVal))
            {
                mat.SetFloat(propertyName, floatVal);
                return;
            }

            // Try color
            if (value.StartsWith("{") || value.StartsWith("#"))
            {
                mat.SetColor(propertyName, ParseColor(value));
                return;
            }

            throw new ArgumentException($"Property '{propertyName}' not found on shader '{shader.name}' and value type could not be auto-detected. Use the propertyType parameter to specify the type explicitly.");
        }

        static void ApplyStandardShaderRenderingMode(Material mat, int mode)
        {
            switch (mode)
            {
                case 0: // Opaque
                    mat.SetOverrideTag("RenderType", "");
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.DisableKeyword("_ALPHABLEND_ON");
                    mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.renderQueue = -1;
                    break;
                case 1: // Cutout
                    mat.SetOverrideTag("RenderType", "TransparentCutout");
                    mat.EnableKeyword("_ALPHATEST_ON");
                    mat.DisableKeyword("_ALPHABLEND_ON");
                    mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                    break;
                case 2: // Fade
                    mat.SetOverrideTag("RenderType", "Transparent");
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.EnableKeyword("_ALPHABLEND_ON");
                    mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    break;
                case 3: // Transparent
                    mat.SetOverrideTag("RenderType", "Transparent");
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.DisableKeyword("_ALPHABLEND_ON");
                    mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    break;
            }
        }

        static object GetMaterialPropertyValue(Material mat, string propertyName, ShaderUtil.ShaderPropertyType propType)
        {
            switch (propType)
            {
                case ShaderUtil.ShaderPropertyType.Float:
                case ShaderUtil.ShaderPropertyType.Range:
                    return mat.GetFloat(propertyName);

                case ShaderUtil.ShaderPropertyType.Color:
                    var c = mat.GetColor(propertyName);
                    return new { r = c.r, g = c.g, b = c.b, a = c.a };

                case ShaderUtil.ShaderPropertyType.Vector:
                    var v = mat.GetVector(propertyName);
                    return new { x = v.x, y = v.y, z = v.z, w = v.w };

                case ShaderUtil.ShaderPropertyType.TexEnv:
                    var tex = mat.GetTexture(propertyName);
                    return tex != null ? AssetDatabase.GetAssetPath(tex) : null;

                default:
                    return null;
            }
        }

        static Color ParseColor(string value)
        {
            value = value.Trim();

            if (value.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value, out var hexColor))
                    return hexColor;
                throw new ArgumentException($"Invalid hex color format: '{value}'. Expected #RRGGBB or #RRGGBBAA.");
            }

            if (value.StartsWith("{"))
            {
                var r = ExtractJsonFloat(value, "r");
                var g = ExtractJsonFloat(value, "g");
                var b = ExtractJsonFloat(value, "b");
                float a = 1f;
                try { a = ExtractJsonFloat(value, "a"); } catch { /* alpha is optional */ }
                return new Color(r, g, b, a);
            }

            throw new ArgumentException($"Cannot parse color from: '{value}'. Use JSON {{\"r\":1,\"g\":0,\"b\":0,\"a\":1}} or hex #RRGGBB.");
        }

        static Vector4 ParseVector4(string value)
        {
            var x = ExtractJsonFloat(value, "x");
            var y = ExtractJsonFloat(value, "y");
            var z = ExtractJsonFloat(value, "z");
            float w = 0f;
            try { w = ExtractJsonFloat(value, "w"); } catch { /* w is optional */ }
            return new Vector4(x, y, z, w);
        }

        static float ExtractJsonFloat(string json, string key)
        {
            var keyPattern = $"\"{key}\"";
            var idx = json.IndexOf(keyPattern, StringComparison.Ordinal);
            if (idx < 0)
                throw new ArgumentException($"Key '{key}' not found in JSON: {json}");

            idx += keyPattern.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':'))
                idx++;

            var start = idx;
            while (idx < json.Length && (char.IsDigit(json[idx]) || json[idx] == '.' || json[idx] == '-' || json[idx] == 'e' || json[idx] == 'E' || json[idx] == '+'))
                idx++;

            var numberStr = json.Substring(start, idx - start);
            return float.Parse(numberStr, System.Globalization.CultureInfo.InvariantCulture);
        }

        static void EnsureFolderExists(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return;

            folderPath = folderPath.Replace('\\', '/');

            if (AssetDatabase.IsValidFolder(folderPath))
                return;

            var parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolderExists(parent);

            var folderName = Path.GetFileName(folderPath);
            AssetDatabase.CreateFolder(parent ?? "", folderName);
        }
    }
}
