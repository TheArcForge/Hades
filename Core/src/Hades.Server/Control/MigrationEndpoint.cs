using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Migration;

namespace Hades.Server.Control;

/// <summary>Mirrors <see cref="Hades.Core.Migration.ClaudeMdShape"/> - see that type's own doc
/// comment for why <see cref="Unmarked"/> deliberately does not distinguish Hades-authored-wholesale
/// from hand-written. Unrecognised values are impossible here (this side always produces the value,
/// never decodes one), but the closed three-case shape is kept identical to the source enum rather
/// than collapsed to a bool, so a future shape is not a silent breaking change to this wire type.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MigrationClaudeMdShape
{
    [JsonStringEnumMemberName("absent")] Absent,
    [JsonStringEnumMemberName("marked")] Marked,
    [JsonStringEnumMemberName("unmarked")] Unmarked,
}

/// <summary>Wire mapping of <see cref="V12ManifestEntry"/> - see <see cref="MigrationDetectionResult"/>.</summary>
public sealed record MigrationManifestEntryInfo
{
    [JsonPropertyName("present")] public required bool Present { get; init; }
    [JsonPropertyName("value")] public string? Value { get; init; }
    [JsonPropertyName("resolvedPath")] public string? ResolvedPath { get; init; }
}

/// <summary>Wire mapping of <see cref="ClaudeMdState"/> - deliberately omits
/// <see cref="ClaudeMdMarkedBlock"/>'s character offsets. Those exist so
/// <see cref="V12Cleanup.CleanClaudeMd"/> can splice exactly the marked block out of the file; a
/// wire client has no legitimate use for them (see <see cref="MigrationEndpoint.CleanClaudeMd"/>'s
/// own doc comment for why cleanup re-detects fresh server-side rather than trusting a client-supplied
/// offset pair back), and shipping them would be exactly the kind of client-side reach-around
/// "Swift renders, .NET decides" exists to prevent.</summary>
public sealed record MigrationClaudeMdInfo
{
    [JsonPropertyName("shape")] public required MigrationClaudeMdShape Shape { get; init; }
}

/// <summary>The full <c>GET /control/migration/{productGuid}/detect</c> response - a wire mapping of
/// <see cref="V12DetectionResult"/>, field for field. See <see cref="MigrationEndpoint"/>'s own class
/// doc comment for why this endpoint is safe to call at any time.</summary>
public sealed record MigrationDetectionResult
{
    [JsonPropertyName("projectRoot")] public required string ProjectRoot { get; init; }

    /// <summary>Mirrors <see cref="V12DetectionResult.ProjectRootExists"/> - false means the
    /// project's folder could not even be looked at (moved, deleted, or its volume is unmounted),
    /// which every OTHER field below cannot tell apart from "looked, found nothing" on its own. A
    /// caller must check this FIRST and, when false, not present the rest of this payload as a
    /// confident "nothing to migrate" answer - see that property's own doc comment for the shape
    /// this closes: a known, previously-adopted project whose path has since gone missing used to
    /// report the exact same "every item absent" answer as a genuine, freshly-scanned, v1.2-free
    /// project.</summary>
    [JsonPropertyName("projectRootExists")] public required bool ProjectRootExists { get; init; }

    /// <summary>Mirrors <see cref="V12DetectionResult.IsV12Project"/> - the one condition spec #4
    /// §5 defines for offering migration at all. Resolved here, not re-derived client-side: Swift
    /// reads this field directly rather than inspecting <see cref="ManifestEntry"/> itself.</summary>
    [JsonPropertyName("isV12Project")] public required bool IsV12Project { get; init; }

    [JsonPropertyName("manifestEntry")] public required MigrationManifestEntryInfo ManifestEntry { get; init; }
    [JsonPropertyName("hasMemory")] public required bool HasMemory { get; init; }
    [JsonPropertyName("memoryDocumentCount")] public required int MemoryDocumentCount { get; init; }
    [JsonPropertyName("hasTraces")] public required bool HasTraces { get; init; }
    [JsonPropertyName("hasGraph")] public required bool HasGraph { get; init; }
    [JsonPropertyName("hasGeneratedMcpConfig")] public required bool HasGeneratedMcpConfig { get; init; }
    [JsonPropertyName("claudeMd")] public required MigrationClaudeMdInfo ClaudeMd { get; init; }
    [JsonPropertyName("hasUnityPlugin")] public required bool HasUnityPlugin { get; init; }
}

/// <summary>Wire mapping of <see cref="Memory.MemoryImportSkip"/>.</summary>
public sealed record MigrationMemorySkip
{
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
}

/// <summary>The response of <c>POST /control/migration/{productGuid}/importMemory</c> - a wire
/// mapping of <see cref="Memory.MemoryImportResult"/>.</summary>
public sealed record MigrationMemoryImportResult
{
    [JsonPropertyName("imported")] public required IReadOnlyList<string> Imported { get; init; }
    [JsonPropertyName("skipped")] public required IReadOnlyList<MigrationMemorySkip> Skipped { get; init; }
}

/// <summary>The response of <c>POST /control/migration/{productGuid}/importTraces</c> - a wire
/// mapping of <see cref="TracesImportResult"/>.</summary>
public sealed record MigrationTracesImportResult
{
    [JsonPropertyName("imported")] public required bool Imported { get; init; }
    [JsonPropertyName("skippedReason")] public string? SkippedReason { get; init; }
}

/// <summary>The response of <c>POST /control/migration/{productGuid}/cleanClaudeMd</c> - a wire
/// mapping of <see cref="ClaudeMdCleanupResult"/>. <see cref="RemainingContentOutsideBlock"/> is
/// the field that keeps "cleanup succeeded" and "the file is now clean" from collapsing into one
/// claim - see that property's own doc comment on <see cref="ClaudeMdCleanupResult"/>.</summary>
public sealed record MigrationClaudeMdCleanupResult
{
    [JsonPropertyName("removed")] public required bool Removed { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("remainingContentOutsideBlock")] public required bool RemainingContentOutsideBlock { get; init; }
}

/// <summary>The response of <c>POST /control/migration/{productGuid}/cleanManifest</c> - a wire
/// mapping of <see cref="ManifestCleanupResult"/>.</summary>
public sealed record MigrationManifestCleanupResult
{
    [JsonPropertyName("removed")] public required bool Removed { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("occurrencesFound")] public required int OccurrencesFound { get; init; }
    [JsonPropertyName("portConflictWarning")] public required string PortConflictWarning { get; init; }
}

/// <summary>The response of <c>POST /control/migration/{productGuid}/cleanMcpConfig</c> - a wire
/// mapping of <see cref="McpConfigCleanupResult"/>.</summary>
public sealed record MigrationMcpConfigCleanupResult
{
    [JsonPropertyName("removed")] public required bool Removed { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("occurrencesFound")] public required int OccurrencesFound { get; init; }
}

/// <summary>The response of <c>POST /control/migration/claudeDesktopConfig/clean</c> - a wire
/// mapping of <see cref="ClaudeDesktopConfigCleanupResult"/>. <see cref="ScopeWarning"/> is always
/// populated - see that property's own doc comment on why this file's global, per-user scope must
/// never be allowed to read as project-scoped. <see cref="OccurrencesFound"/> is likewise always
/// populated (including when <see cref="Removed"/> is false) - since this route has no companion
/// per-project detect endpoint, it is a caller's only way to learn whether there is a "hades" entry
/// here worth offering to clean up at all.</summary>
public sealed record MigrationClaudeDesktopConfigCleanupResult
{
    [JsonPropertyName("removed")] public required bool Removed { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("scopeWarning")] public required string ScopeWarning { get; init; }
    [JsonPropertyName("occurrencesFound")] public required int OccurrencesFound { get; init; }
}

/// <summary>The response of <c>POST /control/migration/hadesHub/clean</c> - a wire mapping of
/// <see cref="HadesHubCleanupResult"/>. <see cref="Found"/> is always populated, including when
/// <see cref="Removed"/> is false - same reasoning as
/// <see cref="MigrationClaudeDesktopConfigCleanupResult.OccurrencesFound"/>: this route has no
/// companion per-project detect endpoint (see <see cref="MigrationDetectionResult"/>), so this
/// field is a caller's only way to learn whether there is anything here worth offering to clean up
/// at all.</summary>
public sealed record MigrationHadesHubCleanupResult
{
    [JsonPropertyName("removed")] public required bool Removed { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("found")] public required bool Found { get; init; }
}

/// <summary>Body of every cleanup POST route below. <see cref="Proceed"/> is <c>required</c>, no
/// default - mirrors <see cref="V12Cleanup"/>'s own four methods, each of which takes a required
/// <c>bool proceed</c> with no default. A request body that omits <c>"proceed"</c> fails to bind
/// (ASP.NET Core's minimal-API JSON binding rejects a missing required member) rather than silently
/// defaulting to false or true - the wire-level twin of the C# rule.</summary>
public sealed record MigrationCleanupRequest
{
    [JsonPropertyName("proceed")] public required bool Proceed { get; init; }
}

/// <summary>
/// The missing caller: <c>/control/migration/*</c> exposes <see cref="V12Detector"/>,
/// <see cref="V12Importer"/>, and <see cref="V12Cleanup"/> (Plan 14 Tasks 2-4) over the control API -
/// see this project's own standing check ("name the caller before calling a capability done").
/// Before this file, nothing under <c>Hades.Server.Control</c> referenced any of the three, so
/// migration - fully built, fully tested (79 tests) - could not run at all.
///
/// <b>Detection is read-only and safe to call at any time.</b> <see cref="Detect"/> only ever calls
/// <see cref="V12Detector.Detect"/>, which itself never writes, moves, or deletes anything (proved by
/// that class's own byte-and-mtime-identical test) - there is no destructive path here to gate.
///
/// <b>Import is mandatory-safe, not gated behind a <c>proceed</c> flag.</b>
/// <see cref="ImportMemory"/>/<see cref="ImportTraces"/> call <see cref="V12Importer"/> directly, with
/// no confirmation parameter of its own, because the core methods themselves are non-destructive by
/// construction: the source is only ever read, and an existing app-side document or traces.db is
/// reported as a skip, never overwritten. Spec #4 §5 marks memory import "Optional? No" for exactly
/// this reason - there is nothing here that can lose data, so there is nothing to ask permission for.
///
/// <b>Cleanup stays five independent routes, matching <see cref="V12Cleanup"/> exactly - there is no
/// "clean everything" route here either.</b> <see cref="CleanClaudeMd"/>, <see cref="CleanManifest"/>,
/// <see cref="CleanMcpConfig"/>, <see cref="CleanClaudeDesktopConfig"/>, and <see cref="CleanHadesHub"/>
/// each take their own <see cref="MigrationCleanupRequest"/> with its own required
/// <see cref="MigrationCleanupRequest.Proceed"/> - calling one never performs another, and refusing
/// one never blocks the rest, the identical contract <see cref="V12Cleanup"/>'s own class doc comment
/// describes for its five methods. Spec #10: "Migration is always offered, never performed silently."
///
/// <b><see cref="CleanClaudeMd"/> re-detects the file's marker state fresh on every call, rather than
/// trusting a client-supplied one.</b> <see cref="V12Cleanup.CleanClaudeMd"/> takes a
/// <see cref="ClaudeMdState"/> parameter, but round-tripping that through the wire would mean shipping
/// raw character offsets to a client that has no legitimate use for them (see
/// <see cref="MigrationClaudeMdInfo"/>'s own doc comment) and would reintroduce exactly the staleness
/// risk <see cref="V12Cleanup.CleanClaudeMd"/>'s own re-validation already defends against for a
/// stale/mismatched caller-supplied state - re-detecting here means the state handed to
/// <see cref="V12Cleanup.CleanClaudeMd"/> always describes the file as it exists at the moment of the
/// call, not as it existed whenever some earlier <see cref="Detect"/> call happened to run.
///
/// <b><see cref="CleanClaudeDesktopConfig"/> carries no <c>{productGuid}</c>, and no project
/// parameter of any kind.</b> <c>claude_desktop_config.json</c> is global and per-user, not
/// per-project (spec #4 §5) - putting it on a per-project route would misstate its scope. Production
/// always resolves <see cref="V12Cleanup.ClaudeDesktopConfigPath"/> itself (see
/// <see cref="CleanClaudeDesktopConfig"/>'s own <c>configPath</c> parameter doc comment for the
/// test-only seam that lets this be proven without ever touching the real file).
///
/// <b><see cref="CleanHadesHub"/> likewise carries no <c>{productGuid}</c>.</b>
/// <c>~/.arcforge/hades-hub/</c> - the retired v1.2 stdio launcher and its hub state - is global and
/// per-user, not per-project either: spec #4 §1 names <c>~/.arcforge/hades-hub/launcher.js</c>
/// explicitly among what v2 retires, and <see cref="V12Cleanup.CleanHadesHub"/>'s own doc comment
/// explains why cleanup here follows the launcher's whole directory rather than that one file alone.
/// Production always resolves <see cref="V12Cleanup.HadesHubDirectory"/> itself (see
/// <see cref="CleanHadesHub"/>'s own <c>hadesHubDirectory</c> parameter doc comment for the
/// test-only seam that lets this be proven without ever touching the developer's real directory).
///
/// <b>Every route's body runs through <see cref="TryRun"/>.</b> Before this fix, the only
/// structured error response anywhere in this class was <see cref="UnknownProject"/>'s 404 - any
/// OTHER failure (a JSON call throwing from underneath, two overlapping imports racing the same
/// destination file - see the concurrency remarks below) surfaced as ASP.NET's own bare, possibly
/// body-less default 500, a completely different shape than every deliberate error path here uses.
/// <see cref="TryRun"/> closes that gap uniformly, the same role <see cref="MemoryEndpoint.TryRun"/>
/// already plays for that endpoint (narrower there - just <see cref="ArgumentException"/> - because
/// unsafe filenames are that endpoint's one anticipated failure mode; broader here, matching
/// <see cref="OperationRegistry.Start"/>'s own top-level catch-all for a long action, because a
/// migration route's underlying call can fail for reasons this class does not fully control - e.g.
/// <see cref="Memory.MemoryStore.ImportFromArcforge"/> propagating an exception for one unreadable
/// source file mid-import; the FULL fix for that skip-and-continue behaviour lives in that method
/// itself, in <c>Hades.Core/Memory/MemoryStore.cs</c>, outside every file this endpoint owns, but no
/// caller of THIS route should see an unexplained 500 either way).
///
/// <b>At most one import in flight per project.</b> <see cref="ImportMemory"/>'s and
/// <see cref="ImportTraces"/>' own destination checks ("does this file already exist") are each a
/// check-then-act pair, not atomic - two callers racing the SAME project can both pass that check
/// before either has written, and the loser's own file copy then throws, which used to surface as
/// exactly the bare 500 <see cref="TryRun"/> now heads off. <see cref="TryClaimImportGate"/>/
/// <see cref="ReleaseImportGate"/> serialise per project instead: a second import for a project
/// already mid-import gets a clean 409 rather than racing the first one's writes at all.
/// </summary>
public static class MigrationEndpoint
{
    // ------------------------------------------------------------------------------------- GET

    /// <summary><c>GET /control/migration/{productGuid}/detect</c>. 404 when the guid is unknown -
    /// same convention as every other per-project Control route (see
    /// <see cref="ProjectsEndpoint.Remove"/>).</summary>
    public static IResult Detect(ProjectService projects, string productGuid)
    {
        var project = projects.Get(productGuid);
        if (project is null) return UnknownProject(productGuid);

        return TryRun(() => Results.Json(ToWire(V12Detector.Detect(project.Path))));
    }

    // --------------------------------------------------------------------------------- import

    /// <summary><c>POST /control/migration/{productGuid}/importMemory</c>. No request body: see
    /// this class's own doc comment for why memory import needs no <c>proceed</c> gate. 409 when an
    /// import for this project is already in flight - see this class's own doc comment on
    /// <see cref="TryClaimImportGate"/>.</summary>
    public static IResult ImportMemory(ProjectService projects, string productGuid)
    {
        var project = projects.Get(productGuid);
        if (project is null) return UnknownProject(productGuid);

        if (!TryClaimImportGate(productGuid)) return ImportAlreadyInProgress(productGuid);
        try
        {
            return TryRun(() =>
            {
                var result = new V12Importer(projects.Paths).ImportMemory(productGuid, project.Path);
                return Results.Json(new MigrationMemoryImportResult
                {
                    Imported = result.Imported,
                    Skipped = result.Skipped.Select(s => new MigrationMemorySkip { Source = s.Source, Reason = s.Reason }).ToList(),
                });
            });
        }
        finally
        {
            ReleaseImportGate(productGuid);
        }
    }

    /// <summary><c>POST /control/migration/{productGuid}/importTraces</c>. No request body - see
    /// <see cref="ImportMemory"/>'s own doc comment; the same reasoning applies (never overwrites,
    /// so there is nothing to confirm; 409 when an import for this project is already in
    /// flight).</summary>
    public static IResult ImportTraces(ProjectService projects, string productGuid)
    {
        var project = projects.Get(productGuid);
        if (project is null) return UnknownProject(productGuid);

        if (!TryClaimImportGate(productGuid)) return ImportAlreadyInProgress(productGuid);
        try
        {
            return TryRun(() =>
            {
                var result = new V12Importer(projects.Paths).ImportTraces(productGuid, project.Path);
                return Results.Json(new MigrationTracesImportResult { Imported = result.Imported, SkippedReason = result.SkippedReason });
            });
        }
        finally
        {
            ReleaseImportGate(productGuid);
        }
    }

    // -------------------------------------------------------------------------------- cleanup

    /// <summary><c>POST /control/migration/{productGuid}/cleanClaudeMd</c>. See this class's own
    /// doc comment for why the marker state is re-detected here rather than accepted from the
    /// caller.</summary>
    public static IResult CleanClaudeMd(ProjectService projects, string productGuid, MigrationCleanupRequest request)
    {
        var project = projects.Get(productGuid);
        if (project is null) return UnknownProject(productGuid);

        return TryRun(() =>
        {
            var state = V12Detector.Detect(project.Path).ClaudeMd;
            var result = V12Cleanup.CleanClaudeMd(project.Path, state, request.Proceed);

            return Results.Json(new MigrationClaudeMdCleanupResult
            {
                Removed = result.Removed,
                Message = result.Message,
                RemainingContentOutsideBlock = result.RemainingContentOutsideBlock,
            });
        });
    }

    /// <summary><c>POST /control/migration/{productGuid}/cleanManifest</c>.</summary>
    public static IResult CleanManifest(ProjectService projects, string productGuid, MigrationCleanupRequest request)
    {
        var project = projects.Get(productGuid);
        if (project is null) return UnknownProject(productGuid);

        return TryRun(() =>
        {
            var result = V12Cleanup.CleanManifest(project.Path, request.Proceed);
            return Results.Json(new MigrationManifestCleanupResult
            {
                Removed = result.Removed,
                Message = result.Message,
                OccurrencesFound = result.OccurrencesFound,
                PortConflictWarning = result.PortConflictWarning,
            });
        });
    }

    /// <summary><c>POST /control/migration/{productGuid}/cleanMcpConfig</c>.</summary>
    public static IResult CleanMcpConfig(ProjectService projects, string productGuid, MigrationCleanupRequest request)
    {
        var project = projects.Get(productGuid);
        if (project is null) return UnknownProject(productGuid);

        return TryRun(() =>
        {
            var result = V12Cleanup.CleanMcpConfig(project.Path, request.Proceed);
            return Results.Json(new MigrationMcpConfigCleanupResult
            {
                Removed = result.Removed,
                Message = result.Message,
                OccurrencesFound = result.OccurrencesFound,
            });
        });
    }

    /// <summary><c>POST /control/migration/claudeDesktopConfig/clean</c> - see this class's own doc
    /// comment for why this route carries no <c>{productGuid}</c> at all.</summary>
    /// <param name="request">The wire body - carries only <see cref="MigrationCleanupRequest.Proceed"/>,
    /// never a path: a caller cannot redirect this call to a different file.</param>
    /// <param name="configPath">Test-only seam, threaded from <see cref="ControlListener"/>'s own
    /// <c>claudeDesktopConfigPath</c> constructor parameter. Production never supplies this (stays
    /// null), so <see cref="V12Cleanup.ClaudeDesktopConfigPath"/> - the real machine path - is what
    /// actually gets touched; tests supply a scratch path so this route can be proven end to end over
    /// real HTTP without any risk to the developer's own Claude Desktop configuration.</param>
    public static IResult CleanClaudeDesktopConfig(MigrationCleanupRequest request, string? configPath = null)
    {
        return TryRun(() =>
        {
            var result = V12Cleanup.CleanClaudeDesktopConfig(configPath ?? V12Cleanup.ClaudeDesktopConfigPath, request.Proceed);
            return Results.Json(new MigrationClaudeDesktopConfigCleanupResult
            {
                Removed = result.Removed,
                Message = result.Message,
                ScopeWarning = result.ScopeWarning,
                OccurrencesFound = result.OccurrencesFound,
            });
        });
    }

    /// <summary><c>POST /control/migration/hadesHub/clean</c> - see this class's own doc comment for
    /// why this route carries no <c>{productGuid}</c> at all.</summary>
    /// <param name="request">The wire body - carries only <see cref="MigrationCleanupRequest.Proceed"/>,
    /// never a path: a caller cannot redirect this call to a different directory.</param>
    /// <param name="hadesHubDirectory">Test-only seam, threaded from <see cref="ControlListener"/>'s
    /// own <c>hadesHubDirectory</c> constructor parameter. Production never supplies this (stays
    /// null), so <see cref="V12Cleanup.HadesHubDirectory"/> - the real machine path - is what
    /// actually gets touched; tests supply a scratch directory so this route can be proven end to
    /// end over real HTTP without any risk to the developer's own <c>~/.arcforge/hades-hub/</c>.</param>
    public static IResult CleanHadesHub(MigrationCleanupRequest request, string? hadesHubDirectory = null)
    {
        return TryRun(() =>
        {
            var result = V12Cleanup.CleanHadesHub(hadesHubDirectory ?? V12Cleanup.HadesHubDirectory, request.Proceed);
            return Results.Json(new MigrationHadesHubCleanupResult
            {
                Removed = result.Removed,
                Message = result.Message,
                Found = result.Found,
            });
        });
    }

    // ------------------------------------------------------------------------- import concurrency

    /// <summary>
    /// Tracks which projects currently have an import in flight - see this class's own doc comment
    /// ("at most one import in flight per project") for why. A single gate shared by
    /// <see cref="ImportMemory"/> and <see cref="ImportTraces"/> rather than one per route: the two
    /// never touch the same destination file (see <see cref="V12Importer"/>'s own remarks on why
    /// memory and traces are independent), so sharing the gate is a deliberately conservative
    /// simplification - at worst a memory import and a traces import for the SAME project cannot run
    /// concurrently either, which costs nothing real (both are fast, local, one-shot copies) in
    /// exchange for one gate instead of two to reason about.
    ///
    /// <para>Exposed as its own testable primitive (<see cref="TryClaimImportGate"/>/
    /// <see cref="ReleaseImportGate"/>) rather than folded invisibly into <see cref="ImportMemory"/>/
    /// <see cref="ImportTraces"/>, so a test can prove the 409 path deterministically - claim the
    /// gate directly, make the HTTP call, assert 409, release, assert the next call succeeds -
    /// instead of racing real threads against file copies too fast to reliably overlap.</para>
    /// </summary>
    static readonly ConcurrentDictionary<string, byte> ImportsInProgress = new();

    /// <summary>Claims the per-project import gate. True on success - the caller MUST release it via
    /// <see cref="ReleaseImportGate"/>, in a <c>finally</c> block; false when another import for the
    /// same project is already in flight.</summary>
    public static bool TryClaimImportGate(string productGuid) => ImportsInProgress.TryAdd(productGuid, 0);

    /// <summary>Releases the per-project import gate. Safe to call even if the gate was never
    /// claimed (or was already released) - never throws.</summary>
    public static void ReleaseImportGate(string productGuid) => ImportsInProgress.TryRemove(productGuid, out _);

    static IResult ImportAlreadyInProgress(string productGuid) =>
        Results.Json(
            new { error = $"An import is already running for project '{productGuid}'. Wait for it to finish and try again." },
            statusCode: StatusCodes.Status409Conflict);

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>
    /// Runs <paramref name="action"/>, turning ANY otherwise-unhandled exception into the same
    /// resolved <c>{ "error": "..." }</c> JSON shape every other failure path in this class already
    /// uses (see <see cref="UnknownProject"/>) - never a bare, framework-default, possibly body-less
    /// 500. See this class's own doc comment ("every route's body runs through TryRun") for why a
    /// broad <c>catch (Exception)</c> is deliberate here, the same as
    /// <see cref="OperationRegistry.Start"/>'s own top-level catch for a long-running action: this is
    /// the outermost boundary of a migration request, so every failure mode collapses to the same
    /// "report it, do not crash the request" answer, with the triggering exception's own message
    /// surfaced verbatim - the same "specific and actionable" standard <see cref="OperationRegistry"/>
    /// already holds itself to.
    /// </summary>
    static IResult TryRun(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = $"Migration operation failed: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    static IResult UnknownProject(string productGuid) =>
        Results.Json(new { error = $"Unknown project '{productGuid}'." }, statusCode: StatusCodes.Status404NotFound);

    static MigrationDetectionResult ToWire(V12DetectionResult r) => new()
    {
        ProjectRoot = r.ProjectRoot,
        ProjectRootExists = r.ProjectRootExists,
        IsV12Project = r.IsV12Project,
        ManifestEntry = new MigrationManifestEntryInfo
        {
            Present = r.ManifestEntry.Present,
            Value = r.ManifestEntry.Value,
            ResolvedPath = r.ManifestEntry.ResolvedPath,
        },
        HasMemory = r.HasMemory,
        MemoryDocumentCount = r.MemoryDocumentCount,
        HasTraces = r.HasTraces,
        HasGraph = r.HasGraph,
        HasGeneratedMcpConfig = r.HasGeneratedMcpConfig,
        ClaudeMd = new MigrationClaudeMdInfo { Shape = ToWire(r.ClaudeMd.Shape) },
        HasUnityPlugin = r.HasUnityPlugin,
    };

    static MigrationClaudeMdShape ToWire(ClaudeMdShape shape) => shape switch
    {
        ClaudeMdShape.Absent => MigrationClaudeMdShape.Absent,
        ClaudeMdShape.Marked => MigrationClaudeMdShape.Marked,
        ClaudeMdShape.Unmarked => MigrationClaudeMdShape.Unmarked,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown ClaudeMdShape."),
    };
}
