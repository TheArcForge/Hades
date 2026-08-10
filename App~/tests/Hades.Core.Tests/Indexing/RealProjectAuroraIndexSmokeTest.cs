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

        // Independently re-verified 2026-08-07 with `find`, matching docs/backlog/graph-correctness-defects.md:
        // 1,406 Assets/*.cs + 338 Packages/com.arongranberg.astar/*.cs (embedded package) = 1,744.
        // This count comes entirely from ProjectWalker's file enumeration, which Plan 15 Task 3
        // never touches — a regression here would mean file DISCOVERY broke, not conditional
        // compilation, and this guards that boundary explicitly.
        Assert.Equal(1744, scriptResult.FilesScanned);

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
