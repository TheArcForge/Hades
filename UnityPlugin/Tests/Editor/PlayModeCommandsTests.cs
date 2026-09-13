// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// DESIGN-01 (docs/design/DESIGN-01-play-mode-control.md) §15.1/§15.1.1/§15.1.2's plugin EditMode
    /// suite. A Unity EditMode test cannot enter Play Mode from inside itself (the same reason the
    /// suite never really invokes TestRunnerBridge.TryStart - see ProjectCommandsTests' own doc
    /// comment) - every test here therefore drives <see cref="EditorPlayModeApi"/>'s seams instead,
    /// restoring them in TearDown per this codebase's stated hygiene rule, and reaches the handlers
    /// through <see cref="CommandTable.Dispatch"/>, the only entry point any test in this folder uses.
    ///
    /// The one thing NOT provable here is a real domain reload's effect on this design - that is
    /// DESIGN-01 §15.4's live end-to-end pass, against a real GUI Editor, which this suite does not
    /// attempt.
    /// </summary>
    [TestFixture]
    public sealed class PlayModeCommandsTests
    {
        [SetUp]
        public void SetUp()
        {
            SceneTestFixtures.ResetScene();
            PlayModeSession.Clear();
            ResetPlayModeApiSeams();

            ConsoleLogBuffer.Clear();
            ConsoleLogBuffer.ResetForTests();
            ConsoleLogBuffer.FlushPath = Path.Combine(Path.GetTempPath(), "hades-test-console-flush-" + Guid.NewGuid().ToString("N") + ".json");
        }

        [TearDown]
        public void TearDown()
        {
            PlayModeSession.Clear();
            ResetPlayModeApiSeams();

            ConsoleLogBuffer.Clear();
            if (File.Exists(ConsoleLogBuffer.FlushPath)) File.Delete(ConsoleLogBuffer.FlushPath);
            ConsoleLogBuffer.FlushPath = Path.Combine(Application.persistentDataPath, "HadesConsoleLog.json");

            // ResetForTests() (used mid-test by several tests here, via ConsoleLogBuffer.Install())
            // must never leave the REAL, boot-installed Application.logMessageReceivedThreaded
            // subscription torn down for the rest of this batchmode run - re-Install() restores it,
            // matching the exact state HadesBoot's own static ctor already put it in before any test
            // in this whole suite ran. Idempotent when nothing was touched.
            ConsoleLogBuffer.ResetForTests();
            ConsoleLogBuffer.Install();
        }

        static void ResetPlayModeApiSeams()
        {
            EditorPlayModeApi.EnterPlayMode = () => EditorApplication.EnterPlaymode();
            EditorPlayModeApi.ExitPlayMode = () => EditorApplication.ExitPlaymode();
            EditorPlayModeApi.SetPaused = paused => EditorApplication.isPaused = paused;
            EditorPlayModeApi.StepFrame = () => EditorApplication.Step();
            EditorPlayModeApi.IsPlaying = () => EditorApplication.isPlaying;
            EditorPlayModeApi.IsPaused = () => EditorApplication.isPaused;
            EditorPlayModeApi.PlayClock = () => DateTime.UtcNow;
            EditorPlayModeApi.ScheduleAfterThisTick = action => EditorApplication.delayCall += () => action();
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

        static string StringProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        static bool BoolProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Boolean && v.AsBoolean();

        // ============================================================== 1: Enter stamps before scheduling

        [Test]
        public void Enter_StampsSessionStateBeforeSchedulingTheTransition()
        {
            var stampedBeforeSchedule = false;
            EditorPlayModeApi.ScheduleAfterThisTick = action =>
            {
                // Deliberately does NOT invoke 'action' - proves the stamp happened strictly before
                // scheduling, independent of whether the scheduled action itself ever runs.
                stampedBeforeSchedule = PlayModeSession.HasSession;
            };

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("play.enter", JsonValue.NewObject()));
            }

            Assert.IsTrue(stampedBeforeSchedule, "SessionState must already be stamped by the time 'enter' schedules the transition");
            Assert.AreEqual(PlayModeSession.PhaseRequested, PlayModeSession.Phase);
        }

        // ============================================================== 2/10: enter refuses on a held lease; exit does not

        [Test]
        public void Enter_WhileALeaseIsHeld_ThrowsActionableError_DoesNotStealOrReleaseIt()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                Assert.IsTrue(gate.Acquire("someone-elses-session", TimeSpan.FromMinutes(5)));

                var ex = Assert.Throws<InvalidOperationException>(() => CommandTable.Dispatch(gate, Request("play.enter", JsonValue.NewObject())));
                StringAssert.Contains("someone-elses-session", ex.Message);
                StringAssert.Contains("script_editing_session", ex.Message);
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);

                Assert.IsTrue(gate.IsHeld);
                Assert.AreEqual("someone-elses-session", gate.CurrentLeaseId);
                Assert.IsFalse(PlayModeSession.HasSession, "a refused enter must never stamp a session record");

                gate.Release("someone-elses-session");
            }
        }

        [Test]
        public void Exit_WhileALeaseIsHeld_StillRequestsExit()
        {
            // DESIGN-01 §5.1 (revised, third draft): exit is UNCONDITIONAL - no lease check at all,
            // and (per that same revision) no advisory note either, since exit triggers no reload and
            // there is nothing for a held lease to defer. NOTE: an earlier revision of §15.1's own
            // test list named this test "...AndReportsTheLeaseInItsNote", describing draft 2's
            // behaviour (a hedging note) - that text was not updated when §5.1 was revised to draft 3
            // ("no lease check and no advisory note... The defensiveness is therefore vestigial and
            // is removed"). Implemented here per §5.1's explicit, most-recently-revised text, which
            // this suite treats as authoritative over the stale test-list wording - see this lane's
            // final report for the discrepancy.
            EditorPlayModeApi.IsPlaying = () => true;
            var exitCalls = 0;
            EditorPlayModeApi.ExitPlayMode = () => exitCalls++;
            EditorPlayModeApi.ScheduleAfterThisTick = action => action(); // run inline for this test

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                Assert.IsTrue(gate.Acquire("someone-elses-session", TimeSpan.FromMinutes(5)));

                JsonValue result = null;
                Assert.DoesNotThrow(() => result = CommandTable.Dispatch(gate, Request("play.exit", JsonValue.NewObject())));

                Assert.AreEqual("exiting", StringProp(result, "state"));
                Assert.IsTrue(BoolProp(result, "wasPlaying"));
                Assert.AreEqual(1, exitCalls);

                // The other session's lease is still completely untouched - exit takes no lease at all.
                Assert.IsTrue(gate.IsHeld);
                Assert.AreEqual("someone-elses-session", gate.CurrentLeaseId);

                gate.Release("someone-elses-session");
            }
        }

        // ============================================================== 3: exit idempotent no-op

        [Test]
        public void Exit_WithNoSession_IsIdempotentNoOp_CallsExitPlayModeZeroTimes()
        {
            EditorPlayModeApi.IsPlaying = () => false;
            var exitCalls = 0;
            EditorPlayModeApi.ExitPlayMode = () => exitCalls++;

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("play.exit", JsonValue.NewObject()));

                Assert.AreEqual("editMode", StringProp(result, "state"));
                Assert.IsFalse(BoolProp(result, "wasPlaying"));
                Assert.AreEqual(0, exitCalls, "nothing was playing - ExitPlayMode must never be called");
            }
        }

        // ============================================================== 4: watchdog deadline -> exit exactly once

        [Test]
        public void Watchdog_PastDeadline_CallsExitExactlyOnce()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            EditorPlayModeApi.PlayClock = () => now;
            EditorPlayModeApi.IsPlaying = () => true;
            var exitCalls = 0;
            EditorPlayModeApi.ExitPlayMode = () => exitCalls++;

            PlayModeSession.Stamp("s1", PlayModeSession.PhasePlaying, now, 10);
            PlayModeSession.MarkPlaying(now);

            now = now.AddSeconds(11); // past the 10s ttl
            PlayModeWatchdog.Tick();
            Assert.AreEqual(1, exitCalls);
            Assert.AreEqual(PlayModeSession.PhaseExitRequested, PlayModeSession.Phase);

            // A second tick, still "playing" (IsPlaying seam untouched) and still past deadline, must
            // NOT call ExitPlayMode again - de-duplicated via the phase flip (DESIGN-01 §9.3).
            PlayModeWatchdog.Tick();
            Assert.AreEqual(1, exitCalls);
        }

        // ============================================================== 5: user-owned session never auto-exits

        [Test]
        public void Watchdog_UserOwnedPlaySession_NeverExits()
        {
            EditorPlayModeApi.IsPlaying = () => true; // playing, but no Hades SessionState record at all
            var exitCalls = 0;
            EditorPlayModeApi.ExitPlayMode = () => exitCalls++;

            Assert.IsFalse(PlayModeSession.HasSession);

            PlayModeWatchdog.Tick();
            PlayModeWatchdog.Tick();

            Assert.AreEqual(0, exitCalls);
            Assert.IsFalse(PlayModeSession.HasSession, "Hades must never adopt a session it did not start");
        }

        // ============================================================== 6: boot/tick reconciliation table (§9.2)

        [Test]
        public void Reconcile_RequestedAndPlaying_FlipsToPlaying()
        {
            var now = DateTime.UtcNow;
            EditorPlayModeApi.PlayClock = () => now;
            EditorPlayModeApi.IsPlaying = () => true;
            PlayModeSession.Stamp("s", PlayModeSession.PhaseRequested, now, 120);

            PlayModeWatchdog.Tick();

            Assert.AreEqual(PlayModeSession.PhasePlaying, PlayModeSession.Phase);
            Assert.IsTrue(PlayModeSession.StartedAtUtc.HasValue);
        }

        [Test]
        public void Reconcile_RequestedAndNotPlaying_DeadlineNotPassed_LeavesRecordAlone()
        {
            var now = DateTime.UtcNow;
            EditorPlayModeApi.PlayClock = () => now;
            EditorPlayModeApi.IsPlaying = () => false;
            PlayModeSession.Stamp("s", PlayModeSession.PhaseRequested, now, 120);

            PlayModeWatchdog.Tick();

            Assert.IsTrue(PlayModeSession.HasSession);
            Assert.AreEqual(PlayModeSession.PhaseRequested, PlayModeSession.Phase);
        }

        [Test]
        public void Boot_SessionRecordedButNotPlaying_ClearsSessionState()
        {
            // "session, phase=requested | IsPlaying()=false | deadline PASSED" (DESIGN-01 §9.2/§9.3):
            // the enter never landed - cleared, ExitPlayMode() called ZERO times.
            var now = DateTime.UtcNow;
            EditorPlayModeApi.PlayClock = () => now;
            EditorPlayModeApi.IsPlaying = () => false;
            var exitCalls = 0;
            EditorPlayModeApi.ExitPlayMode = () => exitCalls++;
            PlayModeSession.Stamp("s", PlayModeSession.PhaseRequested, now, 10);

            now = now.AddSeconds(11);
            PlayModeWatchdog.Tick();

            Assert.IsFalse(PlayModeSession.HasSession);
            Assert.AreEqual(0, exitCalls);
        }

        [Test]
        public void Reconcile_PlayingAndNotPlaying_ClearsSessionState()
        {
            // Covers user Stop, project.end_script_editing's own recompile, or a crash - all collapse
            // to the same observed fact: was playing, is not anymore.
            var now = DateTime.UtcNow;
            EditorPlayModeApi.PlayClock = () => now;
            EditorPlayModeApi.IsPlaying = () => true;
            PlayModeSession.Stamp("s", PlayModeSession.PhasePlaying, now, 120);
            PlayModeSession.MarkPlaying(now);

            EditorPlayModeApi.IsPlaying = () => false;
            PlayModeWatchdog.Tick();

            Assert.IsFalse(PlayModeSession.HasSession);
        }

        [Test]
        public void Reconcile_ExitRequestedAndNotPlaying_ClearsSessionState()
        {
            var now = DateTime.UtcNow;
            EditorPlayModeApi.PlayClock = () => now;
            PlayModeSession.Stamp("s", PlayModeSession.PhaseExitRequested, now, 120);
            EditorPlayModeApi.IsPlaying = () => false;

            PlayModeWatchdog.Tick();

            Assert.IsFalse(PlayModeSession.HasSession);
        }

        // ============================================================== 7: activity renews the deadline

        [Test]
        public void AnyPlayCommand_RenewsTheDeadline()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            EditorPlayModeApi.PlayClock = () => now;
            EditorPlayModeApi.IsPlaying = () => true;
            PlayModeSession.Stamp("s", PlayModeSession.PhasePlaying, now, 100);
            var deadlineBefore = PlayModeSession.DeadlineUtc.Value;

            now = now.AddSeconds(50);
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("play.status", JsonValue.NewObject()));
            }

            var deadlineAfter = PlayModeSession.DeadlineUtc.Value;
            Assert.Greater(deadlineAfter, deadlineBefore, "play.status must renew a live session's deadline");
            Assert.AreEqual(now.AddSeconds(100), deadlineAfter);
        }

        [Test]
        public void AnyOtherCommand_DoesNotRenewTheDeadline()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            EditorPlayModeApi.PlayClock = () => now;
            EditorPlayModeApi.IsPlaying = () => false; // so the mutation guard does not interfere
            PlayModeSession.Stamp("s", PlayModeSession.PhasePlaying, now, 100);
            var deadlineBefore = PlayModeSession.DeadlineUtc.Value;

            now = now.AddSeconds(50);
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("project.get_console_log", JsonValue.NewObject()));
            }

            Assert.AreEqual(deadlineBefore, PlayModeSession.DeadlineUtc.Value, "an unrelated command must never renew a play session's deadline");
        }

        // ============================================================== 8: parameterised allow/refuse over every handler

        [Test]
        public void Dispatch_WhilePlaying_RefusesEveryMethodNotOnTheAllowList()
        {
            EditorPlayModeApi.IsPlaying = () => true;

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                foreach (var method in CommandTable.Handlers.Keys)
                {
                    var allowed = CommandTable.AllowedWhilePlaying.TryGetValue(method, out var overrideMessage) && overrideMessage == null;

                    if (allowed) continue; // proven not to throw the guard by the other tests in this file/class

                    var ex = Assert.Throws<InvalidOperationException>(
                        () => CommandTable.Dispatch(gate, Request(method, JsonValue.NewObject())),
                        "method '" + method + "' must be refused while playing unless explicitly allow-listed");
                    Assert.IsNotEmpty(ex.Message);
                }
            }
        }

        // ============================================================== 9/18: ConsoleLogBuffer flush/restore

        [Test]
        public void ConsoleLogBuffer_FlushThenRestore_PreservesOrderAndRespectsCapacity()
        {
            ConsoleLogBuffer.Capture(LogType.Log, "one", "");
            ConsoleLogBuffer.Capture(LogType.Warning, "two", "");
            ConsoleLogBuffer.Capture(LogType.Error, "three", "");

            ConsoleLogBuffer.FlushBeforeReload();
            Assert.IsTrue(File.Exists(ConsoleLogBuffer.FlushPath));

            ConsoleLogBuffer.Clear();
            ConsoleLogBuffer.ResetForTests();
            ConsoleLogBuffer.Install();

            var (selected, totalBuffered, _) = ConsoleLogBuffer.GetRecent(10, null);
            Assert.AreEqual(3, totalBuffered);
            Assert.AreEqual("one", selected[0].Message);
            Assert.AreEqual("two", selected[1].Message);
            Assert.AreEqual("three", selected[2].Message);
        }

        [Test]
        public void ConsoleLogBuffer_RestoreDeletesTheFlushFile()
        {
            ConsoleLogBuffer.Capture(LogType.Log, "hello", "");
            ConsoleLogBuffer.FlushBeforeReload();
            Assert.IsTrue(File.Exists(ConsoleLogBuffer.FlushPath));

            ConsoleLogBuffer.Clear();
            ConsoleLogBuffer.ResetForTests();
            ConsoleLogBuffer.Install();

            Assert.IsFalse(File.Exists(ConsoleLogBuffer.FlushPath), "the flush file must be consumed (deleted) on restore");
        }

        [Test]
        public void ConsoleLogBuffer_SecondInstallAfterRestore_FindsNothingToRestore()
        {
            // The property that actually matters: it is the SECOND Install() - standing in for
            // tomorrow's Editor start - that would resurrect yesterday's logs if the file were not
            // deleted. A test that only checked File.Exists==false would pass an implementation that
            // deleted the file but had already double-restored into the ring.
            ConsoleLogBuffer.Capture(LogType.Log, "yesterday", "");
            ConsoleLogBuffer.FlushBeforeReload();

            ConsoleLogBuffer.Clear();
            ConsoleLogBuffer.ResetForTests();
            ConsoleLogBuffer.Install(); // first restore - consumes the file

            ConsoleLogBuffer.Clear(); // simulate a fresh ring the way a new Editor session would have
            ConsoleLogBuffer.ResetForTests();
            ConsoleLogBuffer.Install(); // second restore - must find nothing

            var (_, totalBuffered, _) = ConsoleLogBuffer.GetRecent(10, null);
            Assert.AreEqual(0, totalBuffered, "a second Install() must not resurrect an already-consumed flush");
        }

        // ============================================================== 11: every named recovery tool is callable while playing

        [Test]
        public void EveryToolNamedInAPlayModeRefusalMessage_IsCallableWhilePlaying()
        {
            var messages = new[]
            {
                CommandTable.GenericPlayModeRefusal("scene.apply"),
                PlayModeCommands.RunTestsRefusedWhilePlayingMessage,
                PlayModeCommands.BakeStartRefusedWhilePlayingMessage,
                PlayModeCommands.BuildStartRefusedWhilePlayingMessage,
            };

            var toolShapedToken = new Regex(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b");

            foreach (var message in messages)
            {
                foreach (Match match in toolShapedToken.Matches(message))
                {
                    var tool = match.Value;
                    Assert.IsTrue(ToolWireMethods.ByTool.TryGetValue(tool, out var wireMethods), "'" + tool + "' has no ToolWireMethods.ByTool entry");
                    Assert.IsTrue(
                        wireMethods.Any(m => CommandTable.AllowedWhilePlaying.TryGetValue(m, out var v) && v == null),
                        "'" + tool + "' is named in a play-mode refusal message, but none of its wire methods (" + string.Join(", ", wireMethods) + ") are allow-listed");
                }
            }
        }

        // ============================================================== 12: the allow-list is exactly the expected set

        [Test]
        public void AllowedWhilePlaying_IsExactlyTheExpectedSet()
        {
            // DESIGN-01 §6.2's original 13, PLUS the roadmap's Lane B scope (DESIGN-08 §7.1/§7.3):
            // three explicit refuse entries and three allow entries for commands other lanes have not
            // built yet - 19 total. See CommandTable.AllowedWhilePlaying's own doc comment.
            var expectedAllowed = new[]
            {
                "play.enter", "play.exit", "play.set_paused", "play.step", "play.status",
                "lease.renew", "lease.release",
                "project.end_script_editing",
                "inspector.inspect", "inspector.select",
                "project.get_console_log", "project.get_test_results",
                "capture.view",
                "scene.bake:status", "scene.bake:cancel", "project.build_targets",
            };
            var expectedExplicitlyRefused = new[] { "project.run_tests", "scene.bake:start", "project.build_start" };

            var actual = CommandTable.AllowedWhilePlaying;
            Assert.AreEqual(expectedAllowed.Length + expectedExplicitlyRefused.Length, actual.Count);

            foreach (var key in expectedAllowed)
            {
                Assert.IsTrue(actual.TryGetValue(key, out var v), "missing allow entry: " + key);
                Assert.IsNull(v, "'" + key + "' must be allowed (null override)");
            }

            foreach (var key in expectedExplicitlyRefused)
            {
                Assert.IsTrue(actual.TryGetValue(key, out var v), "missing explicit-refuse entry: " + key);
                Assert.IsNotNull(v, "'" + key + "' must be an EXPLICIT refuse (non-null override), not merely absent");
            }

            // Never re-introduced - see DESIGN-01 §6.2's "Refused, deliberately" table.
            CollectionAssert.DoesNotContain(actual.Keys, "lease.acquire");
            CollectionAssert.DoesNotContain(actual.Keys, "project.begin_script_editing");
        }

        // ============================================================== 13/14/15: reload-independence

        [Test]
        public void Reconcile_RunsOnEveryTick_NotOnlyAtInstall()
        {
            var now = DateTime.UtcNow;
            EditorPlayModeApi.PlayClock = () => now;
            EditorPlayModeApi.IsPlaying = () => false;
            PlayModeSession.Stamp("s", PlayModeSession.PhaseRequested, now, 120);

            PlayModeWatchdog.Install(); // ticks once immediately - still 'requested' (not playing yet)
            Assert.AreEqual(PlayModeSession.PhaseRequested, PlayModeSession.Phase);

            EditorPlayModeApi.IsPlaying = () => true; // flipped with NO simulated reload/re-Install
            PlayModeWatchdog.Tick();

            Assert.AreEqual(PlayModeSession.PhasePlaying, PlayModeSession.Phase, "reconciliation must run on every tick, not only at Install()");
        }

        [Test]
        public void Reconcile_ExitWithNoReload_ClearsTheRecordWithinOneTick()
        {
            var now = DateTime.UtcNow;
            EditorPlayModeApi.PlayClock = () => now;
            PlayModeSession.Stamp("s", PlayModeSession.PhaseExitRequested, now, 120);
            EditorPlayModeApi.IsPlaying = () => false; // exit does not reload - this is the production path

            PlayModeWatchdog.Tick();

            Assert.IsFalse(PlayModeSession.HasSession);
        }

        [Test]
        public void Reconcile_EnterWithNoReload_FlipsRequestedToPlayingWithinOneTick()
        {
            // Covers a project with Unity's "Enter Play Mode Options" reload disabled - the
            // requested -> playing flip must not depend on a reload event at all.
            var now = DateTime.UtcNow;
            EditorPlayModeApi.PlayClock = () => now;
            PlayModeSession.Stamp("s", PlayModeSession.PhaseRequested, now, 120);
            EditorPlayModeApi.IsPlaying = () => true;

            PlayModeWatchdog.Tick();

            Assert.AreEqual(PlayModeSession.PhasePlaying, PlayModeSession.Phase);
        }

        // ============================================================== 16: step takes no 'frames' parameter

        [Test]
        public void Step_AdvancesExactlyOneFrame_AndTakesNoCount()
        {
            var stepCalls = 0;
            EditorPlayModeApi.StepFrame = () => stepCalls++;
            EditorPlayModeApi.IsPlaying = () => true;

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("play.step", JsonValue.NewObject()));
                Assert.AreEqual(1, stepCalls);

                var withFrames = JsonValue.NewObject().SetProperty("frames", JsonValue.Integer(3));
                Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("play.step", withFrames)));
                Assert.AreEqual(1, stepCalls, "a rejected 'frames' parameter must not have stepped first");
            }
        }

        // ============================================================== 17: the refusal message's wording

        [Test]
        public void PlayModeRefusalMessage_StatesTheSplitRatherThanAOneSidedClaim()
        {
            // DESIGN-01 §6.2: the generic message must not assert a single, unqualified fate ("your
            // change WILL be discarded"/"WAS discarded"/"you WILL LOSE it") for a SPECIFIC refused
            // command - that claim is wrong for the majority of commands the guard can refuse (most
            // persist as asset-file writes). The shipped wording legitimately uses "discarded" as HALF
            // of a balanced pair ("...would be discarded... while an asset-file write would
            // persist..."), which this test allows; it only guards against reintroducing the old,
            // one-sided claim.
            var messageForAScene = CommandTable.GenericPlayModeRefusal("scene.apply");
            var messageForAnAsset = CommandTable.GenericPlayModeRefusal("material.apply");

            // Never a bare, unqualified claim about THIS call's own fate:
            foreach (var message in new[] { messageForAScene, messageForAnAsset })
            {
                StringAssert.DoesNotContain("will be discarded", message);
                StringAssert.DoesNotContain("was discarded", message);
                StringAssert.DoesNotContain("you will lose", message);
                StringAssert.DoesNotContain("your change will be", message);
            }

            // The template is IDENTICAL for a scene-touching and an asset-touching command, modulo
            // the method name - proving it never resolves to one side for a specific command.
            Assert.AreEqual(
                messageForAScene.Replace("scene.apply", "X"),
                messageForAnAsset.Replace("material.apply", "X"));

            // And it does present the balanced split (both halves), not silence about consequences:
            StringAssert.Contains("discarded", messageForAScene);
            StringAssert.Contains("persist", messageForAScene);
        }

        // ============================================================== general coverage: set_paused / status

        [Test]
        public void SetPaused_TogglesAndReportsCurrentState()
        {
            EditorPlayModeApi.IsPlaying = () => true;
            var pausedValue = false;
            EditorPlayModeApi.SetPaused = p => pausedValue = p;
            EditorPlayModeApi.IsPaused = () => pausedValue;

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("paused", JsonValue.Bool(true));
                var result = CommandTable.Dispatch(gate, Request("play.set_paused", @params));

                Assert.IsTrue(BoolProp(result, "paused"));
                Assert.AreEqual("paused", StringProp(result, "state"));
            }
        }

        [Test]
        public void SetPaused_MissingParam_Throws()
        {
            EditorPlayModeApi.IsPlaying = () => true;
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("play.set_paused", JsonValue.NewObject())));
            }
        }

        [Test]
        public void Status_NoSession_NotPlaying_ReportsEditMode()
        {
            EditorPlayModeApi.IsPlaying = () => false;
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("play.status", JsonValue.NewObject()));
                Assert.AreEqual("editMode", StringProp(result, "state"));
                Assert.IsFalse(BoolProp(result, "ownedByHades"));
            }
        }

        [Test]
        public void Status_NoSession_Playing_ReportsUserOwnedPlaying()
        {
            EditorPlayModeApi.IsPlaying = () => true;
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("play.status", JsonValue.NewObject()));
                Assert.AreEqual("playing", StringProp(result, "state"));
                Assert.IsFalse(BoolProp(result, "ownedByHades"));
                Assert.IsTrue(result.TryGetProperty("sessionId", out var sid) && sid.Kind == JsonValueKind.Null);
            }
        }

        [Test]
        public void Enter_TtlSeconds_ClampedToRange()
        {
            EditorPlayModeApi.IsPlaying = () => false;
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            EditorPlayModeApi.PlayClock = () => now;
            EditorPlayModeApi.ScheduleAfterThisTick = action => { }; // never actually enter

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var tooLow = JsonValue.NewObject().SetProperty("ttlSeconds", JsonValue.Integer(1));
                CommandTable.Dispatch(gate, Request("play.enter", tooLow));
                Assert.AreEqual(now.AddSeconds(PlayModeCommands.MinTtlSeconds), PlayModeSession.DeadlineUtc.Value);
                PlayModeSession.Clear();

                var tooHigh = JsonValue.NewObject().SetProperty("ttlSeconds", JsonValue.Integer(10000));
                CommandTable.Dispatch(gate, Request("play.enter", tooHigh));
                Assert.AreEqual(now.AddSeconds(PlayModeCommands.MaxTtlSeconds), PlayModeSession.DeadlineUtc.Value);
            }
        }

        [Test]
        public void Enter_WhileUserOwnedPlayIsActive_Refuses()
        {
            EditorPlayModeApi.IsPlaying = () => true; // no Hades session recorded - user-owned

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var ex = Assert.Throws<InvalidOperationException>(() => CommandTable.Dispatch(gate, Request("play.enter", JsonValue.NewObject())));
                StringAssert.Contains("play_mode", ex.Message);
                LiveMcpToolNames.AssertMessageNamesOnlyLiveTools(ex.Message);
            }
        }

        [Test]
        public void Enter_WhileAHadesSessionAlreadyPlaying_IsIdempotent_RenewsAndReturnsExistingId()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            EditorPlayModeApi.PlayClock = () => now;
            EditorPlayModeApi.IsPlaying = () => true;
            PlayModeSession.Stamp("existing-session", PlayModeSession.PhasePlaying, now, 50);
            PlayModeSession.MarkPlaying(now);

            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("ttlSeconds", JsonValue.Integer(999));
                var result = CommandTable.Dispatch(gate, Request("play.enter", @params));

                Assert.AreEqual("existing-session", StringProp(result, "sessionId"));
                Assert.AreEqual(now.AddSeconds(50), PlayModeSession.DeadlineUtc.Value, "a renewal must keep the ORIGINAL ttl, never the newly requested one");
            }
        }
    }
}
