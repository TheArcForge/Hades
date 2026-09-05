using Hades.Core.Indexing;

namespace Hades.Core.Observation;

/// <summary>
/// Watches a project's source roots and raises a debounced signal when anything changes.
///
/// This is a LATENCY optimisation, not the source of truth — <see cref="ProjectSweeper"/> is.
/// A missed event costs freshness until the next periodic sweep, never correctness, which is why
/// there is no attempt to reconstruct exactly which files changed from the event stream.
/// Measured: FileSystemWatcher caught 100% of events at 100, 500 and 2,000 simultaneous writes
/// with zero error events, so the drop-under-burst concern did not materialise — but the sweep
/// stays authoritative regardless.
/// </summary>
public sealed class ProjectWatcher : IDisposable
{
    readonly List<FileSystemWatcher> _watchers = [];
    readonly Lock _gate = new();
    readonly TimeSpan _debounce;
    Timer? _debounceTimer;
    bool _disposed;

    /// <summary>Raised after a quiet period. Carries no paths deliberately: the handler sweeps,
    /// which is both authoritative and cheap (~34 ms on a real project).</summary>
    public event Action? ChangesSettled;

    public ProjectWatcher(string projectRoot, TimeSpan? debounce = null)
    {
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);

        var warnings = new List<string>();
        foreach (var root in ProjectWalker.ResolveScanRoots(projectRoot, warnings))
        {
            if (!Directory.Exists(root.AbsolutePath)) continue;

            var watcher = new FileSystemWatcher(root.AbsolutePath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;

            // A buffer overflow means events were dropped. The response is the same as any other
            // change — sweep — which is precisely why the sweep is the source of truth.
            watcher.Error += (_, _) => Nudge();

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    /// <summary>Number of roots actually being watched — external "file:" packages included.</summary>
    public int WatchedRootCount => _watchers.Count;

    void OnChanged(object sender, FileSystemEventArgs e)
    {
        // Unity churns Library/ and Temp/ constantly during import. Those are pruned from
        // indexing, so reacting to them would mean sweeping continuously for no reason.
        if (IsIgnored(sender, e)) return;
        Nudge();
    }

    static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>
    /// Whether a change sits inside something Unity itself ignores — judged on the path RELATIVE to
    /// the watched root.
    ///
    /// <para><b>Relative is the whole point.</b> This used to split the ABSOLUTE path, so any
    /// directory <i>above</i> the project root could silently switch live watching off for the
    /// entire project: a project in <c>D:\Build\Game</c>, or anywhere under a folder called
    /// <c>Temp</c>, <c>bin</c> or <c>Library</c>, matched the exclusion list on a segment the user
    /// never chose and that had nothing to do with their project.</para>
    ///
    /// <para>The failure was invisible by construction. <see cref="ProjectSweeper"/> stays
    /// authoritative, so correctness held and only freshness quietly degraded to the sweep interval
    /// — there was nothing to notice. It surfaced because every live-watch test failed on Windows,
    /// where <c>Path.GetTempPath()</c> is <c>%LOCALAPPDATA%\Temp</c> and so every fixture sat
    /// beneath a segment named "Temp". Re-running them with TMP pointed elsewhere turned all four
    /// green in under a second instead of timing out.</para>
    ///
    /// <para>Rules come from <see cref="ProjectWalker.IsExcludedDirectory"/> rather than being
    /// restated here, because the two copies had already drifted — this one was case-sensitive.</para>
    /// </summary>
    static bool IsIgnored(object sender, FileSystemEventArgs e)
    {
        // e.Name is already relative to the watched root, which is exactly the scope wanted. It is
        // documented nullable, so derive it rather than failing open on the rare null.
        var relative = e.Name;
        if (string.IsNullOrEmpty(relative))
        {
            if (sender is not FileSystemWatcher watcher) return false;
            relative = Path.GetRelativePath(watcher.Path, e.FullPath);
        }

        foreach (var segment in relative.Split(PathSeparators))
        {
            if (segment.Length == 0) continue;

            // Applied to the file's own name as well as its directories, which is what the previous
            // implementation did: a change to `.DS_Store` or `Foo.cs~` should not trigger a sweep.
            if (ProjectWalker.IsExcludedDirectory(segment)) return true;
        }

        return false;
    }

    /// <summary>Restarts the quiet period. A Unity import writes hundreds of files, and one sweep
    /// after it settles is worth far more than one per file.</summary>
    void Nudge()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounceTimer ??= new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    void Fire()
    {
        lock (_gate) { if (_disposed) return; }
        ChangesSettled?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
        _debounceTimer?.Dispose();
        _debounceTimer = null;
    }
}
