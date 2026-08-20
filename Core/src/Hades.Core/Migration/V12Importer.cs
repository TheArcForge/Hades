using Hades.Core.Storage;

namespace Hades.Core.Migration;

/// <summary>
/// Outcome of one <see cref="V12Importer.ImportTraces"/> call. Mirrors
/// <see cref="Memory.MemoryImportSkip"/>'s "report, don't silently drop" shape at the scale of a
/// single file: traces.db is one artifact, not a directory of many, so a single
/// <see cref="Imported"/> flag plus an optional skip reason says everything a per-file list would
/// for memory.
/// </summary>
public sealed record TracesImportResult
{
    public required bool Imported { get; init; }

    /// <summary>Null exactly when <see cref="Imported"/> is true. Set for either of the two
    /// ordinary, anticipated outcomes - no source traces.db to import, or app storage already has
    /// one (import never overwrites it, same rule as memory). An unanticipated failure (a
    /// permissions error, a full disk, ...) is not reported here - it propagates as an exception,
    /// the same convention <see cref="Memory.MemoryStore.ImportFromArcforge"/> itself uses for
    /// anything beyond its own two anticipated outcomes.</summary>
    public string? SkippedReason { get; init; }
}

/// <summary>
/// Executes the two v1.2-migration steps spec #4 §5 assigns to this app, each independently:
/// memory (mandatory, non-destructive) and traces (optional). Config cleanup - the package entry,
/// the generated .mcp.json, the CLAUDE.md block, claude_desktop_config.json - is task 4's
/// V12Cleanup, a distinct class; nothing here deletes or edits anything in the source project.
///
/// <para><b>Memory does not get a second implementation here.</b> The app already imports
/// .arcforge/memory/ - <see cref="Memory.MemoryStore.ImportFromArcforge"/>, wired into
/// <see cref="ProjectService.Adopt"/> and exercised on every project adoption, v1.2 or not. Its
/// rules already ARE spec #4 §5's "Import memory" row: mandatory (it runs unconditionally, never
/// gated behind a confirmation prompt - matching the spec table's "Optional? No"), non-destructive
/// (every source file is only ever opened for reading; <see cref="File.Copy(string, string)"/>
/// never touches the source), and collision-safe (an existing app-side document is reported
/// skipped, never silently overwritten). Re-implementing any of that here under a different name
/// would create exactly the risk called out going in - two importers with different rules, one
/// silently overwriting where the other refuses - so <see cref="ImportMemory"/> below calls
/// <see cref="Memory.MemoryStore.ImportFromArcforge"/> directly and owns none of its logic.
/// Calling it again here (e.g. from a migration screen, after <see cref="ProjectService.Adopt"/>
/// already ran it once for this process) is safe and informative rather than redundant: the
/// destination-exists gate makes every call idempotent, so a second call simply reports what is
/// already there as skipped, the same as it would for a document a human edited app-side.</para>
///
/// <para><b>Traces gets a real implementation here</b> - see <see cref="ImportTraces"/> - because
/// nothing else in the app imports traces.db at all; unlike memory, there is no existing path to
/// defer to.</para>
///
/// <para><b>The graph is never a target.</b> Spec #4 §5: "schema and ownership differ; rebuild
/// instead." Neither method below reads, opens, or copies graph.db, or even constructs a path to
/// it - there is no code path here that could touch it by accident.</para>
/// </summary>
public sealed class V12Importer(AppPaths paths)
{
    readonly Memory.MemoryStore _memory = new(paths);

    /// <summary>
    /// Imports one project's .arcforge/memory/ into app storage under <paramref name="productGuid"/>
    /// - see this class's own remarks on why this calls
    /// <see cref="Memory.MemoryStore.ImportFromArcforge"/> rather than re-implementing it. Mandatory
    /// per spec #4 §5 (never gated behind a confirmation prompt); non-destructive (the source
    /// directory is only ever read); never overwrites an existing app-side document (reported as a
    /// skip instead - see <see cref="Memory.MemoryImportSkip"/>).
    /// </summary>
    public Memory.MemoryImportResult ImportMemory(string productGuid, string projectRoot) =>
        _memory.ImportFromArcforge(productGuid, projectRoot);

    /// <summary>
    /// Optionally imports .arcforge/traces.db into app storage - spec #4 §5's "Import traces" row,
    /// "optional to import". A genuinely separate call from <see cref="ImportMemory"/>: nothing
    /// here reads memory, nothing in <see cref="ImportMemory"/> reads traces, so a caller can
    /// invoke either one without the other, and a failure in one can never take the other down
    /// with it.
    ///
    /// Copied as an opaque file via <see cref="File.Copy(string, string)"/>, byte for byte, never
    /// opened as a database - the same discipline <see cref="V12Detector"/> holds for the same
    /// reason: a v1.2 install may still be running against this project while migration happens
    /// (spec #4 §5: "v1.2 keeps working throughout"), and traces.db can be a live SQLite WAL
    /// database while that is true. Its "-wal"/"-shm" sidecar files, when present, are copied
    /// alongside it for exactly that reason - a plain copy of only the main file would silently
    /// miss whatever transaction is sitting in an as-yet-unchecked-pointed WAL, producing a copy
    /// that looks complete but is quietly missing recent history.
    /// </summary>
    /// <returns>Reports nothing imported, with a reason, when there is no source traces.db, or
    /// when app storage already has one (import never overwrites it - same rule as memory). Any
    /// other failure (permissions, disk full, ...) propagates as an exception - see
    /// <see cref="TracesImportResult.SkippedReason"/>'s own remarks.</returns>
    public TracesImportResult ImportTraces(string productGuid, string projectRoot)
    {
        var source = Path.Combine(projectRoot, ".arcforge", "traces.db");
        if (!File.Exists(source))
        {
            return new TracesImportResult
            {
                Imported = false,
                SkippedReason = "No .arcforge/traces.db in the source project.",
            };
        }

        var destination = paths.TracesDb(productGuid);
        if (File.Exists(destination))
        {
            return new TracesImportResult
            {
                Imported = false,
                SkippedReason = "'traces.db' already exists in app storage; import never overwrites it.",
            };
        }

        paths.EnsureProjectDir(productGuid);

        // Sidecars copied before the main file: the main file's existence is what the
        // already-exists gate above checks, so a partial failure here must never leave a
        // destination traces.db behind without the sidecar data that came with it.
        foreach (var suffix in TracesSidecarSuffixes)
        {
            var sidecarSource = source + suffix;
            if (File.Exists(sidecarSource)) File.Copy(sidecarSource, destination + suffix, overwrite: true);
        }

        File.Copy(source, destination);
        return new TracesImportResult { Imported = true };
    }

    static readonly string[] TracesSidecarSuffixes = ["-wal", "-shm"];
}
