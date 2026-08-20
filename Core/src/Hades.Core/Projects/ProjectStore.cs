using System.Runtime.CompilerServices;
using System.Text.Json;
using Hades.Core.Storage;

// No dedicated AssemblyInfo.cs in this project, so this lives on the one method it exists for:
// lets Hades.Server.Mcp.RootsRouter delegate to Canonicalize below (internal, not public - see
// that method's own doc comment for the invariant this sharing exists to protect) instead of
// keeping its own second copy. Still a one-way dependency - Hades.Server already references
// Hades.Core (see ProjectResolver's own doc comment); this only lets it see one more member.
[assembly: InternalsVisibleTo("Hades.Server")]

namespace Hades.Core.Projects;

/// <summary>
/// The registry of known Unity projects, persisted one project.json per productGUID.
/// Adopting a project that already exists updates its path rather than creating a duplicate —
/// that is what makes a moved or re-cloned project keep its graph.
/// </summary>
public sealed class ProjectStore(AppPaths paths)
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Quarantine filenames tried, in order, before giving up: project.json.corrupt,
    /// then .corrupt.1, .corrupt.2, ... A second corruption must never destroy evidence of the
    /// first, so quarantining never overwrites an existing quarantine file.</summary>
    const int MaxQuarantineSlots = 5;

    /// <summary>Total symlink substitutions <see cref="Canonicalize"/> permits across one walk -
    /// comfortably above any legitimate chain and small enough to fail fast (falling back to the
    /// lexically-normalized input - see that method's own doc comment) on a cycle, e.g. a link
    /// pointing at itself or at one of its own ancestors.</summary>
    const int MaxLinkResolutions = 40;

    enum ReadOutcome { Missing, Ok, Corrupt, Unreadable }

    public UnityProject? Adopt(string projectRoot)
    {
        // Canonicalized BEFORE anything else, so both the guid read below and the Path this
        // project is stored under agree on ONE spelling regardless of how the caller spelled it
        // this time - see Canonicalize's own doc comment for why a raw, verbatim projectRoot must
        // never reach either. Name is deliberately the ONE exception - see its own assignment
        // below for why it comes from the caller's original spelling instead.
        var canonicalRoot = Canonicalize(projectRoot);

        var guid = ProjectIdentity.TryReadProductGuid(canonicalRoot);
        if (guid is null) return null;

        var (outcome, existing) = ReadProjectFile(guid);

        // A transient read failure (a lock, a permission blip) is not evidence the file is
        // corrupt. Treating it as corrupt would overwrite a perfectly good record — exactly
        // the data loss the quarantine mechanism exists to prevent. Abort rather than guess.
        if (outcome == ReadOutcome.Unreadable) return null;

        // Genuine decode failure: quarantine it (unless every slot is already taken, in which
        // case abort rather than destroy the evidence that's there) and fall through to adopt
        // as if this were the project's first sighting.
        if (outcome == ReadOutcome.Corrupt && !TryQuarantine(guid)) return null;

        var now = DateTimeOffset.UtcNow;

        var project = new UnityProject
        {
            ProductGuid = guid,
            Path = canonicalRoot,
            // The ORIGINAL (lexically-normalized-only) leaf, NOT canonicalRoot's - so a project
            // opened through a symlinked alias keeps showing the name the caller actually
            // navigated to, instead of silently renaming to whatever the real directory beneath
            // it happens to be called. Only Path is canonical (see Canonicalize's own doc
            // comment); Name intentionally is not.
            Name = Path.GetFileName(Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar)),
            UnityVersion = existing?.UnityVersion,
            FirstSeen = existing?.FirstSeen ?? now,
            LastSeen = now,
        };

        Save(project);
        return project;
    }

    /// <summary>
    /// Lexically normalizes (<see cref="Path.GetFullPath(string)"/> plus trimming a trailing
    /// separator) and then resolves symlinks at EVERY path component - realpath(3) semantics, not
    /// just the leaf. <c>internal</c>, with <c>Hades.Server</c> named in this assembly's own
    /// <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/> (see this file's
    /// top), specifically so <c>Hades.Server.Mcp.RootsRouter</c>'s own Canonicalize can be a thin
    /// delegate to this exact method instead of a second copy of it - the one-way dependency this
    /// crosses is the ordinary, already-established one (Hades.Server references Hades.Core;
    /// Hades.Core must not reference Hades.Server - see <see cref="ProjectResolver"/>'s own doc
    /// comment for that same constraint), just now reaching one member further than a public API
    /// would need to.
    ///
    /// INVARIANT this sharing exists to protect: <see cref="Adopt"/> (via this method) decides the
    /// one spelling a project's stored <see cref="UnityProject.Path"/> is kept under; RootsRouter's
    /// own routing-time lookup matches a freshly reported root against that same stored Path and
    /// only succeeds when ITS canonicalization of the fresh root agrees with THIS canonicalization
    /// of the stored one, byte for byte. Two independently-maintained copies drifting apart is
    /// exactly what broke that agreement once already: RootsRouter's own Canonicalize used to be
    /// leaf-only, one component short of what this method does, and macOS's own <c>/tmp</c> -&gt;
    /// <c>/private/tmp</c> (an INTERMEDIATE component for a project at <c>/tmp/MyProj</c>, never
    /// the leaf) was enough to make an already-known project's canonical form disagree with itself
    /// depending on which copy computed it - the known project silently failed to match its own
    /// stored self, and got re-adopted and re-announced as brand new on every roots-resolution
    /// call. Delegating here instead of maintaining a second implementation is what keeps that
    /// from happening again.
    ///
    /// Walks the path component by component from the root, substituting each symlinked
    /// directory with its target (<see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> with
    /// <c>returnFinalTarget: true</c>, so a multi-hop chain AT one component collapses in a
    /// single step) before moving on to the next component. <see cref="MaxLinkResolutions"/>
    /// bounds the total substitutions across the whole walk against a symlink cycle (e.g. a link
    /// pointing at itself or at one of its own ancestors); hitting that bound - like any other
    /// resolution error - falls back to the lexically-normalized input rather than throwing,
    /// because a canonicalization failure must never take down <see cref="Adopt"/>.
    ///
    /// A component that does not exist on disk stops resolution right there and the remainder is
    /// appended verbatim rather than probed further (nothing beneath a nonexistent directory can
    /// exist either) - <see cref="Adopt"/> is about to check the result for a ProjectSettings
    /// folder regardless, so a not-yet-real (or never-real) tail is expected input, not
    /// exceptional; it reports "not a Unity project" either way once it gets there.
    ///
    /// Only the STORED PATH is canonical this way - <see cref="Adopt"/>'s <c>Name</c> deliberately
    /// stays derived from the caller's ORIGINAL (pre-canonicalization) leaf; see its own
    /// assignment for why.
    /// </summary>
    internal static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

        try
        {
            return ResolveFullChain(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    static string ResolveFullChain(string full)
    {
        var root = Path.GetPathRoot(full) ?? "";
        var segments = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var resolved = root;
        var stoppedResolving = false; // set once a component doesn't exist; nothing beneath it can either.
        var linkResolutions = 0;

        foreach (var segment in segments)
        {
            var candidate = Path.Combine(resolved, segment);

            if (stoppedResolving || !Directory.Exists(candidate))
            {
                stoppedResolving = true;
            }
            else if (new DirectoryInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName is { } target)
            {
                if (++linkResolutions > MaxLinkResolutions)
                    throw new IOException($"Too many levels of symbolic links resolving '{full}'.");

                candidate = target;
            }

            resolved = candidate;
        }

        return resolved.TrimEnd(Path.DirectorySeparatorChar);
    }

    public UnityProject? Get(string productGuid) => ReadProjectFile(productGuid).Project;

    /// <summary>
    /// Deregisters a project WITHOUT deleting anything on disk - not project.json, not the
    /// derived graph/traces databases, and certainly not memory/ (authored, irreplaceable - see
    /// <see cref="Memory.MemoryStore"/>'s own class doc comment). Implemented as a flag rewritten
    /// into project.json (<see cref="UnityProject.Removed"/>) rather than any file deletion, so
    /// Plan 11 Task 3's load-bearing invariant - "remove never deletes anything on disk" - holds
    /// even for Hades' own bookkeeping, not just the user's project.
    ///
    /// <see cref="All"/> excludes a removed project; <see cref="Get"/> deliberately does not - a
    /// caller who already holds its productGuid can still resolve it directly. Re-<see cref="Adopt"/>ing
    /// the same project always constructs a fresh <see cref="UnityProject"/> record (Removed
    /// defaults to false) and so is what makes it visible again.
    /// </summary>
    /// <returns>False when <paramref name="productGuid"/> names a project Hades has never seen at
    /// all (or whose record is unreadable) - there is nothing to deregister. Calling this on an
    /// already-removed project is idempotent: it re-saves the same Removed=true state and returns
    /// true.</returns>
    public bool Remove(string productGuid)
    {
        if (Get(productGuid) is not { } project) return false;

        Save(project with { Removed = true });
        return true;
    }

    /// <summary>
    /// Distinguishes "the file does not decode" (<see cref="JsonException"/> — genuine
    /// corruption) from "the file could not be read right now" (<see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> — a lock or permission blip, e.g. an AV
    /// scanner, a search indexer, or a root-owned file left behind by one sudo/launchd run).
    /// <see cref="Get"/> collapses both to null, which is right for <see cref="All"/>'s
    /// resilience. <see cref="Adopt"/> needs the distinction: it must never treat a file that
    /// is merely unreadable at this instant as corrupt, or it would quarantine and overwrite a
    /// perfectly good record.
    /// </summary>
    (ReadOutcome Outcome, UnityProject? Project) ReadProjectFile(string productGuid)
    {
        var file = paths.ProjectFile(productGuid);
        if (!File.Exists(file)) return (ReadOutcome.Missing, null);

        try
        {
            var project = JsonSerializer.Deserialize<UnityProject>(File.ReadAllText(file), JsonOptions);
            return (ReadOutcome.Ok, project);
        }
        catch (JsonException)
        {
            return (ReadOutcome.Corrupt, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (ReadOutcome.Unreadable, null);
        }
    }

    /// <summary>
    /// Moves a corrupt project.json aside without ever overwriting a previous quarantine file —
    /// a second corruption event must not erase evidence of the first. Returns false when every
    /// slot up to <see cref="MaxQuarantineSlots"/> is already taken, so the caller can abort
    /// rather than destroy the only remaining evidence.
    /// </summary>
    bool TryQuarantine(string productGuid)
    {
        var file = paths.ProjectFile(productGuid);

        for (var i = 0; i < MaxQuarantineSlots; i++)
        {
            var candidate = i == 0 ? file + ".corrupt" : $"{file}.corrupt.{i}";
            if (File.Exists(candidate)) continue;

            File.Move(file, candidate);
            return true;
        }

        return false;
    }

    /// <summary>Every known, active project - excludes anything <see cref="Remove"/> has
    /// deregistered (see its own doc comment). Every caller of this method (ObservationService's
    /// watch/sweep loop, the control API's project list, hades_status) wants only active
    /// projects; a removed one disappearing from all of them at once is the point.</summary>
    public IReadOnlyList<UnityProject> All()
    {
        if (!Directory.Exists(paths.ProjectsRoot)) return [];

        return Directory.EnumerateDirectories(paths.ProjectsRoot)
            .Select(dir => Get(Path.GetFileName(dir)))
            .OfType<UnityProject>()
            .Where(p => !p.Removed)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Writes via a temp file plus rename rather than directly to project.json: an
    /// interrupted direct write is exactly what leaves behind a truncated file, and a
    /// same-directory rename is atomic, so readers never observe a partial write.
    /// </summary>
    public void Save(UnityProject project)
    {
        var dir = paths.EnsureProjectDir(project.ProductGuid);
        var target = paths.ProjectFile(project.ProductGuid);
        var temp = Path.Combine(dir, $".project.json.{Guid.NewGuid():N}.tmp");

        File.WriteAllText(temp, JsonSerializer.Serialize(project, JsonOptions));
        File.Move(temp, target, overwrite: true);
    }
}
