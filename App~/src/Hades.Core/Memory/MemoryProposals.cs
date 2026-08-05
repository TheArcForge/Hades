using Hades.Core.Storage;
using YamlDotNet.Serialization;

namespace Hades.Core.Memory;

/// <summary>The result of one <see cref="MemoryProposals.Write"/> call.</summary>
public sealed record MemoryProposal
{
    /// <summary>Plain basename directly inside memory/proposals/, e.g.
    /// "20260801-153000-patterns.md" - the SAME shape <see cref="MemoryProposalInfo.FileName"/>
    /// reports (see that property's own doc comment) and <see cref="MemoryProposals.Read"/>/
    /// <see cref="MemoryProposals.SetStatus"/>/<see cref="MemoryProposals.Delete"/> all validate as
    /// a basename, so a caller - an MCP client acting on <c>propose_memory_update</c>'s own
    /// returned <c>fileName</c>, or the control API's proposal-queue actions - can pass this value
    /// straight back into any of them with no stripping. Used to carry a "proposals/" prefix that
    /// every one of those basename-validated methods then rejected outright as an unsafe name -
    /// the exact "propose, then act on what you got back" break both the control API's
    /// accept/dismiss/defer actions and any MCP-side chaining of the same tool hit identically.</summary>
    public required string FileName { get; init; }
}

/// <summary>One proposal as <see cref="MemoryProposals.List"/>/<see cref="MemoryProposals.Read"/>
/// report it - the read-back counterpart of <see cref="MemoryProposal"/> (Plan 11 Task 6, needed so
/// the control API's proposal queue - spec #3 §3.4's Accept/Dismiss/Defer - has something to list
/// and act on; nothing before this read a proposal back at all, only wrote one).</summary>
public sealed record MemoryProposalInfo
{
    /// <summary>Plain basename directly inside memory/proposals/, e.g.
    /// "20260801-153000-patterns.md" - the SAME shape <see cref="MemoryProposal.FileName"/>
    /// reports (see that property's own doc comment), since this is exactly the value a caller
    /// passes back into <see cref="MemoryProposals.Read"/>/<see cref="MemoryProposals.SetStatus"/>/
    /// <see cref="MemoryProposals.Delete"/>, every one of which validates it as a basename.</summary>
    public required string FileName { get; init; }

    /// <summary>From frontmatter's <c>target_file</c> - "" when absent or unparseable (see
    /// <see cref="FrontmatterError"/>), never null, so callers never null-check the common case.</summary>
    public required string TargetFile { get; init; }

    /// <summary>Parsed from frontmatter's <c>created_at</c> - null when absent or not a parseable
    /// timestamp.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    public required string Rationale { get; init; }

    /// <summary>From frontmatter's <c>status</c> - "pending" for a freshly-written proposal (see
    /// <see cref="MemoryProposals.Write"/>'s own frontmatter), "accepted"/"deferred" once
    /// <see cref="MemoryProposals.SetStatus"/> has been called.</summary>
    public required string Status { get; init; }

    /// <summary>The proposed markdown text - <see cref="MemoryFile.Body"/>, unchanged.</summary>
    public required string Content { get; init; }

    /// <summary>Surfaced, never swallowed - same "a broken header must not make the content
    /// unreachable" stance <see cref="MemoryFile"/> itself takes.</summary>
    public string? FrontmatterError { get; init; }
}

/// <summary>
/// Writes AGENT-PROPOSED memory updates - never an authored document, always a brand-new file
/// under memory/proposals/. This is the boundary the whole memory design rests on: an agent can
/// propose, but only a human editing an authored document directly (via
/// <see cref="MemoryStore.Write"/>, i.e. a text editor) can accept. Nothing in this class ever
/// opens, reads, or writes a path outside proposals/ - see <see cref="Write"/>'s own validation.
///
/// A separate class from <see cref="MemoryStore"/>, not an addition to it: MemoryStore's write
/// surface (<see cref="MemoryStore.Write"/>) intentionally only ever targets the top-level
/// authored directory, and giving it a second entry point into proposals/ would blur exactly the
/// boundary this design exists to keep sharp.
/// </summary>
public sealed class MemoryProposals(AppPaths paths)
{
    /// <summary>
    /// Writes a new proposal file and returns its name. <paramref name="targetFile"/> names the
    /// authored document the proposal is ABOUT (e.g. "patterns.md" or "patterns" - it need not
    /// exist yet, since a proposal may suggest an entirely new document); it is never itself
    /// opened or written to. The new file's name is derived from <paramref name="createdAt"/> and
    /// <paramref name="targetFile"/> (e.g. "20260801-153000-patterns.md"), disambiguated with a
    /// numeric suffix on collision so two proposals landing in the same second never overwrite one
    /// another - a proposal must never silently vanish.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="targetFile"/> is null/blank, or is not
    /// a plain document name (contains a path separator).</exception>
    public MemoryProposal Write(string productGuid, string targetFile, string content, string rationale,
        DateTimeOffset createdAt)
    {
        ValidateTargetFile(targetFile);

        var proposalsDir = Path.Combine(paths.MemoryDir(productGuid), ProposalsDirName);
        Directory.CreateDirectory(proposalsDir);

        var fileName = UniqueFileName(proposalsDir, createdAt, targetFile);
        var fullContent = BuildFrontmatter(targetFile, createdAt, rationale) + content;

        AtomicWrite(Path.Combine(proposalsDir, fileName), fullContent);

        // Bare basename, not "{ProposalsDirName}/{fileName}" - see MemoryProposal.FileName's own
        // doc comment for why a prefixed shape here was a defect, not a design choice.
        return new MemoryProposal { FileName = fileName };
    }

    /// <summary>Every pending (and past - nothing here filters by status) proposal, newest first.
    /// Filenames are timestamp-prefixed (see <see cref="UniqueFileName"/>), so a plain descending
    /// ordinal sort on the name is already chronological - no need to parse
    /// <see cref="MemoryProposalInfo.CreatedAt"/> back out just to order by it. Empty, not an
    /// exception, when nothing has ever been proposed for this project (no proposals/ directory
    /// yet) - the ordinary state for most projects.</summary>
    public IReadOnlyList<MemoryProposalInfo> List(string productGuid)
    {
        var proposalsDir = Path.Combine(paths.MemoryDir(productGuid), ProposalsDirName);
        if (!Directory.Exists(proposalsDir)) return [];

        return Directory.EnumerateFiles(proposalsDir, "*.md")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .Select(name => ParseProposal(name, File.ReadAllText(Path.Combine(proposalsDir, name))))
            .ToList();
    }

    /// <summary>One proposal by its plain basename (see <see cref="MemoryProposalInfo.FileName"/>).
    /// Null when it does not exist - never an exception, same "absence is a normal state, not a
    /// failure" convention <see cref="MemoryStore.Read"/> uses.</summary>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is not a safe basename.</exception>
    public MemoryProposalInfo? Read(string productGuid, string fileName)
    {
        var path = ResolveProposalPath(productGuid, fileName);
        return File.Exists(path) ? ParseProposal(fileName, File.ReadAllText(path)) : null;
    }

    /// <summary>
    /// Rewrites ONLY the frontmatter <c>status</c> field, atomically - every other frontmatter
    /// field and the body are preserved byte-for-byte. This is how accepting or deferring a
    /// proposal (Plan 11 Task 6's control-API actions) is recorded; it never deletes the proposal
    /// file itself (see this class's own doc comment: "nothing deletes without an explicit confirm
    /// flag" is enforced by <see cref="Delete"/> being the only method here that ever removes a
    /// file, and only when its own caller explicitly chooses to call it).
    /// </summary>
    /// <returns>False when <paramref name="fileName"/> does not exist - nothing to update.</returns>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is not a safe basename.</exception>
    public bool SetStatus(string productGuid, string fileName, string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        var path = ResolveProposalPath(productGuid, fileName);
        if (!File.Exists(path)) return false;

        var file = MemoryFile.Parse(fileName, File.ReadAllText(path));
        var fields = new Dictionary<string, string>(file.Frontmatter, StringComparer.Ordinal) { ["status"] = status };

        AtomicWrite(path, BuildFrontmatterBlock(fields) + file.Body);
        return true;
    }

    /// <summary>
    /// Removes one proposal file - the ONLY method in this class that ever deletes anything, and
    /// only when ITS OWN caller explicitly calls it (see this class's own doc comment and
    /// <see cref="SetStatus"/>'s doc comment on why accepting/deferring never does).
    /// </summary>
    /// <returns>False when <paramref name="fileName"/> does not exist - nothing to delete, the
    /// same idempotent-no-op stance <see cref="Hades.Core.Graph.GraphDatabase.DeleteNodesForPath"/>
    /// takes on deleting what is already gone.</returns>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is not a safe basename.</exception>
    public bool Delete(string productGuid, string fileName)
    {
        var path = ResolveProposalPath(productGuid, fileName);
        if (!File.Exists(path)) return false;

        File.Delete(path);
        return true;
    }

    static MemoryProposalInfo ParseProposal(string fileName, string rawText)
    {
        var file = MemoryFile.Parse(fileName, rawText);

        DateTimeOffset? createdAt = file.Frontmatter.TryGetValue("created_at", out var rawCreatedAt)
            && DateTimeOffset.TryParse(rawCreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;

        return new MemoryProposalInfo
        {
            FileName = fileName,
            TargetFile = file.Frontmatter.GetValueOrDefault("target_file", ""),
            CreatedAt = createdAt,
            Rationale = file.Frontmatter.GetValueOrDefault("rationale", ""),
            Status = file.Frontmatter.GetValueOrDefault("status", ""),
            Content = file.Body,
            FrontmatterError = file.FrontmatterError,
        };
    }

    /// <summary>Same validated-basename discipline as <see cref="Write"/>'s own generated
    /// filenames, now applied to a CALLER-supplied one - reuses <see cref="MemoryStore.ValidatedChildPath"/>
    /// rather than a second traversal check (see that method's own doc comment for why it is
    /// <c>internal</c>).</summary>
    string ResolveProposalPath(string productGuid, string fileName) =>
        MemoryStore.ValidatedChildPath(Path.Combine(paths.MemoryDir(productGuid), ProposalsDirName), fileName);

    const string ProposalsDirName = "proposals";

    /// <summary>Same two checks as <see cref="MemoryStore"/>'s ValidatedChildPath applies to a
    /// document name: non-blank, and a single path segment. target_file is never itself resolved
    /// to a filesystem path here (see this class's own doc comment), but it IS slugged into the
    /// new proposal's own filename below, so the same discipline applies for the same reason -
    /// this is also the fix for the real corpus's own regression case, proposals/20260614-174745-.md,
    /// where an unvalidated empty target_file produced a filename with an empty slug.</summary>
    static void ValidateTargetFile(string? targetFile)
    {
        if (string.IsNullOrWhiteSpace(targetFile))
        {
            throw new ArgumentException(
                "Proposal target_file must not be null or blank.", nameof(targetFile));
        }

        if (targetFile.Contains(Path.DirectorySeparatorChar) || targetFile.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                $"Invalid target_file '{targetFile}': it must be a plain document name, not a path.",
                nameof(targetFile));
        }
    }

    static string Slug(string targetFile) =>
        targetFile.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? targetFile[..^3] : targetFile;

    /// <summary>Appends "-2", "-3", ... until a free name is found, so two proposals generated in
    /// the same second for the same target never collide - the timestamp alone is only
    /// second-resolution.</summary>
    static string UniqueFileName(string proposalsDir, DateTimeOffset at, string targetFile)
    {
        var baseName = $"{at.UtcDateTime:yyyyMMdd-HHmmss}-{Slug(targetFile)}";

        var candidate = $"{baseName}.md";
        for (var suffix = 2; File.Exists(Path.Combine(proposalsDir, candidate)); suffix++)
            candidate = $"{baseName}-{suffix}.md";

        return candidate;
    }

    // Stateless and safe to reuse - see MemoryFile's identical reasoning for its Deserializer.
    static readonly ISerializer Serializer = new SerializerBuilder().Build();

    /// <summary>
    /// Builds a "---\n...\n---\n" block matching exactly what <see cref="MemoryFile.Parse"/>
    /// expects to close on. Field VALUES are run through YamlDotNet's own serializer rather than
    /// hand-interpolated into "key: value\n" text, so caller-supplied text - targetFile, and
    /// especially rationale, which is free-form and could itself contain a colon, a quote, or even
    /// a bare "---" line - can never corrupt the frontmatter block's structure or bleed into the
    /// body early. The same "never trust text reaching a format-sensitive layer" discipline
    /// <see cref="MemoryIndex.Search"/> applies to FTS5 query syntax, applied here to YAML.
    /// </summary>
    static string BuildFrontmatter(string targetFile, DateTimeOffset createdAt, string rationale) =>
        BuildFrontmatterBlock(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["target_file"] = targetFile,
            ["created_at"] = createdAt.UtcDateTime.ToString("o"),
            ["rationale"] = rationale,
            ["status"] = "pending",
        });

    /// <summary>Shared by <see cref="BuildFrontmatter"/> (a fresh proposal's initial fields) and
    /// <see cref="SetStatus"/> (an existing proposal's fields, with just <c>status</c> replaced) -
    /// one YAML-serialization routine so both stay byte-for-byte consistent with what
    /// <see cref="MemoryFile.Parse"/> expects to close on. See <see cref="BuildFrontmatter"/>'s own
    /// doc comment for why field VALUES are never hand-interpolated.</summary>
    static string BuildFrontmatterBlock(IReadOnlyDictionary<string, string> fields)
    {
        var yaml = Serializer.Serialize(fields);
        if (!yaml.EndsWith('\n')) yaml += "\n";

        return $"---\n{yaml}---\n";
    }

    /// <summary>Same technique as <see cref="MemoryStore"/>'s own AtomicWrite (temp file + rename)
    /// - a reader must never observe a partially written proposal either.</summary>
    static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
