using Hades.Core.Projects;

namespace Hades.Core.Observation;

/// <summary>
/// Keeps every known project's graph current: a catch-up sweep on start, a watcher per project
/// for live changes, and a periodic sweep as the safety net that makes correctness independent of
/// the watcher.
///
/// F14 fix: a project registered AFTER <see cref="Start"/> already ran — the ordinary shape of
/// POST /control/projects/add or RootsRouter adopting a root mid-session — used to never get a
/// <see cref="ProjectWatcher"/> at all: <see cref="Start"/> only ever enrolled what
/// <see cref="ProjectService.KnownProjects"/> listed at that one moment, and no add-project path
/// called <see cref="Watch"/>. <see cref="Start"/> now also subscribes to
/// <see cref="ProjectService.ProjectAdopted"/>/<see cref="ProjectService.ProjectRemoved"/>, so
/// every FUTURE adopt/remove — regardless of which caller triggers it — enrolls or disposes a
/// watcher the same way. The periodic sweep was never actually broken by this: <see cref="SyncAll"/>
/// re-reads <see cref="ProjectService.KnownProjects"/> fresh on every tick rather than a snapshot
/// taken at <see cref="Start"/>, so a runtime-added project was always eventually synced — only
/// instant, watcher-driven freshness was missing.
/// </summary>
public sealed class ObservationService(ProjectService projects) : IDisposable
{
    readonly Dictionary<string, ProjectWatcher> _watchers = [];

    // One project indexing at a time, globally. Ten known projects must never mean ten
    // concurrent scans competing for the same disk.
    readonly SemaphoreSlim _indexGate = new(1, 1);
    readonly Lock _gate = new();

    Timer? _periodicSweep;
    bool _disposed;

    // Managed thread id of the thread currently inside _indexGate, or 0. Only Dispose reads it,
    // to tell "a sweep is running on another thread, wait for it" apart from "I am being called
    // from inside that sweep" - Dispose from a ProjectSynced handler runs on the very thread
    // holding the gate, so draining there would mean waiting DisposeDrainTimeout for itself.
    int _syncThreadId;

    /// <summary>Raised after a project is synced. Exists so the host can log without this class
    /// taking a logging dependency, and so tests can observe progress without sleeping.</summary>
    public event Action<string, SweepResult>? ProjectSynced;

    /// <summary>How long <see cref="Dispose"/> waits for an in-flight sweep to finish before
    /// giving up on it. See the drain in <see cref="Dispose"/> for why it is bounded.</summary>
    static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(30);

    public TimeSpan PeriodicInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(500);

    public void Start()
    {
        // Subscribed BEFORE the catch-up loop below, not after: a project adopted concurrently
        // while this loop is still running must not fall in the gap between "KnownProjects() was
        // read" and "we started listening for adopts". Watch/Unwatch are idempotent either way
        // (see their own doc comments), so a project this loop AND the event both see costs one
        // harmless extra dictionary check, never a double-watch or a missed one.
        projects.ProjectAdopted += OnProjectAdopted;
        projects.ProjectRemoved += Unwatch;

        foreach (var project in projects.KnownProjects())
        {
            // Catch-up first: whatever changed while this process was not running is found here,
            // and it is the entire reason a sweep exists rather than only a watcher.
            Sync(project.ProductGuid);
            Watch(project.ProductGuid, project.Path);
        }

        _periodicSweep = new Timer(_ => SyncAll(), null, PeriodicInterval, PeriodicInterval);
    }

    /// <summary>F14: the ONLY thing that enrolls a watcher for a project adopted (or re-adopted)
    /// after <see cref="Start"/> already ran — see this class's own doc comment. Watch itself
    /// already handles "already watching this project", so re-firing on every RootsRouter-driven
    /// Adopt call costs one idempotent dictionary check, never a duplicate watcher.</summary>
    void OnProjectAdopted(UnityProject project) => Watch(project.ProductGuid, project.Path);

    /// <summary>Begins watching a project, syncing it first. Safe to call for an already-watched
    /// project.</summary>
    public void Watch(string productGuid, string projectPath)
    {
        lock (_gate)
        {
            if (_disposed || _watchers.ContainsKey(productGuid)) return;

            var watcher = new ProjectWatcher(projectPath, Debounce);
            watcher.ChangesSettled += () => Sync(productGuid);
            _watchers[productGuid] = watcher;
        }
    }

    /// <summary>F14's "and dispose on remove" half: stops and disposes <paramref
    /// name="productGuid"/>'s live watcher, if it has one. Safe to call for a project that was
    /// never watched, or was already unwatched — the same "safe to call twice" contract every
    /// other lifecycle method on this class holds.</summary>
    public void Unwatch(string productGuid)
    {
        lock (_gate)
        {
            if (!_watchers.Remove(productGuid, out var watcher)) return;
            watcher.Dispose();
        }
    }

    public void SyncAll()
    {
        foreach (var project in projects.KnownProjects()) Sync(project.ProductGuid);
    }

    /// <summary>
    /// Brings one project up to date. Serialised against every other project's sync, and silent
    /// when nothing changed — an unchanged project costs one sweep and no writes.
    /// </summary>
    public void Sync(string productGuid)
    {
        lock (_gate) { if (_disposed) return; }

        bool acquired;
        try { acquired = _indexGate.Wait(TimeSpan.FromMinutes(2)); }
        catch (ObjectDisposedException) { return; } // disposed before we could even acquire - nothing to sync, nothing to release

        if (!acquired) return;
        Volatile.Write(ref _syncThreadId, Environment.CurrentManagedThreadId);

        try
        {
            // Re-checked INSIDE the gate. The check above can pass and Dispose() can then run in
            // full while this call is still queued in Wait() - a watcher's Fire() invokes
            // ChangesSettled outside its own lock, so ProjectWatcher.Dispose() returning is no
            // promise that a sweep is not about to start. Without this, that sweep opens graph.db
            // AFTER teardown believes observation has stopped.
            lock (_gate) { if (_disposed) return; }

            if (projects.SyncChanges(productGuid) is { } sweep && sweep.AnythingChanged)
                ProjectSynced?.Invoke(productGuid, sweep);
        }
        catch (Exception)
        {
            // Deliberately unconditional - same stance as, and explicitly citing,
            // ToolCallTracer.RecordSafely's documented "nothing this method does is allowed to
            // escape it" rule. A narrower "catch (Exception ex) when (ex is IOException or
            // UnauthorizedAccessException)" (a project on an unmounted volume, or briefly
            // unreadable) used to live here, but projects.SyncChanges -> GraphDatabase.Open can
            // also throw InvalidOperationException (WAL mode refused - another process, e.g.
            // cloud sync/AV/Time Machine, briefly holds the file) or SqliteException (SQLITE_BUSY),
            // neither of which that filter covers. This runs on a Timer/watcher background thread,
            // where an unhandled exception is not "this sync failed" but an unhandled-exception
            // process crash for every project Hades knows about. The next sweep retries; failing
            // here must not take down observation for every other project.
        }
        finally
        {
            // Guards the Dispose()/Sync() teardown race that survives the drain. Dispose() waits
            // for this gate precisely so it does NOT return while a sweep still holds graph.db -
            // but that wait is bounded, so a sweep slower than DisposeDrainTimeout still ends up
            // releasing into a semaphore Dispose() has already disposed. The same applies when
            // disposal is triggered synchronously from a ProjectSynced handler, i.e. from inside
            // this very try. Once _indexGate is disposed there is nothing left to release into -
            // the semaphore it would have signalled is already gone - so let it pass.
            //
            // The same race can also land BEFORE this call ever acquires the gate - Dispose()
            // beating a scheduled Sync() to _indexGate.Dispose() entirely - in which case
            // _indexGate.Wait() above throws ObjectDisposedException itself. That throw is caught
            // right at the call above, not here: it happens outside this try, so this finally
            // never runs for that case, and there is equally nothing to release into. Both ends of
            // the same teardown race are now handled: the acquire returns quietly, the release
            // no-ops.
            Volatile.Write(ref _syncThreadId, 0);
            try { _indexGate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Unsubscribe even though Watch/Unwatch would both no-op safely post-Dispose anyway (the
        // same _disposed guard every other method here already relies on) — a disposed instance
        // must not linger as a live subscriber on projects, which can easily outlive it.
        projects.ProjectAdopted -= OnProjectAdopted;
        projects.ProjectRemoved -= Unwatch;

        _periodicSweep?.Dispose();
        _periodicSweep = null;

        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();

        // Wait out a sweep that is already running before declaring observation stopped. Disposing
        // a Timer or a FileSystemWatcher does not wait for a callback already in flight, so
        // everything above can complete while Sync() still holds graph.db and a
        // .project.json.*.tmp open. Acquiring the gate is exactly the proof that no sweep is
        // running: every path that opens a project's graph holds it, and every sweep that has not
        // yet acquired sees _disposed and returns without opening anything.
        //
        // On macOS/Linux the leak was invisible - deleting a file another handle has open unlinks
        // it and succeeds - so this surfaced only as an occasional "Directory not empty" when a
        // late sweep recreated an entry mid-delete. On Windows an open handle blocks the delete
        // outright, and the same race failed six tests on the first Windows CI run.
        //
        // Bounded, so quitting never hangs behind a large project's first index: on timeout this
        // is no worse than the unconditional teardown it replaces.
        // Skipped when this IS the sweeping thread - Dispose() called from a ProjectSynced
        // handler, which runs inside the gate. There is no other thread to wait for, and waiting
        // would just burn DisposeDrainTimeout before timing out against ourselves.
        if (Volatile.Read(ref _syncThreadId) != Environment.CurrentManagedThreadId)
        {
            try { _indexGate.Wait(DisposeDrainTimeout); }
            catch (ObjectDisposedException) { }
        }

        _indexGate.Dispose();
    }
}
