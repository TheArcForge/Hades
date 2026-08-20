using Hades.Contract.Wire;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Tests;

/// <summary>
/// Direct, no-Editor-needed tests of the contract-test mechanism itself (PluginRequiredFields.cs +
/// PluginWireContract.cs), which EditorToolTestBase wires into every *_apply/*_manage test's shared
/// fake-Unity responder. Three concerns, each its own section below:
///
/// 1. The live parser actually reads UnityPlugin's current source correctly (spot checks against
///    contexts this task hand-verified by reading the plugin files directly).
/// 2. The structural map (PluginWireContract.OpContexts) is complete and self-consistent: every op
///    every one of the 7 consolidated tools' own ValidOps accepts has a resolvable entry, and a
///    fully-fielded operation for every single one passes.
/// 3. The property this whole mechanism exists for: a payload missing a field the plugin actually
///    requires is REJECTED, and - the harder direction - a PLUGIN-side rename is picked up
///    automatically (not hand-copied), so a now-stale app payload starts failing with no test-code
///    change. This is "the test that fails if a field is renamed on either side" the task asks for.
/// </summary>
public class PluginWireContractTests
{
    static JsonValue Obj(params (string Key, JsonValue Value)[] members)
    {
        var o = JsonValue.NewObject();
        foreach (var (key, value) in members) o.SetProperty(key, value);
        return o;
    }

    // ==================================================================== 1. the parser reads real UnityPlugin source correctly

    [Fact]
    public void Parser_FindsThePluginToolsDirectory()
    {
        var dir = PluginRequiredFields.FindPluginToolsDirectory();
        Assert.NotNull(dir);
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir!, "MaterialCommands.cs")));
    }

    [Theory]
    // The exact bug this task was dispatched to fix, and its own live error messages:
    // "'material.set_property' requires a non-empty string 'materialPath' parameter."
    // "'material.set_property' requires a non-empty string 'propertyName' parameter."
    [InlineData("material.set_property", new[] { "materialPath", "propertyName" })]
    [InlineData("material.create", new[] { "path" })]
    [InlineData("material.assign", new[] { "gameObjectPath", "materialPath" })]
    [InlineData("material.duplicate", new[] { "sourcePath", "destPath" })]
    [InlineData("material.swap_shader", new[] { "materialPath", "shader" })]
    [InlineData("prefab.create", new[] { "gameObjectPath", "assetPath" })]
    [InlineData("prefab.instantiate", new[] { "prefabPath" })]
    [InlineData("prefab.apply_overrides", new[] { "gameObjectPath" })]
    [InlineData("prefab.edit_property", new[] { "prefabPath", "componentType", "propertyName" })]
    [InlineData("prefab.create_variant", new[] { "basePrefabPath", "variantPath" })]
    [InlineData("animation.assign_controller", new[] { "gameObjectPath", "controllerPath" })]
    [InlineData("animation.assign_clip", new[] { "controllerPath", "stateName", "clipPath" })]
    [InlineData("animation_apply", new[] { "controllerPath" })]
    [InlineData("asset.move", new[] { "sourcePath", "destPath" })]
    [InlineData("asset.import", new[] { "path" })]
    [InlineData("scene.create", new[] { "path" })]
    [InlineData("scene.open", new[] { "path" })]
    [InlineData("scene.duplicate", new[] { "sourcePath", "destPath" })]
    [InlineData("tag.create", new[] { "name" })]
    [InlineData("tag.delete", new[] { "name" })]
    [InlineData("layer.create", new[] { "name" })]
    [InlineData("asset.set_import_settings", new[] { "path" })]
    [InlineData("asset.set_clip_import_settings", new[] { "path" })]
    [InlineData("scene_apply create", new[] { "name" })]
    [InlineData("scene_apply addComponent", new[] { "target", "type" })]
    [InlineData("scene_apply removeComponent", new[] { "target", "type" })]
    [InlineData("scene_apply setProperties", new[] { "target", "component" })]
    [InlineData("scene_apply setReference", new[] { "target", "component", "property" })]
    [InlineData("scene_apply addListener", new[] { "target", "component", "event", "targetObject", "method" })]
    [InlineData("scene_apply removeListener", new[] { "target", "component", "event", "index" })]
    [InlineData("scene_apply delete", new[] { "target" })]
    [InlineData("scene_apply reparent", new[] { "target" })]
    [InlineData("scene_apply rename", new[] { "target", "newName" })]
    [InlineData("scene_apply select", new[] { "target" })]
    public void Parser_FindsExactlyTheRequiredFieldsThisTaskVerifiedByReadingPluginSource(string context, string[] expectedFields)
    {
        var actual = PluginRequiredFields.RequiredFieldsFor(context);
        Assert.Equal(expectedFields.OrderBy(f => f, StringComparer.Ordinal), actual.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Fact]
    public void Parser_UnknownContext_ThrowsActionablyRatherThanReturningEmpty()
    {
        var ex = Record.Exception(() => PluginRequiredFields.RequiredFieldsFor("material.frobnicate"));
        Assert.NotNull(ex);
        Assert.Contains("material.frobnicate", ex.Message);
    }

    // ==================================================================== 2. the structural map is complete and self-consistent

    public static IEnumerable<object[]> EveryToolOp()
    {
        // Mirrors each tool's own ValidOps array verbatim (SceneApplyTool, PrefabApplyTool,
        // MaterialApplyTool, AnimationApplyTool, AssetManageTool, SceneManageTool,
        // ProjectSettingsApplyTool) - if one of these ever adds an op without updating
        // PluginWireContract.OpContexts, BuildAFullyFieldedOperation below throws immediately.
        (string Method, string[] Ops)[] tools =
        {
            ("scene.apply", new[] { "create", "addComponent", "setProperties", "setReference", "removeComponent", "addListener", "removeListener", "delete", "reparent", "rename", "select" }),
            ("prefab.apply", new[] { "create", "instantiate", "applyOverrides", "editProperty", "createVariant" }),
            ("material.apply", new[] { "create", "setProperty", "assign", "duplicate", "swapShader" }),
            ("animation.apply", new[] { "assignController", "assignClip", "createController", "editController" }),
            ("asset.manage", new[] { "move", "import", "refresh" }),
            ("scene.manage", new[] { "save", "create", "open", "duplicate" }),
            ("projectSettings.apply", new[] { "createTag", "deleteTag", "createLayer", "setBuildScenes", "setImportSettings", "setClipImportSettings" }),
        };

        foreach (var (method, ops) in tools)
            foreach (var op in ops)
                yield return new object[] { method, op };
    }

    [Theory]
    [MemberData(nameof(EveryToolOp))]
    public void EveryOpEveryTool_AFullyFieldedOperation_SatisfiesThePluginContract(string wireMethod, string op)
    {
        // Builds the minimum operation object that OUGHT to satisfy the plugin (every required
        // string field a placeholder, every required collection field one element) and asserts
        // AssertOperationSatisfiesPluginContract raises nothing - proving OpContexts has a working
        // entry for every op every tool actually exposes, not just the ones spot-checked above.
        var opJson = BuildAFullyFieldedOperation(wireMethod, op);

        var ex = Record.Exception(() => PluginWireContract.AssertOperationSatisfiesPluginContract(wireMethod, op, opJson));

        Assert.True(ex is null, $"{wireMethod} op '{op}': expected a fully-fielded operation to satisfy the "
            + $"plugin contract, but got: {ex}\nSent: {opJson}");
    }

    static JsonValue BuildAFullyFieldedOperation(string wireMethod, string op)
    {
        var o = Obj(("op", JsonValue.String(op)));

        // Same lookups PluginWireContract itself uses internally, via its own public entry point -
        // reflection-free: read back through RequiredFieldsFor for whatever context(s) apply, using
        // a tiny local mirror of OpContexts' structure so this test does not need OpContexts made
        // public. Kept in sync with PluginWireContract.OpContexts by the Theory data above (an
        // unmapped op fails via the InvalidOperationException PluginWireContract itself throws).
        foreach (var context in ContextsFor(wireMethod, op))
        {
            foreach (var field in PluginRequiredFields.RequiredFieldsFor(context))
                if (!o.TryGetProperty(field, out _)) o.SetProperty(field, JsonValue.String("placeholder"));
        }

        // The handful of non-RequireString collection fields PluginWireContract also checks.
        var collectionField = op switch
        {
            "setProperties" when wireMethod == "scene.apply" => "values",
            "setImportSettings" when wireMethod == "projectSettings.apply" => "properties",
            "setClipImportSettings" when wireMethod == "projectSettings.apply" => "clips",
            "setBuildScenes" when wireMethod == "projectSettings.apply" => "scenes",
            _ => null,
        };
        if (collectionField != null)
            o.SetProperty(collectionField, JsonValue.NewArray().Add(JsonValue.String("placeholder")));

        return o;
    }

    /// <summary>Local mirror of PluginWireContract.OpContexts, used only to build a fully-fielded
    /// test fixture above - deliberately duplicated rather than exposing OpContexts publicly, so
    /// this file exercises the SAME contract through PluginWireContract's own public surface
    /// (AssertOperationSatisfiesPluginContract) a real caller (EditorToolTestBase) uses, not its
    /// internals directly.</summary>
    static string[] ContextsFor(string wireMethod, string op) => (wireMethod, op) switch
    {
        ("scene.apply", "create") => new[] { "scene_apply create" },
        ("scene.apply", "addComponent") => new[] { "scene_apply addComponent" },
        ("scene.apply", "removeComponent") => new[] { "scene_apply removeComponent" },
        ("scene.apply", "setProperties") => new[] { "scene_apply setProperties" },
        ("scene.apply", "setReference") => new[] { "scene_apply setReference" },
        ("scene.apply", "addListener") => new[] { "scene_apply addListener" },
        ("scene.apply", "removeListener") => new[] { "scene_apply removeListener" },
        ("scene.apply", "delete") => new[] { "scene_apply delete" },
        ("scene.apply", "reparent") => new[] { "scene_apply reparent" },
        ("scene.apply", "rename") => new[] { "scene_apply rename" },
        ("scene.apply", "select") => new[] { "scene_apply select" },
        ("prefab.apply", "create") => new[] { "prefab.create" },
        ("prefab.apply", "instantiate") => new[] { "prefab.instantiate" },
        ("prefab.apply", "applyOverrides") => new[] { "prefab.apply_overrides" },
        ("prefab.apply", "editProperty") => new[] { "prefab.edit_property" },
        ("prefab.apply", "createVariant") => new[] { "prefab.create_variant" },
        ("material.apply", "create") => new[] { "material.create" },
        ("material.apply", "setProperty") => new[] { "material.set_property" },
        ("material.apply", "assign") => new[] { "material.assign" },
        ("material.apply", "duplicate") => new[] { "material.duplicate" },
        ("material.apply", "swapShader") => new[] { "material.swap_shader" },
        ("animation.apply", "assignController") => new[] { "animation.assign_controller" },
        ("animation.apply", "assignClip") => new[] { "animation.assign_clip" },
        ("animation.apply", "createController") => new[] { "animation_apply" },
        ("animation.apply", "editController") => new[] { "animation_apply" },
        ("asset.manage", "move") => new[] { "asset.move" },
        ("asset.manage", "import") => new[] { "asset.import" },
        ("asset.manage", "refresh") => Array.Empty<string>(),
        ("scene.manage", "save") => Array.Empty<string>(),
        ("scene.manage", "create") => new[] { "scene.create" },
        ("scene.manage", "open") => new[] { "scene.open" },
        ("scene.manage", "duplicate") => new[] { "scene.duplicate" },
        ("projectSettings.apply", "createTag") => new[] { "tag.create" },
        ("projectSettings.apply", "deleteTag") => new[] { "tag.delete" },
        ("projectSettings.apply", "createLayer") => new[] { "layer.create" },
        ("projectSettings.apply", "setBuildScenes") => Array.Empty<string>(),
        ("projectSettings.apply", "setImportSettings") => new[] { "asset.set_import_settings" },
        ("projectSettings.apply", "setClipImportSettings") => new[] { "asset.set_clip_import_settings" },
        _ => throw new InvalidOperationException($"ContextsFor: no fixture mapping for {wireMethod} op '{op}' - "
            + "add one alongside PluginWireContract.OpContexts's own entry."),
    };

    // ==================================================================== 3. the property this mechanism exists for

    /// <summary>Direct reproduction of the live defect this whole mechanism exists to prevent: the
    /// app sends 'property' where the plugin's material.set_property requires 'propertyName'
    /// ("'material.set_property' requires a non-empty string 'propertyName' parameter." - the exact
    /// second error from the live-Editor run). Proves AssertOperationSatisfiesPluginContract - the
    /// same call EditorToolTestBase makes for every wire call any *_apply/*_manage test sends -
    /// rejects a payload missing a field the CURRENT, real plugin requires.</summary>
    [Fact]
    public void AssertOperationSatisfiesPluginContract_AppSendsWrongFieldName_FailsNamingTheMissingField()
    {
        var opJson = Obj(
            ("op", JsonValue.String("setProperty")),
            ("materialPath", JsonValue.String("Assets/Foo.mat")),
            ("property", JsonValue.String("_Color"))); // WRONG: plugin requires 'propertyName', not 'property'.

        var ex = Record.Exception(() =>
            PluginWireContract.AssertOperationSatisfiesPluginContract("material.apply", "setProperty", opJson));

        Assert.NotNull(ex);
        Assert.Contains("propertyName", ex!.Message);
        Assert.Contains("material.set_property", ex.Message);
    }

    /// <summary>Same property, the OTHER field on the SAME op: reproduces the first of the two live
    /// errors ("... requires a non-empty string 'materialPath' parameter.") by omitting it entirely
    /// (as opposed to misnaming it) - both this test and the one above must fail independently, the
    /// same way the live run hit them one after another.</summary>
    [Fact]
    public void AssertOperationSatisfiesPluginContract_AppOmitsARequiredField_FailsNamingIt()
    {
        var opJson = Obj(
            ("op", JsonValue.String("setProperty")),
            ("propertyName", JsonValue.String("_Color")),
            ("value", JsonValue.Integer(1)));
        // 'materialPath' is entirely absent.

        var ex = Record.Exception(() =>
            PluginWireContract.AssertOperationSatisfiesPluginContract("material.apply", "setProperty", opJson));

        Assert.NotNull(ex);
        Assert.Contains("materialPath", ex!.Message);
    }

    /// <summary>The harder direction: a PLUGIN-side rename. Points the SAME parser
    /// (PluginRequiredFields.Parse) at a small, hand-written scratch "Tools" directory rather than
    /// the real UnityPlugin - proving the table is read from source TEXT, not hand-copied, so a real
    /// rename in UnityPlugin/Assets/Hades/Tools would flow through automatically the next time this
    /// suite runs, with zero test-project changes. The scratch fixture below mirrors
    /// MaterialCommands.SetProperty's own shape (same context, same first required field) but
    /// renames 'propertyName' -&gt; 'shaderPropertyName', simulating exactly the kind of edit that
    /// would otherwise silently strand every app-side caller still sending the old name.</summary>
    [Fact]
    public void Parse_ReflectsAPluginRename_NotAHandCopiedSnapshot()
    {
        var scratchDir = Directory.CreateTempSubdirectory("hades-plugin-contract-test-");
        try
        {
            File.WriteAllText(Path.Combine(scratchDir.FullName, "FakeMaterialCommands.cs"),
                """
                namespace Hades.Tools
                {
                    internal static class FakeMaterialCommands
                    {
                        internal static JsonValue SetProperty(ReloadGate gate, JsonValue @params)
                        {
                            var materialPath = JsonParams.RequireString(@params, "materialPath", "material.set_property");
                            // Renamed on the plugin side: 'propertyName' -> 'shaderPropertyName'.
                            var propertyName = JsonParams.RequireString(@params, "shaderPropertyName", "material.set_property");
                            return null;
                        }
                    }
                }
                """);

            var parsed = PluginRequiredFields.Parse(scratchDir.FullName);

            // 1. The parser picked up the NEW name, live, from source text alone.
            Assert.Equal(
                new[] { "materialPath", "shaderPropertyName" },
                parsed["material.set_property"].OrderBy(f => f, StringComparer.Ordinal));

            // 2. The app's own EXISTING payload (still sending the pre-rename 'propertyName', exactly
            // what a real app tool would still be sending immediately after this hypothetical plugin
            // edit, before anyone updated the app to match) no longer satisfies the renamed contract -
            // reproduced here directly against the newly-parsed table, the same field-presence check
            // AssertOperationSatisfiesPluginContract performs against the real one.
            var staleAppPayload = Obj(
                ("op", JsonValue.String("setProperty")),
                ("materialPath", JsonValue.String("Assets/Foo.mat")),
                ("propertyName", JsonValue.String("_Color")));

            var stillSatisfied = parsed["material.set_property"]
                .All(field => staleAppPayload.TryGetProperty(field, out var v) && v is { Kind: WireKind.String } && !string.IsNullOrEmpty(v.AsString()));

            Assert.False(stillSatisfied,
                "a payload built against the OLD field name must not satisfy a table parsed from the "
                + "RENAMED plugin source - if this assertion fails, the parser is (wrongly) still "
                + "reporting the pre-rename field set, i.e. it is reading a stale/cached table rather "
                + "than the scratch fixture's own source text.");
        }
        finally
        {
            scratchDir.Delete(recursive: true);
        }
    }

    /// <summary>A missing 'const string ctx' in scope is a parser-fixture problem, not a silently
    /// empty required-fields list - proves the ctx-variable indirection path (used by
    /// SceneApplyCommands.cs's own per-op contexts) fails loudly rather than swallowing the call.</summary>
    [Fact]
    public void Parse_CtxVariableWithNoPrecedingDeclaration_ThrowsRatherThanSilentlyDroppingTheCall()
    {
        var scratchDir = Directory.CreateTempSubdirectory("hades-plugin-contract-test-");
        try
        {
            File.WriteAllText(Path.Combine(scratchDir.FullName, "Broken.cs"),
                """
                namespace Hades.Tools
                {
                    internal static class Broken
                    {
                        internal static void DoThing(JsonValue op)
                        {
                            var target = JsonParams.RequireString(op, "target", ctx);
                        }
                    }
                }
                """);

            var ex = Record.Exception(() => PluginRequiredFields.Parse(scratchDir.FullName));

            Assert.NotNull(ex);
            Assert.Contains("target", ex!.Message);
        }
        finally
        {
            scratchDir.Delete(recursive: true);
        }
    }
}
