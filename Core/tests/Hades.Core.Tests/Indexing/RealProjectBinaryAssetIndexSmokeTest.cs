using Hades.Core.Graph;
using Hades.Core.Indexing;

namespace Hades.Core.Tests.Indexing;

/// <summary>
/// Ground-truth guard, against the real Hades-Unity-Client corpus rather than a synthetic
/// fixture, for binary/imported assets becoming graph nodes: same directory-exists-guard pattern
/// as the sibling <see cref="RealProjectIndexSmokeTest"/>, read-only (only ever opens a throwaway
/// temp graph.db).
///
/// Deliberately asserts on COUNTS only, never on a specific asset's name or path — this project's
/// contents are not to be treated as a public fixture (see this repo's own standing rule against
/// naming tester-project identifiers). What matters for this feature is structural: that indexing
/// the real corpus produces exactly the binary node this corpus's own Assets/ is independently
/// known to contain, and that its GUID is now reachable from a real referencing file — the actual
/// capability this feature exists to deliver, proven without printing what either file is.
///
/// Scoped to Assets/ specifically, not the whole scanned corpus, even though the two now
/// coincide: until this same commit deleted this repo's own v1.2 tree and, with it,
/// Hades-Unity-Client's Packages/manifest.json "file:" dependency on this repo (see
/// ReleasePipeline.md §7), this project also had Hades itself installed as a local package
/// (Packages/com.arcforge.hades), which carried its own documentation image outside Assets/
/// entirely — a second, legitimately-scanned texture (see
/// AssetIndexerTests.IndexesAssetsInsideLocalFilePackages for the same local-package scan-root
/// behaviour proven on a synthetic fixture). That was a real, correct hit for this feature, not a
/// bug, but it made "exactly one texture in the whole corpus" untrue in general — while "exactly
/// one texture under Assets/" was, and remains, a stable, independently-verified fact about this
/// project's own authored content specifically. Keeping the assertion scoped there (rather than
/// broadening it now that the two counts match) keeps it correct regardless of whether a future
/// local package reappears.
/// </summary>
public class RealProjectBinaryAssetIndexSmokeTest
{
    const string RealProject = "/Users/mike/Projects/Hades-Unity-Client";

    [Fact]
    public void IndexingTheRealProjectProducesExactlyOneResolvableTextureNodeUnderAssets()
    {
        if (!Directory.Exists(RealProject)) return;

        var dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "graph.db");
        using var db = GraphDatabase.Open(dbPath);

        // The exact production call path ProjectService.Reindex uses — AssetIndexer.IndexProject
        // internally also runs BinaryAssetIndexer, so this exercises the real wiring, not just
        // the binary indexer standalone.
        var result = AssetIndexer.IndexProject(RealProject, db);

        // Measured 2026-08-13: exactly one texture under Assets/ specifically (see this type's
        // own class doc comment for why the assertion is scoped there rather than to the whole
        // corpus). A regression in either direction (0 → the wiring broke; >1 → sweep/dedup broke)
        // should fail loudly.
        var underAssets = db.SearchByName("", kind: "Texture2D")
            .Where(n => n.Path.StartsWith("Assets/", StringComparison.Ordinal))
            .ToList();
        var texture = Assert.Single(underAssets);
        Assert.NotNull(texture.Guid);

        // The payoff: something in the project already references this texture by GUID (a
        // `references` edge AssetIndexer wrote from the referencing file's own YAML, long before
        // this feature existed) — before binary nodes existed, that edge's target owned no node,
        // so this would have been unreachable. CountReferencingFiles > 0 proves the resolution
        // without this test ever needing to name either file.
        var referencingFiles = db.CountReferencingFiles(texture.Guid!, excludePath: null);
        Assert.True(referencingFiles > 0,
            "the corpus's one Assets/ texture has no resolvable referencer — expected at least " +
            "one (a `references` edge to it is known to exist in this corpus)");

        var totalTextures = db.CountByKind().GetValueOrDefault("Texture2D");
        Console.WriteLine($"[Hades-Unity-Client] filesScanned={result.FilesScanned} " +
                          $"typesFound={result.TypesFound} textureNodesUnderAssets={underAssets.Count} " +
                          $"textureNodesTotal={totalTextures} referencingFiles={referencingFiles} " +
                          $"duration={result.Duration.TotalSeconds:F2}s");

        Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
    }
}
