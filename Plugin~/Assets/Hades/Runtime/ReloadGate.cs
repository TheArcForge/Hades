// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using System;
using System.Threading;
using UnityEditor;

namespace Hades.Runtime
{
    /// <summary>The ONLY type permitted to call EditorApplication.Lock/UnlockReloadAssemblies.
    /// Every other call site in this plugin is a bug - the previous implementation had ten of them
    /// across three files, 2 locks against 8 unlocks, and could drive Unity's native counter negative.
    /// A negative counter is silent: the next real Lock only returns it to 0 and does not lock.
    ///
    /// The held/released state is a single nullable <see cref="ReloadLease"/>, not a counter -
    /// "never nest" is a rule, and this makes nesting unrepresentable rather than merely
    /// discouraged. Exactly one lease may be held at a time; <see cref="Acquire"/> for a different
    /// id while one is already held is rejected, not queued or stacked. There is no parameterless
    /// acquire - see <see cref="ReloadLease"/>'s doc comment.
    ///
    /// Boot reconciliation is conditional on <see cref="SessionState"/>, not unconditional. A
    /// domain reload wipes this class's own fields but NOT Unity's native lock counter, so a plain
    /// field cannot tell a freshly-constructed instance whether a lock is outstanding - which is
    /// exactly why the old implementation's unconditional force-unlock could not recover safely
    /// (it either dropped a real lock to 0 while some other part of the system still believed it
    /// was held, or unlocked nothing and drove the counter to -1). SessionState persists across a
    /// domain reload and is cleared on Editor restart, so it maps precisely onto the two real
    /// cases: post-reload boot (flag says held -> the native counter really is 1 -> unlock once)
    /// versus a plain Editor restart (flag absent, native counter genuinely 0 -> do nothing).
    /// Deliberately NOT "force-release unconditionally" - that unconditional unlock is the bug
    /// this class exists to make impossible, so it cannot also be the recovery mechanism.
    ///
    /// TTL expiry is DETECTED by a background <see cref="Timer"/> running on a ThreadPool thread,
    /// never the main thread - but detection is all that happens there. Measured against a real
    /// Unity Editor: calling EditorApplication.Lock/UnlockReloadAssemblies off the main thread
    /// throws UnityException ("...can only be called from the main thread"). So the watchdog may
    /// only PEEK at expiry off-thread and must defer the actual release to
    /// <see cref="MainThreadPump"/> - see <see cref="RequestOffThreadRelease"/> - for whichever
    /// thread next calls <see cref="MainThreadPump.Tick"/> (the main thread, in production) to
    /// apply. <see cref="ReleaseOnDisconnect"/> (the socket-drop path) is deferred the same way,
    /// for the same reason - it too can run on a thread that is not the main thread.
    ///
    /// The SAME constraint applies to SessionState, which is easy to miss because it is not
    /// routed through a seam the way <see cref="IEditorLockApi"/> is. Measured: SessionState's
    /// setters throw UnityException ("EraseBool can only be called from the main thread"). Since
    /// <see cref="Acquire"/> and <see cref="Release"/> both touch SessionState, BOTH ARE
    /// MAIN-THREAD-ONLY, and nothing here defers on their behalf. That holds today because every
    /// caller reaches them through <see cref="MainThreadPump"/> - lease.* commands are dispatched
    /// through the pump, and the off-thread paths only ever enqueue. A future caller invoking
    /// Acquire or Release directly from a background thread would throw, and no test using a fake
    /// lock API would catch it, because the fake has no thread affinity and SessionState is real.
    ///
    /// This still covers the case a TTL exists for: the agent dies mid-session while the main
    /// thread keeps ticking normally - holding the reload lock only defers recompilation, it does
    /// not stop <c>EditorApplication.update</c> from firing - so detect-off-thread-then-apply-on-
    /// next-tick drains exactly as if nothing were deferred. What this can NOT do is rescue a
    /// genuinely wedged main thread: if <c>EditorApplication.update</c> itself never runs again,
    /// nothing queued on <see cref="MainThreadPump"/> ever applies, TTL included. That case has no
    /// in-process fix - it is covered by boot reconciliation after the user kills the hung Editor,
    /// not by anything running inside it.
    /// </summary>
    public sealed class ReloadGate : IDisposable
    {
        /// <summary>Exposed so tests can simulate a leaked lock surviving a domain reload by
        /// setting this flag directly, without needing a live <see cref="ReloadGate"/> instance to
        /// have set it first.</summary>
        public const string HeldSessionStateKey = "Hades.ReloadGate.Held";

        /// <summary>Long enough for a real script-editing round trip; short enough that a dead
        /// agent costs half a minute of blocked recompilation rather than a day.</summary>
        public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

        /// <summary>How long a lease may be held, continuously, before <see cref="CheckHeldWarning"/>
        /// warns in the Unity console that reload is blocked. Measured from ORIGINAL acquisition
        /// (<see cref="_heldSinceUtc"/>), not from last activity - long enough that an ordinary,
        /// brief script-editing round trip never warns; short enough that a user staring at a
        /// non-recompiling Editor gets an explanation quickly. See the release-paths/visibility
        /// plan: this is the one place this plugin is deliberately loud.</summary>
        public static readonly TimeSpan HeldWarningThreshold = TimeSpan.FromSeconds(10);

        static readonly TimeSpan DefaultTtlPollInterval = TimeSpan.FromMilliseconds(200);

        readonly IEditorLockApi _lockApi;
        readonly MainThreadPump _pump;
        readonly Func<DateTime> _utcNow;
        readonly Action<string> _logWarning;
        readonly object _sync = new object();
        readonly Timer _ttlWatchdog;
        ReloadLease _lease;
        bool _disposed;

        /// <summary>Guarded by <see cref="_sync"/>. When the CURRENT lease was originally
        /// acquired (Released -&gt; Held) - fixed for the lease's whole continuous hold, unlike
        /// <see cref="ReloadLease.LastActivityUtc"/> which moves on every renewal. This is what
        /// lets <see cref="CheckHeldWarning"/> measure "how long has the user's Editor actually
        /// been unable to recompile", which a diligently-renewed lease's LastActivityUtc alone
        /// could never show (it would look "fresh" forever).</summary>
        DateTime _heldSinceUtc;

        /// <summary>Guarded by <see cref="_sync"/>. True once <see cref="CheckHeldWarning"/> has
        /// warned for the CURRENT hold - reset only when that hold ends (see <see cref="ReleaseLocked"/>)
        /// so a later, independent hold warns again, but this same one never repeats.</summary>
        bool _warnedThisHold;

        /// <summary>Guarded by <see cref="_sync"/>. True while a release requested off-thread (TTL
        /// or disconnect - see <see cref="RequestOffThreadRelease"/>) has been enqueued onto
        /// <see cref="_pump"/> but not yet applied, so a second off-thread request in the meantime
        /// is dropped instead of growing the queue for an outcome already coming.</summary>
        bool _releasePending;

        /// <param name="lockApi">The seam over EditorApplication.Lock/UnlockReloadAssemblies - see
        /// <see cref="IEditorLockApi"/>.</param>
        /// <param name="pump">Where a release requested off-thread (TTL expiry, socket disconnect)
        /// is deferred to actually run - see <see cref="RequestOffThreadRelease"/> and this
        /// class's doc comment for why it cannot simply call <see cref="IEditorLockApi.Unlock"/>
        /// inline on whatever thread detected the need to release. Owned and started/disposed by
        /// the caller, same as every other type in this plugin that holds a reference to it.</param>
        /// <param name="utcNow">Clock consulted for every TTL decision. Defaults to real UTC time;
        /// tests inject a fake so a TTL test never has to sleep for the TTL's own duration.</param>
        /// <param name="ttlPollInterval">How often the background watchdog checks for TTL expiry.
        /// Defaults to 200ms; tests may inject a shorter interval for fast feedback. Always runs
        /// on a ThreadPool thread - see this class's doc comment.</param>
        /// <param name="logWarning">Where <see cref="CheckHeldWarning"/> sends the "reload has
        /// been held too long" message. Defaults to <c>UnityEngine.Debug.LogWarning</c>; tests
        /// inject a fake so the exactly-once guarantee can be asserted deterministically. Safe to
        /// call from any thread, unlike <see cref="IEditorLockApi"/> - Unity's own logging API is
        /// thread-safe, so unlike the release paths this needs no <see cref="MainThreadPump"/>
        /// deferral.</param>
        public ReloadGate(IEditorLockApi lockApi, MainThreadPump pump, Func<DateTime> utcNow = null,
            TimeSpan? ttlPollInterval = null, Action<string> logWarning = null)
        {
            _lockApi = lockApi ?? throw new ArgumentNullException(nameof(lockApi));
            _pump = pump ?? throw new ArgumentNullException(nameof(pump));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _logWarning = logWarning ?? (message => UnityEngine.Debug.LogWarning(message));

            // Boot reconciliation - see class doc comment for why this is conditional on
            // SessionState rather than an unconditional force-unlock. Runs synchronously right
            // here, never deferred through _pump: construction itself always happens on the main
            // thread (Editor startup / post-domain-reload construction), so there is no off-thread
            // hazard to defer around for this call specifically - unlike CheckTtl and
            // ReleaseOnDisconnect, which can run on literally any thread.
            var flagged = SessionState.GetBool(HeldSessionStateKey, false);
            Trace("BOOT reconcile: sessionStateHeld=" + flagged);
            if (flagged)
            {
                Trace("BOOT releasing a lock recorded by a previous domain load");
                _lockApi.Unlock();
                SessionState.EraseBool(HeldSessionStateKey);
            }

            // Runs for this gate's whole lifetime rather than only while a lease is held - a
            // watchdog with start/stop transitions has more edge cases to get wrong than one that
            // is simply always on and cheaply no-ops when there is no lease. Drives both the TTL
            // check and the held-too-long console warning (see CheckTtl and CheckHeldWarning) off
            // the same tick, rather than a second Timer, for the same reason.
            var interval = ttlPollInterval ?? DefaultTtlPollInterval;
            _ttlWatchdog = new Timer(OnWatchdogTick, null, interval, interval);
        }

        public bool IsHeld { get { lock (_sync) return _lease != null; } }

        /// <summary>The id of the currently held lease, or null when Released.</summary>
        public string CurrentLeaseId { get { lock (_sync) return _lease?.Id; } }

        /// <summary>The currently held lease, or null when Released. Exposed for introspection
        /// (tests, and eventually status reporting) - not a snapshot, so treat fields read off it
        /// as possibly stale the instant this getter returns.</summary>
        public ReloadLease CurrentLease { get { lock (_sync) return _lease; } }

        /// <summary>Acquires the gate under <paramref name="leaseId"/>. Returns true if the gate is
        /// now held by this lease - either because it just locked (Released -&gt; Held, calling
        /// <c>Lock()</c> exactly once and starting a new lease with <paramref name="ttl"/>, or
        /// <see cref="DefaultTtl"/> when omitted) or because it was already held by this same id
        /// (no further <c>Lock()</c> call; re-acquiring your own lease counts as real activity, so
        /// this renews using the TTL the lease was created with - <paramref name="ttl"/> is
        /// ignored on a renewal, since a lease's duration is fixed at creation, not renegotiated).
        /// Returns false if a DIFFERENT lease already holds the gate; that lease is left
        /// untouched.</summary>
        public bool Acquire(string leaseId, TimeSpan? ttl = null)
        {
            Trace("ACQUIRE requested lease=" + leaseId + " currentlyHeld=" + (CurrentLease == null ? "<none>" : CurrentLease.Id));
            if (string.IsNullOrEmpty(leaseId)) throw new ArgumentException("Lease id must not be null or empty.", nameof(leaseId));

            lock (_sync)
            {
                var now = _utcNow();
                if (_lease == null)
                {
                    _lease = new ReloadLease(leaseId, ttl ?? DefaultTtl, now);
                    _heldSinceUtc = now;
                    _warnedThisHold = false;
                    _lockApi.Lock();
                    SessionState.SetBool(HeldSessionStateKey, true);
                    return true;
                }

                if (_lease.Id == leaseId)
                {
                    _lease.Renew(now);
                    return true;
                }

                return false; // held by a different lease - rejected, untouched
            }
        }

        /// <summary>Explicit renewal: extends the held lease's TTL from now, IF
        /// <paramref name="leaseId"/> matches the current holder. Returns false without changing
        /// anything if nothing is held, or a different lease holds the gate - renewal is activity,
        /// and only the owner's activity counts (spec rule 4: "intent does not renew").</summary>
        public bool Renew(string leaseId)
        {
            if (string.IsNullOrEmpty(leaseId)) throw new ArgumentException("Lease id must not be null or empty.", nameof(leaseId));

            lock (_sync)
            {
                if (_lease == null || _lease.Id != leaseId) return false;
                _lease.Renew(_utcNow());
                return true;
            }
        }

        /// <summary>Releases the gate on behalf of <paramref name="leaseId"/>. Returns true if the
        /// gate is Released when this call returns - either because it just unlocked (Held -&gt;
        /// Released, calling <c>Unlock()</c> exactly once) or because it was already Released
        /// (<c>Unlock()</c> is called ZERO times - never unlock what was not locked). Returns false
        /// if a DIFFERENT lease currently holds the gate; that lease is left untouched and no
        /// <c>Unlock()</c> call happens.</summary>
        public bool Release(string leaseId)
        {
            if (string.IsNullOrEmpty(leaseId)) throw new ArgumentException("Lease id must not be null or empty.", nameof(leaseId));

            lock (_sync)
            {
                if (_lease == null) return true;
                if (_lease.Id != leaseId) return false;

                ReleaseLocked("lease.release");
                return true;
            }
        }

        /// <summary>The background watchdog's single Timer callback - runs both checks that need
        /// to happen periodically off the main thread. Runs on a ThreadPool thread, never the
        /// main thread - see <see cref="CheckTtl"/> and <see cref="CheckHeldWarning"/> for what
        /// each may and may not do from here.</summary>
        void OnWatchdogTick(object state)
        {
            CheckTtl(state);
            CheckHeldWarning();
        }

        /// <summary>Timer callback - runs on a ThreadPool thread, never the main thread. Only
        /// PEEKS at expiry here (read-only, under <see cref="_sync"/>) and defers the actual
        /// release - see <see cref="RequestOffThreadRelease"/> and this class's doc comment for
        /// why calling <see cref="IEditorLockApi.Unlock"/> from this thread would throw
        /// UnityException against a real Editor.</summary>
        void CheckTtl(object state)
        {
            bool expired;
            lock (_sync)
            {
                if (_disposed || _lease == null) return;
                expired = _lease.IsExpired(_utcNow());
            }

            if (expired) RequestOffThreadRelease();
        }

        /// <summary>
        /// Warns in the Unity console, exactly once per continuous hold, once a lease has been
        /// held for longer than <see cref="HeldWarningThreshold"/> - measured from ORIGINAL
        /// acquisition (<see cref="_heldSinceUtc"/>), not from last activity, so a lease an agent
        /// diligently keeps renewing every few seconds still eventually warns: from the user's
        /// point of view, recompiling is blocked the whole time regardless of how often the lease
        /// itself gets renewed. This is the ONE place this plugin is deliberately loud - reconnect
        /// is silent because it is routine (see HadesClient's own class doc comment); a reload
        /// lock held past this threshold is not routine, and silence there is the bug the
        /// release-paths/visibility plan exists to prevent.
        ///
        /// Safe to call from any thread - <c>UnityEngine.Debug.LogWarning</c> is thread-safe,
        /// unlike <see cref="IEditorLockApi"/> (see this class's own doc comment) - so, unlike the
        /// release paths, this never needs to defer through <see cref="MainThreadPump"/>.
        /// </summary>
        void CheckHeldWarning()
        {
            string leaseId;
            DateTime heldSinceUtc;
            TimeSpan heldFor;

            lock (_sync)
            {
                if (_disposed || _lease == null || _warnedThisHold) return;

                heldFor = _utcNow() - _heldSinceUtc;
                if (heldFor < HeldWarningThreshold) return;

                _warnedThisHold = true;
                leaseId = _lease.Id;
                heldSinceUtc = _heldSinceUtc;
            }

            _logWarning(
                "[Hades] Unity's reload lock has been held for over " + HeldWarningThreshold.TotalSeconds.ToString("F0")
                + "s (lease '" + leaseId + "', held since " + heldSinceUtc.ToString("O") + " UTC). "
                + "Unity will NOT recompile your scripts until it is released - either the owning "
                + "agent calls lease.release, or it clears on its own once the lease's TTL expires. "
                + "Check hades_charon_status for current status.");
        }

        /// <summary>Releases whatever is currently held, from any thread, without needing to know
        /// its lease id - call when the connection that owned it has just dropped, so nobody
        /// remains who could ever call <see cref="Release"/> with the right id. A safe no-op when
        /// nothing is held. Deferred exactly like TTL expiry - see
        /// <see cref="RequestOffThreadRelease"/> - so it applies on the next main-thread tick,
        /// ahead of all other queued work, never inline on the calling thread.</summary>
        public void ReleaseOnDisconnect() => RequestOffThreadRelease();

        /// <summary>Requests a release from any thread without needing to know which lease id is
        /// held - used when there is nobody left who could supply the right id: the TTL watchdog
        /// after silence (<see cref="CheckTtl"/>), or <see cref="ReleaseOnDisconnect"/> after the
        /// socket that owned the lease drops. Never calls <see cref="IEditorLockApi.Unlock"/>
        /// itself - only enqueues the release, ahead of all other queued work (see
        /// <see cref="MainThreadPump.EnqueuePriority"/>), for whichever thread next calls
        /// <see cref="MainThreadPump.Tick"/> (the main thread, in production) to actually apply.
        ///
        /// De-duplicated via <see cref="_releasePending"/>: while a request from either source is
        /// already queued and not yet applied, a further request is dropped rather than growing
        /// the queue for an outcome that is already coming. This is also what makes two paths
        /// firing at the same moment (e.g. disconnect right as TTL expires) unlock exactly once -
        /// though even without this guard, correctness would still hold: the enqueued action
        /// re-checks <see cref="_lease"/> under <see cref="_sync"/> before ever calling
        /// <see cref="ReleaseLocked"/>, so a second one to actually run would simply see nothing
        /// held and no-op.
        ///
        /// Deliberately does NOT re-validate why the release was requested once the enqueued
        /// action actually runs - e.g. TTL does not re-check expiry at that point, only whether
        /// something is still held. A renewal that narrowly races an already-enqueued TTL release
        /// can lose that race and be released anyway; that is accepted, because the alternative
        /// failure direction is a hanging lock, and this design must never produce one. Losing a
        /// race releases a fraction of a second early - recoverable, the next Acquire simply
        /// starts a new lease. A hanging lock is silent and unrecoverable in-process. See the
        /// plan's explicit priority: no hanging locks, ever.</summary>
        void RequestOffThreadRelease()
        {
            lock (_sync)
            {
                if (_disposed || _releasePending) return;
                _releasePending = true;
            }

            _pump.EnqueuePriority(() =>
            {
                lock (_sync)
                {
                    _releasePending = false;
                    if (_disposed || _lease == null) return;
                    ReleaseLocked("off-thread (ttl or disconnect)");
                }
            });
        }

        /// <summary>Caller must already hold <see cref="_sync"/>.</summary>
        /// <summary>Transition log. Deliberately unconditional and on by default: the Task 7 E2E
        /// failure was diagnosed almost entirely from a log line that did NOT appear, and a gate
        /// whose state changes are invisible cannot be debugged against a real Editor at all.
        /// Cheap - a handful of lines per lease, not per tick.</summary>
        static void Trace(string message) => UnityEngine.Debug.Log("[Hades.ReloadGate] " + message);

        void ReleaseLocked(string reason)
        {
            Trace("RELEASE lease=" + (_lease == null ? "<none>" : _lease.Id) + " reason=" + reason);
            _lockApi.Unlock();
            _lease = null;
            _warnedThisHold = false; // so the NEXT hold (a genuinely new Acquire) can warn again
            SessionState.EraseBool(HeldSessionStateKey);
        }

        /// <summary>Stops the TTL watchdog. Does not release a currently held lease - by the time
        /// this runs (e.g. HadesBoot tearing down before a domain reload) SessionState already
        /// records what is held, for the next instance's boot reconciliation to pick up.</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }

            _ttlWatchdog.Dispose();
        }
    }
}
