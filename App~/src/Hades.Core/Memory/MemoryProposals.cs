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
    /// <summary>Serializes <see cref="AllocateAndLand"/> - see that method's own doc comment for
    /// why the retry-on-IOException loop ALONE was empirically not enough: under a live 16-way
    /// concurrent-Write test, several threads' internal check-then-rename windows (inside .NET's
    /// own <see cref="File.Move(string, string, bool)"/> implementation, not this class's code)
    /// overlapped closely enough that one thread's move silently succeeded over another's
    /// just-landed file WITHOUT throwing - the retry loop can only retry a failure it is told
    /// about. See <see cref="_statusLock"/>'s own doc comment for why instance-scoping either lock
    /// is correct, not merely convenient.</summary>
    readonly object _allocationLock = new();

    /// <summary>Serializes <see cref="SetStatus"/>'s read-modify-write - see that method's own doc
    /// comment for the race this closes. A separate lock from <see cref="_allocationLock"/>
    /// deliberately: <see cref="Write"/> (creating a proposal) and <see cref="SetStatus"/> (updating
    /// one that already exists) never touch the same file, so serializing one against the other
    /// would cost concurrency for no correctness benefit.
    ///
    /// Both locks are instance-scoped, not static: <c>ProjectService</c> (the only real caller,
    /// constructed once via dependency injection) holds exactly one <see cref="MemoryProposals"/>
    /// for the whole process (see Program.cs's <c>AddSingleton&lt;ProjectService&gt;</c>), so an
    /// instance lock already serializes every real concurrent call in the process; a static lock
    /// would additionally, needlessly serialize unrelated <see cref="MemoryProposals"/> instances
    /// (every test in this suite constructs its own) for no correctness benefit.</summary>
    readonly object _statusLock = new();

    /// <summary>
    /// Writes a new proposal file and returns its name. <paramref name="targetFile"/> names the
    /// authored document the proposal is ABOUT (e.g. "patterns.md" or "patterns", normalized to
    /// always carry ".md" - see <see cref="MemoryStore.NormalizeDocumentName"/> - before it is
    /// recorded; it need not exist yet, since a proposal may suggest an entirely new document); it
    /// is never itself opened or written to. The new file's name is derived from
    /// <paramref name="createdAt"/> and <paramref name="targetFile"/> (e.g.
    /// "20260801-153000-patterns.md"), disambiguated with a numeric suffix on collision so two
    /// proposals landing in the same second never overwrite one another - a proposal must never
    /// silently vanish. That guarantee holds under real concurrency, not just sequential calls -
    /// see <see cref="AllocateAndLand"/> for the mechanism that actually makes it true.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="targetFile"/> is null/blank, or is not
    /// a plain document name (contains a path separator).</exception>
    public MemoryProposal Write(string productGuid, string targetFile, string content, string rationale,
        DateTimeOffset createdAt)
    {
        ValidateTargetFile(targetFile);
        // Same ".md" normalization MemoryStore applies at its own write boundary (accepting a
        // proposal), applied here too so a FRESHLY-created proposal's own target_file is never the
        // extension-less shape that makes an eventually-accepted document invisible to every *.md
        // listing surface - see MemoryStore.NormalizeDocumentName's own doc comment. This does not
        // retroactively fix a proposal already on disk (e.g. a v1.2 .arcforge import, copied
        // byte-for-byte - see MemoryStore.ImportFromArcforge); MemoryStore's own normalization at
        // accept time is what covers those.
        var normalizedTargetFile = MemoryStore.NormalizeDocumentName(targetFile);

        var proposalsDir = Path.Combine(paths.MemoryDir(productGuid), ProposalsDirName);
        Directory.CreateDirectory(proposalsDir);

        var fullContent = BuildFrontmatter(normalizedTargetFile, createdAt, rationale) + content;
        var fileName = AllocateAndLand(proposalsDir, createdAt, normalizedTargetFile, fullContent);

        // Bare basename, not "{ProposalsDirName}/{fileName}" - see MemoryProposal.FileName's own
        // doc comment for why a prefixed shape here was a defect, not a design choice.
        return new MemoryProposal { FileName = fileName };
    }

    /// <summary>Every pending (and past - nothing here filters by status) proposal, ordered for
    /// review - see <see cref="OrderForReview"/> for exactly how. Empty, not an exception, when
    /// nothing has ever been proposed for this project (no proposals/ directory yet) - the
    /// ordinary state for most projects.</summary>
    public IReadOnlyList<MemoryProposalInfo> List(string productGuid)
    {
        var proposalsDir = Path.Combine(paths.MemoryDir(productGuid), ProposalsDirName);
        if (!Directory.Exists(proposalsDir)) return [];

        // Not Directory.EnumerateFiles(proposalsDir, "*.md"): that search pattern's case
        // sensitivity follows the underlying filesystem, case-SENSITIVE on Linux - see
        // MemoryStore.EnumerateMdFiles's identical fix. A ".MD"-suffixed proposal (byte-for-byte
        // copied in by MemoryStore.ImportFromArcforge with whatever case its source had) would
        // silently never be listed there alone.
        var parsed = Directory.EnumerateFiles(proposalsDir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(name => ParseProposal(name, File.ReadAllText(Path.Combine(proposalsDir, name))));

        return OrderForReview(parsed).ToList();
    }

    /// <summary>
    /// Orders proposals for the control API's review queue (spec #3 §3.4, replacing
    /// <c>/hades:show-proposals</c> as the primary surface) - the fix for a flat, unordered list
    /// mixing a handful of agent-authored proposals with dozens of analyzer-generated statistical
    /// rows in no particular order, burying the few a person would actually act on.
    ///
    /// <b>Anything a human review action can still land on sorts ahead of pure analyzer output.</b>
    /// "inferred" is the one <see cref="MemoryProposalInfo.Status"/> value the real
    /// Hades-Unity-Client corpus uses for analyzer-generated rows (topic_cluster, time_of_day,
    /// failure_correlation, acceptance_rate, and the convention-inferrer all write it) - see that
    /// property's own doc comment. Every other value - "pending", "accepted", "deferred", a blank
    /// frontmatter field (a real corpus has had exactly this shape - see
    /// Hades.Core.Tests.Memory.RealProjectMemoryImportSmokeTest's own comment on
    /// proposals/20260614-174745-.md), or anything not yet invented - sorts ahead of it. This
    /// intentionally does NOT enumerate the "authored" side as a closed set: status is not a closed
    /// enum (see <see cref="MemoryProposalInfo.Status"/>'s own doc comment), so the default for
    /// anything unrecognised is to surface it prominently, never to bury it as if it were noise.
    ///
    /// <b>Equal-status rows stay contiguous</b> - <see cref="MemoryProposalInfo.Status"/> itself is
    /// the tiebreak, ordinal, before falling back to the pre-existing newest-name-first order
    /// (filenames are timestamp-prefixed - see <see cref="AllocateAndLand"/> - so a descending
    /// ordinal sort on the name is already chronological). This is not a claim that one status
    /// outranks another; it only guarantees that a shell grouping consecutive equal-status rows
    /// into sections (see <c>ProposalQueueView</c>'s own doc comment) never has to split one status
    /// into two separate, non-adjacent runs.
    ///
    /// Never filters by status - an accepted or deferred proposal still appears in the result
    /// exactly as before this method existed, only WHERE it appears changed. This is also the ONLY
    /// place in this codebase that interprets <c>status</c> at all: spec #3 §1 "Swift renders, .NET
    /// decides" - nothing downstream (<c>MemoryEndpoint</c>, the shell's own <c>ProposalQueueView</c>)
    /// re-sorts or re-derives this ordering, only renders whatever already fell out of it.
    /// </summary>
    static IEnumerable<MemoryProposalInfo> OrderForReview(IEnumerable<MemoryProposalInfo> proposals) =>
        proposals
            .OrderBy(p => p.Status == InferredStatus)
            .ThenBy(p => p.Status, StringComparer.Ordinal)
            .ThenByDescending(p => p.FileName, StringComparer.Ordinal);

    /// <summary>The one status value analyzer-generated proposals carry - see
    /// <see cref="OrderForReview"/>.</summary>
    const string InferredStatus = "inferred";

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

        // _statusLock: without it, two concurrent calls on the SAME proposal (a human clicking
        // Accept while another tab clicks Defer) could each read the status BEFORE either had
        // written it back, so whichever call's own atomic write happened to land last would
        // silently win in full - including reverting the other call's status change - with neither
        // caller ever told anything but success. The lock makes the two calls line up instead: the
        // second call's read always sees the first call's completed write, so the final status is
        // whichever call entered the lock second, deterministically, not whichever call's write
        // happened to land last.
        lock (_statusLock)
        {
            if (!File.Exists(path)) return false;

            var file = MemoryFile.Parse(fileName, File.ReadAllText(path));
            var fields = new Dictionary<string, string>(file.Frontmatter, StringComparer.Ordinal) { ["status"] = status };

            AtomicWrite(path, BuildFrontmatterBlock(fields) + file.Body);
            return true;
        }
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

    /// <summary>
    /// Allocates a unique file name for a new proposal AND lands <paramref name="content"/> under
    /// it, atomically, in the same operation. Replaces a former two-step "find a free name via a
    /// File.Exists loop, then write it" approach that was a check-then-act race: two concurrent
    /// <see cref="Write"/> calls could both pass the File.Exists check for the SAME candidate
    /// before either had created it, after which the second call's <c>File.Move(overwrite: true)</c>
    /// would silently destroy the first's just-landed proposal - both callers told success, one
    /// proposal gone. "A proposal must never silently vanish" (see <see cref="Write"/>'s own doc
    /// comment) is a correctness contract, not a robustness nicety, so this is a defect fix, not a
    /// hardening pass.
    ///
    /// Two layers, not one. <c>File.Move(temp, candidate, overwrite: false)</c> either lands the
    /// file or throws <see cref="IOException"/> because candidate now exists; on exactly that
    /// failure this advances to the next numeric suffix and retries against a new candidate - any
    /// other exception is left to propagate, since "the name is taken" is the only reason to retry.
    /// That alone was NOT sufficient by itself, empirically: under a live many-way concurrent-Write
    /// test targeting the same second, several callers' own internal check-then-rename windows
    /// (inside .NET's Unix <see cref="File.Move(string, string, bool)"/> implementation, which
    /// checks existence and renames as two separate syscalls even with <c>overwrite: false</c>)
    /// overlapped closely enough that one call's move silently succeeded over another's just-landed
    /// file WITHOUT throwing - nothing for a retry loop to catch, because nothing failed. Wrapping
    /// the whole allocate-and-land attempt in <see cref="_allocationLock"/> closes that: only one
    /// call in this process can be inside a single File.Move at a time, so two callers can no longer
    /// overlap inside that gap to begin with. The retry-on-IOException logic stays as a second,
    /// independent guarantee - correct even without the lock, and cheap to keep.
    ///
    /// Still reader-never-sees-partial: each attempt writes to its own GUID-suffixed temp file
    /// first - the same technique <see cref="AtomicWrite"/> uses for <see cref="SetStatus"/> - only
    /// moved into place once it holds the complete content.
    /// </summary>
    string AllocateAndLand(string proposalsDir, DateTimeOffset at, string targetFile, string content)
    {
        var baseName = $"{at.UtcDateTime:yyyyMMdd-HHmmss}-{Slug(targetFile)}";

        lock (_allocationLock)
        {
            for (var suffix = 1; suffix <= MaxAllocationAttempts; suffix++)
            {
                var candidateName = suffix == 1 ? $"{baseName}.md" : $"{baseName}-{suffix}.md";
                var candidatePath = Path.Combine(proposalsDir, candidateName);
                var temp = Path.Combine(proposalsDir, $".{candidateName}.{Guid.NewGuid():N}.tmp");

                File.WriteAllText(temp, content);
                try
                {
                    File.Move(temp, candidatePath, overwrite: false);
                    return candidateName;
                }
                catch (IOException)
                {
                    // candidateName was claimed by another concurrent Write between the moment we
                    // picked it and this move - our temp file never became visible under its final
                    // name, so remove it and retry against the next suffix.
                    File.Delete(temp);
                }
            }
        }

        throw new IOException(
            $"Could not allocate a unique proposal file name under '{proposalsDir}' for target "
            + $"'{targetFile}' after {MaxAllocationAttempts} attempts.");
    }

    /// <summary>Bounded so a persistent, non-collision failure (e.g. a read-only proposals
    /// directory) fails loudly after a fixed number of tries instead of looping forever - genuine
    /// collision could never realistically approach this many concurrent writers for one target in
    /// one second.</summary>
    const int MaxAllocationAttempts = 1000;

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
