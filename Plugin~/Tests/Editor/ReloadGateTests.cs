// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using Hades.Runtime;
using NUnit.Framework;
using UnityEditor;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// <see cref="ReloadGate"/> in isolation, against a <see cref="FakeEditorLockApi"/> instead of
    /// the real Unity API - see that class's doc comment for why a fake is the only way to observe
    /// what Unity's own (getter-less) native counter would have done.
    ///
    /// Every test clears <see cref="ReloadGate.HeldSessionStateKey"/> before and after running:
    /// <c>SessionState</c> is a real Unity API that persists for the whole batchmode Editor
    /// process, across every test in this suite, so a leftover flag from one test would corrupt
    /// boot-reconciliation assertions in the next.
    /// </summary>
    [TestFixture]
    public sealed class ReloadGateTests
    {
        [SetUp]
        public void SetUp() => SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

        [TearDown]
        public void TearDown() => SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

        /// <summary>Every test needs a pump now that off-thread releases (TTL, disconnect) are
        /// enqueued onto one rather than applied inline - see ReloadGate's class doc comment. None
        /// of the tests in this file tick it: they exercise Acquire/Release/boot reconciliation,
        /// which stay synchronous on the calling thread regardless (see ReleaseGate's
        /// <c>Release</c> doc comment) - off-thread deferral is ReloadLeaseTests.cs's and
        /// ReloadReleasePathTests.cs's job.</summary>
        static ReloadGate NewGate(IEditorLockApi lockApi) => new ReloadGate(lockApi, new MainThreadPump());

        [Test]
        public void Acquire_FromReleased_CallsLockExactlyOnce()
        {
            var fake = new FakeEditorLockApi();
            var gate = NewGate(fake);

            var acquired = gate.Acquire("lease-1");

            Assert.IsTrue(acquired);
            Assert.AreEqual(1, fake.LockCalls);
            Assert.AreEqual(0, fake.UnlockCalls);
            Assert.AreEqual(1, fake.Counter);
            Assert.IsTrue(gate.IsHeld);
            Assert.AreEqual("lease-1", gate.CurrentLeaseId);
        }

        [Test]
        public void Acquire_AgainWhileAlreadyHeldBySameLease_CallsLockZeroFurtherTimes()
        {
            var fake = new FakeEditorLockApi();
            var gate = NewGate(fake);
            gate.Acquire("lease-1");

            var acquiredAgain = gate.Acquire("lease-1");

            Assert.IsTrue(acquiredAgain);
            Assert.AreEqual(1, fake.LockCalls, "re-acquiring the same lease must not call Lock a second time");
            Assert.AreEqual(1, fake.Counter, "the counter must stay at 1, never 2");
        }

        [Test]
        public void Release_FromHeld_CallsUnlockExactlyOnce()
        {
            var fake = new FakeEditorLockApi();
            var gate = NewGate(fake);
            gate.Acquire("lease-1");

            var released = gate.Release("lease-1");

            Assert.IsTrue(released);
            Assert.AreEqual(1, fake.UnlockCalls);
            Assert.AreEqual(0, fake.Counter);
            Assert.IsFalse(gate.IsHeld);
            Assert.IsNull(gate.CurrentLeaseId);
        }

        [Test]
        public void Release_FromReleased_CallsUnlockZeroTimes()
        {
            var fake = new FakeEditorLockApi();
            var gate = NewGate(fake);

            var released = gate.Release("whatever");

            Assert.IsTrue(released, "releasing nothing is a safe no-op, not a failure");
            Assert.AreEqual(0, fake.UnlockCalls, "must never unlock what was never locked");
            Assert.AreEqual(0, fake.Counter);
            Assert.GreaterOrEqual(fake.Counter, 0, "the negative-counter bug: this must never go below 0");
        }

        [Test]
        public void Release_WithWrongLeaseId_IsRejected_GateStaysHeld()
        {
            var fake = new FakeEditorLockApi();
            var gate = NewGate(fake);
            gate.Acquire("owner");

            var released = gate.Release("impostor");

            Assert.IsFalse(released);
            Assert.AreEqual(0, fake.UnlockCalls);
            Assert.AreEqual(1, fake.Counter);
            Assert.IsTrue(gate.IsHeld);
            Assert.AreEqual("owner", gate.CurrentLeaseId);
        }

        [Test]
        public void Boot_WhenSessionStateSaysHeld_UnlocksExactlyOnceAndClearsFlag()
        {
            // Simulates a domain reload: SessionState (survives reload, unlike managed state)
            // still says held, but there is no managed ReloadGate instance yet - HadesBoot is
            // about to construct a fresh one, same as it does after every reload.
            SessionState.SetBool(ReloadGate.HeldSessionStateKey, true);
            var fake = new FakeEditorLockApi();
            fake.Lock(); // the real native counter "survived" at 1 from before the simulated reload

            var gate = NewGate(fake);

            Assert.AreEqual(1, fake.UnlockCalls, "boot reconciliation must unlock exactly once");
            Assert.AreEqual(1, fake.LockCalls, "reconciliation itself must never call Lock");
            Assert.AreEqual(0, fake.Counter);
            Assert.IsFalse(gate.IsHeld);
            Assert.IsFalse(SessionState.GetBool(ReloadGate.HeldSessionStateKey, false), "the flag must be cleared after reconciling");
        }

        [Test]
        public void Boot_WhenSessionStateFlagAbsent_CallsUnlockZeroTimes()
        {
            // Simulates a plain Editor restart: SessionState is cleared by Unity itself, and the
            // native counter is genuinely 0. Reconciliation must do NOTHING here - this is the
            // "conditional on recorded state" requirement, not the old implementation's
            // unconditional force-unlock.
            SessionState.EraseBool(ReloadGate.HeldSessionStateKey);
            var fake = new FakeEditorLockApi();

            var gate = NewGate(fake);

            Assert.AreEqual(0, fake.UnlockCalls);
            Assert.AreEqual(0, fake.LockCalls);
            Assert.IsFalse(gate.IsHeld);
        }

        [Test]
        public void RandomizedAcquireReleaseSequence_CounterNeverLeaves0Or1()
        {
            // Not three hand-picked cases: a seeded random walk over Acquire/Release calls across
            // a small pool of lease ids (so both "same owner" and "wrong owner" paths fire),
            // asserting the invariant after EVERY single call. The seed is reported on failure so
            // any counter-example reproduces exactly.
            for (var trial = 0; trial < 25; trial++)
            {
                var seed = trial * 104729 + 17; // arbitrary distinct seeds, deterministic across runs
                var random = new Random(seed);
                var fake = new FakeEditorLockApi();

                // SessionState is real, process-wide Unity state - without erasing it here, a
                // trial that ends Held would leak its flag into the NEXT trial's fresh gate/fake
                // pair, which reads as "a lock survived a reload" and fires a phantom
                // reconciliation Unlock() against a fake that never locked anything. Each trial is
                // an independent simulated process lifetime, so each gets a clean flag.
                SessionState.EraseBool(ReloadGate.HeldSessionStateKey);
                var gate = NewGate(fake);
                var leaseIds = new[] { "a", "b", "c" };

                for (var step = 0; step < 200; step++)
                {
                    var leaseId = leaseIds[random.Next(leaseIds.Length)];
                    if (random.Next(2) == 0)
                        gate.Acquire(leaseId);
                    else
                        gate.Release(leaseId);

                    var counter = fake.Counter;
                    Assert.GreaterOrEqual(counter, 0, $"counter went negative at trial={trial} seed={seed} step={step}");
                    Assert.LessOrEqual(counter, 1, $"counter exceeded 1 at trial={trial} seed={seed} step={step}");
                    Assert.AreEqual(counter == 1, gate.IsHeld,
                        $"gate.IsHeld disagreed with the fake's counter at trial={trial} seed={seed} step={step}");
                }
            }
        }

        [Test]
        public void Constructor_NullLockApi_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ReloadGate(null, new MainThreadPump()));
        }

        [Test]
        public void Constructor_NullPump_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ReloadGate(new FakeEditorLockApi(), null));
        }
    }
}
