using System.Text.Json;
using Hades.Contract.Wire;
using Microsoft.AspNetCore.Mvc.Testing;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Tests;

/// <summary>
/// material_apply: the declarative batch that replaces EditorMaterialTools' five tools
/// (material_create, material_set_property, material_assign, material_duplicate,
/// material_swap_shader). Same scope discipline as SceneApplyTests: this proves the tool-to-wire
/// contract (one wire call, every operation's fields mapped onto the wire key its own underlying
/// plugin command expects - including the caller-facing 'path' field, which BuildOperation renames
/// per-op to 'path'/'materialPath'/'destPath' to match the plugin's own unchanged wire vocabulary -
/// applied/results/failed/summary mapped back), not Undo/no-lease/real ordering, which are
/// plugin-side properties proven against a real Editor in
/// UnityPlugin/Tests/Editor/MaterialApplyCommandsTests.cs.
/// </summary>
public sealed class MaterialApplyTests(WebApplicationFactory<Program> factory) : EditorToolTestBase(factory)
{
    static JsonValue Obj(params (string Key, JsonValue Value)[] members)
    {
        var o = JsonValue.NewObject();
        foreach (var (key, value) in members) o.SetProperty(key, value);
        return o;
    }

    static JsonValue Prop(JsonValue obj, string key)
    {
        Assert.True(obj.TryGetProperty(key, out var value), $"expected wire param '{key}', got: {obj}");
        return value!;
    }

    // ---------------------------------------------------------------- structural validation (no Editor needed)

    [Fact]
    public async Task MaterialApply_EmptyOperationsArray_FailsLocally_NoEditorNeeded()
    {
        var envelope = await McpTestClient.CallTool(Factory, "material_apply", new { operations = Array.Empty<object>() });

        Assert.Contains("operations", McpTestClient.ErrorText(envelope));
    }

    [Fact]
    public async Task MaterialApply_UnknownOp_RejectsWholeCallBeforeDispatchingAnything_ListsValidOps()
    {
        var envelope = await McpTestClient.CallTool(Factory, "material_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["path"] = "Assets/Foo.mat" },
                new Dictionary<string, object> { ["op"] = "frobnicate", ["path"] = "Assets/Foo.mat" },
            },
        });

        var text = McpTestClient.ErrorText(envelope);
        Assert.Contains("frobnicate", text);
        Assert.Contains("operations[1]", text);
        foreach (var op in new[] { "create", "setProperty", "assign", "duplicate", "swapShader" })
            Assert.Contains(op, text);
    }

    // ---------------------------------------------------------------- unknown FIELD refused before any wire call (per-op, not per-tool)

    /// <summary>Direct reproduction of a live-Editor defect: a caller sent 'property' where this
    /// record declares only 'propertyName' - System.Text.Json's default UnmappedMemberHandling.Skip
    /// silently dropped it, the call proceeded with no 'propertyName' on the wire, and the PLUGIN
    /// reported "'material.set_property' requires a non-empty string 'propertyName' parameter" - an
    /// error that reads as an app-to-plugin mapping bug when it was actually a caller typo the app
    /// should have refused by name. See OperationFieldValidator's own doc comment
    /// (OperationFieldValidation.cs) for the mechanism that now catches this before any wire call.</summary>
    [Fact]
    public async Task MaterialApply_UnknownField_LiveRepro_PropertyInsteadOfPropertyName_RejectsWholeCall_PropertyNameStillWorks()
    {
        // 'property' is refused before any wire call - no fake Unity connects at all, matching
        // MaterialApply_UnknownOp's own "zero wire calls" proof.
        var badEnvelope = await McpTestClient.CallTool(Factory, "material_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "setProperty", ["path"] = "Assets/Foo.mat", ["property"] = "_Metallic", ["value"] = 0.5 },
            },
        });

        var text = McpTestClient.ErrorText(badEnvelope);
        Assert.Contains("operations[0]", text);
        Assert.Contains("'property'", text);
        Assert.Contains("Fields 'setProperty' accepts: op, path, propertyName, value.", text);

        // The correct field, 'propertyName', still works end-to-end.
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray().Add(JsonValue.Integer(0))),
            ("results", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(0)), ("op", JsonValue.String("setProperty")),
                ("result", Obj(("materialPath", JsonValue.String("Assets/Foo.mat")), ("property", JsonValue.String("_Metallic")), ("newValue", JsonValue.Float(0.5))))))),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("1 applied, 0 failed of 1 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "material_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "setProperty", ["path"] = "Assets/Foo.mat", ["propertyName"] = "_Metallic", ["value"] = 0.5 },
            },
        }));
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, structured.GetProperty("failed").GetArrayLength());
        Assert.Single(structured.GetProperty("applied").EnumerateArray());
    }

    /// <summary>Field validity is PER-OP, not per-tool: 'sourcePath' is duplicate-only, so sending
    /// it alongside setProperty (a DIFFERENT op in the SAME flat record) must be refused exactly
    /// like a field the record does not declare at all - accepting it would miss exactly the typo
    /// class this mechanism exists to catch (e.g. a 'sourcePath' left over from a duplicate op
    /// copy-pasted earlier in the same spec).</summary>
    [Fact]
    public async Task MaterialApply_FieldFromSiblingOp_IsRejected_NotSilentlyAccepted()
    {
        var envelope = await McpTestClient.CallTool(Factory, "material_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object>
                {
                    ["op"] = "setProperty", ["path"] = "Assets/Foo.mat", ["propertyName"] = "_Metallic", ["value"] = 0.5,
                    ["sourcePath"] = "Assets/Other.mat", // duplicate-only
                },
            },
        });

        var text = McpTestClient.ErrorText(envelope);
        Assert.Contains("operations[0]", text);
        Assert.Contains("'sourcePath'", text);
        Assert.Contains("Fields 'setProperty' accepts: op, path, propertyName, value.", text);
    }

    /// <summary>Enumerates EVERY op material_apply accepts (not a spot check) and proves each one,
    /// individually, refuses an unknown field before any wire call.</summary>
    [Fact]
    public async Task MaterialApply_UnknownField_RejectedForEveryOp()
    {
        foreach (var op in new[] { "create", "setProperty", "assign", "duplicate", "swapShader" })
        {
            var envelope = await McpTestClient.CallTool(Factory, "material_apply", new
            {
                operations = new[] { new Dictionary<string, object> { ["op"] = op, ["zzzNotAField"] = "x" } },
            });

            var text = McpTestClient.ErrorText(envelope);
            Assert.True(text.Contains("'zzzNotAField'") && text.Contains("operations[0]"),
                $"op '{op}' did not refuse an unknown field as expected. Got: {text}");
        }
    }

    // ---------------------------------------------------------------- one wire call, every field translated verbatim

    [Fact]
    public async Task MaterialApply_FullOperationSweep_SendsOneWireCallWithEveryFieldTranslated_MapsAppliedAndSummary()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var results = JsonValue.NewArray()
            .Add(Obj(("index", JsonValue.Integer(0)), ("op", JsonValue.String("create")),
                ("result", Obj(("path", JsonValue.String("Assets/Foo.mat")), ("shader", JsonValue.String("Standard")), ("guid", JsonValue.String("abc123"))))))
            .Add(Obj(("index", JsonValue.Integer(1)), ("op", JsonValue.String("setProperty")),
                ("result", Obj(("materialPath", JsonValue.String("Assets/Foo.mat")), ("property", JsonValue.String("_Color")), ("newValue", JsonValue.Integer(1))))))
            .Add(Obj(("index", JsonValue.Integer(2)), ("op", JsonValue.String("assign")),
                ("result", Obj(("gameObject", JsonValue.String("Cube")), ("renderer", JsonValue.String("MeshRenderer")), ("slot", JsonValue.Integer(0))))))
            .Add(Obj(("index", JsonValue.Integer(3)), ("op", JsonValue.String("duplicate")),
                ("result", Obj(("source", JsonValue.String("Assets/Foo.mat")), ("destination", JsonValue.String("Assets/Bar.mat")))))
            )
            .Add(Obj(("index", JsonValue.Integer(4)), ("op", JsonValue.String("swapShader")),
                ("result", Obj(
                    ("materialPath", JsonValue.String("Assets/Bar.mat")),
                    ("previousShader", JsonValue.String("Standard")),
                    ("newShader", JsonValue.String("Unlit/Color")),
                    ("survivedProperties", JsonValue.NewArray().Add(JsonValue.String("_Color"))),
                    ("lostProperties", JsonValue.NewArray().Add(JsonValue.String("_Glossiness")))))));

        var appliedAll = JsonValue.NewArray();
        for (var i = 0; i < 5; i++) appliedAll.Add(JsonValue.Integer(i));

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", appliedAll),
            ("results", results),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("5 applied, 0 failed of 5 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "material_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["path"] = "Assets/Foo.mat", ["shader"] = "Standard" },
                new Dictionary<string, object> { ["op"] = "setProperty", ["path"] = "Assets/Foo.mat", ["propertyName"] = "_Color", ["value"] = new Dictionary<string, object> { ["r"] = 1, ["g"] = 0, ["b"] = 0, ["a"] = 1 } },
                new Dictionary<string, object> { ["op"] = "assign", ["gameObjectPath"] = "Cube", ["path"] = "Assets/Foo.mat", ["slot"] = 0 },
                new Dictionary<string, object> { ["op"] = "duplicate", ["sourcePath"] = "Assets/Foo.mat", ["path"] = "Assets/Bar.mat" },
                new Dictionary<string, object> { ["op"] = "swapShader", ["path"] = "Assets/Bar.mat", ["shader"] = "Unlit/Color" },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        // ONE wire call for the whole 5-operation spec.
        Assert.Equal("material.apply", request.Method);
        var ops = Prop(request.Params!, "operations");
        Assert.Equal(5, ops.Items.Count);

        Assert.Equal("Assets/Foo.mat", Prop(ops.Items[0], "path").AsString());
        Assert.Equal("Standard", Prop(ops.Items[0], "shader").AsString());

        Assert.Equal("Assets/Foo.mat", Prop(ops.Items[1], "materialPath").AsString());
        Assert.Equal("_Color", Prop(ops.Items[1], "propertyName").AsString());
        Assert.Equal(1.0, Prop(Prop(ops.Items[1], "value"), "r").AsDouble());

        Assert.Equal("Cube", Prop(ops.Items[2], "gameObjectPath").AsString());
        Assert.Equal("Assets/Foo.mat", Prop(ops.Items[2], "materialPath").AsString());
        Assert.Equal(0, Prop(ops.Items[2], "slot").AsInteger());

        Assert.Equal("Assets/Foo.mat", Prop(ops.Items[3], "sourcePath").AsString());
        Assert.Equal("Assets/Bar.mat", Prop(ops.Items[3], "destPath").AsString());

        Assert.Equal("Assets/Bar.mat", Prop(ops.Items[4], "materialPath").AsString());
        Assert.Equal("Unlit/Color", Prop(ops.Items[4], "shader").AsString());

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(Enumerable.Range(0, 5), applied);
        Assert.Equal(0, structured.GetProperty("failed").GetArrayLength());
        Assert.Contains("5", structured.GetProperty("summary").GetString());

        // The two carried-forward findings: swapShader's survived/lost properties, verbatim in
        // 'results' - never collapsed into a bare "applied" flag.
        var resultsEl = structured.GetProperty("results");
        Assert.Equal(5, resultsEl.GetArrayLength());
        var swapResult = resultsEl[4];
        Assert.Equal(4, swapResult.GetProperty("index").GetInt32());
        Assert.Equal("swapShader", swapResult.GetProperty("op").GetString());
        var swapData = swapResult.GetProperty("result");
        Assert.Equal("_Color", swapData.GetProperty("survivedProperties")[0].GetString());
        Assert.Equal("_Glossiness", swapData.GetProperty("lostProperties")[0].GetString());
    }

    // ---------------------------------------------------------------- partial failure: mapped from the wire, no rollback

    [Fact]
    public async Task PartialFailure_WirePerOperationFailure_MapsIndexOpAndError_StillOneWireCall()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray().Add(JsonValue.Integer(0))),
            ("results", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(0)), ("op", JsonValue.String("create")),
                ("result", Obj(("path", JsonValue.String("Assets/Foo.mat")), ("shader", JsonValue.String("Standard")), ("guid", JsonValue.String("abc")))))))
            ,
            ("failed", JsonValue.NewArray().Add(Obj(
                ("index", JsonValue.Integer(1)), ("op", JsonValue.String("setProperty")),
                ("error", JsonValue.String("Property '_Bogus' not found on shader 'Standard'."))))),
            ("summary", JsonValue.String("1 applied, 1 failed of 2 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "material_apply", new
        {
            operations = new[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["path"] = "Assets/Foo.mat" },
                new Dictionary<string, object> { ["op"] = "setProperty", ["path"] = "Assets/Foo.mat", ["propertyName"] = "_Bogus", ["value"] = 1 },
            },
        }));
        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("material.apply", request.Method);
        Assert.Equal(2, Prop(request.Params!, "operations").Items.Count);

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 0 }, applied);

        var failed = structured.GetProperty("failed");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.Equal(1, failed[0].GetProperty("index").GetInt32());
        Assert.Equal("setProperty", failed[0].GetProperty("op").GetString());
        Assert.Contains("_Bogus", failed[0].GetProperty("error").GetString());

        // The unified partial-batch shape: 'results' surfaces an entry (with the op's own result
        // payload) for the APPLIED op, faithfully mapped from the wire - not just a bare index.
        var results = structured.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal(0, results[0].GetProperty("index").GetInt32());
        Assert.Equal("create", results[0].GetProperty("op").GetString());
        Assert.Equal("Assets/Foo.mat", results[0].GetProperty("result").GetProperty("path").GetString());
    }

    // ---------------------------------------------------------------- whole-call (plugin-level) failure still propagates

    [Fact]
    public async Task MaterialApply_PluginLevelError_PropagatesAsToolError()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();
        var responder = AnswerBusyProbeThenFailAsync(reads, writes, "material.apply requires an 'operations' array parameter.");

        var envelope = await McpTestClient.CallTool(Factory, "material_apply", new
        {
            operations = new[] { new Dictionary<string, object> { ["op"] = "create", ["path"] = "Assets/Foo.mat" } },
        });
        await responder.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("material.apply requires an 'operations' array parameter.", McpTestClient.ErrorText(envelope));
    }

    // ---------------------------------------------------------------- live-repro regression: 'path' must work for every op, not just create

    /// <summary>Direct reproduction of a live-Editor usability defect: a caller who successfully
    /// creates a material with 'path' then got a rejected setProperty call for using the SAME field
    /// name the tool had just accepted, because setProperty secretly wanted 'materialPath' instead
    /// ("'material.set_property' requires a non-empty string 'materialPath' parameter."). Both ops
    /// here use 'path' - the caller-facing contract this test locks in - and both must reach the
    /// wire correctly (create's own 'path', setProperty's 'materialPath') for both to succeed.</summary>
    [Fact]
    public async Task MaterialApply_LiveRepro_CreateThenSetProperty_BothUsePath_SecondOpSucceeds()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", JsonValue.NewArray().Add(JsonValue.Integer(0)).Add(JsonValue.Integer(1))),
            ("results", JsonValue.NewArray()
                .Add(Obj(("index", JsonValue.Integer(0)), ("op", JsonValue.String("create")),
                    ("result", Obj(("path", JsonValue.String("Assets/_E2E10/E2EMat.mat")), ("shader", JsonValue.String("Standard")), ("guid", JsonValue.String("abc123"))))))
                .Add(Obj(("index", JsonValue.Integer(1)), ("op", JsonValue.String("setProperty")),
                    ("result", Obj(("materialPath", JsonValue.String("Assets/_E2E10/E2EMat.mat")), ("property", JsonValue.String("_Metallic")), ("newValue", JsonValue.Float(0.5))))))),
            ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("2 applied, 0 failed of 2 operation(s)."))));

        var structured = Structured(await McpTestClient.CallTool(Factory, "material_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["path"] = "Assets/_E2E10/E2EMat.mat", ["shader"] = "Standard" },
                new Dictionary<string, object> { ["op"] = "setProperty", ["path"] = "Assets/_E2E10/E2EMat.mat", ["propertyName"] = "_Metallic", ["value"] = 0.5 },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));
        var ops = Prop(request.Params!, "operations");

        // create still gets the wire's own 'path' key.
        Assert.Equal("Assets/_E2E10/E2EMat.mat", Prop(ops.Items[0], "path").AsString());

        // setProperty must get the wire's own 'materialPath' key, translated from the caller's
        // 'path' - this is what the OLD code failed to do (it only ever read a 'materialPath'
        // field of that exact name, so a caller using 'path' here left the wire object without a
        // 'materialPath' key at all, reproducing the live error verbatim).
        Assert.Equal("Assets/_E2E10/E2EMat.mat", Prop(ops.Items[1], "materialPath").AsString());
        Assert.Equal("_Metallic", Prop(ops.Items[1], "propertyName").AsString());

        var applied = structured.GetProperty("applied").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(new[] { 0, 1 }, applied);
        Assert.Equal(0, structured.GetProperty("failed").GetArrayLength());
    }

    // ---------------------------------------------------------------- the property that broke: one field name, every op

    /// <summary>Asserts directly - not by trusting the implementation - that 'path' is accepted by
    /// EVERY op that has a material of its own (create, setProperty, assign, duplicate,
    /// swapShader), each translated to whatever wire key that op's underlying plugin command
    /// actually expects. 'sourcePath' (duplicate's second, different material) and 'gameObjectPath'
    /// (assign's scene object) are genuinely distinct concepts and keep their own names - see
    /// MaterialApplyTool's own class doc comment.</summary>
    [Fact]
    public async Task MaterialApply_PathIsAcceptedByEveryOpForTheMaterialItActsOn()
    {
        var (reads, writes) = await ConnectAsFakeUnityAsync();

        const string path = "Assets/_E2E10/Shared.mat";
        string[] opNames = ["create", "setProperty", "assign", "duplicate", "swapShader"];
        var appliedAll = JsonValue.NewArray();
        var results = JsonValue.NewArray();
        for (var i = 0; i < opNames.Length; i++)
        {
            appliedAll.Add(JsonValue.Integer(i));
            results.Add(Obj(("index", JsonValue.Integer(i)), ("op", JsonValue.String(opNames[i])), ("result", JsonValue.NewObject())));
        }

        var responder = AnswerBusyProbeThenRespondAsync(reads, writes, Obj(
            ("applied", appliedAll), ("results", results), ("failed", JsonValue.NewArray()),
            ("summary", JsonValue.String("5 applied, 0 failed of 5 operation(s)."))));

        Structured(await McpTestClient.CallTool(Factory, "material_apply", new
        {
            operations = new object[]
            {
                new Dictionary<string, object> { ["op"] = "create", ["path"] = path },
                new Dictionary<string, object> { ["op"] = "setProperty", ["path"] = path, ["propertyName"] = "_Color", ["value"] = 1 },
                new Dictionary<string, object> { ["op"] = "assign", ["gameObjectPath"] = "Cube", ["path"] = path },
                new Dictionary<string, object> { ["op"] = "duplicate", ["sourcePath"] = "Assets/_E2E10/Template.mat", ["path"] = path },
                new Dictionary<string, object> { ["op"] = "swapShader", ["path"] = path, ["shader"] = "Unlit/Color" },
            },
        }));

        var request = await responder.WaitAsync(TimeSpan.FromSeconds(5));
        var ops = Prop(request.Params!, "operations");
        Assert.Equal(5, ops.Items.Count);

        // create: 'path' -> wire 'path'
        Assert.Equal(path, Prop(ops.Items[0], "path").AsString());

        // setProperty: 'path' -> wire 'materialPath'
        Assert.Equal(path, Prop(ops.Items[1], "materialPath").AsString());
        Assert.False(ops.Items[1].TryGetProperty("path", out _));

        // assign: 'path' -> wire 'materialPath'
        Assert.Equal(path, Prop(ops.Items[2], "materialPath").AsString());
        Assert.False(ops.Items[2].TryGetProperty("path", out _));

        // duplicate: 'path' -> wire 'destPath'; 'sourcePath' stays 'sourcePath' (a second, different material)
        Assert.Equal(path, Prop(ops.Items[3], "destPath").AsString());
        Assert.Equal("Assets/_E2E10/Template.mat", Prop(ops.Items[3], "sourcePath").AsString());
        Assert.False(ops.Items[3].TryGetProperty("path", out _));

        // swapShader: 'path' -> wire 'materialPath'
        Assert.Equal(path, Prop(ops.Items[4], "materialPath").AsString());
        Assert.False(ops.Items[4].TryGetProperty("path", out _));
    }
}
