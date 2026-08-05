// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;

namespace Hades.Tools
{
    /// <summary>
    /// material.apply: the single wire command backing the app's material_apply MCP tool
    /// (Hades.Server.Mcp.MaterialApplyTool) - the plugin-side half of Plan 10 Task 2's
    /// consolidation of MaterialCommands' five class-1 tools (create/set_property/assign/duplicate/
    /// swap_shader) into one declarative batch, the SAME "one call, one Undo group, one handler
    /// body, never re-entering CommandTable.Dispatch" shape <see cref="SceneApplyCommands"/>
    /// established in Plan 10 Task 1 - see that class's own doc comment for the full rationale
    /// (repeated only in summary here).
    ///
    /// <para><b>Reuse, not reimplementation.</b> None of MaterialCommands' five handlers ever call
    /// <see cref="Undo.IncrementCurrentGroup"/> themselves (each only calls
    /// <see cref="Undo.RecordObject"/>/<see cref="Undo.RegisterCreatedObjectUndo"/>, recording into
    /// whatever group is already current) - unlike AnimationCommands.EditController, NONE of the
    /// five needed splitting into a "core" + "group-opening wrapper" pair before this class could
    /// call them directly, mid-batch, with no risk of splitting the batch's own single group. Every
    /// op below calls the existing <c>MaterialCommands.Xxx(gate, ...)</c> method directly, through a
    /// small field-copying adapter (<see cref="CopyFields"/>) - never a second, divergent
    /// implementation of shader-property parsing, texture resolution, etc.</para>
    ///
    /// <para><b>Field names, verbatim.</b> Every op field below is spelled EXACTLY as
    /// MaterialCommands' own wire parameters already are (material.create's 'path'/'shader',
    /// material.set_property's 'materialPath'/'propertyName'/'value', ...) - see
    /// Hades.Server.Mcp.MaterialApplyTool's own class doc comment for the one field this
    /// deliberately does NOT rename (material.create's 'path', kept distinct from the other four
    /// ops' 'materialPath', because that is what material.create's own wire contract already
    /// calls it) and why, unlike animation_apply, no field needed normalizing across ops.</para>
    ///
    /// <para><b>Partial failure, never rolled back; unknown op refused per-op.</b> Identical
    /// contract to <see cref="SceneApplyCommands"/> - each operation's outcome is recorded by index
    /// in 'applied'/'failed', processing continues regardless, and an unrecognised 'op' is this
    /// ONE operation's failure (the app already refuses it for the whole call before any wire
    /// round trip - see MaterialApplyTool.ValidOps).</para>
    ///
    /// <para><b>Per-op result data, verbatim.</b> Unlike scene_apply (whose ops are fire-and-forget
    /// mutations with nothing useful to report back beyond success/failure), several material
    /// operations return data a caller needs even when nothing failed - most importantly
    /// material.swap_shader's 'survivedProperties'/'lostProperties' (Unity silently drops shader
    /// properties the new shader does not declare - Plan 9's own finding, carried forward here
    /// verbatim, never collapsed into a blanket "applied"). Each successful operation's own result
    /// JsonValue is therefore returned unchanged in a 'results' array entry (index/op/result), the
    /// SAME per-operation result CreateMaterial/SetProperty/AssignMaterial/DuplicateMaterial/
    /// SwapShader already produce for their standalone wire commands - never reduced to a bare
    /// index the way 'applied' is.</para>
    /// </summary>
    internal static class MaterialApplyCommands
    {
        static readonly string[] ValidOps = { "create", "setProperty", "assign", "duplicate", "swapShader" };

        internal static JsonValue Apply(ReloadGate gate, JsonValue @params)
        {
            var ops = JsonParams.OptionalValue(@params, "operations");
            if (ops == null || ops.Kind != JsonValueKind.Array)
                throw new ArgumentException("material.apply requires an 'operations' array parameter.");

            var applied = JsonValue.NewArray();
            var failed = JsonValue.NewArray();
            var results = JsonValue.NewArray();

            // ONE group for the whole batch - see SceneApplyCommands.Apply's own doc comment for
            // why this collapses harmlessly with CommandTable.Dispatch's own pre-increment
            // (material.apply is a MutatingMethods entry, like scene.apply).
            Undo.IncrementCurrentGroup();

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

            Undo.SetCurrentGroupName("Hades Material Apply: " + applied.Items.Count + " of " + ops.Items.Count + " operation(s)");

            return JsonValue.NewObject()
                .SetProperty("applied", applied)
                .SetProperty("results", results)
                .SetProperty("failed", failed)
                .SetProperty("summary", JsonValue.String(
                    applied.Items.Count + " applied, " + failed.Items.Count + " failed of " + ops.Items.Count + " operation(s)."));
        }

        static JsonValue DispatchOne(ReloadGate gate, string opName, JsonValue op)
        {
            switch (opName)
            {
                case "create":
                    return MaterialCommands.CreateMaterial(gate, CopyFields(op, "path", "shader"));
                case "setProperty":
                    return MaterialCommands.SetProperty(gate, CopyFields(op, "materialPath", "propertyName", "value"));
                case "assign":
                    return MaterialCommands.AssignMaterial(gate, CopyFields(op, "gameObjectPath", "materialPath", "slot"));
                case "duplicate":
                    return MaterialCommands.DuplicateMaterial(gate, CopyFields(op, "sourcePath", "destPath"));
                case "swapShader":
                    return MaterialCommands.SwapShader(gate, CopyFields(op, "materialPath", "shader"));
                default:
                    throw new ArgumentException(
                        "material_apply: unknown op '" + (opName ?? "(missing)") + "'. Valid ops: " + string.Join(", ", ValidOps) + ".");
            }
        }

        /// <summary>Builds a fresh params object carrying only <paramref name="keys"/> that are
        /// actually present on <paramref name="source"/> (the operation entry, which also carries
        /// 'op' and possibly OTHER ops' fields a caller mistakenly included) - the same "never pass
        /// the raw op object straight through" discipline SceneApplyCommands' own DoXxx adapters
        /// use, even where (as for every op here) the field names already match the underlying
        /// command's own parameter names verbatim. A key present but JSON-null is still copied
        /// (as an explicit null), matching "absent" and "present but null" being equivalent for
        /// every optional field the underlying JsonParams helpers read (see SceneApplyTool's own
        /// doc comment) - so there is nothing to lose either way.</summary>
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
