// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEditor;

namespace Hades.Runtime
{
    /// <summary>
    /// The main-thread half of the Editor link: work that needs a Unity API is queued here from
    /// the background I/O thread and actually runs when <see cref="Tick"/> is called - in
    /// production, from <c>EditorApplication.update</c> (wired by <see cref="Start"/>); in tests,
    /// called directly for deterministic control over exactly when and how many times work runs.
    ///
    /// This class is what buys the busy-vs-gone distinction: <c>HadesClient</c> answers
    /// "keepalive" directly on the I/O thread, never through this queue, so a request sitting
    /// here waiting for a slow or absent main thread never delays a keepalive - see
    /// <c>HadesClient</c>'s class doc comment and its keepalive-while-blocked test.
    ///
    /// Three properties carried over from (or fixing) the prior implementation, per the
    /// editor-link plan:
    ///  - Work past its deadline is skipped, never executed - not run-and-then-discarded, which
    ///    is how a timed-out tool could previously apply twice.
    ///  - Draining respects a per-tick time budget, so one slow item cannot freeze the Editor
    ///    frame for an entire queued batch - the budget is wall-clock, not an item count, because
    ///    the failure mode it targets ("one slow tool") is about time, not volume.
    ///  - An exception from one item never stops the next item in the same tick from running.
    /// </summary>
    public sealed class MainThreadPump : IDisposable
    {
        static readonly TimeSpan DefaultPerTickBudget = TimeSpan.FromMilliseconds(8);

        readonly ConcurrentQueue<PumpItem> _queue = new ConcurrentQueue<PumpItem>();
        readonly ConcurrentQueue<Action> _priorityQueue = new ConcurrentQueue<Action>();
        readonly TimeSpan _perTickBudget;
        bool _started;

        public MainThreadPump(TimeSpan? perTickBudget = null)
        {
            _perTickBudget = perTickBudget ?? DefaultPerTickBudget;
        }

        /// <summary>
        /// Queues <paramref name="work"/> to run on whichever thread next calls <see cref="Tick"/>
        /// with budget remaining. The returned task completes with <paramref name="work"/>'s
        /// result, faults with whatever it threw, or - if <paramref name="deadlineUtc"/> passes
        /// before a <see cref="Tick"/> gets to it - is canceled, WITHOUT ever invoking
        /// <paramref name="work"/>. Safe to call from any thread.
        /// </summary>
        public Task<T> EnqueueAsync<T>(Func<T> work, DateTime deadlineUtc)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            // RunContinuationsAsynchronously: a caller's .ContinueWith on this task must never
            // run inline on whatever thread happens to call Tick() (the main thread, in
            // production) - otherwise an arbitrarily slow caller continuation would silently
            // extend Tick()'s own budget accounting, which is supposed to bound only the queued
            // work itself.
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            _queue.Enqueue(new PumpItem(
                deadlineUtc,
                execute: () =>
                {
                    try { tcs.TrySetResult(work()); }
                    catch (Exception e) { tcs.TrySetException(e); }
                },
                skip: () => tcs.TrySetCanceled()));

            return tcs.Task;
        }

        /// <summary>
        /// Queues <paramref name="work"/> to run ahead of everything already queued through
        /// <see cref="EnqueueAsync{T}"/>, the next time <see cref="Tick"/> runs - for off-thread
        /// signals that must never wait behind an ordinary backlog (see <c>ReloadGate</c>'s TTL
        /// watchdog and disconnect-triggered release: a busy main thread must not delay releasing
        /// Unity's reload lock). Unlike <see cref="EnqueueAsync{T}"/> there is no deadline and
        /// nothing to skip - this work always runs on the next <see cref="Tick"/>, because silently
        /// dropping a release is precisely the "hanging lock" failure this exists to prevent. Safe
        /// to call from any thread.
        /// </summary>
        public void EnqueuePriority(Action work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            _priorityQueue.Enqueue(work);
        }

        /// <summary>
        /// Drains the priority queue in full, then drains ordinary queued items in order -
        /// skipping any already past its deadline - until either the ordinary queue is empty or
        /// the per-tick time budget is spent, whichever comes first. The priority queue is
        /// deliberately NOT subject to that budget: it exists specifically so a handful of small,
        /// critical actions (releasing a lock, never more than one or two outstanding in practice)
        /// are never delayed by budget accounting meant to bound an ordinary backlog. Call from
        /// the main thread only (in production, from <c>EditorApplication.update</c>).
        /// </summary>
        public void Tick()
        {
            while (_priorityQueue.TryDequeue(out var work))
            {
                try
                {
                    work();
                }
                catch
                {
                    // Same reasoning as the ordinary queue's guard below - one priority item's
                    // defect must not stop the next priority item, or the ordinary queue after
                    // it, from running this tick.
                }
            }

            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < _perTickBudget && _queue.TryDequeue(out var item))
            {
                if (DateTime.UtcNow > item.DeadlineUtc)
                {
                    item.Skip();
                    continue;
                }

                try
                {
                    item.Execute();
                }
                catch
                {
                    // item.Execute() already funnels the work's own exceptions into its task via
                    // try/catch - this is a last-resort guard so a defect in that plumbing itself
                    // still cannot stop the next queued item from running this tick.
                }
            }
        }

        /// <summary>Subscribes <see cref="Tick"/> to <c>EditorApplication.update</c>. Call once.</summary>
        public void Start()
        {
            if (_started) throw new InvalidOperationException("MainThreadPump is already started.");
            _started = true;
            EditorApplication.update += Tick;
        }

        /// <summary>Unsubscribes from <c>EditorApplication.update</c> and cancels anything still
        /// queued, so no producer is left waiting on a task that will never complete. Safe to call
        /// even if <see cref="Start"/> was never called.</summary>
        public void Dispose()
        {
            if (_started)
            {
                EditorApplication.update -= Tick;
                _started = false;
            }

            while (_queue.TryDequeue(out var item)) item.Skip();
            while (_priorityQueue.TryDequeue(out _)) { } // fire-and-forget - nothing to cancel
        }

        readonly struct PumpItem
        {
            public readonly DateTime DeadlineUtc;
            public readonly Action Execute;
            public readonly Action Skip;

            public PumpItem(DateTime deadlineUtc, Action execute, Action skip)
            {
                DeadlineUtc = deadlineUtc;
                Execute = execute;
                Skip = skip;
            }
        }
    }
}
