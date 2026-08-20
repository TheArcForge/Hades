// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Hades.Runtime;
using NUnit.Framework;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// <see cref="MainThreadPump"/> in isolation - no sockets, no HadesClient. Every test calls
    /// <see cref="MainThreadPump.Tick"/> directly rather than relying on
    /// <c>EditorApplication.update</c>, which is what makes "which thread ran the work" and "how
    /// many ticks did it take" deterministic instead of dependent on real frame timing.
    /// </summary>
    [TestFixture]
    public sealed class MainThreadPumpTests
    {
        static readonly TimeSpan FarFutureDeadline = TimeSpan.FromSeconds(30);

        [Test]
        public void EnqueueAsync_RunsOnTheThreadThatCallsTick_AndReturnsTheResultToTheCaller()
        {
            using var pump = new MainThreadPump();
            var tickThreadId = Thread.CurrentThread.ManagedThreadId; // this test's thread stands in for "the main thread"
            int? observedThreadId = null;

            Task<int> resultTask = null;
            var producer = Task.Run(() =>
            {
                // Stands in for the I/O thread enqueueing work it cannot do itself.
                resultTask = pump.EnqueueAsync(() =>
                {
                    observedThreadId = Thread.CurrentThread.ManagedThreadId;
                    return 7;
                }, DateTime.UtcNow + FarFutureDeadline);
            });
            Assert.IsTrue(producer.Wait(TimeSpan.FromSeconds(5)));

            pump.Tick();

            Assert.IsTrue(resultTask.Wait(TimeSpan.FromSeconds(5)), "the result task never completed");
            Assert.AreEqual(7, resultTask.Result);
            Assert.AreEqual(tickThreadId, observedThreadId, "queued work must run on the thread that calls Tick()");
        }

        [Test]
        public void WorkPastItsDeadline_IsSkippedNotAppliedLate()
        {
            using var pump = new MainThreadPump();
            var ran = false;

            var task = pump.EnqueueAsync(() =>
            {
                ran = true;
                return 123;
            }, DateTime.UtcNow.AddMilliseconds(-1)); // already expired before Tick() ever runs

            pump.Tick();

            Assert.IsFalse(ran, "expired work must never execute - not run-then-discarded, never run at all");
            Assert.IsTrue(task.IsCanceled, "an expired item's task should be canceled, distinct from faulted or completed");
        }

        [Test]
        public void QueueDrainsUnderAPerTickBudget_SpreadingWorkAcrossMultipleTicks()
        {
            // Each item costs ~15ms; a 20ms budget must not let all 10 drain in a single Tick().
            using var pump = new MainThreadPump(TimeSpan.FromMilliseconds(20));
            const int itemCount = 10;
            var completed = 0;
            var tasks = new List<Task<int>>();

            for (var i = 0; i < itemCount; i++)
            {
                var captured = i;
                tasks.Add(pump.EnqueueAsync(() =>
                {
                    Thread.Sleep(15);
                    Interlocked.Increment(ref completed);
                    return captured;
                }, DateTime.UtcNow + FarFutureDeadline));
            }

            pump.Tick();
            var afterFirstTick = Volatile.Read(ref completed);

            Assert.Greater(afterFirstTick, 0, "the first tick should make some progress");
            Assert.Less(afterFirstTick, itemCount, "a 20ms budget with ~15ms items must not drain all 10 in one tick");

            var ticksTaken = 1;
            while (Volatile.Read(ref completed) < itemCount && ticksTaken < 1000)
            {
                pump.Tick();
                ticksTaken++;
            }

            Assert.AreEqual(itemCount, Volatile.Read(ref completed));
            Assert.Greater(ticksTaken, 1, "work should have been spread across more than one Tick() call");

            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));
        }

        [Test]
        public void Tick_NeverRunsLongerThanASmallMarginOverItsBudget()
        {
            // The budget check happens BETWEEN items, so one item can overshoot it - but Tick()
            // must not let unboundedly many items run past the point the budget was exceeded.
            using var pump = new MainThreadPump(TimeSpan.FromMilliseconds(10));
            for (var i = 0; i < 50; i++)
            {
                pump.EnqueueAsync(() =>
                {
                    Thread.Sleep(2);
                    return 0;
                }, DateTime.UtcNow + FarFutureDeadline);
            }

            var stopwatch = Stopwatch.StartNew();
            pump.Tick();
            stopwatch.Stop();

            Assert.Less(stopwatch.ElapsedMilliseconds, 100,
                "a single Tick() must respect its budget, not drain the whole queue regardless of cost");
        }

        [Test]
        public void ExceptionInQueuedWork_DoesNotKillThePump_TheNextItemStillRuns()
        {
            using var pump = new MainThreadPump();

            var failing = pump.EnqueueAsync<int>(() => throw new InvalidOperationException("boom"),
                DateTime.UtcNow + FarFutureDeadline);
            var succeeding = pump.EnqueueAsync(() => 42, DateTime.UtcNow + FarFutureDeadline);

            pump.Tick();

            // Tick() runs queued work synchronously, so both tasks are already in their final
            // state by the time it returns - deliberately NOT calling Task.Wait()/.Result on the
            // faulted one, since Wait() rethrows the task's exception rather than just reporting
            // IsFaulted, which would make this assertion throw instead of evaluate.
            Assert.IsTrue(failing.IsFaulted, "the failing item's task should be faulted, not canceled or successful");
            Assert.IsInstanceOf<InvalidOperationException>(failing.Exception?.InnerException);

            Assert.AreEqual(TaskStatus.RanToCompletion, succeeding.Status,
                "an exception in one item must not stop the next from running");
            Assert.AreEqual(42, succeeding.Result);
        }

        [Test]
        public void Tick_WithNothingQueued_DoesNotThrow()
        {
            using var pump = new MainThreadPump();

            Assert.DoesNotThrow(() => pump.Tick());
        }

        // ----- EnqueuePriority: the release-paths plan's "jump the queue" primitive. See
        // ReloadGate's TTL watchdog and disconnect handling (Hades.Tests.Editor.ReloadReleasePathTests)
        // for why a release must never wait behind an ordinary backlog. -----

        [Test]
        public void EnqueuePriority_RunsBeforeAlreadyQueuedOrdinaryWork()
        {
            using var pump = new MainThreadPump();
            var order = new List<string>();

            // Ordinary work queued FIRST, priority work queued SECOND - if priority merely meant
            // "also runs this tick" rather than "runs ahead", ordinary would still win the race
            // since it was queued first. The order below only comes out right if Tick() drains
            // the priority queue before it ever looks at the ordinary one.
            pump.EnqueueAsync(() => { order.Add("ordinary"); return 0; }, DateTime.UtcNow + FarFutureDeadline);
            pump.EnqueuePriority(() => order.Add("priority"));

            pump.Tick();

            CollectionAssert.AreEqual(new[] { "priority", "ordinary" }, order);
        }

        [Test]
        public void EnqueuePriority_CallableFromAnyThread_RunsOnTheThreadThatCallsTick()
        {
            using var pump = new MainThreadPump();
            var tickThreadId = Thread.CurrentThread.ManagedThreadId; // stands in for the main thread
            int? observedThreadId = null;

            var producer = Task.Run(() =>
                pump.EnqueuePriority(() => observedThreadId = Thread.CurrentThread.ManagedThreadId));
            Assert.IsTrue(producer.Wait(TimeSpan.FromSeconds(5)));

            pump.Tick();

            Assert.AreEqual(tickThreadId, observedThreadId, "priority work must run on the thread that calls Tick(), never the enqueueing thread");
        }

        [Test]
        public void ExceptionInPriorityWork_DoesNotStopSubsequentPriorityOrOrdinaryWork()
        {
            using var pump = new MainThreadPump();
            var ranSecondPriority = false;
            var ranOrdinary = false;

            pump.EnqueuePriority(() => throw new InvalidOperationException("boom"));
            pump.EnqueuePriority(() => ranSecondPriority = true);
            pump.EnqueueAsync(() => { ranOrdinary = true; return 0; }, DateTime.UtcNow + FarFutureDeadline);

            Assert.DoesNotThrow(() => pump.Tick());

            Assert.IsTrue(ranSecondPriority, "an exception in one priority item must not stop the next priority item");
            Assert.IsTrue(ranOrdinary, "an exception in a priority item must not stop the ordinary queue from draining");
        }

        [Test]
        public void EnqueuePriority_NullWork_Throws()
        {
            using var pump = new MainThreadPump();
            Assert.Throws<ArgumentNullException>(() => pump.EnqueuePriority(null));
        }

        [Test]
        public void Dispose_DiscardsPendingPriorityWork_WithoutThrowing()
        {
            var pump = new MainThreadPump();
            pump.EnqueuePriority(() => { });

            Assert.DoesNotThrow(() => pump.Dispose());
        }
    }
}
