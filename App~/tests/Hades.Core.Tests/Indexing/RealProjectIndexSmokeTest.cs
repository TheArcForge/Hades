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

        // Re-baselined 2026-08-18, the same commit that deleted this repo's own v1.2 tree
        // (Editor/, Tests/, ThirdParty/, Fixtures~/, package.json — see ReleasePipeline.md §7)
        // and, with it, removed Hades-Unity-Client's Packages/manifest.json "file:" dependency
        // on this repo (com.arcforge.hades). The old 198-file total was 16 Assets/ + 107 Editor/
        // + 65 Tests/ + 10 ThirdParty/ pulled in through that dependency; with it gone, this scan
        // is Assets/-only (Packages/ contributes no source files today). Measured just now: 45
        // files, 64 types, 0.2s, 0 warnings — the same Assets/ tree the old test's own comment
        // already called out as the local-package-broken floor ("Assets/ alone yields 16",
        // measured 2026-08-01), grown to 45 in the weeks since as the fixture project gained
        // content.
        // Two floors, guarding opposite regressions, re-baselined against today's Assets/-only
        // number rather than the old package-inclusive one:
        //   below 25 → scanning broke outright (Assets/ resolution, or the walker itself)
        //   above 120 → directory pruning broke (Library/, obj/, bin/ swept in instead of
        //               excluded) — generous headroom above 45 since this fixture project's
        //               Assets/ keeps growing and is no longer offset by a fixed package count
        Assert.True(result.FilesScanned > 25, $"Only scanned {result.FilesScanned} files");
        Assert.True(result.TypesFound > 40, $"Only found {result.TypesFound} types");
        Assert.True(result.FilesScanned < 120,
            $"Scanned {result.FilesScanned} files — pruning likely regressed (Library/, obj/, bin/)");

        // Recorded for Task 14's verification note.
        Console.WriteLine($"files={result.FilesScanned} types={result.TypesFound} " +
                          $"duration={result.Duration.TotalSeconds:F1}s warnings={result.Warnings.Count}");

        Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
    }
}
