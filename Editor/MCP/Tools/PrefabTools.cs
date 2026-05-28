using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using ArcForge.Hades.Editor.MCP;
using ArcForge.Hades.Editor.Core;

namespace ArcForge.Hades.Editor.MCP.Tools
{
    [InitializeOnLoad]
    public static class PrefabTools
    {
        static PrefabTools()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ClosePrefabEditingSession;
        }

        [MCPTool("prefab_create", "Save a scene GameObject as a prefab asset (creates parent directories if needed)")]
        public static MCPToolResult CreatePrefab(
            [MCPToolParam("Scene GameObject name or path (e.g. 'Player')", required: true)] string game_object_path,
            [MCPToolParam("Asset path for the prefab (e.g. 'Assets/Prefabs/Player.prefab')", required: true)] string asset_path)
        {
            var go = FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            // Ensure parent directories exist
            var absolutePath = PathSandbox.ResolveWritable(asset_path);
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            bool success;
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, asset_path, out success);
            if (!success || prefab == null)
                return MCPToolResult.Error($"Failed to save prefab at '{asset_path}'. Ensure the path ends with .prefab and is under the Assets folder.");

            return MCPToolResult.Success(new { createdAsset = asset_path });
        }

        [MCPTool("prefab_instantiate", "Instantiate a prefab into the scene with optional parent (supports undo)")]
        public static MCPToolResult InstantiatePrefab(
            [MCPToolParam("Prefab asset path (e.g. 'Assets/Prefabs/Player.prefab')", required: true)] string prefab_path,
            [MCPToolParam("Parent GameObject path (omit for root)")] string parent)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path);
            if (prefab == null)
                return MCPToolResult.Error(
                    $"Prefab not found at '{prefab_path}'. Ensure the path is a valid project-relative asset path (e.g. 'Assets/Prefabs/MyPrefab.prefab').");

            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null)
                return MCPToolResult.Error($"Failed to instantiate prefab at '{prefab_path}'.");

            if (!string.IsNullOrEmpty(parent))
            {
                var parentGo = GameObjectResolver.FindByPath(parent);
                if (parentGo == null)
                {
                    Object.DestroyImmediate(instance);
                    var rootNames = GetRootObjectNames();
                    return MCPToolResult.Error(
                        $"Parent not found: '{parent}'. Root objects in scene: {string.Join(", ", rootNames)}");
                }
                instance.transform.SetParent(parentGo.transform);
            }

            Undo.RegisterCreatedObjectUndo(instance, $"MCP Instantiate {prefab.name}");
            return MCPToolResult.Success(new { path = GetPath(instance) });
        }

        [MCPTool("prefab_apply_overrides", "Apply all overrides on a prefab instance back to the source prefab asset")]
        public static MCPToolResult ApplyPrefabOverrides(
            [MCPToolParam("Prefab instance GameObject name or path", required: true)] string game_object_path)
        {
            var go = FindGameObject(game_object_path);
            if (go == null)
                return GameObjectNotFoundError(game_object_path);

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                return MCPToolResult.Error(
                    $"GameObject '{GetPath(go)}' is not a prefab instance. " +
                    "Only prefab instances in the scene can have overrides applied.");

            PrefabUtility.ApplyPrefabInstance(go, InteractionMode.AutomatedAction);
            var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            return MCPToolResult.Success(new { applied = GetPath(go), sourcePrefab = sourcePath });
        }

        [MCPTool("prefab_get_contents", "Inspect a prefab asset's hierarchy without instantiating it in the scene")]
        public static MCPToolResult GetPrefabContents(
            [MCPToolParam("Prefab asset path (e.g. 'Assets/Prefabs/Player.prefab')", required: true)] string prefab_path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path);
            if (prefab == null)
                return MCPToolResult.Error(
                    $"Prefab not found at '{prefab_path}'. Ensure the path is a valid project-relative asset path (e.g. 'Assets/Prefabs/MyPrefab.prefab').");

            var root = PrefabUtility.LoadPrefabContents(prefab_path);
            try
            {
                var tree = BuildNode(root);
                return MCPToolResult.Success(new { prefab = prefab_path, hierarchy = tree });
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ── Prefab Editing Session State ──

        static GameObject _editingRoot;
        static string _editingPrefabPath;

        [MCPTool("prefab_edit_property", "Set a serialized property inside a prefab asset (atomic load/edit/save)")]
        public static MCPToolResult EditPrefabProperty(
            [MCPToolParam("Prefab asset path", required: true)] string prefab_path,
            [MCPToolParam("Component type name", required: true)] string component_type,
            [MCPToolParam("Property name", required: true)] string property_name,
            [MCPToolParam("Value to set", required: true)] string value)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path);
            if (prefab == null)
                return MCPToolResult.Error($"Prefab not found at '{prefab_path}'.");

            var root = PrefabUtility.LoadPrefabContents(prefab_path);
            try
            {
                var type = ComponentTools.FindComponentType(component_type);
                if (type == null)
                    return MCPToolResult.Error($"Component type not found: '{component_type}'.");

                var component = root.GetComponent(type);
                if (component == null)
                {
                    var existing = root.GetComponents<Component>()
                        .Where(c => c != null).Select(c => c.GetType().Name).ToArray();
                    return MCPToolResult.Error(
                        $"Component '{component_type}' not found on prefab root. " +
                        $"Existing: {string.Join(", ", existing)}");
                }

                var so = new SerializedObject(component);
                var prop = so.FindProperty(property_name);
                if (prop == null)
                    return MCPToolResult.Error($"Property '{property_name}' not found on {component_type}.");

                ComponentTools.SetSerializedPropertyValue(prop, value);
                so.ApplyModifiedProperties();
                PrefabUtility.SaveAsPrefabAsset(root, prefab_path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return MCPToolResult.Success(new
            {
                prefab = prefab_path,
                component = component_type,
                property = property_name,
                newValue = value
            });
        }

        [MCPTool("prefab_open_editing", "Open a prefab for multi-edit session. " +
            "Use existing component/reference/event tools on the returned root path, then call prefab_save_editing.")]
        public static MCPToolResult OpenPrefabEditing(
            [MCPToolParam("Prefab asset path", required: true)] string prefab_path)
        {
            if (_editingRoot != null)
                return MCPToolResult.Error(
                    $"A prefab is already open for editing: '{_editingPrefabPath}'. " +
                    "Call prefab_save_editing first.");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefab_path);
            if (prefab == null)
                return MCPToolResult.Error($"Prefab not found at '{prefab_path}'.");

            _editingRoot = PrefabUtility.LoadPrefabContents(prefab_path);
            _editingPrefabPath = prefab_path;

            return MCPToolResult.Success(new
            {
                prefab = prefab_path,
                rootPath = _editingRoot.name,
                components = _editingRoot.GetComponents<Component>()
                    .Where(c => c != null).Select(c => c.GetType().Name).ToArray()
            });
        }

        [MCPTool("prefab_save_editing", "Save and close the current prefab editing session")]
        public static MCPToolResult SavePrefabEditing()
        {
            if (_editingRoot == null)
                return MCPToolResult.Error("No prefab is currently open for editing. Call prefab_open_editing first.");

            var path = _editingPrefabPath;
            PrefabUtility.SaveAsPrefabAsset(_editingRoot, _editingPrefabPath);
            PrefabUtility.UnloadPrefabContents(_editingRoot);
            _editingRoot = null;
            _editingPrefabPath = null;

            return MCPToolResult.Success(new { saved = path });
        }

        internal static void ClosePrefabEditingSession()
        {
            if (_editingRoot != null)
            {
                PrefabUtility.UnloadPrefabContents(_editingRoot);
                _editingRoot = null;
                _editingPrefabPath = null;
            }
        }

        [MCPTool("prefab_create_variant", "Create a prefab variant from a base prefab")]
        public static MCPToolResult CreatePrefabVariant(
            [MCPToolParam("Base prefab asset path", required: true)] string base_prefab_path,
            [MCPToolParam("Variant asset path", required: true)] string variant_path)
        {
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(base_prefab_path);
            if (basePrefab == null)
                return MCPToolResult.Error($"Base prefab not found at '{base_prefab_path}'.");

            var instance = PrefabUtility.InstantiatePrefab(basePrefab) as GameObject;
            if (instance == null)
                return MCPToolResult.Error($"Failed to instantiate base prefab.");

            var directory = System.IO.Path.GetDirectoryName(variant_path);
            if (!string.IsNullOrEmpty(directory))
                EnsureFolderExists(directory);

            bool success;
            PrefabUtility.SaveAsPrefabAssetAndConnect(instance, variant_path, InteractionMode.AutomatedAction, out success);
            Object.DestroyImmediate(instance);

            if (!success)
                return MCPToolResult.Error($"Failed to save variant at '{variant_path}'.");

            return MCPToolResult.Success(new { basePrefab = base_prefab_path, variant = variant_path });
        }

        // ── Helpers ──

        static void EnsureFolderExists(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolderExists(parent);
            var folderName = System.IO.Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(folderName))
                AssetDatabase.CreateFolder(parent, folderName);
        }

        static GameObject FindGameObject(string path)
        {
            return GameObjectResolver.FindByPath(path);
        }

        static MCPToolResult GameObjectNotFoundError(string path)
        {
            var rootNames = GetRootObjectNames();
            return MCPToolResult.Error(
                $"GameObject not found: '{path}'. " +
                $"Root objects in scene: {string.Join(", ", rootNames)}");
        }

        static string[] GetRootObjectNames()
        {
            return SceneManager.GetActiveScene()
                .GetRootGameObjects()
                .Select(r => r.name)
                .ToArray();
        }

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

        static object BuildNode(GameObject go)
        {
            return new
            {
                name = go.name,
                active = go.activeSelf,
                components = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToArray(),
                children = Enumerable.Range(0, go.transform.childCount)
                    .Select(i => BuildNode(go.transform.GetChild(i).gameObject))
                    .ToArray()
            };
        }
    }
}
