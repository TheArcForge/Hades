using System.Diagnostics;
using Hades.Core.Graph;
using Hades.Core.Projects;
using Hades.Core.Scanning;
using Hades.Core.Unity;

namespace Hades.Core.Indexing;

/// <summary>Walks a Unity project's C# and writes the result into the graph.</summary>
public static class ScriptIndexer
{
    /// <summary>
    /// Indexes ONLY the named files. Deliberately does not sweep for stale nodes: SweepStaleNodes
    /// exists to find files that vanished during a FULL walk, and its visited-set here would be
    /// just this batch — so it would delete every node belonging to every file not in it. That
    /// exact mistake took the graph to zero nodes once already. Deletions are handled by the
    /// caller, which knows precisely which files went away.
    /// </summary>
    public static IndexResult IndexFiles(string projectRoot, GraphDatabase database,
        IReadOnlyList<string> relativePaths)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();
        var roots = ProjectWalker.ResolveScanRoots(projectRoot, warnings);
        var filesScanned = 0;
        var typesFound = 0;

        // Resolved once per call, not per file — ProjectVersion.txt/ProjectSettings.asset are
        // small, project-level facts, not the source corpus being walked. See ProjectDefines'
        // own class doc comment for what this set contains and the per-assembly-union caveat it
        // carries.
        var defines = ProjectDefines.Resolve(projectRoot).Symbols;

        foreach (var relativePath in relativePaths)
        {
            if (!relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (Observation.ProjectSweeper.ToAbsolute(roots, relativePath) is not { } absolute) continue;
            if (!File.Exists(absolute)) continue;

            filesScanned++;

            try
            {
                var types = RoslynScriptScanner.ScanFile(relativePath, absolute, defines);
                var scriptGuid = Unity.MetaFileReader.TryReadGuid(absolute);

                database.DeleteNodesForPath(relativePath);
                database.UpsertNodes(types.Select(t => ToNode(t, scriptGuid)).ToList());
                typesFound += database.CountNodesForPath(relativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{relativePath}: {ex.Message}");
            }
        }

        return new IndexResult
        {
            FilesScanned = filesScanned,
            TypesFound = typesFound,
            Duration = stopwatch.Elapsed,
            Warnings = warnings,
        };
    }

    public static IndexResult IndexProject(string projectRoot, GraphDatabase database)
    {
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var filesScanned = 0;
        var typesFound = 0;

        // Prefixes of packages no swept root will ever reach this run — see
        // UnreachablePackagePrefixes for exactly which ones and why.
        var unreachablePackagePrefixes = ProjectWalker.UnreachablePackagePrefixes(projectRoot);

        // Resolved once per call, not per file — see IndexFiles' own identical comment.
        var defines = ProjectDefines.Resolve(projectRoot).Symbols;

        foreach (var root in ProjectWalker.ResolveScanRoots(projectRoot, warnings))
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in ProjectWalker.EnumerateSourceFiles(root.AbsolutePath, "*.cs"))
            {
                filesScanned++;
                var relativePath = ProjectWalker.ToRecordedPath(root, file);

                // Recorded even if the scan below fails: a file that exists but could not be
                // read was not deleted, so its previous nodes (if any) must survive the sweep.
                visited.Add(relativePath);

                try
                {
                    var types = RoslynScriptScanner.ScanFile(relativePath, file, defines);

                    // A .cs file is a Unity asset like any other, and its .meta GUID is what
                    // every MonoBehaviour's m_Script actually points at. Without it, script
                    // nodes are unreachable from the reference graph — "what uses this script",
                    // the most valuable query a Unity developer asks, cannot resolve at all.
                    var scriptGuid = MetaFileReader.TryReadGuid(file);

                    // Delete-then-insert per file: a type removed from the source must
                    // disappear from the graph, which an upsert alone would never do.
                    database.DeleteNodesForPath(relativePath);
                    database.UpsertNodes(types.Select(t => ToNode(t, scriptGuid)).ToList());

                    // Counts rows actually recorded, not types parsed: two declarations that
                    // collide onto one node identity must not inflate this past what the graph
                    // actually holds — see GraphSchema's node-identity comment for why that can
                    // legitimately happen (e.g. a duplicate declaration in the same namespace).
                    typesFound += database.CountNodesForPath(relativePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"{relativePath}: {ex.Message}");
                }
            }

            // A file deleted or renamed since the last index was never visited above, so
            // delete-then-insert alone would leave its nodes behind forever. Scoped to this
            // root's prefix and called once per root actually resolved — a root that failed to
            // resolve (warned above, never reaches this loop) keeps its prior nodes untouched
            // rather than having them read as "every file in this package was deleted".
            // Unreachable packages' prefixes are reserved so the generic "Packages" root's
            // sweep cannot reach into a namespace nothing this run actually walked. A package
            // embedded INSIDE the project is deliberately excluded from that reserved set — the
            // generic "Packages" walk covers it directly and is the only thing that ever will.
            database.SweepStaleNodes(root.PathPrefix, visited, unreachablePackagePrefixes, [".cs"]);
        }

        return new IndexResult
        {
            FilesScanned = filesScanned,
            TypesFound = typesFound,
            Duration = stopwatch.Elapsed,
            Warnings = warnings,
        };
    }

    static GraphNode ToNode(ScriptType type, string? scriptGuid) => new()
    {
        Kind = type.Kind,
        Name = type.Name,
        Path = type.Path,
        Namespace = type.Namespace,
        Line = type.Line,
        Guid = scriptGuid,
    };

}
