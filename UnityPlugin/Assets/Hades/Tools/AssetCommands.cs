// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.IO;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;

namespace Hades.Tools
{
    /// <summary>
    /// Class-1 asset.move (single-tick, no reload lease - see the "52 Editor tools" plan's
    /// operation-class table) alongside the class-2 asset.import / asset.set_import_settings /
    /// asset.set_clip_import_settings handlers Plan 9 Task 3 adds - one file per subject area,
    /// matching SceneCommands/ComponentCommands, rather than splitting by operation class. The
    /// class-2 trio goes through <see cref="LeaseScope.Run"/> exactly like PrefabCommands (see
    /// that class's own doc comment for the full acquire/work/release-in-finally contract and why
    /// an exception here still leaves gate.IsHeld false): importing a NEW asset (unlike moving one
    /// Unity already knows about) can trigger the asset pipeline and, if the asset is a script,
    /// compilation - real reload risk bounded by the one call. asset.move never touches the gate:
    /// renaming/moving an already-imported asset completes inside one synchronous AssetDatabase
    /// call, same reasoning as every other class-1 handler.
    ///
    /// <para><b>Plan 10 Task 4.</b> <see cref="SetImportSettings"/>/<see cref="SetClipImportSettings"/>
    /// are each split into a thin <c>LeaseScope.Run(gate, "asset.xxx", () =&gt; DoXxx(@params))</c>
    /// wrapper plus a lease-free <see cref="DoSetImportSettings"/>/<see cref="DoSetClipImportSettings"/>
    /// core - the SAME split PrefabCommands established in Plan 10 Task 2, so
    /// <see cref="ProjectSettingsApplyCommands"/> (project_settings_apply's plugin-side batch
    /// handler) can call the SAME core logic directly, inside the ONE LeaseScope.Run that wraps its
    /// WHOLE batch, rather than each op re-acquiring its own nested lease (which fails outright -
    /// see PrefabCommands' own doc comment for why).</para>
    /// </summary>
    public static class AssetCommands
    {
        // ---------------------------------------------------------------------------- asset.move

        /// <summary>No Undo here - deliberately. Unlike a scene/component mutation,
        /// <see cref="UnityEditor.Undo.RecordObject"/> snapshots an object's SERIALIZED FIELDS,
        /// and an asset's project-relative path is not one of those; it is an AssetDatabase-level
        /// mapping with no Unity Undo primitive that covers it. Filesystem-adjacent for the same
        /// reason scene.save is: the result is a fact about the project's file layout, not
        /// in-memory object state Undo could snapshot and restore.</summary>
        internal static JsonValue MoveAsset(ReloadGate gate, JsonValue @params)
        {
            var sourcePath = JsonParams.RequireString(@params, "sourcePath", "asset.move");
            var destPath = JsonParams.RequireString(@params, "destPath", "asset.move");
            // Structural check only (traversal/absolute/whitespace/length) - no existence check, so
            // the missing-parent-directory refusal AssetDatabase.MoveAsset already gives below is
            // unchanged; this just makes the traversal/length failure modes uniform with every other
            // write-path tool instead of relying on MoveAsset's own incidental checks.
            destPath = AssetPathGuard.RequireWellFormedProjectPath(destPath, "asset.move");

            if (AssetDatabase.GetMainAssetTypeAtPath(sourcePath) == null)
                throw new ArgumentException("Source asset not found at path: '" + sourcePath + "'.");

            // Uneven-validation audit: the same cycle hazard SceneCommands.ReparentGameObject's own
            // self/descendant check (F21) closes for the scene hierarchy, here for the AssetDatabase
            // path hierarchy - moving a folder to a path inside itself (or a no-op "move" onto its own
            // exact path) has no sensible outcome. Checked explicitly, before anything on disk is
            // touched or any folder created below, rather than trusting whatever AssetDatabase.MoveAsset
            // happens to do with it - the same "checked here because the underlying API has no such
            // guard promised" reasoning as every other explicit guard in this file.
            if (destPath == sourcePath || destPath.StartsWith(sourcePath + "/", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Cannot move '" + sourcePath + "' to '" + destPath + "' - the destination is the source itself "
                    + "or a descendant of it, which would create a cycle.");
            }

            AssetFolders.EnsureExists(AssetFolders.DirectoryName(destPath));

            var error = AssetDatabase.MoveAsset(sourcePath, destPath);
            if (!string.IsNullOrEmpty(error))
                throw new ArgumentException("Move failed: " + error);

            return JsonValue.NewObject()
                .SetProperty("source", JsonValue.String(sourcePath))
                .SetProperty("destination", JsonValue.String(destPath));
        }

        // --------------------------------------------------------------------------- asset.delete

        /// <summary>Seam over <see cref="AssetDatabase.MoveAssetToTrash"/> - same swappable-static-field
        /// convention as ProjectCommands.RefreshAssets/TriggerRecompile (see that class's own doc
        /// comment): a real Editor essentially never returns false from MoveAssetToTrash on demand (it
        /// takes a locked file, a VCS hold, or an exotic platform), so DESIGN-04 §5.3.1's "refuse
        /// loudly on false" branch needs this indirection to be provoked deterministically in a test,
        /// rather than being accepted as untestable. Restore to <see cref="RealMoveAssetToTrash"/> in
        /// TearDown.</summary>
        public static Func<string, bool> MoveAssetToTrash = RealMoveAssetToTrash;

        static bool RealMoveAssetToTrash(string path) => AssetDatabase.MoveAssetToTrash(path);

        /// <summary>DESIGN-04 §5.3: <see cref="AssetDatabase.MoveAssetToTrash"/>, never
        /// <see cref="AssetDatabase.DeleteAsset"/> - the whole safety design. On macOS/Windows the file
        /// lands in the OS trash/recycle bin, restorable without Hades, Unity, or version control;
        /// Linux/headless recoverability is unverified (DESIGN-04 §9 item 5 / roadmap H4 - probe P18
        /// confirmed only that batchmode-macOS MoveAssetToTrash returns true and removes the asset from
        /// the project, not that the file is Finder-restorable, which a sandboxed probe could not check).
        /// <c>DeleteAsset</c> is unrecoverable and there is no scenario here where that is the better
        /// call. Folders are refused outright (unbounded blast radius; delete the folder's contents
        /// and let Unity/git not care about the empty folder that remains). No Undo: an asset delete
        /// has never had Unity Undo coverage in this plugin (see MoveAsset's own doc comment for why
        /// a path/file-existence fact is not something <see cref="Undo.RecordObject"/> can snapshot),
        /// and moving to trash is the recoverability story instead.
        ///
        /// <para><b>The <c>false</c> path, handled loudly.</b> <c>MoveAssetToTrash</c> returns
        /// <c>bool</c>; a locked file, a VCS integration holding the asset, or an exotic platform can
        /// all make it return <c>false</c> having moved nothing. Treating that as success would report
        /// a deletion that never happened - the worst available outcome for a destructive op - so a
        /// <c>false</c> return throws here, landing in the batch's own 'failed' entry (see
        /// AssetManageCommands' own per-op try/catch), never in 'applied'.</para></summary>
        internal static JsonValue DeleteAsset(JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "asset.delete");
            path = AssetPathGuard.RequireWellFormedProjectPath(path, "asset.delete");

            if (AssetDatabase.IsValidFolder(path))
            {
                throw new ArgumentException(
                    "Cannot delete '" + path + "' - it is a folder. asset_manage's delete op only removes individual "
                    + "assets; delete its contents instead (an empty folder left behind is not tracked by git and "
                    + "is not meaningfully preserved by Unity).");
            }

            if (AssetDatabase.GetMainAssetTypeAtPath(path) == null)
                throw new ArgumentException("Asset not found at path: '" + path + "'.");

            if (!MoveAssetToTrash(path))
            {
                throw new ArgumentException(
                    "Failed to move '" + path + "' to the trash - AssetDatabase.MoveAssetToTrash returned false. "
                    + "The file may be locked, held by a version-control integration, or otherwise unwritable. "
                    + "Nothing was deleted.");
            }

            return JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(path))
                .SetProperty("movedToTrash", JsonValue.Bool(true));
        }

        // ------------------------------------------------------------------------ asset.duplicate

        /// <summary>Content-agnostic copy, unlike material.duplicate/scene.duplicate (which each
        /// type-check their own source first) - see DESIGN-04 §2.1/§2.2's create-lives-with-the-model,
        /// lifecycle-lives-with-the-path rule: a copy neither knows nor cares what the file is.
        /// <see cref="Undo.RegisterCreatedObjectUndo"/> on the copy - same primitive, same
        /// "object destroyed on undo, file remains on disk" caveat as scriptableObject.apply's
        /// create (DESIGN-04 §5.2/§6.2's residual [VERIFY]: CopyAsset's own .meta timing was not the
        /// probe's subject, so this is not assumed to share create's clean graph-coherence story).</summary>
        internal static JsonValue DuplicateAsset(JsonValue @params)
        {
            var sourcePath = JsonParams.RequireString(@params, "sourcePath", "asset.duplicate");
            var destPath = JsonParams.RequireString(@params, "destPath", "asset.duplicate");
            destPath = AssetPathGuard.RequireNewAssetPath(destPath, "asset.duplicate", "Asset", "asset_manage's move/delete operations");

            if (AssetDatabase.GetMainAssetTypeAtPath(sourcePath) == null)
                throw new ArgumentException("Source asset not found at path: '" + sourcePath + "'.");

            AssetFolders.EnsureExists(AssetFolders.DirectoryName(destPath));

            if (!AssetDatabase.CopyAsset(sourcePath, destPath))
                throw new ArgumentException("Failed to copy asset from '" + sourcePath + "' to '" + destPath + "'.");

            var duplicated = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(destPath);
            if (duplicated != null) Undo.RegisterCreatedObjectUndo(duplicated, "Hades Duplicate Asset");

            return JsonValue.NewObject()
                .SetProperty("source", JsonValue.String(sourcePath))
                .SetProperty("destination", JsonValue.String(destPath))
                .SetProperty("guid", JsonValue.String(AssetDatabase.AssetPathToGUID(destPath)));
        }

        // -------------------------------------------------------------------------- asset.import

        /// <summary>(Re)imports a file/folder that already exists on disk under Assets/ - this is
        /// NOT how a new asset is created (there is no content to write here, unlike
        /// material.create); it is how a file dropped onto disk by some OTHER process (a build
        /// step, git checkout, an external tool) gets pulled into Unity's AssetDatabase. Verified
        /// on disk BEFORE calling ImportAsset (which returns void - no error signal of its own) and
        /// verified again after via AssetPathToGUID, so a path Unity still does not recognise post-
        /// import is reported plainly rather than silently returning a hollow "success".</summary>
        internal static JsonValue ImportAsset(ReloadGate gate, JsonValue @params) =>
            LeaseScope.Run(gate, "asset.import", () => DoImportAsset(@params));

        /// <summary>Lease-free core - see <see cref="DoSetImportSettings"/>'s own "Plan 10 Task 4"
        /// doc comment for the identical split this mirrors. Added in Plan 10 Task 5 so
        /// <see cref="AssetManageCommands"/>' own "import" op can call this directly inside its ONE
        /// whole-batch <see cref="LeaseScope.Run"/>, exactly as <see cref="ProjectSettingsApplyCommands"/>
        /// already does for <see cref="DoSetImportSettings"/>/<see cref="DoSetClipImportSettings"/>.</summary>
        internal static JsonValue DoImportAsset(JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "asset.import");
            // Uneven-validation audit: this method's own existence check below (File.Exists/
            // Directory.Exists against ToAbsolutePath) is a RAW filesystem check, not confined to
            // 'Assets/' the way AssetDatabase's own path space is - unlike asset.move's sourcePath
            // (an AssetDatabase lookup, safe by construction), a traversal path here reaches real
            // File.Exists/Directory.Exists calls before anything refuses it. Structural check only
            // (traversal/absolute/whitespace/length) - existence is still this method's own concern
            // immediately below, since asset.import legitimately requires the target to ALREADY exist.
            path = AssetPathGuard.RequireWellFormedProjectPath(path, "asset.import");
            var forceUpdate = JsonParams.OptionalBool(@params, "forceUpdate", false);
            var recursive = JsonParams.OptionalBool(@params, "recursive", false);

            var absolutePath = ToAbsolutePath(path);
            if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
            {
                throw new ArgumentException(
                    "Nothing exists on disk at '" + path + "' to import. asset.import re-imports a file/folder "
                    + "already inside 'Assets/' - use material.create (or a similar create tool) to create a new asset instead.");
            }

            var options = ImportAssetOptions.Default;
            if (forceUpdate) options |= ImportAssetOptions.ForceUpdate;
            if (recursive) options |= ImportAssetOptions.ImportRecursive;

            AssetDatabase.ImportAsset(path, options);

            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                throw new ArgumentException(
                    "Import completed but '" + path + "' is still not recognized by the AssetDatabase - check the Unity console for import errors.");
            }

            var mainType = AssetDatabase.GetMainAssetTypeAtPath(path);

            return JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(path))
                .SetProperty("guid", JsonValue.String(guid))
                .SetProperty("type", JsonValue.String(mainType != null ? mainType.Name : "Unknown"));
        }

        // ------------------------------------------------------------------- asset.set_import_settings

        internal static JsonValue SetImportSettings(ReloadGate gate, JsonValue @params) =>
            LeaseScope.Run(gate, "asset.set_import_settings", () => DoSetImportSettings(@params));

        /// <summary>Lease-free core - see <see cref="ProjectSettingsApplyCommands"/>' own doc
        /// comment ("Plan 10 Task 4"), the SAME split <see cref="PrefabCommands"/> established in
        /// Plan 10 Task 2 (parameter parsing now happens INSIDE this core rather than outside, for
        /// the identical reason PrefabCommands' own doc comment gives). Generic over any importer
        /// (Texture/Model/Audio/...): reads/writes through SerializedObject exactly like
        /// component.set_property does for a component, reusing the SAME SerializedPropertyJson
        /// helper (ComponentCommands.cs) - an importer's serialized fields are ordinary
        /// SerializedProperty values with no importer-specific handling needed.
        /// ApplyModifiedPropertiesWithoutUndo, not ApplyModifiedProperties: import settings are
        /// project configuration, not scene/asset-instance state Unity's Undo stack meaningfully
        /// covers - same reasoning TagLayerCommands documents for ProjectSettings-backed values, and
        /// the old package's own AssetImportTools.SetImportSettings already made this same
        /// choice.</summary>
        internal static JsonValue DoSetImportSettings(JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "asset.set_import_settings");
            // Uneven-validation audit: same structural guard asset.import's own path now gets - see
            // DoImportAsset's own doc comment. AssetImporter.GetAtPath below likely already fails
            // closed (returns null) for a traversal path, but this makes the failure mode a uniform,
            // actionable AssetPathGuard message instead of depending on that incidentally.
            path = AssetPathGuard.RequireWellFormedProjectPath(path, "asset.set_import_settings");
            var properties = JsonParams.OptionalValue(@params, "properties");
            if (properties == null || properties.Kind != JsonValueKind.Object || properties.Members.Count == 0)
                throw new ArgumentException("asset.set_import_settings requires a non-empty 'properties' object parameter.");

            var importer = AssetImporter.GetAtPath(path)
                ?? throw new ArgumentException("No importer found for '" + path + "'. The asset may not exist or has no configurable import settings.");

            var so = new SerializedObject(importer);
            var applied = JsonValue.NewArray();
            var failed = JsonValue.NewArray();

            foreach (var member in properties.Members)
            {
                var resolved = SerializedPropertyJson.ResolvePropertyName(so, member.Key, out var resolveErr);
                if (resolved == null)
                {
                    failed.Add(JsonValue.NewObject().SetProperty("property", JsonValue.String(member.Key)).SetProperty("error", JsonValue.String(resolveErr)));
                    continue;
                }

                try
                {
                    SerializedPropertyJson.Set(so.FindProperty(resolved), member.Value);
                    applied.Add(JsonValue.String(member.Key));
                }
                catch (Exception ex)
                {
                    failed.Add(JsonValue.NewObject().SetProperty("property", JsonValue.String(member.Key)).SetProperty("error", JsonValue.String(ex.Message)));
                }
            }

            if (applied.Items.Count > 0)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                importer.SaveAndReimport();
            }

            return JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(path))
                .SetProperty("applied", applied)
                .SetProperty("failed", failed);
        }

        // --------------------------------------------------------------- asset.set_clip_import_settings

        internal static JsonValue SetClipImportSettings(ReloadGate gate, JsonValue @params) =>
            LeaseScope.Run(gate, "asset.set_clip_import_settings", () => DoSetClipImportSettings(@params));

        /// <summary>Lease-free core - see this class's own "Plan 10 Task 4" note on
        /// <see cref="DoSetImportSettings"/>. Configures loopTime/loopPose/cycleOffset/first-last
        /// frame on named clips inside an FBX/model's ModelImporter.clipAnimations - port of the old
        /// package's AssetImportTools.SetClipImportSettings, re-targeted onto JsonValue.
        ///
        /// <para><b>MutationToolValidation.md Table 6/Table 8 gap #4 residue, closed.</b> A per-clip
        /// name that fails to resolve (or is missing) used to be recorded as a bare string in an
        /// 'errors' array, with a clip that DID apply named in 'updatedClips' - two key names found
        /// nowhere else in this codebase, diverging for no reason from <see cref="DoSetImportSettings"/>'s
        /// own per-property 'applied'/'failed' pair one method up, for the IDENTICAL shape of problem
        /// (one op's own list of named sub-outcomes - property names there, clip names here). Now
        /// converged onto that same applied/failed vocabulary: 'failed' entries are structured
        /// <c>{clip, error}</c> objects (matching <see cref="DoSetImportSettings"/>'s own
        /// <c>{property, error}</c> and <see cref="ComponentCommands"/>'s per-property applied/failed
        /// pair), not a bare string a caller had to parse to learn which clip. <c>clip</c> is JSON
        /// null (never omitted) when an entry has no name to report - the same "identifier present
        /// but null, never dropped" convention <see cref="ProjectSettingsApplyCommands"/>'s own
        /// per-op 'failed' entries and <see cref="ComponentCommands.TopLevelError"/> already use.
        /// </para></summary>
        internal static JsonValue DoSetClipImportSettings(JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "asset.set_clip_import_settings");
            // Uneven-validation audit: same structural guard as DoImportAsset/DoSetImportSettings -
            // see DoImportAsset's own doc comment.
            path = AssetPathGuard.RequireWellFormedProjectPath(path, "asset.set_clip_import_settings");
            var clipsParam = JsonParams.OptionalValue(@params, "clips");
            if (clipsParam == null || clipsParam.Kind != JsonValueKind.Array || clipsParam.Items.Count == 0)
                throw new ArgumentException("asset.set_clip_import_settings requires a non-empty 'clips' array parameter.");

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                var anyImporter = AssetImporter.GetAtPath(path);
                if (anyImporter == null) throw new ArgumentException("Asset not found at '" + path + "'.");
                throw new ArgumentException(
                    "Asset at '" + path + "' uses " + anyImporter.GetType().Name + ", not ModelImporter. This tool only works on model/FBX files.");
            }

            var existingClips = importer.clipAnimations;
            if (existingClips == null || existingClips.Length == 0) existingClips = importer.defaultClipAnimations;
            if (existingClips == null || existingClips.Length == 0)
            {
                throw new ArgumentException(
                    "No animation clips found in '" + path + "'. Ensure the model has animations and the rig type is Humanoid or Generic.");
            }

            var clipNames = existingClips.Select(c => c.name).ToArray();
            var applied = JsonValue.NewArray();
            var failed = JsonValue.NewArray();

            foreach (var config in clipsParam.Items)
            {
                var name = config != null ? JsonParams.OptionalString(config, "name") : null;
                if (string.IsNullOrEmpty(name))
                {
                    failed.Add(JsonValue.NewObject()
                        .SetProperty("clip", JsonValue.Null)
                        .SetProperty("error", JsonValue.String("A clip entry is missing its 'name'.")));
                    continue;
                }

                var index = Array.FindIndex(existingClips, c => c.name == name);
                if (index < 0)
                {
                    failed.Add(JsonValue.NewObject()
                        .SetProperty("clip", JsonValue.String(name))
                        .SetProperty("error", JsonValue.String("Clip '" + name + "' not found. Available clips: " + string.Join(", ", clipNames) + ".")));
                    continue;
                }

                ApplyClipConfig(existingClips[index], config);
                applied.Add(JsonValue.String(name));
            }

            if (applied.Items.Count > 0)
            {
                importer.clipAnimations = existingClips;
                importer.SaveAndReimport();
            }

            return JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(path))
                .SetProperty("applied", applied)
                .SetProperty("failed", failed);
        }

        static void ApplyClipConfig(ModelImporterClipAnimation clip, JsonValue config)
        {
            var loopTime = JsonParams.OptionalValue(config, "loopTime");
            if (loopTime != null && loopTime.Kind == JsonValueKind.Boolean) clip.loopTime = loopTime.AsBoolean();

            var loopPose = JsonParams.OptionalValue(config, "loopPose");
            if (loopPose != null && loopPose.Kind == JsonValueKind.Boolean) clip.loopPose = loopPose.AsBoolean();

            var cycleOffset = JsonParams.OptionalValue(config, "cycleOffset");
            if (cycleOffset != null && (cycleOffset.Kind == JsonValueKind.Float || cycleOffset.Kind == JsonValueKind.Integer))
                clip.cycleOffset = (float)cycleOffset.AsDouble();

            var firstFrame = JsonParams.OptionalValue(config, "firstFrame");
            if (firstFrame != null && (firstFrame.Kind == JsonValueKind.Float || firstFrame.Kind == JsonValueKind.Integer))
                clip.firstFrame = (float)firstFrame.AsDouble();

            var lastFrame = JsonParams.OptionalValue(config, "lastFrame");
            if (lastFrame != null && (lastFrame.Kind == JsonValueKind.Float || lastFrame.Kind == JsonValueKind.Integer))
                clip.lastFrame = (float)lastFrame.AsDouble();
        }

        /// <summary>Project-relative "Assets/..." path to an absolute filesystem path, the same
        /// projectRoot-from-Application.dataPath convention HadesBoot.BuildHello uses.</summary>
        static string ToAbsolutePath(string projectRelativePath)
        {
            var projectRoot = Directory.GetParent(UnityEngine.Application.dataPath).FullName;
            return Path.Combine(projectRoot, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }
    }

    /// <summary>Project-relative folder creation shared by every command that writes a NEW asset
    /// to a caller-supplied path (material.create/duplicate, animation.create_controller,
    /// scene.create/duplicate, asset.move) - port of the old package's repeated
    /// EnsureFolderExists/CreateFolderRecursive (it appeared, near-identically, in four different
    /// tool files), kept here once instead of copied a fifth and sixth time.</summary>
    internal static class AssetFolders
    {
        /// <summary>Project-relative directory containing <paramref name="assetPath"/>, with
        /// backslashes normalized to forward slashes - AssetDatabase paths are always '/'-separated
        /// regardless of OS, but a caller-supplied path is not guaranteed to already be.</summary>
        public static string DirectoryName(string assetPath)
        {
            var normalized = assetPath.Replace('\\', '/');
            var lastSlash = normalized.LastIndexOf('/');
            return lastSlash < 0 ? "" : normalized.Substring(0, lastSlash);
        }

        /// <summary>Creates <paramref name="folderPath"/> and every missing ancestor under it, if
        /// not already a valid folder. A no-op for an empty path (the asset belongs at the Assets
        /// root, which always exists) or one that already exists.</summary>
        public static void EnsureExists(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            if (AssetDatabase.IsValidFolder(folderPath)) return;

            var parent = DirectoryName(folderPath);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureExists(parent);

            var folderName = folderPath.Substring(folderPath.LastIndexOf('/') + 1);
            AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}
