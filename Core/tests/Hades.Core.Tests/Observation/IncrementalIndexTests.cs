using Hades.Core;
using Hades.Core.Graph;
using Hades.Core.Observation;
using Hades.Core.Storage;

namespace Hades.Core.Tests.Observation;

public class IncrementalIndexTests : IDisposable
{
    readonly string _appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly string _projectRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    const string Guid = "aaaabbbbccccddddeeeeffff00001111";

    ProjectService NewService() => new(new AppPaths(_appRoot));

    void Write(string relative, string body)
    {
        var full = Path.Combine(_projectRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
    }

    void MakeProject()
    {
        Write("ProjectSettings/ProjectSettings.asset", $"  productGUID: {Guid}\n");
        Write("Assets/Alpha.cs", "public class Alpha { }");
        Write("Assets/Beta.cs", "public class Beta { }");
        Write("Assets/Gamma.cs", "public class Gamma { }");
    }

    GraphDatabase OpenGraph() => GraphDatabase.Open(new AppPaths(_appRoot).GraphDb(Guid));

    [Fact]
    public void SyncIndexesOnlyWhatChanged()
    {
        MakeProject();
        var service = NewService();
        service.AdoptAndIndex(_projectRoot);

        Write("Assets/Beta.cs", "public class BetaRenamed { }");
        var sweep = service.SyncChanges(Guid)!;

        Assert.Single(sweep.Changed);
        Assert.Empty(sweep.Added);
        Assert.Empty(sweep.Deleted);
        Assert.Single(service.Search(Guid, "BetaRenamed"));
        Assert.Empty(service.Search(Guid, "class Beta"));
    }

    [Fact]
    public void IndexingOneFileLeavesEveryOtherFilesNodesIntact()
    {
        // The guard against the mistake that took the graph to zero nodes in plan 2: a sweep
        // scoped to a partial batch deletes everything outside it.
        MakeProject();
        var service = NewService();
        service.AdoptAndIndex(_projectRoot);

        Write("Assets/Beta.cs", "public class BetaRenamed { }");
        service.SyncChanges(Guid);

        Assert.Single(service.Search(Guid, "Alpha"));
        Assert.Single(service.Search(Guid, "Gamma"));
    }

    [Fact]
    public void ADeletedFilesNodesDisappear()
    {
        MakeProject();
        var service = NewService();
        service.AdoptAndIndex(_projectRoot);

        File.Delete(Path.Combine(_projectRoot, "Assets/Gamma.cs"));
        var sweep = service.SyncChanges(Guid)!;

        Assert.Single(sweep.Deleted);
        Assert.Empty(service.Search(Guid, "Gamma"));
        Assert.Single(service.Search(Guid, "Alpha"));
    }

    [Fact]
    public void AnAddedFileIsPickedUp()
    {
        MakeProject();
        var service = NewService();
        service.AdoptAndIndex(_projectRoot);

        Write("Assets/Delta.cs", "public class Delta { }");
        var sweep = service.SyncChanges(Guid)!;

        Assert.Single(sweep.Added);
        Assert.Single(service.Search(Guid, "Delta"));
    }

    [Fact]
    public void AnUnchangedProjectSyncsToNothing()
    {
        MakeProject();
        var service = NewService();
        service.AdoptAndIndex(_projectRoot);

        var sweep = service.SyncChanges(Guid)!;

        Assert.False(sweep.AnythingChanged);
    }

    [Fact]
    public void IncrementalAndFullReindexConverge()
    {
        // The test that matters most. A fast path that drifts from the correct path is worse than
        // no fast path — so after a batch of mutations, incremental must land on exactly the same
        // graph a full reindex would produce.
        MakeProject();
        Write("Assets/Scene.unity",
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1 &1\nGameObject:\n  m_Name: Root\n");
        Write("Assets/Scene.unity.meta", "fileFormatVersion: 2\nguid: bbbb2222bbbb2222bbbb2222bbbb2222\n");

        var service = NewService();
        service.AdoptAndIndex(_projectRoot);

        Write("Assets/Alpha.cs", "public class AlphaChanged { }\npublic class AlphaExtra { }");
        File.Delete(Path.Combine(_projectRoot, "Assets/Beta.cs"));
        Write("Assets/Epsilon.cs", "public interface IEpsilon { }");
        Write("Assets/Scene.unity",
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1 &1\nGameObject:\n  m_Name: Renamed\n");

        service.SyncChanges(Guid);

        int incrementalNodes, incrementalEdges;
        using (var db = OpenGraph()) { incrementalNodes = db.TotalNodes(); incrementalEdges = db.TotalEdges(); }

        // A fresh, full index of the same tree into a separate database.
        var freshAppRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            new ProjectService(new AppPaths(freshAppRoot)).AdoptAndIndex(_projectRoot);
            using var fresh = GraphDatabase.Open(new AppPaths(freshAppRoot).GraphDb(Guid));

            Assert.Equal(fresh.TotalNodes(), incrementalNodes);
            Assert.Equal(fresh.TotalEdges(), incrementalEdges);
        }
        finally
        {
            if (Directory.Exists(freshAppRoot)) Directory.Delete(freshAppRoot, recursive: true);
        }
    }

    [Fact]
    public void FilesInLocalPackagesGetRecordedStateAndDoNotResweepForever()
    {
        // The generic in-project "Packages" root is a textual prefix of a local package's
        // "Packages/<id>" root. Resolving a recorded path by FIRST matching prefix sent every
        // package file to <project>/Packages/... — which does not exist — so state was silently
        // skipped and every sweep re-reported them as added. Measured: 138 of 182 real files.
        var external = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(external, "Runtime"));
        File.WriteAllText(Path.Combine(external, "Runtime", "Packaged.cs"), "public class Packaged { }");

        MakeProject();
        Write("Packages/manifest.json", $"{{\"dependencies\":{{\"com.example.pkg\":\"file:{external}\"}}}}");

        var service = NewService();
        service.AdoptAndIndex(_projectRoot);
        Assert.Single(service.Search(Guid, "Packaged"));

        // Second sync must be a no-op. Before the fix it reported the package's files as added,
        // every single time, forever.
        var sweep = service.SyncChanges(Guid)!;

        Assert.False(sweep.AnythingChanged,
            $"resweep reported +{sweep.Added.Count} ~{sweep.Changed.Count} -{sweep.Deleted.Count}");

        Directory.Delete(external, recursive: true);
    }

    // ---------------------------------------------------------------- I10: unreadable directory
    //
    // The sweep used to treat a directory it could not even list as "everything under it was
    // deleted" — Deleted is computed purely from "recorded but not seen this walk", and a
    // directory read failure meant nothing under it was ever seen. An unreadable directory (a
    // permissions hiccup, a not-yet-synced mount, ...) must instead be skipped with a warning,
    // its previously recorded state left exactly alone.

    // CA1416: File.SetUnixFileMode is (obviously) POSIX-only. Hades only ever runs on macOS/Unix
    // (see AppPaths and every other Unix-assuming path in this codebase) and this test exists
    // specifically to simulate a Unix permissions failure, so the platform-compatibility warning
    // has nothing to protect here — suppressed for exactly the two call sites that need it.
#pragma warning disable CA1416
    // File.SetUnixFileMode is a POSIX chmod equivalent and throws PlatformNotSupportedException on
    // Windows. This test exists specifically to simulate a Unix permissions failure, so it is
    // gated by trait rather than early-returned - see PlatformTraits for why traits, not skips.
    [Fact, Trait(PlatformTraits.Key, PlatformTraits.Unix)]
    public void AnUnreadableDirectory_PreservesItsRecordedState_InsteadOfReportingDeletions()
    {
        MakeProject();
        Write("Assets/Locked/Hidden.cs", "public class Hidden { }");
        var service = NewService();
        service.AdoptAndIndex(_projectRoot);
        Assert.Single(service.Search(Guid, "Hidden"));

        var lockedDir = Path.Combine(_projectRoot, "Assets", "Locked");
        File.SetUnixFileMode(lockedDir, UnixFileMode.None);
        try
        {
            var sweep = service.SyncChanges(Guid)!;

            Assert.DoesNotContain("Assets/Locked/Hidden.cs", sweep.Deleted);
            Assert.NotEmpty(sweep.Warnings);
            Assert.Single(service.Search(Guid, "Hidden"));
        }
        finally
        {
            // Restore before Dispose()'s recursive delete runs — chmod itself needs no
            // permission on lockedDir (ownership is what matters), but deleting its contents
            // afterward does.
            File.SetUnixFileMode(lockedDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
#pragma warning restore CA1416

    // I4 (.meta-only changes invisible to the sweep) was investigated but NOT fixed this round:
    // the natural fix — folding a .meta's own mtime into its owning asset's tracked mtime — was
    // implemented, proven to correctly detect a guid-only edit, and then REVERTED after it broke
    // Hades.Server.Tests.SummaryToolTests.GetRecentlyChanged_SortsNewestFirstAndHonoursSince:
    // file_state.mtime_utc is also the sort key get_recently_changed exposes to callers, and
    // folding in an unrelated .meta timestamp can pull an asset's reported "last changed" time
    // forward independent of its own content ever changing. A correct fix needs the .meta's own
    // mtime tracked separately from file_state (e.g. its own table), which is a GraphSchema/
    // GraphDatabase change outside this pass's file ownership. See the final report for detail.

    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
