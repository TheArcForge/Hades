// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;

namespace Hades.Tools
{
    /// <summary>
    /// Class-1 (single-tick, no reload lease) tag/layer mutations: create/delete a custom tag,
    /// create a layer. Same no-lease contract as SceneCommands/ComponentCommands.
    ///
    /// Undo here is best-effort, not proven by a PerformUndo-revert test the way scene/component
    /// mutations are - see this plan's own guidance: tag.create/tag.delete/layer.create write
    /// ProjectSettings/TagManager.asset, a project-level singleton asset outside any scene, and
    /// whether Unity's Undo stack reliably covers it the same way it covers a scene GameObject or
    /// component was NOT assumed here. <see cref="Undo.RecordObject"/> is still called before every
    /// mutation (the old package's TagLayerTools called neither RecordObject NOR anything else
    /// undo-related - exactly the missing-RecordObject bug this plan's own text calls out for
    /// SetComponentProperty, repeated here for tags/layers), so IF Unity does cover it, undo works;
    /// this file just does not stake a tested claim on it either way.
    /// </summary>
    public static class TagLayerCommands
    {
        static readonly string[] BuiltInTags =
        {
            "Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController"
        };

        // ------------------------------------------------------------------------------ tag.create

        internal static JsonValue CreateTag(ReloadGate gate, JsonValue @params)
        {
            var name = JsonParams.RequireString(@params, "name", "tag.create");

            if (Array.IndexOf(BuiltInTags, name) >= 0)
                throw new ArgumentException("Tag '" + name + "' is a built-in tag and already exists.");

            var tagManager = LoadTagManager();
            var so = new SerializedObject(tagManager);
            var tags = so.FindProperty("tags");

            for (var i = 0; i < tags.arraySize; i++)
            {
                if (tags.GetArrayElementAtIndex(i).stringValue == name)
                    throw new ArgumentException("Tag '" + name + "' already exists.");
            }

            Undo.RecordObject(tagManager, "Hades Create Tag " + name);
            tags.InsertArrayElementAtIndex(tags.arraySize);
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = name;
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssetIfDirty(tagManager);

            return JsonValue.NewObject().SetProperty("created", JsonValue.String(name));
        }

        // ------------------------------------------------------------------------------ tag.delete

        internal static JsonValue DeleteTag(ReloadGate gate, JsonValue @params)
        {
            var name = JsonParams.RequireString(@params, "name", "tag.delete");

            if (Array.IndexOf(BuiltInTags, name) >= 0)
                throw new ArgumentException("Cannot delete '" + name + "' - it is a built-in tag.");

            var tagManager = LoadTagManager();
            var so = new SerializedObject(tagManager);
            var tags = so.FindProperty("tags");

            for (var i = 0; i < tags.arraySize; i++)
            {
                if (tags.GetArrayElementAtIndex(i).stringValue != name) continue;

                Undo.RecordObject(tagManager, "Hades Delete Tag " + name);
                tags.DeleteArrayElementAtIndex(i);
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssetIfDirty(tagManager);
                return JsonValue.NewObject().SetProperty("deleted", JsonValue.String(name));
            }

            var existing = new List<string>();
            for (var i = 0; i < tags.arraySize; i++)
            {
                var value = tags.GetArrayElementAtIndex(i).stringValue;
                if (!string.IsNullOrEmpty(value)) existing.Add(value);
            }
            throw new ArgumentException("Tag '" + name + "' not found. Custom tags: " + string.Join(", ", existing) + ".");
        }

        // ---------------------------------------------------------------------------- layer.create

        internal static JsonValue CreateLayer(ReloadGate gate, JsonValue @params)
        {
            var name = JsonParams.RequireString(@params, "name", "layer.create");
            var explicitIndex = JsonParams.OptionalValue(@params, "layerIndex");

            var tagManager = LoadTagManager();
            var so = new SerializedObject(tagManager);
            var layers = so.FindProperty("layers");

            // Uneven-validation audit: tag.create (above) already refuses a duplicate NAME; this
            // sibling only ever checked its target SLOT for a collision, never the name itself - so
            // two layers with the same name at different indices was silently accepted, leaving
            // LayerMask.NameToLayer(name) to resolve ambiguously between them. Same duplicate-name
            // refusal shape as CreateTag above, scanning every slot rather than just the requested one.
            for (var i = 0; i < layers.arraySize; i++)
            {
                var existingName = layers.GetArrayElementAtIndex(i).stringValue;
                if (!string.IsNullOrEmpty(existingName) && existingName == name)
                    throw new ArgumentException("Layer '" + name + "' already exists at index " + i + ".");
            }

            if (explicitIndex != null && explicitIndex.Kind != JsonValueKind.Null)
            {
                if (explicitIndex.Kind != JsonValueKind.Integer && explicitIndex.Kind != JsonValueKind.Float)
                    throw new ArgumentException("'layerIndex' must be an integer.");

                var idx = (int)explicitIndex.AsDouble();
                if (idx < 8 || idx > 31)
                    throw new ArgumentException("Layer index must be 8-31 (0-7 are reserved for Unity's built-in layers), got " + idx + ".");

                var current = layers.GetArrayElementAtIndex(idx).stringValue;
                if (!string.IsNullOrEmpty(current))
                {
                    throw new ArgumentException(
                        "Layer index " + idx + " is occupied by '" + current + "'. Choose a different index or omit layerIndex to auto-assign.");
                }

                Undo.RecordObject(tagManager, "Hades Create Layer " + name);
                layers.GetArrayElementAtIndex(idx).stringValue = name;
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssetIfDirty(tagManager);
                return JsonValue.NewObject().SetProperty("created", JsonValue.String(name)).SetProperty("index", JsonValue.Integer(idx));
            }

            // Auto-assign: the FIRST EMPTY slot in 8..31 - NOT "one past the last occupied slot".
            // Layers are a fixed 32-element array where any of 8..31 may independently be blank -
            // see layer_list's own note that index 8 being free is not the same as layers ending
            // at 7. Scanning every slot (rather than tracking a running "highest used" index) is
            // what makes that true regardless of which slots happen to be occupied.
            for (var i = 8; i < 32 && i < layers.arraySize; i++)
            {
                if (!string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue)) continue;

                Undo.RecordObject(tagManager, "Hades Create Layer " + name);
                layers.GetArrayElementAtIndex(i).stringValue = name;
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssetIfDirty(tagManager);
                return JsonValue.NewObject().SetProperty("created", JsonValue.String(name)).SetProperty("index", JsonValue.Integer(i));
            }

            throw new ArgumentException("All user layer slots (8-31) are occupied. Call layer_list to see current assignments.");
        }

        // ---------------------------------------------------------------------------- shared

        static UnityEngine.Object LoadTagManager() =>
            AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset")
                ?? throw new InvalidOperationException("Could not load ProjectSettings/TagManager.asset.");
    }
}
