// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;

namespace Hades.Tools
{
    /// <summary>
    /// asset.manage: the single wire command backing the app's asset_manage MCP tool
    /// (Hades.Server.Mcp.AssetManageTool) - the plugin-side half of Plan 10 Task 5's consolidation
    /// of AssetCommands' asset.move and asset.import plus CommandTable's own "assets.refresh" (3
    /// wire commands) into one declarative batch - the SAME "one call, one handler body, never
    /// re-entering CommandTable.Dispatch" shape <see cref="SceneApplyCommands"/> (Plan 10 Task 1)
    /// established, mixing lease classes the way <see cref="PrefabApplyCommands"/>/
    /// <see cref="ProjectSettingsApplyCommands"/> (Plan 10 Tasks 2 and 4) already do.
    ///
    /// <para><b>Mixed lease classes - ONE lease for the whole batch regardless.</b> "move" is
    /// class-1 (AssetCommands.MoveAsset never touches the gate); "import" and "refresh" are class-2,
    /// each normally wrapped in its own <see cref="LeaseScope.Run"/> (AssetCommands.ImportAsset and
    /// CommandTable's own "assets.refresh" handler respectively). Calling their normal, self-leasing
    /// entry points per-op inside this batch's loop would acquire-and-release the reload lock once
    /// per class-2 operation - see <see cref="PrefabApplyCommands"/>' own doc comment for why that is
    /// both wasteful and unsafe (a gap between one op's Release and the next op's Acquire lets an
    /// unrelated lease race in). So, exactly like PrefabApplyCommands/ProjectSettingsApplyCommands,
    /// this class wraps the ENTIRE loop in exactly ONE <see cref="LeaseScope.Run"/> call: "move" goes
    /// through <see cref="AssetCommands.MoveAsset"/> unchanged (it never touches the gate regardless
    /// of whether one is already held), "import" calls the lease-FREE <see cref="AssetCommands.DoImportAsset"/>
    /// core directly (added in this same Plan 10 Task 5 change, the identical split PrefabCommands/
    /// AssetCommands' own import-settings pair already established), and "refresh" calls
    /// <c>AssetDatabase.Refresh()</c> inline (its own wire handler, CommandTable.HandleAssetsRefresh,
    /// is a one-line wrapper with nothing worth extracting a shared core for). One Lock, one Unlock,
    /// regardless of how many operations the batch contains or which of the three ops each one is.</para>
    ///
    /// <para><b>Undo: self-managed, not a CommandTable.MutatingMethods entry - same reasoning as
    /// PrefabApplyCommands/ProjectSettingsApplyCommands.</b> Because this batch already wraps itself
    /// in its own LeaseScope.Run, CommandTable.Dispatch's pre-increment (used by scene.apply/
    /// material.apply/animation.apply, all entirely class-1) is not available the same way - this
    /// class opens ONE Undo group itself, before any operation runs, for the SAME isolation reason
    /// <c>asset.move</c> alone is already a CommandTable.MutatingMethods entry today (Task 7's
    /// Defect 3: two back-to-back RPC mutations must never land in the same group) even though NONE
    /// of this batch's three operations ever have anything for <see cref="Undo.RecordObject"/> to
    /// snapshot - see AssetCommands.MoveAsset's own doc comment ("no Unity Undo primitive that covers
    /// [a path]") and CommandTable's own "assets.refresh"/asset.import comments (project-file-level
    /// operations, not serialized in-memory object state). Unlike PrefabApplyCommands/
    /// ProjectSettingsApplyCommands, no operation in THIS batch's vocabulary ever populates the
    /// group - it stays uniformly empty - but the group still opens uniformly for the same "a caller
    /// of a batch tool should never have to reason per-op about this" reason. A caller must not
    /// expect a single Ctrl/Cmd+Z to undo an asset_manage call, exactly as asset_move's own existing
    /// tool already documents.</para>
    ///
    /// <para><b>Partial failure, unknown op, per-op result data.</b> Identical contract to
    /// <see cref="PrefabApplyCommands"/>/<see cref="ProjectSettingsApplyCommands"/>: each operation's
    /// outcome is recorded by index in 'applied'/'failed', an unrecognised 'op' is this ONE
    /// operation's failure (the app already refuses it for the whole call before any wire round trip -
    /// see Hades.Server.Mcp.AssetManageTool.ValidOps - this is a defensive fallback for a non-app
    /// caller), and every successful operation's own result JsonValue rides along unchanged in a
    /// 'results' array entry.</para>
    /// </summary>
    internal static class AssetManageCommands
    {
        static readonly string[] ValidOps = { "move", "import", "refresh" };

        internal static JsonValue Apply(ReloadGate gate, JsonValue @params)
        {
            var ops = JsonParams.OptionalValue(@params, "operations");
            if (ops == null || ops.Kind != JsonValueKind.Array)
                throw new ArgumentException("asset.manage requires an 'operations' array parameter.");

            // ONE lease for the WHOLE batch - see this class's own doc comment for why calling
            // "import"/"refresh"'s normal, self-leasing entry points per-op would be both wasteful
            // and unsafe.
            return LeaseScope.Run(gate, "asset.manage", () =>
            {
                // ONE group for the whole batch, opened right after the lease and before any
                // operation runs - see this class's own doc comment for why asset.manage is not a
                // CommandTable.MutatingMethods entry (so nothing pre-increments on its behalf the
                // way scene.apply/material.apply/animation.apply get) and self-manages instead, and
                // for why this particular batch's group is uniformly empty regardless.
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

                Undo.SetCurrentGroupName("Hades Asset Manage: " + applied.Items.Count + " of " + ops.Items.Count + " operation(s)");

                return JsonValue.NewObject()
                    .SetProperty("applied", applied)
                    .SetProperty("results", results)
                    .SetProperty("failed", failed)
                    .SetProperty("summary", JsonValue.String(
                        applied.Items.Count + " applied, " + failed.Items.Count + " failed of " + ops.Items.Count + " operation(s)."));
            });
        }

        /// <summary><paramref name="gate"/> is passed through to "move" unchanged (AssetCommands.
        /// MoveAsset already ignores it - see that method's own doc comment), the same pass-through
        /// MaterialApplyCommands/PrefabApplyCommands use for their own class-1 ops. "import" calls
        /// AssetCommands' lease-FREE core directly instead - see this class's own doc comment for why
        /// calling its normal, self-leasing entry point here would be unsafe. "refresh" has no
        /// separate core to call - CommandTable.HandleAssetsRefresh is a one-line wrapper around
        /// <c>AssetDatabase.Refresh()</c>, reproduced here rather than extracted for one line.</summary>
        static JsonValue DispatchOne(ReloadGate gate, string opName, JsonValue op)
        {
            switch (opName)
            {
                case "move":
                    return AssetCommands.MoveAsset(gate, CopyFields(op, "sourcePath", "destPath"));
                case "import":
                    return AssetCommands.DoImportAsset(CopyFields(op, "path", "forceUpdate", "recursive"));
                case "refresh":
                    AssetDatabase.Refresh();
                    return JsonValue.NewObject().SetProperty("refreshed", JsonValue.Bool(true));
                default:
                    throw new ArgumentException(
                        "asset_manage: unknown op '" + (opName ?? "(missing)") + "'. Valid ops: " + string.Join(", ", ValidOps) + ".");
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
