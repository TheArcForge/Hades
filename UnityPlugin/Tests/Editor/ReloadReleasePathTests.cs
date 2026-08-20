// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Hades.Runtime;
using NUnit.Framework;
using UnityEditor;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// The four paths that can return <see cref="ReloadGate"/> to Released - app release, socket
    /// disconnect, plugin-side TTL, and boot reconciliation - each proven in isolation (the other
    /// three inert in that test), plus the cross-cutting guarantees the release-paths plan
    /// requires of all of them together: an exception mid-operation does not release, the lock
    /// never spans an <c>await</c>, and two paths firing at once still unlock exactly once.
    ///
    /// "In isolation" here means: TTL is parked with an hour-long poll interval/TTL unless it is
    /// specifically the path under test, no test calls a release path it is not proving, and
    /// <see cref="ReloadGate.HeldSessionStateKey"/> is cleared before and after every test (same
    /// reason as <c>ReloadGateTests</c> and <c>ReloadLeaseTests</c>: it is real, process-wide
    /// Unity state that outlives any single test).
    /// </summary>
    [TestFixture]
    public sealed class ReloadReleasePathTests
    {
        [SetUp]
        public void SetUp() => SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

        [TearDown]
        public void TearDown() => SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

        /// <summary>Polls <paramref name="condition"/> (calling <see cref="MainThreadPump.Tick"/>
        /// first on every attempt, so a release enqueued from a background <see cref="Timer"/> -
        /// which cannot be <c>Join()</c>ed the way a <see cref="Thread"/> can - gets applied the
        /// moment it lands, however many attempts that takes) until it is true or
        /// <paramref name="timeout"/> elapses. Same pattern as ReloadLeaseTests.cs's WaitUntil.</summary>
        static bool WaitUntilTicked(MainThreadPump pump, Func<bool> condition, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            do
            {
                pump.Tick();
                if (condition()) return true;
                Thread.Sleep(5);
            } while (stopwatch.Elapsed < timeout);
            pump.Tick();
            return condition();
        }

        // ----- Path 1: the app releases -----

        [Test]
        public void Path1_AppReleases_ExplicitRelease_UnlocksExactlyOnce()
        {
            // Isolation: TTL parked (an hour) so it cannot ALSO be the cause; disconnect and the
            // boot-leak flag are never touched in this test at all.
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            gate.Acquire("agent-1");

            // What a "lease.release" RPC handler calls - synchronously, already on the main
            // thread by construction (see ReloadGate.Release's own doc comment), so no pump
            // involvement is needed or expected here.
            var released = gate.Release("agent-1");

            Assert.IsTrue(released);
            Assert.IsFalse(gate.IsHeld);
            Assert.AreEqual(1, fake.LockCalls);
            Assert.AreEqual(1, fake.UnlockCalls);
            Assert.AreEqual(0, fake.Counter);
            Assert.IsFalse(SessionState.GetBool(ReloadGate.HeldSessionStateKey, false));
        }

        // ----- Path 2: socket disconnect -----

        [Test]
        public void Path2_SocketDisconnect_ReleaseRunsAheadOfAnAlreadyBackedUpQueue()
        {
            // Isolation: TTL parked so it cannot ALSO be the cause; no explicit Release() call;
            // the boot-leak flag is never touched.
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            gate.Acquire("agent-1");

            // Back the pump up with ordinary work BEFORE the disconnect fires - standing in for a
            // main thread with a backlog of queued tool calls at the exact moment the app's
            // socket drops. Each item records what fake.UnlockCalls already was WHEN IT RAN - if
            // the disconnect release truly jumps the queue, every one of these must see 1, never 0.
            var unlockCallsObservedByBacklogItem = new int[5];
            for (var i = 0; i < unlockCallsObservedByBacklogItem.Length; i++)
            {
                var index = i;
                pump.EnqueueAsync(() =>
                {
                    unlockCallsObservedByBacklogItem[index] = fake.UnlockCalls;
                    return 0;
                }, DateTime.UtcNow.AddSeconds(30));
            }

            // The I/O thread's disconnect signal - a real background thread, not called inline.
            var ioThread = new Thread(gate.ReleaseOnDisconnect);
            ioThread.Start();
            Assert.IsTrue(ioThread.Join(TimeSpan.FromSeconds(5)), "the disconnect signal must not block the calling thread");

            pump.Tick();

            Assert.IsFalse(gate.IsHeld);
            Assert.AreEqual(1, fake.UnlockCalls);
            Assert.IsFalse(SessionState.GetBool(ReloadGate.HeldSessionStateKey, false));
            for (var i = 0; i < unlockCallsObservedByBacklogItem.Length; i++)
                Assert.AreEqual(1, unlockCallsObservedByBacklogItem[i],
                    $"backlog item {i} must have observed the release as ALREADY applied - it must never wait behind a queued backlog");
        }

        // ----- Path 3: plugin-side TTL -----

        [Test]
        public void Path3_PluginSideTtl_AppAliveButSilent_ReleaseEnqueuedAndAppliedOnNextTick()
        {
            // Isolation: no explicit Release(), no ReleaseOnDisconnect() call, fresh boot flag.
            // "App alive but silent" is simulated the only way meaningful at this layer: the app
            // simply never calls Acquire/Renew again after the first Acquire - there is no
            // separate "still alive" signal for this test to assert on.
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromMilliseconds(20));
            gate.Acquire("agent-1", TimeSpan.FromMilliseconds(50));

            clock += TimeSpan.FromMilliseconds(200); // past TTL, in FAKE time - no real sleep for this part
            Thread.Sleep(200); // bounded real wait: 10x the 20ms poll interval, for the watchdog to notice

            Assert.IsTrue(gate.IsHeld, "must still be held - a release is only ENQUEUED off-thread, never applied off-thread");
            Assert.AreEqual(0, fake.UnlockCalls);

            pump.Tick();

            Assert.IsFalse(gate.IsHeld);
            Assert.AreEqual(1, fake.UnlockCalls);
            Assert.AreEqual(0, fake.Counter);
            Assert.IsFalse(SessionState.GetBool(ReloadGate.HeldSessionStateKey, false));
        }

        // ----- Path 4: boot reconciliation -----

        [Test]
        public void Path4_BootReconciliation_LeakedFlagWithNoGateInstance_UnlocksExactlyOnceAndClearsFlag()
        {
            // Simulates a leak surviving a domain reload: SessionState says held, but there is no
            // managed ReloadGate instance yet - HadesBoot is about to construct a fresh one, same
            // as it does after every reload. Isolation: nothing here touches TTL, disconnect, or
            // an explicit Release.
            SessionState.SetBool(ReloadGate.HeldSessionStateKey, true);
            var fake = new FakeEditorLockApi();
            fake.Lock(); // the real native counter "survived" at 1 from before the simulated reload
            using var pump = new MainThreadPump();

            using var gate = new ReloadGate(fake, pump);

            Assert.AreEqual(1, fake.UnlockCalls, "boot reconciliation must unlock exactly once");
            Assert.AreEqual(1, fake.LockCalls, "reconciliation itself must never call Lock");
            Assert.AreEqual(0, fake.Counter);
            Assert.IsFalse(gate.IsHeld);
            Assert.IsFalse(SessionState.GetBool(ReloadGate.HeldSessionStateKey, false), "the flag must be cleared after reconciling");
        }

        [Test]
        public void Path4_BootReconciliation_NoFlagSet_CallsUnlockZeroTimes()
        {
            // Simulates a plain Editor restart: SessionState is cleared by Unity itself, and the
            // native counter is genuinely 0. Reconciliation must do NOTHING here.
            SessionState.EraseBool(ReloadGate.HeldSessionStateKey);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();

            using var gate = new ReloadGate(fake, pump);

            Assert.AreEqual(0, fake.UnlockCalls);
            Assert.AreEqual(0, fake.LockCalls);
            Assert.IsFalse(gate.IsHeld);
        }

        // ----- An exception mid-operation is not evidence the operation finished -----

        [Test]
        public void ExceptionInsideLeasedOperation_DoesNotReleaseTheLease()
        {
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            gate.Acquire("op-1");

            // Deliberately no try/finally { gate.Release(...) } around this call - proving there
            // is no ambient magic anywhere that releases on unwind. Only an explicit Release (or
            // one of the other three nets: disconnect, TTL, boot) may end the lease.
            Assert.Throws<InvalidOperationException>(() => throw new InvalidOperationException("simulated tool failure"));

            Assert.IsTrue(gate.IsHeld, "an exception inside the operation must not release the lease");
            Assert.AreEqual("op-1", gate.CurrentLeaseId);
            Assert.AreEqual(0, fake.UnlockCalls);
            Assert.AreEqual(1, fake.Counter);
        }

        // ----- The lock never spans an await -----

        [Test]
        public void AcquireAndRelease_AreSynchronous_AnAsyncSignatureWouldLetTheLockSpanAnAwait()
        {
            // Regression guard at the API-shape level: if Acquire/Release were ever "helpfully"
            // made async (e.g. to do I/O), the lock could then span an await - exactly what the
            // plan forbids. Both must keep returning bool, never Task<bool>.
            var acquireMethod = typeof(ReloadGate).GetMethod(nameof(ReloadGate.Acquire));
            var releaseMethod = typeof(ReloadGate).GetMethod(nameof(ReloadGate.Release));

            Assert.IsNotNull(acquireMethod);
            Assert.IsNotNull(releaseMethod);
            Assert.AreEqual(typeof(bool), acquireMethod.ReturnType,
                "Acquire must return bool, not Task<bool> - an async signature would let the lock span an await");
            Assert.AreEqual(typeof(bool), releaseMethod.ReturnType,
                "Release must return bool, not Task<bool> - an async signature would let the lock span an await");
        }

        [Test]
        public void BoundedOperation_AcquireAndReleaseCompleteWithinOneSynchronousMainThreadExecution()
        {
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            var callingThreadId = Thread.CurrentThread.ManagedThreadId; // stands in for the main thread

            var heldDuringWork = false;
            var threadIdDuringWork = -1;

            // Standing in for how a lease-guarded RPC handler must be structured: acquire, do the
            // (synchronous) work, release - never `async`, so it can never suspend at an `await`
            // while still holding the lease. The try/finally is THIS caller's responsibility to
            // build, not something ReloadGate provides for free - see
            // ExceptionInsideLeasedOperation_DoesNotReleaseTheLease above.
            void RunBoundedOperation(string leaseId)
            {
                gate.Acquire(leaseId);
                try
                {
                    heldDuringWork = gate.IsHeld;
                    threadIdDuringWork = Thread.CurrentThread.ManagedThreadId;
                }
                finally
                {
                    gate.Release(leaseId);
                }
            }

            RunBoundedOperation("op-1");

            Assert.IsTrue(heldDuringWork, "the lease must be held for the duration of the operation");
            Assert.AreEqual(callingThreadId, threadIdDuringWork, "the operation runs on the same synchronous call stack that acquired - nothing here ever yields");
            Assert.IsFalse(gate.IsHeld, "release must already be applied by the time the synchronous call returns");
            Assert.AreEqual(1, fake.LockCalls);
            Assert.AreEqual(1, fake.UnlockCalls);
            Assert.AreEqual(0, fake.Counter);
        }

        // ----- Two paths firing together release exactly once -----

        [Test]
        public void SocketDisconnectAndTtlExpiry_FiringTogether_UnlockExactlyOnce_CounterNeverNegative()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromMilliseconds(20));
            gate.Acquire("agent-1", TimeSpan.FromMilliseconds(50));

            clock += TimeSpan.FromMilliseconds(200); // past TTL, in FAKE time
            Thread.Sleep(200); // bounded real wait - let the watchdog get several chances to enqueue

            // "At the same moment": fire the disconnect path concurrently too, racing whatever
            // the watchdog may have already enqueued.
            var disconnectThread = new Thread(gate.ReleaseOnDisconnect);
            disconnectThread.Start();
            Assert.IsTrue(disconnectThread.Join(TimeSpan.FromSeconds(5)));

            // Joining the thread above proves ITS enqueue (if any) already happened - but the
            // watchdog's competing enqueue runs on a background Timer callback that cannot be
            // Join()ed the same way, so keep ticking (bounded) rather than assume a fixed number
            // of ticks is enough to have caught up with it.
            var releasedInTime = WaitUntilTicked(pump, () => !gate.IsHeld, TimeSpan.FromSeconds(3));
            Assert.IsTrue(releasedInTime);

            Assert.IsFalse(gate.IsHeld);
            Assert.AreEqual(1, fake.UnlockCalls, "two paths firing together must still unlock exactly once");
            Assert.AreEqual(0, fake.Counter);
            Assert.GreaterOrEqual(fake.Counter, 0, "the negative-counter bug: must never go below 0 even under concurrent release paths");
        }

        [Test]
        public void ManyConcurrentDisconnectSignals_UnlockExactlyOnce()
        {
            // Harder than two paths racing once: sixteen threads all calling the disconnect path
            // at once, hardening the de-duplication guard under real contention, not just a
            // two-thread coincidence.
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            gate.Acquire("agent-1");

            var threads = new Thread[16];
            for (var i = 0; i < threads.Length; i++) threads[i] = new Thread(gate.ReleaseOnDisconnect);
            foreach (var t in threads) t.Start();
            foreach (var t in threads) Assert.IsTrue(t.Join(TimeSpan.FromSeconds(5)));

            pump.Tick();

            Assert.IsFalse(gate.IsHeld);
            Assert.AreEqual(1, fake.UnlockCalls);
            Assert.AreEqual(0, fake.Counter);
            Assert.GreaterOrEqual(fake.Counter, 0);
        }
    }
}
