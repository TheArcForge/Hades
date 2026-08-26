using System.Net.Http;
using Hades.Control.Client;

namespace Hades.Supervision;

/// <summary>
/// Decides whether the Hades core is already running, adopts it if so, spawns it if not, restarts
/// it if a spawned instance dies, and guarantees a spawned instance never outlives the process that
/// spawned it (see <see cref="ICoreProcessHost"/>'s own doc comment for how that guarantee is
/// established on Windows).
///
/// This is the Windows port of Mac/HadesSupervision/Sources/HadesSupervision/CoreSupervisor.swift -
/// a port, not a redesign. Every doc comment below that says "matches Swift" or cites a bug is
/// citing that file directly. The one deliberate structural difference: this type contains NO
/// P/Invoke of its own. Every OS-specific action goes through <see cref="ICoreProcessHost"/>, so
/// this file - the adopt/spawn/backoff/stable-uptime/ownership DECISION logic - compiles and runs
/// on any platform, including the macOS machine this port was written on. Only
/// <see cref="Win32CoreProcessHost"/> needs real Windows.
///
/// Swift's version is an <c>actor</c>: every method body runs with exclusive access to its own
/// state, but a method can be safely SUSPENDED (at an <c>await</c>) while another actor method
/// runs, then resume without any other actor method having interleaved with it mid-statement. This
/// type gets the same property without an actor runtime by protecting every individual state
/// mutation with <see cref="_sync"/>, but never holding that lock across an <c>await</c> - so two
/// calls can still interleave at suspension points (exactly what <c>Stop()</c> racing a
/// backoff-sleeping restart cycle depends on - see <c>SpawnWithRetriesAsync</c>'s own comment),
/// while each individual read-modify-write of <see cref="_state"/> and friends stays atomic.
/// </summary>
public sealed class CoreSupervisor
{
    /// <summary>
    /// Everything <see cref="CoreSupervisor"/> needs to adopt-or-spawn and supervise the core. The
    /// platform-neutral analogue of Swift's <c>CoreSupervisor.Configuration</c> - the reaper
    /// executable Swift has to point at does not exist here (see <see cref="ICoreProcessHost"/>'s
    /// own doc comment for why: the Job Object plays that role, built into
    /// <see cref="Win32CoreProcessHost"/> itself rather than a separate binary).
    /// </summary>
    public sealed class Configuration
    {
        /// <summary>
        /// The application-data root <see cref="Discovery.Read"/> reads <c>control.token</c> from,
        /// and the value written into the spawned core's <c>HADES_HOME</c> environment variable
        /// (see <see cref="BuildStartInfo"/>) - explicitly threaded through rather than read from
        /// the real environment internally, so a spawned core is always pointed at the exact same
        /// root this supervisor itself reads back. Matches Swift's own reasoning for
        /// <c>Configuration.home</c>, minus that type's env-var-reading default: callers here
        /// resolve <c>HADES_HOME</c> (or its per-platform default) themselves before constructing
        /// this, keeping this type a pure consumer of the value with no environment knowledge of
        /// its own.
        /// </summary>
        public required string Home { get; init; }

        /// <summary>The core's executable and already-quoted-and-joined argument string - the
        /// same shape <see cref="ProcessLauncher.LaunchSuspended"/> itself takes.</summary>
        public required string CoreExecutable { get; init; }
        public required string CoreArguments { get; init; }

        /// <summary>Extra environment variables merged over the spawned process's environment.
        /// <c>HADES_HOME</c> is set automatically from <see cref="Home"/> - callers do not need to
        /// duplicate it here. (NOTE: <see cref="Win32CoreProcessHost"/> does not yet thread this
        /// dictionary through to <c>CreateProcessW</c>'s own environment block - see that type's
        /// own comment. Building it here regardless keeps this type's public surface stable for
        /// when that's wired up.)</summary>
        public IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; } =
            new Dictionary<string, string>();

        public int MaxRestartAttempts { get; init; } = 5;

        public Func<int, TimeSpan> Backoff { get; init; } = DefaultBackoff;

        public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(15);
        public TimeSpan PingPollInterval { get; init; } = TimeSpan.FromMilliseconds(200);
        public TimeSpan AdoptionProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long a spawned core must stay running before its death is treated as a FRESH
        /// problem (attempt budget resets) rather than a continuation of whatever already-diagnosed
        /// problem produced the previous death (attempt budget keeps depleting).
        ///
        /// Ported directly from Swift's <c>Configuration.minimumStableUptime</c>, which documents
        /// exactly the bug this exists to close: without it, a core that answers one ping and then
        /// dies moments later gets a brand-new full-attempts budget on EVERY death, so
        /// <see cref="MaxRestartAttempts"/> never actually binds - measured live on the Swift side
        /// at 49 spawn attempts in 75 seconds, still going. Three seconds is comfortably past any
        /// plausible "answered ping while already doomed" window (measured there at ~100ms) while
        /// still short enough that a core which genuinely recovers is not kept on a depleting
        /// budget for long. See <see cref="HandleCoreProcessExitAsync"/>'s own comment for exactly
        /// where this is read.
        /// </summary>
        public TimeSpan MinimumStableUptime { get; init; } = TimeSpan.FromSeconds(3);

        /// <summary>1s, 2s, 4s, 8s, 16s - doubling, capped at 16s. Matches Swift's
        /// <c>Configuration.defaultBackoff</c> exactly, including the cap rationale: the default
        /// <see cref="MaxRestartAttempts"/> (5) with this backoff spends about 15s sleeping between
        /// attempts before giving up - long enough to ride out a slow one-off hiccup, short enough
        /// that a genuinely broken core does not look alive for minutes.</summary>
        public static TimeSpan DefaultBackoff(int attempt) =>
            TimeSpan.FromSeconds(Math.Min(16, 1 << Math.Max(0, attempt - 1)));
    }

    private readonly Configuration _configuration;
    private readonly ICoreProcessHost _host;
    private readonly ISupervisionClock _clock;

    /// <summary>Guards every read-modify-write of the fields below. Never held across an
    /// <c>await</c> - see this type's own class doc comment for why that matters.</summary>
    private readonly object _sync = new();

    private SupervisorState _state = SupervisorState.NotStarted;
    private Ownership? _ownership;
    private ControlConnection? _connection;
    private ICoreProcess? _currentProcess;
    private bool _isStopping;

    /// <summary>How many spawn attempts have been used in the restart cycle currently in progress.
    /// Persists ACROSS calls to <see cref="SpawnWithRetriesAsync"/> - see that method and
    /// <see cref="HandleCoreProcessExitAsync"/>'s own doc comments for why: resetting this to zero
    /// on every death is the exact bug <see cref="Configuration.MinimumStableUptime"/> exists to
    /// close. Reset to zero only by <see cref="StartAsync"/> (a fresh, caller-initiated attempt) or
    /// by <see cref="HandleCoreProcessExitAsync"/> noticing the core that just died had proven
    /// itself stable first.</summary>
    private int _attemptsUsedInCurrentCycle;

    /// <summary>When the most recent spawn was last confirmed running - <see langword="null"/>
    /// whenever there is no live spawned core to measure. Read by
    /// <see cref="HandleCoreProcessExitAsync"/> against <see cref="Configuration.MinimumStableUptime"/>
    /// to decide whether a death earned a fresh budget.</summary>
    private DateTimeOffset? _lastSpawnBecameRunningAt;

    public CoreSupervisor(Configuration configuration, ICoreProcessHost host, ISupervisionClock? clock = null)
    {
        _configuration = configuration;
        _host = host;
        _clock = clock ?? SystemSupervisionClock.Instance;
    }

    public SupervisorState State
    {
        get { lock (_sync) return _state; }
    }

    /// <summary>Convenience for callers that only care whether quitting is safe right now, without
    /// switching over the full <see cref="State"/> machine.</summary>
    public Ownership? CurrentOwnership
    {
        get { lock (_sync) return _ownership; }
    }

    /// <summary>
    /// Adopt-or-spawn. If a core is already reachable via the discovery file, attaches to it
    /// without spawning anything. Otherwise spawns one and waits, with bounded retries and backoff,
    /// for it to come up. Idempotent: calling this while already starting, running, or restarting
    /// is a no-op - matches Swift's own <c>start()</c> exactly.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            switch (_state.Kind)
            {
                case SupervisorStateKind.Starting:
                case SupervisorStateKind.Running:
                case SupervisorStateKind.Restarting:
                    return;
            }

            _state = SupervisorState.Starting;
            // A fresh, caller-initiated start always gets the full budget - see
            // _attemptsUsedInCurrentCycle's own doc comment. Harmless when this ends up adopting
            // instead of spawning (the adopt branch below never reads it).
            _attemptsUsedInCurrentCycle = 0;
        }

        var existing = Discovery.Read(_configuration.Home);
        if (existing is not null && await CanPingAsync(existing, cancellationToken).ConfigureAwait(false))
        {
            lock (_sync)
            {
                _connection = existing;
                _ownership = Ownership.Adopted;
                _state = SupervisorState.Running(Ownership.Adopted);
            }

            return;
        }

        lock (_sync) { _ownership = Ownership.Spawned; }
        await SpawnWithRetriesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-validates the current core without spawning or restarting anything. A spawned core's
    /// death is already detected event-drivenly (<see cref="MonitorForExitAsync"/>), so this exists
    /// specifically for the ADOPTED case, which has no such signal: this supervisor does not own
    /// that process, so the only way to know it is gone is to ask. Drops <see cref="State"/> back
    /// to <see cref="SupervisorStateKind.NotStarted"/> so a later <see cref="StartAsync"/> re-runs
    /// the normal adopt-or-spawn decision, but never spawns anything itself - matches Swift's own
    /// <c>refresh()</c> exactly, including that callers drive the poll cadence; this type runs no
    /// background timer of its own.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ControlConnection? connection;
        lock (_sync)
        {
            if (_state.Kind != SupervisorStateKind.Running
                || _ownership != Ownership.Adopted
                || _connection is null)
            {
                return;
            }

            connection = _connection;
        }

        if (!await CanPingAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            lock (_sync)
            {
                _connection = null;
                _ownership = null;
                _state = SupervisorState.NotStarted;
            }
        }
    }

    /// <summary>
    /// Graceful, caller-initiated shutdown. If the current core is spawned, terminates it and waits
    /// for that to finish. If adopted, does nothing to the core at all - it outlives this call. This
    /// is the entire "quit stops Hades in one case and not the other" trade the adopt-or-spawn
    /// decision makes, and matches Swift's <c>stop()</c> exactly, including the ordering: every
    /// field that marks this core as "ours" is cleared BEFORE <c>Terminate()</c> is ever called, and
    /// with no <c>await</c> in between - closing the same race Swift's own comment calls S2 (a
    /// late-firing exit handler respawning a core as the caller quits). <see cref="_state"/> leaving
    /// <see cref="SupervisorStateKind.Running"/> makes <see cref="HandleCoreProcessExitAsync"/>'s own
    /// state guard reject a stale exit regardless of timing; clearing <see cref="_currentProcess"/>
    /// also makes its identity guard reject it even if <see cref="_state"/> somehow read
    /// <see cref="SupervisorStateKind.Running"/> again by then (e.g. a subsequent
    /// <see cref="StartAsync"/>).
    ///
    /// Never runs for an ADOPTED core: the guard below returns before any of this, so
    /// adopt-never-kill holds exactly as it does in Swift - this method still does not touch
    /// <see cref="_ownership"/>/<see cref="_state"/>/<see cref="_currentProcess"/> at all when the
    /// current core is adopted.
    /// </summary>
    public async Task StopAsync()
    {
        ICoreProcess? process;
        lock (_sync)
        {
            if (_ownership != Ownership.Spawned || _currentProcess is null || !_currentProcess.IsRunning)
            {
                return;
            }

            process = _currentProcess;
            _isStopping = true;
            _state = SupervisorState.NotStarted;
            _ownership = null;
            _currentProcess = null;
        }

        try
        {
            process.Terminate();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.Exited.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Best-effort wait only - Terminate() was already requested above regardless.
            }
        }
        finally
        {
            lock (_sync) { _isStopping = false; }
        }

        // Disposal happens in MonitorForExitAsync, which is still awaiting this same process's
        // Exited task in the background and will observe it complete right after Terminate() above
        // - not here, to avoid a double-dispose race between the two call sites.
    }

    // MARK: - Spawn / restart

    /// <summary>
    /// Resumes from <see cref="_attemptsUsedInCurrentCycle"/>, NOT zero - the fix for the bug
    /// Swift's own doc comment describes (measured live: 49 spawn attempts in 75 seconds, still
    /// going). Continuing from wherever the cycle left off is what makes the budget actually
    /// deplete across repeated fast deaths, while a call from a fresh <see cref="StartAsync"/>
    /// (where <see cref="_attemptsUsedInCurrentCycle"/> is always reset to 0 first) behaves exactly
    /// as before.
    ///
    /// Bails without spawning if <see cref="_state"/> reads <see cref="SupervisorStateKind.NotStarted"/>
    /// after either suspension point below (the backoff delay, or <see cref="SpawnOnceAsync"/>'s own
    /// awaits) - matches Swift's own Defect D-B fix. <see cref="StopAsync"/> sets
    /// <see cref="_state"/> to <see cref="SupervisorStateKind.NotStarted"/> synchronously, with no
    /// <c>await</c> of its own in between, before it ever terminates the process this cycle might
    /// still be polling - so a resume that observes <see cref="SupervisorStateKind.NotStarted"/>
    /// here is proof a stop happened while this cycle was suspended, and must not spawn again or
    /// overwrite <see cref="SupervisorStateKind.NotStarted"/> with anything else. If a spawn attempt
    /// nonetheless SUCCEEDED in that exact window (the attempt started before <c>Stop()</c> ran, and
    /// finished after), the newly-spawned process is torn down here rather than left running
    /// unsupervised - the same "no core is worse than an unsupervised core" principle
    /// <see cref="CoreProcessAssignmentException"/> is built around, applied to a narrow timing
    /// window the Swift original does not close.
    /// </summary>
    private async Task SpawnWithRetriesAsync(CancellationToken cancellationToken)
    {
        int attempt;
        lock (_sync) { attempt = _attemptsUsedInCurrentCycle; }

        while (attempt < _configuration.MaxRestartAttempts)
        {
            attempt++;
            if (attempt > 1)
            {
                lock (_sync) { _state = SupervisorState.Restarting(attempt); }
                await _clock.DelayAsync(_configuration.Backoff(attempt - 1), cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_state.Kind == SupervisorStateKind.NotStarted) return;
                }
            }

            ICoreProcess? process;
            try
            {
                process = await SpawnOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (CoreProcessAssignmentException)
            {
                // Fail loudly and refuse to spawn - see CoreProcessAssignmentException's own doc
                // comment. Not a retryable per-attempt failure like an ordinary launch error:
                // continuing to retry would just keep trying to hand back more unsupervised cores.
                lock (_sync)
                {
                    if (_state.Kind == SupervisorStateKind.NotStarted) return;
                    _attemptsUsedInCurrentCycle = attempt;
                    _state = SupervisorState.Failed(attempt);
                }

                return;
            }

            bool bail;
            lock (_sync) { bail = _state.Kind == SupervisorStateKind.NotStarted; }
            if (bail)
            {
                if (process is not null)
                {
                    process.Terminate();
                    process.Dispose();
                }

                return;
            }

            if (process is not null)
            {
                lock (_sync)
                {
                    _state = SupervisorState.Running(Ownership.Spawned);
                    _attemptsUsedInCurrentCycle = attempt;
                    _currentProcess = process;
                    _lastSpawnBecameRunningAt = _clock.UtcNow;
                }

                MonitorForExit(process);
                return;
            }
        }

        lock (_sync)
        {
            _attemptsUsedInCurrentCycle = attempt;
            _state = SupervisorState.Failed(attempt);
        }
    }

    /// <summary>
    /// One spawn attempt: launches the core, then polls the discovery file plus
    /// <c>/control/ping</c> until it answers or <see cref="Configuration.PingTimeout"/> elapses.
    /// Returns the live process on success, <see langword="null"/> on any kind of failure. Matches
    /// Swift's <c>spawnOnce()</c> exactly, including the final re-check of liveness right before
    /// committing to success: <see cref="CanPingAsync"/> suspended on a real network call, and the
    /// process could in principle have died in that exact window - this closes the gap cheaply
    /// rather than reporting success for a core that is already gone.
    /// </summary>
    private async Task<ICoreProcess?> SpawnOnceAsync(CancellationToken cancellationToken)
    {
        var process = _host.Spawn(BuildStartInfo()); // CoreProcessAssignmentException propagates to the caller.

        var deadline = _clock.UtcNow + _configuration.PingTimeout;
        while (_clock.UtcNow < deadline)
        {
            if (!process.IsRunning)
            {
                process.Dispose();
                return null; // died (or was refused) before ever answering ping
            }

            var discovered = Discovery.Read(_configuration.Home);
            if (discovered is not null && await CanPingAsync(discovered, cancellationToken).ConfigureAwait(false))
            {
                if (process.IsRunning)
                {
                    lock (_sync) { _connection = discovered; }
                    return process;
                }

                process.Dispose();
                return null;
            }

            await _clock.DelayAsync(_configuration.PingPollInterval, cancellationToken).ConfigureAwait(false);
        }

        // Timed out waiting for a ping response; this attempt failed. Stop it before trying again
        // so a hung attempt does not linger alongside the next one.
        process.Terminate();
        process.Dispose();
        return null;
    }

    private void MonitorForExit(ICoreProcess process)
    {
        _ = MonitorForExitAsync(process);
    }

    /// <summary>
    /// Awaits a successfully-spawned process's own exit, then hands off to
    /// <see cref="HandleCoreProcessExitAsync"/>. Only ever started for an attempt that actually
    /// succeeded (see <see cref="SpawnWithRetriesAsync"/>) - unlike Swift, which wires up a
    /// termination handler on EVERY attempt (including ones later abandoned by a ping timeout) and
    /// then has to filter stale firings by process identity in <c>handleCoreProcessExit</c> itself.
    /// Because abandoned/failed attempts here are never subscribed to in the first place, there is
    /// nothing stale to filter - the identity check in <see cref="HandleCoreProcessExitAsync"/> below
    /// still exists, but only to reject a firing that arrives after <see cref="StopAsync"/> or a
    /// later <see cref="StartAsync"/> has already moved <see cref="_currentProcess"/> on.
    /// Disposes <paramref name="process"/> unconditionally once it has exited - the one place that
    /// happens for a process that made it all the way to <see cref="SupervisorStateKind.Running"/>.
    /// </summary>
    private async Task MonitorForExitAsync(ICoreProcess process)
    {
        try
        {
            await process.Exited.ConfigureAwait(false);
        }
        catch
        {
            // Exited should not normally fault; treat any failure to observe it the same as an
            // ordinary exit rather than letting a background task fault silently.
        }

        try
        {
            await HandleCoreProcessExitAsync(process).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Fires when a process this supervisor spawned exits, for any reason. Ignored while
    /// <see cref="StopAsync"/> is deliberately causing exactly this exit (<see cref="_isStopping"/>,
    /// and - matching <see cref="StopAsync"/>'s own doc comment - <see cref="_state"/>/
    /// <see cref="_currentProcess"/> too), and never reachable at all for an adopted core (which has
    /// no monitored process - see <see cref="MonitorForExitAsync"/>).
    ///
    /// The identity guard (<c>exitedProcess</c> must still be <see cref="_currentProcess"/>) is the
    /// real reentrancy guard here, matching Swift's own "S1" comment: even though abandoned attempts
    /// are never monitored (see <see cref="MonitorForExitAsync"/>'s own comment on why this port's
    /// story is simpler than Swift's), a stale firing can still arrive for the CURRENT successful
    /// process after <see cref="StopAsync"/> or a fresh <see cref="StartAsync"/> has already moved
    /// <see cref="_currentProcess"/> on - the state guard alone cannot tell that apart from a real,
    /// current outage.
    ///
    /// Whether this death gets a fresh attempt budget depends on how long the core had been
    /// running - see <see cref="_attemptsUsedInCurrentCycle"/>'s own doc comment for the bug this
    /// closes. A core that ran for at least <see cref="Configuration.MinimumStableUptime"/> proved
    /// itself healthy: whatever killed it is a NEW problem, so it earns a full budget, the same as
    /// <see cref="StartAsync"/> gives a caller-initiated attempt. A core that died sooner never
    /// proved itself, so it keeps consuming the SAME budget <see cref="SpawnWithRetriesAsync"/> is
    /// already working through, rather than resetting it back to zero.
    /// </summary>
    private async Task HandleCoreProcessExitAsync(ICoreProcess exitedProcess)
    {
        bool shouldRespawn;
        lock (_sync)
        {
            if (_isStopping || _ownership != Ownership.Spawned)
            {
                return;
            }

            if (!ReferenceEquals(_currentProcess, exitedProcess))
            {
                return;
            }

            if (_state.Kind != SupervisorStateKind.Running)
            {
                return; // an in-progress SpawnOnceAsync handles its own failure path
            }

            var stableEnough = _lastSpawnBecameRunningAt is { } becameRunningAt
                && _clock.UtcNow - becameRunningAt >= _configuration.MinimumStableUptime;
            if (stableEnough)
            {
                _attemptsUsedInCurrentCycle = 0;
            }

            // Moves state off Running BEFORE any respawn attempt, synchronously, with no await in
            // between - matches Swift's own Defect C1 fix: leaving this to SpawnWithRetriesAsync's
            // own "attempt > 1" check never fires for attempt 1 of a fresh cycle, exactly the case a
            // STABLE core's death always hits (the budget resets to zero above whenever the death
            // already proved itself stable).
            _state = SupervisorState.Restarting(_attemptsUsedInCurrentCycle + 1);
            _currentProcess = null;
            shouldRespawn = true;
        }

        if (shouldRespawn)
        {
            await SpawnWithRetriesAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // MARK: - Helpers

    private CoreProcessStartInfo BuildStartInfo()
    {
        var environment = new Dictionary<string, string>(_configuration.ExtraEnvironment)
        {
            ["HADES_HOME"] = _configuration.Home,
        };

        return new CoreProcessStartInfo(
            _configuration.CoreExecutable, _configuration.CoreArguments, WorkingDirectory: null, environment);
    }

    /// <summary>
    /// Matches Swift's own <c>canPing</c> exactly, including the blanket catch-all: any failure to
    /// get back a decoded ping response - a transport failure, a stale token, a malformed body, or
    /// the request being cancelled - means "not adoptable/not up yet", never a condition worth
    /// surfacing as its own error from this type.
    ///
    /// A fresh <see cref="HttpClient"/> is created for every call rather than one being reused for
    /// this supervisor's whole lifetime: each spawn attempt gets a NEW <see cref="ControlConnection"/>
    /// (a fresh ephemeral port), and <see cref="ControlClient"/>'s constructor sets
    /// <c>BaseAddress</c>/<c>DefaultRequestHeaders</c> on whatever <see cref="HttpClient"/> it is
    /// given - which throws <see cref="InvalidOperationException"/> if that client has already sent
    /// a request. Swift's equivalent (<c>probeSession</c>) can be shared across every call because
    /// <c>URLSession</c> has no such restriction; a short-lived <see cref="HttpClient"/> per call is
    /// this port's equivalent, and cheap enough at the poll cadence this is used at.
    /// </summary>
    private async Task<bool> CanPingAsync(ControlConnection connection, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = _configuration.AdoptionProbeTimeout };
        var client = new ControlClient(connection, http);
        try
        {
            await client.PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
