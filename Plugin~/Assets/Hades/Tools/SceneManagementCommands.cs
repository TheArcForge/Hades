// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Hades.Tools
{
    /// <summary>
    /// Class-1 (single-tick, no reload lease) scene-as-a-FILE lifecycle: save/create/duplicate a
    /// scene asset, and set the Build Settings scene list. Deliberately a separate file from
    /// SceneCommands.cs, which mutates a scene's in-memory CONTENTS (GameObjects/hierarchy) -
    /// these instead read/write the .unity file itself via EditorSceneManager/AssetDatabase/
    /// EditorBuildSettings, the same split the old package's own SceneTools vs
    /// SceneManagementTools already drew.
    ///
    /// Undo per tool (not a uniform claim - see this plan's own guidance): scene.save is a pure
    /// filesystem write with nothing to revert (the scene's in-memory content is unchanged by
    /// saving it); scene.set_build mutates a static property with no serialized-object handle
    /// Undo.RecordObject could snapshot. scene.create/scene.duplicate each produce a brand new
    /// SceneAsset, so they attempt <see cref="Undo.RegisterCreatedObjectUndo"/> on it (the same
    /// primitive SceneCommands uses for a new GameObject and MaterialCommands uses for a new
    /// Material) - whether Unity's asset-undo machinery actually deletes a .unity file the same
    /// way it does a .mat file was verified against a real Editor rather than assumed.
    /// </summary>
    public static class SceneManagementCommands
    {
        // -------------------------------------------------------------------------------- scene.save

        internal static JsonValue SaveScene(ReloadGate gate, JsonValue @params)
        {
            var path = JsonParams.OptionalString(@params, "path");
            var scene = SceneManager.GetActiveScene();

            if (string.IsNullOrEmpty(path))
            {
                if (string.IsNullOrEmpty(scene.path))
                {
                    throw new ArgumentException(
                        "Scene has never been saved and no 'path' was provided. Provide a path, e.g. 'Assets/Scenes/MyScene.unity'.");
                }

                EditorSceneManager.SaveScene(scene);
                return JsonValue.NewObject().SetProperty("saved", JsonValue.String(scene.path));
            }

            AssetFolders.EnsureExists(AssetFolders.DirectoryName(path));
            EditorSceneManager.SaveScene(scene, path);
            return JsonValue.NewObject().SetProperty("saved", JsonValue.String(path));
        }

        // ------------------------------------------------------------------------------ scene.create

        internal static JsonValue CreateScene(ReloadGate gate, JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "scene.create");
            var template = JsonParams.OptionalString(@params, "template");

            AssetFolders.EnsureExists(AssetFolders.DirectoryName(path));

            if (!string.IsNullOrEmpty(template))
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(template) == null)
                    throw new ArgumentException("Template scene not found at path: '" + template + "'.");

                if (!AssetDatabase.CopyAsset(template, path))
                    throw new ArgumentException("Failed to copy template scene to '" + path + "'.");
            }
            else
            {
                // Additive + close, deliberately NOT NewSceneMode.Single: creating a new scene
                // asset must never discard whatever the caller currently has open (and possibly
                // unsaved) in the Editor - NewSceneMode.Single would silently replace it with no
                // save-changes prompt in a scripted context. This tool's only observable effect is
                // "a new .unity file now exists"; it does not change what is currently open.
                var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Additive);
                EditorSceneManager.SaveScene(scene, path);
                EditorSceneManager.CloseScene(scene, true);
            }

            var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            if (sceneAsset != null) Undo.RegisterCreatedObjectUndo(sceneAsset, "Hades Create Scene");

            return JsonValue.NewObject().SetProperty("created", JsonValue.String(path));
        }

        // --------------------------------------------------------------------------- scene.duplicate

        internal static JsonValue DuplicateScene(ReloadGate gate, JsonValue @params)
        {
            var sourcePath = JsonParams.RequireString(@params, "sourcePath", "scene.duplicate");
            var destPath = JsonParams.RequireString(@params, "destPath", "scene.duplicate");

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(sourcePath) == null)
                throw new ArgumentException("Source scene not found at path: '" + sourcePath + "'.");

            AssetFolders.EnsureExists(AssetFolders.DirectoryName(destPath));

            if (!AssetDatabase.CopyAsset(sourcePath, destPath))
                throw new ArgumentException("Failed to copy scene from '" + sourcePath + "' to '" + destPath + "'.");

            var duplicated = AssetDatabase.LoadAssetAtPath<SceneAsset>(destPath);
            if (duplicated != null) Undo.RegisterCreatedObjectUndo(duplicated, "Hades Duplicate Scene");

            return JsonValue.NewObject()
                .SetProperty("source", JsonValue.String(sourcePath))
                .SetProperty("destination", JsonValue.String(destPath));
        }

        // ------------------------------------------------------------------------------ scene.set_build

        internal static JsonValue SetBuildScenes(ReloadGate gate, JsonValue @params)
        {
            var scenesParam = JsonParams.OptionalValue(@params, "scenes");
            if (scenesParam == null || scenesParam.Kind != JsonValueKind.Array)
                throw new ArgumentException("scene.set_build requires a 'scenes' array parameter.");

            var count = scenesParam.Items.Count;
            var paths = new string[count];
            var enabledFlags = new bool[count];
            var missing = new List<string>();

            for (var i = 0; i < count; i++)
            {
                var entry = scenesParam.Items[i];
                var path = entry != null ? JsonParams.OptionalString(entry, "path") : null;
                if (string.IsNullOrEmpty(path))
                    throw new ArgumentException("Each entry in 'scenes' requires a non-empty 'path'.");

                var enabled = JsonParams.OptionalBool(entry, "enabled", true);

                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null) missing.Add(path);

                paths[i] = path;
                enabledFlags[i] = enabled;
            }

            if (missing.Count > 0)
            {
                throw new ArgumentException(
                    "Scene(s) not found: " + string.Join(", ", missing) + ". All scenes must exist before adding to build settings.");
            }

            var buildScenes = new EditorBuildSettingsScene[count];
            for (var i = 0; i < count; i++)
                buildScenes[i] = new EditorBuildSettingsScene(paths[i], enabledFlags[i]);

            // No Undo: EditorBuildSettings.scenes is a static property, not a field on a
            // UnityEngine.Object instance Undo.RecordObject could snapshot beforehand - see this
            // file's class doc comment.
            EditorBuildSettings.scenes = buildScenes;

            var resultEntries = JsonValue.NewArray();
            for (var i = 0; i < count; i++)
            {
                resultEntries.Add(JsonValue.NewObject()
                    .SetProperty("index", JsonValue.Integer(i))
                    .SetProperty("path", JsonValue.String(paths[i]))
                    .SetProperty("enabled", JsonValue.Bool(enabledFlags[i])));
            }

            return JsonValue.NewObject().SetProperty("scenes", resultEntries).SetProperty("count", JsonValue.Integer(count));
        }
    }
}
