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

    /// <summary>I5: per-file diagnostics from this sweep and the indexing it drove — an
    /// unreadable directory (I10), or a file <see cref="Indexing.ScriptIndexer"/> / <see
    /// cref="Indexing.AssetIndexer"/> could not read or parse. Empty, never null, when nothing
    /// went wrong. <see cref="ProjectService.SyncChanges"/> populates the indexing half; this
    /// record only ever sets the sweep half itself.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

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
    /// a graph event, so sweeping it would produce work with nothing to do. The seven YAML/script
    /// extensions (mirroring <see cref="Indexing.AssetIndexer"/>'s own private list — ".anim"
    /// included, DESIGN-07 §11: plain Unity YAML, not binary) plus <see
    /// cref="ImportedAssetKind.Extensions"/> — binary/imported assets are a graph event too (a new
    /// texture, deleted audio clip, or renamed shader must be picked up by the same incremental
    /// path as everything else), sourced from that single shared list rather than duplicated here
    /// so this and <see cref="Indexing.BinaryAssetIndexer"/> cannot drift.</summary>
    static readonly string[] IndexableExtensions =
        [".cs", ".unity", ".prefab", ".asset", ".mat", ".controller", ".anim", .. ImportedAssetKind.Extensions];

    /// <summary>DESIGN-07 §7.3 (I4) — the extensions whose SIBLING .meta is also stat'd, so an
    /// import-setting edit (Inspector) or an out-of-band script write (agent) is visible to
    /// incremental indexing even though the asset's OWN (mtime, size) never changed. Exactly
    /// <see cref="ImportedAssetKind.Extensions"/> plus ".cs" — the scope §7.3 measured and
    /// settled: binary kinds need it for THIS feature's own Tier-1 correctness, ".cs" closes a
    /// separate, pre-existing null-GUID race (ScriptIndexer reads a script's GUID from its
    /// sibling .meta; an agent-written .cs can be swept before Unity mints one). YAML asset
    /// extensions (.unity/.prefab/.asset/.mat/.controller/.anim) are deliberately OUT — VERIFIED
    /// (probe, Unity 6000.3.2f1): AssetDatabase.CreateAsset mints the sibling .meta synchronously,
    /// before the call returns, so an Editor-created YAML asset never exists in a meta-less state
    /// for this to matter, and the extra stat would buy nothing.</summary>
    static readonly string[] MetaTrackedExtensions = [".cs", .. ImportedAssetKind.Extensions];

    public static SweepResult Sweep(string projectRoot, GraphDatabase database)
    {
        var stopwatch = Stopwatch.StartNew();
        var recorded = database.AllFileState();
        var onDisk = new Dictionary<string, FileState>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var unreadablePrefixes = new List<string>();

        // Walk exactly as the indexers do — same roots, same pruning, same local "file:" package
        // resolution. Any divergence here would read as spurious additions or deletions.
        foreach (var root in ProjectWalker.ResolveScanRoots(projectRoot, warnings))
        {
            var failedDirectories = new List<string>();

            foreach (var file in ProjectWalker.EnumerateSourceFiles(root.AbsolutePath, "*", failedDirectories))
            {
                if (!IndexableExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                var relativePath = ProjectWalker.ToRecordedPath(root, file);

                try
                {
                    var info = new FileInfo(file);
                    var (metaMtime, metaSize) = StatSiblingMeta(file);
                    onDisk[relativePath] = new FileState
                    {
                        Path = relativePath,
                        MTimeUtcMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                        Size = info.Length,
                        MetaMTimeUtcMs = metaMtime,
                        MetaSize = metaSize,
                    };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Unreadable now does not mean deleted. Carry the previous state forward so
                    // the file is not swept away for being briefly locked.
                    if (recorded.TryGetValue(relativePath, out var previous)) onDisk[relativePath] = previous;
                }
            }

            // I10: a directory this walk could not even list is not evidence anything under it
            // was deleted - it may be a permissions hiccup, a not-yet-synced network/cloud mount,
            // or a transient lock. Reported once per sweep (not per file) and excluded from the
            // Deleted computation below, so its previously recorded state survives untouched.
            foreach (var directory in failedDirectories)
            {
                var prefix = ProjectWalker.ToRecordedPath(root, directory);
                unreadablePrefixes.Add(prefix);
                warnings.Add($"{prefix}: directory could not be read this sweep; previously recorded state preserved.");
            }
        }

        var added = new List<string>();
        var changed = new List<string>();

        foreach (var (path, current) in onDisk)
        {
            if (!recorded.TryGetValue(path, out var previous)) { added.Add(path); continue; }

            // DESIGN-07 §7.3 (I4): a .meta-only edit (an Inspector import-setting change, or
            // Unity minting one for a script an agent wrote out-of-band) changes NEITHER the
            // asset's own mtime nor its size, so it was previously invisible here entirely. The
            // extra two comparisons are both null==null (never computed, on either side) for a
            // path outside MetaTrackedExtensions' scope, so this is a no-op there.
            if (previous.MTimeUtcMs != current.MTimeUtcMs || previous.Size != current.Size
                || previous.MetaMTimeUtcMs != current.MetaMTimeUtcMs || previous.MetaSize != current.MetaSize)
            {
                changed.Add(path);
            }
        }

        var deleted = recorded.Keys
            .Where(p => !onDisk.ContainsKey(p))
            .Where(p => !IsUnderAnyPrefix(p, unreadablePrefixes))
            .ToList();

        return new SweepResult
        {
            Added = added,
            Changed = changed,
            Deleted = deleted,
            FilesExamined = onDisk.Count,
            Duration = stopwatch.Elapsed,
            Warnings = warnings,
        };
    }

    static bool IsUnderAnyPrefix(string path, IReadOnlyList<string> prefixes) =>
        prefixes.Any(prefix => path.Equals(prefix, StringComparison.Ordinal)
            || path.StartsWith(prefix + "/", StringComparison.Ordinal));

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

                var (metaMtime, metaSize) = StatSiblingMeta(absolute);
                results.Add(new FileState
                {
                    Path = relativePath,
                    MTimeUtcMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    Size = info.Length,
                    MetaMTimeUtcMs = metaMtime,
                    MetaSize = metaSize,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip: no recorded state means the next sweep treats it as added and retries.
            }
        }

        return results;
    }

    /// <summary>DESIGN-07 §7.3 — stats the sibling ".meta" for a path within
    /// <see cref="MetaTrackedExtensions"/>'s scope, null for everything else (including a path IN
    /// scope with no .meta yet — a normal state for a file just added outside the Editor, not an
    /// error). One extra <see cref="FileInfo"/> stat, not a read of the .meta's content — the
    /// content is read only later, by whichever indexer actually re-indexes a file this flags as
    /// Changed.</summary>
    static (long? MTimeUtcMs, long? Size) StatSiblingMeta(string absoluteAssetPath)
    {
        if (!MetaTrackedExtensions.Contains(Path.GetExtension(absoluteAssetPath), StringComparer.OrdinalIgnoreCase))
            return (null, null);

        var metaInfo = new FileInfo(absoluteAssetPath + ".meta");
        if (!metaInfo.Exists) return (null, null);

        return (new DateTimeOffset(metaInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds(), metaInfo.Length);
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
