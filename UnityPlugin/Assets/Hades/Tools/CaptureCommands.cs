// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Hades.Tools
{
    /// <summary>
    /// DESIGN-02 (docs/design/DESIGN-02-visual-feedback.md), Lane F of ROADMAP.md's Phase 2. One
    /// new wire command, 'capture.view' - class 4 (live-state read, no lease; see CommandTable's own
    /// class-4 comment block). 'capture.view' sits with project.get_console_log/get_test_results/
    /// inspector.inspect for the identical reason: the answer (an image of the running Editor's own
    /// GPU output) lives only for the instant of the render, so there is nothing a domain reload
    /// could interrupt mid-flight. Never in <see cref="CommandTable.MutatingMethods"/> (nothing here
    /// is ever recorded to Undo - a render mutates no serialized state) and already allow-listed on
    /// <see cref="CommandTable.AllowedWhilePlaying"/> by DESIGN-01/Lane B (see that dictionary's own
    /// doc comment) - a render taken during Play Mode is the single most informative capture this
    /// tool can produce (DESIGN-02 §2.1).
    ///
    /// <b>Scope shipped: slice 1 only - view: "game" and view: "scene".</b> DESIGN-02 §13 open
    /// question 2 names 'view: "prefab"' (preview-scene lifecycle, bounds of an uninstantiated
    /// asset, isolation cleanup) as the highest-implementation-surface, lowest-ranked of the three
    /// views, and ROADMAP.md's Lane F entry explicitly invites cutting it first under time/complexity
    /// pressure - taken here. 'view' still validates against the full three-value vocabulary DESIGN-02
    /// designed for all three views from the start (so a future slice 2 adds no NEW wire surface),
    /// but 'prefab' refuses with an explicit, actionable <see cref="NotSupportedException"/> naming
    /// the two views that DO work - never silently miscategorised as an unknown view, never a wrong
    /// answer.
    ///
    /// <b>Never an OS screen capture, never ScreenCapture.*, never AssetPreview</b> - DESIGN-02 §2's
    /// central decision. Every render goes through a Camera -&gt; RenderTexture this handler owns,
    /// on the main thread, inside this one Dispatch call. See <see cref="RenderCamera"/>'s own doc
    /// comment for the Built-in/SRP branch (DESIGN-02 §3.4, Phase 0 probe P14).
    ///
    /// <b>Resource hygiene (DESIGN-02 §3.6), non-negotiable on every path including a thrown
    /// exception:</b> the temporary camera (view: "scene") is HideAndDontSave and destroyed in a
    /// <c>finally</c>; <see cref="RenderTexture.active"/> is captured before and restored after;
    /// the temporary <see cref="RenderTexture"/> and readback <see cref="Texture2D"/> are released/
    /// destroyed in the same <c>finally</c>; an existing user camera's own <c>targetTexture</c> (view:
    /// "game") is restored to whatever it was before this call touched it. Nothing here ever sets
    /// the active scene's dirty flag.
    /// </summary>
    public static class CaptureCommands
    {
        // ---- DESIGN-02 §11.2 seam ----
        //
        // Same swappable-public-static-field convention as ProjectCommands.TriggerRecompile/
        // RefreshAssets (see either's own doc comment): a test that
        // reassigns this MUST restore RealHasGraphicsDevice in its own TearDown. Left at its real
        // value, the very first thing 'capture.view' does under THIS REPO'S OWN '-nographics'
        // EditMode runner (scripts/regression/run-plugin-editmode.sh) is refuse - DESIGN-02 §7.2's
        // headline test, which passes BECAUSE the harness is headless (Phase 0 probe P17). Flipped
        // to true, every non-render path (parameter validation, vocabulary refusal, width clamping,
        // unknown-parameter rejection, target/camera resolution against a fixture hierarchy) is
        // testable too, all the way through a real (meaningless, but non-crashing) render on Unity's
        // own GraphicsDeviceType.Null stub.
        public static Func<bool> HasGraphicsDevice = RealHasGraphicsDevice;

        static bool RealHasGraphicsDevice() => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>DESIGN-02 §7.2's exact refusal, word for word - "never a black image."</summary>
        public const string NoGraphicsDeviceMessage =
            "This Unity Editor is running without a graphics device (-nographics), so nothing can be "
            + "rendered. Restart the Editor without -nographics to use capture_view.";

        static readonly string[] ValidViews = { "game", "scene", "prefab" };
        static readonly string[] ValidFormats = { "jpeg", "png" };

        const int MinWidth = 256;
        const int DefaultWidth = 1024;
        const int MaxWidthJpeg = 1920;
        const int MaxWidthPng = 1280;
        const int JpegQuality = 75;

        /// <summary>DESIGN-02 §5.4's backstop: 2 MiB encoded, ~17% of the 16 MiB plugin/core line
        /// bound (DESIGN-02 §5.1). Should never fire at the recommended defaults - the largest
        /// producible payload (a 1280-wide PNG or a 1920-wide JPEG, both bounded by the same clamp
        /// as every other width) is comfortably inside it. See <see cref="EnforceByteCeiling"/>.</summary>
        public const int ByteCeiling = 2 * 1024 * 1024;

        /// <summary>DESIGN-02 §5.3: height is derived, never passed. Fixed 16:9 for 'scene'/'prefab'
        /// (view: "game" instead uses the render camera's own current aspect - DESIGN-02 §5.3).</summary>
        const float SceneAspect = 16f / 9f;

        /// <summary>DESIGN-02 §6: opaque mid-grey, so an unlit black object is still visible. Holds
        /// on all three pipelines for a BARE temporary camera - DESIGN-02 §3.4.1 (Phase 0 probes P15/
        /// P16, VERIFIED against source).</summary>
        static readonly Color CaptureClearColor = new Color(0.5f, 0.5f, 0.5f, 1f);

        // ---------------------------------------------------------------- capture.view

        internal static JsonValue CaptureView(ReloadGate gate, JsonValue @params)
        {
            CommandTable.RejectUnknownParams(@params, "view", "target", "camera", "width", "format");

            var view = JsonParams.RequireString(@params, "view", "capture.view");
            if (!ValidViews.Contains(view))
            {
                throw new ArgumentException(
                    "'capture_view': 'view' must be one of 'game', 'scene', or 'prefab' - got '" + view + "'.");
            }

            var format = JsonParams.OptionalString(@params, "format") ?? "jpeg";
            if (!ValidFormats.Contains(format))
            {
                throw new ArgumentException(
                    "'capture_view': 'format' must be 'jpeg' or 'png' - got '" + format + "'.");
            }

            var target = JsonParams.OptionalString(@params, "target");
            var cameraParam = JsonParams.OptionalString(@params, "camera");

            var notes = new List<string>();
            var requestedWidth = JsonParams.OptionalInt(@params, "width", DefaultWidth);
            var width = ClampWidth(requestedWidth, format, notes);

            // DESIGN-02 §13 open question 2 / ROADMAP.md's own cut order: slice 2 ('prefab') is not
            // shipped in this build. Validated against the full vocabulary above (so this is a
            // deliberate, named refusal - never a silent "unknown view"), refused here, BEFORE the
            // graphics-device check, so this refusal is reachable under -nographics like every other
            // parameter-validation error.
            if (view == "prefab")
            {
                throw new NotSupportedException(
                    "'capture_view': view='prefab' is not implemented in this build - only 'game' and "
                    + "'scene' are available. Use one of those instead.");
            }

            // Target/camera resolution is pure Editor-object-graph reading (Transform/Renderer/
            // RectTransform/Camera state already in memory) - no graphics device involved, so it
            // happens (and can throw its own actionable errors) before the device check below.
            Camera existingCamera = null;
            Bounds sceneBounds = default;
            string framedTarget = null;
            var fallbackNote = (string)null;
            int height;

            if (view == "game")
            {
                existingCamera = ResolveGameCamera(cameraParam);
                height = Mathf.Max(1, Mathf.RoundToInt(width / Mathf.Max(existingCamera.aspect, 0.0001f)));
            }
            else // "scene"
            {
                height = Mathf.Max(1, Mathf.RoundToInt(width / SceneAspect));
                (sceneBounds, framedTarget, fallbackNote) = ResolveSceneBounds(target);
                if (fallbackNote != null) notes.Add(fallbackNote);
            }

            // DESIGN-02 §7.2: the one thing an on-demand render cannot survive. Checked last, so
            // every parameter/target/camera mistake above is reported on its own terms even under
            // -nographics, and a genuinely renderable request is the only thing that reaches this.
            if (!HasGraphicsDevice()) throw new InvalidOperationException(NoGraphicsDeviceMessage);

            GameObject tempCameraGo = null;
            var previousActiveRt = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                Camera camera;
                Vector3 position;
                Quaternion rotation;
                double[] framedBoundsArray = null;

                if (view == "game")
                {
                    camera = existingCamera;
                    position = camera.transform.position;
                    rotation = camera.transform.rotation;
                }
                else
                {
                    var (pos, rot, near, far) = CaptureFraming.Compute(sceneBounds, SceneAspect);
                    position = pos;
                    rotation = rot;
                    framedBoundsArray = BoundsToArray(sceneBounds);

                    tempCameraGo = new GameObject("Hades.Capture.TempCamera") { hideFlags = HideFlags.HideAndDontSave };
                    camera = tempCameraGo.AddComponent<Camera>();
                    camera.transform.SetPositionAndRotation(pos, rot);
                    camera.fieldOfView = CaptureFraming.DefaultFieldOfView;
                    camera.aspect = SceneAspect;
                    camera.nearClipPlane = near;
                    camera.farClipPlane = far;
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = CaptureClearColor;
                }

                // F1 fix: for 'scene' the render camera is the HideAndDontSave temp camera created
                // just above - its own .scene is never valid (it belongs to no loaded scene at all),
                // so GetRootGameObjects() on it throws "The scene is invalid". Overlay canvases are a
                // property of the ACTIVE scene being framed, not of whatever camera happens to be
                // doing the rendering - use it directly for every non-'game' view, matching
                // ResolveSceneBounds' own fallback ("in the active scene" when no target is given).
                notes.AddRange(DetectOverlayCanvasNotes(view == "game" ? camera.gameObject.scene : SceneManager.GetActiveScene()));

                var cameraLabel = view == "game" ? GameObjectPaths.GetPath(camera.gameObject) : "(temporary capture camera)";

                rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                var originalTargetTexture = camera.targetTexture;
                try
                {
                    RenderCamera(camera, rt);
                    RenderTexture.active = rt;

                    // linear:false - the Texture2D is treated as already gamma/display-space, which
                    // is what ReadPixels captures given RenderTextureReadWrite.Default above (DESIGN-02
                    // §3.1/§6; the exact incantation is written from documentation and is UNPROVEN
                    // here - see this file's own class doc comment and DESIGN-02 §12 item 3. Proven
                    // only by the H3 manual pass).
                    tex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    tex.Apply();
                }
                finally
                {
                    camera.targetTexture = originalTargetTexture;
                }

                var bytes = format == "png" ? ImageConversion.EncodeToPNG(tex) : ImageConversion.EncodeToJPG(tex, JpegQuality);
                var mimeType = format == "png" ? "image/png" : "image/jpeg";

                // DESIGN-02 §5.4: enforced HERE, before base64/the JsonValue this handler returns
                // is ever built - the plugin-to-core socket has no length prefix and treats an
                // over-limit line as indistinguishable from EOF (DESIGN-02 §5.1), which would drop
                // the Editor connection rather than return a clean error. Should never fire at the
                // recommended defaults (design's own words) - a backstop, not a routine path.
                EnforceByteCeiling(bytes.Length, width);

                return JsonValue.NewObject()
                    .SetProperty("image", JsonValue.String(Convert.ToBase64String(bytes)))
                    .SetProperty("mimeType", JsonValue.String(mimeType))
                    .SetProperty("width", JsonValue.Integer(width))
                    .SetProperty("height", JsonValue.Integer(height))
                    .SetProperty("camera", JsonValue.String(cameraLabel))
                    .SetProperty("cameraPosition", Vec3Array(position))
                    .SetProperty("cameraRotation", Vec3Array(rotation.eulerAngles))
                    .SetProperty("framedTarget", JsonValue.String(framedTarget))
                    .SetProperty("framedBounds", framedBoundsArray != null ? DoubleArray(framedBoundsArray) : null)
                    .SetProperty("renderPipeline", JsonValue.String(DetectRenderPipeline()))
                    .SetProperty("notes", StringArray(notes));
            }
            finally
            {
                RenderTexture.active = previousActiveRt;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (tempCameraGo != null) UnityEngine.Object.DestroyImmediate(tempCameraGo);
            }
        }

        // ---------------------------------------------------------------- width clamp (DESIGN-02 §4.2)

        /// <summary>DESIGN-02 §4.2's single binding formula, verbatim: <c>clamp(width ?? 1024, 256,
        /// format == "png" ? 1280 : 1920)</c> - the png cap applied to the EFFECTIVE width inside the
        /// SAME clamp, not a separate pre-check, so <c>width: 1920, format: "png"</c> clamps to 1280
        /// and succeeds rather than refusing. Always clamps, never refuses; a clamp that actually
        /// changed the request appends one <paramref name="notes"/> entry naming why.</summary>
        public static int ClampWidth(int requestedWidth, string format, List<string> notes)
        {
            var maxWidth = format == "png" ? MaxWidthPng : MaxWidthJpeg;
            var clamped = Mathf.Clamp(requestedWidth, MinWidth, maxWidth);
            if (clamped != requestedWidth)
            {
                var reason = format == "png"
                    ? "png caps at " + MaxWidthPng + " - use jpeg for up to " + MaxWidthJpeg
                    : "valid range is [" + MinWidth + ", " + MaxWidthJpeg + "]";
                notes.Add("width clamped to " + clamped + ": requested " + requestedWidth + "; " + reason + ".");
            }
            return clamped;
        }

        /// <summary>DESIGN-02 §5.4: the one and only refusal path in the sizing policy. Never a
        /// truncated or silently degraded image - a half-image is worse than no image, because the
        /// agent cannot tell.</summary>
        public static void EnforceByteCeiling(int encodedByteLength, int requestedWidth)
        {
            if (encodedByteLength <= ByteCeiling) return;

            throw new InvalidOperationException(
                "'capture_view': the encoded image (" + encodedByteLength + " bytes) exceeds the "
                + ByteCeiling + "-byte safety ceiling enforced before it crosses the plugin/core socket "
                + "(exceeding that wire's own line bound would silently drop the Editor connection "
                + "instead of returning an error). Retry with a smaller 'width' than " + requestedWidth + ".");
        }

        // ---------------------------------------------------------------- view: "game" camera resolution

        /// <summary>DESIGN-02 §4.2's 'camera' parameter: an explicit name/path, or <see
        /// cref="Camera.main"/>, falling back to the enabled camera with the highest depth.</summary>
        public static Camera ResolveGameCamera(string cameraParam)
        {
            if (!string.IsNullOrEmpty(cameraParam))
            {
                var go = GameObjectPaths.FindByPath(cameraParam);
                var camera = go != null ? go.GetComponent<Camera>() : null;
                if (camera != null) return camera;

                var known = string.Join(", ", AllSceneCameras().Select(c => GameObjectPaths.GetPath(c.gameObject)));
                throw new ArgumentException(
                    "'capture_view': no Camera found at '" + cameraParam + "'. Cameras in the active scene: "
                    + (known.Length == 0 ? "(none)" : known) + ".");
            }

            if (Camera.main != null) return Camera.main;

            var fallback = AllSceneCameras()
                .Where(c => c.enabled && c.gameObject.activeInHierarchy)
                .OrderByDescending(c => c.depth)
                .FirstOrDefault();
            if (fallback != null) return fallback;

            throw new ArgumentException(
                "'capture_view': no camera to render through - no Camera.main and no enabled Camera in "
                + "the active scene. Pass 'camera' explicitly, or add/enable a camera.");
        }

        static IEnumerable<Camera> AllSceneCameras() =>
            SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Camera>(true));

        // ---------------------------------------------------------------- view: "scene" bounds resolution

        /// <summary>DESIGN-02 §3.5 step 1-2's fallback chain: Renderer bounds of the target (or the
        /// whole active scene when <paramref name="target"/> is omitted) -&gt; RectTransform world
        /// corners -&gt; the transform's own position with a default radius - reporting which one was
        /// used via the returned fallback note (null when the common Renderer-bounds path was
        /// taken).</summary>
        public static (Bounds bounds, string resolvedTarget, string fallbackNote) ResolveSceneBounds(string target)
        {
            GameObject root = null;
            if (!string.IsNullOrEmpty(target))
                root = GameObjectPaths.FindByPath(target) ?? throw GameObjectPaths.NotFoundError(target);

            var renderers = RootsToSearch(root).SelectMany(r => r.GetComponentsInChildren<Renderer>(true)).ToArray();
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                return (bounds, target, null);
            }

            var rects = RootsToSearch(root).SelectMany(r => r.GetComponentsInChildren<RectTransform>(true)).ToArray();
            if (rects.Length > 0)
            {
                var corners = new Vector3[4];
                rects[0].GetWorldCorners(corners);
                var bounds = new Bounds(corners[0], Vector3.zero);
                for (var i = 1; i < 4; i++) bounds.Encapsulate(corners[i]);
                for (var r = 1; r < rects.Length; r++)
                {
                    rects[r].GetWorldCorners(corners);
                    for (var i = 0; i < 4; i++) bounds.Encapsulate(corners[i]);
                }

                var whatWasSearched = target != null ? "under '" + target + "'" : "in the active scene";
                return (bounds, target, "No renderers found " + whatWasSearched + "; framed using RectTransform bounds instead.");
            }

            if (root != null)
            {
                return (new Bounds(root.transform.position, Vector3.one),
                    target,
                    "No renderers or RectTransforms found under '" + target + "'; framed using its transform "
                    + "position with a default radius.");
            }

            return (new Bounds(Vector3.zero, Vector3.one), null,
                "No renderers or RectTransforms found in the active scene; framed on the origin with a default radius.");
        }

        static IEnumerable<GameObject> RootsToSearch(GameObject root) =>
            root != null ? new[] { root } : SceneManager.GetActiveScene().GetRootGameObjects();

        // ---------------------------------------------------------------- DESIGN-02 §7.3 overlay-canvas disclosure

        /// <summary>DESIGN-02 §7.3: a Screen Space - Overlay canvas composites into the backbuffer
        /// AFTER camera rendering, so it never appears in an off-screen camera render. Disclosed,
        /// never mutated (switching the canvas's render mode would dirty the scene from a
        /// <c>ReadOnly = true</c> tool - rejected outright by the design).
        ///
        /// <para>F1 fix: an invalid <paramref name="scene"/> (e.g. a caller somehow reaching this with
        /// a HideAndDontSave temp object's own non-scene) reports no notes rather than throwing -
        /// <see cref="Scene.GetRootGameObjects()"/> throws "The scene is invalid" for one, and a
        /// missing disclosure note is a far smaller failure than the capture itself never
        /// happening.</para></summary>
        public static List<string> DetectOverlayCanvasNotes(Scene scene)
        {
            if (!scene.IsValid()) return new List<string>();

            var overlays = scene.GetRootGameObjects()
                .SelectMany(r => r.GetComponentsInChildren<Canvas>(true))
                .Where(c => c.enabled && c.renderMode == RenderMode.ScreenSpaceOverlay)
                .ToList();

            if (overlays.Count == 0) return new List<string>();

            var plural = overlays.Count == 1 ? "" : "es";
            var verb = overlays.Count == 1 ? "is" : "are";
            var names = string.Join(", ", overlays.Select(c => "'" + GameObjectPaths.GetPath(c.gameObject) + "'"));

            return new List<string>
            {
                overlays.Count + " Screen Space - Overlay canvas" + plural + " (" + names + ") " + verb
                + " not visible to an off-screen render and " + verb + " absent from this image. Screen "
                + "Space - Camera and World Space canvases render normally.",
            };
        }

        // ---------------------------------------------------------------- render pipeline branch (DESIGN-02 §3.4)

        /// <summary>DESIGN-02 §3.4's branch, Phase 0 probe P14 (VERIFIED (probe) against 6000.3.2f1):
        /// <c>RenderPipeline.StandardRequest</c>, <c>Camera.SubmitRenderRequest</c>, and
        /// <c>RenderPipeline.SupportsRenderRequest</c> all exist and are public. Built-in uses the
        /// direct <c>Camera.Render()</c> call (the only supported path there - DESIGN-02 §12 item
        /// 10); URP/HDRP use the render-request API instead, since <c>Camera.Render()</c> is not the
        /// supported call under SRP. Actually exercising this branch under a real URP/HDRP project is
        /// H3 manual-pass territory (DESIGN-02 §11.5 step 3) - no automated suite here can.</summary>
        static void RenderCamera(Camera camera, RenderTexture destination)
        {
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null)
            {
                camera.targetTexture = destination;
                camera.Render();
                return;
            }

            var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = destination };
            if (!UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request))
            {
                throw new InvalidOperationException(
                    "'capture_view': the active render pipeline does not support an on-demand render "
                    + "request for this camera.");
            }

            camera.SubmitRenderRequest(request);
        }

        static string DetectRenderPipeline()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return "Built-in";

            var typeName = pipeline.GetType().Name;
            if (typeName.IndexOf("Universal", StringComparison.Ordinal) >= 0) return "URP";
            if (typeName.IndexOf("HighDefinition", StringComparison.Ordinal) >= 0) return "HDRP";
            return "unknown";
        }

        // ---------------------------------------------------------------- JsonValue helpers

        static JsonValue Vec3Array(Vector3 v) =>
            JsonValue.NewArray().Add(JsonValue.Float(v.x)).Add(JsonValue.Float(v.y)).Add(JsonValue.Float(v.z));

        static double[] BoundsToArray(Bounds b) =>
            new[] { (double)b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z };

        static JsonValue DoubleArray(IEnumerable<double> values)
        {
            var array = JsonValue.NewArray();
            foreach (var v in values) array.Add(JsonValue.Float(v));
            return array;
        }

        static JsonValue StringArray(IEnumerable<string> values)
        {
            var array = JsonValue.NewArray();
            foreach (var v in values) array.Add(JsonValue.String(v));
            return array;
        }
    }
}
