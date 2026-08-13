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

    /// <summary>Polls rather than sleeping a fixed period — a timing-sensitive test that passes
    /// on a fast machine and fails in CI is worse than no test.</summary>
    static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }

        return condition();
    }

    [Fact]
    public async Task ALiveChangeIsIndexedWithoutARestart()
    {
        var service = MakeProject();
        using var observation = new ObservationService(service) { Debounce = TimeSpan.FromMilliseconds(150) };
        observation.Start();

        Write("Assets/Added.cs", "public class AddedWhileRunning { }");

        Assert.True(await Eventually(() => service.Search(Guid, "AddedWhileRunning").Count == 1),
            "the watcher never picked up a file created while running");
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

        File.Delete(Path.Combine(_projectRoot, "Assets/Alpha.cs"));

        Assert.True(await Eventually(() => service.Search(Guid, "Alpha").Count == 0),
            "a deleted file's nodes survived");
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
        Write("Assets/AddedAfterAdopt.cs", "public class AddedAfterAdopt { }");

        Assert.True(await Eventually(() => service.Search(Guid, "AddedAfterAdopt").Count == 1),
            "a project adopted after Start() never got a live watcher — F14");
    }

    [Fact]
    public async Task RemovingAProjectStopsItsLiveWatcher()
    {
        var service = MakeProject();
        using var observation = new ObservationService(service) { Debounce = TimeSpan.FromMilliseconds(150) };
        observation.Start();

        // Prove the watcher is live before removing anything, so a false negative below can only
        // mean "unwatched on remove", never "was never watched at all".
        Write("Assets/BeforeRemove.cs", "public class BeforeRemove { }");
        Assert.True(await Eventually(() => service.Search(Guid, "BeforeRemove").Count == 1),
            "setup failure: the watcher was not live before RemoveProject");

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
        Write("Packages/manifest.json", $"{{\"dependencies\":{{\"com.example.pkg\":\"file:{external}\"}}}}");

        var service = MakeProject();
        using var watcher = new ProjectWatcher(_projectRoot);

        // Assets + Packages + the external package root.
        Assert.True(watcher.WatchedRootCount >= 3, $"only watching {watcher.WatchedRootCount} roots");

        Directory.Delete(external, recursive: true);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
