// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// DESIGN-02 (docs/design/DESIGN-02-visual-feedback.md) §11.2's plugin EditMode suite for
    /// 'capture.view'. Reaches every handler through <see cref="CommandTable.Dispatch"/>, same
    /// convention as every other file in this folder (see SceneCommandsTests' own doc comment).
    ///
    /// <b>The headline test is <see cref="CaptureView_UnderThisHarnessOwnMinusNographics_RefusesWithAnActionableMessage_NeverABlackImage"/>,
    /// and it passes BECAUSE scripts/regression/run-plugin-editmode.sh runs '-nographics' (DESIGN-02
    /// §7.2/§11.2, Phase 0 probe P17) - it never overrides <see cref="CaptureCommands.HasGraphicsDevice"/>.</b>
    ///
    /// Every other non-render code path (parameter validation, vocabulary refusal, width clamping,
    /// unknown-parameter rejection, target/camera resolution against a fixture hierarchy) is proven
    /// directly against <see cref="CaptureCommands"/>'s own public seams/helpers rather than by
    /// flipping <see cref="CaptureCommands.HasGraphicsDevice"/> and pushing a real call all the way
    /// through <c>Camera.Render()</c>/<c>ReadPixels</c> under a Null graphics device - the actual
    /// render is exactly the thing DESIGN-02 §11 states no automated suite in this repo can exercise.
    /// </summary>
    [TestFixture]
    public sealed class CaptureCommandsTests
    {
        static readonly Func<bool> RealHasGraphicsDevice = CaptureCommands.HasGraphicsDevice;

        [SetUp]
        public void SetUp()
        {
            SceneCommandsTestsResetScene();
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            CaptureCommands.HasGraphicsDevice = RealHasGraphicsDevice;
            Undo.ClearAll();
        }

        static void SceneCommandsTestsResetScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, "Assets/Tests/_HadesCaptureCommandsTestsScratchScene.unity");
        }

        static JsonRpcRequest Request(JsonValue @params) =>
            new JsonRpcRequest { Id = JsonValue.Integer(1), Method = "capture.view", Params = @params };

        static (ReloadGate gate, MainThreadPump pump) NoopGateParts()
        {
            var fake = new FakeEditorLockApi();
            var pump = new MainThreadPump();
            var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            return (gate, pump);
        }

        static Camera AddCamera(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            return go.AddComponent<Camera>();
        }

        static MeshRenderer AddCube(string name, Vector3 position, Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = position;
            if (parent != null) go.transform.SetParent(parent, false);
            return go.GetComponent<MeshRenderer>();
        }

        // ---------------------------------------------------------------- headline: -nographics refusal

        [Test]
        public void CaptureView_UnderThisHarnessOwnMinusNographics_RefusesWithAnActionableMessage_NeverABlackImage()
        {
            AddCube("Cube", Vector3.zero);
            AddCamera("Main Camera").tag = "MainCamera";

            var previousActiveRt = RenderTexture.active;
            var wasDirty = EditorSceneManager.GetActiveScene().isDirty;

            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("view", JsonValue.String("scene"));
                var ex = Assert.Throws<InvalidOperationException>(() => CommandTable.Dispatch(gate, Request(@params)));

                Assert.AreEqual(CaptureCommands.NoGraphicsDeviceMessage, ex.Message);
                StringAssert.Contains("-nographics", ex.Message);
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);
            }

            // Resource hygiene (DESIGN-02 §3.6/§11.2): a refusal must never leak a temp camera, must
            // never touch RenderTexture.active, and must never dirty the active scene.
            Assert.AreEqual(previousActiveRt, RenderTexture.active);
            Assert.AreEqual(wasDirty, EditorSceneManager.GetActiveScene().isDirty);
            CollectionAssert.DoesNotContain(
                Resources.FindObjectsOfTypeAll<Camera>().Select(c => c.name).ToList(), "Hades.Capture.TempCamera");
        }

        // ---------------------------------------------------------------- vocabulary / parameter validation

        [Test]
        public void CaptureView_UnknownView_Refuses()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("view", JsonValue.String("bogus"));
                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request(@params)));
                StringAssert.Contains("'view' must be one of", ex.Message);
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);
            }
        }

        [Test]
        public void CaptureView_UnknownFormat_Refuses()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("view", JsonValue.String("game"))
                    .SetProperty("format", JsonValue.String("bmp"));
                var ex = Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request(@params)));
                StringAssert.Contains("'format' must be", ex.Message);
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);
            }
        }

        [Test]
        public void CaptureView_MissingView_Refuses()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request(JsonValue.NewObject())));
            }
        }

        [Test]
        public void CaptureView_UnknownParameter_Refuses()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject()
                    .SetProperty("view", JsonValue.String("game"))
                    .SetProperty("resolution", JsonValue.Integer(4096));
                Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request(@params)));
            }
        }

        // ---------------------------------------------------------------- slice-2 cut: view: "prefab"

        [Test]
        public void CaptureView_ViewPrefab_RefusesAsNotImplemented_NamingTheTwoViewsThatWork()
        {
            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("view", JsonValue.String("prefab"));
                var ex = Assert.Throws<NotSupportedException>(() => CommandTable.Dispatch(gate, Request(@params)));
                StringAssert.Contains("'game'", ex.Message);
                StringAssert.Contains("'scene'", ex.Message);
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);
            }
        }

        // ---------------------------------------------------------------- width clamp (DESIGN-02 §4.2)

        [Test]
        public void ClampWidth_DefaultAndInRangeValues_AreNotClamped_NoNote()
        {
            var notes = new System.Collections.Generic.List<string>();
            Assert.AreEqual(1024, CaptureCommands.ClampWidth(1024, "jpeg", notes));
            Assert.AreEqual(512, CaptureCommands.ClampWidth(512, "jpeg", notes));
            CollectionAssert.IsEmpty(notes);
        }

        [Test]
        public void ClampWidth_BelowMinimum_ClampsTo256_WithNote()
        {
            var notes = new System.Collections.Generic.List<string>();
            Assert.AreEqual(256, CaptureCommands.ClampWidth(64, "jpeg", notes));
            Assert.AreEqual(1, notes.Count);
            StringAssert.Contains("clamped to 256", notes[0]);
        }

        [Test]
        public void ClampWidth_AboveJpegMaximum_ClampsTo1920_WithNote()
        {
            var notes = new System.Collections.Generic.List<string>();
            Assert.AreEqual(1920, CaptureCommands.ClampWidth(4096, "jpeg", notes));
            Assert.AreEqual(1, notes.Count);
            StringAssert.Contains("clamped to 1920", notes[0]);
        }

        [Test]
        public void ClampWidth_PngAbove1280_ClampsTo1280_WithNote_NeverRefuses()
        {
            // DESIGN-02 §4.2: 'width: 1920, format: "png"' clamps to 1280 and SUCCEEDS - it does not
            // refuse. Exercised here directly on the pure helper.
            var notes = new System.Collections.Generic.List<string>();
            Assert.AreEqual(1280, CaptureCommands.ClampWidth(1920, "png", notes));
            Assert.AreEqual(1, notes.Count);
            StringAssert.Contains("clamped to 1280", notes[0]);
            StringAssert.Contains("png caps at 1280", notes[0]);
        }

        [Test]
        public void ClampWidth_PngWithinItsOwnCap_IsNotClamped()
        {
            var notes = new System.Collections.Generic.List<string>();
            Assert.AreEqual(1024, CaptureCommands.ClampWidth(1024, "png", notes));
            CollectionAssert.IsEmpty(notes);
        }

        // ---------------------------------------------------------------- byte ceiling (DESIGN-02 §5.4)

        [Test]
        public void EnforceByteCeiling_WithinLimit_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => CaptureCommands.EnforceByteCeiling(CaptureCommands.ByteCeiling, 1024));
        }

        [Test]
        public void EnforceByteCeiling_ExceedsLimit_ThrowsActionableError_NamingASmallerWidth()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => CaptureCommands.EnforceByteCeiling(CaptureCommands.ByteCeiling + 1, 1920));
            StringAssert.Contains("exceeds", ex.Message);
            StringAssert.Contains("width", ex.Message);
            LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);
        }

        // ---------------------------------------------------------------- view: "game" camera resolution

        [Test]
        public void ResolveGameCamera_ExplicitPath_ReturnsThatCamera()
        {
            AddCamera("Other");
            var target = AddCamera("Target");

            var resolved = CaptureCommands.ResolveGameCamera("Target");
            Assert.AreSame(target, resolved);
        }

        [Test]
        public void ResolveGameCamera_UnknownPath_ThrowsActionableError()
        {
            AddCamera("OnlyCamera");
            var ex = Assert.Throws<ArgumentException>(() => CaptureCommands.ResolveGameCamera("DoesNotExist"));
            StringAssert.Contains("DoesNotExist", ex.Message);
            StringAssert.Contains("OnlyCamera", ex.Message);
        }

        [Test]
        public void ResolveGameCamera_Omitted_PrefersCameraMain()
        {
            var other = AddCamera("NotMain");
            other.depth = 100;
            var main = AddCamera("MainOne");
            main.tag = "MainCamera";

            var resolved = CaptureCommands.ResolveGameCamera(null);
            Assert.AreSame(main, resolved);
        }

        [Test]
        public void ResolveGameCamera_OmittedAndNoMainCamera_FallsBackToHighestDepthEnabledCamera()
        {
            var low = AddCamera("Low");
            low.depth = 0;
            var high = AddCamera("High");
            high.depth = 5;
            var disabledHighest = AddCamera("DisabledHighest");
            disabledHighest.depth = 99;
            disabledHighest.enabled = false;

            var resolved = CaptureCommands.ResolveGameCamera(null);
            Assert.AreSame(high, resolved);
        }

        [Test]
        public void ResolveGameCamera_NoCameraAnywhere_ThrowsActionableError()
        {
            var ex = Assert.Throws<ArgumentException>(() => CaptureCommands.ResolveGameCamera(null));
            StringAssert.Contains("no camera", ex.Message);
        }

        // ---------------------------------------------------------------- view: "scene" target/bounds resolution

        [Test]
        public void ResolveSceneBounds_TargetNotFound_ThrowsActionableError()
        {
            var ex = Assert.Throws<ArgumentException>(() => CaptureCommands.ResolveSceneBounds("Nope"));
            StringAssert.Contains("Nope", ex.Message);
        }

        [Test]
        public void ResolveSceneBounds_TargetWithRenderers_CombinesTheirBounds_NoFallbackNote()
        {
            var root = new GameObject("Group");
            AddCube("A", new Vector3(-5f, 0f, 0f), root.transform);
            AddCube("B", new Vector3(5f, 0f, 0f), root.transform);

            var (bounds, resolvedTarget, note) = CaptureCommands.ResolveSceneBounds("Group");

            Assert.AreEqual("Group", resolvedTarget);
            Assert.IsNull(note);
            Assert.Greater(bounds.size.x, 9f, "combined bounds must span both cubes");
        }

        [Test]
        public void ResolveSceneBounds_NoTarget_CombinesEveryRendererInTheActiveScene()
        {
            AddCube("Root1", new Vector3(-20f, 0f, 0f));
            AddCube("Root2", new Vector3(20f, 0f, 0f));

            var (bounds, resolvedTarget, note) = CaptureCommands.ResolveSceneBounds(null);

            Assert.IsNull(resolvedTarget);
            Assert.IsNull(note);
            Assert.Greater(bounds.size.x, 39f);
        }

        [Test]
        public void ResolveSceneBounds_TargetWithNoRenderers_ButARectTransform_FallsBackToRectTransformBounds_WithNote()
        {
            var root = new GameObject("UiOnly", typeof(RectTransform), typeof(Canvas));
            var rect = (RectTransform)root.transform;
            rect.sizeDelta = new Vector2(200f, 100f);

            var (bounds, resolvedTarget, note) = CaptureCommands.ResolveSceneBounds("UiOnly");

            Assert.AreEqual("UiOnly", resolvedTarget);
            Assert.IsNotNull(note);
            StringAssert.Contains("RectTransform", note);
            Assert.Greater(bounds.size.x, 0f);
        }

        [Test]
        public void ResolveSceneBounds_TargetWithNoRenderersOrRectTransforms_FallsBackToTransformPositionWithDefaultRadius_WithNote()
        {
            var root = new GameObject("BareTransform");
            root.transform.position = new Vector3(3f, 4f, 5f);

            var (bounds, resolvedTarget, note) = CaptureCommands.ResolveSceneBounds("BareTransform");

            Assert.AreEqual("BareTransform", resolvedTarget);
            Assert.IsNotNull(note);
            StringAssert.Contains("transform position", note);
            Assert.AreEqual(root.transform.position, bounds.center);
        }

        // ---------------------------------------------------------------- DESIGN-02 §7.3 overlay-canvas disclosure

        [Test]
        public void DetectOverlayCanvasNotes_NoOverlayCanvas_ReturnsEmpty()
        {
            var notes = CaptureCommands.DetectOverlayCanvasNotes(SceneManager.GetActiveScene());
            CollectionAssert.IsEmpty(notes);
        }

        [Test]
        public void DetectOverlayCanvasNotes_OneEnabledOverlayCanvas_NamesItAndExplainsWhy()
        {
            var go = new GameObject("HUD", typeof(RectTransform), typeof(Canvas));
            go.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

            var notes = CaptureCommands.DetectOverlayCanvasNotes(SceneManager.GetActiveScene());

            Assert.AreEqual(1, notes.Count);
            StringAssert.Contains("HUD", notes[0]);
            StringAssert.Contains("Screen Space - Overlay", notes[0]);
        }

        [Test]
        public void DetectOverlayCanvasNotes_ScreenSpaceCameraCanvas_IsNotFlagged()
        {
            var go = new GameObject("WorldUi", typeof(RectTransform), typeof(Canvas));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = AddCamera("CanvasCamera");

            var notes = CaptureCommands.DetectOverlayCanvasNotes(SceneManager.GetActiveScene());
            CollectionAssert.IsEmpty(notes);
        }

        [Test]
        public void DetectOverlayCanvasNotes_DisabledOverlayCanvas_IsNotFlagged()
        {
            var go = new GameObject("DisabledHud", typeof(RectTransform), typeof(Canvas));
            go.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            go.GetComponent<Canvas>().enabled = false;

            var notes = CaptureCommands.DetectOverlayCanvasNotes(SceneManager.GetActiveScene());
            CollectionAssert.IsEmpty(notes);
        }

        // ---------------------------------------------------------------- F1: view:"scene" no longer throws "The scene is invalid"

        [Test]
        public void DetectOverlayCanvasNotes_InvalidScene_ReturnsEmpty_NeverThrows()
        {
            // The exact shape 'view: "scene"' used to pass in here: a HideAndDontSave temp camera's
            // own .scene, which is never valid - Scene.GetRootGameObjects() throws "The scene is
            // invalid" for one. default(Scene) reproduces the same invalid scene without needing a
            // real temp camera.
            System.Collections.Generic.List<string> notes = null;
            Assert.DoesNotThrow(() => notes = CaptureCommands.DetectOverlayCanvasNotes(default));
            CollectionAssert.IsEmpty(notes);
        }

        [Test]
        public void CaptureView_SceneView_NoLongerThrowsSceneIsInvalid_UnderANullGraphicsDevice()
        {
            // End-to-end reproduction of the live defect (F1): 'view: "scene"' always failed with
            // "The scene is invalid" because CaptureView passed the temp camera's own (invalid)
            // scene into DetectOverlayCanvasNotes instead of the active scene. HasGraphicsDevice is
            // flipped to true here - see this class's own doc comment - so the call reaches the real
            // (meaningless, but non-crashing) render on Unity's GraphicsDeviceType.Null stub instead
            // of refusing at the -nographics check first.
            CaptureCommands.HasGraphicsDevice = () => true;
            AddCube("Cube", Vector3.zero);

            var (gate, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("view", JsonValue.String("scene"));
                JsonValue result = null;
                Assert.DoesNotThrow(() => result = CommandTable.Dispatch(gate, Request(@params)));
                Assert.IsTrue(result.TryGetProperty("image", out _), "a successful capture must return an image");
            }
        }
    }
}
