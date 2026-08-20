// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;

namespace Hades.Tools
{
    /// <summary>
    /// scene.manage: the single wire command backing the app's scene_manage MCP tool
    /// (Hades.Server.Mcp.SceneManageTool) - the plugin-side half of Plan 10 Task 5's consolidation
    /// of SceneManagementCommands' scene.save/scene.create/scene.duplicate and ProjectCommands'
    /// scene.open (4 wire commands, two different files) into one declarative batch - the SAME
    /// "one call, one handler body, never re-entering CommandTable.Dispatch" shape
    /// <see cref="SceneApplyCommands"/> (Plan 10 Task 1) established, mixing lease classes the way
    /// <see cref="PrefabApplyCommands"/>/<see cref="ProjectSettingsApplyCommands"/>/
    /// <see cref="AssetManageCommands"/> (Plan 10 Tasks 2, 4 and 5) already do.
    ///
    /// <para><b>Mixed lease classes - ONE lease for the whole batch regardless.</b> "save"/"create"/
    /// "duplicate" are class-1 (SceneManagementCommands never touches the gate); "open" is class-2
    /// (<see cref="ProjectCommands.OpenScene"/> normally wraps it in its own
    /// <see cref="LeaseScope.Run"/>). Calling "open"'s normal, self-leasing entry point inside this
    /// batch's loop would be both wasteful and unsafe for the same reason PrefabApplyCommands' own
    /// doc comment gives, so this class wraps the ENTIRE loop in exactly ONE LeaseScope.Run call:
    /// "save"/"create"/"duplicate" go through SceneManagementCommands unchanged (none ever touch the
    /// gate regardless of whether one is already held), and "open" calls the lease-FREE
    /// <see cref="ProjectCommands.DoOpenScene"/> core directly (added in this same Plan 10 Task 5
    /// change, the identical split AssetCommands.DoImportAsset/DoSetImportSettings/
    /// DoSetClipImportSettings already established). One Lock, one Unlock, regardless of how many
    /// operations the batch contains or which of the four ops each one is.</para>
    ///
    /// <para><b>"create" keeps NOT switching the active scene - unchanged, reused verbatim.</b> This
    /// op calls <see cref="SceneManagementCommands.CreateScene"/> directly, completely unmodified:
    /// that method already builds the new scene additively and closes it again
    /// (<c>NewSceneMode.Additive</c>, never <c>NewSceneMode.Single</c>) precisely so creating a new
    /// scene ASSET never discards whatever the caller currently has open (and possibly unsaved) in
    /// the Editor - Plan 9's own E2E found the Single-mode version of this bug the hard way, silently
    /// discarding a caller's unsaved scene with no prompt in a scripted context. Routing scene_manage's
    /// "create" op through the SAME method (rather than a parallel reimplementation) is what makes
    /// this property hold by construction rather than by discipline - see Hades.Server.Mcp.
    /// SceneManageTool's own doc comment, which says this plainly in its tool description too.</para>
    ///
    /// <para><b>Undo: self-managed, not a CommandTable.MutatingMethods entry - same reasoning as
    /// PrefabApplyCommands/ProjectSettingsApplyCommands/AssetManageCommands.</b> Because this batch
    /// already wraps itself in its own LeaseScope.Run, CommandTable.Dispatch's pre-increment is not
    /// available the same way - this class opens ONE Undo group itself, before any operation runs.
    /// Undo coverage is UNEVEN across this batch's four ops, already documented per-op in
    /// SceneManagementCommands' own class doc comment: "create"/"duplicate" attempt
    /// <see cref="Undo.RegisterCreatedObjectUndo"/> on the new SceneAsset; "save" (a pure filesystem
    /// write) and "open" (a live-Editor state change, not a serialized field) have none at all. The
    /// group still opens uniformly for every op, since "does this specific op happen to be
    /// Undo-tracked" is exactly the kind of case-by-case reasoning a caller of a BATCH tool should
    /// never have to do (PrefabApplyCommands' own phrasing) - but a caller must not assume a single
    /// Ctrl/Cmd+Z reliably reverts a scene_manage batch the way it does scene_apply's.</para>
    ///
    /// <para><b>Partial failure, unknown op, per-op result data.</b> Identical contract to
    /// <see cref="PrefabApplyCommands"/>/<see cref="ProjectSettingsApplyCommands"/>/
    /// <see cref="AssetManageCommands"/>: each operation's outcome is recorded by index in
    /// 'applied'/'failed', an unrecognised 'op' is this ONE operation's failure (the app already
    /// refuses it for the whole call before any wire round trip - see Hades.Server.Mcp.
    /// SceneManageTool.ValidOps - this is a defensive fallback for a non-app caller), and every
    /// successful operation's own result JsonValue rides along unchanged in a 'results' array entry.
    /// </para>
    /// </summary>
    internal static class SceneManageCommands
    {
        static readonly string[] ValidOps = { "save", "create", "open", "duplicate" };

        internal static JsonValue Apply(ReloadGate gate, JsonValue @params)
        {
            var ops = JsonParams.OptionalValue(@params, "operations");
            if (ops == null || ops.Kind != JsonValueKind.Array)
                throw new ArgumentException("scene.manage requires an 'operations' array parameter.");

            // ONE lease for the WHOLE batch - see this class's own doc comment for why calling
            // "open"'s normal, self-leasing entry point per-op would be both wasteful and unsafe.
            return LeaseScope.Run(gate, "scene.manage", () =>
            {
                // ONE group for the whole batch, opened right after the lease and before any
                // operation runs - see this class's own doc comment for why scene.manage is not a
                // CommandTable.MutatingMethods entry (so nothing pre-increments on its behalf the
                // way scene.apply/material.apply/animation.apply get) and self-manages instead, and
                // for why Undo coverage across this particular batch is uneven.
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

                Undo.SetCurrentGroupName("Hades Scene Manage: " + applied.Items.Count + " of " + ops.Items.Count + " operation(s)");

                return JsonValue.NewObject()
                    .SetProperty("applied", applied)
                    .SetProperty("results", results)
                    .SetProperty("failed", failed)
                    .SetProperty("summary", JsonValue.String(
                        applied.Items.Count + " applied, " + failed.Items.Count + " failed of " + ops.Items.Count + " operation(s)."));
            });
        }

        /// <summary><paramref name="gate"/> is passed through to "save"/"create"/"duplicate"
        /// unchanged (SceneManagementCommands already ignores it - see that class's own doc
        /// comment), the same pass-through MaterialApplyCommands/PrefabApplyCommands use for their
        /// own class-1 ops. "open" calls ProjectCommands' lease-FREE core directly instead - see
        /// this class's own doc comment for why calling its normal, self-leasing entry point here
        /// would be unsafe.</summary>
        static JsonValue DispatchOne(ReloadGate gate, string opName, JsonValue op)
        {
            switch (opName)
            {
                case "save":
                    return SceneManagementCommands.SaveScene(gate, CopyFields(op, "path"));
                case "create":
                    return SceneManagementCommands.CreateScene(gate, CopyFields(op, "path", "template"));
                case "open":
                    return ProjectCommands.DoOpenScene(CopyFields(op, "path", "additive"));
                case "duplicate":
                    return SceneManagementCommands.DuplicateScene(gate, CopyFields(op, "sourcePath", "destPath"));
                default:
                    throw new ArgumentException(
                        "scene_manage: unknown op '" + (opName ?? "(missing)") + "'. Valid ops: " + string.Join(", ", ValidOps) + ".");
            }
        }

        /// <summary>Builds a fresh params object carrying only <paramref name="keys"/> that are
        /// actually present on <paramref name="source"/> - see PrefabApplyCommands.CopyFields's own
        /// doc comment for the full "never pass the raw op object straight through" rationale,
        /// identical here.</summary>
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
