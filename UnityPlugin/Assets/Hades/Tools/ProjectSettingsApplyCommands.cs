// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;

namespace Hades.Tools
{
    /// <summary>
    /// projectSettings.apply: the single wire command backing the app's project_settings_apply MCP
    /// tool (Hades.Server.Mcp.ProjectSettingsApplyTool) - the plugin-side half of Plan 10 Task 4's
    /// consolidation of TagLayerCommands' tag.create/tag.delete/layer.create,
    /// SceneManagementCommands' scene.set_build, and AssetCommands' asset.set_import_settings/
    /// asset.set_clip_import_settings (6 wire commands, three different files) into one declarative
    /// batch - the SAME "one call, one handler body, never re-entering CommandTable.Dispatch" shape
    /// <see cref="SceneApplyCommands"/> established in Plan 10 Task 1.
    ///
    /// <para><b>Reuse, not reimplementation - across THREE files, unlike any earlier apply
    /// command.</b> Every op below calls an EXISTING TagLayerCommands/SceneManagementCommands/
    /// AssetCommands method directly, through a small field-copying adapter
    /// (<see cref="CopyFields"/>) - never a second, divergent implementation of tag/layer/
    /// build-scene/import-settings logic. scene_apply's/material_apply's/animation_apply's batches
    /// each compose ops from a SINGLE source file; this one spans three, because "project settings"
    /// is the caller-facing grouping the six replaced tools already shared (SettingsTools.cs reads
    /// all of them today), not a grouping the PLUGIN's own file layout mirrors - see this project's
    /// own "consolidation happens in the app's MCP layer" rule (Plan 10's own Scope section) for why
    /// that mismatch is fine: this class is exactly the seam where a caller-facing regrouping meets
    /// the plugin's existing per-subject-area files, unchanged.</para>
    ///
    /// <para><b>Mixed lease classes - ONE lease for the whole batch regardless.</b> createTag/
    /// deleteTag/createLayer (TagLayerCommands) and setBuildScenes (SceneManagementCommands) are
    /// class-1 (no lease - both classes' own doc comments explain why); setImportSettings/
    /// setClipImportSettings (AssetCommands) are class-2, each normally wrapped in its own
    /// <see cref="LeaseScope.Run"/>. Calling THEIR normal, self-leasing entry points per-op inside
    /// this batch's loop would acquire-and-release the reload lock once per class-2 operation - not
    /// just wasteful, but unsafe (see <see cref="PrefabApplyCommands"/>' own doc comment, Plan 10
    /// Task 2, for why a gap between per-op Release and the next op's Acquire lets an unrelated
    /// lease race in and break "one call, one reload window"). So, exactly like PrefabApplyCommands,
    /// THIS class wraps the ENTIRE loop in exactly ONE <see cref="LeaseScope.Run"/> call and calls
    /// the two class-2 ops' lease-FREE cores directly - <see cref="AssetCommands.DoSetImportSettings"/>/
    /// <see cref="AssetCommands.DoSetClipImportSettings"/> (added in this same Plan 10 Task 4 change,
    /// the identical split PrefabCommands established for its own five handlers) - while the
    /// class-1 ops are called through their normal entry points unchanged (they never touch the
    /// gate regardless of whether one is already held, so no split was needed for them). One Lock,
    /// one Unlock, regardless of how many operations the batch contains, which class each one is, or
    /// how many fail.</para>
    ///
    /// <para><b>Undo: self-managed, not a CommandTable.MutatingMethods entry - same reasoning as
    /// PrefabApplyCommands.</b> Because this batch already wraps itself in its own LeaseScope.Run
    /// (for the class-2 ops), CommandTable.Dispatch's pre-increment (used by scene.apply/
    /// material.apply/animation.apply, which are entirely class-1) is not available the same way -
    /// this class opens ONE Undo group itself, immediately after acquiring the lease and before any
    /// operation runs. Whether Undo meaningfully covers any given op is UNEVEN and already
    /// documented per underlying class, not uniform the way scene_apply's is: TagLayerCommands'
    /// create/delete calls Undo.RecordObject but is "best-effort, not a tested claim" (its own doc
    /// comment); SceneManagementCommands.SetBuildScenes and AssetCommands' two import-settings ops
    /// have NO Undo AT ALL (a static property and project configuration respectively - neither is a
    /// serialized field on a UnityEngine.Object instance Undo.RecordObject could snapshot). The
    /// group still opens uniformly for every op, since "does this specific op happen to be
    /// Undo-tracked" is exactly the kind of case-by-case reasoning a caller of a BATCH tool should
    /// never have to do (PrefabApplyCommands' own phrasing, repeated here because the same
    /// reasoning applies verbatim) - but unlike scene_apply/prefab_apply, a caller must NOT assume a
    /// single Ctrl/Cmd+Z reliably reverts this batch. Hades.Server.Mcp.ProjectSettingsApplyTool's own
    /// description says this plainly.</para>
    ///
    /// <para><b>Partial failure, unknown op, per-op result data.</b> Identical contract to
    /// <see cref="PrefabApplyCommands"/>/<see cref="MaterialApplyCommands"/>: each operation's
    /// outcome is recorded by index in 'applied'/'failed', an unrecognised 'op' is this ONE
    /// operation's failure (the app already refuses it for the whole call before any wire round trip
    /// - see ProjectSettingsApplyTool.ValidOps - this is a defensive fallback for a non-app caller,
    /// the same belt-and-suspenders shape SceneApplyTool's own doc comment describes), and every
    /// successful operation's own result JsonValue rides along unchanged in a 'results' array entry
    /// - most notably createLayer's assigned 'index', the one piece of data a caller cannot know in
    /// advance.</para>
    /// </summary>
    internal static class ProjectSettingsApplyCommands
    {
        static readonly string[] ValidOps =
            { "createTag", "deleteTag", "createLayer", "setBuildScenes", "setImportSettings", "setClipImportSettings" };

        internal static JsonValue Apply(ReloadGate gate, JsonValue @params)
        {
            var ops = JsonParams.OptionalValue(@params, "operations");
            if (ops == null || ops.Kind != JsonValueKind.Array)
                throw new ArgumentException("projectSettings.apply requires an 'operations' array parameter.");

            // ONE lease for the WHOLE batch - see this class's own doc comment for why calling
            // setImportSettings/setClipImportSettings's normal, self-leasing entry points per-op
            // would be both wasteful and unsafe.
            return LeaseScope.Run(gate, "projectSettings.apply", () =>
            {
                // ONE group for the whole batch, opened right after the lease and before any
                // operation runs - see this class's own doc comment for why projectSettings.apply
                // is not a CommandTable.MutatingMethods entry (so nothing pre-increments on its
                // behalf the way scene.apply/material.apply/animation.apply get) and self-manages
                // instead, and for why Undo coverage across this particular batch is uneven.
                Undo.IncrementCurrentGroup();

                var applied = JsonValue.NewArray();
                var failed = JsonValue.NewArray();
                var results = JsonValue.NewArray();

                for (var i = 0; i < ops.Items.Count; i++)
                {
                    var op = ops.Items[i];
                    var opName = JsonParams.OptionalString(op, "op");
                    try
                    {
                        var opResult = DispatchOne(gate, opName, op);
                        applied.Add(JsonValue.Integer(i));
                        results.Add(JsonValue.NewObject()
                            .SetProperty("index", JsonValue.Integer(i))
                            .SetProperty("op", JsonValue.String(opName))
                            .SetProperty("result", opResult));
                    }
                    catch (Exception ex)
                    {
                        failed.Add(JsonValue.NewObject()
                            .SetProperty("index", JsonValue.Integer(i))
                            .SetProperty("op", opName != null ? JsonValue.String(opName) : JsonValue.Null)
                            .SetProperty("error", JsonValue.String(ex.Message)));
                    }
                }

                Undo.SetCurrentGroupName("Hades Project Settings Apply: " + applied.Items.Count + " of " + ops.Items.Count + " operation(s)");

                // Flush to disk before returning, so a read that happens right after this call sees
                // what this call just did. createTag/deleteTag/createLayer (TagLayerCommands) already
                // call AssetDatabase.SaveAssetIfDirty on the TagManager object each one touches, but
                // verified empirically (batchmode, no scene save in between) that call does not reach
                // ProjectSettings/TagManager.asset on disk - a freshly-reloaded reference to the same
                // object, SaveAssetIfDirty'd again right here, does not either. Only
                // AssetDatabase.SaveAssets() (confirmed empirically to write the new tag to disk
                // immediately) closes the gap. Until this returns, the split is invisible to a caller
                // in the SAME session - the in-memory SerializedObject is already updated, so a
                // same-session duplicate createTag still correctly fails "already exists" - but the
                // disk-backed project_settings read tool, and any process that starts fresh before a
                // scene save or Editor quit happens to flush it incidentally, both see the mutation as
                // never having happened.
                //
                // This is the blanket save MaterialCommands' own doc comment deliberately avoids for
                // a single material edit (flushing every dirty asset in the project on every
                // material.set_property call would surprise a caller mid-WIP on unrelated assets).
                // That tradeoff does not hold the same way here: project_settings_apply is already an
                // explicit "commit these settings" call, not one a caller makes mid-iteration on WIP
                // content, and the narrower per-asset save this class could reach for instead -
                // reloading TagManager.asset and calling SaveAssetIfDirty on it directly - was the
                // first thing tried and, per the same empirical check, does not work either. Gated on
                // applied.Items.Count > 0 so a no-op or all-failed batch never touches disk.
                if (applied.Items.Count > 0) AssetDatabase.SaveAssets();

                return JsonValue.NewObject()
                    .SetProperty("applied", applied)
                    .SetProperty("results", results)
                    .SetProperty("failed", failed)
                    .SetProperty("summary", JsonValue.String(
                        applied.Items.Count + " applied, " + failed.Items.Count + " failed of " + ops.Items.Count + " operation(s)."));
            });
        }

        /// <summary><paramref name="gate"/> is passed through to the class-1 ops (createTag/
        /// deleteTag/createLayer/setBuildScenes) unchanged - each already ignores it (see
        /// TagLayerCommands/SceneManagementCommands' own doc comments), the same pass-through
        /// MaterialApplyCommands uses for MaterialCommands' five handlers. The two class-2 ops call
        /// AssetCommands' lease-FREE cores directly instead - see this class's own doc comment for
        /// why calling THEIR normal, self-leasing entry points here would be unsafe.</summary>
        static JsonValue DispatchOne(ReloadGate gate, string opName, JsonValue op)
        {
            switch (opName)
            {
                case "createTag":
                    return TagLayerCommands.CreateTag(gate, CopyFields(op, "name"));
                case "deleteTag":
                    return TagLayerCommands.DeleteTag(gate, CopyFields(op, "name"));
                case "createLayer":
                    return TagLayerCommands.CreateLayer(gate, CopyFields(op, "name", "layerIndex"));
                case "setBuildScenes":
                    return SceneManagementCommands.SetBuildScenes(gate, CopyFields(op, "scenes"));
                case "setImportSettings":
                    return AssetCommands.DoSetImportSettings(CopyFields(op, "path", "properties"));
                case "setClipImportSettings":
                    return AssetCommands.DoSetClipImportSettings(CopyFields(op, "path", "clips"));
                default:
                    throw new ArgumentException(
                        "project_settings_apply: unknown op '" + (opName ?? "(missing)") + "'. Valid ops: " + string.Join(", ", ValidOps) + ".");
            }
        }

        /// <summary>Builds a fresh params object carrying only <paramref name="keys"/> that are
        /// actually present on <paramref name="source"/> - see MaterialApplyCommands.CopyFields's/
        /// PrefabApplyCommands.CopyFields's own doc comment for the full "never pass the raw op
        /// object straight through" rationale, identical here.</summary>
        static JsonValue CopyFields(JsonValue source, params string[] keys)
        {
            var copy = JsonValue.NewObject();
            foreach (var key in keys)
                if (source.TryGetProperty(key, out var value) && value != null)
                    copy.SetProperty(key, value);
            return copy;
        }
    }
}
