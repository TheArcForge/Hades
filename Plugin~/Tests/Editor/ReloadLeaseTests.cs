// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Hades.Runtime;
using NUnit.Framework;
using UnityEditor;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// <see cref="ReloadLease"/> and the TTL half of <see cref="ReloadGate"/>: renewal-by-activity,
    /// expiry-by-silence, and - the whole point of a TTL - that expiry is DETECTED off the main
    /// thread, so noticing it does not depend on the main thread being free to poll for it. The
    /// actual release still only applies on a main-thread <see cref="MainThreadPump.Tick"/> - see
    /// <see cref="ReloadGate"/>'s class doc comment for why, and this file's TTL tests for the
    /// resulting two-phase (detect off-thread, apply on tick) shape. Every gate-level test injects
    /// a fake, mutable clock so no test sleeps for anything resembling a real TTL duration; only
    /// the background watchdog's poll INTERVAL costs a small bounded real-time wait, which is an
    /// implementation detail of how fast the watchdog notices, not the TTL itself.
    /// </summary>
    [TestFixture]
    public sealed class ReloadLeaseTests
    {
        [SetUp]
        public void SetUp() => SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

        [TearDown]
        public void TearDown() => SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

        static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            do
            {
                if (condition()) return true;
                Thread.Sleep(5);
            } while (stopwatch.Elapsed < timeout);
            return condition();
        }

        // ----- ReloadLease itself: pure, synchronous, no gate or timer involved -----

        [Test]
        public void Constructor_RejectsNullOrEmptyId()
        {
            Assert.Throws<ArgumentException>(() => new ReloadLease(null, TimeSpan.FromSeconds(1), DateTime.UtcNow));
            Assert.Throws<ArgumentException>(() => new ReloadLease("", TimeSpan.FromSeconds(1), DateTime.UtcNow));
        }

        [Test]
        public void Constructor_RejectsNonPositiveTtl()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReloadLease("x", TimeSpan.Zero, DateTime.UtcNow));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReloadLease("x", TimeSpan.FromSeconds(-1), DateTime.UtcNow));
        }

        [Test]
        public void IsExpired_TrueOnlyAtOrPastLastActivityPlusTtl()
        {
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var lease = new ReloadLease("x", TimeSpan.FromSeconds(10), t0);

            Assert.IsFalse(lease.IsExpired(t0));
            Assert.IsFalse(lease.IsExpired(t0 + TimeSpan.FromSeconds(9.999)));
            Assert.IsTrue(lease.IsExpired(t0 + TimeSpan.FromSeconds(10)));
        }

        [Test]
        public void Renew_PushesExpiryOutFromTheNewNow_NotTheOriginal()
        {
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var lease = new ReloadLease("x", TimeSpan.FromSeconds(10), t0);

            lease.Renew(t0 + TimeSpan.FromSeconds(5));

            Assert.IsFalse(lease.IsExpired(t0 + TimeSpan.FromSeconds(14.999)), "renewal must extend from the NEW now, not the original");
            Assert.IsTrue(lease.IsExpired(t0 + TimeSpan.FromSeconds(15)));
        }

        // ----- ReloadGate: default TTL and renewal semantics (no watchdog timing involved) -----

        [Test]
        public void DefaultTtl_Is30Seconds()
        {
            Assert.AreEqual(TimeSpan.FromSeconds(30), ReloadGate.DefaultTtl);
        }

        [Test]
        public void Acquire_WithoutExplicitTtl_UsesDefaultTtl()
        {
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1)); // watchdog effectively parked

            gate.Acquire("a");

            Assert.AreEqual(ReloadGate.DefaultTtl, gate.CurrentLease.Ttl);
        }

        [Test]
        public void Acquire_ReacquiringSameLease_RenewsUsingOriginalTtl_IgnoringNewTtlArgument()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => now, TimeSpan.FromHours(1));
            gate.Acquire("a", TimeSpan.FromSeconds(10));

            now += TimeSpan.FromSeconds(5);
            gate.Acquire("a", TimeSpan.FromSeconds(999)); // must be ignored - not a renegotiation

            Assert.AreEqual(TimeSpan.FromSeconds(10), gate.CurrentLease.Ttl, "TTL is fixed at creation, not renegotiated on renewal");
            Assert.AreEqual(now, gate.CurrentLease.LastActivityUtc);
            Assert.AreEqual(1, fake.LockCalls, "still only ever locked once");
        }

        [Test]
        public void Renew_ByOwningLease_UpdatesLastActivity()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => now, TimeSpan.FromHours(1));
            gate.Acquire("owner", TimeSpan.FromSeconds(10));

            now += TimeSpan.FromSeconds(5);
            var renewed = gate.Renew("owner");

            Assert.IsTrue(renewed);
            Assert.AreEqual(now, gate.CurrentLease.LastActivityUtc);
        }

        [Test]
        public void Renew_WithWrongLeaseId_Rejected_DoesNotChangeExpiry()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => now, TimeSpan.FromHours(1));
            gate.Acquire("owner", TimeSpan.FromSeconds(10));
            var originalActivity = gate.CurrentLease.LastActivityUtc;

            now += TimeSpan.FromSeconds(5);
            var renewed = gate.Renew("impostor");

            Assert.IsFalse(renewed);
            Assert.AreEqual(originalActivity, gate.CurrentLease.LastActivityUtc);
            Assert.IsTrue(gate.IsHeld);
        }

        [Test]
        public void Renew_WhenNothingHeld_Rejected()
        {
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Assert.IsFalse(gate.Renew("anything"));
        }

        // ----- TTL expiry: the mechanism that must survive a wedged main thread -----

        [Test]
        public void TtlExpiry_NoActivity_EnqueuesReleaseOffThread_AppliesOnlyOnceTicked()
        {
            // Unity throws UnityException if Lock/UnlockReloadAssemblies is called off the main
            // thread (measured against a real Editor - see the release-paths plan). So the
            // background watchdog (see ReloadGate's class doc comment) must never call Unlock()
            // itself - it may only DETECT expiry and ENQUEUE the release onto the pump, for
            // whichever thread next calls Tick() (the main thread, in production) to actually
            // apply. A test asserting release "without ever ticking the main thread pump" would be
            // testing a fiction - this test asserts the two-phase reality instead.
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromMilliseconds(20));
            var callingThreadId = Thread.CurrentThread.ManagedThreadId; // stands in for the main thread

            gate.Acquire("a", TimeSpan.FromMilliseconds(50));
            Assert.IsTrue(gate.IsHeld);

            clock += TimeSpan.FromMilliseconds(200); // well past the 50ms TTL, in FAKE time - no real sleep for this part

            // Real (but small, bounded) sleep only for the watchdog's own poll interval to get
            // several chances to notice - NOT for the TTL itself, which is entirely fake-clock-
            // driven (see TimeIsInjected_ATtlTestNeverSleepsForTheTtlDuration below).
            Thread.Sleep(200); // 10x the 20ms poll interval

            Assert.IsTrue(gate.IsHeld, "expiry must only be ENQUEUED off-thread, never applied off-thread");
            Assert.AreEqual(0, fake.UnlockCalls, "Unlock must not be called until a Tick() applies the enqueued release");

            pump.Tick();

            Assert.IsFalse(gate.IsHeld, "the enqueued release must apply on this Tick()");
            Assert.AreEqual(1, fake.UnlockCalls);
            Assert.AreEqual(0, fake.Counter);
            Assert.AreEqual(callingThreadId, fake.LastCallerThreadId,
                "Unlock must be called from the thread that calls Tick() (the main thread in production), never the watchdog's own ThreadPool thread");
        }

        [Test]
        public void Renew_BeforeTtl_PreventsAutoRelease_PastTheOriginalDeadline()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromMilliseconds(20));

            gate.Acquire("a", TimeSpan.FromMilliseconds(80));

            clock += TimeSpan.FromMilliseconds(50); // before the original 80ms deadline
            Assert.IsTrue(gate.Renew("a"));          // real activity - pushes the deadline to +130ms from t0

            clock += TimeSpan.FromMilliseconds(50);  // now +100ms: past the ORIGINAL deadline...
            Thread.Sleep(200);                       // ...give the watchdog several chances (10x its 20ms interval) to wrongly fire
            Assert.IsTrue(gate.IsHeld, "a renewal must prevent release at the ORIGINAL deadline");
            Assert.AreEqual(0, fake.UnlockCalls);

            clock += TimeSpan.FromMilliseconds(50);  // now +150ms: past the RENEWED deadline

            // Ticking inside the wait condition itself: the watchdog enqueues asynchronously at
            // some point in this window, and only a Tick() after that point actually applies it -
            // see TtlExpiry_NoActivity_EnqueuesReleaseOffThread_AppliesOnlyOnceTicked above for why
            // there are two phases now instead of one.
            var releasedInTime = WaitUntil(() => { pump.Tick(); return !gate.IsHeld; }, TimeSpan.FromSeconds(3));

            Assert.IsTrue(releasedInTime, "silence past the RENEWED deadline must still release");
            Assert.AreEqual(1, fake.UnlockCalls);
        }

        [Test]
        public void TimeIsInjected_ATtlTestNeverSleepsForTheTtlDuration()
        {
            // Guards against a regression to sleep-based expiry: uses the real 30s DefaultTtl as
            // the lease TTL but, because time is injected, the whole test still completes in well
            // under 30 real seconds.
            var stopwatch = Stopwatch.StartNew();
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromMilliseconds(20));

            gate.Acquire("a"); // default 30s TTL
            clock += ReloadGate.DefaultTtl + TimeSpan.FromSeconds(1);

            var releasedInTime = WaitUntil(() => { pump.Tick(); return !gate.IsHeld; }, TimeSpan.FromSeconds(3));

            Assert.IsTrue(releasedInTime);
            stopwatch.Stop();
            Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(5),
                "a real 30s TTL must not require anything close to 30 real seconds when time is injected");
        }

        // ----- console warning: a held lock must never be silent, but must never spam either -----

        [Test]
        public void HeldWarningThreshold_Is10Seconds()
        {
            Assert.AreEqual(TimeSpan.FromSeconds(10), ReloadGate.HeldWarningThreshold);
        }

        [Test]
        public void HeldPastWarningThreshold_LogsExactlyOneWarning_NamingHowToReleaseIt()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            var warnings = new List<string>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromMilliseconds(20), warnings.Add);

            gate.Acquire("agent-1");
            Assert.AreEqual(0, warnings.Count, "must not warn immediately on acquire");

            clock += ReloadGate.HeldWarningThreshold + TimeSpan.FromSeconds(1); // past threshold, in FAKE time
            Thread.Sleep(200); // bounded real wait: 10x the 20ms poll interval, for the watchdog to notice

            Assert.AreEqual(1, warnings.Count);
            Assert.IsTrue(warnings[0].IndexOf("release", StringComparison.OrdinalIgnoreCase) >= 0,
                "the warning must name how to release it");
        }

        [Test]
        public void HeldContinuouslyWellPastThreshold_StillLogsOnlyOnce()
        {
            // "Once - not every tick": an agent diligently renewing every few seconds keeps the
            // gate held for many multiples of the threshold, yet the user's Editor still is not
            // recompiling the whole time - the warning must not repeat just because the watchdog
            // keeps ticking, but it must also not have been a one-shot fluke that only happened to
            // fire once by coincidence of timing.
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            var warnings = new List<string>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromMilliseconds(20), warnings.Add);
            gate.Acquire("agent-1");

            clock += ReloadGate.HeldWarningThreshold + TimeSpan.FromSeconds(1);
            Thread.Sleep(200);
            Assert.AreEqual(1, warnings.Count);

            for (var i = 0; i < 3; i++)
            {
                Assert.IsTrue(gate.Renew("agent-1"));
                clock += ReloadGate.HeldWarningThreshold;
                Thread.Sleep(200);
            }

            Assert.AreEqual(1, warnings.Count, "a long, renewed hold must warn exactly once, not on every subsequent tick");
        }

        [Test]
        public void ReleasedBeforeTheThreshold_NeverWarns()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            var warnings = new List<string>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromMilliseconds(20), warnings.Add);

            gate.Acquire("agent-1");
            clock += TimeSpan.FromSeconds(2); // well under the 10s threshold
            Thread.Sleep(100);
            gate.Release("agent-1");

            clock += ReloadGate.HeldWarningThreshold + TimeSpan.FromSeconds(1); // "past threshold" but nothing is held anymore
            Thread.Sleep(200);

            Assert.AreEqual(0, warnings.Count);
        }

        [Test]
        public void ReacquiringAfterRelease_WarnsAgainForTheNewIndependentHold()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            var warnings = new List<string>();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromMilliseconds(20), warnings.Add);

            gate.Acquire("agent-1");
            clock += ReloadGate.HeldWarningThreshold + TimeSpan.FromSeconds(1);
            Thread.Sleep(200);
            Assert.AreEqual(1, warnings.Count);

            gate.Release("agent-1");
            gate.Acquire("agent-2"); // a genuinely new, later hold

            clock += ReloadGate.HeldWarningThreshold + TimeSpan.FromSeconds(1);
            Thread.Sleep(200);

            Assert.AreEqual(2, warnings.Count, "a later, independent hold must warn again - the guard resets per-hold, not permanently");
        }

        [Test]
        public void DefaultLogWarning_IsNotRequired_ConstructorAcceptsOmittingIt()
        {
            // Regression guard at the API-shape level: every existing call site across this
            // plugin's other test files constructs ReloadGate without a 5th argument. Confirms
            // that keeps compiling and working (falls back to UnityEngine.Debug.LogWarning,
            // exercised for real by the boot-time singleton in HadesBoot, not asserted here).
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));

            Assert.DoesNotThrow(() => gate.Acquire("a"));
        }
    }
}
