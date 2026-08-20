// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;

namespace Hades.Tools
{
    /// <summary>
    /// animation.apply: the single wire command backing the app's animation_apply MCP tool
    /// (Hades.Server.Mcp.AnimationApplyTool) - the plugin-side half of Plan 10 Task 2's
    /// consolidation of AnimationCommands' four class-1 tools (assign_controller/assign_clip/
    /// create_controller/edit_controller) into one declarative batch, the SAME shape
    /// <see cref="SceneApplyCommands"/> (Plan 10 Task 1) and <see cref="MaterialApplyCommands"/>
    /// establish - see SceneApplyCommands' own doc comment for the full rationale.
    ///
    /// <para><b>Reuse, not reimplementation - with one exception.</b> assignController/assignClip/
    /// createController never call <see cref="Undo.IncrementCurrentGroup"/> themselves, so this
    /// class calls <c>AnimationCommands.AssignController</c>/<c>AssignClip</c>/<c>CreateController</c>
    /// directly, mid-batch, with no risk of splitting the batch's own single group.
    /// editController is the exception: <c>AnimationCommands.EditController</c> opens ITS OWN group
    /// (a real batch tool in its own right, editing several parameters/states/transitions in one
    /// call) - calling it directly mid-batch would split animation_apply's group exactly the way
    /// calling scene.setup mid-batch would have for scene_apply (see SceneApplyCommands' own doc
    /// comment). <see cref="AnimationCommands.DoEditController"/> is the identical core logic minus
    /// that one increment, added in this same Plan 10 Task 2 change specifically so this class has a
    /// safe entry point - see AnimationCommands.EditController's own doc comment.</para>
    ///
    /// <para><b>Field names: 'controllerPath' normalized across all four ops.</b> The four
    /// underlying wire commands spell "which AnimatorController" two different ways -
    /// animation.assign_controller/assign_clip call it 'controllerPath', animation.create_controller/
    /// edit_controller call it 'path' - a pre-existing inconsistency in the wire vocabulary itself,
    /// not something this task introduces. Unlike prefab_apply/material_apply (where every op's
    /// fields already matched their underlying command verbatim, so no adapter was needed),
    /// animation_apply's op vocabulary uses 'controllerPath' for all FOUR ops - one field name for
    /// "which controller" regardless of which op asks - and this class's own <see cref="DoCreateController"/>/
    /// <see cref="DoEditController"/> adapters rename it back to 'path' before calling the
    /// underlying command. This is a deliberate ergonomic choice (see Hades.Server.Mcp.
    /// AnimationApplyTool's own doc comment for the fuller rationale): consolidation exists to make
    /// the tool surface easier for an agent to use correctly, and a single caller-facing name for
    /// one concept is easier to use correctly than two.</para>
    ///
    /// <para><b>Partial failure, unknown op, per-op result data:</b> identical contract to
    /// <see cref="MaterialApplyCommands"/> - see that class's own doc comment.</para>
    /// </summary>
    internal static class AnimationApplyCommands
    {
        static readonly string[] ValidOps = { "assignController", "assignClip", "createController", "editController" };

        internal static JsonValue Apply(ReloadGate gate, JsonValue @params)
        {
            var ops = JsonParams.OptionalValue(@params, "operations");
            if (ops == null || ops.Kind != JsonValueKind.Array)
                throw new ArgumentException("animation.apply requires an 'operations' array parameter.");

            var applied = JsonValue.NewArray();
            var failed = JsonValue.NewArray();
            var results = JsonValue.NewArray();

            // ONE group for the whole batch - see SceneApplyCommands.Apply's own doc comment for
            // why this collapses harmlessly with CommandTable.Dispatch's own pre-increment
            // (animation.apply is a MutatingMethods entry, like scene.apply).
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

            Undo.SetCurrentGroupName("Hades Animation Apply: " + applied.Items.Count + " of " + ops.Items.Count + " operation(s)");

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
                case "assignController":
                    return AnimationCommands.AssignController(gate, CopyFields(op, "gameObjectPath", "controllerPath"));
                case "assignClip":
                    // animation.assign_clip's own wire field is ALREADY 'controllerPath' (unlike
                    // create_controller/edit_controller's 'path') - so no rename, just the same
                    // "copy only these keys" adapter every other op uses.
                    return AnimationCommands.AssignClip(gate, CopyFields(op, "controllerPath", "stateName", "clipPath"));
                case "createController":
                    return DoCreateController(gate, op);
                case "editController":
                    return DoEditController(op);
                default:
                    throw new ArgumentException(
                        "animation_apply: unknown op '" + (opName ?? "(missing)") + "'. Valid ops: " + string.Join(", ", ValidOps) + ".");
            }
        }

        // ---------------------------------------------------------------- op: createController

        static JsonValue DoCreateController(ReloadGate gate, JsonValue op)
        {
            var adapted = ControllerPathToPath(op);
            CopyInto(op, adapted, "parameters", "states", "transitions");
            return AnimationCommands.CreateController(gate, adapted);
        }

        // ---------------------------------------------------------------- op: editController

        static JsonValue DoEditController(JsonValue op)
        {
            var adapted = ControllerPathToPath(op);
            CopyInto(op, adapted, "addParameters", "removeParameters", "addStates", "removeStates", "addTransitions", "removeTransitions");
            return AnimationCommands.DoEditController(adapted);
        }

        // ---------------------------------------------------------------------------- shared

        /// <summary>Builds a fresh params object with 'path' set from the op's 'controllerPath' -
        /// the rename animation_apply's createController/editController ops need (see this class's
        /// own doc comment for why 'controllerPath' is the caller-facing name for all four ops even
        /// though create_controller/edit_controller's underlying wire field is 'path').</summary>
        static JsonValue ControllerPathToPath(JsonValue op)
        {
            var controllerPath = JsonParams.RequireString(op, "controllerPath", "animation_apply");
            return JsonValue.NewObject().SetProperty("path", JsonValue.String(controllerPath));
        }

        /// <summary>Copies <paramref name="keys"/> from <paramref name="source"/> (an operation
        /// entry) into <paramref name="dest"/> (an already-partially-built adapted params object),
        /// when present - the same "present but null is still copied, absent is omitted" rule
        /// <see cref="CopyFields"/> uses, kept as a separate helper here because
        /// <see cref="DoCreateController"/>/<see cref="DoEditController"/> need to ADD to an object
        /// <see cref="ControllerPathToPath"/> already started, not build one from scratch.</summary>
        static void CopyInto(JsonValue source, JsonValue dest, params string[] keys)
        {
            foreach (var key in keys)
                if (source.TryGetProperty(key, out var value) && value != null)
                    dest.SetProperty(key, value);
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
