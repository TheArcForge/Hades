using Hades.Core.Storage;

namespace Hades.Core.Memory;

/// <summary>
/// One file <see cref="MemoryStore.ImportFromArcforge"/> did not copy in, and why - reported, not
/// silently dropped, whether the reason is a validation failure or (the far more common real
/// case) that app storage already has something at that name and import never overwrites it.
/// </summary>
public sealed record MemoryImportSkip
{
    /// <summary>The source file's path relative to .arcforge/memory, e.g.
    /// "inferred/convention-prefab_variants.md".</summary>
    public required string Source { get; init; }

    public required string Reason { get; init; }
}

/// <summary>The result of one <see cref="MemoryStore.ImportFromArcforge"/> call.</summary>
public sealed record MemoryImportResult
{
    /// <summary>Destination names actually written, relative to memory/ - e.g. "conventions.md"
    /// or "proposals/convention-prefab_variants.md".</summary>
    public required IReadOnlyList<string> Imported { get; init; }

    public required IReadOnlyList<MemoryImportSkip> Skipped { get; init; }
}

/// <summary>
/// Reads and writes AUTHORED memory documents - markdown files a human can open, edit, and save
/// with any text editor, living under <see cref="AppPaths.MemoryDir"/>. Nothing here regenerates
/// their content; nothing here ever deletes one. Writes are atomic (temp file + rename, the same
/// technique <see cref="Projects.ProjectStore.Save"/> uses for project.json) so a reader never
/// observes a partially written file.
/// </summary>
public sealed class MemoryStore(AppPaths paths)
{
    /// <summary>
    /// The document names recognised in the wild (see the real Hades-Unity-Client corpus). NOT a
    /// restriction - <see cref="Write"/> accepts any valid name. A fixed enum of memory documents
    /// would make memory less useful than a text editor; this list exists only so a caller (and,
    /// later, the memory-index) can tell a project's own convention apart from a name nobody has
    /// given meaning to yet.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownDocuments =
    [
        "conventions.md", "decisions.md", "glossary.md", "intent.md", "patterns.md", "pitfalls.md",
    ];

    /// <summary>Reads one memory document. Null when it does not exist - never an exception,
    /// since "no file written yet" is the ordinary state for a project with no memory.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a safe plain file name.</exception>
    public MemoryFile? Read(string productGuid, string name)
    {
        var path = ResolveDocumentPath(productGuid, name);
        return File.Exists(path) ? MemoryFile.Parse(name, File.ReadAllText(path)) : null;
    }

    /// <summary>Writes (creating or overwriting) one memory document, atomically.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a safe plain file name.</exception>
    public void Write(string productGuid, string name, string content) =>
        AtomicWrite(ResolveDocumentPath(productGuid, name), content);

    /// <summary>
    /// One-time, non-destructive import of a project's pre-existing <c>.arcforge/memory/</c>
    /// content into app storage - written for a user who has been authoring memory inside the
    /// Unity project itself, so moving storage to app-space does not silently abandon that work.
    /// The source directory is only ever read, never written to or deleted, and nothing here is
    /// interpreted or reformatted - every file is copied byte-for-byte via <see cref="File.Copy"/>,
    /// exactly as it exists on disk, "you are only importing what exists" applied literally.
    ///
    /// Every destination write is gated on "does this file already exist in app storage" - the
    /// one rule that makes both required properties true at once: calling this a second time for
    /// the same project imports nothing new for a file already imported (one-time), and never
    /// overwrites an edit a human made app-side since the first import (non-destructive). The
    /// same gate also resolves the one real collision in this shape of import: proposals/ and
    /// inferred/ both flatten into the SAME memory/proposals/ destination directory, and the real
    /// Hades-Unity-Client corpus has a same-named file in both (the old auto-inferrer's promoted
    /// proposal, and the inferred/ source it was promoted from) - proposals/ is processed first,
    /// so it claims the name, and inferred/'s copy is reported as skipped rather than silently
    /// overwriting it.
    ///
    /// Only *.md files are ever candidates - a stray non-markdown file sitting in these
    /// directories (the real corpus has inferred/.conventions-state.json, bookkeeping for the old
    /// automatic inferrer) is not a memory document at all, so it is never imported and never
    /// reported as skipped either; there is nothing to report skipping.
    ///
    /// Every destination name is resolved through <see cref="ResolveDocumentPath"/> or
    /// <see cref="ResolveProposalPath"/> - the exact same validated-path routine <see cref="Write"/>
    /// uses - so a source file whose name could not safely become a memory document is skipped
    /// and reported rather than silently dropped, the same as any other invalid write.
    /// </summary>
    /// <returns>An empty result, with no error, when <paramref name="projectRoot"/> has no
    /// .arcforge/memory directory at all.</returns>
    public MemoryImportResult ImportFromArcforge(string productGuid, string projectRoot)
    {
        var sourceDir = Path.Combine(projectRoot, ".arcforge", "memory");
        if (!Directory.Exists(sourceDir)) return new MemoryImportResult { Imported = [], Skipped = [] };

        var imported = new List<string>();
        var skipped = new List<MemoryImportSkip>();

        void TryImport(string sourceFile, string sourceLabel, Func<string, string> resolveDestination, string destinationLabel)
        {
            string destination;
            try
            {
                destination = resolveDestination(Path.GetFileName(sourceFile));
            }
            catch (ArgumentException ex)
            {
                skipped.Add(new MemoryImportSkip { Source = sourceLabel, Reason = ex.Message });
                return;
            }

            if (File.Exists(destination))
            {
                skipped.Add(new MemoryImportSkip
                {
                    Source = sourceLabel,
                    Reason = $"'{destinationLabel}' already exists in app storage; import never overwrites it.",
                });
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                File.Copy(sourceFile, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read or copied is skipped and reported like any other
                // invalid source - one bad file must never abort the files after it. A partial
                // destination would satisfy the already-exists gate above and permanently block
                // this file's retry on the next import, so it is removed best-effort.
                try { File.Delete(destination); } catch (Exception) { /* best effort */ }
                skipped.Add(new MemoryImportSkip { Source = sourceLabel, Reason = ex.Message });
                return;
            }
            imported.Add(destinationLabel);
        }

        foreach (var file in EnumerateMdFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            TryImport(file, name, n => ResolveDocumentPath(productGuid, n), name);
        }

        // proposals/ before inferred/: see this method's own doc comment for why order matters
        // for the one real name collision between them.
        foreach (var subdirectory in new[] { ProposalsDirName, "inferred" })
        {
            var subdirectoryPath = Path.Combine(sourceDir, subdirectory);
            if (!Directory.Exists(subdirectoryPath)) continue;

            foreach (var file in EnumerateMdFiles(subdirectoryPath))
            {
                var name = Path.GetFileName(file);
                TryImport(file, $"{subdirectory}/{name}", n => ResolveProposalPath(productGuid, n), $"{ProposalsDirName}/{name}");
            }
        }

        return new MemoryImportResult { Imported = imported, Skipped = skipped };
    }

    /// <summary>*.md files directly inside <paramref name="dir"/>, in a stable order - directory
    /// enumeration order is not guaranteed by the OS, and a deterministic order keeps an import
    /// report (and the collision rule in <see cref="ImportFromArcforge"/>) reproducible.
    ///
    /// Filtered in C# with <see cref="string.EndsWith(string, StringComparison)"/> rather than via
    /// <see cref="Directory.EnumerateFiles(string, string)"/>'s own "*.md" search pattern: that
    /// pattern's case sensitivity follows the underlying filesystem, which is case-SENSITIVE on
    /// Linux (unlike Windows/macOS's usual defaults) - a source file named e.g. "CAPS.MD" would
    /// silently never be found, and never imported, on Linux alone. OrdinalIgnoreCase here makes
    /// every platform behave the same, regardless of the filesystem underneath it.</summary>
    static IEnumerable<string> EnumerateMdFiles(string dir) =>
        Directory.EnumerateFiles(dir)
            .Where(f => Path.GetFileName(f).EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>
    /// Resolves <paramref name="name"/> to an absolute path inside <paramref name="productGuid"/>'s
    /// memory directory, applying the same three-check discipline as
    /// <see cref="AppPaths.ProjectDir"/>: reject null/blank, reject anything but a single path
    /// segment, then require the resolved path to be a strict child of memory/. Both
    /// <see cref="Read"/>/<see cref="Write"/> and Task 2's import route every name through this
    /// (or <see cref="ResolveProposalPath"/>), so a caller-supplied or disk-supplied name can
    /// never write - or read - outside the memory directory it belongs to.
    ///
    /// <paramref name="name"/> is validated BEFORE <see cref="NormalizeDocumentName"/> ever sees
    /// it - only the already-safe, already-contained result is normalized. Appending ".md" to a
    /// resolved path cannot itself introduce a traversal, but normalizing first and validating
    /// second would defeat the traversal check instead: ".." would become "...md", a harmless
    /// -looking single path segment that no longer trips the parent-directory check below.
    /// </summary>
    string ResolveDocumentPath(string productGuid, string name) =>
        NormalizeDocumentName(ValidatedChildPath(paths.MemoryDir(productGuid), name));

    /// <summary>Same discipline as <see cref="ResolveDocumentPath"/>, one level down: a name
    /// inside memory/proposals/, used only by Task 2's import.</summary>
    string ResolveProposalPath(string productGuid, string name) =>
        ValidatedChildPath(Path.Combine(paths.MemoryDir(productGuid), ProposalsDirName), name);

    const string ProposalsDirName = "proposals";

    /// <summary>
    /// Appends ".md" when <paramref name="name"/> does not already end with it, checked
    /// case-insensitively so "conventions.MD" is left alone rather than becoming
    /// "conventions.MD.md". Every document this store reads or writes is a markdown file by
    /// definition (see this class's own doc comment), so "conventions" and "conventions.md" name
    /// the SAME document, not two different ones.
    ///
    /// This is the fix for an invisible-orphan defect: a proposal accepted with an extension-less
    /// <c>target_file</c> - the real shape a v1.2 <c>.arcforge/memory</c> import produces, copied
    /// byte-for-byte by <see cref="ImportFromArcforge"/> - used to land its merged content in a
    /// bare, extension-less file that every *.md listing surface (<see cref="EnumerateMdFiles"/>
    /// here, and <see cref="MemoryIndex"/>'s own sync) enumerates straight past. Applied here, at
    /// <see cref="ResolveDocumentPath"/>, it closes that for every caller uniformly - the control
    /// API's own document editor (<see cref="Read"/>/<see cref="Write"/> via
    /// <c>MemoryEndpoint.GetDocument</c>/<c>WriteDocument</c>) and an accepted proposal's merge
    /// (<c>MemoryEndpoint.AcceptProposal</c>) alike - rather than a special case in just one of
    /// them.
    ///
    /// Blank/null input is returned unchanged rather than throwing here: <see cref="ValidatedChildPath"/>
    /// is what owns rejecting a blank name with its own clear message, and this must never
    /// pre-empt that with an unrelated NullReferenceException.
    ///
    /// <c>internal</c> for the same reason <see cref="ValidatedChildPath"/> is:
    /// <see cref="MemoryProposals.Write"/> reuses this EXACT normalization for the target_file it
    /// records, rather than a second, independently-maintained copy of the same ".md" rule.
    /// </summary>
    internal static string NormalizeDocumentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name : name + ".md";
    }

    /// <summary>
    /// Validates <paramref name="name"/> as a safe, single-segment basename directly inside
    /// <paramref name="baseDir"/> - rejecting blank names, embedded path separators, and any
    /// resolved path that would land outside <paramref name="baseDir"/> (<c>..</c>, a rooted path).
    /// Internal rather than private specifically so <see cref="MemoryProposals"/> can reuse this
    /// EXACT validation for memory/proposals/ (Plan 11 Task 6 - the control API's memory surface
    /// closes the audit's "dashboard memory API does FS writes/unlinks from URL path params" by
    /// routing every caller-supplied filename, on every read AND write path, through here or
    /// <see cref="Read"/>/<see cref="Write"/> above, which already call this) rather than a second,
    /// independently-maintained copy of the same traversal check.
    /// </summary>
    internal static string ValidatedChildPath(string baseDir, string name)
    {
        // Not ArgumentException.ThrowIfNullOrWhiteSpace: that throws ArgumentNullException on
        // null, and xUnit's Assert.Throws<T> matches the exact type, not subtypes - see
        // AppPaths.ProjectDir, which this mirrors.
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Memory document name must not be null or blank.", nameof(name));
        }

        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                $"Invalid memory document name '{name}': it must be a plain file name, not a path.",
                nameof(name));
        }

        var baseFull = Path.GetFullPath(baseDir);
        var candidate = Path.GetFullPath(Path.Combine(baseFull, name));

        if (candidate.Length <= baseFull.Length
            || !candidate.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Invalid memory document name '{name}': it must name a file directly inside "
                + "the memory directory.",
                nameof(name));
        }

        return candidate;
    }

    static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
