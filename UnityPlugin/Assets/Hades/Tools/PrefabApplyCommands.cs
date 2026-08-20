// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;

namespace Hades.Tools
{
    /// <summary>
    /// prefab.apply: the single wire command backing the app's prefab_apply MCP tool
    /// (Hades.Server.Mcp.PrefabApplyTool) - the plugin-side half of Plan 10 Task 2's consolidation
    /// of PrefabCommands' seven class-2 tools (create/instantiate/apply_overrides/edit_property/
    /// open_editing/save_editing/create_variant) into one declarative batch. Same "one call, one
    /// handler body, never re-entering CommandTable.Dispatch" shape <see cref="SceneApplyCommands"/>
    /// (Plan 10 Task 1) established for class 1 - but PrefabCommands is class 2 (multi-tick, lease
    /// bounded by the call - see that class's own doc comment), so this class additionally has to
    /// answer a question SceneApplyCommands/MaterialApplyCommands/AnimationApplyCommands never
    /// faced: what happens to the reload lease across a WHOLE batch of lease-bounded operations.
    ///
    /// <para><b>One lease for the whole batch, not one per operation.</b> Every PrefabCommands
    /// handler normally acquires its OWN single-use lease (<see cref="LeaseScope.Run"/>, a fresh
    /// GUID-suffixed id per call) and releases it before returning. Calling
    /// <c>PrefabCommands.CreatePrefab(gate, ...)</c>/<c>InstantiatePrefab(gate, ...)</c>/etc.
    /// directly, once per operation, would therefore open and close the reload lock N times inside
    /// ONE prefab_apply call - not just inefficient, but a real violation of "one call, one reload
    /// window" (the same invariant scene_apply proved for Undo groups): between operation i's
    /// Release and operation i+1's Acquire, the gate is briefly Released, and nothing prevents a
    /// DIFFERENT lease (e.g. an unrelated BeginScriptEditing session racing in) from acquiring it in
    /// that gap, which would then make operation i+1 fail with "held by a different lease" even
    /// though the whole batch was meant to be one atomic reload-safe unit. So instead, THIS class
    /// wraps the ENTIRE loop in exactly ONE <see cref="LeaseScope.Run"/> call, and every operation
    /// calls the lease-FREE core of each PrefabCommands handler directly - <see cref="PrefabCommands.DoCreate"/>/
    /// <see cref="PrefabCommands.DoInstantiate"/>/<see cref="PrefabCommands.DoApplyOverrides"/>/
    /// <see cref="PrefabCommands.DoEditProperty"/>/<see cref="PrefabCommands.DoCreateVariant"/>,
    /// added in this same Plan 10 Task 2 change specifically so this class has a safe entry point
    /// (see PrefabCommands' own doc comment, "Plan 10 Task 2", for the full split). One Lock, one
    /// Unlock, regardless of how many operations the batch contains or how many fail.</para>
    ///
    /// <para><b>Not a MutatingMethods entry, unlike scene.apply/material.apply/animation.apply.</b>
    /// CommandTable's own MutatingMethods set deliberately excludes every class-2 method (prefab/
    /// asset/project operations "scoped by their own bounded lease... not part of Unity's
    /// interactive Undo model the way a scene GameObject/component edit is" - see CommandTable.cs's
    /// own comment) - prefab.apply stays consistent with its class-2 siblings rather than becoming
    /// the one exception, so CommandTable.Dispatch does not pre-increment the Undo group before
    /// calling it (exactly as for prefab.create/instantiate/... today). This class still opens ONE
    /// Undo group itself, immediately after acquiring the lease and before any operation runs - see
    /// <see cref="Apply"/> - so a batch containing an 'instantiate' op (the one op of the five that
    /// touches the scene, hence Unity's interactive Undo stack at all) is still revertible by a
    /// single Ctrl/Cmd+Z, the same property scene_apply/material_apply/animation_apply prove. Most
    /// of the five ops mutate an asset on disk, not part of that Undo stack at all (see
    /// CommandTable's own comment again) - the group still opens uniformly for all five, since
    /// "does this specific op happen to be Undo-tracked" is exactly the kind of case-by-case
    /// reasoning a caller of a BATCH tool should never have to do.</para>
    ///
    /// <para><b>Op vocabulary reuses PrefabCommands' own field names verbatim - no adapter.</b>
    /// Unlike scene_apply (which invented a terser vocabulary - 'target'/'type'/'component' -
    /// distinct from the commands it composes) or animation_apply (which normalizes one
    /// inconsistently-named field), every prefab_apply op field is spelled exactly as the
    /// corresponding PrefabCommands parameter already is: create's 'gameObjectPath'/'assetPath',
    /// instantiate's 'prefabPath'/'parent', applyOverrides' 'gameObjectPath', editProperty's
    /// 'prefabPath'/'componentType'/'propertyName'/'value'/'gameObjectPath', createVariant's
    /// 'basePrefabPath'/'variantPath' - see Hades.Server.Mcp.PrefabApplyTool's own doc comment for
    /// why: unlike animation's single "which controller" concept, prefab operations routinely need
    /// TWO different kinds of path at once (a scene GameObject path AND a prefab asset path), so
    /// scene_apply's single terse 'target' would be ambiguous here in a way it never was for scene
    /// operations. 'gameObjectPath' is deliberately reused across create/applyOverrides/editProperty
    /// (a scene object for the first two, an optional nested prefab-internal child for the third) -
    /// the same name because it is the same kind of value each time, and each op only ever reads the
    /// fields it declares.</para>
    ///
    /// <para><b>No 'open'/'save' ops - the footgun prefab_apply exists to remove.</b> Every
    /// editProperty op is atomic (load, edit, save, unload in one operation - the SAME code path
    /// prefab_edit_property already uses when no prefab_open_editing session is open), never leaving
    /// a prefab loaded in memory across a return to the caller. A caller wanting to change several
    /// properties on the SAME prefab simply includes several editProperty ops in one batch (each
    /// reloads and resaves - true even within one prefab_apply call - trading a little redundant
    /// disk I/O for a WORKING SET with no cross-call state to forget to close), rather than the old
    /// three-call open/edit-N-times/save protocol where forgetting the last call left a prefab
    /// genuinely stuck open. prefab_open_editing/prefab_edit_property/prefab_save_editing remain
    /// available as their own standalone tools until Plan 10 Task 6's cutover; prefab_apply's
    /// editProperty op still detects and defers to an already-open session for the SAME prefabPath
    /// if one exists (see PrefabCommands.DoEditProperty's own doc comment) rather than racing it.</para>
    ///
    /// <para><b>Partial failure, unknown op, per-op result data - including 'unappliedProperties'.</b>
    /// Identical contract to <see cref="MaterialApplyCommands"/>/<see cref="AnimationApplyCommands"/>:
    /// each operation's outcome is recorded by index in 'applied'/'failed', an unrecognised 'op' is
    /// this ONE operation's failure, and every successful operation's own result JsonValue rides
    /// along unchanged in a 'results' array entry. This is what carries Plan 9's prefab_apply_overrides
    /// finding forward: an applyOverrides op that "succeeds" (throws nothing) but leaves the prefab
    /// instance root's own default-override properties un-applied (Unity's own permanent, documented
    /// behaviour - see PrefabCommands.DoApplyOverrides' own doc comment) still reports its full
    /// 'unappliedProperties'/'note' payload in that op's 'results' entry - prefab_apply's 'applied'
    /// list alone would otherwise look like blanket success, exactly the dishonesty Plan 9 fixed.
    /// </para>
    /// </summary>
    internal static class PrefabApplyCommands
    {
        static readonly string[] ValidOps = { "create", "instantiate", "applyOverrides", "editProperty", "createVariant" };

        internal static JsonValue Apply(ReloadGate gate, JsonValue @params)
        {
            var ops = JsonParams.OptionalValue(@params, "operations");
            if (ops == null || ops.Kind != JsonValueKind.Array)
                throw new ArgumentException("prefab.apply requires an 'operations' array parameter.");

            // ONE lease for the WHOLE batch - see this class's own doc comment for why calling each
            // operation's normal, self-leasing entry point (as opposed to the lease-free DoXxx core
            // PrefabCommands now also exposes) would be both wasteful and unsafe.
            return LeaseScope.Run(gate, "prefab.apply", () =>
            {
                // ONE group for the whole batch, opened right after the lease and before any
                // operation runs - see this class's own doc comment for why prefab.apply is not a
                // CommandTable.MutatingMethods entry (so nothing pre-increments on its behalf the
                // way scene.apply/material.apply/animation.apply get) and self-manages instead.
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
                        var opResult = DispatchOne(opName, op);
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

                Undo.SetCurrentGroupName("Hades Prefab Apply: " + applied.Items.Count + " of " + ops.Items.Count + " operation(s)");

                return JsonValue.NewObject()
                    .SetProperty("applied", applied)
                    .SetProperty("results", results)
                    .SetProperty("failed", failed)
                    .SetProperty("summary", JsonValue.String(
                        applied.Items.Count + " applied, " + failed.Items.Count + " failed of " + ops.Items.Count + " operation(s)."));
            });
        }

        static JsonValue DispatchOne(string opName, JsonValue op)
        {
            switch (opName)
            {
                case "create":
                    return PrefabCommands.DoCreate(CopyFields(op, "gameObjectPath", "assetPath"));
                case "instantiate":
                    return PrefabCommands.DoInstantiate(CopyFields(op, "prefabPath", "parent"));
                case "applyOverrides":
                    return PrefabCommands.DoApplyOverrides(CopyFields(op, "gameObjectPath"));
                case "editProperty":
                    return PrefabCommands.DoEditProperty(
                        CopyFields(op, "prefabPath", "componentType", "propertyName", "value", "gameObjectPath"));
                case "createVariant":
                    return PrefabCommands.DoCreateVariant(CopyFields(op, "basePrefabPath", "variantPath"));
                default:
                    throw new ArgumentException(
                        "prefab_apply: unknown op '" + (opName ?? "(missing)") + "'. Valid ops: " + string.Join(", ", ValidOps) + ".");
            }
        }

        /// <summary>Builds a fresh params object carrying only <paramref name="keys"/> that are
        /// actually present on <paramref name="source"/> - see MaterialApplyCommands.CopyFields's
        /// own doc comment for the full "never pass the raw op object straight through" rationale,
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
