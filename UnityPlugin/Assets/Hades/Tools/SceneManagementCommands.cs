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

            // Save-AS, not create: unlike scene.create/scene.duplicate this MAY legitimately land on
            // an existing .unity file (including the currently open scene's own path) - so only
            // structural well-formedness is required here, not RequireNewAssetPath's existence
            // refusal. See AssetPathGuard.RequireWellFormedProjectPath's own doc comment.
            path = AssetPathGuard.RequireWellFormedProjectPath(path, "scene.save");
            AssetFolders.EnsureExists(AssetFolders.DirectoryName(path));
            EditorSceneManager.SaveScene(scene, path);
            return JsonValue.NewObject().SetProperty("saved", JsonValue.String(path));
        }

        // ------------------------------------------------------------------------------ scene.create

        /// <summary>F5/F6 fix: 'create' now OPENS the new scene as the single active scene - the
        /// SAME behaviour as scene_manage's own non-additive 'open' - rather than the old
        /// additive-create-then-close, which left whatever was active before this call untouched.
        /// That old choice was itself a fix for a real bug (Plan 9's own E2E: NewSceneMode.Single
        /// used to silently discard a caller's unsaved scene with no prompt), but it traded one
        /// defect for another: a caller's very next scene_apply/scene_save call landed on whatever
        /// scene happened to be active BEFORE 'create' ran, not the one just created - live-observed
        /// writing test objects straight into a real, unrelated scene on disk. <see
        /// cref="RequireNoUnsavedChanges"/> is what now protects the caller's unsaved work instead:
        /// an explicit refusal naming the dirty scene(s), not a silent "never switch" workaround.</summary>
        internal static JsonValue CreateScene(ReloadGate gate, JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "scene.create");
            path = AssetPathGuard.RequireNewAssetPath(path, "scene.create", "Scene", "scene_manage's 'open' operation");
            var template = JsonParams.OptionalString(@params, "template");
            var discardUnsaved = JsonParams.OptionalBool(@params, "discardUnsaved", false);

            RequireNoUnsavedChanges(discardUnsaved, "scene.create");

            AssetFolders.EnsureExists(AssetFolders.DirectoryName(path));

            if (!string.IsNullOrEmpty(template))
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(template) == null)
                    throw new ArgumentException("Template scene not found at path: '" + template + "'.");

                if (!AssetDatabase.CopyAsset(template, path))
                    throw new ArgumentException("Failed to copy template scene to '" + path + "'.");

                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            }
            else
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, path);
            }

            var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            if (sceneAsset != null) Undo.RegisterCreatedObjectUndo(sceneAsset, "Hades Create Scene");

            return JsonValue.NewObject()
                .SetProperty("created", JsonValue.String(path))
                .SetProperty("activeScene", JsonValue.String(SceneManager.GetActiveScene().path));
        }

        // -------------------------------------------------------------------- F5/F6 shared dirty-scene guard

        /// <summary>Refuses 'create' and non-additive 'open' when any currently LOADED scene has
        /// unsaved changes, naming every dirty one - the same "refused, not ignored" convention this
        /// codebase already uses for other silent-discard risks (OperationFieldValidator's own doc
        /// comment). Both EditorSceneManager.NewScene(..., Single)/OpenScene(..., Single) discard
        /// unsaved changes in the scene(s) they replace with NO PROMPT - the interactive File > Open
        /// Scene/New Scene menu commands would ask; the scripting API never does (F6, live-observed:
        /// a scratch scene's unsaved edits were silently gone after an unrelated 'open'). Every
        /// LOADED scene is checked, not just the active one, since an additive multi-scene setup can
        /// have several open at once and a non-additive open/create replaces ALL of them.
        /// <paramref name="discardUnsaved"/> is the caller's explicit, one-off override - omitted or
        /// false is the safe default. Additive 'open' never calls this: it unloads nothing, so there
        /// is nothing here for it to discard.</summary>
        internal static void RequireNoUnsavedChanges(bool discardUnsaved, string context)
        {
            if (discardUnsaved) return;

            var dirty = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var loaded = SceneManager.GetSceneAt(i);
                if (!loaded.isDirty) continue;
                dirty.Add(string.IsNullOrEmpty(loaded.path) ? "'" + loaded.name + "' (never saved)" : loaded.path);
            }

            if (dirty.Count == 0) return;

            throw new InvalidOperationException(
                context + ": " + dirty.Count + " loaded scene(s) have unsaved changes (" + string.Join(", ", dirty)
                + ") that this call would discard with no prompt. Call scene_manage's 'save' op first, or pass "
                + "'discardUnsaved: true' on this operation to proceed and discard them.");
        }

        // --------------------------------------------------------------------------- scene.duplicate

        internal static JsonValue DuplicateScene(ReloadGate gate, JsonValue @params)
        {
            var sourcePath = JsonParams.RequireString(@params, "sourcePath", "scene.duplicate");
            var destPath = JsonParams.RequireString(@params, "destPath", "scene.duplicate");
            destPath = AssetPathGuard.RequireNewAssetPath(destPath, "scene.duplicate", "Scene", "scene_manage's 'open' operation");

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
