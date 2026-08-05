using Hades.Core;
using Hades.Core.Observation;
using Hades.Core.Storage;

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
