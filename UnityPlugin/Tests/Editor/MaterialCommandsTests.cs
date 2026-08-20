// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.IO;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// The five class-1 material mutations (see the "52 Editor tools" plan's operation-class table
    /// - single-tick, no reload lease), dispatched through <see cref="CommandTable.Dispatch"/> the
    /// same way every other class-1 suite does - see SceneCommandsTests' own doc comment for the
    /// three things every mutation test proves (result, Undo-revert, never-touched-lease) and for
    /// why <see cref="SceneTestFixtures.ResetScene"/> is reused rather than duplicated.
    ///
    /// Materials are ASSETS, not scene objects, so each test also owns a scratch asset folder
    /// (created fresh in SetUp, deleted in TearDown) instead of relying on the scene reset alone -
    /// a material left behind by one test must never leak into the next.
    /// </summary>
    [TestFixture]
    public sealed class MaterialCommandsTests
    {
        const string ScratchDir = "Assets/Tests/_HadesMaterialScratch";

        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            AssetDatabase.CreateFolder("Assets/Tests", "_HadesMaterialScratch");
        }

        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
        }

        static JsonRpcRequest Request(string method, JsonValue @params) =>
            new JsonRpcRequest { Id = JsonValue.Integer(1), Method = method, Params = @params };

        static (ReloadGate gate, FakeEditorLockApi fake, MainThreadPump pump) NoopGateParts()
        {
            var fake = new FakeEditorLockApi();
            var pump = new MainThreadPump();
            var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            return (gate, fake, pump);
        }

        static void AssertNeverTouchedLease(FakeEditorLockApi fake, ReloadGate gate)
        {
            Assert.AreEqual(0, fake.LockCalls, "a class-1 material mutation must never call Lock");
            Assert.AreEqual(0, fake.UnlockCalls, "a class-1 material mutation must never call Unlock");
            Assert.IsFalse(gate.IsHeld, "a class-1 material mutation must never leave a lease held");
        }

        static string StringProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        static long IntProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Integer ? v.AsInteger() : long.MinValue;

        static string AbsolutePath(string projectRelativePath) =>
            Path.Combine(Directory.GetParent(Application.dataPath).FullName, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));

        // -------------------------------------------------------------------------- material.create

        [Test]
        public void CreateMaterial_WritesAssetWithRequestedShader_NoLeaseTouched()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var path = ScratchDir + "/Foo.mat";
                var @params = JsonValue.NewObject()
                    .SetProperty("path", JsonValue.String(path))
                    .SetProperty("shader", JsonValue.String("Unlit/Color"));
                var result = CommandTable.Dispatch(gate, Request("material.create", @params));

                Assert.AreEqual(path, StringProp(result, "path"));
                Assert.AreEqual("Unlit/Color", StringProp(result, "shader"));
                Assert.IsFalse(string.IsNullOrEmpty(StringProp(result, "guid")));

                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                Assert.IsNotNull(mat);
                Assert.AreEqual("Unlit/Color", mat.shader.name);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void CreateMaterial_DefaultsToStandardShader_WhenOmitted()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var path = ScratchDir + "/DefaultShader.mat";
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));
            var result = CommandTable.Dispatch(gate, Request("material.create", @params));

            Assert.AreEqual("Standard", StringProp(result, "shader"));
        }

        [Test]
        public void CreateMaterial_UnknownShader_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("path", JsonValue.String(ScratchDir + "/Bad.mat"))
                .SetProperty("shader", JsonValue.String("NotAShader"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.create", @params)));

            StringAssert.Contains("NotAShader", ex.Message);
        }

        [Test]
        public void CreateMaterial_RegistersUndo_PerformUndoRemovesAsset()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var path = ScratchDir + "/Undoable.mat";
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));
            CommandTable.Dispatch(gate, Request("material.create", @params));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(path));

            Undo.PerformUndo();

            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(path));
        }

        // ---------------------------------------------------------- material.create - path guard (F16/F17/F20)

        [Test]
        public void CreateMaterial_TraversalPath_RefusedBeforeAnyWrite()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String("Assets/../Escaped.mat"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.create", @params)));

            StringAssert.Contains("Assets/../Escaped.mat", ex.Message);
            Assert.IsFalse(File.Exists(AbsolutePath("Escaped.mat")), "a refused traversal path must leave no file behind, inside or outside Assets/");
        }

        [Test]
        public void CreateMaterial_AbsolutePathIntoAssets_Refused()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            // A CORRECT absolute path landing inside Assets/ must still be refused - only a plain
            // project-relative form is accepted (see AssetPathGuard's own doc comment for why).
            var path = AbsolutePath(ScratchDir + "/Abs.mat");
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));

            Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.create", @params)));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(ScratchDir + "/Abs.mat"));
        }

        [Test]
        public void CreateMaterial_NonNormalizedDotSlashPath_Refused()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(ScratchDir + "/./NotNormalized.mat"));

            Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.create", @params)));
            // Not even created at the normalized equivalent - a caller must resubmit the clean form itself.
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(ScratchDir + "/NotNormalized.mat"));
        }

        [Test]
        public void CreateMaterial_PathComponentOverSafeByteLimit_Refused()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var tooLongName = new string('a', 245) + ".mat"; // 249 bytes, over the 240-byte safe bound
            var path = ScratchDir + "/" + tooLongName;
            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.create", @params)));

            StringAssert.Contains("bytes", ex.Message);
            Assert.IsFalse(File.Exists(AbsolutePath(path)));
        }

        [Test]
        public void CreateMaterial_ExistingFile_Refused_OriginalUntouched()
        {
            var path = ScratchDir + "/AlreadyThere.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), path);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject().SetProperty("path", JsonValue.String(path)).SetProperty("shader", JsonValue.String("Standard"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.create", @params)));

            StringAssert.Contains("already exists", ex.Message);
            StringAssert.Contains("material_apply", ex.Message);
            // F16's destructive half: the pre-existing file must be untouched, not silently replaced.
            Assert.AreEqual("Unlit/Color", AssetDatabase.LoadAssetAtPath<Material>(path).shader.name);
        }

        // --------------------------------------------------------------------- material.set_property

        [Test]
        public void SetProperty_Color_AppliesValue_NoLeaseTouched()
        {
            var path = ScratchDir + "/ColorTarget.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var color = JsonValue.NewObject().SetProperty("r", JsonValue.Float(1)).SetProperty("g", JsonValue.Float(0)).SetProperty("b", JsonValue.Float(0)).SetProperty("a", JsonValue.Float(1));
                var @params = JsonValue.NewObject()
                    .SetProperty("materialPath", JsonValue.String(path))
                    .SetProperty("propertyName", JsonValue.String("_Color"))
                    .SetProperty("value", color);
                var result = CommandTable.Dispatch(gate, Request("material.set_property", @params));

                Assert.AreEqual("_Color", StringProp(result, "property"));
                Assert.AreEqual(new Color(1, 0, 0, 1), mat.GetColor("_Color"));

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void SetProperty_Float_AppliesValue()
        {
            var path = ScratchDir + "/FloatTarget.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("materialPath", JsonValue.String(path))
                .SetProperty("propertyName", JsonValue.String("_Glossiness"))
                .SetProperty("value", JsonValue.Float(0.25));
            CommandTable.Dispatch(gate, Request("material.set_property", @params));

            Assert.AreEqual(0.25f, mat.GetFloat("_Glossiness"), 0.0001f);
        }

        [Test]
        public void SetProperty_UnknownProperty_ThrowsActionableErrorListingValidOnes()
        {
            var path = ScratchDir + "/UnknownProp.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("materialPath", JsonValue.String(path))
                .SetProperty("propertyName", JsonValue.String("_NoSuchProperty"))
                .SetProperty("value", JsonValue.Float(1));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.set_property", @params)));

            StringAssert.Contains("_NoSuchProperty", ex.Message);
            StringAssert.Contains("_Color", ex.Message);
        }

        [Test]
        public void SetProperty_UnknownMaterial_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("materialPath", JsonValue.String(ScratchDir + "/Ghost.mat"))
                .SetProperty("propertyName", JsonValue.String("_Color"))
                .SetProperty("value", JsonValue.Float(1));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.set_property", @params)));

            StringAssert.Contains("Ghost.mat", ex.Message);
        }

        [Test]
        public void SetProperty_RegistersUndo_PerformUndoRestoresPriorValue()
        {
            var path = ScratchDir + "/UndoableProp.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var original = mat.GetColor("_Color");

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var color = JsonValue.NewObject().SetProperty("r", JsonValue.Float(0)).SetProperty("g", JsonValue.Float(1)).SetProperty("b", JsonValue.Float(0)).SetProperty("a", JsonValue.Float(1));
            var @params = JsonValue.NewObject()
                .SetProperty("materialPath", JsonValue.String(path))
                .SetProperty("propertyName", JsonValue.String("_Color"))
                .SetProperty("value", color);
            CommandTable.Dispatch(gate, Request("material.set_property", @params));
            Assert.AreEqual(new Color(0, 1, 0, 1), mat.GetColor("_Color"));

            Undo.PerformUndo();

            Assert.AreEqual(original, mat.GetColor("_Color"));
        }

        // -------------------------------------------------------------------------- material.assign

        [Test]
        public void AssignMaterial_SetsRendererSlot_NoLeaseTouched()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Target";
            var path = ScratchDir + "/Assigned.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("Target"))
                    .SetProperty("materialPath", JsonValue.String(path));
                var result = CommandTable.Dispatch(gate, Request("material.assign", @params));

                Assert.AreEqual(0L, IntProp(result, "slot"));
                Assert.AreEqual(path, StringProp(result, "materialPath"));
                Assert.AreEqual("Assigned", go.GetComponent<Renderer>().sharedMaterial.name);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void AssignMaterial_NoRendererOnTarget_ThrowsActionableError()
        {
            new GameObject("Bare");
            var path = ScratchDir + "/Assigned2.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Bare"))
                .SetProperty("materialPath", JsonValue.String(path));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.assign", @params)));

            StringAssert.Contains("Renderer", ex.Message);
        }

        [Test]
        public void AssignMaterial_RegistersUndo_PerformUndoRestoresPriorMaterial()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Target";
            var originalMat = go.GetComponent<Renderer>().sharedMaterial;
            var path = ScratchDir + "/NewAssigned.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject()
                .SetProperty("gameObjectPath", JsonValue.String("Target"))
                .SetProperty("materialPath", JsonValue.String(path));
            CommandTable.Dispatch(gate, Request("material.assign", @params));
            Assert.AreEqual("NewAssigned", go.GetComponent<Renderer>().sharedMaterial.name);

            Undo.PerformUndo();

            Assert.AreEqual(originalMat, go.GetComponent<Renderer>().sharedMaterial);
        }

        // ----------------------------------------------------------------------- material.duplicate

        [Test]
        public void DuplicateMaterial_CopiesAsset_NoLeaseTouched()
        {
            var sourcePath = ScratchDir + "/Source.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), sourcePath);
            var destPath = ScratchDir + "/Dest.mat";

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("sourcePath", JsonValue.String(sourcePath))
                    .SetProperty("destPath", JsonValue.String(destPath));
                var result = CommandTable.Dispatch(gate, Request("material.duplicate", @params));

                Assert.AreEqual(sourcePath, StringProp(result, "source"));
                Assert.AreEqual(destPath, StringProp(result, "destination"));

                var duplicated = AssetDatabase.LoadAssetAtPath<Material>(destPath);
                Assert.IsNotNull(duplicated);
                Assert.AreEqual("Unlit/Color", duplicated.shader.name);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void DuplicateMaterial_UnknownSource_ThrowsActionableError()
        {
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("sourcePath", JsonValue.String(ScratchDir + "/Ghost.mat"))
                .SetProperty("destPath", JsonValue.String(ScratchDir + "/Dest2.mat"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.duplicate", @params)));

            StringAssert.Contains("Ghost.mat", ex.Message);
        }

        [Test]
        public void DuplicateMaterial_RegistersUndo_PerformUndoRemovesAsset()
        {
            var sourcePath = ScratchDir + "/UndoSource.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), sourcePath);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var destPath = ScratchDir + "/UndoDest.mat";
            var @params = JsonValue.NewObject().SetProperty("sourcePath", JsonValue.String(sourcePath)).SetProperty("destPath", JsonValue.String(destPath));
            CommandTable.Dispatch(gate, Request("material.duplicate", @params));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(destPath));

            Undo.PerformUndo();

            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(destPath));
        }

        // -------------------------------------------------------- material.duplicate - path guard (F16/F17/F20)

        [Test]
        public void DuplicateMaterial_TraversalDestPath_RefusedBeforeAnyWrite()
        {
            var sourcePath = ScratchDir + "/TravSource.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), sourcePath);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("sourcePath", JsonValue.String(sourcePath))
                .SetProperty("destPath", JsonValue.String("Assets/../EscapedMatDup.mat"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.duplicate", @params)));

            StringAssert.Contains("EscapedMatDup.mat", ex.Message);
            Assert.IsFalse(File.Exists(AbsolutePath("EscapedMatDup.mat")), "a refused traversal path must leave no file behind, inside or outside Assets/");
        }

        [Test]
        public void DuplicateMaterial_AbsoluteDestPathIntoAssets_Refused()
        {
            var sourcePath = ScratchDir + "/AbsSource.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), sourcePath);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            // A CORRECT absolute path landing inside Assets/ must still be refused - only a plain
            // project-relative form is accepted (see AssetPathGuard's own doc comment for why).
            var destPath = AbsolutePath(ScratchDir + "/AbsDup.mat");
            var @params = JsonValue.NewObject()
                .SetProperty("sourcePath", JsonValue.String(sourcePath))
                .SetProperty("destPath", JsonValue.String(destPath));

            Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.duplicate", @params)));
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(ScratchDir + "/AbsDup.mat"));
        }

        [Test]
        public void DuplicateMaterial_NonNormalizedDotSlashDestPath_Refused()
        {
            var sourcePath = ScratchDir + "/DotSource.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), sourcePath);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("sourcePath", JsonValue.String(sourcePath))
                .SetProperty("destPath", JsonValue.String(ScratchDir + "/./NotNormalizedDup.mat"));

            Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.duplicate", @params)));
            // Not even created at the normalized equivalent - a caller must resubmit the clean form itself.
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(ScratchDir + "/NotNormalizedDup.mat"));
        }

        [Test]
        public void DuplicateMaterial_DestPathComponentOverSafeByteLimit_Refused()
        {
            var sourcePath = ScratchDir + "/LongSource.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), sourcePath);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var tooLongName = new string('a', 245) + ".mat"; // 249 bytes, over the 240-byte safe bound
            var destPath = ScratchDir + "/" + tooLongName;
            var @params = JsonValue.NewObject()
                .SetProperty("sourcePath", JsonValue.String(sourcePath))
                .SetProperty("destPath", JsonValue.String(destPath));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.duplicate", @params)));

            StringAssert.Contains("bytes", ex.Message);
            Assert.IsFalse(File.Exists(AbsolutePath(destPath)));
        }

        [Test]
        public void DuplicateMaterial_ExistingDestFile_Refused_OriginalUntouched()
        {
            var sourcePath = ScratchDir + "/ExistSource.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Unlit/Color")), sourcePath);
            var destPath = ScratchDir + "/ExistDupDest.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), destPath);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("sourcePath", JsonValue.String(sourcePath))
                .SetProperty("destPath", JsonValue.String(destPath));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.duplicate", @params)));

            StringAssert.Contains("already exists", ex.Message);
            StringAssert.Contains("material_apply", ex.Message);
            // F16's destructive half: the pre-existing file must be untouched, not silently replaced.
            Assert.AreEqual("Standard", AssetDatabase.LoadAssetAtPath<Material>(destPath).shader.name);
        }

        // --------------------------------------------------------------------- material.swap_shader

        [Test]
        public void SwapShader_ReportsSurvivedAndLostProperties_NoLeaseTouched()
        {
            var path = ScratchDir + "/SwapMe.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("materialPath", JsonValue.String(path))
                    .SetProperty("shader", JsonValue.String("Unlit/Color"));
                var result = CommandTable.Dispatch(gate, Request("material.swap_shader", @params));

                Assert.AreEqual("Standard", StringProp(result, "previousShader"));
                Assert.AreEqual("Unlit/Color", StringProp(result, "newShader"));
                Assert.AreEqual("Unlit/Color", mat.shader.name);

                Assert.IsTrue(result.TryGetProperty("survivedProperties", out var survived));
                Assert.IsTrue(result.TryGetProperty("lostProperties", out var lost));
                // _Color exists (by name and type) on both Standard and Unlit/Color - must survive.
                var survivedNames = survived.Items.Select(v => v.AsString()).ToList();
                Assert.Contains("_Color", survivedNames);
                // _Glossiness only exists on Standard - must be reported lost.
                var lostNames = lost.Items.Select(v => v.AsString()).ToList();
                Assert.Contains("_Glossiness", lostNames);

                AssertNeverTouchedLease(fake, gate);
            }
        }

        [Test]
        public void SwapShader_UnknownShader_ThrowsActionableError()
        {
            var path = ScratchDir + "/SwapBad.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            var @params = JsonValue.NewObject()
                .SetProperty("materialPath", JsonValue.String(path))
                .SetProperty("shader", JsonValue.String("NotAShader"));

            var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("material.swap_shader", @params)));

            StringAssert.Contains("NotAShader", ex.Message);
        }

        [Test]
        public void SwapShader_RegistersUndo_PerformUndoRestoresPriorShader()
        {
            var path = ScratchDir + "/SwapUndo.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), path);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);

            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new FakeEditorLockApi(), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Undo.IncrementCurrentGroup();
            var @params = JsonValue.NewObject()
                .SetProperty("materialPath", JsonValue.String(path))
                .SetProperty("shader", JsonValue.String("Unlit/Color"));
            CommandTable.Dispatch(gate, Request("material.swap_shader", @params));
            Assert.AreEqual("Unlit/Color", mat.shader.name);

            Undo.PerformUndo();

            Assert.AreEqual("Standard", mat.shader.name);
        }
    }
}
