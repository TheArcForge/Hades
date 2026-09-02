using Hades.Core;
using Hades.Core.Observation;
using Hades.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Hades.Core.Tests.Observation;

public class ObservationServiceTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    const string Guid = "aaaabbbbccccddddeeeeffff00001111";

    void Write(string relative, string body)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
    }

    ProjectService MakeProject()
    {
        Write("ProjectSettings/ProjectSettings.asset", $"  productGUID: {Guid}\n");
        Write("Assets/Alpha.cs", "public class Alpha { }");
        var service = new ProjectService(new AppPaths(_appRoot));
        service.AdoptAndIndex(_projectRoot);
        return service;
    }

    /// <summary>Same fixture as <see cref="MakeProject"/>, minus the AdoptAndIndex call — for F14
    /// tests that need to control exactly when (relative to ObservationService.Start()) the
    /// project becomes known. Assets/ is created (empty) up front, same as every real Unity
    /// project already has by the time anything adopts it: ProjectWatcher only watches scan roots
    /// that exist AT CONSTRUCTION time (see its own constructor), so a fixture that instead
    /// created Assets/ for the first time via a later Write() would be testing that unrelated
    /// directory-must-pre-exist behaviour, not F14's watcher-enrollment fix.</summary>
    ProjectService MakeUnadoptedProject()
    {
        Write("ProjectSettings/ProjectSettings.asset", $"  productGUID: {Guid}\n");
        Directory.CreateDirectory(Path.Combine(_projectRoot, "Assets"));
        return new ProjectService(new AppPaths(_appRoot));
    }

    /// <summary>
    /// Waits for the service's OWN "I synced this project" signal, rather than polling the graph
    /// until the answer appears.
    ///
    /// <para><b>Why, concretely.</b> Every test here used to wait by calling
    /// <c>service.Search(...)</c> every 100 ms for up to 8 s — up to eighty SQLite opens per waiting
    /// test, five such tests in this class, all while the other test assemblies run in parallel. A
    /// watcher test was therefore generating a large share of the very contention that made it miss
    /// its deadline. Measured before this change: across six consecutive full-solution runs, four
    /// failed and this class's <see cref="ALiveChangeIsIndexedWithoutARestart"/> was in all four —
    /// yet it passed 5 of 5 in isolation at ~400 ms, and its own assembly passed 882/882 alone. The
    /// captured TRX showed it exhausting the full 8 s ceiling, not failing an assertion.</para>
    ///
    /// <para>Waiting on the event costs NOTHING while it waits: no database, no timer, no polling.
    /// The ceiling is generous because a ceiling on an event wait is free when the test passes — it
    /// returns the instant the signal arrives — and only spends time on a genuine failure.</para>
    ///
    /// <para><b>Subscribe BEFORE causing the change.</b> The signal is not replayed, so a handler
    /// attached after the sync has already run waits for something that has already happened. Every
    /// caller here takes this task first and only then writes the file.</para>
    /// </summary>
    static Task<bool> SyncOf(ObservationService observation, string productGuid, int timeoutMs = 30_000)
    {
        var signalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnSynced(string guid, SweepResult sweep)
        {
            if (guid == productGuid) signalled.TrySetResult(true);
        }

        observation.ProjectSynced += OnSynced;

        return Task.WhenAny(signalled.Task, Task.Delay(timeoutMs))
            .ContinueWith(finished =>
            {
                observation.ProjectSynced -= OnSynced;
                return finished.Result == signalled.Task;
            }, TaskScheduler.Default);
    }

    [Fact]
    public async Task ALiveChangeIsIndexedWithoutARestart()
    {
        var service = MakeProject();
        using var observation = new ObservationService(service) { Debounce = TimeSpan.FromMilliseconds(150) };
        observation.Start();

        // Subscribed before the write, so the signal cannot be missed - see SyncOf.
        var synced = SyncOf(observation, Guid);
        Write("Assets/Added.cs", "public class AddedWhileRunning { }");

        Assert.True(await synced, "the watcher never reported syncing a file created while running");

        // Asserted separately from the wait: if the sync fires but the node is absent, that is a
        // real indexing defect and must not read as "the watcher was slow".
        Assert.Single(service.Search(Guid, "AddedWhileRunning"));
    }

    [Fact]
    public void AChangeMadeWhileStoppedIsCaughtOnStart()
    {
        // The offline case, and the entire reason a sweep exists rather than only a watcher.
        var service = MakeProject();
        Write("Assets/Offline.cs", "public class AddedWhileStopped { }");

        using var observation = new ObservationService(service);
        observation.Start();

        Assert.Single(service.Search(Guid, "AddedWhileStopped"));
    }

    [Fact]
    public async Task ADeletionIsPickedUpLive()
    {
        var service = MakeProject();
        using var observation = new ObservationService(service) { Debounce = TimeSpan.FromMilliseconds(150) };
        observation.Start();

        var synced = SyncOf(observation, Guid);
        File.Delete(Path.Combine(_projectRoot, "Assets/Alpha.cs"));

        Assert.True(await synced, "the watcher never reported syncing a deletion");
        Assert.Empty(service.Search(Guid, "Alpha"));
    }

    // ---------------------------------------------------------------- F14: watcher enrollment
    //
    // Start() only ever enrolled a ProjectWatcher for projects KnownProjects() already listed at
    // that exact moment. A project registered afterwards — exactly what the control API's
    // POST /control/projects/add and RootsRouter's per-tool-call Adopt both do at runtime, the
    // ordinary "add a project while Hades is already running" flow — never got one: no add-project
    // call path invoked Watch(). Live writes to that project were invisible until the next
    // 5-minute periodic sweep (or a manual hades_rebuild_graph), while graph_query/find_references_to
    // answered confidently and wrongly in the meantime — F14's "graph inverts the truth with no
    // staleness signal". These three tests pin the repro and the fix.

    [Fact]
    public async Task AProjectAdoptedAfterStartIsWatchedLiveNotJustOnTheNextPeriodicSweep()
    {
        var service = MakeUnadoptedProject();
        using var observation = new ObservationService(service) { Debounce = TimeSpan.FromMilliseconds(150) };
        observation.Start();

        // Registered AFTER Start() already ran — Start()'s own KnownProjects() loop never saw it.
        service.AdoptAndIndex(_projectRoot);

        var synced = SyncOf(observation, Guid);
        Write("Assets/AddedAfterAdopt.cs", "public class AddedAfterAdopt { }");

        Assert.True(await synced,
            "a project adopted after Start() never got a live watcher — F14");
        Assert.Single(service.Search(Guid, "AddedAfterAdopt"));
    }

    [Fact]
    public async Task RemovingAProjectStopsItsLiveWatcher()
    {
        var service = MakeProject();
        using var observation = new ObservationService(service) { Debounce = TimeSpan.FromMilliseconds(150) };
        observation.Start();

        // Prove the watcher is live before removing anything, so a false negative below can only
        // mean "unwatched on remove", never "was never watched at all".
        var synced = SyncOf(observation, Guid);
        Write("Assets/BeforeRemove.cs", "public class BeforeRemove { }");
        Assert.True(await synced, "setup failure: the watcher was not live before RemoveProject");
        Assert.Single(service.Search(Guid, "BeforeRemove"));

        service.RemoveProject(Guid);
        Write("Assets/AfterRemove.cs", "public class AfterRemove { }");
        await Task.Delay(400);

        Assert.Empty(service.Search(Guid, "AfterRemove"));
    }

    [Fact]
    public void PeriodicSweepCoversAProjectAddedAfterStart()
    {
        // Defense in depth, independent of watcher enrollment: SyncAll — exactly what the
        // periodic Timer invokes — re-reads KnownProjects() fresh on every call rather than a
        // snapshot taken at Start(), so it must already cover a project added later. Verifies this
        // stays true regardless of the watcher-enrollment fix above.
        var service = MakeUnadoptedProject();
        using var observation = new ObservationService(service);
        observation.Start();

        service.AdoptAndIndex(_projectRoot);
        Write("Assets/CaughtByPeriodicSweep.cs", "public class CaughtByPeriodicSweep { }");

        observation.SyncAll();

        Assert.Single(service.Search(Guid, "CaughtByPeriodicSweep"));
    }

    [Fact]
    public void AnUnchangedProjectRaisesNothing()
    {
        var service = MakeProject();
        using var observation = new ObservationService(service);
        var synced = 0;
        observation.ProjectSynced += (_, _) => Interlocked.Increment(ref synced);

        observation.Start();

        Assert.Equal(0, synced);
    }

    [Fact]
    public void ASyncFailureOutsideTheNarrowIoFilterIsSwallowedNotFatal()
    {
        // GraphDatabase.Open throws InvalidOperationException (WAL refused) or SqliteException
        // (SQLITE_BUSY) for a locked/refusing database - neither is an IOException or
        // UnauthorizedAccessException, so the pre-fix catch filter let it straight through, and
        // on a Timer/watcher background thread that is an unhandled exception -> process death
        // for every project, not just this one. Replacing the already-created graph.db with a
        // directory reproduces an exception in that same uncaught family (SqliteException,
        // "unable to open database file") deterministically, without depending on a real
        // file-locking race. ClearAllPools is required, not decorative: Microsoft.Data.Sqlite
        // pools native connections by connection string, so without it the next Open() would
        // silently hand back a pooled handle from MakeProject's own earlier, successful open of
        // this exact path instead of genuinely re-opening the now-corrupted file.
        var service = MakeProject();
        var dbPath = new AppPaths(_appRoot).GraphDb(Guid);
        File.Delete(dbPath);
        Directory.CreateDirectory(dbPath);
        SqliteConnection.ClearAllPools();

        using var observation = new ObservationService(service);

        var ex = Record.Exception(() => observation.Sync(Guid));

        Assert.Null(ex);
    }

    [Fact]
    public void SyncsFinallyReleaseSurvivesADisposeThatRacedInDuringProjectSynced()
    {
        // The production face of the known teardown race: Dispose() (main thread, or another
        // background callback) disposes _indexGate while a Sync() call on a Timer/watcher thread
        // is still between _indexGate.Wait() and its own finally. Firing Dispose() synchronously
        // from inside the ProjectSynced handler reproduces the identical ordering deterministically
        // - by the time Invoke() returns and Sync() falls through to `finally { _indexGate.Release(); }`,
        // _indexGate has already been disposed, exactly as a genuinely racing Dispose() on another
        // thread would leave it - with no dependence on real thread timing.
        var service = MakeProject();
        var observation = new ObservationService(service);
        Write("Assets/TriggersASync.cs", "public class TriggersASync { }");

        observation.ProjectSynced += (_, _) => observation.Dispose();

        var ex = Record.Exception(() => observation.Sync(Guid));

        Assert.Null(ex);
    }

    [Fact]
    public void SyncAfterDisposeReturnsQuietlyInsteadOfThrowingObjectDisposed()
    {
        // The acquire-side twin of SyncsFinallyReleaseSurvivesADisposeThatRacedInDuringProjectSynced
        // above: if Dispose() -> _indexGate.Dispose() runs BEFORE a call reaches _indexGate.Wait(),
        // Wait() itself throws ObjectDisposedException - and that line used to sit outside Sync's
        // own try/catch, so the exception propagated straight out of Sync. On a Timer/watcher
        // thread, an unhandled exception is not "this sync failed", it is a process crash - the
        // exact failure mode Sync's catch block exists to prevent. Disposing first and then
        // calling Sync reproduces that ordering deterministically, with no dependence on real
        // thread timing.
        var service = MakeProject();
        var observation = new ObservationService(service);
        observation.Dispose();

        var ex = Record.Exception(() => observation.Sync(Guid));

        Assert.Null(ex);
    }

    [Fact]
    public void DisposeStopsWatchingAndIsSafeTwice()
    {
        var service = MakeProject();
        var observation = new ObservationService(service) { Debounce = TimeSpan.FromMilliseconds(100) };
        observation.Start();

        observation.Dispose();
        observation.Dispose();

        Write("Assets/AfterDispose.cs", "public class AfterDispose { }");
        Thread.Sleep(400);

        Assert.Empty(service.Search(Guid, "AfterDispose"));
    }

    [Fact]
    public void WatchesExternalLocalPackagesToo()
    {
        var external = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(external, "Runtime"));
        File.WriteAllText(Path.Combine(external, "Runtime", "Ext.cs"), "public class Ext { }");
        Write("Packages/manifest.json", ManifestJson.WithLocalPackage("com.example.pkg", external));

        var service = MakeProject();
        using var watcher = new ProjectWatcher(_projectRoot);

        // Assets + Packages + the external package root.
        Assert.True(watcher.WatchedRootCount >= 3, $"only watching {watcher.WatchedRootCount} roots");

        Directory.Delete(external, recursive: true);
    }

    /// <summary>
    /// A project whose PARENT directory is named like something Unity ignores must still be watched
    /// live. ProjectWatcher used to test the whole absolute path against the exclusion list, so a
    /// project in D:\Build\Game — or any project under a folder called Temp, bin or Library — had
    /// live watching silently switched off by a directory the user never chose and that was not part
    /// of the project at all.
    ///
    /// The bug hid because <see cref="ProjectSweeper"/> is authoritative: correctness never broke,
    /// only freshness, and it degraded quietly to the sweep interval with nothing to observe.
    ///
    /// "Build" is chosen deliberately over "Temp": it is on the exclusion list but appears in no
    /// OS temp path, so this test fails against the old code on macOS and Linux too rather than
    /// being an accident of Windows putting every fixture under %LOCALAPPDATA%\Temp.
    /// </summary>
    [Fact]
    public async Task AProjectUnderADirectoryNamedLikeAnIgnoredOneIsStillWatchedLive()
    {
        var container = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var awkwardRoot = Path.Combine(container, "Build", "Game");
        Directory.CreateDirectory(Path.Combine(awkwardRoot, "Assets"));
        Directory.CreateDirectory(Path.Combine(awkwardRoot, "ProjectSettings"));
        File.WriteAllText(Path.Combine(awkwardRoot, "ProjectSettings", "ProjectSettings.asset"),
            $"  productGUID: {Guid}\n");
        File.WriteAllText(Path.Combine(awkwardRoot, "Assets", "Alpha.cs"), "public class Alpha { }");

        try
        {
            var service = new ProjectService(new AppPaths(_appRoot));
            service.AdoptAndIndex(awkwardRoot);

            using var observation = new ObservationService(service) { Debounce = TimeSpan.FromMilliseconds(150) };
            observation.Start();

            var synced = SyncOf(observation, Guid);
            File.WriteAllText(Path.Combine(awkwardRoot, "Assets", "Added.cs"),
                "public class AddedUnderAnIgnoredParent { }");

            Assert.True(await synced,
                "a project under a directory named 'Build' never got live watching — the exclusion "
                + "list was applied to the absolute path instead of the project-relative one");
            Assert.Single(service.Search(Guid, "AddedUnderAnIgnoredParent"));
        }
        finally
        {
            if (Directory.Exists(container)) Directory.Delete(container, recursive: true);
        }
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
