using System.Diagnostics;
using Hades.Core.Graph;
using Hades.Core.Indexing;
using Hades.Core.Unity;

namespace Hades.Core.Observation;

public sealed record SweepResult
{
    public required IReadOnlyList<string> Added { get; init; }
    public required IReadOnlyList<string> Changed { get; init; }
    public required IReadOnlyList<string> Deleted { get; init; }
    public required int FilesExamined { get; init; }
    public required TimeSpan Duration { get; init; }

    public bool AnythingChanged => Added.Count > 0 || Changed.Count > 0 || Deleted.Count > 0;

    /// <summary>Files needing a (re)index — added and changed together, which is what callers want.</summary>
    public IReadOnlyList<string> NeedsIndexing => [.. Added, .. Changed];
}

/// <summary>
/// Compares what was recorded at index time against what is on disk now.
///
/// This is the source of truth for freshness, not the file watcher. A watcher only shortens the
/// delay between a change and the graph reflecting it; if an event is ever missed the next sweep
/// repairs it. Measured cost on a real project: ~193 ms across 8,056 files, which is why this is
/// affordable as the primary mechanism rather than a fallback.
/// </summary>
public static class ProjectSweeper
{
    /// <summary>Extensions the indexers actually handle. A file outside this set changing is not
    /// a graph event, so sweeping it would produce work with nothing to do. The six YAML/script
    /// extensions plus <see cref="ImportedAssetKind.Extensions"/> — binary/imported assets are a
    /// graph event too (a new texture, deleted audio clip, or renamed shader must be picked up by
    /// the same incremental path as everything else), sourced from that single shared list rather
    /// than duplicated here so this and <see cref="Indexing.BinaryAssetIndexer"/> cannot drift.</summary>
    static readonly string[] IndexableExtensions =
        [".cs", ".unity", ".prefab", ".asset", ".mat", ".controller", .. ImportedAssetKind.Extensions];

    public static SweepResult Sweep(string projectRoot, GraphDatabase database)
    {
        var stopwatch = Stopwatch.StartNew();
        var recorded = database.AllFileState();
        var onDisk = new Dictionary<string, FileState>(StringComparer.Ordinal);
        var warnings = new List<string>();

        // Walk exactly as the indexers do — same roots, same pruning, same local "file:" package
        // resolution. Any divergence here would read as spurious additions or deletions.
        foreach (var root in ProjectWalker.ResolveScanRoots(projectRoot, warnings))
        {
            foreach (var file in ProjectWalker.EnumerateSourceFiles(root.AbsolutePath, "*"))
            {
                if (!IndexableExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                var relativePath = ProjectWalker.ToRecordedPath(root, file);

                try
                {
                    var info = new FileInfo(file);
                    onDisk[relativePath] = new FileState
                    {
                        Path = relativePath,
                        MTimeUtcMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                        Size = info.Length,
                    };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Unreadable now does not mean deleted. Carry the previous state forward so
                    // the file is not swept away for being briefly locked.
                    if (recorded.TryGetValue(relativePath, out var previous)) onDisk[relativePath] = previous;
                }
            }
        }

        var added = new List<string>();
        var changed = new List<string>();

        foreach (var (path, current) in onDisk)
        {
            if (!recorded.TryGetValue(path, out var previous)) { added.Add(path); continue; }
            if (previous.MTimeUtcMs != current.MTimeUtcMs || previous.Size != current.Size) changed.Add(path);
        }

        var deleted = recorded.Keys.Where(p => !onDisk.ContainsKey(p)).ToList();

        return new SweepResult
        {
            Added = added,
            Changed = changed,
            Deleted = deleted,
            FilesExamined = onDisk.Count,
            Duration = stopwatch.Elapsed,
        };
    }

    /// <summary>The on-disk state of specific files, for recording after they are indexed.</summary>
    public static IReadOnlyList<FileState> StateFor(string projectRoot, IEnumerable<string> relativePaths)
    {
        var results = new List<FileState>();
        var warnings = new List<string>();
        var roots = ProjectWalker.ResolveScanRoots(projectRoot, warnings);

        foreach (var relativePath in relativePaths)
        {
            if (ToAbsolute(roots, relativePath) is not { } absolute) continue;

            try
            {
                var info = new FileInfo(absolute);
                if (!info.Exists) continue;

                results.Add(new FileState
                {
                    Path = relativePath,
                    MTimeUtcMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    Size = info.Length,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip: no recorded state means the next sweep treats it as added and retries.
            }
        }

        return results;
    }

    /// <summary>Maps a recorded path back to disk. Local "file:" packages are recorded under
    /// "Packages/&lt;id&gt;/…" but live elsewhere entirely, so this cannot just join to the root.</summary>
    public static string? ToAbsolute(IReadOnlyList<ScanRoot> roots, string relativePath)
    {
        // LONGEST prefix wins, not the first match. Prefixes nest: the in-project "Packages" root
        // is a textual prefix of a local package's "Packages/com.example.thing" root, so taking
        // the first match resolves a package file to <project>/Packages/... — a path that does not
        // exist, because the package lives outside the project entirely. That silently skipped
        // 138 of 182 files on a real project: they never got recorded state, so every sweep
        // re-reported them as added and nothing was ever incremental.
        ScanRoot? best = null;

        foreach (var root in roots)
        {
            var prefix = root.PathPrefix + "/";
            if (!relativePath.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (best is null || root.PathPrefix.Length > best.Value.PathPrefix.Length) best = root;
        }

        if (best is null) return null;

        var relative = relativePath[(best.Value.PathPrefix.Length + 1)..];
        return Path.Combine(best.Value.AbsolutePath, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
