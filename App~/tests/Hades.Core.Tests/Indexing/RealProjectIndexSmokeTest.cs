using Hades.Core.Graph;
using Hades.Core.Indexing;

namespace Hades.Core.Tests.Indexing;

public class RealProjectIndexSmokeTest
{
    const string RealProject = "/Users/mike/Projects/Hades-Unity-Client";

    [Fact]
    public void IndexesTheRealUnityProject()
    {
        // Plain guard rather than a skip package: this is a local sanity check, and adding
        // a dependency to express "not on this machine" is not worth it.
        if (!Directory.Exists(RealProject)) return;

        var dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "graph.db");
        using var db = GraphDatabase.Open(dbPath);

        var result = ScriptIndexer.IndexProject(RealProject, db);

        // Measured 2026-08-01: 198 files, 287 types, 0.4s, 0 warnings. Accounted for exactly:
        //   16 Assets/ + 107 Editor/ + 65 Tests/ + 10 ThirdParty/ in the file: package.
        // Two floors, guarding opposite regressions:
        //   below ~150 → local-package resolution broke (Assets/ alone yields 16)
        //   above ~230 → directory pruning broke (unpruned yields 263, including this
        //                solution's own App~/ sources and generated build artifacts)
        Assert.True(result.FilesScanned > 150, $"Only scanned {result.FilesScanned} files");
        Assert.True(result.TypesFound > 150, $"Only found {result.TypesFound} types");
        Assert.True(result.FilesScanned < 230,
            $"Scanned {result.FilesScanned} files — pruning likely regressed (App~/, obj/, bin/)");

        // Recorded for Task 14's verification note.
        Console.WriteLine($"files={result.FilesScanned} types={result.TypesFound} " +
                          $"duration={result.Duration.TotalSeconds:F1}s warnings={result.Warnings.Count}");

        Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
    }
}
