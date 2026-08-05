// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Hades.Contract.Wire;
using Hades.Runtime;
using Hades.Tools;
using NUnit.Framework;
using UnityEditor;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// Class-3 (multi-call session - see the "52 Editor tools" plan's operation-class table)
    /// script-editing session tools: project.begin_script_editing / project.end_script_editing,
    /// dispatched through <see cref="CommandTable.Dispatch"/> the same way every other suite in
    /// this folder does.
    ///
    /// Deliberately its OWN file, not folded into ProjectCommandsTests.cs: every handler tested
    /// there is class 2, whose defining, universally-asserted property is
    /// AssertLeaseCleanlyReleased ("after ANY call - success or exception - no lease may remain
    /// held"). Class 3 is the DELIBERATE INVERSE of that property (see ReloadGate's own class doc
    /// comment and the "52 Editor tools" plan's Task 4): the lease is meant to survive an
    /// exception, survive the call returning, and be released ONLY by EndScriptEditing, TTL
    /// expiry, disconnect, or boot reconciliation. Mixing both invariants into one file risks
    /// exactly the confusion that produced the old package's unbalanced-unlock bug in the first
    /// place (see DomainReloadTools.cs's own EndScriptEditing, which force-unlocked "in case
    /// auto-lock is still held") - this file exists so "class 3 is different" is visible from the
    /// file layout itself, not just a comment a future reader might skip.
    ///
    /// Same SessionState hygiene as ReloadGateTests/ReloadGateCriticalSuite: BeginScriptEditing/
    /// EndScriptEditing call straight through to gate.Acquire/gate.Release, which touch real,
    /// process-wide Unity SessionState - erased before and after every test here so a leftover
    /// flag from one test (e.g. a deliberately-abandoned Begin) can never corrupt the next test's
    /// freshly constructed gate.
    /// </summary>
    [TestFixture]
    public sealed class ScriptEditingSessionTests
    {
        [SetUp]
        public void SetUp() => SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

        [TearDown]
        public void TearDown() => SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

        static JsonRpcRequest Request(string method, JsonValue @params) =>
            new JsonRpcRequest { Id = JsonValue.Integer(1), Method = method, Params = @params };

        static (ReloadGate gate, FakeEditorLockApi fake, MainThreadPump pump) NoopGateParts() =>
            NoopGateParts(() => DateTime.UtcNow);

        static (ReloadGate gate, FakeEditorLockApi fake, MainThreadPump pump) NoopGateParts(Func<DateTime> clock)
        {
            var fake = new FakeEditorLockApi();
            var pump = new MainThreadPump();
            var gate = new ReloadGate(fake, pump, clock, TimeSpan.FromHours(1));
            return (gate, fake, pump);
        }

        static string StringProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.String ? v.AsString() : null;

        static long LongProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Integer ? v.AsInteger() : -1;

        static bool BoolProp(JsonValue result, string key) =>
            result.TryGetProperty(key, out var v) && v.Kind == JsonValueKind.Boolean && v.AsBoolean();

        /// <summary>Same polling shape as ReloadGateCriticalSuite.WaitUntilTicked - an off-thread
        /// TTL release is enqueued from a background Timer thread, not something a test can
        /// Join(), so the only way to observe it land is to keep ticking the pump until it
        /// does.</summary>
        static bool WaitUntilTicked(MainThreadPump pump, Func<bool> condition, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            do
            {
                pump.Tick();
                if (condition()) return true;
                Thread.Sleep(2);
            } while (stopwatch.Elapsed < timeout);
            pump.Tick();
            return condition();
        }

        sealed class OrderTrackingLockApi : IEditorLockApi
        {
            readonly List<string> _order;
            public OrderTrackingLockApi(List<string> order) => _order = order;
            public void Lock() => _order.Add("lock");
            public void Unlock() => _order.Add("unlock");
        }

        // ---------------------------------------------------------------- project.begin_script_editing

        [Test]
        public void BeginScriptEditing_AcquiresLease_ReturnsLeaseIdAndActualExpiry()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var (gate, fake, pump) = NoopGateParts(() => clock);
            using (pump) using (gate)
            {
                var result = CommandTable.Dispatch(gate, Request("project.begin_script_editing", JsonValue.NewObject()));

                Assert.AreEqual(ProjectCommands.ScriptEditingLeaseId, StringProp(result, "leaseId"));
                var expectedExpiryMs = new DateTimeOffset(clock + ReloadGate.DefaultTtl).ToUnixTimeMilliseconds();
                Assert.AreEqual(expectedExpiryMs, LongProp(result, "expiresAtUtcMs"),
                    "must report the ACTUAL expiry the gate applied, never merely echo a requested/default value");

                Assert.IsTrue(gate.IsHeld);
                Assert.AreEqual(ProjectCommands.ScriptEditingLeaseId, gate.CurrentLeaseId);
                Assert.AreEqual(1, fake.LockCalls);
                Assert.AreEqual(0, fake.UnlockCalls);

                CommandTable.Dispatch(gate, Request("project.end_script_editing", JsonValue.NewObject()));
            }
        }

        [Test]
        public void BeginScriptEditing_CustomTtlSeconds_ActualExpiryReflectsIt()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var (gate, fake, pump) = NoopGateParts(() => clock);
            using (pump) using (gate)
            {
                var @params = JsonValue.NewObject().SetProperty("ttlSeconds", JsonValue.Integer(120));
                var result = CommandTable.Dispatch(gate, Request("project.begin_script_editing", @params));

                var expectedExpiryMs = new DateTimeOffset(clock.AddSeconds(120)).ToUnixTimeMilliseconds();
                Assert.AreEqual(expectedExpiryMs, LongProp(result, "expiresAtUtcMs"));
                Assert.AreEqual(TimeSpan.FromSeconds(120), gate.CurrentLease.Ttl);

                CommandTable.Dispatch(gate, Request("project.end_script_editing", JsonValue.NewObject()));
            }
        }

        [Test]
        public void BeginScriptEditing_CalledAgainBeforeEnd_RenewsSameLease_NeverCallsLockTwice()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var (gate, fake, pump) = NoopGateParts(() => clock);
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("project.begin_script_editing", JsonValue.NewObject()));
                var firstExpiry = gate.CurrentLease.ExpiresAtUtc;

                clock += TimeSpan.FromSeconds(10); // real activity - a genuine second Begin call - well within the TTL
                var second = CommandTable.Dispatch(gate, Request("project.begin_script_editing", JsonValue.NewObject()));

                Assert.AreEqual(ProjectCommands.ScriptEditingLeaseId, StringProp(second, "leaseId"),
                    "calling Begin again mid-session must renew the SAME lease, not be rejected as a different one");
                Assert.Greater(gate.CurrentLease.ExpiresAtUtc, firstExpiry, "renewal must push the expiry out");
                Assert.AreEqual(1, fake.LockCalls, "re-acquiring the same lease must never call Lock a second time");
                Assert.IsTrue(gate.IsHeld);

                CommandTable.Dispatch(gate, Request("project.end_script_editing", JsonValue.NewObject()));
            }
        }

        // ---------------------------------------------------------------- project.end_script_editing

        [Test]
        public void EndScriptEditing_ReleasesLease_ThenTriggersRecompile_OrderProven()
        {
            var order = new List<string>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(new OrderTrackingLockApi(order), pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            CommandTable.Dispatch(gate, Request("project.begin_script_editing", JsonValue.NewObject()));

            var original = ProjectCommands.TriggerRecompile;
            ProjectCommands.TriggerRecompile = () => order.Add("recompile");
            JsonValue result;
            try
            {
                result = CommandTable.Dispatch(gate, Request("project.end_script_editing", JsonValue.NewObject()));
            }
            finally
            {
                ProjectCommands.TriggerRecompile = original;
            }

            Assert.IsTrue(BoolProp(result, "released"));
            Assert.IsTrue(BoolProp(result, "requested"));
            CollectionAssert.AreEqual(new[] { "lock", "unlock", "recompile" }, order,
                "release must happen strictly BEFORE the recompile trigger - never fight its own lock, exactly as "
                + "project.recompile_scripts already proves for class 2, mirrored here for class 3's Begin/End pair");
            Assert.IsFalse(gate.IsHeld);
        }

        [Test]
        public void EndScriptEditing_WithoutMatchingBegin_SucceedsIdempotently_CallsUnlockZeroTimes()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                var original = ProjectCommands.TriggerRecompile;
                var recompileCalls = 0;
                ProjectCommands.TriggerRecompile = () => recompileCalls++;
                JsonValue result;
                try
                {
                    result = CommandTable.Dispatch(gate, Request("project.end_script_editing", JsonValue.NewObject()));
                }
                finally
                {
                    ProjectCommands.TriggerRecompile = original;
                }

                Assert.IsFalse(BoolProp(result, "released"), "nothing was held, so nothing was actually released");
                Assert.IsTrue(BoolProp(result, "requested"), "still asks Unity to recompile - the old package's End always did this too");
                Assert.AreEqual(1, recompileCalls);

                // The exact bug this whole plan exists to make impossible: the old EndScriptEditing
                // called UnlockReloadAssemblies() UNCONDITIONALLY "in case auto-lock is still
                // held", which could drive Unity's native counter to -1 (see
                // DomainReloadTools.cs). This is the fake's signed counter never going negative,
                // and Unlock never being called when there was nothing of ours to release.
                Assert.AreEqual(0, fake.UnlockCalls);
                Assert.AreEqual(0, fake.LockCalls);
                Assert.AreEqual(0, fake.Counter);
                Assert.GreaterOrEqual(fake.Counter, 0);
                Assert.IsFalse(gate.IsHeld);
            }
        }

        [Test]
        public void EndScriptEditing_CalledTwiceInARow_SecondCallAlsoCallsUnlockZeroTimes()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("project.begin_script_editing", JsonValue.NewObject()));
                CommandTable.Dispatch(gate, Request("project.end_script_editing", JsonValue.NewObject()));
                Assert.AreEqual(1, fake.UnlockCalls);

                CommandTable.Dispatch(gate, Request("project.end_script_editing", JsonValue.NewObject()));

                Assert.AreEqual(1, fake.UnlockCalls, "a second End, with nothing left to release, must not unlock again");
                Assert.AreEqual(0, fake.Counter);
                Assert.GreaterOrEqual(fake.Counter, 0);
            }
        }

        // ---------------------------------------------------------------- the TTL: Begin, never End

        [Test]
        public void BeginWithoutEnd_LosesLeaseToTtl_UsingInjectedClock_NoRealSleep()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromMilliseconds(5));

            var @params = JsonValue.NewObject().SetProperty("ttlSeconds", JsonValue.Float(0.02)); // 20ms
            CommandTable.Dispatch(gate, Request("project.begin_script_editing", @params));
            Assert.IsTrue(gate.IsHeld);

            clock += TimeSpan.FromSeconds(5); // WAY past the 20ms TTL - no agent ever called End

            var releasedInTime = WaitUntilTicked(pump, () => !gate.IsHeld, TimeSpan.FromSeconds(3));

            Assert.IsTrue(releasedInTime,
                "an abandoned BeginScriptEditing session must lose the lease to its TTL - this is the whole point of the gate");
            Assert.IsFalse(gate.IsHeld);
            Assert.AreEqual(1, fake.UnlockCalls);
            Assert.AreEqual(0, fake.Counter);
            // Unity's own reload pipeline resumes automatically once unlocked (a pending reload
            // was only ever DEFERRED, never cancelled) - proving that live requires a real Editor
            // (Task 7's end-to-end pass), not a FakeEditorLockApi. What this test proves is the
            // precondition for it: the TTL watchdog treats a Begin'd lease exactly like any other
            // ReloadGate lease, with no special exemption for class 3.
        }

        // ---------------------------------------------------------------- exception mid-session

        [Test]
        public void ExceptionFromUnrelatedCall_WhileSessionHeld_LeavesLeaseHeld_NoUnlock()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("project.begin_script_editing", JsonValue.NewObject()));
                Assert.IsTrue(gate.IsHeld);

                var badParams = JsonValue.NewObject()
                    .SetProperty("gameObjectPath", JsonValue.String("NoSuchObjectAnywhere"))
                    .SetProperty("componentType", JsonValue.String("BoxCollider"));

                Assert.Throws<ArgumentException>(() => CommandTable.Dispatch(gate, Request("component.add", badParams)));

                // UNLIKE class 2 (LeaseScope.Run's finally releases unconditionally - see
                // PrefabCommandsTests), an exception thrown by something entirely unrelated is NOT
                // evidence this script-editing session finished. Only EndScriptEditing, TTL
                // expiry, disconnect, or boot reconciliation may end it.
                Assert.IsTrue(gate.IsHeld, "an unrelated exception must not release the script-editing lease");
                Assert.AreEqual(ProjectCommands.ScriptEditingLeaseId, gate.CurrentLeaseId);
                Assert.AreEqual(1, fake.LockCalls);
                Assert.AreEqual(0, fake.UnlockCalls);

                CommandTable.Dispatch(gate, Request("project.end_script_editing", JsonValue.NewObject()));
            }
        }

        // ---------------------------------------------------------------- busy: a class-2 call while a session is open

        [Test]
        public void ClassTwoCall_WhileScriptEditingSessionHeld_ThrowsBusyError_SessionUntouched()
        {
            var (gate, fake, pump) = NoopGateParts();
            using (pump) using (gate)
            {
                CommandTable.Dispatch(gate, Request("project.begin_script_editing", JsonValue.NewObject()));

                var ex = Assert.Throws<InvalidOperationException>(() =>
                    CommandTable.Dispatch(gate, Request("project.recompile_scripts", JsonValue.NewObject())));
                StringAssert.Contains(ProjectCommands.ScriptEditingLeaseId, ex.Message);

                Assert.IsTrue(gate.IsHeld);
                Assert.AreEqual(ProjectCommands.ScriptEditingLeaseId, gate.CurrentLeaseId);
                Assert.AreEqual(1, fake.LockCalls);
                Assert.AreEqual(0, fake.UnlockCalls);

                CommandTable.Dispatch(gate, Request("project.end_script_editing", JsonValue.NewObject()));
            }
        }
    }
}
