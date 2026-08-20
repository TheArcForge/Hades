using System.Diagnostics;
using Hades.Core.Graph;
using Hades.Core.Unity;

namespace Hades.Core.Indexing;

/// <summary>
/// Indexes binary/imported assets — textures, models, audio, fonts, shaders, and animation clips
/// (see <see cref="ImportedAssetKind"/> for the exact extension-to-kind mapping) — as meta-only
/// graph nodes: path, name (the filename stem), kind (from the extension), and guid (from the
/// sibling .meta, via <see cref="MetaFileReader"/>). No content is ever read, and no edges are
/// ever written from one of these files — see this type's own "why a separate type" paragraph
/// below for why splitting content-reading from stub-emission this way matters.
///
/// The point of a node here is not what it says about itself (nothing — these files are opaque),
/// but what it makes RESOLVABLE. A material, prefab, or renderer asset that references one of
/// these by GUID has always produced a real `references` edge — <see cref="AssetIndexer"/> reads
/// the REFERENCING file's own YAML, same as any other reference, regardless of what the target
/// turns out to be. Before this type existed, that edge's target GUID owned no node anywhere in
/// the graph, so <see cref="GraphDatabase.TraceDependencies"/> and find_references_to could only
/// report it dangling or absent. Indexing the target is the entire fix: zero new parsing, because
/// the .meta already carries the GUID, the filename already carries the name, and the extension
/// already carries the kind.
///
/// Kept as its own type rather than a branch inside <see cref="AssetIndexer"/>'s own IndexAsset:
/// that method reads a file's FULL content as UTF8 text (see its own <c>File.ReadAllText</c>)
/// before <see cref="Unity.UnityYamlPreprocessor.LooksLikeUnityYaml"/> can even reject it — exactly
/// the multi-megabyte binary read this type exists to avoid, and a second, unrelated concern
/// (stub emission vs. full YAML object-graph parsing) that doesn't belong in one method. Wired in
/// by <see cref="AssetIndexer.IndexFiles"/> and <see cref="AssetIndexer.IndexProject"/> calling
/// straight into this type's own IndexFiles/IndexProject and merging the two <see cref="IndexResult"/>s,
/// rather than by a third call added at each of ProjectService's own call sites — every existing
/// entry point (full reindex, incremental sync) therefore picks up binary assets with no call-site
/// changes at all.
/// </summary>
public static class BinaryAssetIndexer
{
    /// <summary>
    /// Indexes ONLY the named files. As with <see cref="ScriptIndexer.IndexFiles"/> and
    /// <see cref="AssetIndexer.IndexFiles"/>, this must not sweep — a sweep scoped to a partial
    /// batch would delete every node outside it.
    /// </summary>
    public static IndexResult IndexFiles(string projectRoot, GraphDatabase database,
        IReadOnlyList<string> relativePaths)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var roots = ProjectWalker.ResolveScanRoots(projectRoot, warnings);
        var filesScanned = 0;
        var nodesFound = 0;

        foreach (var relativePath in relativePaths)
        {
            if (ImportedAssetKind.KindForPath(relativePath) is not { } kind) continue;
            if (Observation.ProjectSweeper.ToAbsolute(roots, relativePath) is not { } absolute) continue;
            if (!File.Exists(absolute)) continue;

            filesScanned++;

            try
            {
                IndexAsset(absolute, relativePath, kind, database);
                nodesFound++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{relativePath}: {ex.Message}");
            }
        }

        return new IndexResult
        {
            FilesScanned = filesScanned,
            TypesFound = nodesFound,
            Duration = stopwatch.Elapsed,
            Warnings = warnings,
        };
    }

    public static IndexResult IndexProject(string projectRoot, GraphDatabase database)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var filesScanned = 0;
        var nodesFound = 0;

        var unreachablePackagePrefixes = ProjectWalker.UnreachablePackagePrefixes(projectRoot);

        foreach (var root in ProjectWalker.ResolveScanRoots(projectRoot, warnings))
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var failedDirectories = new List<string>();

            foreach (var file in ProjectWalker.EnumerateSourceFiles(root.AbsolutePath, "*", failedDirectories))
            {
                if (ImportedAssetKind.KindForPath(file) is not { } kind) continue;

                filesScanned++;
                var relativePath = ProjectWalker.ToRecordedPath(root, file);

                // Recorded even when the write below fails: a file that exists but could not be
                // read was not deleted, so its previous node (if any) must survive the sweep.
                visited.Add(relativePath);

                try
                {
                    IndexAsset(file, relativePath, kind, database);
                    nodesFound++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"{relativePath}: {ex.Message}");
                }
            }

            // I10: a directory this walk could not even read is not evidence anything under it
            // was deleted — reserved from the sweep below exactly like an unresolvable package's
            // prefix already is (see ScriptIndexer/AssetIndexer's identical handling), and named
            // in a warning rather than silently wiping whatever was previously recorded for it.
            var reserved = unreachablePackagePrefixes;
            if (failedDirectories.Count > 0)
            {
                var unreadablePrefixes = failedDirectories.Select(dir => ProjectWalker.ToRecordedPath(root, dir)).ToList();
                reserved = [.. unreachablePackagePrefixes, .. unreadablePrefixes];
                foreach (var prefix in unreadablePrefixes)
                    warnings.Add($"{prefix}: directory could not be read this rebuild; previously recorded state preserved.");
            }

            // Scoped to exactly the extensions this indexer owns, same as ScriptIndexer and
            // AssetIndexer each scope their own sweep — three indexers now share one graph and
            // one path-prefix space, and without this a full reindex by any one of them would
            // delete the other two's nodes entirely (see GraphDatabase.SweepStaleNodes's own
            // ownedExtensions doc comment).
            database.SweepStaleNodes(root.PathPrefix, visited, reserved, ImportedAssetKind.Extensions);
        }

        return new IndexResult
        {
            FilesScanned = filesScanned,
            TypesFound = nodesFound,
            Duration = stopwatch.Elapsed,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// One node, no edges. Delete-then-insert, same discipline as every other indexer: a file
    /// re-imported under a different extension (rare, but not impossible) or that lost its .meta
    /// must not leave a stale node behind under its old identity.
    /// </summary>
    static void IndexAsset(string absolutePath, string relativePath, string kind, GraphDatabase database)
    {
        // The ENTIRE read: a GUID lookup against the sibling .meta, never the asset's own bytes.
        var guid = MetaFileReader.TryReadGuid(absolutePath);
        var name = Path.GetFileNameWithoutExtension(relativePath);

        // F22: DeleteNodesAndEdgesForPath, never the file-state-clearing DeleteNodesForPath —
        // this file is being re-indexed, not retired (see that method's own doc comment for why
        // conflating the two silently emptied file_state on every repeated full rebuild).
        database.DeleteNodesAndEdgesForPath(relativePath);
        database.UpsertNodes([new GraphNode { Kind = kind, Name = name, Path = relativePath, Guid = guid }]);
    }
}
