// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEngine;

namespace Hades.Tools
{
    /// <summary>
    /// scriptableObject.apply: the single wire command backing the app's scriptable_object_apply MCP
    /// tool (Hades.Server.Mcp.ScriptableObjectApplyTool) - DESIGN-04's answer to "Hades cannot create
    /// a ScriptableObject asset". Class 1 (single-tick, no reload lease), registered in
    /// <see cref="CommandTable.Handlers"/> AND <see cref="CommandTable.MutatingMethods"/> exactly like
    /// material.apply/animation.apply/scene.apply, so <see cref="CommandTable.Dispatch"/>'s
    /// pre-increment gives the whole batch one Undo group and no <see cref="LeaseScope"/> is involved -
    /// see that class's own doc comment.
    ///
    /// Unlike MaterialApplyCommands, there is no pre-existing standalone "scriptableObject.create"/
    /// "scriptableObject.set_properties" wire command this delegates to - ScriptableObject creation
    /// never existed on the wire before this change, so both ops are implemented directly in this one
    /// file/class, the same "single new tool, no legacy single-purpose sibling to preserve" shape
    /// AssetManageCommands' own "delete"/"duplicate" additions use.
    ///
    /// <para><b>Ops.</b> <c>create{path,type}</c> resolves <c>type</c> against every loaded assembly
    /// (<see cref="UnityTypeSearch.FindAll"/>, requiring assignability to <see cref="ScriptableObject"/>),
    /// refusing rather than guessing on ambiguity (more than one same-named type in different
    /// assemblies) - see <see cref="ResolveScriptableObjectType"/>. <c>setProperties{path,values}</c>
    /// reuses <see cref="ComponentCommands.ApplyProperties"/> verbatim (widened in this same change
    /// from <c>Component</c> to <c>UnityEngine.Object</c>) for its per-field applied/failed reporting,
    /// adding only the asset-specific <see cref="EditorUtility.SetDirty"/> +
    /// <see cref="AssetDatabase.SaveAssetIfDirty"/> a scene/component mutation never needs (an asset
    /// change is a file write, not scene state a separate scene.save call would persist later).</para>
    ///
    /// <para><b>Undo - NOT the same claim as material.apply's.</b> <c>create</c> registers the new
    /// object with <see cref="Undo.RegisterCreatedObjectUndo"/> - probe-verified (DESIGN-04 §5.2) to
    /// destroy the in-memory object on undo WITHOUT removing the <c>.asset</c> file it wrote.
    /// <c>setProperties</c> is genuinely, fully undoable (<see cref="Undo.RecordObject"/> inside
    /// <see cref="ComponentCommands.ApplyProperties"/>). So one Ctrl/Cmd+Z reverts every field change
    /// AND destroys every object a batch's own <c>create</c> ops made, but the files those <c>create</c>
    /// ops wrote remain on disk - finishing the undo needs a separate <c>asset_manage</c> delete. See
    /// Hades.Server.Mcp.ScriptableObjectApplyTool's own description for the caller-facing wording;
    /// this is the one place that wording must NOT be copied from material.apply's (a live accuracy
    /// bug there for batches containing create - fixed separately in MaterialApplyTool's own
    /// description, not here).</para>
    ///
    /// <para><b>Partial failure, never rolled back; unknown op refused per-op.</b> Identical contract
    /// to every other consolidated batch tool - each operation's outcome is recorded by index in
    /// 'applied'/'failed', processing continues regardless, and an unrecognised 'op' is this ONE
    /// operation's failure (the app already refuses it for the whole call before any wire round trip -
    /// see ScriptableObjectApplyTool.ValidOps - this is a defensive fallback for a non-app caller).</para>
    /// </summary>
    internal static class ScriptableObjectApplyCommands
    {
        static readonly string[] ValidOps = { "create", "setProperties" };

        internal static JsonValue Apply(ReloadGate gate, JsonValue @params)
        {
            var ops = JsonParams.OptionalValue(@params, "operations");
            if (ops == null || ops.Kind != JsonValueKind.Array)
                throw new ArgumentException("scriptableObject.apply requires an 'operations' array parameter.");

            var applied = JsonValue.NewArray();
            var failed = JsonValue.NewArray();
            var results = JsonValue.NewArray();

            // ONE group for the whole batch - collapses harmlessly with CommandTable.Dispatch's own
            // pre-increment (scriptableObject.apply is a MutatingMethods entry, like material.apply).
            Undo.IncrementCurrentGroup();

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

            Undo.SetCurrentGroupName("Hades ScriptableObject Apply: " + applied.Items.Count + " of " + ops.Items.Count + " operation(s)");

            return JsonValue.NewObject()
                .SetProperty("applied", applied)
                .SetProperty("results", results)
                .SetProperty("failed", failed)
                .SetProperty("summary", JsonValue.String(
                    applied.Items.Count + " applied, " + failed.Items.Count + " failed of " + ops.Items.Count + " operation(s)."));
        }

        static JsonValue DispatchOne(string opName, JsonValue op)
        {
            switch (opName)
            {
                case "create": return Create(op);
                case "setProperties": return SetProperties(op);
                default:
                    throw new ArgumentException(
                        "scriptableObject.apply: unknown op '" + (opName ?? "(missing)") + "'. Valid ops: " + string.Join(", ", ValidOps) + ".");
            }
        }

        // ------------------------------------------------------------------------------------ create

        static JsonValue Create(JsonValue op)
        {
            const string ctx = "scriptableObject.create";
            var path = JsonParams.RequireString(op, "path", ctx);
            path = AssetPathGuard.RequireNewAssetPath(path, ctx, "ScriptableObject", "scriptable_object_apply's setProperties operation");
            var typeName = JsonParams.RequireString(op, "type", ctx);

            var resolvedType = ResolveScriptableObjectType(typeName);

            AssetFolders.EnsureExists(AssetFolders.DirectoryName(path));

            var instance = ScriptableObject.CreateInstance(resolvedType);
            AssetDatabase.CreateAsset(instance, path);
            Undo.RegisterCreatedObjectUndo(instance, "Hades Create ScriptableObject");

            return JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(path))
                .SetProperty("type", JsonValue.String(resolvedType.FullName))
                .SetProperty("assembly", JsonValue.String(resolvedType.Assembly.GetName().Name))
                .SetProperty("guid", JsonValue.String(AssetDatabase.AssetPathToGUID(path)));
        }

        // ------------------------------------------------------------------------------------ setProperties

        /// <summary>Reuse, not reimplementation - see this class's own doc comment. The ONLY thing
        /// added on top of <see cref="ComponentCommands.ApplyProperties"/> is the asset-specific save:
        /// a scene/component mutation persists through a later scene.save, but a ScriptableObject
        /// asset IS the file, so this must flush it itself, exactly as material.set_property does
        /// (MaterialCommands.cs) - <see cref="AssetDatabase.SaveAssetIfDirty"/>, never the blanket
        /// <see cref="AssetDatabase.SaveAssets"/>, so only the asset actually touched gets written.</summary>
        static JsonValue SetProperties(JsonValue op)
        {
            const string ctx = "scriptableObject.set_properties";
            var path = JsonParams.RequireString(op, "path", ctx);
            path = AssetPathGuard.RequireWellFormedProjectPath(path, ctx);
            var values = JsonParams.OptionalValue(op, "values");

            var instance = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (instance == null) throw NotAScriptableObjectError(path);

            var (applied, failed, propsSet) = ComponentCommands.ApplyProperties(instance, values);

            if (propsSet > 0)
            {
                EditorUtility.SetDirty(instance);
                AssetDatabase.SaveAssetIfDirty(instance);
            }

            return JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(path))
                .SetProperty("applied", applied)
                .SetProperty("failed", failed);
        }

        static ArgumentException NotAScriptableObjectError(string path)
        {
            var anyAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (anyAsset == null)
            {
                return new ArgumentException(
                    "ScriptableObject asset not found at path: '" + path + "'. Use search_by_name to find the correct project-relative path.");
            }

            var hint = anyAsset is Material ? " - use material_apply instead"
                : anyAsset is UnityEditor.Animations.AnimatorController ? " - use animation_apply instead"
                : "";

            return new ArgumentException(
                "'" + path + "' is a " + anyAsset.GetType().Name + ", not a ScriptableObject" + hint + ".");
        }

        // ------------------------------------------------------------------------------------ type resolution

        /// <summary>DESIGN-04 §4: refuse rather than guess. Ambiguity (more than one same-named
        /// ScriptableObject-derived type across loaded assemblies) is refused, listing every match -
        /// unlike <see cref="ComponentTypes.Find"/>'s first-match-wins, deliberately NOT changed here
        /// (that would alter scene_apply's own behaviour for projects with duplicate component names -
        /// out of scope, see DESIGN-04 §11.5). Abstract types and open generic type definitions are
        /// refused BEFORE <see cref="ScriptableObject.CreateInstance(Type)"/> is ever called, since
        /// that call throws for both with no actionable message of its own.</summary>
        static Type ResolveScriptableObjectType(string typeName)
        {
            var matches = UnityTypeSearch.FindAll(typeName, typeof(ScriptableObject), out var unloadable);

            if (matches.Count == 0) throw NotFoundOrWrongKindError(typeName, unloadable);

            if (matches.Count > 1)
            {
                throw new ArgumentException(
                    "Ambiguous ScriptableObject type '" + typeName + "' - " + matches.Count + " types match: "
                    + string.Join(", ", matches.Select(t => t.FullName + " (" + t.Assembly.GetName().Name + ")"))
                    + ". Pass the namespace-qualified name.");
            }

            var resolved = matches[0];

            if (resolved.IsAbstract)
            {
                throw new ArgumentException(
                    "'" + resolved.FullName + "' is abstract and cannot be instantiated. Use a concrete subclass.");
            }

            if (resolved.IsGenericTypeDefinition)
            {
                throw new ArgumentException(
                    "'" + resolved.FullName + "' is an open generic type and cannot be instantiated directly. "
                    + "Pass a closed, concrete type name instead.");
            }

            return resolved;
        }

        /// <summary>No ScriptableObject-derived match. Distinguishes "found, but it is the wrong
        /// kind" (a MonoBehaviour/Component, or some other type entirely - a targeted, one-extra-walk
        /// error) from "genuinely not found anywhere" (the §4.4 recompile recipe - the dominant
        /// real-world failure: the agent just wrote the script and the domain has not reloaded yet).</summary>
        static ArgumentException NotFoundOrWrongKindError(string typeName, List<string> unloadableAssemblies)
        {
            var anyMatches = UnityTypeSearch.FindAll(typeName, typeof(object), out _);
            if (anyMatches.Count > 0)
            {
                var found = anyMatches[0];
                if (typeof(Component).IsAssignableFrom(found))
                {
                    return new ArgumentException(
                        "'" + typeName + "' is a " + (typeof(MonoBehaviour).IsAssignableFrom(found) ? "MonoBehaviour" : "Component")
                        + ", not a ScriptableObject. Use scene_apply's addComponent to put it on a GameObject.");
                }

                return new ArgumentException(
                    "'" + typeName + "' is a " + found.Name + ", not a ScriptableObject-derived type. "
                    + "scriptable_object_apply can only create types that derive from UnityEngine.ScriptableObject.");
            }

            var message = "ScriptableObject type '" + typeName + "' not found in any loaded assembly. If you just "
                + "created or edited the script that defines it, the Editor has not compiled it yet: call "
                + "project_recompile_scripts, then poll hades_charon_status until it reports attached (a domain "
                + "reload drops and re-establishes the plugin connection), then retry. Use search_by_name to "
                + "confirm the exact class name and that its file is inside a compiled assembly.";

            if (unloadableAssemblies != null && unloadableAssemblies.Count > 0)
            {
                var plural = unloadableAssemblies.Count != 1;
                message += " Note: " + unloadableAssemblies.Count + " assembl" + (plural ? "ies" : "y") + " could not be "
                    + "loaded and " + (plural ? "were" : "was") + " skipped (" + string.Join(", ", unloadableAssemblies)
                    + ") - if your type lives in one of them, fix its compile errors first (project_get_console_log).";
            }

            return new ArgumentException(message);
        }
    }
}
