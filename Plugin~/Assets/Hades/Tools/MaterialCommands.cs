// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEngine;

namespace Hades.Tools
{
    /// <summary>
    /// Class-1 (single-tick, no reload lease - see the "52 Editor tools" plan's operation-class
    /// table) material mutations: create/duplicate a material asset, set a shader property, assign
    /// a material to a Renderer, and swap a material's shader. Same no-lease contract as
    /// SceneCommands/ComponentCommands - none of these ever touch the <c>gate</c> parameter.
    ///
    /// Undo per tool (deliberately not a uniform claim - see this plan's own guidance): material.
    /// create/duplicate register the newly created asset with <see cref="Undo.RegisterCreatedObjectUndo"/>
    /// (same primitive SceneCommands uses for a new GameObject); material.set_property/assign/
    /// swap_shader call <see cref="Undo.RecordObject"/> on the object actually mutated (the
    /// Material asset for set_property/swap_shader, the Renderer component for assign) BEFORE
    /// mutating it - the old package's MaterialTools already did this correctly for these three,
    /// unlike SetComponentProperty, but is re-verified here rather than assumed.
    ///
    /// material.set_property/swap_shader mutate an on-disk ASSET (not a scene object), so - unlike
    /// scene/component mutations, which persist only through an explicit scene_save - they call
    /// <see cref="AssetDatabase.SaveAssetIfDirty"/> on just the material touched, so the change is
    /// visible to the graph without a separate "material_save" tool (there isn't one). Deliberately
    /// NOT the old package's blanket <see cref="AssetDatabase.SaveAssets"/>, which would flush every
    /// dirty asset in the project - a footprint bigger than what the tool was asked to write.
    /// </summary>
    public static class MaterialCommands
    {
        // ------------------------------------------------------------------------- material.create

        internal static JsonValue CreateMaterial(ReloadGate gate, JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "material.create");
            var shaderName = JsonParams.OptionalString(@params, "shader");
            if (string.IsNullOrEmpty(shaderName)) shaderName = "Standard";

            var shader = Shader.Find(shaderName) ?? throw ShaderNotFoundError(shaderName);

            AssetFolders.EnsureExists(AssetFolders.DirectoryName(path));

            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
            Undo.RegisterCreatedObjectUndo(material, "Hades Create Material");

            return JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(path))
                .SetProperty("shader", JsonValue.String(shader.name))
                .SetProperty("guid", JsonValue.String(AssetDatabase.AssetPathToGUID(path)));
        }

        // ------------------------------------------------------------------- material.set_property

        /// <summary>Property type is resolved from the material's OWN shader (via
        /// <see cref="ShaderUtil.GetPropertyType"/>), never guessed from the caller - the wire's
        /// JsonValue is already richly typed (a number, a string, or a nested object), so unlike
        /// the old package (which had to disambiguate a raw string with a propertyType hint) this
        /// needs no hint parameter: the shader's declared type says exactly which JSON shape
        /// <paramref name="@params"/>'s 'value' must take, and a mismatch is an actionable error
        /// rather than a silent misparse.</summary>
        internal static JsonValue SetProperty(ReloadGate gate, JsonValue @params)
        {
            var materialPath = JsonParams.RequireString(@params, "materialPath", "material.set_property");
            var propertyName = JsonParams.RequireString(@params, "propertyName", "material.set_property");
            var value = JsonParams.OptionalValue(@params, "value") ?? JsonValue.Null;

            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath) ?? throw MaterialNotFoundError(materialPath);

            if (!TryFindShaderPropertyType(mat.shader, propertyName, out var propType))
            {
                throw new ArgumentException(
                    "Property '" + propertyName + "' not found on shader '" + mat.shader.name + "'. Valid properties: "
                    + string.Join(", ", ShaderPropertyNames(mat.shader)) + ".");
            }

            Undo.RecordObject(mat, "Hades Set Material Property " + propertyName);
            ApplyShaderProperty(mat, propertyName, propType, value);

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);

            return JsonValue.NewObject()
                .SetProperty("materialPath", JsonValue.String(materialPath))
                .SetProperty("property", JsonValue.String(propertyName))
                .SetProperty("newValue", value);
        }

        /// <summary>Dispatches on the STRING name of the shader property type rather than the enum
        /// member directly - defensive against this exact Unity version's
        /// <see cref="ShaderUtil.ShaderPropertyType"/> not declaring a member this code references
        /// by name (this plugin has no fast Unity-side compile check the way the app's C# does; a
        /// bad enum reference here would only surface after a full batchmode Editor launch). An
        /// unrecognised type string falls through to the same actionable error either way.</summary>
        static void ApplyShaderProperty(Material mat, string propertyName, string propType, JsonValue value)
        {
            switch (propType)
            {
                case "Float":
                case "Range":
                case "Int":
                    mat.SetFloat(propertyName, (float)RequireNumber(value, propertyName));
                    break;
                case "Color":
                    mat.SetColor(propertyName, ColorFromJson(value));
                    break;
                case "Vector":
                    mat.SetVector(propertyName, Vector4FromJson(value));
                    break;
                case "TexEnv":
                    mat.SetTexture(propertyName, ResolveTexture(value));
                    break;
                default:
                    throw new ArgumentException("Property '" + propertyName + "' has unsupported shader property type " + propType + ".");
            }
        }

        // ------------------------------------------------------------------------ material.assign

        internal static JsonValue AssignMaterial(ReloadGate gate, JsonValue @params)
        {
            var goPath = JsonParams.RequireString(@params, "gameObjectPath", "material.assign");
            var materialPath = JsonParams.RequireString(@params, "materialPath", "material.assign");
            var slotIndex = JsonParams.OptionalInt(@params, "slot", 0);

            var go = GameObjectPaths.FindByPath(goPath) ?? throw GameObjectPaths.NotFoundError(goPath);
            var renderer = (Renderer)GameObjectPaths.RequireComponent(go, typeof(Renderer), "Renderer");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath) ?? throw MaterialNotFoundError(materialPath);

            var mats = renderer.sharedMaterials;
            if (slotIndex < 0 || slotIndex >= mats.Length)
            {
                throw new ArgumentException(
                    "Slot index " + slotIndex + " is out of range. Renderer has " + mats.Length + " material slot(s).");
            }

            Undo.RecordObject(renderer, "Hades Assign Material " + mat.name);
            mats[slotIndex] = mat;
            renderer.sharedMaterials = mats;

            return JsonValue.NewObject()
                .SetProperty("gameObject", JsonValue.String(GameObjectPaths.GetPath(go)))
                .SetProperty("renderer", JsonValue.String(renderer.GetType().Name))
                .SetProperty("slot", JsonValue.Integer(slotIndex))
                .SetProperty("material", JsonValue.String(mat.name))
                .SetProperty("materialPath", JsonValue.String(materialPath));
        }

        // ---------------------------------------------------------------------- material.duplicate

        internal static JsonValue DuplicateMaterial(ReloadGate gate, JsonValue @params)
        {
            var sourcePath = JsonParams.RequireString(@params, "sourcePath", "material.duplicate");
            var destPath = JsonParams.RequireString(@params, "destPath", "material.duplicate");

            if (AssetDatabase.LoadAssetAtPath<Material>(sourcePath) == null)
                throw new ArgumentException("Source material not found at path: '" + sourcePath + "'.");

            AssetFolders.EnsureExists(AssetFolders.DirectoryName(destPath));

            if (!AssetDatabase.CopyAsset(sourcePath, destPath))
                throw new ArgumentException("Failed to copy material from '" + sourcePath + "' to '" + destPath + "'.");

            var duplicated = AssetDatabase.LoadAssetAtPath<Material>(destPath);
            if (duplicated != null) Undo.RegisterCreatedObjectUndo(duplicated, "Hades Duplicate Material");

            return JsonValue.NewObject()
                .SetProperty("source", JsonValue.String(sourcePath))
                .SetProperty("destination", JsonValue.String(destPath));
        }

        // -------------------------------------------------------------------- material.swap_shader

        /// <summary>Unity silently drops any shader property whose name/type does not exist on the
        /// new shader - the caller has no other way to find out which of its old values just
        /// vanished. Reported as two name lists, computed from each shader's OWN declared
        /// properties (name AND type must both match to count as "survived" - a same-named
        /// property whose type changed, e.g. Color -> Vector, does not actually carry its value
        /// over either).</summary>
        internal static JsonValue SwapShader(ReloadGate gate, JsonValue @params)
        {
            var materialPath = JsonParams.RequireString(@params, "materialPath", "material.swap_shader");
            var shaderName = JsonParams.RequireString(@params, "shader", "material.swap_shader");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath) ?? throw MaterialNotFoundError(materialPath);
            var newShader = Shader.Find(shaderName) ?? throw ShaderNotFoundError(shaderName);

            var previousShaderName = mat.shader.name;
            var oldProps = ShaderPropertyTypes(mat.shader);
            var newProps = ShaderPropertyTypes(newShader);

            Undo.RecordObject(mat, "Hades Swap Shader " + shaderName);
            mat.shader = newShader;

            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);

            var survived = JsonValue.NewArray();
            var lost = JsonValue.NewArray();
            foreach (var pair in oldProps)
            {
                if (newProps.TryGetValue(pair.Key, out var newType) && newType == pair.Value)
                    survived.Add(JsonValue.String(pair.Key));
                else
                    lost.Add(JsonValue.String(pair.Key));
            }

            return JsonValue.NewObject()
                .SetProperty("materialPath", JsonValue.String(materialPath))
                .SetProperty("previousShader", JsonValue.String(previousShaderName))
                .SetProperty("newShader", JsonValue.String(shaderName))
                .SetProperty("survivedProperties", survived)
                .SetProperty("lostProperties", lost);
        }

        // ---------------------------------------------------------------------------- shared

        static bool TryFindShaderPropertyType(Shader shader, string propertyName, out string propType)
        {
            var count = ShaderUtil.GetPropertyCount(shader);
            for (var i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyName(shader, i) == propertyName)
                {
                    propType = ShaderUtil.GetPropertyType(shader, i).ToString();
                    return true;
                }
            }
            propType = null;
            return false;
        }

        static string[] ShaderPropertyNames(Shader shader)
        {
            var count = ShaderUtil.GetPropertyCount(shader);
            var names = new string[count];
            for (var i = 0; i < count; i++) names[i] = ShaderUtil.GetPropertyName(shader, i);
            return names;
        }

        static Dictionary<string, string> ShaderPropertyTypes(Shader shader)
        {
            var result = new Dictionary<string, string>();
            var count = ShaderUtil.GetPropertyCount(shader);
            for (var i = 0; i < count; i++)
                result[ShaderUtil.GetPropertyName(shader, i)] = ShaderUtil.GetPropertyType(shader, i).ToString();
            return result;
        }

        static Color ColorFromJson(JsonValue value)
        {
            if (value == null || value.Kind != JsonValueKind.Object)
                throw new ArgumentException("Color property requires a JSON object, e.g. {\"r\":1,\"g\":0,\"b\":0,\"a\":1}.");
            return new Color(RequireFloat(value, "r"), RequireFloat(value, "g"), RequireFloat(value, "b"), OptionalFloat(value, "a", 1f));
        }

        static Vector4 Vector4FromJson(JsonValue value)
        {
            if (value == null || value.Kind != JsonValueKind.Object)
                throw new ArgumentException("Vector property requires a JSON object, e.g. {\"x\":1,\"y\":0,\"z\":0,\"w\":0}.");
            return new Vector4(RequireFloat(value, "x"), RequireFloat(value, "y"), RequireFloat(value, "z"), OptionalFloat(value, "w", 0f));
        }

        static Texture ResolveTexture(JsonValue value)
        {
            if (value == null || value.Kind == JsonValueKind.Null) return null;
            if (value.Kind != JsonValueKind.String)
                throw new ArgumentException("Texture property requires a string asset path, or null to clear it.");

            var path = value.AsString();
            var texture = AssetDatabase.LoadAssetAtPath<Texture>(path);
            if (texture == null) throw new ArgumentException("Texture asset not found at path: '" + path + "'.");
            return texture;
        }

        static double RequireNumber(JsonValue value, string propertyName)
        {
            if (value != null && (value.Kind == JsonValueKind.Integer || value.Kind == JsonValueKind.Float)) return value.AsDouble();
            throw new ArgumentException("Property '" + propertyName + "' needs a numeric value.");
        }

        static float RequireFloat(JsonValue obj, string key)
        {
            if (obj.TryGetProperty(key, out var v) && v != null && (v.Kind == JsonValueKind.Float || v.Kind == JsonValueKind.Integer))
                return (float)v.AsDouble();
            throw new ArgumentException("Expected a numeric '" + key + "' field in the JSON value.");
        }

        static float OptionalFloat(JsonValue obj, string key, float defaultValue)
        {
            if (obj.TryGetProperty(key, out var v) && v != null && (v.Kind == JsonValueKind.Float || v.Kind == JsonValueKind.Integer))
                return (float)v.AsDouble();
            return defaultValue;
        }

        static ArgumentException MaterialNotFoundError(string path) =>
            new ArgumentException(
                "Material not found at path: '" + path + "'. Use search_by_name (kind=\"Material\") to find the correct project-relative path.");

        static ArgumentException ShaderNotFoundError(string shaderName) =>
            new ArgumentException(
                "Shader '" + shaderName + "' not found. Verify the name (e.g. 'Standard', 'Unlit/Color', "
                + "'Universal Render Pipeline/Lit') and that it is included in the project.");
    }
}
