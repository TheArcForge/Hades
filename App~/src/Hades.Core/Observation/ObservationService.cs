namespace Hades.Core.Observation;

/// <summary>
/// Keeps every known project's graph current: a catch-up sweep on start, a watcher per project
/// for live changes, and a periodic sweep as the safety net that makes correctness independent of
/// the watcher.
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

    /// <summary>Raised after a project is synced. Exists so the host can log without this class
    /// taking a logging dependency, and so tests can observe progress without sleeping.</summary>
    public event Action<string, SweepResult>? ProjectSynced;

    public TimeSpan PeriodicInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(500);

    public void Start()
    {
        foreach (var project in projects.KnownProjects())
        {
            // Catch-up first: whatever changed while this process was not running is found here,
            // and it is the entire reason a sweep exists rather than only a watcher.
            Sync(project.ProductGuid);
            Watch(project.ProductGuid, project.Path);
        }

        _periodicSweep = new Timer(_ => SyncAll(), null, PeriodicInterval, PeriodicInterval);
    }

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
        if (!_indexGate.Wait(TimeSpan.FromMinutes(2))) return;

        try
        {
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
            // Guards against the known Dispose()/Sync() teardown race: Dispose() can run - and
            // dispose _indexGate - while this call is still between the Wait() above and this
            // finally, on another thread (or synchronously, if disposing is itself triggered from
            // a ProjectSynced handler). Guarding the release (rather than reordering Dispose to
            // wait out every in-flight Sync first) is the minimal fix: Dispose already tears down
            // _watchers and _periodicSweep unconditionally without waiting for them either, and
            // making Dispose block on a sync that can itself wait up to two minutes for the gate
            // would trade a rare, harmless race for a routine, user-visible stall. Once _indexGate
            // is disposed there is nothing left to release into - the semaphore it would have
            // signalled is already gone - so there is nothing to do here but let it pass.
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

        _periodicSweep?.Dispose();
        _periodicSweep = null;

        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
        _indexGate.Dispose();
    }
}
