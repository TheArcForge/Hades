using System.Text.Json;

namespace Hades.Core.Migration;

/// <summary>
/// Whether Packages/manifest.json declares a "com.arcforge.hades" dependency, and what it
/// points at - the one trigger condition spec #4 §5 defines for offering migration at all.
/// </summary>
public sealed record V12ManifestEntry
{
    public required bool Present { get; init; }

    /// <summary>The JSON string value verbatim - e.g. "file:/Users/mike/Projects/Hades" for a
    /// local install, or a plain version like "1.2.3" for a registry install. Null when
    /// <see cref="Present"/> is false. Reported as-is: the detector does not assume which shape
    /// it is.</summary>
    public string? Value { get; init; }

    /// <summary>Resolved absolute path, set only when <see cref="Value"/> is a "file:"
    /// reference. A relative "file:" path resolves against Packages/, mirroring
    /// <see cref="Indexing.ProjectWalker"/>'s own manifest rule - the same file, read the same
    /// way, so the two never disagree about what a local package reference means. Null for a
    /// registry version string, or when <see cref="Present"/> is false.</summary>
    public string? ResolvedPath { get; init; }
}

/// <summary>
/// Which of the three real-world shapes a project's CLAUDE.md is in. See
/// <see cref="V12Detector"/>'s remarks on why <see cref="Unmarked"/> deliberately does not
/// distinguish Hades-authored-wholesale from hand-written.
/// </summary>
public enum ClaudeMdShape
{
    /// <summary>No CLAUDE.md at all. Ordinary, not an error.</summary>
    Absent,

    /// <summary>Contains EXACTLY ONE well-formed &lt;!-- HADES:START --&gt; / &lt;!-- HADES:END --&gt;
    /// pair - no second START or END anywhere else in the file. Only the block between them is
    /// Hades' - everything outside it, marked or not, is left alone by cleanup regardless of how
    /// it reads.</summary>
    Marked,

    /// <summary>Exists, but has no single well-formed marker pair. Covers a file Hades wrote
    /// wholesale before markers existed, a file the user wrote themselves with no Hades
    /// involvement at all - see <see cref="V12Detector"/>'s remarks on why those two are not told
    /// apart - AND a file with a second START or END anywhere (nested or a separate extra pair),
    /// where which pair is "the" block is genuinely ambiguous: never guessed at, folded in here
    /// with the same "ask, never assume" answer every other Unmarked case already gets.</summary>
    Unmarked,
}

/// <summary>
/// The marked block's extent within its file's raw text, as UTF-16 character offsets (the same
/// indexing <see cref="string.IndexOf(string, StringComparison)"/> and range indexers use):
/// <see cref="Start"/> is where "&lt;!-- HADES:START --&gt;" begins, <see cref="End"/> is just
/// past the final "&gt;" of "&lt;!-- HADES:END --&gt;". Removing exactly
/// <c>content[Start..End]</c> deletes the marked block and nothing else; this type does not
/// decide what happens to surrounding whitespace - that is cleanup's call to make, not
/// detection's.
/// </summary>
public sealed record ClaudeMdMarkedBlock
{
    public required int Start { get; init; }
    public required int End { get; init; }
}

/// <summary>CLAUDE.md's detected shape, plus the marked block's extent when there is one.</summary>
public sealed record ClaudeMdState
{
    public required ClaudeMdShape Shape { get; init; }

    /// <summary>Non-null if and only if <see cref="Shape"/> is <see cref="ClaudeMdShape.Marked"/>.</summary>
    public ClaudeMdMarkedBlock? MarkedBlock { get; init; }
}

/// <summary>
/// Everything <see cref="V12Detector.Detect"/> found in one project, item by item. Every
/// <c>Has*</c> flag, <see cref="ManifestEntry"/>, and <see cref="ClaudeMd"/> stand
/// independently - no combination is invalid, and false/Absent everywhere (a project with none
/// of this) is a perfectly ordinary result, not an error case.
/// </summary>
public sealed record V12DetectionResult
{
    public required string ProjectRoot { get; init; }

    /// <summary>Whether <see cref="ProjectRoot"/> exists as a directory AT ALL - checked first, and
    /// reported honestly. Every OTHER field below still resolves to its ordinary "absent" value even
    /// when this is false (<see cref="File.Exists(string)"/>/<see cref="Directory.Exists(string)"/>
    /// on a path under a nonexistent root safely return false, never throw), but a caller must not
    /// read those as "confirmed nothing here" when this is false - the honest claim is "could not
    /// even look," not "looked and found nothing." Before this field existed, a project whose folder
    /// had moved, been deleted, or sat on an unmounted volume - despite still being a KNOWN,
    /// previously-adopted project - reported the exact same "every item absent" shape as a genuine,
    /// freshly-scanned, v1.2-free project, with no way for a caller to tell the two apart. The
    /// Control API's migration-detect route maps this straight onto its own wire response so a
    /// caller over HTTP gets the same distinction.</summary>
    public required bool ProjectRootExists { get; init; }

    public required V12ManifestEntry ManifestEntry { get; init; }

    /// <summary>True on the one condition spec #4 §5 defines for offering migration at all: the
    /// manifest carries the com.arcforge.hades entry. Every other field here can be true or
    /// false independent of this one - e.g. a project can carry leftover .arcforge/ state after
    /// the package entry was already removed by hand, and this correctly reports
    /// <c>IsV12Project = false</c> for it without hiding what is still on disk.</summary>
    public bool IsV12Project => ManifestEntry.Present;

    /// <summary>Whether .arcforge/memory/ exists at all. See <see cref="MemoryDocumentCount"/>
    /// for how much is actually in it - the directory can exist and be empty.</summary>
    public required bool HasMemory { get; init; }

    /// <summary>
    /// Count of *.md files across the three locations
    /// <see cref="Memory.MemoryStore.ImportFromArcforge"/> itself scans: memory/ directly,
    /// memory/proposals/, and memory/inferred/ (one level deep in each). A name that collides
    /// between proposals/ and inferred/ - the real Hades-Unity-Client corpus has one - is
    /// counted once per file on disk here, not de-duplicated the way import's collision rule
    /// would; resolving that collision is import's job, not detection's. Zero when
    /// <see cref="HasMemory"/> is false, or when the directory exists but has nothing
    /// importable in it.
    /// </summary>
    public required int MemoryDocumentCount { get; init; }

    /// <summary>Whether .arcforge/traces.db exists. Never opened - existence is the entire
    /// signal detection needs, and opening a SQLite file that may be mid-write under a live app
    /// is exactly the kind of touch detection must not risk.</summary>
    public required bool HasTraces { get; init; }

    /// <summary>Whether .arcforge/graph.db exists. Detected and reported, but - spec #4 §5 -
    /// deliberately never a target for import: "schema and ownership differ; rebuild instead."
    /// Like <see cref="HasTraces"/>, existence-only; never opened.</summary>
    public required bool HasGraph { get; init; }

    /// <summary>Whether a project-root .mcp.json exists. Presence-only: v1.2 always wrote this
    /// file wholesale (see spec #4 §1's "not an install unit any more" list), so - unlike
    /// CLAUDE.md, which Hades edits inside a document the user may also own - there is no "whose
    /// content is this" question to resolve at detection time.</summary>
    public required bool HasGeneratedMcpConfig { get; init; }

    public required ClaudeMdState ClaudeMd { get; init; }

    /// <summary>Whether Assets/Hades/Hades.asmdef exists - checked by that specific file, not
    /// just the folder name, since "Hades" is also a well-known game title and a bare
    /// Assets/Hades/ folder is not on its own good evidence of the plugin.</summary>
    public required bool HasUnityPlugin { get; init; }
}

/// <summary>
/// Reports what a v1.2 (pre-distribution) Hades install left behind in a Unity project -
/// nothing more. Every path here only ever reads: no file is written, moved, or deleted, and
/// neither traces.db nor graph.db is ever opened (existence is checked with
/// <see cref="File.Exists"/> alone). That is not an implementation detail, it is the whole
/// point - spec #4 §10: "Migration is always offered, never performed silently," and a detector
/// with a side effect would break that promise before the user has been asked anything. The
/// importer (task 3) and the config cleanup (task 4) are where anything actually gets written.
///
/// <para><b>CLAUDE.md's Unmarked case, honestly:</b> spec #4 §5 describes three real shapes - a
/// marked block, a file Hades wrote wholesale with no markers, and a file the user wrote with no
/// Hades involvement. This detector reliably tells the FIRST apart from the other two: a
/// well-formed marker pair is unambiguous. It does NOT attempt to tell the other two apart from
/// each other - there is no marker, no metadata, and no naming convention that survives a user's
/// own edits reliably enough to assert "Hades wrote this" versus "the user did." The spec itself
/// concedes exactly this: "Where markers are absent - a file Hades created wholesale - the app
/// asks rather than deletes." Both collapse into <see cref="ClaudeMdShape.Unmarked"/>, and
/// downstream behaviour (ask, never delete) is identical either way. A heuristic that guessed
/// here would let a UI skip asking on high "confidence" - exactly the kind of silent judgment
/// call this whole migration path exists to avoid.</para>
/// </summary>
public static class V12Detector
{
    public const string PackageId = "com.arcforge.hades";

    public const string StartMarker = "<!-- HADES:START -->";
    public const string EndMarker = "<!-- HADES:END -->";

    /// <summary>Reads everything spec #4 §5 lists for one project. Never throws for an ordinary
    /// absence - a missing file, a missing directory, an unreadable file, or a project that was
    /// never a v1.2 install at all all resolve to items reported absent, not an exception.</summary>
    public static V12DetectionResult Detect(string projectRoot) => new()
    {
        ProjectRoot = projectRoot,
        ProjectRootExists = Directory.Exists(projectRoot),
        ManifestEntry = ReadManifestEntry(projectRoot),
        HasMemory = Directory.Exists(MemoryDir(projectRoot)),
        MemoryDocumentCount = CountMemoryDocuments(projectRoot),
        HasTraces = File.Exists(Path.Combine(projectRoot, ".arcforge", "traces.db")),
        HasGraph = File.Exists(Path.Combine(projectRoot, ".arcforge", "graph.db")),
        HasGeneratedMcpConfig = File.Exists(Path.Combine(projectRoot, ".mcp.json")),
        ClaudeMd = ReadClaudeMd(projectRoot),
        HasUnityPlugin = File.Exists(Path.Combine(projectRoot, "Assets", "Hades", "Hades.asmdef")),
    };

    static string MemoryDir(string projectRoot) => Path.Combine(projectRoot, ".arcforge", "memory");

    static int CountMemoryDocuments(string projectRoot)
    {
        var memoryDir = MemoryDir(projectRoot);
        if (!Directory.Exists(memoryDir)) return 0;

        return CountMdFiles(memoryDir)
            + CountMdFiles(Path.Combine(memoryDir, "proposals"))
            + CountMdFiles(Path.Combine(memoryDir, "inferred"));
    }

    static int CountMdFiles(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.md").Count() : 0;

    /// <summary>Mirrors <see cref="Indexing.ProjectWalker"/>'s own manifest read: same path,
    /// same JsonDocument navigation, same exception handling - a second, subtly different parse
    /// of the same file would risk disagreeing with the reader that already exists.</summary>
    static V12ManifestEntry ReadManifestEntry(string projectRoot)
    {
        var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
        if (!File.Exists(manifestPath)) return new V12ManifestEntry { Present = false };

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));

            if (!document.RootElement.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object
                || !dependencies.TryGetProperty(PackageId, out var entry)
                || entry.ValueKind != JsonValueKind.String)
            {
                return new V12ManifestEntry { Present = false };
            }

            var value = entry.GetString();
            if (value is null) return new V12ManifestEntry { Present = false };

            return new V12ManifestEntry { Present = true, Value = value, ResolvedPath = TryResolveFilePath(value, projectRoot) };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new V12ManifestEntry { Present = false };
        }
    }

    /// <summary>Null for a registry version string (no "file:" prefix). For a "file:" value,
    /// resolves relative paths against Packages/ - Unity's own rule for local package
    /// dependencies. Also null for a degenerate value like the bare prefix "file:" with nothing
    /// after it: <see cref="Path.Combine(string, string, string)"/> treats an empty final
    /// segment as a no-op, so without this check the result would silently be the Packages
    /// directory itself - a coincidence of empty-string handling, not a real answer to "what
    /// does this point at." The try/catch below remains for any other value that is not a valid
    /// path at all (e.g. embedded illegal characters).</summary>
    static string? TryResolveFilePath(string value, string projectRoot)
    {
        if (!value.StartsWith("file:", StringComparison.Ordinal)) return null;

        var raw = value["file:".Length..];
        if (string.IsNullOrEmpty(raw)) return null;

        try
        {
            return Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(projectRoot, "Packages", raw));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    static ClaudeMdState ReadClaudeMd(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "CLAUDE.md");
        if (!File.Exists(path)) return new ClaudeMdState { Shape = ClaudeMdShape.Absent };

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file demonstrably exists (File.Exists above) but could not be read right now -
            // a lock or permission blip. Absent would be a lie (there IS something here);
            // Unmarked is the conservative truth: content could not be inspected for markers, so
            // treat it exactly like any other file with no verified marker pair - ask, never
            // assume it is safe to touch.
            return new ClaudeMdState { Shape = ClaudeMdShape.Unmarked };
        }

        var start = content.IndexOf(StartMarker, StringComparison.Ordinal);
        var end = content.IndexOf(EndMarker, StringComparison.Ordinal);

        // A second occurrence of either marker anywhere else in the file - nested inside the
        // first pair, or simply a second well-formed pair later on - makes which pair is "the"
        // block genuinely ambiguous. This is exactly the shape V12Cleanup's own multiplicity
        // guard (CountOccurrences(...) != 1) already refuses to act on rather than guess at;
        // checking it here too means Shape.Marked itself is now trustworthy, so no future
        // consumer has to re-derive that same defence just to avoid acting on a false positive.
        var hasSecondStart = start >= 0
            && content.IndexOf(StartMarker, start + StartMarker.Length, StringComparison.Ordinal) >= 0;
        var hasSecondEnd = end >= 0
            && content.IndexOf(EndMarker, end + EndMarker.Length, StringComparison.Ordinal) >= 0;

        if (start >= 0 && end > start && !hasSecondStart && !hasSecondEnd)
        {
            return new ClaudeMdState
            {
                Shape = ClaudeMdShape.Marked,
                MarkedBlock = new ClaudeMdMarkedBlock { Start = start, End = end + EndMarker.Length },
            };
        }

        return new ClaudeMdState { Shape = ClaudeMdShape.Unmarked };
    }
}
