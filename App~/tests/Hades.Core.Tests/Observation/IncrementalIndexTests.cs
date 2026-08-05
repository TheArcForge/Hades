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

    public void Dispose()
    {
        foreach (var dir in new[] { _appRoot, _projectRoot })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
