using Hades.Core.Memory;
using Hades.Core.Storage;

namespace Hades.Core.Tests.Memory;

public class RealProjectMemoryImportSmokeTest
{
    const string RealProject = "/Users/mike/Projects/Hades-Unity-Client";
    const string ProductGuid = "smoketestguid00000000000000000000";

    [Fact]
    public void ImportsTheRealProjectsArcforgeMemory()
    {
        // Plain guard rather than a skip package: this is a local sanity check, and adding a
        // dependency to express "not on this machine" is not worth it. See RealProjectIndexSmokeTest.
        if (!Directory.Exists(RealProject)) return;

        // Import writes only into a fresh temp app-storage root - NEVER into RealProject, which
        // is read-only input here. ImportFromArcforge itself never writes to the source either.
        var appRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            var store = new MemoryStore(new AppPaths(appRoot));
            var result = store.ImportFromArcforge(ProductGuid, RealProject);

            Console.WriteLine($"imported={result.Imported.Count} skipped={result.Skipped.Count}");
            foreach (var name in result.Imported.OrderBy(n => n, StringComparer.Ordinal))
                Console.WriteLine($"  imported: {name}");
            foreach (var skip in result.Skipped.OrderBy(s => s.Source, StringComparer.Ordinal))
                Console.WriteLine($"  skipped: {skip.Source} — {skip.Reason}");

            // The six known top-level documents must always come through.
            foreach (var known in MemoryStore.KnownDocuments)
                Assert.Contains(known, result.Imported);

            // Degenerate names import as-is rather than being rejected: automatic inference is
            // not being ported, so this only ever copies what exists. A leading-dash filename
            // (from an empty template prefix) is still a valid basename - no path separators,
            // not empty - so name validation has no reason to reject it. Its degenerate CONTENT
            // is exactly what "importing what exists" means.
            //
            // Asserted as a SHAPE, not by filename. This reads a corpus the app does not own:
            // the old Unity package rewrites .arcforge/memory/ on Editor start, and it deleted
            // proposals/20260614-174745-.md - which this test had pinned by name - between two
            // runs on 2026-08-03. A real-corpus test that hard-codes an artifact of someone's
            // live project fails on their workflow, not on a regression.
            Assert.Contains(result.Imported, name => name.StartsWith("proposals/-", StringComparison.Ordinal));

            // Nothing that is not a memory document (*.md) is imported, and none is reported as a
            // skip either - e.g. inferred/.conventions-state.json.
            Assert.DoesNotContain(result.Imported, name => !name.EndsWith(".md", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Skipped, s => !s.Source.EndsWith(".md", StringComparison.Ordinal));

            // proposals/ and inferred/ can share a basename. proposals/ wins (processed first)
            // and inferred/'s copy is reported as skipped rather than silently overwriting -
            // asserted only when such a collision actually exists in the corpus.
            var collisions = result.Skipped.Where(s => s.Source.StartsWith("inferred/", StringComparison.Ordinal)).ToList();
            Assert.All(collisions, s => Assert.Contains("already exists", s.Reason, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(appRoot)) Directory.Delete(appRoot, recursive: true);
        }

        Assert.False(Directory.Exists(Path.Combine(RealProject, ".hades")));
    }
}
