namespace Hades.Server.Tests;

using Hades.Contract.Wire;
using WireKind = Hades.Contract.Wire.JsonValueKind;

/// <summary>
/// The structural half of the contract-test mechanism described in EditorToolTestBase: WHICH
/// plugin-side JsonParams 'context' string(s) each of the 7 consolidated *_apply/*_manage tools' own
/// (wireMethod, op) pairs dispatches to. PluginRequiredFields.cs supplies the field NAMES each
/// context requires, parsed live from Plugin~ source; this class supplies the STRUCTURE connecting
/// an op to the context(s) that govern it - a fact that only changes when an op is deliberately
/// re-plumbed to call a different underlying plugin command, never by a same-meaning field rename
/// (that class of change is exactly what PluginRequiredFields' live parse absorbs automatically).
///
/// Verified against Plugin~/Assets/Hades/Tools/{Scene,Prefab,Material,Animation,Asset,SceneManagement,
/// TagLayer,Project}*Commands.cs and the 7 *ApplyCommands.cs/*ManageCommands.cs batch dispatchers on
/// 2026-08-04 - see each op's own comment below for the exact DoXxx/CopyFields call it mirrors.
///
/// Applied by EditorToolTestBase.AnswerOneAsync/AnswerBusyProbeThenRespondAsync/
/// AnswerBusyProbeThenFailAsync, so every existing test in every *_apply/*_manage test file (and any
/// future one built on the same base class) enforces this contract automatically, with zero
/// per-test changes - see that class's own doc comment.
/// </summary>
internal static class PluginWireContract
{
    /// <summary>wireMethod -&gt; op -&gt; the plugin context(s) whose RequireString/RequireInt fields
    /// that op's own wire-level JSON object must satisfy. Most ops map to exactly one context: the
    /// underlying single-purpose command the batch dispatcher's CopyFields(op, "keyA", "keyB", ...)
    /// forwards into verbatim (field names unchanged - e.g. MaterialApplyCommands.DispatchOne's
    /// "setProperty" case copies "materialPath"/"propertyName"/"value" straight into
    /// MaterialCommands.SetProperty, whose own context is "material.set_property"). Two structural
    /// exceptions, both noted inline: scene.apply's ops map to SceneApplyCommands' OWN per-op
    /// contexts (it builds an ADAPTED params object using different field names before calling the
    /// shared commands, so what the APP must send is what SceneApplyCommands.DoXxx itself requires
    /// from 'op' directly, not the further-adapted call's own requirement); animation.apply's
    /// createController/editController both funnel through AnimationApplyCommands.
    /// ControllerPathToPath first, whose own RequireString call (context "animation_apply") is what
    /// fixes the op-level field as 'controllerPath' - the FURTHER rename to 'path' for the
    /// downstream animation.create_controller/animation.edit_controller call is the adapter's job,
    /// invisible to the app.</summary>
    static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string[]>> OpContexts =
        new Dictionary<string, IReadOnlyDictionary<string, string[]>>(StringComparer.Ordinal)
        {
            // SceneApplyCommands.DispatchOne - every op's own DoXxx reads 'op' (the wire-level
            // operation object) directly via JsonParams.RequireString(op, "...", ctx-or-literal).
            ["scene.apply"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["create"] = new[] { "scene_apply create" },
                ["addComponent"] = new[] { "scene_apply addComponent" },
                ["removeComponent"] = new[] { "scene_apply removeComponent" },
                ["setProperties"] = new[] { "scene_apply setProperties" },
                ["setReference"] = new[] { "scene_apply setReference" },
                ["addListener"] = new[] { "scene_apply addListener" },
                ["removeListener"] = new[] { "scene_apply removeListener" },
                ["delete"] = new[] { "scene_apply delete" },
                ["reparent"] = new[] { "scene_apply reparent" },
                ["rename"] = new[] { "scene_apply rename" },
                ["select"] = new[] { "scene_apply select" },
            },

            // PrefabApplyCommands.DispatchOne - CopyFields forwards named keys verbatim into each
            // PrefabCommands.DoXxx core.
            ["prefab.apply"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["create"] = new[] { "prefab.create" },
                ["instantiate"] = new[] { "prefab.instantiate" },
                ["applyOverrides"] = new[] { "prefab.apply_overrides" },
                ["editProperty"] = new[] { "prefab.edit_property" },
                ["createVariant"] = new[] { "prefab.create_variant" },
            },

            // MaterialApplyCommands.DispatchOne - CopyFields forwards named keys verbatim into each
            // MaterialCommands.Xxx.
            ["material.apply"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["create"] = new[] { "material.create" },
                ["setProperty"] = new[] { "material.set_property" },
                ["assign"] = new[] { "material.assign" },
                ["duplicate"] = new[] { "material.duplicate" },
                ["swapShader"] = new[] { "material.swap_shader" },
            },

            // AnimationApplyCommands.DispatchOne - assignController/assignClip forward verbatim;
            // createController/editController both go through ControllerPathToPath first (context
            // "animation_apply", field "controllerPath") - see this class's own doc comment.
            ["animation.apply"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["assignController"] = new[] { "animation.assign_controller" },
                ["assignClip"] = new[] { "animation.assign_clip" },
                ["createController"] = new[] { "animation_apply" },
                ["editController"] = new[] { "animation_apply" },
            },

            // AssetManageCommands.DispatchOne - "move"/"import" forward verbatim; "refresh" needs no
            // fields (AssetDatabase.Refresh() takes none).
            ["asset.manage"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["move"] = new[] { "asset.move" },
                ["import"] = new[] { "asset.import" },
                ["refresh"] = Array.Empty<string>(),
            },

            // SceneManageCommands.DispatchOne - "save"/"create"/"open"/"duplicate" forward verbatim;
            // "save"'s 'path' is genuinely optional (SceneManagementCommands.SaveScene falls back to
            // the active scene's own path when omitted).
            ["scene.manage"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["save"] = Array.Empty<string>(),
                ["create"] = new[] { "scene.create" },
                ["open"] = new[] { "scene.open" },
                ["duplicate"] = new[] { "scene.duplicate" },
            },

            // ProjectSettingsApplyCommands.DispatchOne - every op forwards verbatim into
            // TagLayerCommands/SceneManagementCommands/AssetCommands. "setBuildScenes" needs no
            // RequireString field (its 'scenes' requirement is a non-empty-array check - see
            // NonEmptyCollectionFields below, keyed directly by op name since there is no RequireXxx
            // context to hang it off).
            ["projectSettings.apply"] = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["createTag"] = new[] { "tag.create" },
                ["deleteTag"] = new[] { "tag.delete" },
                ["createLayer"] = new[] { "layer.create" },
                ["setBuildScenes"] = Array.Empty<string>(),
                ["setImportSettings"] = new[] { "asset.set_import_settings" },
                ["setClipImportSettings"] = new[] { "asset.set_clip_import_settings" },
            },
        };

    /// <summary>Required fields JsonParams.RequireString/RequireInt cannot see: a non-empty OBJECT
    /// or ARRAY parameter the plugin checks with an inline <c>== null || ... Count == 0</c> instead
    /// (e.g. SceneApplyCommands.DoSetProperties: <c>"... needs a non-empty 'values' object."</c>).
    /// Keyed by PLUGIN CONTEXT where one exists, so it composes with OpContexts above; small and
    /// stable - it only grows when a new op adds a nested-collection parameter, not on an ordinary
    /// field rename (RequireString/RequireInt already covers those, live, via PluginRequiredFields).
    /// Verified against source, file:line noted per entry, 2026-08-04.</summary>
    static readonly IReadOnlyDictionary<string, string> NonEmptyCollectionFieldsByContext =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // SceneApplyCommands.DoSetProperties (SceneApplyCommands.cs): "scene_apply
            // setProperties needs a non-empty 'values' object."
            ["scene_apply setProperties"] = "values",
            // AssetCommands.DoSetImportSettings (AssetCommands.cs): "asset.set_import_settings
            // requires a non-empty 'properties' object parameter."
            ["asset.set_import_settings"] = "properties",
            // AssetCommands.DoSetClipImportSettings (AssetCommands.cs): "asset.set_clip_import_settings
            // requires a non-empty 'clips' array parameter."
            ["asset.set_clip_import_settings"] = "clips",
        };

    /// <summary>Same idea as <see cref="NonEmptyCollectionFieldsByContext"/>, but keyed directly by
    /// op name for the one op with no RequireXxx context of its own to hang the field off:
    /// ProjectSettingsApplyCommands' "setBuildScenes" forwards straight into
    /// SceneManagementCommands.SetBuildScenes (SceneManagementCommands.cs), whose own check is
    /// <c>"scene.set_build requires a 'scenes' array parameter."</c> - an inline check, not a
    /// JsonParams.RequireString/RequireInt call, so it never gets its own context string at all.</summary>
    static readonly IReadOnlyDictionary<string, string> NonEmptyCollectionFieldsByOp =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["setBuildScenes"] = "scenes",
        };

    /// <summary>Asserts that <paramref name="opJson"/> - one entry of a batch wire call's
    /// 'operations' array, exactly as the app is about to send it - carries every field the plugin
    /// actually requires for <paramref name="op"/> under <paramref name="wireMethod"/>. Silently
    /// returns for a wireMethod this mechanism does not cover (anything other than the 7 consolidated
    /// *_apply/*_manage tools), so it is safe to call unconditionally from the shared fake-Unity
    /// responder for every wire call any test happens to make.</summary>
    public static void AssertOperationSatisfiesPluginContract(string? wireMethod, string op, JsonValue opJson)
    {
        if (wireMethod == null || !OpContexts.TryGetValue(wireMethod, out var opMap)) return;

        if (!opMap.TryGetValue(op, out var contexts))
        {
            throw new InvalidOperationException(
                $"PluginWireContract has no entry for {wireMethod} op '{op}' - the tool's own ValidOps "
                + "just accepted this op (or a test sent it directly), so OpContexts in "
                + "PluginWireContract.cs is missing an entry, not that the op is genuinely invalid. "
                + "Add one alongside its siblings.");
        }

        foreach (var context in contexts)
        {
            foreach (var field in PluginRequiredFields.RequiredFieldsFor(context))
            {
                if (!HasNonBlankValue(opJson, field))
                {
                    throw new InvalidOperationException(
                        $"{wireMethod} op '{op}': plugin context '{context}' requires field '{field}' "
                        + "(JsonParams.RequireString/RequireInt in Plugin~/Assets/Hades/Tools, parsed live "
                        + $"from source), but the wire operation object about to be sent has no non-blank "
                        + $"'{field}': {opJson}");
                }
            }

            if (NonEmptyCollectionFieldsByContext.TryGetValue(context, out var contextField)
                && !HasNonEmptyCollection(opJson, contextField))
            {
                throw new InvalidOperationException(
                    $"{wireMethod} op '{op}': plugin context '{context}' requires a non-empty "
                    + $"'{contextField}' array/object, but the wire operation object about to be sent "
                    + $"does not have one: {opJson}");
            }
        }

        if (NonEmptyCollectionFieldsByOp.TryGetValue(op, out var opField) && !HasNonEmptyCollection(opJson, opField))
        {
            throw new InvalidOperationException(
                $"{wireMethod} op '{op}': the plugin requires a non-empty '{opField}' array/object, but "
                + $"the wire operation object about to be sent does not have one: {opJson}");
        }
    }

    static bool HasNonBlankValue(JsonValue obj, string field)
    {
        if (!obj.TryGetProperty(field, out var value) || value is null) return false;
        return value.Kind switch
        {
            WireKind.String => !string.IsNullOrEmpty(value.AsString()),
            WireKind.Null => false,
            _ => true,
        };
    }

    static bool HasNonEmptyCollection(JsonValue obj, string field)
    {
        if (!obj.TryGetProperty(field, out var value) || value is null) return false;
        return (value.Kind == WireKind.Array && value.Items.Count > 0)
            || (value.Kind == WireKind.Object && value.Members.Count > 0);
    }
}
