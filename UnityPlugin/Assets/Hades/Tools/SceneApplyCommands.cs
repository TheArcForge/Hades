// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEngine;

namespace Hades.Tools
{
    /// <summary>
    /// scene.apply: the single wire command backing the app's scene_apply MCP tool
    /// (Hades.Server.Mcp.SceneApplyTool) - the plugin-side half of Plan 10 Task 1's consolidation.
    /// Generalises the "one call, one Undo group, direct Unity Undo API calls inside a single
    /// handler body" pattern SceneCommands.SceneSetup/ComponentCommands.SetProperties/
    /// AnimationCommands.EditController already established, to the FULL op vocabulary those three
    /// (plus every other class-1 scene/component/wiring handler) individually cover: create,
    /// addComponent, removeComponent, setProperties, setReference, addListener, removeListener,
    /// delete, reparent, rename, select.
    ///
    /// <para><b>Why this exists.</b> SceneApplyTool originally composed its batch by sending ONE
    /// EditorProxy.SendCommandAsync round trip PER operation - each its own CommandTable.Dispatch
    /// call, so Dispatch's per-mutating-call Undo.IncrementCurrentGroup() (Plan 9 Task 7 Defect 3)
    /// opened a FRESH Undo group per operation, not one for the whole spec. An N-operation scene_
    /// apply call opened N Undo groups - Ctrl/Cmd+Z reverted only the most recently applied
    /// operation, forfeiting the entire point of batching. This command is the fix: the app now
    /// sends the WHOLE 'operations' array in ONE scene.apply call, and this ONE handler body
    /// applies every operation directly, never re-entering CommandTable.Dispatch, incrementing the
    /// Undo group exactly once for the whole batch - see SceneApplyCommandsTests for the "one
    /// PerformUndo reverts everything" proof, and CommandTableUndoGroupingTests for the "registered
    /// as a mutating method, but Dispatch's own pre-increment collapses harmlessly into this
    /// handler's own leading increment" proof (the identical property scene.setup/component.set_
    /// properties/animation.edit_controller already rely on).</para>
    ///
    /// <para><b>Reuse, not reimplementation.</b> Nine of the eleven ops (every one except 'create'
    /// and 'setProperties') call the EXISTING internal handler those tools already send -
    /// ComponentCommands.AddComponent/RemoveComponent/ReferenceSet/EventAddListener/
    /// EventRemoveListener, SceneCommands.DeleteGameObject/ReparentGameObject/RenameGameObject,
    /// InspectorCommands.SelectGameObject - DIRECTLY (bypassing CommandTable.Dispatch, so none of
    /// their own dispatch-level Undo grouping applies; they never increment a group themselves),
    /// through a small field-renaming adapter (this op vocabulary's 'target'/'type'/'component'/...
    /// vs those methods' own 'gameObjectPath'/'componentType'/... parameter names - see
    /// SceneApplyTool's own class doc comment, Hades.Server.Mcp, for the authoritative op-field
    /// shapes this mirrors). Every error message, path-resolution rule, and edge case (fake-null
    /// components, "which roots exist" listings, ...) therefore comes from the SAME code
    /// SceneCommandsTests/ComponentCommandsTests already exercise, not a second copy. 'create' and
    /// 'setProperties' are the two exceptions - 'create' because the only existing endpoint that
    /// supports tag/layer at creation time (scene.setup) increments its OWN Undo group internally
    /// and so is UNSAFE to call mid-batch; 'setProperties' because aggregating every failed property
    /// into ONE op-level failure (matching the app tool's own established "no partial success
    /// silently reported as success" rule) is simplest as a short dedicated loop over the same
    /// SerializedPropertyJson helper every other property mutation already uses.</para>
    ///
    /// <para><b>Partial failure, never rolled back.</b> Each operation applies in array order; a
    /// failure is caught and recorded by index in the returned 'failed' array (with a copy of the
    /// original 'op' name and the exception's Message), and processing continues with the rest of
    /// the batch - mirroring scene.setup/component.set_properties' own per-entry error-and-continue
    /// shape. An operation that partially mutated the scene before throwing (e.g. 'create' created
    /// the GameObject, then a bad 'tag' threw) is NOT rolled back either - only a WHOLE-BATCH
    /// Undo.PerformUndo (this handler's one Undo group) removes it, exactly as the app tool's own
    /// description says. An unrecognised 'op' value is likewise just THIS operation's failure, not a
    /// whole-call rejection - the app tool already refuses an unknown op for the WHOLE call before
    /// ever reaching the wire (SceneApplyTool.ValidOps), so this handler only needs to behave
    /// sensibly for the case that check does not cover, a direct/non-app caller.</para>
    /// </summary>
    public static class SceneApplyCommands
    {
        static readonly string[] ValidOps =
        {
            "create", "addComponent", "removeComponent", "setProperties", "setReference",
            "addListener", "removeListener", "delete", "reparent", "rename", "select",
        };

        static readonly string[] ValidPrimitiveTypes = Enum.GetNames(typeof(PrimitiveType));

        // ---------------------------------------------------------------- scene.apply

        internal static JsonValue Apply(ReloadGate gate, JsonValue @params)
        {
            var ops = JsonParams.OptionalValue(@params, "operations");
            if (ops == null || ops.Kind != JsonValueKind.Array)
                throw new ArgumentException("scene.apply requires an 'operations' array parameter.");

            var applied = JsonValue.NewArray();
            var failed = JsonValue.NewArray();

            // ONE group for the whole batch, incremented before ANY work - the same position
            // SceneSetup/SetProperties/EditController already use. CommandTable.Dispatch may ALSO
            // increment once before calling this handler at all (scene.apply is a registered
            // MutatingMethods entry, like those three) - with nothing recorded between that
            // increment and this one, the two collapse into one harmless empty leading group, never
            // a second REAL one; see CommandTableUndoGroupingTests for the proof.
            Undo.IncrementCurrentGroup();

            for (var i = 0; i < ops.Items.Count; i++)
            {
                var op = ops.Items[i];
                var opName = JsonParams.OptionalString(op, "op");
                try
                {
                    DispatchOne(gate, opName, op);
                    applied.Add(JsonValue.Integer(i));
                }
                catch (Exception ex)
                {
                    // Broad catch is deliberate - matches SceneApplyTool's own per-operation shape
                    // (Hades.Server.Mcp): a local field-validation problem (ArgumentException) and
                    // anything else a handler throws both become THIS operation's own failure,
                    // never an aborted batch.
                    failed.Add(JsonValue.NewObject()
                        .SetProperty("index", JsonValue.Integer(i))
                        .SetProperty("op", opName != null ? JsonValue.String(opName) : JsonValue.Null)
                        .SetProperty("error", JsonValue.String(ex.Message)));
                }
            }

            Undo.SetCurrentGroupName("Hades Scene Apply: " + applied.Items.Count + " of " + ops.Items.Count + " operation(s)");

            return JsonValue.NewObject()
                .SetProperty("applied", applied)
                .SetProperty("failed", failed)
                .SetProperty("summary", JsonValue.String(
                    applied.Items.Count + " applied, " + failed.Items.Count + " failed of " + ops.Items.Count + " operation(s)."));
        }

        static void DispatchOne(ReloadGate gate, string opName, JsonValue op)
        {
            switch (opName)
            {
                case "create": DoCreate(op); return;
                case "addComponent": DoAddComponent(gate, op); return;
                case "removeComponent": DoRemoveComponent(gate, op); return;
                case "setProperties": DoSetProperties(op); return;
                case "setReference": DoSetReference(gate, op); return;
                case "addListener": DoAddListener(gate, op); return;
                case "removeListener": DoRemoveListener(gate, op); return;
                case "delete": DoDelete(gate, op); return;
                case "reparent": DoReparent(gate, op); return;
                case "rename": DoRename(gate, op); return;
                case "select": DoSelect(gate, op); return;
                default:
                    throw new ArgumentException(
                        "scene_apply: unknown op '" + (opName ?? "(missing)") + "'. Valid ops: " + string.Join(", ", ValidOps) + ".");
            }
        }

        // ---------------------------------------------------------------- op: create

        /// <summary>The union of scene_create_gameobject/scene_create_primitive/scene_setup's own
        /// create-time fields, as ONE linear path rather than routing to those three separate wire
        /// calls (what SceneApplyTool did before this handler existed) - see this class's own doc
        /// comment for why scene.setup specifically cannot be called mid-batch. 'primitive' combined
        /// with 'tag'/'layer' is refused: neither scene_create_primitive nor scene_setup ever
        /// supported that combination (scene_setup never creates primitives; scene_create_primitive
        /// never took tag/layer), so this is not a new gap, just an honest refusal instead of
        /// silently dropping half the request.</summary>
        static void DoCreate(JsonValue op)
        {
            const string ctx = "scene_apply create";
            var name = JsonParams.RequireString(op, "name", ctx);
            var parentPath = JsonParams.OptionalString(op, "parent");
            var primitive = JsonParams.OptionalString(op, "primitive");
            var tag = JsonParams.OptionalString(op, "tag");
            var layer = JsonParams.OptionalString(op, "layer");
            var position = JsonParams.OptionalVector3(op, "position");
            var rotation = JsonParams.OptionalVector3(op, "rotation");
            var scale = JsonParams.OptionalVector3(op, "scale");

            var hasTagOrLayer = !string.IsNullOrEmpty(tag) || !string.IsNullOrEmpty(layer);

            GameObject go;
            if (!string.IsNullOrEmpty(primitive))
            {
                if (hasTagOrLayer)
                {
                    throw new ArgumentException(ctx + ": 'tag'/'layer' cannot be combined with 'primitive' - "
                        + "creating a primitive does not support setting a tag or layer at creation time. "
                        + "Omit 'primitive', or omit 'tag'/'layer'.");
                }
                if (!Enum.TryParse<PrimitiveType>(primitive, true, out var primitiveType))
                {
                    throw new ArgumentException("Invalid primitive type: '" + primitive + "'. Valid types: "
                        + string.Join(", ", ValidPrimitiveTypes) + ".");
                }
                go = GameObject.CreatePrimitive(primitiveType);
                go.name = name;
            }
            else
            {
                go = new GameObject(name);
            }

            Undo.RegisterCreatedObjectUndo(go, "Hades Create " + name);

            if (!string.IsNullOrEmpty(parentPath))
            {
                var parentGo = GameObjectPaths.FindByPath(parentPath) ?? throw GameObjectPaths.NotFoundError(parentPath);
                go.transform.SetParent(parentGo.transform);
            }
            if (position != null) go.transform.localPosition = new Vector3(position[0], position[1], position[2]);
            if (rotation != null) go.transform.localEulerAngles = new Vector3(rotation[0], rotation[1], rotation[2]);
            if (scale != null) go.transform.localScale = new Vector3(scale[0], scale[1], scale[2]);

            if (!string.IsNullOrEmpty(tag)) go.tag = tag;
            if (!string.IsNullOrEmpty(layer))
            {
                var layerIndex = LayerMask.NameToLayer(layer);
                if (layerIndex < 0) throw new ArgumentException("Layer not found: '" + layer + "'.");
                go.layer = layerIndex;
            }
        }

        // ---------------------------------------------------------------- op: addComponent / removeComponent

        static void DoAddComponent(ReloadGate gate, JsonValue op)
        {
            const string ctx = "scene_apply addComponent";
            var target = JsonParams.RequireString(op, "target", ctx);
            var type = JsonParams.RequireString(op, "type", ctx);
            ComponentCommands.AddComponent(gate, JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String(target))
                .SetProperty("componentType", JsonValue.String(type)));
        }

        static void DoRemoveComponent(ReloadGate gate, JsonValue op)
        {
            const string ctx = "scene_apply removeComponent";
            var target = JsonParams.RequireString(op, "target", ctx);
            var type = JsonParams.RequireString(op, "type", ctx);
            ComponentCommands.RemoveComponent(gate, JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String(target))
                .SetProperty("componentType", JsonValue.String(type)));
        }

        // ---------------------------------------------------------------- op: setProperties

        /// <summary>Aggregates every failed property in 'values' into ONE exception, matching
        /// SceneApplyTool's own pre-existing "no partial success within one operation is silently
        /// reported as success" rule (it used to enforce this by inspecting component.set_
        /// properties' nested 'failed' list after a wire round trip; here it is the direct result of
        /// the same SerializedPropertyJson calls ComponentCommands.ApplyProperties makes, just
        /// aggregated locally instead of returned as a nested array - this op's outcome is a single
        /// applied/failed slot, not a nested per-property one). A property that DID resolve and set
        /// is still applied (so.ApplyModifiedProperties runs if anything succeeded) even when a
        /// sibling property in the same 'values' object fails - not rolled back, only reported as
        /// part of this operation's overall failure.</summary>
        static void DoSetProperties(JsonValue op)
        {
            const string ctx = "scene_apply setProperties";
            var target = JsonParams.RequireString(op, "target", ctx);
            var componentTypeName = JsonParams.RequireString(op, "component", ctx);
            var values = JsonParams.OptionalValue(op, "values");
            if (values == null || values.Kind != JsonValueKind.Object || values.Members.Count == 0)
                throw new ArgumentException(ctx + " needs a non-empty 'values' object.");

            var go = GameObjectPaths.FindByPath(target) ?? throw GameObjectPaths.NotFoundError(target);
            var componentType = ComponentTypes.Find(componentTypeName) ?? throw ComponentTypes.NotFoundError(componentTypeName);
            var component = GameObjectPaths.RequireComponent(go, componentType, componentTypeName);

            var so = new SerializedObject(component);
            Undo.RecordObject(component, "Hades Set Properties " + componentType.Name);

            var propsSet = 0;
            var failures = new List<string>();
            foreach (var member in values.Members)
            {
                var resolved = SerializedPropertyJson.ResolvePropertyName(so, member.Key, out var resolveErr);
                if (resolved == null) { failures.Add(member.Key + ": " + resolveErr); continue; }
                try { SerializedPropertyJson.Set(so.FindProperty(resolved), member.Value); propsSet++; }
                catch (Exception ex) { failures.Add(member.Key + ": " + ex.Message); }
            }
            if (propsSet > 0) so.ApplyModifiedProperties();

            if (failures.Count > 0)
            {
                var noun = failures.Count == 1 ? "property" : "properties";
                throw new ArgumentException(failures.Count + " of " + values.Members.Count + " " + noun
                    + " failed - " + string.Join("; ", failures));
            }
        }

        // ---------------------------------------------------------------- op: setReference

        static void DoSetReference(ReloadGate gate, JsonValue op)
        {
            const string ctx = "scene_apply setReference";
            var target = JsonParams.RequireString(op, "target", ctx);
            var component = JsonParams.RequireString(op, "component", ctx);
            var property = JsonParams.RequireString(op, "property", ctx);
            var value = JsonParams.OptionalString(op, "value");
            var targetPath = JsonParams.OptionalString(op, "targetPath");
            var targetComponentType = JsonParams.OptionalString(op, "targetComponentType");

            var hasValue = !string.IsNullOrEmpty(value);
            var hasTargetPath = !string.IsNullOrEmpty(targetPath);
            if (hasValue == hasTargetPath)
            {
                throw new ArgumentException(ctx + " needs exactly one of 'value' (a project asset path) or "
                    + "'targetPath' (a scene GameObject) - got " + (hasValue ? "both." : "neither."));
            }

            var adapted = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String(target))
                .SetProperty("componentType", JsonValue.String(component))
                .SetProperty("propertyName", JsonValue.String(property));
            if (hasValue) adapted.SetProperty("targetAssetPath", JsonValue.String(value));
            else adapted.SetProperty("targetPath", JsonValue.String(targetPath));
            if (!string.IsNullOrEmpty(targetComponentType)) adapted.SetProperty("targetComponentType", JsonValue.String(targetComponentType));

            ComponentCommands.ReferenceSet(gate, adapted);
        }

        // ---------------------------------------------------------------- op: addListener / removeListener

        static void DoAddListener(ReloadGate gate, JsonValue op)
        {
            const string ctx = "scene_apply addListener";
            var target = JsonParams.RequireString(op, "target", ctx);
            var component = JsonParams.RequireString(op, "component", ctx);
            var evt = JsonParams.RequireString(op, "event", ctx);
            var targetObject = JsonParams.RequireString(op, "targetObject", ctx);
            var method = JsonParams.RequireString(op, "method", ctx);
            var argument = JsonParams.OptionalString(op, "argument");
            var argumentType = JsonParams.OptionalString(op, "argumentType");

            var adapted = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String(target))
                .SetProperty("componentType", JsonValue.String(component))
                .SetProperty("eventName", JsonValue.String(evt))
                .SetProperty("targetPath", JsonValue.String(targetObject))
                .SetProperty("targetMethod", JsonValue.String(method));
            if (argument != null) adapted.SetProperty("argument", JsonValue.String(argument));
            if (!string.IsNullOrEmpty(argumentType)) adapted.SetProperty("argumentType", JsonValue.String(argumentType));

            ComponentCommands.EventAddListener(gate, adapted);
        }

        static void DoRemoveListener(ReloadGate gate, JsonValue op)
        {
            const string ctx = "scene_apply removeListener";
            var target = JsonParams.RequireString(op, "target", ctx);
            var component = JsonParams.RequireString(op, "component", ctx);
            var evt = JsonParams.RequireString(op, "event", ctx);
            var index = JsonParams.RequireInt(op, "index", ctx);

            ComponentCommands.EventRemoveListener(gate, JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String(target))
                .SetProperty("componentType", JsonValue.String(component))
                .SetProperty("eventName", JsonValue.String(evt))
                .SetProperty("index", JsonValue.Integer(index)));
        }

        // ---------------------------------------------------------------- op: delete / reparent / rename

        static void DoDelete(ReloadGate gate, JsonValue op)
        {
            var target = JsonParams.RequireString(op, "target", "scene_apply delete");
            SceneCommands.DeleteGameObject(gate, JsonValue.NewObject().SetProperty("path", JsonValue.String(target)));
        }

        static void DoReparent(ReloadGate gate, JsonValue op)
        {
            var target = JsonParams.RequireString(op, "target", "scene_apply reparent");
            var newParent = JsonParams.OptionalString(op, "newParent");
            var adapted = JsonValue.NewObject().SetProperty("path", JsonValue.String(target));
            if (!string.IsNullOrEmpty(newParent)) adapted.SetProperty("newParent", JsonValue.String(newParent));
            SceneCommands.ReparentGameObject(gate, adapted);
        }

        static void DoRename(ReloadGate gate, JsonValue op)
        {
            const string ctx = "scene_apply rename";
            var target = JsonParams.RequireString(op, "target", ctx);
            var newName = JsonParams.RequireString(op, "newName", ctx);
            SceneCommands.RenameGameObject(gate, JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(target))
                .SetProperty("newName", JsonValue.String(newName)));
        }

        // ---------------------------------------------------------------- op: select

        static void DoSelect(ReloadGate gate, JsonValue op)
        {
            var target = JsonParams.RequireString(op, "target", "scene_apply select");
            InspectorCommands.SelectGameObject(gate, JsonValue.NewObject().SetProperty("path", JsonValue.String(target)));
        }
    }
}
