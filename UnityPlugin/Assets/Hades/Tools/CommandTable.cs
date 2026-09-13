// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;

namespace Hades.Tools
{
    /// <summary>
    /// Maps a JSON-RPC method name to the handler that answers it - the one place
    /// <see cref="HadesBoot.HandleRequest(ReloadGate, JsonRpcRequest)"/> dispatches into, instead
    /// of a switch statement that would otherwise grow by one case per new Editor tool (52 of them
    /// - see the "52 Editor tools" plan). "assets.refresh" and the three "lease.*" commands are the
    /// first entries, moved here unchanged from HadesBoot's own switch - LeaseCommandTests (still
    /// calling <c>HadesBoot.HandleRequest(gate, request)</c> exactly as before) is what proves
    /// nothing about their behaviour changed.
    ///
    /// Every handler receives the <see cref="ReloadGate"/> (unused by "assets.refresh", needed by
    /// every "lease.*" command) and the request's raw params, and returns the raw result. An
    /// unknown method throws <see cref="NotSupportedException"/> with the exact wording
    /// <c>HadesBoot.HandleRequest</c> always answered unknown methods with - a regression guard
    /// (LeaseCommandTests.UnknownMethod_StillThrowsNotSupportedException) already pins it.
    /// </summary>
    public static class CommandTable
    {
        public delegate JsonValue Handler(ReloadGate gate, JsonValue @params);

        /// <summary>Public - not internal - for the same "no InternalsVisibleTo between this
        /// assembly and Hades.Tests.Editor" reason every other test seam in this plugin is (see
        /// e.g. ConsoleLogBuffer.Capture's own doc comment). DESIGN-01 §15.1 test 8
        /// (Dispatch_WhilePlaying_RefusesEveryMethodNotOnTheAllowList) is parameterised directly over
        /// this dictionary's keys, so a future handler added with no play-mode decision is caught
        /// without needing to keep a second, hand-maintained list of method names in sync.</summary>
        public static readonly Dictionary<string, Handler> Handlers = new Dictionary<string, Handler>
        {
            ["assets.refresh"] = HandleAssetsRefresh,
            ["lease.acquire"] = HandleLeaseAcquire,
            ["lease.renew"] = HandleLeaseRenew,
            ["lease.release"] = HandleLeaseRelease,

            // Class 1 - single-tick mutations (no lease; see SceneCommands/ComponentCommands'
            // own doc comments for why). "scene.*"/"component.*"/"reference.*"/"event.*" is this
            // dispatch table's own naming (namespace.snake_case_action) - distinct from the
            // snake_case MCP tool names (scene_create_gameobject, etc) Hades.Server.Mcp registers;
            // see EditorSceneTools/EditorComponentTools for why the two deliberately differ.
            ["scene.create_gameobject"] = SceneCommands.CreateGameObject,
            ["scene.create_primitive"] = SceneCommands.CreatePrimitive,
            ["scene.delete_gameobject"] = SceneCommands.DeleteGameObject,
            ["scene.reparent_gameobject"] = SceneCommands.ReparentGameObject,
            ["scene.rename_gameobject"] = SceneCommands.RenameGameObject,
            ["scene.setup"] = SceneCommands.SceneSetup,
            ["scene.apply"] = SceneApplyCommands.Apply,

            ["component.add"] = ComponentCommands.AddComponent,
            ["component.remove"] = ComponentCommands.RemoveComponent,
            ["component.set_property"] = ComponentCommands.SetProperty,
            ["component.set_properties"] = ComponentCommands.SetProperties,

            ["reference.set"] = ComponentCommands.ReferenceSet,
            ["event.add_listener"] = ComponentCommands.EventAddListener,
            ["event.remove_listener"] = ComponentCommands.EventRemoveListener,

            ["material.create"] = MaterialCommands.CreateMaterial,
            ["material.set_property"] = MaterialCommands.SetProperty,
            ["material.assign"] = MaterialCommands.AssignMaterial,
            ["material.duplicate"] = MaterialCommands.DuplicateMaterial,
            ["material.swap_shader"] = MaterialCommands.SwapShader,
            ["material.apply"] = MaterialApplyCommands.Apply,

            ["animation.assign_controller"] = AnimationCommands.AssignController,
            ["animation.assign_clip"] = AnimationCommands.AssignClip,
            ["animation.create_controller"] = AnimationCommands.CreateController,
            ["animation.edit_controller"] = AnimationCommands.EditController,
            ["animation.apply"] = AnimationApplyCommands.Apply,

            ["tag.create"] = TagLayerCommands.CreateTag,
            ["tag.delete"] = TagLayerCommands.DeleteTag,
            ["layer.create"] = TagLayerCommands.CreateLayer,

            ["scene.save"] = SceneManagementCommands.SaveScene,
            ["scene.create"] = SceneManagementCommands.CreateScene,
            ["scene.duplicate"] = SceneManagementCommands.DuplicateScene,
            ["scene.set_build"] = SceneManagementCommands.SetBuildScenes,

            ["asset.move"] = AssetCommands.MoveAsset,

            // DESIGN-04 (Lane D). Class 1, like material.apply/animation.apply - see
            // ScriptableObjectApplyCommands' own class doc comment.
            ["scriptableObject.apply"] = ScriptableObjectApplyCommands.Apply,

            ["inspector.select"] = InspectorCommands.SelectGameObject,

            // Class 2 - multi-tick, one call, lease bounded by the call (see PrefabCommands' own
            // doc comment for the shared LeaseScope.Run wrapper every handler below uses).
            ["prefab.create"] = PrefabCommands.CreatePrefab,
            ["prefab.instantiate"] = PrefabCommands.InstantiatePrefab,
            ["prefab.apply_overrides"] = PrefabCommands.ApplyOverrides,
            ["prefab.edit_property"] = PrefabCommands.EditProperty,
            ["prefab.open_editing"] = PrefabCommands.OpenEditing,
            ["prefab.save_editing"] = PrefabCommands.SaveEditing,
            ["prefab.create_variant"] = PrefabCommands.CreateVariant,
            ["prefab.apply"] = PrefabApplyCommands.Apply,

            ["asset.import"] = AssetCommands.ImportAsset,
            ["asset.set_import_settings"] = AssetCommands.SetImportSettings,
            ["asset.set_clip_import_settings"] = AssetCommands.SetClipImportSettings,

            // "projectSettings.apply" (Plan 10 Task 4) mixes class-1 (tag/layer/build-scenes) and
            // class-2 (import-settings) ops in one batch, exactly like "prefab.apply" mixes class-2
            // ops of its own five kinds - see ProjectSettingsApplyCommands' own doc comment for why
            // it self-manages its lease/Undo group the same way and is NOT a MutatingMethods entry
            // below.
            ["projectSettings.apply"] = ProjectSettingsApplyCommands.Apply,

            // "asset.manage"/"scene.manage" (Plan 10 Task 5) mix class-1 and class-2 ops from
            // AssetCommands / SceneManagementCommands+ProjectCommands the same way - see their own
            // doc comments (AssetManageCommands.cs/SceneManageCommands.cs) for why both self-manage
            // their lease/Undo group and are NOT MutatingMethods entries below.
            ["asset.manage"] = AssetManageCommands.Apply,
            ["scene.manage"] = SceneManageCommands.Apply,

            ["scene.open"] = ProjectCommands.OpenScene,
            ["project.recompile_scripts"] = ProjectCommands.RecompileScripts,
            ["project.run_tests"] = ProjectCommands.RunTests,
            ["hades.regression_replay"] = ProjectCommands.RegressionReplay,

            // DESIGN-03 (project_build). 'project.build_start' is class 2 (bounded LeaseScope.Run
            // prep, then the real BuildPipeline.BuildPlayer call on a LATER tick - see
            // BuildCommands.Start's own doc comment); 'project.build_targets' is class 4 (live-state
            // read, just below). See BuildCommands.cs's own class doc comment for the full picture.
            ["project.build_start"] = BuildCommands.Start,

            // Class 3 - multi-call session, the agent must come back (see the "52 Editor tools"
            // plan's Task 4). BeginScriptEditing/EndScriptEditing deliberately do NOT go through
            // LeaseScope.Run - see ProjectCommands' own doc comment for why an exception between
            // the two must leave the lease held, the opposite of every class-2 entry above.
            ["project.begin_script_editing"] = ProjectCommands.BeginScriptEditing,
            ["project.end_script_editing"] = ProjectCommands.EndScriptEditing,
            ["hades.regression_record_start"] = ProjectCommands.RegressionRecordStart,
            ["hades.regression_record_stop"] = ProjectCommands.RegressionRecordStop,

            // Class 4 - live-state reads (no lease; see the "52 Editor tools" plan's operation-
            // class table, Task 5). None of these three ever touch gate, the same as every
            // class-1 entry above - see ProjectCommands/InspectorCommands' own doc comments for
            // why: their answers live only in this running Editor's memory (the console
            // scrollback, an in-progress test run, a GameObject's live serialized state), so there
            // is nothing a domain reload could interrupt mid-flight.
            ["project.get_console_log"] = ProjectCommands.GetConsoleLog,
            ["project.get_test_results"] = ProjectCommands.GetTestResults,
            ["project.build_targets"] = BuildCommands.Targets,
            ["inspector.inspect"] = InspectorCommands.InspectGameObject,

            // DESIGN-02 (capture_view). Slice 1 only (view: "game"/"scene" - "prefab" refuses with
            // an actionable NotSupportedException; see CaptureCommands' own class doc comment for
            // the cut). No lease, for the same reason as every other class-4 entry above: the
            // rendered bytes exist only for the instant of this call, so there is nothing a domain
            // reload could interrupt mid-flight. Already allow-listed on AllowedWhilePlaying below
            // (seeded by Lane B before this handler existed - see that dictionary's own doc comment).
            ["capture.view"] = CaptureCommands.CaptureView,

            // DESIGN-01 (play_mode). 'play.enter' is class 2 (bounded LeaseScope.Run prep, see
            // PlayModeCommands.Enter's own doc comment); 'play.exit'/'play.set_paused'/'play.step'
            // are single-tick with no lease; 'play.status' is class 4 (live-state read). See
            // PlayModeCommands.cs's own class doc comment for the full picture.
            ["play.enter"] = PlayModeCommands.Enter,
            ["play.exit"] = PlayModeCommands.Exit,
            ["play.set_paused"] = PlayModeCommands.SetPaused,
            ["play.step"] = PlayModeCommands.Step,
            ["play.status"] = PlayModeCommands.Status,

            // DESIGN-06 (scene_bake). One wire method, three actions (start|status|cancel) - see
            // SceneBakeCommands.cs's own class doc comment. 'start'/'cancel' are class 2 (bounded
            // LeaseScope.Run prep); 'status' is class 4 (live-state read). Guard entries already
            // exist in AllowedWhilePlaying above, seeded by Lane B before this handler existed.
            ["scene.bake"] = SceneBakeCommands.Bake,
        };

        /// <summary>
        /// DESIGN-01 §6.2's mutation guard, keyed "method" or "method:action" (DESIGN-08 §7.2's
        /// action-keyed single-op shape - a command with a top-level 'action' JSON param, e.g. the
        /// not-yet-built 'scene.bake', is looked up as "scene.bake:start" first, falling back to the
        /// bare method so every existing, action-less entry is unaffected).
        ///
        /// A <see cref="Dictionary{TKey,TValue}"/> rather than a <see cref="HashSet{T}"/>
        /// (DESIGN-08 §7.1's recommendation): present-with-null means "permitted, the generic guard
        /// never fires"; present-with-a-string means "explicitly refused, using this message instead
        /// of the generic one"; ABSENT means "refused, generic message" - default-deny, so a future
        /// handler added with no decision here is refused by construction, never by remembering.
        ///
        /// The generating rule (DESIGN-01 §6.2), which settles every entry without a judgement call:
        /// <b>refuse anything that ACQUIRES or MUTATES; permit anything that RELEASES, READS, or
        /// RECOVERS.</b> It deliberately does not rest on what happens to a mutation after Play Mode
        /// exits (scene changes are discarded, asset-file writes persist - a split invisible in every
        /// result, which is why a uniform refusal is the only safe rule) - it turns only on what the
        /// command DOES, which is knowable by inspection.
        ///
        /// 'capture.view' is owned by DESIGN-02, not created here - permitted anyway, because the
        /// decision ("is this permitted while playing") belongs to this guard regardless of which
        /// design creates the command. Harmless if DESIGN-02 has not shipped: this dictionary is only
        /// consulted after a successful <see cref="Handlers"/> lookup, so an allow-listed name with no
        /// registered handler is unreachable and costs nothing but a string (DESIGN-01 §6.2's
        /// "cross-document dependency" note). scene.bake's two "not yet built" entries below are the
        /// same story, for DESIGN-06 (Lane G). 'project.build_start'/'project.build_targets' were
        /// this story too until DESIGN-03 (Lane C) shipped real handlers for both - see
        /// <see cref="BuildCommands.Start"/>/<see cref="BuildCommands.Targets"/> in Handlers above.
        /// </summary>
        public static readonly Dictionary<string, string> AllowedWhilePlaying = new Dictionary<string, string>
        {
            // Permitted - each one releases, reads, or recovers (see the generating rule above).
            ["play.enter"] = null,
            ["play.exit"] = null,
            ["play.set_paused"] = null,
            ["play.step"] = null,
            ["play.status"] = null,
            ["lease.renew"] = null,
            ["lease.release"] = null,
            ["project.end_script_editing"] = null,
            ["inspector.inspect"] = null,
            ["inspector.select"] = null,
            ["project.get_console_log"] = null,
            ["project.get_test_results"] = null,
            ["capture.view"] = null, // DESIGN-02 - see this dictionary's own doc comment

            // Explicit refuse (DESIGN-08 §7.3) - present so the play-mode interaction is a stated
            // decision, not an oversight the guard's own default-deny happened to cover by silence.
            // Functionally identical to being absent; each carries a message more specific than the
            // generic one CommandTable.Dispatch falls back to.
            ["project.run_tests"] = PlayModeCommands.RunTestsRefusedWhilePlayingMessage,
            ["scene.bake:start"] = PlayModeCommands.BakeStartRefusedWhilePlayingMessage,
            ["project.build_start"] = PlayModeCommands.BuildStartRefusedWhilePlayingMessage,

            // Allow entry for a command DESIGN-06 (Lane G) has not built yet - see this
            // dictionary's own doc comment for why this is safe.
            ["scene.bake:status"] = null,
            ["scene.bake:cancel"] = null,
            ["project.build_targets"] = null,

            // DESIGN-04 (Lane D): 'scriptableObject.apply' and 'asset.manage' are DELIBERATELY ABSENT
            // from this dictionary - both are refused while playing, registered by absence, per
            // DESIGN-01 §15.1.1's "a future handler added with no play-mode decision is caught, not
            // remembered" rule. The reasoning is uniformity and conservatism about live-reference
            // blast radius (an object playing right now may hold a reference to the very
            // ScriptableObject being edited), NOT that the writes are discarded on exit - they are
            // not: both tools write asset FILES, which persist across a Play Mode exit exactly like
            // any other asset write, unlike the scene/component state this dictionary's own doc
            // comment's generic refusal message describes (DESIGN-04 §5.4). See that section for the
            // full reasoning and for 'setProperties' as a stated future allow-list candidate once the
            // live-reference question is answered.
        };

        /// <summary>
        /// Task 7's Defect 3: exactly the Class 1 (single-tick mutation) keys from
        /// <see cref="Handlers"/> above - the same entries the "Class 1" comment block marks (31 as
        /// of Plan 9 Task 7, plus Plan 10 Task 1's "scene.apply" and Task 2's "material.apply"/
        /// "animation.apply" - all three themselves Class 1 handlers: single-tick, no lease, done
        /// inside one Dispatch call), copied rather than computed, so this list is auditable by eye
        /// against that block. Used by <see cref="Dispatch"/> to give every one of these its own
        /// Undo group.
        ///
        /// Deliberately excludes everything else registered above: "lease.*"/"assets.refresh"
        /// (reload-lock bookkeeping, not scene/asset content a user would ever Ctrl/Cmd+Z), class 2
        /// (prefab/asset/project operations scoped by their own bounded lease - PrefabUtility's
        /// LoadPrefabContents/SaveAsPrefabAsset write straight to an asset file and are not part of
        /// Unity's interactive Undo model the way a scene GameObject/component edit is), class 3
        /// (BeginScriptEditing/EndScriptEditing/regression-record sessions - not a content mutation
        /// either), and class 4 (project.get_console_log/get_test_results/inspector.inspect - pure
        /// reads). Grouping those would be pure cost (Unity's undo group counter growing on every
        /// read-only poll an agent happens to make) for zero benefit (nothing in them is ever
        /// recorded to Undo in the first place). "prefab.apply" (Plan 10 Task 2) is class 2 like
        /// every other prefab.* entry and stays out for the SAME reason those do - it self-manages
        /// its own single Undo group AND its own single reload lease directly inside
        /// <see cref="PrefabApplyCommands.Apply"/>, rather than relying on Dispatch's pre-increment
        /// the way the three Class 1 apply tools do - see that class's own doc comment.
        /// "projectSettings.apply" (Plan 10 Task 4) stays out for the SAME reason - it MIXES class-1
        /// (tag.*/layer.*/scene.set_build) and class-2 (asset.set_import_settings/
        /// asset.set_clip_import_settings) operations in one batch, so it self-manages its own
        /// single Undo group AND its own single reload lease directly inside
        /// <see cref="ProjectSettingsApplyCommands.Apply"/>, exactly like prefab.apply - see that
        /// class's own doc comment.
        /// </summary>
        static readonly HashSet<string> MutatingMethods = new HashSet<string>
        {
            "scene.create_gameobject", "scene.create_primitive", "scene.delete_gameobject",
            "scene.reparent_gameobject", "scene.rename_gameobject", "scene.setup", "scene.apply",
            "component.add", "component.remove", "component.set_property", "component.set_properties",
            "reference.set", "event.add_listener", "event.remove_listener",
            "material.create", "material.set_property", "material.assign", "material.duplicate", "material.swap_shader",
            "material.apply",
            "animation.assign_controller", "animation.assign_clip", "animation.create_controller", "animation.edit_controller",
            "animation.apply",
            "tag.create", "tag.delete", "layer.create",
            "scene.save", "scene.create", "scene.duplicate", "scene.set_build",
            "asset.move",
            "scriptableObject.apply",
            "inspector.select",
        };

        /// <summary>Looks up and runs the handler for <paramref name="request"/>.Method, or throws
        /// <see cref="NotSupportedException"/> for anything not registered. Every SUCCESSFUL
        /// dispatch (a handler that returns rather than throws) is also offered to
        /// <see cref="ProjectCommands.CaptureIfRecording"/> - a no-op unless a
        /// hades.regression_record session is currently open, in which case this is the one place
        /// that can see every method this plugin answers, regardless of which class it belongs to
        /// or which file defines it.
        ///
        /// Task 7's Defect 3: two back-to-back RPC-driven mutations used to land in the SAME Unity
        /// undo group (one Cmd/Z reverted both), because RPC calls never pass through the
        /// interactive GUI event cycle that normally opens a fresh group between distinct user
        /// actions - only the explicit batch tools (scene.setup, component.set_properties,
        /// animation.edit_controller, and - as of Plan 10 Task 1 - scene.apply) called Undo.
        /// IncrementCurrentGroup() themselves, and only around their OWN batch. Incrementing here,
        /// once per Dispatch call, before the handler runs, gives every OTHER class-1 call the same
        /// isolation with a one-line, single-purpose change: every mutating call now starts its own
        /// fresh group, regardless of what a previous call (or the user's own last interactive edit)
        /// left the group at. A batch tool's own internal increment (called before ANY of its own
        /// Undo-recording work - see SceneCommands.SceneSetup/ComponentCommands.SetProperties/
        /// AnimationCommands.EditController/SceneApplyCommands.Apply) still runs straight after this
        /// one with nothing recorded in between, so it just produces one harmless, empty leading
        /// group; the batch's own single Undo step for its WHOLE call is unchanged - see
        /// CommandTableUndoGroupingTests for the proof of both properties.</summary>
        public static JsonValue Dispatch(ReloadGate gate, JsonRpcRequest request)
        {
            if (gate == null) throw new ArgumentNullException(nameof(gate));
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (!Handlers.TryGetValue(request.Method, out var handler))
                throw new NotSupportedException("Method '" + request.Method + "' is not implemented yet.");

            // DESIGN-01 §6.2's mutation guard - after the handler lookup, before the Undo
            // increment, exactly as specified there.
            if (EditorPlayModeApi.IsPlaying()) EnforcePlayModeGuard(request);

            if (MutatingMethods.Contains(request.Method)) Undo.IncrementCurrentGroup();

            var result = handler(gate, request.Params);
            ProjectCommands.CaptureIfRecording(request.Method, request.Params, result);
            return result;
        }

        // ---------------------------------------------------------------- DESIGN-01 play-mode guard

        /// <summary>Looks <paramref name="request"/> up in <see cref="AllowedWhilePlaying"/>,
        /// consulting the action-specific key ("method:action") first when the request carries a
        /// top-level 'action' param, falling back to the bare method - see that dictionary's own doc
        /// comment for the full lookup/refusal contract. Throws for both an absent key (generic
        /// message) and a present-but-refused one (its own override message); returns normally only
        /// when the key is present with a null value.</summary>
        static void EnforcePlayModeGuard(JsonRpcRequest request)
        {
            var action = JsonParams.OptionalString(request.Params, "action");

            string message;
            var found = (action != null && AllowedWhilePlaying.TryGetValue(request.Method + ":" + action, out message))
                || AllowedWhilePlaying.TryGetValue(request.Method, out message);

            if (!found) throw new InvalidOperationException(GenericPlayModeRefusal(request.Method));
            if (message != null) throw new InvalidOperationException(message);
            // else: found with a null value - permitted, fall through to the real handler.
        }

        /// <summary>The DESIGN-01 §6.2 message, verbatim, for a command with no entry in
        /// <see cref="AllowedWhilePlaying"/> at all. States the split (scene changes are discarded on
        /// exit, asset-file writes persist) rather than picking one side of it for THIS specific
        /// <paramref name="method"/> - a claim resolved one way would be wrong for the majority of the
        /// commands this message can be thrown for. Names exactly one tool-name-shaped token,
        /// 'play_mode', which is live (LiveMcpToolNames.Names) and whose 'exit' action reaches
        /// 'play.exit', itself on <see cref="AllowedWhilePlaying"/> - so the recovery this message
        /// advertises is always callable from the state it is thrown in.</summary>
        public static string GenericPlayModeRefusal(string method) =>
            "'" + method + "' changes project state, and this Unity Editor is in Play Mode. Hades "
            + "refuses project mutations while playing: a scene change made now would be discarded "
            + "when Play Mode exits, while an asset-file write would persist - and the result would "
            + "look the same either way. Call play_mode with action 'exit' first, then retry.";

        // ---------------------------------------------------------------- assets.refresh

        // Runs on the main thread like every other command (HadesBoot dispatches through
        // MainThreadPump before Dispatch ever runs), which AssetDatabase.Refresh requires. Exists
        // because a BACKGROUND Editor never refreshes on its own - it refreshes on focus - so
        // without this nothing can provoke a recompile, and "did not recompile" proves nothing
        // about whether the reload lock is working.
        //
        // Class 2 as of Plan 9 Task 3 (project_refresh_assets is this command's MCP-tool face -
        // see ProjectCommands' own doc comment for why no second plugin-side implementation
        // exists): wrapped in LeaseScope.Run for the same reason every other class-2 handler is -
        // AssetDatabase.Refresh can trigger asset import and, if scripts changed on disk, script
        // compilation, both real reload risk bounded by this one call.
        static JsonValue HandleAssetsRefresh(ReloadGate gate, JsonValue @params) =>
            LeaseScope.Run(gate, "assets.refresh", () =>
            {
                UnityEditor.AssetDatabase.Refresh();
                return JsonValue.NewObject().SetProperty("refreshed", JsonValue.Bool(true));
            });

        // ---------------------------------------------------------------- lease.*

        /// <summary>Acquires <paramref name="gate"/> under the request's leaseId - see
        /// ReloadGate.Acquire. <c>ttlSeconds</c> is optional; omitted means ReloadGate.DefaultTtl.</summary>
        static JsonValue HandleLeaseAcquire(ReloadGate gate, JsonValue @params)
        {
            RejectUnknownParams(@params, "leaseId", "ttlSeconds");
            var leaseId = RequireLeaseId(@params);
            var ttl = OptionalTtl(@params);
            var success = gate.Acquire(leaseId, ttl);
            return BuildLeaseResult(gate, success);
        }

        /// <summary>Renews <paramref name="gate"/>'s held lease if the request's leaseId is still
        /// the current holder - see ReloadGate.Renew. Also the app's reconnect-reconciliation probe
        /// (see Hades.Core.Editors.LeaseRegistry.ReconcileAsync): a false <c>success</c> with a
        /// null leaseId in the result is exactly "nothing is held here".</summary>
        static JsonValue HandleLeaseRenew(ReloadGate gate, JsonValue @params)
        {
            RejectUnknownParams(@params, "leaseId");
            var leaseId = RequireLeaseId(@params);
            var success = gate.Renew(leaseId);
            return BuildLeaseResult(gate, success);
        }

        /// <summary>Releases <paramref name="gate"/> on behalf of the request's leaseId - see
        /// ReloadGate.Release, which already succeeds idempotently for an unknown or
        /// already-released id (never an error).</summary>
        static JsonValue HandleLeaseRelease(ReloadGate gate, JsonValue @params)
        {
            RejectUnknownParams(@params, "leaseId");
            var leaseId = RequireLeaseId(@params);
            var success = gate.Release(leaseId);
            return BuildLeaseResult(gate, success);
        }

        /// <summary>Rejects any parameter this command does not understand.
        ///
        /// Silently ignoring an unknown key is a footgun with real cost: during the reload-gate
        /// end-to-end work a caller sent "ttlMs" where the plugin reads "ttlSeconds", got back
        /// success:true, and silently received DefaultTtl (30s) instead of the 120s it asked for.
        /// The lease then expired mid-test and the resulting misdiagnosis cost far more than the
        /// typo. The wire contract already returns the ACTUAL expiry, so a diligent caller could
        /// have caught it - but a mistake that needs diligence to detect is a mistake the protocol
        /// should refuse outright.</summary>
        internal static void RejectUnknownParams(JsonValue @params, params string[] known)
        {
            if (@params == null || @params.Kind != JsonValueKind.Object) return;

            foreach (var member in @params.Members)
            {
                var recognised = false;
                for (var i = 0; i < known.Length; i++)
                {
                    if (known[i] == member.Key) { recognised = true; break; }
                }

                if (!recognised)
                {
                    throw new ArgumentException(
                        "Unknown parameter '" + member.Key + "'. Expected one of: " + string.Join(", ", known)
                        + ". Refused rather than ignored, so a typo cannot silently change behaviour.");
                }
            }
        }

        static string RequireLeaseId(JsonValue @params)
        {
            if (@params != null && @params.TryGetProperty("leaseId", out var value)
                && value != null && value.Kind == JsonValueKind.String)
            {
                var id = value.AsString();
                if (!string.IsNullOrEmpty(id)) return id;
            }

            throw new ArgumentException("lease.* requires a non-empty string 'leaseId' parameter.");
        }

        static TimeSpan? OptionalTtl(JsonValue @params)
        {
            if (@params != null && @params.TryGetProperty("ttlSeconds", out var value) && value != null
                && (value.Kind == JsonValueKind.Integer || value.Kind == JsonValueKind.Float))
            {
                return TimeSpan.FromSeconds(value.AsDouble());
            }

            return null;
        }

        /// <summary>
        /// Builds the wire result every lease.* command returns: <paramref name="success"/> is
        /// that specific call's own ReloadGate result; <c>leaseId</c>/<c>expiresAtUtcMs</c> are
        /// always <see cref="ReloadGate.CurrentLease"/> AFTER the call - the plugin's own ground
        /// truth, never an echo of what was requested (see the release-paths/visibility plan: the
        /// app must track the TTL actually applied, not assumed). Null/null when nothing is held.
        /// A rejected acquire/release (a DIFFERENT lease holds the gate) still reports a real
        /// leaseId/expiresAtUtcMs: the actual current holder's, not the rejected caller's.
        /// </summary>
        static JsonValue BuildLeaseResult(ReloadGate gate, bool success)
        {
            var lease = gate.CurrentLease;

            var result = JsonValue.NewObject();
            result.SetProperty("success", JsonValue.Bool(success));
            result.SetProperty("leaseId", lease != null ? JsonValue.String(lease.Id) : JsonValue.Null);
            result.SetProperty("expiresAtUtcMs", lease != null ? JsonValue.Integer(ToUnixTimeMs(lease.ExpiresAtUtc)) : JsonValue.Null);
            return result;
        }

        static long ToUnixTimeMs(DateTime utc) =>
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    }
}
