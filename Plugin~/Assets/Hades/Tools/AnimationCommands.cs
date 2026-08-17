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

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath)
                ?? throw new ArgumentException("AnimatorController not found at '" + controllerPath + "'.");
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath)
                ?? throw new ArgumentException("AnimationClip not found at '" + clipPath + "'.");

            var stateNames = new List<string>();
            foreach (var layer in controller.layers)
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

            // Parameters live directly on the controller; state/transition membership lives on the
            // state machine, a separate serialized object one layer down - both are recorded before
            // any removal so PerformUndo can restore either kind of change.
            Undo.RecordObject(controller, "Hades Edit AnimatorController");
            var sm = controller.layers[0].stateMachine;
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

        // ---------------------------------------------------------------------------- shared

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
