using System.Text.Json;
using Hades.Core.Storage;

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

    enum ReadOutcome { Missing, Ok, Corrupt, Unreadable }

    public UnityProject? Adopt(string projectRoot)
    {
        var guid = ProjectIdentity.TryReadProductGuid(projectRoot);
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
            Path = projectRoot,
            Name = Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar)),
            UnityVersion = existing?.UnityVersion,
            FirstSeen = existing?.FirstSeen ?? now,
            LastSeen = now,
        };

        Save(project);
        return project;
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
