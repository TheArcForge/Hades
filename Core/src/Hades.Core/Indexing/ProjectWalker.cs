using System.Text.Json;

namespace Hades.Core.Indexing;

/// <summary>A source root to scan, and the project-relative prefix its files are recorded under.</summary>
public readonly record struct ScanRoot(string AbsolutePath, string PathPrefix);

/// <summary>
/// Resolves which directories of a Unity project to scan and enumerates the files inside them.
/// Shared by every indexer, because "what counts as project source" is one question with one
/// answer — it covers local "file:" packages that live outside the project directory, and prunes
/// the directories Unity itself ignores.
/// </summary>
public static class ProjectWalker
{
    /// <summary>In-project roots. Library/ churns constantly during import and contains
    /// resolved registry packages; Temp/, obj/, Build/, and Logs/ are noise.</summary>
    static readonly string[] InProjectRoots = ["Assets", "Packages"];

    /// <summary>
    /// The in-project roots, plus every local package declared in Packages/manifest.json with a
    /// "file:" dependency. Those live OUTSIDE the project directory but are first-class project
    /// code — a Unity project that develops a local package keeps most of its C# there, so
    /// scanning only Assets/ can miss the large majority of a real project.
    /// Registry packages resolved into Library/PackageCache are deliberately NOT scanned:
    /// they are third-party code and would swamp the graph.
    /// </summary>
    public static IReadOnlyList<ScanRoot> ResolveScanRoots(string projectRoot, List<string> warnings)
    {
        var roots = new List<ScanRoot>();

        foreach (var name in InProjectRoots)
        {
            var absolute = Path.Combine(projectRoot, name);
            if (Directory.Exists(absolute)) roots.Add(new ScanRoot(absolute, name));
        }

        foreach (var (packageId, packagePath) in ReadLocalPackages(projectRoot, warnings))
        {
            if (!Directory.Exists(packagePath))
            {
                warnings.Add($"Packages/manifest.json declares '{packageId}' at '{packagePath}', which does not exist.");
                continue;
            }

            // Already covered by the Packages/ root scan when the package lives inside the project.
            if (IsInside(projectRoot, packagePath)) continue;

            // A package that CONTAINS the project root would rescan the project itself and pull
            // in unrelated sibling directories — e.g. "file:.." or "file:<parent>".
            if (IsInside(packagePath, projectRoot))
            {
                warnings.Add($"Packages/manifest.json declares '{packageId}' at '{packagePath}', " +
                    "which contains the project root; skipping to avoid scanning unrelated sibling code.");
                continue;
            }

            // Recorded under Unity's own convention for local packages, so paths stay
            // recognisable rather than surfacing as "../../elsewhere/Editor/Foo.cs".
            roots.Add(new ScanRoot(packagePath, $"Packages/{packageId}"));
        }

        return roots;
    }

    /// <summary>
    /// Prefixes of packages that no root swept this run will ever reach: a package that failed
    /// to resolve (<see cref="ResolveScanRoots"/>'s <c>!Directory.Exists</c> branch), or one
    /// that is an ancestor of the project (its ancestor branch). Both need protecting from the
    /// generic "Packages" root's sweep, since nothing else will visit their namespace this run.
    /// A package embedded INSIDE the project (skipped at <see cref="ResolveScanRoots"/>'s
    /// "already covered" branch) is deliberately excluded — its files are physically part of
    /// the generic "Packages" walk, so that walk's own visited-set already accounts for them,
    /// and it is the ONLY thing that will ever sweep them. Reserving it too would mean nothing
    /// ever sweeps it, orphaning a deleted file's node forever — a real regression this fixed.
    /// A successfully-resolved external package needs no reservation either: in-project roots
    /// are always walked before external ones (see <see cref="InProjectRoots"/> order), so if
    /// the generic sweep runs before that package's own turn, its nodes are simply re-inserted
    /// moments later when that root scans — self-healing within this same run.
    /// </summary>
    public static IReadOnlyList<string> UnreachablePackagePrefixes(string projectRoot)
    {
        var prefixes = new List<string>();

        foreach (var (packageId, packagePath) in ReadLocalPackages(projectRoot, []))
        {
            if (!Directory.Exists(packagePath))
            {
                prefixes.Add($"Packages/{packageId}");
                continue;
            }

            if (IsInside(projectRoot, packagePath)) continue;   // embedded — do not reserve

            if (IsInside(packagePath, projectRoot)) prefixes.Add($"Packages/{packageId}");
        }

        return prefixes;
    }

    static IEnumerable<(string PackageId, string AbsolutePath)> ReadLocalPackages(
        string projectRoot, List<string> warnings)
    {
        var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
        if (!File.Exists(manifestPath)) yield break;

        JsonElement dependencies;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("dependencies", out var deps)) yield break;
            dependencies = deps.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Packages/manifest.json could not be read: {ex.Message}");
            yield break;
        }

        if (dependencies.ValueKind != JsonValueKind.Object) yield break;

        foreach (var dependency in dependencies.EnumerateObject())
        {
            if (dependency.Value.ValueKind != JsonValueKind.String) continue;

            var value = dependency.Value.GetString();
            if (value is null || !value.StartsWith("file:", StringComparison.Ordinal)) continue;

            var raw = value["file:".Length..];

            // Relative file: paths resolve against Packages/, per Unity's manifest rules.
            var absolute = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(projectRoot, "Packages", raw));

            yield return (dependency.Name, absolute);
        }
    }

    /// <summary>Build output and tooling directories, in addition to the name-shape rules below.</summary>
    static readonly string[] ExcludedDirectoryNames =
        ["obj", "bin", "Library", "Temp", "Logs", "Build", "node_modules"];

    /// <summary>
    /// Mirrors Unity's own visibility rules — Unity ignores directories whose name starts with
    /// "." or ends with "~" — plus build output. Without this, scanning an external "file:"
    /// package root descends into things Unity cannot see: measured on the real project, the
    /// Hades package root contributed 36 files from its own .NET solution under Core/, 15 of
    /// them generated (AssemblyInfo.cs, GlobalUsings.g.cs). Hades models what Unity models.
    ///
    /// <para><b>Internal, not private, because <see cref="Observation.ProjectWatcher"/> shares it.</b>
    /// It used to carry its own copy of these rules, and the two drifted: the watcher's was
    /// case-SENSITIVE, so a directory named <c>library/</c> was pruned by this walker and watched by
    /// that watcher. One definition is the fix; do not restate the list anywhere else.</para>
    /// </summary>
    internal static bool IsExcludedDirectory(string name) =>
        name.StartsWith('.')
        || name.EndsWith('~')
        || ExcludedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Recursive walk that can prune directories — <see cref="SearchOption.AllDirectories"/>
    /// cannot, and an unreadable subdirectory must skip rather than abort the whole scan.
    /// Tracks each directory's canonical identity in a visited-set so a symlink that cycles
    /// back to an ancestor is recognised as already-visited rather than expanded again —
    /// <see cref="Directory.EnumerateDirectories"/> follows symlinks, so without this the walk
    /// is exponential in branching (not depth) and effectively unbounded.
    /// </summary>
    /// <param name="failedDirectories">
    /// I10: when supplied, every directory this walk could not read (permissions, a transient
    /// lock, a not-yet-synced network/cloud mount, ...) is appended here, as the SAME path
    /// string that was passed to <see cref="Directory.GetFiles"/> — i.e. relative to whatever
    /// <paramref name="root"/> the caller is currently walking. A caller comparing recorded
    /// state against this walk's output (<see cref="Observation.ProjectSweeper.Sweep"/>) needs
    /// this to tell "genuinely deleted" apart from "could not confirm either way" - nothing
    /// yielded from under a failed directory is evidence it no longer exists.
    /// </param>
    public static IEnumerable<string> EnumerateSourceFiles(string root, string searchPattern,
        List<string>? failedDirectories = null)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;

            try
            {
                // Inside the try, not before it: CanonicalIdentity can throw for a directory
                // this process cannot access, and that must skip the directory like any other
                // I/O failure here, not abort the whole scan.
                if (!visited.Add(CanonicalIdentity(directory))) continue;

                foreach (var subdirectory in Directory.EnumerateDirectories(directory))
                {
                    if (!IsExcludedDirectory(Path.GetFileName(subdirectory)))
                        pending.Push(subdirectory);
                }

                files = Directory.GetFiles(directory, searchPattern);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // I10: reported, not just skipped — see this parameter's own doc comment for why
                // "skip" alone let a caller mistake "could not check" for "confirmed gone".
                failedDirectories?.Add(directory);
                continue;
            }

            foreach (var file in files) yield return file;
        }
    }

    /// <summary>
    /// The identity used to detect a directory already visited. A symlinked directory resolves
    /// to its real target so a cycle (e.g. a symlink pointing at an ancestor) is detected as
    /// "already visited" instead of expanded again. A directory that is not a link (the common
    /// case) resolves to null and falls back to its own normalised path. An unresolvable link
    /// (broken, or a permissions error — <see cref="UnauthorizedAccessException"/> does not
    /// derive from <see cref="IOException"/>, so both must be caught here) falls back the same
    /// way; callers still wrap this call in the same try/catch as the I/O below it, so a
    /// directory this process cannot access is skipped, not fatal to the whole scan.
    /// </summary>
    static string CanonicalIdentity(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(directory);
        }
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="root"/> itself, or strictly
    /// nested inside it. Trims trailing separators from both sides before comparing: without
    /// that, only the nested case matched, and equality — the resolved form of e.g. "file:.." —
    /// fell through as "outside", which is what let a package pointing at the project root get
    /// rescanned as a whole extra one. (Called with swapped arguments to test the reverse
    /// containment: whether a package path is an ANCESTOR of the project root.)
    /// </summary>
    static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);

        return normalizedCandidate.Equals(normalizedRoot, StringComparison.Ordinal)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }


    /// <summary>Project-relative path a file is recorded under, given the root that found it.</summary>
    public static string ToRecordedPath(ScanRoot root, string absoluteFilePath)
    {
        var relative = Path.GetRelativePath(root.AbsolutePath, absoluteFilePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        return $"{root.PathPrefix}/{relative}";
    }
}
