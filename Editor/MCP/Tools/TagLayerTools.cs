using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class TagLayerTools
    {
        static readonly string[] BuiltInTags =
        {
            "Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController"
        };

        [MCPTool("tag_create", "Create a custom tag in the project's TagManager")]
        public static MCPToolResult CreateTag(
            [MCPToolParam("Tag name to create", required: true)] string name)
        {
            if (string.IsNullOrEmpty(name))
                return MCPToolResult.Error("Tag name is required.");

            var asset = AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset");
            if (asset == null)
                return MCPToolResult.Error("Could not load TagManager.asset.");

            var so = new SerializedObject(asset);
            var tags = so.FindProperty("tags");

            if (BuiltInTags.Contains(name))
                return MCPToolResult.Error($"Tag '{name}' is a built-in tag and already exists.");

            for (int i = 0; i < tags.arraySize; i++)
            {
                if (tags.GetArrayElementAtIndex(i).stringValue == name)
                    return MCPToolResult.Error($"Tag '{name}' already exists.");
            }

            Undo.IncrementCurrentGroup();
            tags.InsertArrayElementAtIndex(tags.arraySize);
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = name;
            so.ApplyModifiedProperties();
            Undo.SetCurrentGroupName($"MCP Create Tag: {name}");
            AssetDatabase.SaveAssets();

            return MCPToolResult.Success(new { created = name });
        }

        [MCPTool("tag_delete", "Delete a custom tag from the project's TagManager (cannot delete built-in tags)")]
        public static MCPToolResult DeleteTag(
            [MCPToolParam("Tag name to delete", required: true)] string name)
        {
            if (string.IsNullOrEmpty(name))
                return MCPToolResult.Error("Tag name is required.");

            if (BuiltInTags.Contains(name))
                return MCPToolResult.Error($"Cannot delete '{name}' — it is a built-in tag.");

            var asset = AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset");
            if (asset == null)
                return MCPToolResult.Error("Could not load TagManager.asset.");

            var so = new SerializedObject(asset);
            var tags = so.FindProperty("tags");

            for (int i = 0; i < tags.arraySize; i++)
            {
                if (tags.GetArrayElementAtIndex(i).stringValue == name)
                {
                    Undo.IncrementCurrentGroup();
                    tags.DeleteArrayElementAtIndex(i);
                    so.ApplyModifiedProperties();
                    Undo.SetCurrentGroupName($"MCP Delete Tag: {name}");
                    AssetDatabase.SaveAssets();
                    return MCPToolResult.Success(new { deleted = name });
                }
            }

            var existing = new List<string>();
            for (int i = 0; i < tags.arraySize; i++)
                existing.Add(tags.GetArrayElementAtIndex(i).stringValue);

            return MCPToolResult.Error(
                $"Tag '{name}' not found. Custom tags: {string.Join(", ", existing)}");
        }

        [MCPTool("tag_list", "List all tags in the project (built-in and custom)")]
        public static MCPToolResult ListTags()
        {
            var asset = AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset");
            if (asset == null)
                return MCPToolResult.Error("Could not load TagManager.asset.");

            var so = new SerializedObject(asset);
            var tags = so.FindProperty("tags");

            var result = new List<object>();

            foreach (var builtIn in BuiltInTags)
                result.Add(new { name = builtIn, builtIn = true });

            for (int i = 0; i < tags.arraySize; i++)
            {
                var tagName = tags.GetArrayElementAtIndex(i).stringValue;
                if (!string.IsNullOrEmpty(tagName))
                    result.Add(new { name = tagName, builtIn = false });
            }

            return MCPToolResult.Success(new { tags = result });
        }

        [MCPTool("layer_create", "Create a layer in the project's TagManager (assigns to first available slot 8-31, or specific index)")]
        public static MCPToolResult CreateLayer(
            [MCPToolParam("Layer name to create", required: true)] string name,
            [MCPToolParam("Specific layer index (8-31). Omit to auto-assign first available.")] string layerIndex = null)
        {
            if (string.IsNullOrEmpty(name))
                return MCPToolResult.Error("Layer name is required.");

            var asset = AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset");
            if (asset == null)
                return MCPToolResult.Error("Could not load TagManager.asset.");

            var so = new SerializedObject(asset);
            var layers = so.FindProperty("layers");

            if (!string.IsNullOrEmpty(layerIndex))
            {
                if (!int.TryParse(layerIndex, out var idx) || idx < 0 || idx >= 32)
                    return MCPToolResult.Error($"Layer index must be 0-31, got '{layerIndex}'.");

                if (idx < 8)
                    return MCPToolResult.Error($"Layer indices 0-7 are reserved for Unity built-in layers. Use indices 8-31.");

                var current = layers.GetArrayElementAtIndex(idx).stringValue;
                if (!string.IsNullOrEmpty(current))
                    return MCPToolResult.Error(
                        $"Layer index {idx} is occupied by '{current}'. " +
                        "Choose a different index or omit layerIndex to auto-assign.");

                Undo.IncrementCurrentGroup();
                layers.GetArrayElementAtIndex(idx).stringValue = name;
                so.ApplyModifiedProperties();
                Undo.SetCurrentGroupName($"MCP Create Layer: {name} [{idx}]");
                AssetDatabase.SaveAssets();
                return MCPToolResult.Success(new { created = name, index = idx });
            }

            for (int i = 8; i < 32 && i < layers.arraySize; i++)
            {
                if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue))
                {
                    Undo.IncrementCurrentGroup();
                    layers.GetArrayElementAtIndex(i).stringValue = name;
                    so.ApplyModifiedProperties();
                    Undo.SetCurrentGroupName($"MCP Create Layer: {name} [{i}]");
                    AssetDatabase.SaveAssets();
                    return MCPToolResult.Success(new { created = name, index = i });
                }
            }

            return MCPToolResult.Error("All user layer slots (8-31) are occupied.");
        }

        [MCPTool("layer_list", "List all 32 layers with index, name, and empty status")]
        public static MCPToolResult ListLayers()
        {
            var asset = AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset");
            if (asset == null)
                return MCPToolResult.Error("Could not load TagManager.asset.");

            var so = new SerializedObject(asset);
            var layers = so.FindProperty("layers");

            var result = new List<object>();
            for (int i = 0; i < layers.arraySize && i < 32; i++)
            {
                var layerName = layers.GetArrayElementAtIndex(i).stringValue;
                result.Add(new
                {
                    index = i,
                    name = string.IsNullOrEmpty(layerName) ? "" : layerName,
                    empty = string.IsNullOrEmpty(layerName)
                });
            }

            return MCPToolResult.Success(new { layers = result });
        }
    }
}
