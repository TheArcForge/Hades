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

                // F22: DeleteNodesAndEdgesForPath, never the file-state-clearing
                // DeleteNodesForPath — this file is being re-indexed, not retired, and its
                // file_state row must survive so an unchanged file's next sweep still sees it as
                // recorded (see DeleteNodesAndEdgesForPath's own doc comment for the mechanism).
                database.DeleteNodesAndEdgesForPath(relativePath);
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

    /// <param name="progress">
    /// Optional, and reported per file. Null for every caller that does not show a person what is
    /// happening - the incremental sweep, tests, the MCP tools - so nothing pays for a channel it
    /// has no use for.
    /// </param>
    public static IndexResult IndexProject(
        string projectRoot, GraphDatabase database, IProgress<IndexProgressUpdate>? progress = null)
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

        var roots = ProjectWalker.ResolveScanRoots(projectRoot, warnings).ToList();
        var totalFiles = 0;

        // Materialised ONCE per root when someone is watching, and then reused as the walk below.
        //
        // A separate counting pass is the obvious way to learn the total and it is a trap: it walks
        // every directory twice, which measured at 12.1s against 6.4s for the same 1,774-script
        // project - progress that costs double the index it reports on. A caller passing no progress
        // still gets the original lazy enumeration and allocates nothing extra.
        List<(List<string> Files, List<string> Failed)>? prewalked = null;

        if (progress is not null)
        {
            prewalked = [];
            foreach (var root in roots)
            {
                var failed = new List<string>();
                var files = ProjectWalker.EnumerateSourceFiles(root.AbsolutePath, "*.cs", failed).ToList();

                prewalked.Add((files, failed));
                totalFiles += files.Count;
            }

            progress.Report(new IndexProgressUpdate("Scripts", 0, totalFiles));
        }

        for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
        {
            var root = roots[rootIndex];
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var failedDirectories = prewalked is null ? [] : prewalked[rootIndex].Failed;

            var walk = prewalked is null
                ? ProjectWalker.EnumerateSourceFiles(root.AbsolutePath, "*.cs", failedDirectories)
                : prewalked[rootIndex].Files;

            foreach (var file in walk)
            {
                filesScanned++;

                // Every 25 files: a report per file would be thousands of updates for a number the
                // eye cannot follow, and each one crosses a lock in the operation registry.
                if (progress is not null && filesScanned % 25 == 0)
                {
                    progress.Report(new IndexProgressUpdate("Scripts", filesScanned, totalFiles));
                }
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
                    // disappear from the graph, which an upsert alone would never do. F22:
                    // DeleteNodesAndEdgesForPath, never DeleteNodesForPath — this file may well be
                    // UNCHANGED since the last index (a full rebuild visits every file regardless),
                    // and only DeleteNodesAndEdgesForPath leaves an unchanged file's file_state row
                    // alone rather than silently erasing it with nothing to restore it (see that
                    // method's own doc comment for the mechanism this once broke).
                    database.DeleteNodesAndEdgesForPath(relativePath);
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

            // I10: a directory this walk could not even read is not evidence anything under it
            // was deleted — reserved from the sweep below exactly like an unresolvable package's
            // prefix already is (same parameter, same reasoning), and named in a warning rather
            // than silently wiping whatever was previously recorded for it.
            var reserved = unreachablePackagePrefixes;
            if (failedDirectories.Count > 0)
            {
                var unreadablePrefixes = failedDirectories.Select(dir => ProjectWalker.ToRecordedPath(root, dir)).ToList();
                reserved = [.. unreachablePackagePrefixes, .. unreadablePrefixes];
                foreach (var prefix in unreadablePrefixes)
                    warnings.Add($"{prefix}: directory could not be read this rebuild; previously recorded state preserved.");
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
            database.SweepStaleNodes(root.PathPrefix, visited, reserved, [".cs"]);
        }

        // The final figure, so the last thing anyone sees is the whole count rather than whatever
        // the every-25th report happened to land on.
        progress?.Report(new IndexProgressUpdate("Scripts", filesScanned, totalFiles));

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
