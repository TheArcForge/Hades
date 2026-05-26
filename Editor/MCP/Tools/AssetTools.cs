using System.IO;
using System.Linq;
using UnityEditor;
using ArcForge.Hades.Editor.MCP;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    public static class AssetTools
    {
        [MCPTool("asset_get_info", "Get asset metadata: type, GUID, labels, and direct dependencies. " +
            "If the returned type is 'UnityEditor.DefaultAsset', Unity has no native importer for this format — " +
            "common unsupported formats: .webp, .svg, .heic. Convert to PNG/JPG/TGA/PSD/EXR before use.")]
        public static MCPToolResult GetAssetInfo(
            [MCPToolParam("Asset path relative to project root (e.g. 'Assets/Scripts/Player.cs')", required: true)]
            string path)
        {
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type == null)
                return MCPToolResult.Error($"Asset not found at path: {path}");

            var guid = AssetDatabase.GUIDFromAssetPath(path);
            var labels = AssetDatabase.GetLabels(AssetDatabase.LoadMainAssetAtPath(path));
            var dependencies = AssetDatabase.GetDependencies(path, false);

            return MCPToolResult.Success(new
            {
                path,
                type = type.FullName,
                guid = guid.ToString(),
                labels,
                dependencies
            });
        }

        [MCPTool("asset_find", "Search for assets by filter (e.g. 't:Script', 't:Texture player'). Optional folder restriction. Max 100 results.")]
        public static MCPToolResult FindAssets(
            [MCPToolParam("AssetDatabase search filter (e.g. 't:Script', 't:Texture player')", required: true)]
            string filter,
            [MCPToolParam("Comma-separated folder paths to search in (e.g. 'Assets/Scripts,Assets/Prefabs')")]
            string searchInFolders)
        {
            string[] folders = null;
            if (!string.IsNullOrEmpty(searchInFolders))
            {
                folders = searchInFolders
                    .Split(',')
                    .Select(f => f.Trim())
                    .Where(f => f.Length > 0)
                    .ToArray();
            }

            var guids = folders != null && folders.Length > 0
                ? AssetDatabase.FindAssets(filter, folders)
                : AssetDatabase.FindAssets(filter);

            var paths = guids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Take(100)
                .ToArray();

            return MCPToolResult.Success(new
            {
                filter,
                matches = paths,
                count = paths.Length,
                truncated = guids.Length > 100
            });
        }

        [MCPTool("asset_move", "Move or rename an asset from one path to another")]
        public static MCPToolResult MoveAsset(
            [MCPToolParam("Current asset path (e.g. 'Assets/Old/Player.cs')", required: true)]
            string sourcePath,
            [MCPToolParam("Destination asset path (e.g. 'Assets/New/Player.cs')", required: true)]
            string destPath)
        {
            var sourceType = AssetDatabase.GetMainAssetTypeAtPath(sourcePath);
            if (sourceType == null)
                return MCPToolResult.Error($"Source asset not found at path: {sourcePath}");

            // Ensure the destination directory exists
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir) && !AssetDatabase.IsValidFolder(destDir))
            {
                CreateFolderRecursive(destDir);
            }

            var error = AssetDatabase.MoveAsset(sourcePath, destPath);
            if (!string.IsNullOrEmpty(error))
                return MCPToolResult.Error($"Move failed: {error}");

            return MCPToolResult.Success(new
            {
                source = sourcePath,
                destination = destPath
            });
        }

        [MCPTool("asset_import", "Force reimport of an asset at the given path")]
        public static MCPToolResult ImportAsset(
            [MCPToolParam("Asset path to reimport (e.g. 'Assets/Textures/icon.png')", required: true)]
            string path)
        {
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type == null)
                return MCPToolResult.Error($"Asset not found at path: {path}");

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return MCPToolResult.Success(new
            {
                path,
                reimported = true
            });
        }

        // ── Helpers ──

        static void CreateFolderRecursive(string folderPath)
        {
            // Normalize separators
            folderPath = folderPath.Replace('\\', '/');

            if (AssetDatabase.IsValidFolder(folderPath))
                return;

            var parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                CreateFolderRecursive(parent);
            }

            var folderName = Path.GetFileName(folderPath);
            AssetDatabase.CreateFolder(parent ?? "", folderName);
        }
    }
}
