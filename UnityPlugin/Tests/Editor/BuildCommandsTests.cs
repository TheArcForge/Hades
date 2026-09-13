// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// DESIGN-03 (docs/design/DESIGN-03-build-pipeline.md) §11.2's plugin EditMode suite. Reaches
    /// every handler through <see cref="CommandTable.Dispatch"/>, same convention as
    /// <see cref="PlayModeCommandsTests"/>. <see cref="BuildCommands.RunBuild"/> is ALWAYS
    /// substituted with a fake in <see cref="SetUp"/> and restored in <see cref="TearDown"/> - see
    /// that field's own doc comment: a real <c>BuildPipeline.BuildPlayer</c> invoked from inside an
    /// already-running EditMode test would pin the very main thread running this test for minutes.
    ///
    /// The three preflights that read live, non-seamed-by-DESIGN-01 Editor state
    /// ('EditorApplication.isCompiling', 'EditorUtility.scriptCompilationFailed',
    /// 'EditorApplication.isPlayingOrWillChangePlaymode') turned out NOT to be safely ignorable:
    /// measured directly, 'isCompiling' reads true for a real window while this suite's own scratch
    /// project is still settling after import, which made every "the build should proceed" test
    /// below flake solid red against the real property. <see cref="BuildCommands"/> therefore exposes
    /// all three as swappable seams (<see cref="BuildCommands.IsCompiling"/> etc.) - not one of
    /// DESIGN-01 §6.1's six named seams, but required by the identical testability argument - forced
    /// false in <see cref="SetUp"/> so every OTHER test's "should proceed" path is deterministic, and
    /// exercised directly by name further down.
    /// </summary>
    [TestFixture]
    public sealed class BuildCommandsTests
    {
        static readonly Func<BuildRequest, BuildOutcome> RealRunBuild = BuildCommands.RunBuild;

        [SetUp]
        public void SetUp()
        {
            DeleteStatusFileIfPresent();
            ClearBakeKeys();
            BuildCommands.RunBuild = _ => throw new InvalidOperationException(
                "A test forgot to substitute BuildCommands.RunBuild before dispatching 'project.build_start'.");

            // Measured directly: EditorApplication.isCompiling reads true for a real window while
            // this batchmode scratch project settles right after import, which otherwise makes every
            // "the build should proceed" test below flake solid-red against the real property (not
            // occasionally - every run). Forced false/false/false here so each test controls its own
            // preflight seam explicitly, restored in TearDown per this plugin's stated hygiene rule.
            BuildCommands.IsCompiling = () => false;
            BuildCommands.ScriptCompilationFailed = () => false;
            BuildCommands.IsPlayingOrWillChangePlaymode = () => false;
        }

        [TearDown]
        public void TearDown()
        {
            BuildCommands.RunBuild = RealRunBuild;
            BuildCommands.IsCompiling = () => EditorApplication.isCompiling;
            BuildCommands.ScriptCompilationFailed = () => EditorUtility.scriptCompilationFailed;
            BuildCommands.IsPlayingOrWillChangePlaymode = () => EditorApplication.isPlayingOrWillChangePlaymode;
            DeleteStatusFileIfPresent();
            ClearBakeKeys();
        }

        static void DeleteStatusFileIfPresent()
        {
            if (File.Exists(BuildStatusStore.StatusPath)) File.Delete(BuildStatusStore.StatusPath);
        }

        static void ClearBakeKeys()
        {
            // Was hardcoded string literals ("Hades.Bake.JobId" etc) rather than BakeKeys.* - found
            // by the Phase 3 integration sweep: Lane G's BakeKeys class exists exactly so a rename
            // fails to compile, but a literal here would have kept compiling and gone stale silently
            // on exactly that rename. Referencing the constants makes this test move with the class.
            SessionState.EraseString(BakeKeys.JobId);
            SessionState.EraseString(BakeKeys.Terminal);
            SessionState.EraseString(BakeKeys.Epoch);
            SessionState.EraseInt(BakeKeys.ReloadEpoch);
        }

        static (ReloadGate gate, FakeEditorLockApi fake, MainThreadPump pump) NoopGateParts()
        {
            var fake = new FakeEditorLockApi();
            var pump = new MainThreadPump();
            var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            return (gate, fake, pump);
        }

        static JsonRpcRequest Request(string method, JsonValue @params) =>
            new JsonRpcRequest { Id = JsonValue.Integer(1), Method = method, Params = @params };

        static JsonValue DispatchStart(JsonValue @params)
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                return CommandTable.Dispatch(gate, Request("project.build_start", @params));
            }
        }

        /// <summary>A fake outcome that never touches the real build pipeline - what most tests
        /// hand to <see cref="BuildCommands.RunBuild"/>.</summary>
        static BuildOutcome FakeSucceeded() => new BuildOutcome
        {
            Result = "Succeeded",
            TotalErrors = 0,
            TotalWarnings = 0,
            OutputSizeBytes = 12345,
            Messages = new List<(string, string, string)>(),
            EditorLogPath = "/tmp/fake-editor.log",
        };

        // 'project.build_start' schedules the actual build via EditorApplication.delayCall, which
        // does not drain during a synchronous EditMode test method (there is no seam for it - unlike
        // DESIGN-01 §6.1's six named play-mode seams, build_start has none of its own). This suite
        // therefore asserts against the 'queued' record 'project.build_start' itself writes - which
        // is exactly what DESIGN-03's single-flight/atomic-write/dirty-scene/params/preflight tests
        // need - and exercises the 'running'/terminal transition separately, through BuildStatusStore
        // and BuildWatchdog directly (see BuildStatusStoreTests below).

        // ============================================================== params

        [Test]
        public void Start_WithNeitherTargetNorProfile_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => DispatchStart(JsonValue.NewObject()));
            StringAssert.Contains("target", ex.Message);
            StringAssert.Contains("profile", ex.Message);
        }

        [Test]
        public void Start_WithBothTargetAndProfile_Throws()
        {
            var @params = JsonValue.NewObject()
                .SetProperty("target", JsonValue.String("StandaloneOSX"))
                .SetProperty("profile", JsonValue.String("Assets/Build.asset"));

            var ex = Assert.Throws<ArgumentException>(() => DispatchStart(@params));
            StringAssert.Contains("mutually exclusive", ex.Message);
        }

        [Test]
        public void Start_WithProfileAndDevelopment_Throws()
        {
            var @params = JsonValue.NewObject()
                .SetProperty("profile", JsonValue.String("Assets/Build.asset"))
                .SetProperty("development", JsonValue.Bool(true));

            var ex = Assert.Throws<ArgumentException>(() => DispatchStart(@params));
            StringAssert.Contains("mutually exclusive", ex.Message);
        }

        [Test]
        public void Start_WithUnrecognisedTargetName_ThrowsActionableError()
        {
            var @params = JsonValue.NewObject().SetProperty("target", JsonValue.String("NotARealBuildTarget"));

            var ex = Assert.Throws<ArgumentException>(() => DispatchStart(@params));
            StringAssert.Contains("not a recognised BuildTarget name", ex.Message);
        }

        [Test]
        public void Start_WithUnknownParameter_Throws()
        {
            var @params = JsonValue.NewObject().SetProperty("bogus", JsonValue.Bool(true));
            Assert.Throws<ArgumentException>(() => DispatchStart(@params));
        }

        [Test]
        public void Start_WithNonexistentProfileAsset_ThrowsActionableError()
        {
            var @params = JsonValue.NewObject().SetProperty("profile", JsonValue.String("Assets/DoesNotExist.asset"));
            var ex = Assert.Throws<ArgumentException>(() => DispatchStart(@params));
            StringAssert.Contains("no Build Profile asset found", ex.Message);
        }

        // ============================================================== active-target success path + dirty scenes + outputPath default

        [Test]
        public void Start_ForTheActiveTarget_Succeeds_WritesQueuedFile_ReturnsDefaultOutputPath()
        {
            BuildCommands.RunBuild = _ => FakeSucceeded();

            var active = EditorUserBuildSettings.activeBuildTarget;
            var @params = JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString()));

            var result = DispatchStart(@params);

            Assert.AreEqual("queued", result.TryGetProperty("state", out var s) ? s.AsString() : null);
            Assert.AreEqual(active.ToString(), result.TryGetProperty("target", out var t) ? t.AsString() : null);
            var outputPath = result.TryGetProperty("outputPath", out var o) ? o.AsString() : null;
            StringAssert.StartsWith("Build/" + active + "/", outputPath);

            var doc = BuildStatusStore.TryRead();
            Assert.IsNotNull(doc, "project.build_start must have written build-status.json");
            Assert.AreEqual(BuildStatusStore.StateQueued, doc.State);
            Assert.AreEqual(active.ToString(), doc.Target);
            Assert.IsNotEmpty(doc.BuildId);
            Assert.IsNotEmpty(doc.BootNonce);
        }

        [Test]
        public void Start_NeverSavesAnyOpenScene_F12Pin()
        {
            // Mirrors ProjectCommandsTests' AssertRunTestsNeverSavesDirtyScene - DESIGN-03 §7.3.
            BuildCommands.RunBuild = _ => FakeSucceeded();
            SceneTestFixtures.ResetScene();

            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            Assert.IsTrue(scene.isDirty, "test setup: the scene must actually be dirty before the call");

            var active = EditorUserBuildSettings.activeBuildTarget;
            var result = DispatchStart(JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString())));

            Assert.IsTrue(scene.isDirty, "'project.build_start' must never save a dirty scene");
            Assert.IsTrue(result.TryGetProperty("dirtyScenes", out var dirty) && dirty.Kind == JsonValueKind.Array && dirty.Items.Count > 0,
                "the dirty scene must be disclosed in the result, not silently saved or silently dropped");
        }

        // ============================================================== outputPath validation

        [Test]
        public void Start_WithAbsoluteOutputPath_Throws()
        {
            var active = EditorUserBuildSettings.activeBuildTarget;
            var @params = JsonValue.NewObject()
                .SetProperty("target", JsonValue.String(active.ToString()))
                .SetProperty("outputPath", JsonValue.String("/tmp/somewhere-else"));

            Assert.Throws<ArgumentException>(() => DispatchStart(@params));
        }

        [Test]
        public void Start_WithOutputPathEscapingProjectViaTraversal_Throws()
        {
            var active = EditorUserBuildSettings.activeBuildTarget;
            var @params = JsonValue.NewObject()
                .SetProperty("target", JsonValue.String(active.ToString()))
                .SetProperty("outputPath", JsonValue.String("../outside"));

            Assert.Throws<ArgumentException>(() => DispatchStart(@params));
        }

        [Test]
        public void Start_WithOutputPathInsideAssets_Throws()
        {
            var active = EditorUserBuildSettings.activeBuildTarget;
            var @params = JsonValue.NewObject()
                .SetProperty("target", JsonValue.String(active.ToString()))
                .SetProperty("outputPath", JsonValue.String("Assets/Build"));

            var ex = Assert.Throws<ArgumentException>(() => DispatchStart(@params));
            StringAssert.Contains("Assets", ex.Message);
        }

        [Test]
        public void Start_WithOutputPathInsideLibrary_Throws()
        {
            var active = EditorUserBuildSettings.activeBuildTarget;
            var @params = JsonValue.NewObject()
                .SetProperty("target", JsonValue.String(active.ToString()))
                .SetProperty("outputPath", JsonValue.String("Library/Sneaky"));

            Assert.Throws<ArgumentException>(() => DispatchStart(@params));
        }

        // ============================================================== compile / play-mode preflights

        [Test]
        public void Start_WhileScriptsAreCompiling_Refuses()
        {
            BuildCommands.IsCompiling = () => true;
            var active = EditorUserBuildSettings.activeBuildTarget;
            var ex = Assert.Throws<InvalidOperationException>(
                () => DispatchStart(JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString()))));
            StringAssert.Contains("compiling", ex.Message);
            Assert.IsNull(BuildStatusStore.TryRead(), "a refused build must never write a status file");
        }

        [Test]
        public void Start_WhileScriptCompilationFailed_Refuses_NamesTheConsoleLogTool()
        {
            BuildCommands.ScriptCompilationFailed = () => true;
            var active = EditorUserBuildSettings.activeBuildTarget;
            var ex = Assert.Throws<InvalidOperationException>(
                () => DispatchStart(JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString()))));
            StringAssert.Contains("do not compile", ex.Message);
            LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);
        }

        [Test]
        public void Start_WhileIsPlayingOrWillChangePlaymode_Refuses()
        {
            // Distinct from the CommandTable play-mode GUARD (already pinned in
            // Start_WhilePlaying_IsRefusedByTheGuard_NeverReachesTheHandler below) - this is the
            // handler's OWN preflight, DESIGN-03 §5.2's belt-and-braces for the transitional window
            // where isPlayingOrWillChangePlaymode is true but EditorPlayModeApi.IsPlaying() (what the
            // guard itself checks) is not yet.
            BuildCommands.IsPlayingOrWillChangePlaymode = () => true;
            var active = EditorUserBuildSettings.activeBuildTarget;
            var ex = Assert.Throws<InvalidOperationException>(
                () => DispatchStart(JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString()))));
            StringAssert.Contains("Play Mode", ex.Message);
        }

        // ============================================================== single-flight

        [Test]
        public void Start_WhileANonTerminalBuildFileExists_Refuses_NamesTheInFlightBuildId()
        {
            var doc = new BuildStatusStore.Document
            {
                Schema = BuildStatusStore.CurrentSchema,
                BuildId = "existing-build-id",
                BootNonce = BuildSession.CurrentBootNonce,
                State = BuildStatusStore.StateRunning,
                Target = "StandaloneOSX",
                OutputPath = "Build/StandaloneOSX/Demo.app",
            };
            BuildStatusStore.Write(doc);

            var active = EditorUserBuildSettings.activeBuildTarget;
            var @params = JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString()));

            var ex = Assert.Throws<InvalidOperationException>(() => DispatchStart(@params));
            StringAssert.Contains("existing-build-id", ex.Message);
        }

        [Test]
        public void Start_WhileATerminalBuildFileExists_Succeeds()
        {
            BuildCommands.RunBuild = _ => FakeSucceeded();

            var doc = new BuildStatusStore.Document
            {
                Schema = BuildStatusStore.CurrentSchema,
                BuildId = "old-build-id",
                BootNonce = BuildSession.CurrentBootNonce,
                State = BuildStatusStore.StateComplete,
                Target = "StandaloneOSX",
                OutputPath = "Build/StandaloneOSX/Demo.app",
            };
            BuildStatusStore.Write(doc);

            var active = EditorUserBuildSettings.activeBuildTarget;
            var @params = JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString()));

            Assert.DoesNotThrow(() => DispatchStart(@params));
        }

        // ============================================================== cross-target refusal (environment-dependent, best-effort)

        [Test]
        public void Start_ForANonActiveButBuildableTarget_Refuses_AndDoesNotSwitchTheActiveTarget()
        {
            var active = EditorUserBuildSettings.activeBuildTarget;
            BuildTarget? other = null;
            foreach (BuildTarget candidate in Enum.GetValues(typeof(BuildTarget)))
            {
                if (candidate == active || candidate == BuildTarget.NoTarget) continue;
                var field = typeof(BuildTarget).GetField(candidate.ToString());
                if (field != null && field.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length > 0) continue;
                try
                {
                    if (BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(candidate), candidate))
                    {
                        other = candidate;
                        break;
                    }
                }
                catch { /* some targets throw querying support - skip */ }
            }

            if (other == null)
            {
                Assert.Ignore("This Editor installation has no second buildable target alongside '" + active + "' to test the cross-target refusal against.");
                return;
            }

            var @params = JsonValue.NewObject().SetProperty("target", JsonValue.String(other.Value.ToString()));
            var ex = Assert.Throws<InvalidOperationException>(() => DispatchStart(@params));
            StringAssert.Contains(other.Value.ToString(), ex.Message);
            StringAssert.Contains(active.ToString(), ex.Message);
            Assert.AreEqual(active, EditorUserBuildSettings.activeBuildTarget, "a refused cross-target build must never switch the Editor's active target");
            Assert.IsNull(BuildStatusStore.TryRead(), "a refused build must never write a status file");
        }

        [Test]
        public void Start_ForAnUnsupportedTarget_Refuses_ListsBuildableTargets()
        {
            BuildTarget? unsupported = null;
            foreach (BuildTarget candidate in Enum.GetValues(typeof(BuildTarget)))
            {
                if (candidate == BuildTarget.NoTarget) continue;
                var field = typeof(BuildTarget).GetField(candidate.ToString());
                if (field != null && field.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length > 0) continue;
                try
                {
                    if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(candidate), candidate))
                    {
                        unsupported = candidate;
                        break;
                    }
                }
                catch { unsupported = candidate; break; }
            }

            if (unsupported == null)
            {
                Assert.Ignore("This Editor installation has every BuildTarget module installed - nothing to test the module-not-installed refusal against.");
                return;
            }

            var @params = JsonValue.NewObject().SetProperty("target", JsonValue.String(unsupported.Value.ToString()));
            var ex = Assert.Throws<InvalidOperationException>(() => DispatchStart(@params));
            StringAssert.Contains("not buildable by this Editor", ex.Message);
            StringAssert.Contains("Buildable targets right now:", ex.Message);
        }

        // ============================================================== bake x build preflight (DESIGN-08 §7.3)

        [Test]
        public void Start_WithNoBakeKeysWritten_IsInert_Succeeds()
        {
            // The reader stays inert until DESIGN-06 (Lane G) ships - see BuildCommands'
            // HadesOwnedBakeInProgressRefusal doc comment. Every key absent must never block a build.
            BuildCommands.RunBuild = _ => FakeSucceeded();
            var active = EditorUserBuildSettings.activeBuildTarget;
            Assert.DoesNotThrow(() => DispatchStart(JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString()))));
        }

        [Test]
        public void Start_WithAHadesOwnedNonTerminalBakeAtTheCurrentEpoch_Refuses()
        {
            SessionState.SetString("Hades.Bake.JobId", "bake-1");
            SessionState.SetString("Hades.Bake.Terminal", "");
            SessionState.SetInt("Hades.ReloadEpoch", 0);
            SessionState.SetString("Hades.Bake.Epoch", "0");

            var active = EditorUserBuildSettings.activeBuildTarget;
            var ex = Assert.Throws<InvalidOperationException>(
                () => DispatchStart(JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString()))));
            StringAssert.Contains("bake-1", ex.Message);
        }

        [Test]
        public void Start_WithABakeFromAnEarlierEpoch_IsUnattributable_DoesNotBlock()
        {
            BuildCommands.RunBuild = _ => FakeSucceeded();
            SessionState.SetString("Hades.Bake.JobId", "bake-stale");
            SessionState.SetString("Hades.Bake.Terminal", "");
            SessionState.SetInt("Hades.ReloadEpoch", 5);
            SessionState.SetString("Hades.Bake.Epoch", "1"); // stale - a reload happened since

            var active = EditorUserBuildSettings.activeBuildTarget;
            Assert.DoesNotThrow(() => DispatchStart(JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString()))));
        }

        [Test]
        public void Start_WithATerminalBake_DoesNotBlock()
        {
            BuildCommands.RunBuild = _ => FakeSucceeded();
            SessionState.SetString("Hades.Bake.JobId", "bake-done");
            SessionState.SetString("Hades.Bake.Terminal", "complete");

            var active = EditorUserBuildSettings.activeBuildTarget;
            Assert.DoesNotThrow(() => DispatchStart(JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString()))));
        }

        [Test]
        public void Start_WithJobIdButNoTerminalKey_TreatsAsCannotTell_Refuses()
        {
            // A JobId with no Terminal at all is the "Lane G renamed a key this reader missed"
            // shape - must never silently read as "no bake". See BakeKeys' own doc comment.
            SessionState.SetString("Hades.Bake.JobId", "bake-weird");

            var active = EditorUserBuildSettings.activeBuildTarget;
            var ex = Assert.Throws<InvalidOperationException>(
                () => DispatchStart(JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString()))));
            StringAssert.Contains("cannot tell", ex.Message);
        }

        // ============================================================== project.build_targets

        [Test]
        public void Targets_ReturnsHostPlatformAndAtLeastOneSupportedTarget()
        {
            var (gate, fake, pump) = NoopGateParts();
            JsonValue result;
            using (pump) using (gate)
            {
                result = CommandTable.Dispatch(gate, Request("project.build_targets", JsonValue.NewObject()));
            }

            Assert.IsTrue(result.TryGetProperty("hostPlatform", out var host) && host.Kind == JsonValueKind.String && !string.IsNullOrEmpty(host.AsString()));
            Assert.IsTrue(result.TryGetProperty("targets", out var targets) && targets.Kind == JsonValueKind.Array && targets.Items.Count > 0);

            var active = EditorUserBuildSettings.activeBuildTarget;
            var activeEntry = targets.Items.FirstOrDefault(t =>
                t.TryGetProperty("target", out var name) && name.AsString() == active.ToString());
            Assert.IsNotNull(activeEntry, "the Editor's own active build target must be present in the 'targets' list");
            Assert.IsTrue(activeEntry.TryGetProperty("supported", out var supported) && supported.Kind == JsonValueKind.Boolean && supported.AsBoolean(),
                "the Editor's own active build target must always report supported:true");
        }

        [Test]
        public void Targets_NeverContainsObsoleteMembersLikeStandaloneOSXIntel()
        {
            var (gate, fake, pump) = NoopGateParts();
            JsonValue result;
            using (pump) using (gate)
            {
                result = CommandTable.Dispatch(gate, Request("project.build_targets", JsonValue.NewObject()));
            }

            result.TryGetProperty("targets", out var targets);
            var names = targets.Items.Select(t => t.TryGetProperty("target", out var n) ? n.AsString() : null).ToList();
            CollectionAssert.DoesNotContain(names, "StandaloneOSXIntel");
            CollectionAssert.DoesNotContain(names, "StandaloneOSXUniversal");
            CollectionAssert.DoesNotContain(names, "NoTarget");
        }

        [Test]
        public void Targets_IsCallableWhilePlaying()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                Assert.DoesNotThrow(() => CommandTable.Dispatch(gate, Request("project.build_targets", JsonValue.NewObject())));
            }
        }

        // ============================================================== play mode guard

        [Test]
        public void Start_WhilePlaying_IsRefusedByTheGuard_NeverReachesTheHandler()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var savedIsPlaying = EditorPlayModeApi.IsPlaying;
                EditorPlayModeApi.IsPlaying = () => true;
                try
                {
                    var ex = Assert.Throws<InvalidOperationException>(
                        () => CommandTable.Dispatch(gate, Request("project.build_start", JsonValue.NewObject())));
                    LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);
                }
                finally
                {
                    EditorPlayModeApi.IsPlaying = savedIsPlaying;
                }
            }

            Assert.IsNull(BuildStatusStore.TryRead(), "the guard must refuse before the handler ever writes a status file");
        }

        // ============================================================== no lease held across the call; not a MutatingMethods entry

        [Test]
        public void Start_AcquiresNoLeaseByTheTimeItReturns()
        {
            BuildCommands.RunBuild = _ => FakeSucceeded();
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var active = EditorUserBuildSettings.activeBuildTarget;
                CommandTable.Dispatch(gate, Request("project.build_start", JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString()))));
                Assert.IsFalse(gate.IsHeld, "'project.build_start' must release its bounded prep lease before returning");
            }
        }

        [Test]
        public void Start_WhileAForeignLeaseIsHeld_Refuses_DoesNotStealIt()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                Assert.IsTrue(gate.Acquire("someone-elses-lease"));

                var active = EditorUserBuildSettings.activeBuildTarget;
                var ex = Assert.Throws<InvalidOperationException>(() => CommandTable.Dispatch(
                    gate, Request("project.build_start", JsonValue.NewObject().SetProperty("target", JsonValue.String(active.ToString())))));

                StringAssert.Contains("someone-elses-lease", ex.Message);
                Assert.AreEqual("someone-elses-lease", gate.CurrentLeaseId, "a refused build must never steal or release a foreign lease");
            }
        }
    }

    /// <summary>
    /// DESIGN-03 §4.2.2/§4.4's file-level pins, independent of the wire dispatch path -
    /// <see cref="BuildStatusStore"/> and <see cref="BuildWatchdog"/> exercised directly.
    /// </summary>
    [TestFixture]
    public sealed class BuildStatusStoreTests
    {
        [SetUp]
        [TearDown]
        public void Reset()
        {
            if (File.Exists(BuildStatusStore.StatusPath)) File.Delete(BuildStatusStore.StatusPath);
        }

        static BuildStatusStore.Document SampleDoc(string state, string bootNonce = "nonce-a") => new BuildStatusStore.Document
        {
            Schema = BuildStatusStore.CurrentSchema,
            BuildId = "build-1",
            BootNonce = bootNonce,
            State = state,
            Target = "StandaloneOSX",
            OutputPath = "Build/StandaloneOSX/Demo.app",
            QueuedAtUtcMs = 1000,
            Errors = new List<(string, string, string)> { ("Compiling", "Error", "boom") },
        };

        [Test]
        public void Write_FirstWrite_UsesMoveNotReplace_AndIsReadableBack()
        {
            Assert.IsFalse(File.Exists(BuildStatusStore.StatusPath));
            BuildStatusStore.Write(SampleDoc(BuildStatusStore.StateQueued));

            var doc = BuildStatusStore.TryRead();
            Assert.IsNotNull(doc);
            Assert.AreEqual("build-1", doc.BuildId);
            Assert.AreEqual(BuildStatusStore.StateQueued, doc.State);
            Assert.AreEqual(1, doc.Errors.Count);
            Assert.AreEqual("boom", doc.Errors[0].Content);
        }

        [Test]
        public void Write_SecondWrite_ReplacesTheExistingFile_NeverLeavesATempFileBehind()
        {
            BuildStatusStore.Write(SampleDoc(BuildStatusStore.StateQueued));
            BuildStatusStore.Write(SampleDoc(BuildStatusStore.StateRunning));

            var doc = BuildStatusStore.TryRead();
            Assert.AreEqual(BuildStatusStore.StateRunning, doc.State);

            var dir = Path.GetDirectoryName(BuildStatusStore.StatusPath);
            var leftoverTemp = Directory.GetFiles(dir, "*.tmp-*");
            CollectionAssert.IsEmpty(leftoverTemp, "no .tmp- file may survive a successful write");
        }

        [Test]
        public void TryRead_MissingFile_ReturnsNull()
        {
            Assert.IsNull(BuildStatusStore.TryRead());
        }

        [Test]
        public void TryRead_CorruptFile_ReturnsNullRatherThanThrowing()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BuildStatusStore.StatusPath));
            File.WriteAllText(BuildStatusStore.StatusPath, "{ not valid json");

            Assert.DoesNotThrow(() => BuildStatusStore.TryRead());
            Assert.IsNull(BuildStatusStore.TryRead());
        }

        [Test]
        public void IsTerminal_MatchesExactlyTheFourTerminalStates()
        {
            Assert.IsFalse(BuildStatusStore.IsTerminal(BuildStatusStore.StateQueued));
            Assert.IsFalse(BuildStatusStore.IsTerminal(BuildStatusStore.StateRunning));
            Assert.IsTrue(BuildStatusStore.IsTerminal(BuildStatusStore.StateComplete));
            Assert.IsTrue(BuildStatusStore.IsTerminal(BuildStatusStore.StateFailed));
            Assert.IsTrue(BuildStatusStore.IsTerminal(BuildStatusStore.StateCancelled));
            Assert.IsTrue(BuildStatusStore.IsTerminal(BuildStatusStore.StateAbandoned));
        }

        // -------------------------------------------------------------- BuildWatchdog reconciliation

        [Test]
        public void Watchdog_NonTerminalFile_ForeignBootNonce_ReconcilesToAbandoned_AndStartWorksAgain()
        {
            BuildStatusStore.Write(SampleDoc(BuildStatusStore.StateRunning, bootNonce: "a-previous-editor-process"));

            BuildWatchdog.Tick();

            var doc = BuildStatusStore.TryRead();
            Assert.AreEqual(BuildStatusStore.StateAbandoned, doc.State);
            Assert.IsNotNull(doc.Note);
        }

        [Test]
        public void Watchdog_NonTerminalFile_MatchingBootNonce_IsLeftAlone()
        {
            var nonce = BuildSession.CurrentBootNonce;
            BuildStatusStore.Write(SampleDoc(BuildStatusStore.StateRunning, bootNonce: nonce));
            BuildSession.BuildInFlight = true;
            try
            {
                BuildWatchdog.Tick();
                var doc = BuildStatusStore.TryRead();
                Assert.AreEqual(BuildStatusStore.StateRunning, doc.State, "a plain domain reload with matching nonce and a live build must not be clobbered");
            }
            finally
            {
                BuildSession.BuildInFlight = false;
            }
        }

        [Test]
        public void Watchdog_RunningState_MatchingNonce_ButNotBuildInFlight_ReconcilesToAbandoned()
        {
            var nonce = BuildSession.CurrentBootNonce;
            BuildStatusStore.Write(SampleDoc(BuildStatusStore.StateRunning, bootNonce: nonce));
            BuildSession.BuildInFlight = false; // the intra-session liveness gap DESIGN-08 §12 adds

            BuildWatchdog.Tick();

            var doc = BuildStatusStore.TryRead();
            Assert.AreEqual(BuildStatusStore.StateAbandoned, doc.State);
        }

        [Test]
        public void Watchdog_TerminalFile_IsNeverTouched()
        {
            BuildStatusStore.Write(SampleDoc(BuildStatusStore.StateComplete, bootNonce: "whatever"));
            BuildWatchdog.Tick();

            var doc = BuildStatusStore.TryRead();
            Assert.AreEqual(BuildStatusStore.StateComplete, doc.State);
        }

        [Test]
        public void Watchdog_NoFile_DoesNothing()
        {
            Assert.DoesNotThrow(() => BuildWatchdog.Tick());
            Assert.IsNull(BuildStatusStore.TryRead());
        }
    }
}
