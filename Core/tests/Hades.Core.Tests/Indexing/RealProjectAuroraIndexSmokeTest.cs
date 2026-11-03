using Hades.Core.Graph;

namespace Hades.Core.Tests.Indexing;

/// <summary>
/// Plan 15 Task 3: real-project regression guard for the conditional-compilation fix, against the
/// exact corpus (project_aurora) the defect was found and fixed against. Same guarded,
/// skip-if-absent pattern as the sibling Hades-Unity-Client smoke test
/// (<see cref="RealProjectIndexSmokeTest"/>) in this directory — a local sanity check, not
/// something CI can depend on having this checkout. Read-only: only ever opens a throwaway temp
/// graph.db, never writes anything under project_aurora itself.
/// </summary>
public class RealProjectAuroraIndexSmokeTest
{
    const string RealProject = "/Users/mike/Projects/project_aurora";

    const string MathematicsDrawersPath =
        "Assets/Plugins/Sirenix/Odin Inspector/Modules/Unity.Mathematics/MathematicsDrawers.cs";

    [Fact]
    public void IndexesTheRealProjectAndFindsPreviouslyInvisibleEditorOnlyTypes()
    {
        if (!Directory.Exists(RealProject)) return;

        var dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "graph.db");
        using var db = GraphDatabase.Open(dbPath);

        var scriptResult = Hades.Core.Indexing.ScriptIndexer.IndexProject(RealProject, db);
        Hades.Core.Indexing.AssetIndexer.IndexProject(RealProject, db);

        // This count comes entirely from ProjectWalker's file enumeration, which Plan 15 Task 3
        // never touches — a regression here would mean file DISCOVERY broke, not conditional
        // compilation, and this guards that boundary explicitly.
        //
        // Two floors rather than an exact count, for the same reason RealProjectIndexSmokeTest
        // carries them: project_aurora is a live project outside this repo, so an exact pin
        // reports every file its developer adds as a Hades regression. It did. The original
        // 1,744 (1,406 Assets/*.cs + 338 Packages/com.arongranberg.astar/*.cs, verified
        // 2026-08-07 against docs/backlog/graph-correctness-defects.md) read 1,780 on
        // 2026-11-03 — 1,442 + the same 338 — a grown corpus, not a broken walker.
        //
        // Deriving the expected number from a `find` instead was considered and rejected: it
        // would restate ProjectWalker's own pruning rules (dot/tilde directory names, obj, bin,
        // Library, ...) in a second place, which IsExcludedDirectory's doc comment warns against
        // by name after exactly that duplication drifted once already. The bounds keep the
        // guarantee that matters here without a second definition of "project source":
        //   below 1,500 → a root stopped being walked (losing the embedded astar package alone
        //                 lands at 1,442) or enumeration broke outright
        //   above 2,600 → directory pruning regressed and swept in Library/PackageCache, obj/,
        //                 or bin/, which would add thousands rather than dozens; generous
        //                 headroom above today's 1,780 for ordinary growth
        Assert.True(scriptResult.FilesScanned > 1500,
            $"Only scanned {scriptResult.FilesScanned} files — a scan root or the walker likely regressed");
        Assert.True(scriptResult.FilesScanned < 2600,
            $"Scanned {scriptResult.FilesScanned} files — pruning likely regressed (Library/, obj/, bin/)");

        // The defect: MathematicsDrawers.cs wraps 64 real type declarations in one
        // #if UNITY_EDITOR / #endif pair (verified directly against the file: exactly one #if,
        // one #endif, 64 declarations). Before this fix, ParseText had no CSharpParseOptions at
        // all, so Roslyn evaluated the #if as false and none of the 64 ever reached the graph.
        var declarations = db.QueryGraph(kind: null, namePattern: null, pathPrefix: MathematicsDrawersPath,
            edgeKind: null, edgeDirection: "outgoing", limit: 200);
        Assert.Equal(64, declarations.Count);
        Assert.Contains(declarations, d => d.Name == "MatrixFloat2x2Processor");

        // Regression check: the 12/12 hand-verified reference counts from Plan 15 Task 2's
        // validation must be exactly unchanged by this fix — a project-wide define union must
        // not fabricate new edges, only reveal previously-invisible NODES.
        AssertExactlyTwelveReferences(db, "Assets/Scripts/View/Mono/Position2DView.cs");
        AssertExactlyTwelveReferences(db, "Assets/Scripts/View/Mono/EntityViewLink.cs");

        Console.WriteLine($"[project_aurora] files={scriptResult.FilesScanned} " +
                          $"mathematicsDrawersDeclarations={declarations.Count} " +
                          $"duration={scriptResult.Duration.TotalSeconds:F2}s");

        Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
    }

    static void AssertExactlyTwelveReferences(GraphDatabase db, string assetPath)
    {
        var guid = db.GuidForPath(assetPath);
        Assert.NotNull(guid);
        Assert.Equal(12, db.CountReferencesTo(guid!, assetPath));
        Assert.Equal(12, db.CountReferencingFiles(guid!, assetPath));
    }
}
