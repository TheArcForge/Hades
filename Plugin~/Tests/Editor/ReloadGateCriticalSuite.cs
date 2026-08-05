// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Hades.Runtime;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// Task 6 of the ReloadGate plan - "the critical suite". Tasks 1-5 each proved their own piece
    /// in isolation (the gate/lease core, the four release paths, the lease.* commands and
    /// visibility); this file attacks the WHOLE thing rather than any one piece, because the gate
    /// stops Unity recompiling - if it is ever wrong, a user's Editor silently stops compiling
    /// their code with no explanation, and none of the per-piece suites can catch a defect that
    /// only shows up when pieces interact.
    ///
    /// Four requirements, each its own region below:
    ///  1. The counter invariant under a randomised sequence mixing all five things that can move
    ///     it - acquire, release, disconnect, TTL expiry, boot reconciliation - thousands of
    ///     events, seeded, printing the seed on failure.
    ///  2. Every pair of the four release paths, firing concurrently, releases exactly once - not
    ///     just the one pair ReloadReleasePathTests already covers.
    ///  3. Leak-then-recover: a leaked flag with no live gate recovers exactly once, and STAYS
    ///     recovered - a second boot must not unlock again.
    ///  4. A source scan enforcing Rule 1 ("a single ReloadGate type; every other call site is a
    ///     compile error") since C# itself cannot express that as a language guarantee.
    ///
    /// Same SessionState hygiene as every sibling file in this folder: it is real, process-wide
    /// Unity state that outlives any single test, so it is erased before and after each one.
    /// </summary>
    [TestFixture]
    public sealed class ReloadGateCriticalSuite
    {
        [SetUp]
        public void SetUp() => SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

        [TearDown]
        public void TearDown() => SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

        /// <summary>Same polling pattern as ReloadReleasePathTests.WaitUntilTicked: calls
        /// <see cref="MainThreadPump.Tick"/> on every attempt (an off-thread release enqueued from
        /// a background <see cref="Timer"/> cannot be <c>Join()</c>ed the way a <see cref="Thread"/>
        /// can, so the only way to catch it the moment it lands is to keep ticking) until
        /// <paramref name="condition"/> is true or <paramref name="timeout"/> elapses.</summary>
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

        // =================================================================================
        // 1. THE COUNTER INVARIANT, under a randomised sequence mixing every event kind
        // =================================================================================

        /// <summary>
        /// The one property every other test in this plan assumes: the fake's signed
        /// <see cref="FakeEditorLockApi.Counter"/> - standing in for Unity's own native
        /// reload-lock counter, which Unity exposes no getter for - never leaves {0, 1}, under an
        /// arbitrarily long, arbitrarily-ordered MIX of every event that can move it: explicit
        /// Acquire/Release, socket disconnect, TTL expiry, and boot reconciliation (a simulated
        /// domain reload). Not just the two-event-kind walk
        /// ReloadGateTests.RandomizedAcquireReleaseSequence_CounterNeverLeaves0Or1 already covers -
        /// this is that same idea, extended to the full event set the plan actually has to survive.
        ///
        /// 25 trials x 200 steps = 5,000 events, each one asserted individually. Seeds are
        /// deterministic (same "trial * a large prime + a constant" shape as the existing
        /// randomized test) and printed in every failure message, so any counter-example
        /// reproduces exactly by re-running with the same trial number.
        ///
        /// Boot reconciliation is modelled faithfully, not by resetting anything: the FAKE
        /// persists across a simulated reload - a real domain reload wipes managed state but never
        /// Unity's native lock counter, see ReloadGate's own class doc comment - while the gate and
        /// pump are disposed and freshly reconstructed, exactly as HadesBoot's
        /// Shutdown()-then-static-constructor-rerun does. SessionState is erased only ONCE per
        /// trial, before that trial's first gate, never between a trial's own simulated reloads -
        /// a real domain reload does not clear it either (only an Editor restart does). A previous
        /// agent hit cross-trial contamination from skipping exactly this reset; see
        /// ReloadGateTests' own randomized test for the same note.
        ///
        /// TTL expiry uses the REAL background watchdog (a short poll interval against a
        /// controllable fake clock), never a hand-rolled substitute for it - so this suite's TTL
        /// events exercise the actual detection path (Timer -> CheckTtl -> IsExpired), not merely
        /// the release mechanism it happens to share with disconnect. A short, bounded settle
        /// follows each such event to give that real, genuinely asynchronous detection a fair
        /// chance to land within the SAME step - but the invariant asserted below holds regardless
        /// of whether it lands early, late, or not yet within this step: ReloadGate keeps
        /// CurrentLease/IsHeld and the fake's Counter mutually consistent, atomically, at every
        /// instant - there is no window where one has moved and the other has not. A stray release
        /// that lands late and hits a DIFFERENT, since-acquired lease instead of the one that
        /// actually expired is a known, accepted tradeoff of the design (see
        /// RequestOffThreadRelease's own doc comment: "that is accepted, because the alternative
        /// failure direction is a hanging lock") - not a violation of anything asserted here.
        /// </summary>
        [Test]
        public void RandomizedFullEventMix_CounterNeverLeaves0Or1_AndAgreesWithIsHeld()
        {
            const int Trials = 25;
            const int StepsPerTrial = 200; // 5,000 events total
            var leaseIds = new[] { "a", "b", "c", "d" };
            var pollInterval = TimeSpan.FromMilliseconds(5);

            for (var trial = 0; trial < Trials; trial++)
            {
                var seed = trial * 104729 + 17; // same arbitrary-distinct-seed convention as ReloadGateTests
                var random = new System.Random(seed); // fully qualified: UnityEngine.Random is also in scope here

                // Each trial is an independent simulated PROCESS lifetime (possibly spanning
                // several simulated domain reloads within the loop below). Reset only here, never
                // between a trial's own reloads - see this test's own doc comment.
                SessionState.EraseBool(ReloadGate.HeldSessionStateKey);

                var fake = new FakeEditorLockApi();
                var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var pump = new MainThreadPump();
                var gate = new ReloadGate(fake, pump, () => clock, pollInterval);

                try
                {
                    for (var step = 0; step < StepsPerTrial; step++)
                    {
                        var leaseId = leaseIds[random.Next(leaseIds.Length)];
                        var eventKind = random.Next(10); // weighted: see cases below

                        switch (eventKind)
                        {
                            case 0: case 1: case 2: // Acquire - 30%
                                gate.Acquire(leaseId, TimeSpan.FromMilliseconds(20));
                                break;

                            case 3: case 4: case 5: // Release - 30%
                                gate.Release(leaseId);
                                break;

                            case 6: // Socket disconnect - 10%
                                gate.ReleaseOnDisconnect();
                                break;

                            case 7: case 8: // TTL expiry - 20%
                                clock += TimeSpan.FromMilliseconds(200); // past any TTL used above
                                for (var settle = 0; settle < 5; settle++)
                                {
                                    Thread.Sleep(3);
                                    pump.Tick();
                                }
                                break;

                            default: // Boot reconciliation (simulated domain reload) - 10%
                                gate.Dispose();
                                pump.Dispose();
                                pump = new MainThreadPump();
                                gate = new ReloadGate(fake, pump, () => clock, pollInterval);
                                break;
                        }

                        // Production ticks continuously via EditorApplication.update regardless of
                        // what triggered this particular step - so does this, unconditionally.
                        pump.Tick();

                        var counter = fake.Counter;
                        Assert.GreaterOrEqual(counter, 0,
                            $"counter went NEGATIVE at trial={trial} seed={seed} step={step} event={eventKind} leaseId={leaseId}");
                        Assert.LessOrEqual(counter, 1,
                            $"counter EXCEEDED 1 at trial={trial} seed={seed} step={step} event={eventKind} leaseId={leaseId}");
                        Assert.AreEqual(counter == 1, gate.IsHeld,
                            $"gate.IsHeld disagreed with the fake's counter at trial={trial} seed={seed} step={step} event={eventKind} leaseId={leaseId}");
                    }
                }
                finally
                {
                    gate.Dispose();
                    pump.Dispose();
                    SessionState.EraseBool(ReloadGate.HeldSessionStateKey);
                }
            }
        }

        // =================================================================================
        // 2. EVERY PAIR OF RELEASE PATHS, FIRING CONCURRENTLY, RELEASES EXACTLY ONCE
        // =================================================================================
        //
        // The four release paths (see ReloadGate's own class doc comment): (1) an explicit app
        // Release, (2) socket disconnect, (3) plugin-side TTL, (4) boot reconciliation.
        // C(4,2) = six pairs:
        //   (1,2) Release x Disconnect            - Pair_ReleaseAndDisconnect... below
        //   (1,3) Release x TTL                   - Pair_ReleaseAndTtlExpiry... below
        //   (1,4) Release x BootReconciliation     - Pair_ReleaseAndBootReconciliation... below
        //   (2,3) Disconnect x TTL                 - ALREADY COVERED: ReloadReleasePathTests.
        //         SocketDisconnectAndTtlExpiry_FiringTogether_UnlockExactlyOnce_CounterNeverNegative
        //   (2,4) Disconnect x BootReconciliation  - Pair_DisconnectAndBootReconciliation... below
        //   (3,4) TTL x BootReconciliation          - Pair_TtlWatchdogAndBootReconciliation... below

        /// <summary>
        /// DISCOVERY while writing this suite: <see cref="ReloadGate.Release"/> (and
        /// <see cref="ReloadGate.Acquire"/>) call <c>SessionState.SetBool</c>/<c>EraseBool</c>
        /// directly, and a real Unity Editor throws <c>UnityException</c> ("...can only be called
        /// from the main thread") if that runs off-thread - confirmed empirically: an earlier draft
        /// of this test called <c>gate.Release(...)</c> from a spawned <see cref="Thread"/> and hit
        /// exactly that exception. Unlike <see cref="IEditorLockApi"/>, <c>SessionState</c> access
        /// is NOT routed through a fake/seam in <see cref="ReloadGate"/>, so this constraint is
        /// real even under test, not merely a production convention. It is not a bug - every real
        /// <c>lease.release</c>/<c>lease.acquire</c> already reaches <see cref="ReloadGate"/> via
        /// <c>HadesBoot.HandleRequest</c>, which <c>HadesClient</c> always dispatches through
        /// <see cref="MainThreadPump"/> first (see that class's own doc comment) - so
        /// Acquire/Release never run off-thread in practice. It IS, however, an implicit
        /// constraint <see cref="ReloadGate"/>'s own otherwise-thorough class doc comment does not
        /// spell out anywhere. Both tests below race the genuinely-off-thread signal (the
        /// disconnect notification; the TTL watchdog's own peek) against an explicit Release called
        /// correctly on the calling ("main") thread, rather than wrapping Release itself in a
        /// Thread.
        /// </summary>
        [Test]
        public void Pair_ReleaseAndDisconnect_FiringConcurrently_UnlocksExactlyOnce()
        {
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            gate.Acquire("agent-1");

            // The disconnect SIGNAL genuinely is off-thread in production (the I/O thread notices
            // the socket drop) - only its actual apply is deferred to the main thread via the pump.
            // Deliberately not joined before Release below: both genuinely race for ReloadGate's
            // own _sync lock.
            var disconnectThread = new Thread(gate.ReleaseOnDisconnect);
            disconnectThread.Start();
            gate.Release("agent-1"); // main-thread, synchronous - see this test's own doc comment
            Assert.IsTrue(disconnectThread.Join(TimeSpan.FromSeconds(5)));

            pump.Tick(); // applies the disconnect path's enqueued release, if it is still the one pending

            Assert.IsFalse(gate.IsHeld);
            Assert.AreEqual(1, fake.UnlockCalls, "Release and Disconnect firing together must unlock exactly once");
            Assert.AreEqual(0, fake.Counter);
            Assert.GreaterOrEqual(fake.Counter, 0);
        }

        [Test]
        public void Pair_ReleaseAndTtlExpiry_FiringConcurrently_UnlocksExactlyOnce()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            using var pump = new MainThreadPump();
            using var gate = new ReloadGate(fake, pump, () => clock, TimeSpan.FromMilliseconds(10));
            gate.Acquire("agent-1", TimeSpan.FromMilliseconds(20));

            clock += TimeSpan.FromMilliseconds(200); // past TTL - the live watchdog is now racing in the background, off-thread

            // Release runs right here on this (main) thread, immediately - no wait, so it
            // genuinely races whatever the live background watchdog Timer is doing concurrently on
            // its own thread right now. See this test class's doc comment above for why Release
            // itself must never be the one wrapped in a spawned Thread.
            gate.Release("agent-1");

            var releasedInTime = WaitUntilTicked(pump, () => !gate.IsHeld, TimeSpan.FromSeconds(3));
            Assert.IsTrue(releasedInTime);

            Assert.IsFalse(gate.IsHeld);
            Assert.AreEqual(1, fake.UnlockCalls, "Release and TTL expiry firing together must unlock exactly once");
            Assert.AreEqual(0, fake.Counter);
            Assert.GreaterOrEqual(fake.Counter, 0);
        }

        [Test]
        public void Pair_ReleaseAndBootReconciliation_StaleReleaseAfterReconciliationAlreadyRan_IsASafeNoOp()
        {
            var fake = new FakeEditorLockApi();
            var pump1 = new MainThreadPump();
            var gate1 = new ReloadGate(fake, pump1);
            gate1.Acquire("agent-1"); // SessionState flag now set

            // Simulate: a domain reload happens BEFORE the in-flight "lease.release" for
            // "agent-1" is processed - old instance torn down, fresh one constructed;
            // reconciliation sees the leaked flag and unlocks once, exactly as the isolated boot
            // reconciliation tests already prove.
            gate1.Dispose();
            pump1.Dispose();
            using var pump2 = new MainThreadPump();
            using var gate2 = new ReloadGate(fake, pump2);

            Assert.AreEqual(1, fake.UnlockCalls, "reconciliation on the fresh instance must have unlocked once");

            // The late-arriving release for the OLD (now-stale) lease id reaches the NEW instance.
            var released = gate2.Release("agent-1");

            Assert.IsTrue(released, "releasing an id the fresh instance never heard of is a safe no-op");
            Assert.AreEqual(1, fake.UnlockCalls, "the stale release must call Unlock zero further times");
            Assert.AreEqual(0, fake.Counter);
            Assert.IsFalse(gate2.IsHeld);
        }

        [Test]
        public void Pair_DisconnectAndBootReconciliation_EnqueuedButNotYetApplied_ReconciliationStillUnlocksExactlyOnce()
        {
            var fake = new FakeEditorLockApi();
            var pump1 = new MainThreadPump();
            var gate1 = new ReloadGate(fake, pump1, () => DateTime.UtcNow, TimeSpan.FromHours(1));
            gate1.Acquire("agent-1");

            // The disconnect signal fires and DEFINITELY enqueues (Join proves it ran) - but
            // nobody ticks pump1 before the reload tears everything down, so the enqueued release
            // is discarded, never applied. Mirrors HadesBoot.Shutdown()'s own ordering: Gate then
            // Pump disposed, both before a fresh instance is constructed.
            var disconnectThread = new Thread(gate1.ReleaseOnDisconnect);
            disconnectThread.Start();
            Assert.IsTrue(disconnectThread.Join(TimeSpan.FromSeconds(5)));

            gate1.Dispose();
            pump1.Dispose(); // discards the still-pending disconnect release without ever invoking it

            using var pump2 = new MainThreadPump();
            using var gate2 = new ReloadGate(fake, pump2); // flag still set - reconciliation unlocks once

            Assert.AreEqual(1, fake.UnlockCalls,
                "reconciliation must independently unlock exactly once, even though the disconnect path's own release was discarded");
            Assert.AreEqual(0, fake.Counter);
            Assert.IsFalse(gate2.IsHeld);
        }

        [Test]
        public void Pair_TtlWatchdogAndBootReconciliation_LiveWatchdogRacingTeardown_ReconciliationStillUnlocksExactlyOnce()
        {
            var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fake = new FakeEditorLockApi();
            var pump1 = new MainThreadPump();
            var gate1 = new ReloadGate(fake, pump1, () => clock, TimeSpan.FromMilliseconds(5));
            gate1.Acquire("agent-1", TimeSpan.FromMilliseconds(10));

            clock += TimeSpan.FromMilliseconds(100); // past TTL - the watchdog is now live, racing in the background

            // No wait, no tick: tear down IMMEDIATELY, genuinely racing whatever gate1's watchdog
            // Timer callback may be doing concurrently on its own ThreadPool thread right now.
            // Whatever that callback manages to do, it can only ever ENQUEUE (never call Unlock
            // directly - see ReloadGate's own doc comment), and either the _disposed guard or
            // pump1.Dispose()'s discard neutralises it - see this test class's doc comment on the
            // (3,4) pair for the full case analysis.
            gate1.Dispose();
            pump1.Dispose();

            using var pump2 = new MainThreadPump();
            using var gate2 = new ReloadGate(fake, pump2, () => clock, TimeSpan.FromMilliseconds(5));

            Assert.AreEqual(1, fake.UnlockCalls,
                "reconciliation must unlock exactly once regardless of how the live watchdog interleaved with teardown");
            Assert.AreEqual(0, fake.Counter);
            Assert.IsFalse(gate2.IsHeld);

            // Give any straggling watchdog callback from the disposed gate1/pump1 time to finish -
            // Timer.Dispose() does not block for an in-flight callback - so it cannot fire later
            // and race the NEXT test, and to prove no further unlock arrives from it.
            Thread.Sleep(50);
            Assert.AreEqual(1, fake.UnlockCalls, "no further unlock may arrive later from the torn-down instance");
        }

        // =================================================================================
        // 3. LEAK, THEN RECOVER, THEN STAY RECOVERED
        // =================================================================================

        [Test]
        public void LeakThenRecover_FirstBootUnlocksOnceAndClearsFlag_SecondBootUnlocksZeroTimes()
        {
            SessionState.SetBool(ReloadGate.HeldSessionStateKey, true); // leak: flag set, no live gate anywhere
            var fake = new FakeEditorLockApi();
            fake.Lock(); // the real native counter "survived" at 1 from before the simulated crash/reload

            using (var pump1 = new MainThreadPump())
            using (var gate1 = new ReloadGate(fake, pump1))
            {
                Assert.AreEqual(1, fake.UnlockCalls, "the first boot must unlock exactly once");
                Assert.AreEqual(1, fake.LockCalls, "reconciliation itself must never call Lock");
                Assert.AreEqual(0, fake.Counter);
                Assert.IsFalse(gate1.IsHeld);
                Assert.IsFalse(SessionState.GetBool(ReloadGate.HeldSessionStateKey, false), "the flag must be cleared after reconciling");
            }

            // Boot again - same fake (the native counter persists across the simulated reload),
            // flag now absent. Must do NOTHING: the leak was already fixed once, and reconciliation
            // must not repeat it against an instance that never leaked anything itself.
            using var pump2 = new MainThreadPump();
            using var gate2 = new ReloadGate(fake, pump2);

            Assert.AreEqual(1, fake.UnlockCalls, "the second boot must not unlock again - the leak was already fixed");
            Assert.AreEqual(1, fake.LockCalls, "neither boot may ever call Lock");
            Assert.AreEqual(0, fake.Counter);
            Assert.IsFalse(gate2.IsHeld);
        }

        // =================================================================================
        // 4. SOURCE SCAN: NO NEW Lock/UnlockReloadAssemblies CALL SITES
        // =================================================================================

        /// <summary>
        /// Rule 1 of the design: "a single ReloadGate type; every other call site is a compile
        /// error." C# cannot express that as a language-level guarantee, so this asserts it
        /// instead: no file under Assets/Hades/ (this plugin's shipped runtime, resolved from the
        /// test's own perspective via <see cref="Application.dataPath"/> - the scratch/consuming
        /// project it is actually installed into, never a hardcoded dev-machine path) may contain
        /// REAL call syntax for EditorApplication's LockReloadAssemblies/UnlockReloadAssemblies,
        /// except <c>IEditorLockApi.cs</c> - the one file permitted to call them (see that file's
        /// own class doc comment).
        ///
        /// "Real call syntax" - not merely the identifier's name - is the load-bearing distinction.
        /// Both names appear all over this plugin's PROSE: ReloadGate.cs's class doc comment names
        /// them explicitly three times, IEditorLockApi.cs's own summary spells out what it is a
        /// seam over, HadesBoot.cs explains what the real Gate is built on - precisely because this
        /// is the mechanism the whole plan revolves around. A scan that flagged every mention would
        /// fail permanently against the very documentation that explains the rule. The
        /// distinguishing feature of an actual invocation is the open parenthesis that immediately
        /// follows the method name (at most whitespace in between) - <c>LockReloadAssemblies()</c> -
        /// which no prose mention in this codebase happens to be followed by (verified: every
        /// existing mention is followed by a period, "off", "-", or a closing doc-comment tag,
        /// never "("). Same spirit as PluginInstallerTests' Install_ZeroDependencyGuard test from
        /// the transport plan: a pragmatic scan, not a C# parser, deliberately calibrated against
        /// what this codebase's prose actually looks like rather than an airtight grammar.
        ///
        /// Also asserts the OTHER half of the same guarantee: the real mechanism must still exist,
        /// exactly twice (one Lock, one Unlock) inside the one file allowed to have it - not zero
        /// (silently deleted) and not duplicated - so this cannot be satisfied by simply deleting
        /// the lock mechanism.
        /// </summary>
        [Test]
        public void SourceScan_NoCallSitesForTheNativeLockApi_OutsideIEditorLockApi()
        {
            var pluginRoot = Path.Combine(Application.dataPath, "Hades");
            Assert.IsTrue(Directory.Exists(pluginRoot),
                $"expected the plugin to be installed at '{pluginRoot}' - copy Plugin~/Assets/Hades there before running this suite");

            var sourceFiles = Directory.GetFiles(pluginRoot, "*.cs", SearchOption.AllDirectories);
            Assert.GreaterOrEqual(sourceFiles.Length, 5,
                "suspiciously few .cs files under Assets/Hades/ - is the plugin actually installed there?");

            // Real invocation syntax only: the method name immediately followed by an open paren,
            // so a doc-comment mention (never followed by "(" anywhere in this codebase) can never
            // trip this - see this test's own doc comment.
            var realCallSyntax = new Regex(@"\b(Lock|Unlock)ReloadAssemblies\s*\(");

            var violationsOutsideAllowedFile = new List<string>();
            var callSitesInsideAllowedFile = 0;

            foreach (var file in sourceFiles)
            {
                var text = File.ReadAllText(file);
                var matches = realCallSyntax.Matches(text);
                if (matches.Count == 0) continue;

                if (string.Equals(Path.GetFileName(file), "IEditorLockApi.cs", StringComparison.Ordinal))
                {
                    callSitesInsideAllowedFile += matches.Count;
                    continue;
                }

                var relativePath = file.Substring(pluginRoot.Length).TrimStart('/', Path.DirectorySeparatorChar);
                foreach (Match match in matches)
                {
                    var line = text.Substring(0, match.Index).Count(c => c == '\n') + 1;
                    violationsOutsideAllowedFile.Add($"{relativePath}:{line}: '{match.Value.TrimEnd()}'");
                }
            }

            Assert.IsEmpty(violationsOutsideAllowedFile,
                "found a Lock/UnlockReloadAssemblies call site OUTSIDE IEditorLockApi.cs - ReloadGate's whole design "
                + "rests on being the only path to Unity's native lock; violations:\n" + string.Join("\n", violationsOutsideAllowedFile));

            Assert.AreEqual(2, callSitesInsideAllowedFile,
                "expected exactly two real call sites inside IEditorLockApi.cs (one Lock, one Unlock) - found "
                + callSitesInsideAllowedFile + " - the lock mechanism itself may have been deleted or duplicated");
        }
    }
}
