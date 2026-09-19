// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Hades.Tools
{
    /// <summary>
    /// Class-1 (single-tick, no reload lease) animation mutations: assign a controller/clip, create
    /// an AnimatorController with parameters/states/transitions in one call, and batch-edit an
    /// existing one. Same no-lease contract as SceneCommands/ComponentCommands. Ported from the old
    /// package's feature set, but re-targeted from hand-rolled JSON strings parsed with
    /// Newtonsoft.Json onto <see cref="JsonValue"/> - this plugin has a zero-third-party-dependency
    /// rule Newtonsoft would violate.
    ///
    /// Undo per tool (not a uniform claim): animation.assign_controller/assign_clip call
    /// <see cref="Undo.RecordObject"/> on the Animator/AnimatorState actually mutated (or
    /// <see cref="Undo.AddComponent{T}"/> when an Animator had to be added first).
    /// animation.create_controller registers the whole new asset with
    /// <see cref="Undo.RegisterCreatedObjectUndo"/>, the same primitive SceneCommands/
    /// MaterialCommands/SceneManagementCommands use for a newly created object - undoing it removes
    /// the entire controller, parameters/states/transitions included, so those are not separately
    /// registered there. animation.edit_controller instead touches an EXISTING controller: it
    /// records the controller AND its state machine (parameters live directly on the controller;
    /// state/transition membership lives on the state machine, a separate serialized object one
    /// layer down) before any removal, and registers each newly ADDED state/transition individually
    /// (there is no single "whole asset" undo entry to fall back on for those, unlike create).
    /// </summary>
    public static class AnimationCommands
    {
        // --------------------------------------------------------------- animation.assign_controller

        internal static JsonValue AssignController(ReloadGate gate, JsonValue @params)
        {
            var goPath = JsonParams.RequireString(@params, "gameObjectPath", "animation.assign_controller");
            var controllerPath = JsonParams.RequireString(@params, "controllerPath", "animation.assign_controller");

            var go = GameObjectPaths.FindByPath(goPath) ?? throw GameObjectPaths.NotFoundError(goPath);
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controllerPath)
                ?? throw new ArgumentException(
                    "AnimatorController not found at '" + controllerPath + "'. Ensure the path ends with .controller and is under the Assets folder.");

            var animator = go.GetComponent<Animator>();
            var addedAnimator = false;
            if (animator == null)
            {
                animator = Undo.AddComponent<Animator>(go);
                addedAnimator = true;
            }
            else
            {
                Undo.RecordObject(animator, "Hades Assign AnimatorController");
            }

            animator.runtimeAnimatorController = controller;

            return JsonValue.NewObject()
                .SetProperty("gameObject", JsonValue.String(GameObjectPaths.GetPath(go)))
                .SetProperty("controller", JsonValue.String(controllerPath))
                .SetProperty("addedAnimator", JsonValue.Bool(addedAnimator));
        }

        // -------------------------------------------------------------------- animation.assign_clip

        internal static JsonValue AssignClip(ReloadGate gate, JsonValue @params)
        {
            var controllerPath = JsonParams.RequireString(@params, "controllerPath", "animation.assign_clip");
            var stateName = JsonParams.RequireString(@params, "stateName", "animation.assign_clip");
            var clipPath = JsonParams.RequireString(@params, "clipPath", "animation.assign_clip");
            var layerName = JsonParams.OptionalString(@params, "layer");

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath)
                ?? throw new ArgumentException("AnimatorController not found at '" + controllerPath + "'.");
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath)
                ?? throw new ArgumentException("AnimationClip not found at '" + clipPath + "'.");

            // 'layer' (DESIGN-05 Wave 1) narrows the search to one named layer; omitted keeps this
            // op's PRE-EXISTING behaviour of searching every layer (unlike createController/
            // editController, this was never hardcoded to layer 0) - every existing call keeps
            // working unchanged.
            var layers = string.IsNullOrEmpty(layerName) ? controller.layers : new[] { FindLayerByName(controller, layerName) };

            var stateNames = new List<string>();
            foreach (var layer in layers)
            {
                foreach (var childState in layer.stateMachine.states)
                {
                    stateNames.Add(childState.state.name);
                    if (childState.state.name != stateName) continue;

                    if (childState.state.motion is BlendTree)
                    {
                        throw new ArgumentException(
                            "State '" + stateName + "' uses a BlendTree, not a single clip. BlendTree editing is not supported by this tool.");
                    }

                    Undo.RecordObject(childState.state, "Hades Assign Clip " + stateName);
                    childState.state.motion = clip;
                    EditorUtility.SetDirty(controller);
                    AssetDatabase.SaveAssetIfDirty(controller);

                    return JsonValue.NewObject()
                        .SetProperty("controller", JsonValue.String(controllerPath))
                        .SetProperty("state", JsonValue.String(stateName))
                        .SetProperty("clip", JsonValue.String(clipPath));
                }
            }

            throw new ArgumentException("State '" + stateName + "' not found in controller. Available states: " + string.Join(", ", stateNames) + ".");
        }

        // --------------------------------------------------------------- animation.create_controller

        internal static JsonValue CreateController(ReloadGate gate, JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "animation.create_controller");
            if (!path.EndsWith(".controller", StringComparison.Ordinal))
                throw new ArgumentException("Path must end with '.controller'. Got: '" + path + "'.");

            path = AssetPathGuard.RequireNewAssetPath(path, "animation.create_controller", "AnimatorController", "animation_edit_controller");

            AssetFolders.EnsureExists(AssetFolders.DirectoryName(path));

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            Undo.RegisterCreatedObjectUndo(controller, "Hades Create AnimatorController");

            var errors = JsonValue.NewArray();

            var parameters = JsonParams.OptionalValue(@params, "parameters");
            if (IsNonEmptyArray(parameters))
                foreach (var p in parameters.Items) AddParameter(controller, p, errors, out _);

            var sm = controller.layers[0].stateMachine;
            var stateMap = new Dictionary<string, AnimatorState>(StringComparer.OrdinalIgnoreCase);

            // Remove Unity's default empty state, matching the old package - only the auto-created
            // placeholder, never something the caller's own 'states' array added.
            if (sm.states.Length == 1 && sm.states[0].state.name == "New State")
                sm.RemoveState(sm.states[0].state);

            var states = JsonParams.OptionalValue(@params, "states");
            if (IsNonEmptyArray(states))
            {
                var hasExplicitDefault = false;
                foreach (var s in states.Items)
                    if (JsonParams.OptionalBool(s, "isDefault", false)) { hasExplicitDefault = true; break; }

                var isFirst = true;
                foreach (var s in states.Items)
                {
                    var state = AddState(sm, stateMap, s, errors, out _);
                    if (state != null && isFirst && !hasExplicitDefault) sm.defaultState = state;
                    if (state != null) isFirst = false;
                }
            }

            var transitions = JsonParams.OptionalValue(@params, "transitions");
            if (IsNonEmptyArray(transitions))
                foreach (var t in transitions.Items) AddTransition(sm, stateMap, controller, t, errors);

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);

            var stateNames = new List<string>();
            foreach (var cs in sm.states) stateNames.Add(cs.state.name);

            return JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(path))
                .SetProperty("parameterCount", JsonValue.Integer(controller.parameters.Length))
                .SetProperty("stateCount", JsonValue.Integer(sm.states.Length))
                .SetProperty("transitionCount", JsonValue.Integer(CountTransitions(sm)))
                .SetProperty("stateNames", ToJsonStringArray(stateNames))
                .SetProperty("errors", errors);
        }

        // ----------------------------------------------------------------- animation.edit_controller

        /// <summary>Plan 10 Task 2: opens ITS OWN Undo group (see the inline
        /// Undo.IncrementCurrentGroup() call below) when dispatched standalone - the same "batch
        /// tool manages its own boundary" contract scene.setup/component.set_properties always had.
        /// <see cref="AnimationApplyCommands"/> (animation_apply's plugin-side batch handler) must
        /// NOT go through this method mid-batch - re-entering it would open a SECOND, unwanted group
        /// splitting animation_apply's own single Undo step (exactly the reason SceneApplyCommands
        /// reimplements 'create' rather than calling scene.setup mid-batch - see that class's own
        /// doc comment). <see cref="DoEditController"/> is the identical core logic MINUS that one
        /// increment, for exactly that caller.</summary>
        internal static JsonValue EditController(ReloadGate gate, JsonValue @params)
        {
            Undo.IncrementCurrentGroup();
            return DoEditController(@params);
        }

        /// <summary>The core of animation.edit_controller, without opening its own Undo group - see
        /// <see cref="EditController"/>'s own doc comment for why the split exists and who may call
        /// which. Everything below is unchanged from the pre-Plan-10-Task-2 body of EditController
        /// itself, only the leading Undo.IncrementCurrentGroup() call moved out.</summary>
        internal static JsonValue DoEditController(JsonValue @params)
        {
            var path = JsonParams.RequireString(@params, "path", "animation.edit_controller");

            var addParameters = JsonParams.OptionalValue(@params, "addParameters");
            var removeParameters = JsonParams.OptionalValue(@params, "removeParameters");
            var addStates = JsonParams.OptionalValue(@params, "addStates");
            var removeStates = JsonParams.OptionalValue(@params, "removeStates");
            var addTransitions = JsonParams.OptionalValue(@params, "addTransitions");
            var removeTransitions = JsonParams.OptionalValue(@params, "removeTransitions");

            if (!IsNonEmptyArray(addParameters) && !IsNonEmptyArray(removeParameters) && !IsNonEmptyArray(addStates)
                && !IsNonEmptyArray(removeStates) && !IsNonEmptyArray(addTransitions) && !IsNonEmptyArray(removeTransitions))
            {
                throw new ArgumentException(
                    "animation.edit_controller requires at least one of addParameters/removeParameters/addStates/removeStates/addTransitions/removeTransitions.");
            }

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path)
                ?? throw new ArgumentException("AnimatorController not found at '" + path + "'.");

            // 'layer' (DESIGN-05 Wave 1) targets addStates/addTransitions/removeStates/
            // removeTransitions at one named layer's state machine; omitted keeps layer 0 - exactly
            // today's pre-existing hardcoded behaviour, so every existing call keeps working
            // unchanged. Parameters stay controller-level regardless of 'layer' - they are not
            // per-layer in Unity's own model.
            var layerName = JsonParams.OptionalString(@params, "layer");
            var targetLayer = TargetLayer(controller, layerName);

            // Parameters live directly on the controller; state/transition membership lives on the
            // state machine, a separate serialized object one layer down - both are recorded before
            // any removal so PerformUndo can restore either kind of change.
            Undo.RecordObject(controller, "Hades Edit AnimatorController");
            var sm = targetLayer.stateMachine;
            Undo.RecordObject(sm, "Hades Edit AnimatorController");

            var errors = JsonValue.NewArray();
            var added = new List<string>();
            var removed = new List<string>();

            // Removals first, matching the old package's order.
            if (IsNonEmptyArray(removeParameters))
            {
                foreach (var nameValue in removeParameters.Items)
                {
                    var name = AsStringOrNull(nameValue);
                    if (string.IsNullOrEmpty(name)) { errors.Add(JsonValue.String("removeParameters entries must be non-empty strings.")); continue; }

                    var idx = -1;
                    for (var i = 0; i < controller.parameters.Length; i++)
                        if (controller.parameters[i].name == name) { idx = i; break; }

                    if (idx >= 0) { controller.RemoveParameter(idx); removed.Add("parameter:" + name); }
                    else errors.Add(JsonValue.String("Parameter '" + name + "' not found (skipped)."));
                }
            }

            if (IsNonEmptyArray(removeStates))
            {
                foreach (var nameValue in removeStates.Items)
                {
                    var name = AsStringOrNull(nameValue);
                    if (string.IsNullOrEmpty(name)) { errors.Add(JsonValue.String("removeStates entries must be non-empty strings.")); continue; }

                    var found = false;
                    foreach (var cs in sm.states)
                    {
                        if (cs.state.name != name) continue;
                        sm.RemoveState(cs.state);
                        removed.Add("state:" + name);
                        found = true;
                        break;
                    }
                    if (!found) errors.Add(JsonValue.String("State '" + name + "' not found (skipped)."));
                }
            }

            if (IsNonEmptyArray(removeTransitions))
            {
                foreach (var def in removeTransitions.Items)
                {
                    var from = JsonParams.OptionalString(def, "from");
                    var to = JsonParams.OptionalString(def, "to");
                    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
                    {
                        errors.Add(JsonValue.String("removeTransitions entries require 'from' and 'to'."));
                        continue;
                    }

                    var removedCount = RemoveTransitions(sm, from, to);
                    if (removedCount > 0) removed.Add("transition:" + from + "->" + to + " (x" + removedCount + ")");
                    else errors.Add(JsonValue.String("No transition from '" + from + "' to '" + to + "' found (skipped)."));
                }
            }

            // Additions.
            if (IsNonEmptyArray(addParameters))
            {
                foreach (var p in addParameters.Items)
                    if (AddParameter(controller, p, errors, out var name)) added.Add("parameter:" + name);
            }

            var stateMap = new Dictionary<string, AnimatorState>(StringComparer.OrdinalIgnoreCase);
            foreach (var cs in sm.states) stateMap[cs.state.name] = cs.state;

            if (IsNonEmptyArray(addStates))
            {
                foreach (var s in addStates.Items)
                {
                    var state = AddState(sm, stateMap, s, errors, out var name);
                    if (state != null) added.Add("state:" + name);
                }
            }

            if (IsNonEmptyArray(addTransitions))
            {
                foreach (var t in addTransitions.Items)
                {
                    var from = JsonParams.OptionalString(t, "from");
                    var to = JsonParams.OptionalString(t, "to");
                    if (AddTransition(sm, stateMap, controller, t, errors)) added.Add("transition:" + from + "->" + to);
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);
            Undo.SetCurrentGroupName("Hades Edit Controller: +" + added.Count + " -" + removed.Count);

            return JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(path))
                .SetProperty("added", ToJsonStringArray(added))
                .SetProperty("removed", ToJsonStringArray(removed))
                .SetProperty("errors", errors);
        }

        // ------------------------------------------------------------------------ animation.add_layer

        /// <summary>DESIGN-05 Wave 1: removes the "layers[0] hardcoded, no way to add a layer" wall
        /// (this class's own doc comment on <see cref="CreateController"/>/<see cref="DoEditController"/>).
        /// Undo: <see cref="Undo.RecordObject"/> the controller (so the layers-array append itself
        /// reverts) plus <see cref="Undo.RegisterCreatedObjectUndo"/> on the new state machine
        /// sub-asset <see cref="AnimatorController.AddLayer"/> creates - the same two-primitive
        /// shape <see cref="AddState"/>/<see cref="AddTransition"/> already use for their own created
        /// sub-objects.</summary>
        internal static JsonValue AddLayer(ReloadGate gate, JsonValue @params)
        {
            var controllerPath = JsonParams.RequireString(@params, "controllerPath", "animation.add_layer");
            var name = JsonParams.RequireString(@params, "name", "animation.add_layer");

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath)
                ?? throw new ArgumentException("AnimatorController not found at '" + controllerPath + "'.");

            foreach (var existing in controller.layers)
            {
                if (existing.name == name)
                    throw new ArgumentException("Layer '" + name + "' already exists in '" + controllerPath + "'.");
            }

            Undo.RecordObject(controller, "Hades Add Layer " + name);
            controller.AddLayer(name);

            // AnimatorController.layers returns a FRESH COPY on every read (see AddParameter's own
            // comment for the identical gotcha) - read once, mutate the last entry, write the whole
            // array back for it to stick.
            var layers = controller.layers;
            var index = layers.Length - 1;
            var newLayer = layers[index];
            Undo.RegisterCreatedObjectUndo(newLayer.stateMachine, "Hades Add Layer " + name);

            newLayer.defaultWeight = (float)JsonParams.OptionalDouble(@params, "weight", newLayer.defaultWeight);

            var blendingName = JsonParams.OptionalString(@params, "blending");
            if (!string.IsNullOrEmpty(blendingName))
            {
                if (!Enum.TryParse(blendingName, true, out AnimatorLayerBlendingMode blendingMode))
                {
                    throw new ArgumentException(
                        "Invalid layer blending mode '" + blendingName + "'. Valid: " + string.Join(", ", Enum.GetNames(typeof(AnimatorLayerBlendingMode))) + ".");
                }
                newLayer.blendingMode = blendingMode;
            }

            var maskPath = JsonParams.OptionalString(@params, "avatarMask");
            if (!string.IsNullOrEmpty(maskPath))
            {
                newLayer.avatarMask = AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath)
                    ?? throw new ArgumentException("AvatarMask not found at '" + maskPath + "'.");
            }

            newLayer.iKPass = JsonParams.OptionalBool(@params, "ikPass", newLayer.iKPass);

            layers[index] = newLayer;
            controller.layers = layers;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);

            return JsonValue.NewObject()
                .SetProperty("controllerPath", JsonValue.String(controllerPath))
                .SetProperty("name", JsonValue.String(name))
                .SetProperty("weight", JsonValue.Float(newLayer.defaultWeight))
                .SetProperty("blending", JsonValue.String(newLayer.blendingMode.ToString()))
                .SetProperty("ikPass", JsonValue.Bool(newLayer.iKPass));
        }

        // ------------------------------------------------------------------------- animation.set_events

        /// <summary>DESIGN-05 §5.5: replace-all, matching AnimationUtility.SetAnimationEvents' own
        /// semantics and idempotent under retry - <c>"events": []</c> clears every event. Refuses an
        /// imported (FBX sub-asset) clip outright, naming project_settings_apply
        /// setClipImportSettings - see <see cref="ThrowIfImportedClip"/>. Method-existence validation
        /// against 'receiver' is opt-in (a .anim asset does not know which GameObject will play it)
        /// and reuses <see cref="ComponentCommands.FindListenerMethod"/>/
        /// <see cref="ComponentCommands.ListAvailableMethods"/> verbatim - see those methods' own
        /// doc comment for why they are internal, not private.</summary>
        internal static JsonValue SetEvents(ReloadGate gate, JsonValue @params)
        {
            var clipPath = JsonParams.RequireString(@params, "clipPath", "animation.set_events");

            var eventsJson = JsonParams.OptionalValue(@params, "events");
            if (eventsJson == null || eventsJson.Kind != JsonValueKind.Array)
                throw new ArgumentException("animation.set_events requires an 'events' array parameter (use [] to clear every event).");

            var clip = ResolveClip(clipPath, out var clipError) ?? throw new ArgumentException(clipError);
            ThrowIfImportedClip(clipPath, "setEvents");

            var receiverPath = JsonParams.OptionalString(@params, "receiver");
            GameObject receiver = null;
            if (!string.IsNullOrEmpty(receiverPath))
                receiver = GameObjectPaths.FindByPath(receiverPath) ?? throw GameObjectPaths.NotFoundError(receiverPath);

            var errors = JsonValue.NewArray();
            var animEvents = new List<AnimationEvent>();

            for (var i = 0; i < eventsJson.Items.Count; i++)
            {
                var e = eventsJson.Items[i];
                var timeValue = JsonParams.OptionalValue(e, "time");
                var function = JsonParams.OptionalString(e, "function");
                if (timeValue == null || (timeValue.Kind != JsonValueKind.Integer && timeValue.Kind != JsonValueKind.Float) || string.IsNullOrEmpty(function))
                {
                    errors.Add(JsonValue.String("events[" + i + "]: requires 'time' (number) and 'function' (string)."));
                    continue;
                }

                var animEvent = new AnimationEvent { time = (float)timeValue.AsDouble(), functionName = function };

                var argumentCount = 0;
                var hasArgument = false;
                if (e.TryGetProperty("float", out var floatValue) && floatValue != null && floatValue.Kind != JsonValueKind.Null)
                {
                    animEvent.floatParameter = (float)RequireNumericField(floatValue, "events[" + i + "].float");
                    argumentCount++; hasArgument = true;
                }
                if (e.TryGetProperty("int", out var intValue) && intValue != null && intValue.Kind != JsonValueKind.Null)
                {
                    animEvent.intParameter = (int)RequireNumericField(intValue, "events[" + i + "].int");
                    argumentCount++; hasArgument = true;
                }
                if (e.TryGetProperty("string", out var stringValue) && stringValue != null && stringValue.Kind == JsonValueKind.String)
                {
                    animEvent.stringParameter = stringValue.AsString();
                    argumentCount++; hasArgument = true;
                }
                var objectReferencePath = JsonParams.OptionalString(e, "objectReference");
                if (!string.IsNullOrEmpty(objectReferencePath))
                {
                    animEvent.objectReferenceParameter = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(objectReferencePath)
                        ?? throw new ArgumentException("events[" + i + "]: objectReference asset not found at '" + objectReferencePath + "'.");
                    argumentCount++; hasArgument = true;
                }

                if (argumentCount > 1)
                {
                    errors.Add(JsonValue.String("events[" + i + "]: at most one of float/int/string/objectReference may be set (got " + argumentCount + ")."));
                    continue;
                }

                if (receiver != null)
                {
                    var (_, method) = ComponentCommands.FindListenerMethod(receiver, function, hasArgument);
                    if (method == null)
                    {
                        errors.Add(JsonValue.String("events[" + i + "]: function '" + function + "' not found on '" + receiverPath + "'. Available methods: "
                            + string.Join(", ", ComponentCommands.ListAvailableMethods(receiver)) + "."));
                        continue;
                    }
                }

                animEvents.Add(animEvent);
            }

            Undo.RecordObject(clip, "Hades Set Events " + clipPath);
            AnimationUtility.SetAnimationEvents(clip, animEvents.ToArray());
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssetIfDirty(clip);

            return JsonValue.NewObject()
                .SetProperty("clipPath", JsonValue.String(clipPath))
                .SetProperty("eventCount", JsonValue.Integer(animEvents.Count))
                .SetProperty("errors", errors);
        }

        // --------------------------------------------------------------------- animation.set_blend_tree

        const int MaxBlendTreeDepth = 4;

        /// <summary>Ceiling on keyframes <see cref="DescribeClip"/> will emit across all curves,
        /// resolving DESIGN-05 open question 6 (which decided the shape - a cap plus a 'truncated'
        /// flag - and left the units to Wave 3). Deliberately the same 2000 as InspectTool's own
        /// MaxValueUnits rather than a fresh number: this codebase has already calibrated that
        /// figure against the ~25,000-token single-response cap, and a second, differently-guessed
        /// ceiling for the same class of problem would be worse than reusing the settled one.</summary>
        const int MaxDescribeKeys = 2000;
        static readonly string[] BlendTreeAcceptedKeys = { "type", "parameter", "parameterX", "parameterY", "children" };
        static readonly string[] BlendTreeChildAcceptedKeys = { "clip", "tree", "threshold", "position", "directBlendParameter", "timeScale", "cycleOffset", "mirror" };

        /// <summary>DESIGN-05 §4: declarative whole-tree replace. Pre-flight validates the ENTIRE
        /// tree - every child, every nesting level - before touching state.motion at all (§4.4):
        /// on failure NOTHING is created or replaced and every problem found is reported at once,
        /// not just the first, which is what makes "re-send the corrected tree" converge cleanly
        /// rather than requiring the caller to clean up a half-built predecessor first. Atomicity
        /// here is per-op, not per-batch - the op still lands in animation.apply's own 'failed[index]'
        /// like any other op (AnimationApplyCommands.Apply's own per-op try/catch).
        ///
        /// <para>Sub-asset lifecycle (§4.7, probe-verified against Unity 6000.3.2f1): the root tree
        /// is created via <c>new BlendTree()</c> + <see cref="AssetDatabase.AddObjectToAsset"/> (the
        /// same primitive this file's own AddState/AddLayer already use for their own sub-objects -
        /// there is no "CreateBlendTreeInController for an EXISTING state" overload, since that
        /// method's own contract is create-state-and-tree together); nested trees use
        /// <see cref="BlendTree.CreateBlendTreeChild(Vector2)"/>/<c>(float)</c>, which registers its
        /// own sub-asset with no <c>AddObjectToAsset</c> call needed (VERIFIED, probe). Both get
        /// ordinary, complete <see cref="Undo.RegisterCreatedObjectUndo"/> - no partial-undo
        /// caveat (VERIFIED, probe: sub-asset creation IS undone, unlike a created .asset FILE).
        /// Replacing an existing tree additionally <see cref="Undo.DestroyObjectImmediate"/>s the
        /// superseded one so the .controller does not accumulate orphans across edits.</para>
        /// </summary>
        internal static JsonValue SetBlendTree(ReloadGate gate, JsonValue @params)
        {
            var controllerPath = JsonParams.RequireString(@params, "controllerPath", "animation.set_blend_tree");
            var stateName = JsonParams.RequireString(@params, "stateName", "animation.set_blend_tree");
            var treeJson = JsonParams.OptionalValue(@params, "tree");
            if (treeJson == null || treeJson.Kind != JsonValueKind.Object)
                throw new ArgumentException("animation.set_blend_tree requires a 'tree' object parameter.");

            var validationErrors = new List<string>();
            ValidateBlendTreeNode(treeJson, "tree", 1, validationErrors);
            if (validationErrors.Count > 0)
                throw new ArgumentException("animation.set_blend_tree: " + string.Join(" ", validationErrors));

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath)
                ?? throw new ArgumentException("AnimatorController not found at '" + controllerPath + "'.");

            var layerName = JsonParams.OptionalString(@params, "layer");
            var layer = TargetLayer(controller, layerName);
            var sm = layer.stateMachine;

            var stateMap = new Dictionary<string, AnimatorState>(StringComparer.OrdinalIgnoreCase);
            foreach (var cs in sm.states) stateMap[cs.state.name] = cs.state;

            var createdState = false;
            if (!stateMap.TryGetValue(stateName, out var state))
            {
                state = sm.AddState(stateName);
                Undo.RegisterCreatedObjectUndo(state, "Hades Set Blend Tree " + stateName);
                createdState = true;
            }

            var previousTree = state.motion as BlendTree;
            // Captured BEFORE any destroy call - UnityEngine.Object overrides == / != to report a
            // DESTROYED object as "null" even though the C# reference itself is not, so evaluating
            // "previousTree != null" again after Undo.DestroyObjectImmediate below would silently
            // (and wrongly) read false.
            var hadPreviousTree = previousTree != null;

            Undo.RecordObject(state, "Hades Set Blend Tree " + stateName);
            var newTree = new BlendTree { name = stateName + " Tree" };
            AssetDatabase.AddObjectToAsset(newTree, controller);
            Undo.RegisterCreatedObjectUndo(newTree, "Hades Set Blend Tree " + stateName);
            PopulateBlendTree(newTree, treeJson);
            state.motion = newTree;

            // Orphan-accumulation guard: cleanup of a SUPERSEDED tree is independent of - and not
            // covered by - the new tree's own undo registration above (§4.7).
            if (hadPreviousTree && previousTree != newTree)
                Undo.DestroyObjectImmediate(previousTree);

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);

            return JsonValue.NewObject()
                .SetProperty("controllerPath", JsonValue.String(controllerPath))
                .SetProperty("layer", JsonValue.String(layer.name))
                .SetProperty("stateName", JsonValue.String(stateName))
                .SetProperty("createdState", JsonValue.Bool(createdState))
                .SetProperty("replacedExistingTree", JsonValue.Bool(hadPreviousTree));
        }

        /// <summary>Walks the WHOLE tree, collecting every problem into <paramref name="errors"/>
        /// rather than throwing on the first one - see <see cref="SetBlendTree"/>'s own doc comment
        /// for why a partial answer would be worse than a slower complete one here. Touches nothing;
        /// pure inspection of the wire value.</summary>
        static void ValidateBlendTreeNode(JsonValue node, string path, int depth, List<string> errors)
        {
            if (depth > MaxBlendTreeDepth)
            {
                errors.Add(path + ": nested more than " + MaxBlendTreeDepth + " levels deep.");
                return;
            }
            if (node == null || node.Kind != JsonValueKind.Object)
            {
                errors.Add(path + ": must be an object.");
                return;
            }

            foreach (var member in node.Members)
            {
                if (Array.IndexOf(BlendTreeAcceptedKeys, member.Key) < 0)
                    errors.Add(path + ": unknown key '" + member.Key + "'. Accepted: " + string.Join(", ", BlendTreeAcceptedKeys) + ".");
            }

            var typeName = JsonParams.OptionalString(node, "type");
            var hasType = false;
            var type = default(BlendTreeType);
            if (string.IsNullOrEmpty(typeName))
                errors.Add(path + ": requires a 'type' string (one of " + string.Join(", ", Enum.GetNames(typeof(BlendTreeType))) + ").");
            else if (!Enum.TryParse(typeName, true, out type))
                errors.Add(path + ": type is '" + typeName + "', must be one of " + string.Join(", ", Enum.GetNames(typeof(BlendTreeType))) + ".");
            else
                hasType = true;

            if (hasType)
            {
                if (type == BlendTreeType.Simple1D && string.IsNullOrEmpty(JsonParams.OptionalString(node, "parameter")))
                    errors.Add(path + ": type Simple1D requires a 'parameter' string.");
                else if ((type == BlendTreeType.SimpleDirectional2D || type == BlendTreeType.FreeformDirectional2D || type == BlendTreeType.FreeformCartesian2D)
                    && (string.IsNullOrEmpty(JsonParams.OptionalString(node, "parameterX")) || string.IsNullOrEmpty(JsonParams.OptionalString(node, "parameterY"))))
                    errors.Add(path + ": type " + type + " requires 'parameterX' and 'parameterY' strings.");
            }

            var childrenJson = JsonParams.OptionalValue(node, "children");
            if (childrenJson == null || childrenJson.Kind != JsonValueKind.Array || childrenJson.Items.Count == 0)
            {
                errors.Add(path + ": requires a non-empty 'children' array.");
                return;
            }

            for (var i = 0; i < childrenJson.Items.Count; i++)
                ValidateBlendTreeChild(childrenJson.Items[i], path + ".child[" + i + "]", depth, hasType ? (BlendTreeType?)type : null, errors);
        }

        static void ValidateBlendTreeChild(JsonValue child, string path, int depth, BlendTreeType? parentType, List<string> errors)
        {
            if (child == null || child.Kind != JsonValueKind.Object)
            {
                errors.Add(path + ": must be an object.");
                return;
            }

            foreach (var member in child.Members)
            {
                if (Array.IndexOf(BlendTreeChildAcceptedKeys, member.Key) < 0)
                    errors.Add(path + ": unknown key '" + member.Key + "'. Accepted: " + string.Join(", ", BlendTreeChildAcceptedKeys) + ".");
            }

            var clipValue = JsonParams.OptionalString(child, "clip");
            var hasClip = !string.IsNullOrEmpty(clipValue);
            var treeValue = JsonParams.OptionalValue(child, "tree");
            var hasTree = treeValue != null && treeValue.Kind == JsonValueKind.Object;

            if (hasClip == hasTree)
                errors.Add(path + ": must have exactly one of 'clip' or 'tree', not " + (hasClip ? "both" : "neither") + ".");
            else if (hasClip && ResolveClip(clipValue, out var clipError) == null)
                errors.Add(path + ": clip '" + clipValue + "' " + clipError);

            if (parentType is { } type)
            {
                if (type == BlendTreeType.Simple1D)
                {
                    var threshold = JsonParams.OptionalValue(child, "threshold");
                    if (threshold == null || (threshold.Kind != JsonValueKind.Integer && threshold.Kind != JsonValueKind.Float))
                        errors.Add(path + ": type is Simple1D but 'threshold' is missing or not a number.");
                }
                else if (type == BlendTreeType.SimpleDirectional2D || type == BlendTreeType.FreeformDirectional2D || type == BlendTreeType.FreeformCartesian2D)
                {
                    var position = JsonParams.OptionalValue(child, "position");
                    if (position == null || position.Kind != JsonValueKind.Array || position.Items.Count != 2
                        || position.Items[0].Kind is not (JsonValueKind.Integer or JsonValueKind.Float)
                        || position.Items[1].Kind is not (JsonValueKind.Integer or JsonValueKind.Float))
                        errors.Add(path + ": type is " + type + " but 'position' is missing or not a 2-element numeric array [x, y].");
                }
                else if (type == BlendTreeType.Direct)
                {
                    if (string.IsNullOrEmpty(JsonParams.OptionalString(child, "directBlendParameter")))
                        errors.Add(path + ": type is Direct but 'directBlendParameter' is missing.");
                }
            }

            CheckOptionalNumberField(child, "timeScale", path, errors);
            CheckOptionalNumberField(child, "cycleOffset", path, errors);
            CheckOptionalBoolField(child, "mirror", path, errors);

            if (hasTree) ValidateBlendTreeNode(treeValue, path + ".tree", depth + 1, errors);
        }

        static void CheckOptionalNumberField(JsonValue obj, string key, string path, List<string> errors)
        {
            var value = JsonParams.OptionalValue(obj, key);
            if (value != null && value.Kind != JsonValueKind.Null && value.Kind != JsonValueKind.Integer && value.Kind != JsonValueKind.Float)
                errors.Add(path + ": '" + key + "' must be a number.");
        }

        static void CheckOptionalBoolField(JsonValue obj, string key, string path, List<string> errors)
        {
            var value = JsonParams.OptionalValue(obj, key);
            if (value != null && value.Kind != JsonValueKind.Null && value.Kind != JsonValueKind.Boolean)
                errors.Add(path + ": '" + key + "' must be a boolean.");
        }

        /// <summary>Populates an already-created (and already asset-registered) BlendTree from a
        /// validated node - called for the root and, recursively, for every nested tree. Assumes
        /// <see cref="ValidateBlendTreeNode"/> already passed cleanly for this exact node.</summary>
        static void PopulateBlendTree(BlendTree tree, JsonValue node)
        {
            Enum.TryParse(JsonParams.OptionalString(node, "type"), true, out BlendTreeType type);
            tree.blendType = type;

            switch (type)
            {
                case BlendTreeType.Simple1D:
                    tree.blendParameter = JsonParams.OptionalString(node, "parameter");

                    // F4 fix: Unity's default useAutomaticThresholds=true silently RECOMPUTES every
                    // child's threshold (evenly spaced over [0,1]) the moment a child is added,
                    // discarding whatever explicit 'threshold' AddBlendTreeChild just set - sent
                    // 0/1/3, stored 0/0.5/1, live-verified. 'threshold' is required for Simple1D
                    // (validated above this call ever runs), so an explicit value must win; set
                    // BEFORE any child is added, on every Simple1D tree this populates including a
                    // nested one (this method runs once per tree, root or nested - see this method's
                    // own doc comment).
                    tree.useAutomaticThresholds = false;
                    break;
                case BlendTreeType.SimpleDirectional2D:
                case BlendTreeType.FreeformDirectional2D:
                case BlendTreeType.FreeformCartesian2D:
                    tree.blendParameter = JsonParams.OptionalString(node, "parameterX");
                    tree.blendParameterY = JsonParams.OptionalString(node, "parameterY");
                    break;
            }

            var children = JsonParams.OptionalValue(node, "children");
            foreach (var childJson in children.Items)
                AddBlendTreeChild(tree, childJson, type);
        }

        static void AddBlendTreeChild(BlendTree tree, JsonValue childJson, BlendTreeType type)
        {
            var clipPath = JsonParams.OptionalString(childJson, "clip");
            Motion motion = !string.IsNullOrEmpty(clipPath) ? ResolveClip(clipPath, out _) : null;
            BlendTree nestedTree = null;

            switch (type)
            {
                case BlendTreeType.Simple1D:
                {
                    var threshold = (float)JsonParams.OptionalDouble(childJson, "threshold", 0);
                    if (motion != null) tree.AddChild(motion, threshold);
                    else nestedTree = tree.CreateBlendTreeChild(threshold);
                    break;
                }
                case BlendTreeType.Direct:
                {
                    if (motion != null) tree.AddChild(motion);
                    else nestedTree = tree.CreateBlendTreeChild(0f);
                    break;
                }
                default: // SimpleDirectional2D / FreeformDirectional2D / FreeformCartesian2D
                {
                    var positionJson = JsonParams.OptionalValue(childJson, "position");
                    var position = new Vector2((float)positionJson.Items[0].AsDouble(), (float)positionJson.Items[1].AsDouble());
                    if (motion != null) tree.AddChild(motion, position);
                    else nestedTree = tree.CreateBlendTreeChild(position);
                    break;
                }
            }

            if (nestedTree != null)
                PopulateBlendTree(nestedTree, JsonParams.OptionalValue(childJson, "tree"));

            // Per-child extras: none of AddChild/CreateBlendTreeChild's overloads accept timeScale/
            // cycleOffset/mirror/directBlendParameter, so read-modify-write the whole children array
            // afterward - same "fresh copy on every read" pattern as controller.parameters/layers
            // elsewhere in this file. The child just added/created is always the LAST entry.
            var children = tree.children;
            var idx = children.Length - 1;
            var cm = children[idx];
            var timeScale = JsonParams.OptionalValue(childJson, "timeScale");
            if (timeScale != null && (timeScale.Kind == JsonValueKind.Integer || timeScale.Kind == JsonValueKind.Float)) cm.timeScale = (float)timeScale.AsDouble();
            var cycleOffset = JsonParams.OptionalValue(childJson, "cycleOffset");
            if (cycleOffset != null && (cycleOffset.Kind == JsonValueKind.Integer || cycleOffset.Kind == JsonValueKind.Float)) cm.cycleOffset = (float)cycleOffset.AsDouble();
            var mirror = JsonParams.OptionalValue(childJson, "mirror");
            if (mirror != null && mirror.Kind == JsonValueKind.Boolean) cm.mirror = mirror.AsBoolean();
            if (type == BlendTreeType.Direct)
            {
                var directParam = JsonParams.OptionalString(childJson, "directBlendParameter");
                if (!string.IsNullOrEmpty(directParam)) cm.directBlendParameter = directParam;
            }
            children[idx] = cm;
            tree.children = children;
        }

        // ------------------------------------------------------------------------- animation.create_clip

        /// <summary>DESIGN-05 Wave 3 §5.1/§5.6: procedural/utility clips only - creates an EMPTY
        /// .anim asset (no curves yet; setCurve adds those). Goes through AssetPathGuard like every
        /// other create-family command (F16/F17/F20).</summary>
        internal static JsonValue CreateClip(ReloadGate gate, JsonValue @params)
        {
            var clipPath = JsonParams.RequireString(@params, "clipPath", "animation.create_clip");
            if (!clipPath.EndsWith(".anim", StringComparison.Ordinal))
                throw new ArgumentException("Path must end with '.anim'. Got: '" + clipPath + "'.");

            clipPath = AssetPathGuard.RequireNewAssetPath(clipPath, "animation.create_clip", "AnimationClip", "animation_apply setCurve/setEvents");

            AssetFolders.EnsureExists(AssetFolders.DirectoryName(clipPath));

            var clip = new AnimationClip { frameRate = (float)JsonParams.OptionalDouble(@params, "frameRate", 60.0) };

            var loop = JsonParams.OptionalBool(@params, "loop", false);
            if (loop)
            {
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = true;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
            }

            // 'defaultEasing' is accepted (so it is never an "unknown field" rejection) but
            // currently has no effect: DESIGN-05 §5.3 names it but never specifies where a
            // clip-level default would be STORED (AnimationClip has no such field) or how a LATER,
            // independent setCurve call on the same clip would look it up - there is no per-clip
            // Hades-side metadata store anywhere in this codebase to hang it off. Flagged back to
            // the design rather than invented here; every key's own 'easing' (defaulting to
            // "linear") is unaffected.
            _ = JsonParams.OptionalString(@params, "defaultEasing");

            AssetDatabase.CreateAsset(clip, clipPath);
            Undo.RegisterCreatedObjectUndo(clip, "Hades Create Clip " + clipPath);

            return JsonValue.NewObject()
                .SetProperty("clipPath", JsonValue.String(clipPath))
                .SetProperty("frameRate", JsonValue.Float(clip.frameRate))
                .SetProperty("loop", JsonValue.Bool(loop));
        }

        // -------------------------------------------------------------------------- animation.set_curve

        /// <summary>DESIGN-05 §5.2/§5.2.1: replaces ONE EditorCurveBinding's curve wholesale (not
        /// append). 'easing' is Free-tangent math, NOT a TangentMode (probe: TangentMode has no
        /// easeIn/easeOut member) - every tangent is written exactly once (key i's inTangent comes
        /// from segment [i-1,i], its outTangent from segment [i,i+1]), and the two outer boundaries
        /// mirror their one adjacent segment rather than being left at a spurious default.</summary>
        internal static JsonValue SetCurve(ReloadGate gate, JsonValue @params)
        {
            var clipPath = JsonParams.RequireString(@params, "clipPath", "animation.set_curve");
            // 'path' may legitimately be "" (the Animator root) - JsonParams.RequireString rejects
            // an empty string outright, so presence is checked directly instead.
            var relPath = JsonParams.OptionalString(@params, "path");
            if (relPath == null)
                throw new ArgumentException("'animation.set_curve' requires a 'path' parameter (use '' for the Animator root).");
            var typeName = JsonParams.RequireString(@params, "type", "animation.set_curve");
            var propertyName = JsonParams.RequireString(@params, "property", "animation.set_curve");

            var keysJson = JsonParams.OptionalValue(@params, "keys");
            if (keysJson == null || keysJson.Kind != JsonValueKind.Array || keysJson.Items.Count == 0)
                throw new ArgumentException("animation.set_curve requires a non-empty 'keys' array.");

            var clip = ResolveClip(clipPath, out var clipError) ?? throw new ArgumentException(clipError);
            ThrowIfImportedClip(clipPath, "setCurve");

            var componentType = ResolveComponentType(typeName);
            var resolvedProperty = ResolveBindingAlias(propertyName);

            var keys = new (float time, float value, string easing)[keysJson.Items.Count];
            for (var i = 0; i < keys.Length; i++)
            {
                var k = keysJson.Items[i];
                var timeValue = JsonParams.OptionalValue(k, "time");
                var valueValue = JsonParams.OptionalValue(k, "value");
                if (timeValue == null || (timeValue.Kind != JsonValueKind.Integer && timeValue.Kind != JsonValueKind.Float)
                    || valueValue == null || (valueValue.Kind != JsonValueKind.Integer && valueValue.Kind != JsonValueKind.Float))
                {
                    throw new ArgumentException("keys[" + i + "]: requires numeric 'time' and 'value'.");
                }
                keys[i] = ((float)timeValue.AsDouble(), (float)valueValue.AsDouble(), JsonParams.OptionalString(k, "easing"));
            }

            for (var i = 1; i < keys.Length; i++)
            {
                if (keys[i].time <= keys[i - 1].time)
                {
                    throw new ArgumentException("keys must be in strictly ascending 'time' order - keys[" + (i - 1) + "].time ("
                        + keys[i - 1].time + ") is not before keys[" + i + "].time (" + keys[i].time + ").");
                }
            }

            if (keys[0].easing != null)
            {
                throw new ArgumentException(
                    "keys[0]: 'easing' governs the segment ARRIVING at a key from the PREVIOUS one, so it is "
                    + "meaningless on the first key - rejected, not silently ignored.");
            }

            var keyframes = new Keyframe[keys.Length];
            for (var i = 0; i < keys.Length; i++) keyframes[i] = new Keyframe(keys[i].time, keys[i].value);

            var constantSegmentSourceIndices = new List<int>();
            for (var i = 1; i < keys.Length; i++)
            {
                var (m0, m1, isConstant) = EasingTangents(keys[i].easing, keys[i].time - keys[i - 1].time, keys[i].value - keys[i - 1].value);
                keyframes[i - 1].outTangent = m0;
                keyframes[i].inTangent = m1;
                if (isConstant) constantSegmentSourceIndices.Add(i - 1);
            }
            // Outer boundaries mirror their one adjacent segment (DESIGN-05 §5.2.1 point 2) rather
            // than being left at Keyframe's zero default, which would read as a spurious flat
            // handle in the curve editor even though it evaluates identically either way.
            keyframes[0].inTangent = keyframes[0].outTangent;
            keyframes[keys.Length - 1].outTangent = keyframes[keys.Length - 1].inTangent;

            var curve = new AnimationCurve(keyframes);
            for (var i = 0; i < keyframes.Length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Free);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Free);
            }
            foreach (var sourceIndex in constantSegmentSourceIndices)
                AnimationUtility.SetKeyRightTangentMode(curve, sourceIndex, AnimationUtility.TangentMode.Constant);

            var binding = new EditorCurveBinding { path = relPath, type = componentType, propertyName = resolvedProperty };
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssetIfDirty(clip);

            // §5.4 point 1: echo the resolved binding AND confirm the Editor reads it back - the
            // real safety net against a wrong-but-well-formed property name silently animating
            // nothing.
            var readBack = AnimationUtility.GetEditorCurve(clip, binding);

            return JsonValue.NewObject()
                .SetProperty("clipPath", JsonValue.String(clipPath))
                .SetProperty("path", JsonValue.String(relPath))
                .SetProperty("type", JsonValue.String(componentType.Name))
                .SetProperty("property", JsonValue.String(resolvedProperty))
                .SetProperty("keyCount", JsonValue.Integer(keys.Length))
                .SetProperty("confirmed", JsonValue.Bool(readBack != null && readBack.length == keys.Length));
        }

        /// <summary>The closed-form tangent table, DESIGN-05 §5.2.1 - <paramref name="dt"/>/
        /// <paramref name="dv"/> are the segment's own time/value deltas (B - A). Every named easing
        /// except 'constant' is Free-tangent math (TangentMode has no easeIn/easeOut member,
        /// VERIFIED probe); 'constant' is a genuine mode, not math, signalled via
        /// <c>isConstant</c>.</summary>
        static (float m0, float m1, bool isConstant) EasingTangents(string easing, float dt, float dv)
        {
            var s = dt != 0f ? dv / dt : 0f;
            switch ((easing ?? "linear").ToLowerInvariant())
            {
                case "linear": return (s, s, false);
                case "easein": return (0f, 2f * s, false);
                case "easeout": return (2f * s, 0f, false);
                case "easeinout": return (0f, 0f, false);
                case "constant": return (0f, 0f, true);
                default:
                    throw new ArgumentException("Invalid easing '" + easing + "'. Valid: linear, easeIn, easeOut, easeInOut, constant.");
            }
        }

        /// <summary>DESIGN-05 §5.4: the ~15-highest-frequency curve-binding aliases, ranked against
        /// a real project's corpus (project_aurora, ~50 authored .anim files - not a guess) rather
        /// than the skill's own recommendations - by real EditorCurveBinding frequency, EXCLUDING
        /// humanoid muscle curves (baked from FBX import, never hand-authored):
        /// m_LocalPosition.{x,y,z} (223 occurrences each), localEulerAngles.{x,y,z} (42 - the
        /// Animation Window's OWN Euler rotation curve name, not the raw m_LocalRotation quaternion,
        /// which is not directly human-authorable), m_LocalScale.{x,y,z} (~51), m_SpriteHash/
        /// m_Sprite, m_IsActive, m_Color.{r,g,b,a}, m_Alpha (CanvasGroup), m_Enabled,
        /// m_AnchoredPosition.{x,y} (RectTransform). Exact names pass through unchanged - only an
        /// alias hit is rewritten.</summary>
        static readonly Dictionary<string, string> BindingAliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["position.x"] = "m_LocalPosition.x", ["position.y"] = "m_LocalPosition.y", ["position.z"] = "m_LocalPosition.z",
            ["rotation.x"] = "localEulerAngles.x", ["rotation.y"] = "localEulerAngles.y", ["rotation.z"] = "localEulerAngles.z",
            ["scale.x"] = "m_LocalScale.x", ["scale.y"] = "m_LocalScale.y", ["scale.z"] = "m_LocalScale.z",
            ["color.r"] = "m_Color.r", ["color.g"] = "m_Color.g", ["color.b"] = "m_Color.b", ["color.a"] = "m_Color.a",
            ["alpha"] = "m_Alpha",
            ["sprite"] = "m_Sprite",
            ["active"] = "m_IsActive",
            ["enabled"] = "m_Enabled",
            ["anchoredPosition.x"] = "m_AnchoredPosition.x", ["anchoredPosition.y"] = "m_AnchoredPosition.y",
        };

        static string ResolveBindingAlias(string property) => BindingAliases.TryGetValue(property, out var real) ? real : property;

        /// <summary>Resolves a 'type' string to a real UnityEngine type via
        /// <see cref="UnityTypeSearch"/> (reused, not re-rolled - see that class's
        /// own doc comment), the same fuzzy-but-actionable resolution component_add/removeComponent
        /// already use. <see cref="UnityEngine.Object"/> as the required base admits both Component
        /// types (Transform, RectTransform, SpriteRenderer, ...) and GameObject itself (the 'active'
        /// binding's own type).</summary>
        static Type ResolveComponentType(string typeName)
        {
            var matches = UnityTypeSearch.FindAll(typeName, typeof(UnityEngine.Object), out var unloadableAssemblies);
            if (matches.Count == 1) return matches[0];

            if (matches.Count > 1)
            {
                var names = new List<string>();
                foreach (var m in matches) names.Add(m.FullName);
                throw new ArgumentException("Type '" + typeName + "' is ambiguous - matches: " + string.Join(", ", names) + ". Use a fully-qualified name.");
            }

            throw new ArgumentException("Type '" + typeName + "' not found."
                + (unloadableAssemblies.Count > 0 ? " Some assemblies could not be searched: " + string.Join(", ", unloadableAssemblies) + "." : ""));
        }

        // ------------------------------------------------------------------------- animation.describe

        /// <summary>DESIGN-05 §6.3 Tier 1: the one READ inside animation.apply's nine writes - live,
        /// over UnityEditor.Animations/AnimationUtility, so it works for binary-serialized projects
        /// and FBX sub-asset clips alike (the offline YAML reader cannot). Returns a controller's
        /// blend trees and a clip's curves/events in EXACTLY the schema setBlendTree/setCurve/
        /// setEvents accept - read, mutate the JSON, send back.</summary>
        internal static JsonValue Describe(ReloadGate gate, JsonValue @params)
        {
            var controllerPath = JsonParams.OptionalString(@params, "controllerPath");
            var clipPath = JsonParams.OptionalString(@params, "clipPath");

            if (!string.IsNullOrEmpty(controllerPath)) return DescribeController(controllerPath);
            if (!string.IsNullOrEmpty(clipPath)) return DescribeClip(clipPath);
            throw new ArgumentException("animation.describe requires exactly one of 'controllerPath' or 'clipPath'.");
        }

        static JsonValue DescribeController(string controllerPath)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath)
                ?? throw new ArgumentException("AnimatorController not found at '" + controllerPath + "'.");

            var layersJson = JsonValue.NewArray();
            foreach (var layer in controller.layers)
            {
                var layerJson = JsonValue.NewObject()
                    .SetProperty("name", JsonValue.String(layer.name))
                    .SetProperty("weight", JsonValue.Float(layer.defaultWeight))
                    .SetProperty("blending", JsonValue.String(layer.blendingMode.ToString()))
                    .SetProperty("ikPass", JsonValue.Bool(layer.iKPass));
                if (layer.avatarMask != null)
                    layerJson.SetProperty("avatarMask", JsonValue.String(AssetDatabase.GetAssetPath(layer.avatarMask)));

                var statesJson = JsonValue.NewArray();
                foreach (var cs in layer.stateMachine.states)
                {
                    var stateJson = JsonValue.NewObject()
                        .SetProperty("name", JsonValue.String(cs.state.name))
                        .SetProperty("isDefault", JsonValue.Bool(layer.stateMachine.defaultState == cs.state))
                        .SetProperty("speed", JsonValue.Float(cs.state.speed))
                        .SetProperty("mirror", JsonValue.Bool(cs.state.mirror))
                        .SetProperty("cycleOffset", JsonValue.Float(cs.state.cycleOffset))
                        .SetProperty("writeDefaults", JsonValue.Bool(cs.state.writeDefaultValues))
                        .SetProperty("tag", JsonValue.String(cs.state.tag ?? ""));

                    if (cs.state.motion is BlendTree blendTree) stateJson.SetProperty("tree", DescribeBlendTree(blendTree));
                    else if (cs.state.motion != null) stateJson.SetProperty("clip", JsonValue.String(AssetDatabase.GetAssetPath(cs.state.motion)));

                    statesJson.Add(stateJson);
                }
                layerJson.SetProperty("states", statesJson);
                layersJson.Add(layerJson);
            }

            var parametersJson = JsonValue.NewArray();
            foreach (var p in controller.parameters)
            {
                var pJson = JsonValue.NewObject().SetProperty("name", JsonValue.String(p.name)).SetProperty("type", JsonValue.String(p.type.ToString()));
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: pJson.SetProperty("default", JsonValue.Float(p.defaultFloat)); break;
                    case AnimatorControllerParameterType.Int: pJson.SetProperty("default", JsonValue.Integer(p.defaultInt)); break;
                    case AnimatorControllerParameterType.Bool:
                    case AnimatorControllerParameterType.Trigger: pJson.SetProperty("default", JsonValue.Bool(p.defaultBool)); break;
                }
                parametersJson.Add(pJson);
            }

            return JsonValue.NewObject()
                .SetProperty("controllerPath", JsonValue.String(controllerPath))
                .SetProperty("layers", layersJson)
                .SetProperty("parameters", parametersJson);
        }

        static JsonValue DescribeBlendTree(BlendTree tree)
        {
            var json = JsonValue.NewObject().SetProperty("type", JsonValue.String(tree.blendType.ToString()));
            switch (tree.blendType)
            {
                case BlendTreeType.Simple1D:
                    json.SetProperty("parameter", JsonValue.String(tree.blendParameter));
                    break;
                case BlendTreeType.SimpleDirectional2D:
                case BlendTreeType.FreeformDirectional2D:
                case BlendTreeType.FreeformCartesian2D:
                    json.SetProperty("parameterX", JsonValue.String(tree.blendParameter));
                    json.SetProperty("parameterY", JsonValue.String(tree.blendParameterY));
                    break;
            }

            var childrenJson = JsonValue.NewArray();
            foreach (var child in tree.children)
            {
                var childJson = JsonValue.NewObject();
                if (child.motion is BlendTree nested) childJson.SetProperty("tree", DescribeBlendTree(nested));
                else if (child.motion != null) childJson.SetProperty("clip", JsonValue.String(AssetDatabase.GetAssetPath(child.motion)));

                switch (tree.blendType)
                {
                    case BlendTreeType.Simple1D:
                        childJson.SetProperty("threshold", JsonValue.Float(child.threshold));
                        break;
                    case BlendTreeType.SimpleDirectional2D:
                    case BlendTreeType.FreeformDirectional2D:
                    case BlendTreeType.FreeformCartesian2D:
                        childJson.SetProperty("position", JsonValue.NewArray().Add(JsonValue.Float(child.position.x)).Add(JsonValue.Float(child.position.y)));
                        break;
                    case BlendTreeType.Direct:
                        childJson.SetProperty("directBlendParameter", JsonValue.String(child.directBlendParameter));
                        break;
                }
                childJson.SetProperty("timeScale", JsonValue.Float(child.timeScale));
                childJson.SetProperty("cycleOffset", JsonValue.Float(child.cycleOffset));
                childJson.SetProperty("mirror", JsonValue.Bool(child.mirror));

                childrenJson.Add(childJson);
            }
            json.SetProperty("children", childrenJson);
            return json;
        }

        static JsonValue DescribeClip(string clipPath)
        {
            var clip = ResolveClip(clipPath, out var clipError) ?? throw new ArgumentException(clipError);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);

            var bindings = AnimationUtility.GetCurveBindings(clip);
            var curvesJson = JsonValue.NewArray();
            var emittedKeys = 0;
            var truncated = false;

            for (var b = 0; b < bindings.Length; b++)
            {
                var binding = bindings[b];
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null) continue;

                var keyframes = curve.keys;

                // DESIGN-05 open question 6: an imported humanoid clip is hundreds of curves of
                // hundreds of keys - megabytes of JSON, far over the ~25,000-token single-response
                // cap the consolidation plan measured against, and useless to the agent however
                // fast it arrives. Same answer inspect_asset already uses: a cap plus an honest
                // 'truncated' flag (InspectTool.cs's MaxValueUnits is the 2000 this matches).
                //
                // Whole curves only. A half-curve is worse than an absent one - a caller cannot
                // tell a genuinely short curve from a clipped one, and this method's whole purpose
                // is being trustworthy enough to author a replacement from. The first curve is
                // always emitted however long it is, so the response is never empty.
                if (curvesJson.Items.Count > 0 && emittedKeys + keyframes.Length > MaxDescribeKeys)
                {
                    truncated = true;
                    break;
                }

                var keysJson = JsonValue.NewArray();
                for (var i = 0; i < keyframes.Length; i++)
                {
                    var keyJson = JsonValue.NewObject()
                        .SetProperty("time", JsonValue.Float(keyframes[i].time))
                        .SetProperty("value", JsonValue.Float(keyframes[i].value));
                    if (i > 0)
                    {
                        var easing = DetectEasing(keyframes[i - 1], keyframes[i], curve, i - 1);
                        if (easing != null) keyJson.SetProperty("easing", JsonValue.String(easing));
                    }
                    keysJson.Add(keyJson);
                }

                emittedKeys += keyframes.Length;
                curvesJson.Add(JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String(binding.path))
                    .SetProperty("type", JsonValue.String(binding.type.Name))
                    .SetProperty("property", JsonValue.String(binding.propertyName))
                    .SetProperty("keys", keysJson));
            }

            // AnimationEvent carries floatParameter/intParameter/stringParameter ALL simultaneously
            // (Unity has no "which one was intended" tag), so an event explicitly authored with e.g.
            // 'float: 0' describes back with no 'float' field - indistinguishable from one that
            // never set it. Inherent to Unity's own AnimationEvent shape, not a gap in this reader.
            var eventsJson = JsonValue.NewArray();
            foreach (var e in AnimationUtility.GetAnimationEvents(clip))
            {
                var eventJson = JsonValue.NewObject().SetProperty("time", JsonValue.Float(e.time)).SetProperty("function", JsonValue.String(e.functionName));
                if (e.floatParameter != 0f) eventJson.SetProperty("float", JsonValue.Float(e.floatParameter));
                if (e.intParameter != 0) eventJson.SetProperty("int", JsonValue.Integer(e.intParameter));
                if (!string.IsNullOrEmpty(e.stringParameter)) eventJson.SetProperty("string", JsonValue.String(e.stringParameter));
                if (e.objectReferenceParameter != null) eventJson.SetProperty("objectReference", JsonValue.String(AssetDatabase.GetAssetPath(e.objectReferenceParameter)));
                eventsJson.Add(eventJson);
            }

            return JsonValue.NewObject()
                .SetProperty("clipPath", JsonValue.String(clipPath))
                .SetProperty("frameRate", JsonValue.Float(clip.frameRate))
                .SetProperty("loop", JsonValue.Bool(settings.loopTime))
                .SetProperty("curves", curvesJson)
                // Honest, not silent - the inspect_asset precedent: "a caller must know some of the
                // tree is missing rather than assuming this is the whole file." Always present, so
                // 'false' is a positive statement of completeness rather than an absent field a
                // caller has to interpret.
                .SetProperty("truncated", JsonValue.Bool(truncated))
                .SetProperty("totalCurves", JsonValue.Integer(bindings.Length))
                .SetProperty("curvesReturned", JsonValue.Integer(curvesJson.Items.Count))
                .SetProperty("events", eventsJson);
        }

        /// <summary>Reverse of <see cref="EasingTangents"/>, within a small numeric tolerance -
        /// there is no stored "which named easing produced this" tag, only the raw tangent floats
        /// (and mode, for 'constant'), so a curve hand-edited outside this vocabulary - or one whose
        /// tangents do not closely match any of the five - correctly reports no 'easing' at all
        /// rather than guessing.</summary>
        static string DetectEasing(Keyframe a, Keyframe b, AnimationCurve curve, int sourceIndex)
        {
            if (AnimationUtility.GetKeyRightTangentMode(curve, sourceIndex) == AnimationUtility.TangentMode.Constant) return "constant";

            var dt = b.time - a.time;
            if (dt == 0f) return null;
            var s = (b.value - a.value) / dt;

            var scale = Mathf.Max(1f, Mathf.Abs(s));
            const float relativeEpsilon = 0.001f;
            bool Close(float x, float y) => Mathf.Abs(x - y) <= relativeEpsilon * scale;

            if (Close(a.outTangent, s) && Close(b.inTangent, s)) return "linear";
            if (Close(a.outTangent, 0f) && Close(b.inTangent, 2f * s)) return "easeIn";
            if (Close(a.outTangent, 2f * s) && Close(b.inTangent, 0f)) return "easeOut";
            if (Close(a.outTangent, 0f) && Close(b.inTangent, 0f)) return "easeInOut";
            return null;
        }

        // ---------------------------------------------------------------------------- shared

        /// <summary>DESIGN-05 §5.5's routing guard: an authored .anim's main asset IS the clip
        /// itself; an imported (FBX) clip's main asset is the model/prefab, with the clip as a
        /// SUB-asset - exactly the shape <see cref="ResolveClip"/> already reaches deliberately
        /// (its own doc comment). setCurve/setEvents refuse the latter outright, naming the tool
        /// that DOES own it - the same pattern asset.import uses pointing at material.create.</summary>
        static void ThrowIfImportedClip(string clipPath, string opName)
        {
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(clipPath);
            if (mainAsset != null && !(mainAsset is AnimationClip))
            {
                throw new ArgumentException(
                    "'" + clipPath + "' is an imported clip (its main asset is not the clip itself - likely an "
                    + "FBX). animation_apply " + opName + " only edits an authored .anim clip's own asset; use "
                    + "project_settings_apply setClipImportSettings to configure an imported clip's "
                    + "events/curves instead.");
            }
        }

        static double RequireNumericField(JsonValue value, string context) =>
            value != null && (value.Kind == JsonValueKind.Integer || value.Kind == JsonValueKind.Float)
                ? value.AsDouble()
                : throw new ArgumentException("'" + context + "' must be a number.");

        static bool AddParameter(AnimatorController controller, JsonValue paramDef, JsonValue errors, out string name)
        {
            name = JsonParams.OptionalString(paramDef, "name");
            var typeName = JsonParams.OptionalString(paramDef, "type");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(typeName))
            {
                errors.Add(JsonValue.String("A parameter entry requires 'name' and 'type'."));
                return false;
            }

            if (!Enum.TryParse(typeName, true, out AnimatorControllerParameterType paramType))
            {
                errors.Add(JsonValue.String("Invalid parameter type '" + typeName + "' for '" + name + "'. Valid: Int, Float, Bool, Trigger."));
                return false;
            }

            controller.AddParameter(name, paramType);

            var defaultValue = JsonParams.OptionalValue(paramDef, "default");
            if (defaultValue != null && defaultValue.Kind != JsonValueKind.Null)
            {
                try
                {
                    // AnimatorController.parameters returns a FRESH COPY of the array on every read -
                    // measured directly: indexing into controller.parameters[i] = param (the old
                    // package's own pattern) silently discards the write, because the array that got
                    // mutated is not the same instance the controller holds. The whole array must be
                    // read once, mutated, and written back through the setter for it to stick.
                    var allParams = controller.parameters;
                    var index = allParams.Length - 1;
                    var param = allParams[index];
                    switch (paramType)
                    {
                        case AnimatorControllerParameterType.Float:
                            param.defaultFloat = (float)RequireNumber(defaultValue, name);
                            break;
                        case AnimatorControllerParameterType.Int:
                            param.defaultInt = (int)RequireNumber(defaultValue, name);
                            break;
                        case AnimatorControllerParameterType.Bool:
                            param.defaultBool = defaultValue.Kind == JsonValueKind.Boolean && defaultValue.AsBoolean();
                            break;
                    }
                    allParams[index] = param;
                    controller.parameters = allParams;
                }
                catch (ArgumentException ex)
                {
                    // RequireNumber throws for a non-numeric 'default' on a Float/Int parameter.
                    // The parameter itself is already added above (with its type's own zero-value
                    // default) - only ITS requested default failed - so this degrades exactly like
                    // the bad-type/missing-name checks above (record into errors, keep going)
                    // rather than throwing past CreateController's/DoEditController's own per-entry
                    // handling: by the time this runs, CreateController has already created the
                    // .controller asset on disk, and DoEditController has already applied earlier
                    // removals/additions, so letting this escape would abort the whole batch and,
                    // for CreateController, orphan the asset it already created.
                    errors.Add(JsonValue.String(ex.Message));
                }
            }

            return true;
        }

        static AnimatorState AddState(AnimatorStateMachine sm, Dictionary<string, AnimatorState> stateMap, JsonValue stateDef, JsonValue errors, out string name)
        {
            name = JsonParams.OptionalString(stateDef, "name");
            if (string.IsNullOrEmpty(name))
            {
                errors.Add(JsonValue.String("A state entry requires 'name'."));
                return null;
            }
            if (stateMap.ContainsKey(name))
            {
                errors.Add(JsonValue.String("State '" + name + "' already exists."));
                return null;
            }

            var state = sm.AddState(name);
            Undo.RegisterCreatedObjectUndo(state, "Hades Add State " + name);
            stateMap[name] = state;

            var clipPath = JsonParams.OptionalString(stateDef, "clip");
            if (!string.IsNullOrEmpty(clipPath))
            {
                var clip = ResolveClip(clipPath, out var clipError);
                if (clip != null) state.motion = clip;
                else errors.Add(JsonValue.String(clipError));
            }

            if (JsonParams.OptionalBool(stateDef, "isDefault", false)) sm.defaultState = state;

            // DESIGN-05 Wave 1 §2 row 4: additive state fields, all optional.
            state.speed = (float)JsonParams.OptionalDouble(stateDef, "speed", state.speed);
            state.mirror = JsonParams.OptionalBool(stateDef, "mirror", state.mirror);
            state.cycleOffset = (float)JsonParams.OptionalDouble(stateDef, "cycleOffset", state.cycleOffset);
            state.writeDefaultValues = JsonParams.OptionalBool(stateDef, "writeDefaults", state.writeDefaultValues);
            var tag = JsonParams.OptionalString(stateDef, "tag");
            if (!string.IsNullOrEmpty(tag)) state.tag = tag;

            // 'position' is the STATE MACHINE's own per-child editor-layout hint (ChildAnimatorState.
            // position), not a field on AnimatorState itself - read-modify-write the whole states
            // array back, the same "fresh copy on every read" pattern AddParameter's own comment
            // documents for controller.parameters.
            var positionValue = JsonParams.OptionalValue(stateDef, "position");
            if (positionValue != null && positionValue.Kind == JsonValueKind.Array && positionValue.Items.Count == 2)
            {
                var childStates = sm.states;
                for (var i = 0; i < childStates.Length; i++)
                {
                    if (childStates[i].state != state) continue;
                    var cs = childStates[i];
                    cs.position = new Vector3((float)positionValue.Items[0].AsDouble(), (float)positionValue.Items[1].AsDouble(), 0f);
                    childStates[i] = cs;
                    break;
                }
                sm.states = childStates;
            }
            else if (positionValue != null && positionValue.Kind != JsonValueKind.Null)
            {
                errors.Add(JsonValue.String("State '" + name + "': 'position' must be a 2-element array [x, y]."));
            }

            return state;
        }

        static bool AddTransition(AnimatorStateMachine sm, Dictionary<string, AnimatorState> stateMap, AnimatorController controller, JsonValue t, JsonValue errors)
        {
            var from = JsonParams.OptionalString(t, "from");
            var to = JsonParams.OptionalString(t, "to");
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            {
                errors.Add(JsonValue.String("A transition entry requires 'from' and 'to'."));
                return false;
            }

            var destState = FindState(sm, stateMap, to);
            if (destState == null)
            {
                errors.Add(JsonValue.String("Transition destination '" + to + "' not found. Available states: " + string.Join(", ", AllStateNames(sm)) + "."));
                return false;
            }

            AnimatorStateTransition transition;
            if (string.Equals(from, "AnyState", StringComparison.OrdinalIgnoreCase))
            {
                transition = sm.AddAnyStateTransition(destState);
            }
            else
            {
                var srcState = FindState(sm, stateMap, from);
                if (srcState == null)
                {
                    errors.Add(JsonValue.String("Transition source '" + from + "' not found. Available states: " + string.Join(", ", AllStateNames(sm)) + "."));
                    return false;
                }
                transition = srcState.AddTransition(destState);
            }
            Undo.RegisterCreatedObjectUndo(transition, "Hades Add Transition " + from + "->" + to);

            transition.hasExitTime = JsonParams.OptionalBool(t, "hasExitTime", true);
            transition.duration = (float)JsonParams.OptionalDouble(t, "duration", 0.25);

            // DESIGN-05 Wave 1 §2 row 4: additive transition fields, all optional - closes the "the
            // skill's own scaffold uses exitTime and this vocabulary cannot express it" gap.
            transition.exitTime = (float)JsonParams.OptionalDouble(t, "exitTime", transition.exitTime);
            transition.offset = (float)JsonParams.OptionalDouble(t, "offset", transition.offset);
            transition.canTransitionToSelf = JsonParams.OptionalBool(t, "canTransitionToSelf", transition.canTransitionToSelf);

            var interruptionSourceName = JsonParams.OptionalString(t, "interruptionSource");
            if (!string.IsNullOrEmpty(interruptionSourceName))
            {
                if (Enum.TryParse(interruptionSourceName, true, out TransitionInterruptionSource interruptionSource))
                    transition.interruptionSource = interruptionSource;
                else
                {
                    errors.Add(JsonValue.String("Invalid interruptionSource '" + interruptionSourceName + "' for transition '"
                        + from + "->" + to + "'. Valid: " + string.Join(", ", Enum.GetNames(typeof(TransitionInterruptionSource))) + "."));
                }
            }

            var conditions = JsonParams.OptionalValue(t, "conditions");
            if (IsNonEmptyArray(conditions))
            {
                foreach (var c in conditions.Items)
                {
                    var paramName = JsonParams.OptionalString(c, "parameter");
                    var modeName = JsonParams.OptionalString(c, "mode");
                    if (string.IsNullOrEmpty(paramName) || string.IsNullOrEmpty(modeName))
                    {
                        errors.Add(JsonValue.String("A condition entry requires 'parameter' and 'mode'."));
                        continue;
                    }
                    if (!Enum.TryParse(modeName, true, out AnimatorConditionMode mode))
                    {
                        errors.Add(JsonValue.String("Invalid condition mode '" + modeName + "'. Valid: If, IfNot, Greater, Less, Equals, NotEqual."));
                        continue;
                    }

                    var paramExists = false;
                    foreach (var p in controller.parameters) if (p.name == paramName) { paramExists = true; break; }
                    if (!paramExists)
                    {
                        var names = new List<string>();
                        foreach (var p in controller.parameters) names.Add(p.name);
                        errors.Add(JsonValue.String("Parameter '" + paramName + "' not found. Available: " + string.Join(", ", names) + "."));
                        continue;
                    }

                    transition.AddCondition(mode, (float)JsonParams.OptionalDouble(c, "threshold", 0), paramName);
                }
            }

            return true;
        }

        static int RemoveTransitions(AnimatorStateMachine sm, string from, string to)
        {
            var count = 0;

            if (string.Equals(from, "AnyState", StringComparison.OrdinalIgnoreCase))
            {
                var toRemove = new List<AnimatorStateTransition>();
                foreach (var t in sm.anyStateTransitions)
                    if (string.Equals(t.destinationState != null ? t.destinationState.name : null, to, StringComparison.OrdinalIgnoreCase))
                        toRemove.Add(t);
                foreach (var t in toRemove) { sm.RemoveAnyStateTransition(t); count++; }
                return count;
            }

            foreach (var cs in sm.states)
            {
                if (!string.Equals(cs.state.name, from, StringComparison.OrdinalIgnoreCase)) continue;

                var toRemove = new List<AnimatorStateTransition>();
                foreach (var t in cs.state.transitions)
                    if (string.Equals(t.destinationState != null ? t.destinationState.name : null, to, StringComparison.OrdinalIgnoreCase))
                        toRemove.Add(t);
                foreach (var t in toRemove) { cs.state.RemoveTransition(t); count++; }
            }

            return count;
        }

        /// <summary>The one target layer for an op that edits a single state machine
        /// (editController/setBlendTree) - layer 0 when <paramref name="layerName"/> is omitted,
        /// matching this codebase's pre-existing hardcoded behaviour (CreateController's own
        /// <c>controller.layers[0]</c>), so every existing call keeps working unchanged.</summary>
        static AnimatorControllerLayer TargetLayer(AnimatorController controller, string layerName) =>
            string.IsNullOrEmpty(layerName) ? controller.layers[0] : FindLayerByName(controller, layerName);

        static AnimatorControllerLayer FindLayerByName(AnimatorController controller, string layerName)
        {
            foreach (var layer in controller.layers)
                if (layer.name == layerName) return layer;

            var names = new List<string>();
            foreach (var layer in controller.layers) names.Add(layer.name);
            throw new ArgumentException("Layer '" + layerName + "' not found. Available layers: " + string.Join(", ", names) + ".");
        }

        static AnimatorState FindState(AnimatorStateMachine sm, Dictionary<string, AnimatorState> stateMap, string name)
        {
            if (stateMap.TryGetValue(name, out var state)) return state;
            foreach (var cs in sm.states)
                if (string.Equals(cs.state.name, name, StringComparison.OrdinalIgnoreCase)) return cs.state;
            return null;
        }

        static string[] AllStateNames(AnimatorStateMachine sm)
        {
            var names = new string[sm.states.Length];
            for (var i = 0; i < sm.states.Length; i++) names[i] = sm.states[i].state.name;
            return names;
        }

        static int CountTransitions(AnimatorStateMachine sm)
        {
            var count = sm.anyStateTransitions.Length;
            foreach (var cs in sm.states) count += cs.state.transitions.Length;
            return count;
        }

        /// <summary>Resolves a clip path exactly like an AnimationClip asset reference elsewhere in
        /// this plugin, plus the one animation-specific wrinkle: an imported FBX's clip is a SUB-
        /// asset, not the main asset at that path, so a direct load can miss it even though the
        /// clip is right there - ported unchanged from the old package's AnimationTools.</summary>
        static AnimationClip ResolveClip(string clipPath, out string error)
        {
            error = null;

            var direct = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (direct != null) return direct;

            var allAssets = AssetDatabase.LoadAllAssetsAtPath(clipPath);
            if (allAssets == null || allAssets.Length == 0)
            {
                error = "Asset not found at '" + clipPath + "'.";
                return null;
            }

            AnimationClip firstClip = null;
            foreach (var asset in allAssets)
            {
                if (firstClip == null && asset is AnimationClip clip && !clip.name.StartsWith("__preview__", StringComparison.Ordinal))
                    firstClip = clip;
            }

            if (firstClip == null)
            {
                error = "No AnimationClip found in '" + clipPath + "'. If this is an FBX, ensure the rig type is set to Humanoid or Generic.";
                return null;
            }

            return firstClip;
        }

        static bool IsNonEmptyArray(JsonValue value) => value != null && value.Kind == JsonValueKind.Array && value.Items.Count > 0;

        static string AsStringOrNull(JsonValue value) => value != null && value.Kind == JsonValueKind.String ? value.AsString() : null;

        static double RequireNumber(JsonValue value, string context)
        {
            if (value != null && (value.Kind == JsonValueKind.Integer || value.Kind == JsonValueKind.Float)) return value.AsDouble();
            throw new ArgumentException("'" + context + "' requires a numeric default value.");
        }

        static JsonValue ToJsonStringArray(List<string> values)
        {
            var array = JsonValue.NewArray();
            foreach (var v in values) array.Add(JsonValue.String(v));
            return array;
        }
    }
}
