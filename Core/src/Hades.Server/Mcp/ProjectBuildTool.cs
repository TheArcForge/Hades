using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Editors;
using Hades.Server.Control;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WireJson = Hades.Contract.Wire.JsonValue;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Mcp;

/// <summary>One structured error/exception/assert message from a build's <c>BuildReport</c> -
/// DESIGN-03 §12 layer 2, capped by the plugin to 50 entries of 2000 characters each before it ever
/// reaches the wire (see <c>errorsTruncated</c>).</summary>
public sealed record BuildMessageResult
{
    [JsonPropertyName("step")] public required string Step { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("content")] public required string Content { get; init; }
}

/// <summary>One <c>BuildTarget</c> this Editor installation was asked about - action='targets'.</summary>
public sealed record BuildTargetResult
{
    [JsonPropertyName("target")] public required string Target { get; init; }
    [JsonPropertyName("group")] public required string Group { get; init; }
    [JsonPropertyName("supported")] public required bool Supported { get; init; }
}

/// <summary>project_build's result, shaped by 'action' - see DESIGN-03 §3.3. BuildId/State/Target/
/// OutputPath/OperationId/DirtyScenes populated for 'start'; every field except Targets/HostPlatform
/// populated for 'status' (Errors/TotalErrors/TotalWarnings/OutputSizeBytes only once a terminal
/// state has actually been observed - see <see cref="ProjectBuildTool.MapDocumentToResult"/>); only
/// HostPlatform/Targets populated for 'targets'. Same action-shaped convention as
/// <see cref="ScriptEditingSessionResult"/> - never a parallel record per action.</summary>
public sealed record ProjectBuildResult
{
    [JsonPropertyName("buildId")] public string? BuildId { get; init; }

    /// <summary>queued|running|complete|failed|cancelled|abandoned|none|unknown - a plain string,
    /// not a closed enum (DESIGN-08 §5; DESIGN-03 §3.3 originally said 'succeeded', corrected to
    /// 'complete' to match the set-wide vocabulary).</summary>
    [JsonPropertyName("state")] public string? State { get; init; }

    [JsonPropertyName("target")] public string? Target { get; init; }
    [JsonPropertyName("outputPath")] public string? OutputPath { get; init; }

    /// <summary>The <see cref="OperationRegistry"/> id for 'start' - pollable via
    /// <c>GET /control/operations/{id}</c> for as long as the registry retains it. Null for 'status'
    /// when the core that started the build is no longer the one answering (a restart, or a
    /// different core instance) - the file remains the truth either way (DESIGN-03 §4.3, O6).</summary>
    [JsonPropertyName("operationId")] public string? OperationId { get; init; }

    [JsonPropertyName("note")] public string? Note { get; init; }

    [JsonPropertyName("elapsedSeconds")] public int? ElapsedSeconds { get; init; }
    [JsonPropertyName("totalErrors")] public int? TotalErrors { get; init; }
    [JsonPropertyName("totalWarnings")] public int? TotalWarnings { get; init; }
    [JsonPropertyName("outputSizeBytes")] public long? OutputSizeBytes { get; init; }
    [JsonPropertyName("errors")] public IReadOnlyList<BuildMessageResult>? Errors { get; init; }
    [JsonPropertyName("errorsTruncated")] public bool? ErrorsTruncated { get; init; }
    [JsonPropertyName("editorLogPath")] public string? EditorLogPath { get; init; }

    /// <summary>'start' only, DESIGN-03 §7.3: open scenes with unsaved changes at the moment the
    /// build was queued - disclosed, never saved.</summary>
    [JsonPropertyName("dirtyScenes")] public IReadOnlyList<string>? DirtyScenes { get; init; }

    [JsonPropertyName("hostPlatform")] public string? HostPlatform { get; init; }
    [JsonPropertyName("targets")] public IReadOnlyList<BuildTargetResult>? Targets { get; init; }
}

/// <summary>The wire-shaped result stored in <see cref="OperationRecord.Result"/> for a 'build'
/// operation - DESIGN-03 §4.3 item 4: the same fields as <see cref="ProjectBuildResult"/> minus
/// 'errors[]' (capped but still potentially large, and the shell/CLI observability this exists for
/// has no consumer for the per-message detail). <see cref="ProjectsEndpoint"/>'s own
/// <c>RebuildOperationResult</c> is the pattern this copies.</summary>
public sealed record BuildOperationResult
{
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("target")] public string? Target { get; init; }
    [JsonPropertyName("outputPath")] public string? OutputPath { get; init; }
    [JsonPropertyName("totalErrors")] public int? TotalErrors { get; init; }
    [JsonPropertyName("totalWarnings")] public int? TotalWarnings { get; init; }
    [JsonPropertyName("outputSizeBytes")] public long? OutputSizeBytes { get; init; }
    [JsonPropertyName("editorLogPath")] public string? EditorLogPath { get; init; }
}

/// <summary>The plugin-written status-file document, deserialized directly with
/// <see cref="JsonSerializer"/> (<see cref="ProjectBuildTool.StatusFileJsonOptions"/> makes this
/// case-insensitive against the plugin's own lowerCamelCase field names - the plugin writes this
/// with <c>MiniJson</c>, the core parses it with <c>System.Text.Json</c>: one more instance of the
/// duplicated-contract trade-off <c>Hades.Contract/Wire</c> already makes, accepted here for the same
/// reason - DESIGN-03 §4.2).</summary>
sealed record BuildStatusDocument
{
    public int Schema { get; init; }
    public string? BuildId { get; init; }
    public string? BootNonce { get; init; }
    public string? State { get; init; }
    public string? Target { get; init; }
    public string? Profile { get; init; }
    public string? OutputPath { get; init; }
    public long? QueuedAtUtcMs { get; init; }
    public long? StartedAtUtcMs { get; init; }
    public long? FinishedAtUtcMs { get; init; }
    public string? UnityVersion { get; init; }
    public string? EditorLogPath { get; init; }
    public int? TotalErrors { get; init; }
    public int? TotalWarnings { get; init; }
    public long? OutputSizeBytes { get; init; }
    public List<BuildStatusErrorEntry>? Errors { get; init; }
    public bool? ErrorsTruncated { get; init; }
    public string? Note { get; init; }
}

sealed record BuildStatusErrorEntry
{
    public string? Step { get; init; }
    public string? Type { get; init; }
    public string? Content { get; init; }
}

/// <summary>
/// DESIGN-03 (docs/design/DESIGN-03-build-pipeline.md): one new tool, <c>project_build</c>
/// (action='start'|'status'|'targets'). 'start' sends one wire command that validates, writes a
/// status file, defers the actual build to the next Editor tick, and returns inside milliseconds.
/// <b>'status' is answered entirely from disk, with NO Editor round trip</b> - the single most
/// important property of this design (DESIGN-03 §1 fact 3/4): a build pins the Editor's main
/// thread for minutes, so every OTHER wire-routed tool (a wire-routed 'status' included) would be
/// refused as busy for the whole duration. 'targets' asks the live Editor which BuildTargets this
/// installation can actually build - Hades never ships a host-OS matrix of its own.
///
/// <para><b>No 'project.build_status' wire method exists - that absence is the point.</b> This class
/// reads <c>&lt;projectRoot&gt;/Library/Hades/build-status.json</c> directly, the same file the
/// plugin's own <c>BuildWatchdog</c> reconciles (see UnityPlugin/Assets/Hades/Tools/BuildCommands.cs).</para>
///
/// <para><b><see cref="OperationRegistry"/> reuse (DESIGN-03 §4.3).</b> 'start' registers a
/// background poll task under kind "build" so the shell/CLI can observe an agent-started build for
/// free through machinery they already have. The registry's <see cref="OperationState"/> enum is
/// closed to three values (Running/Done/Failed); the lossy mapping this forces is DESIGN-03's own,
/// stated table: queued/running -&gt; Running, complete -&gt; Done, everything else (failed/
/// cancelled/abandoned) -&gt; Failed. <b>O6 (DESIGN-08):</b> this registry is a mirror for the
/// shell/CLI ONLY - 'status' above never consults it, and after a core restart 'start's own
/// operationId is simply gone; the file remains the one source of truth either way.</para>
///
/// <para><b>The poll task (DESIGN-03 §4.3 item 1/3).</b> Runs on a background thread-pool thread for
/// the whole build (a real stretch of <see cref="OperationRegistry"/>'s own seconds-long design
/// assumption, priced rather than hidden) at a slow (<see cref="PollInterval"/>) cadence via
/// <c>Task.Delay(...).GetAwaiter().GetResult()</c>, never a spin, and terminates the moment the
/// status file reaches ANY terminal state - abandonment included, so a crashed build's operation
/// record does not become a zombie the registry's 5-minute-past-completion retention can never
/// reach (only completion starts that clock).</para>
/// </summary>
[McpServerToolType]
public sealed class ProjectBuildTool(EditorProxy editor, ProjectService projects, OperationRegistry operations)
{
    /// <summary>How often the background poll task (DESIGN-03 §4.3 item 1) re-reads the status
    /// file. Slow and blocking-via-Task.Delay by design - see this class's own doc comment.</summary>
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>The newest status-file schema this Hades core understands - DESIGN-03 §4.2: "a core
    /// reading a file written by an older plugin can refuse cleanly rather than misparse". A file
    /// from a NEWER plugin (schema greater than this) is refused explicitly rather than silently
    /// misread.</summary>
    const int CurrentSchema = 1;

    static readonly JsonSerializerOptions StatusFileJsonOptions = new() { PropertyNameCaseInsensitive = true };

    static readonly string[] ForbiddenOutputRoots = { "Assets/", "Packages/", "Library/", "ProjectSettings/" };

    [McpServerTool(Name = "project_build", Title = "Build Player", ReadOnly = false, UseStructuredContent = true)]
    [Description("Builds a Unity Player. BLUNT COST, READ BEFORE CALLING action='start': a build "
               + "takes minutes to TENS OF MINUTES; it PINS this Editor's main thread for the whole "
               + "duration, so EVERY OTHER Hades tool reports the Editor busy until it finishes; and "
               + "it CANNOT BE CANCELLED once started (Unity exposes no API to abort it - only "
               + "force-quitting the Editor stops it, which loses unsaved work). action='start' "
               + "refuses for a target other than this Editor's own active build target - building "
               + "another one would silently and PERSISTENTLY switch the Editor's platform and "
               + "reimport the whole project; switch it in Unity first, or build the active one. "
               + "action='status' answers instantly from disk, even while the Editor is busy building "
               + "- poll it rather than assuming 'start' already finished. action='targets' asks the "
               + "live Editor which BuildTargets this installation can actually build. Configuration "
               + "is a Unity 6 Build Profile asset ('profile') or a bare 'target' for a default build "
               + "- Hades does not accept a synthesised configuration object. Never saves an open "
               + "scene - a dirty scene is reflected in the LAST SAVED state and disclosed via "
               + "'dirtyScenes', never silently written to disk.")]
    public async Task<ProjectBuildResult> ProjectBuild(
        [Description("'start', 'status', or 'targets'")] string action,
        [Description("start: a BuildTarget name exactly as action='targets' reports it, e.g. 'StandaloneOSX'. Required unless 'profile' is given. Must equal the Editor's active build target.")] string? target = null,
        [Description("start: project-relative path to a Unity 6 Build Profile asset, e.g. 'Assets/Build/Demo.asset'. Supplies target, scenes, and options; 'target' and 'development' are then refused as conflicting.")] string? profile = null,
        [Description("start: project-relative output path. Defaults to 'Build/<target>/'. Absolute paths, and paths resolving inside Assets/Packages/Library/ProjectSettings, are refused.")] string? outputPath = null,
        [Description("start: development build (BuildOptions.Development). Default false.")] bool? development = null,
        [Description("status: the buildId 'start' returned. Omit to report the most recent build for this project.")] string? buildId = null,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        EditorComponentTools.RequireNonBlank(action, nameof(action), "project_build");

        switch (action.Trim().ToLowerInvariant())
        {
            case "start": return await StartAsync(target, profile, outputPath, development, project, context).ConfigureAwait(false);
            case "status": return await StatusAsync(buildId, project, context).ConfigureAwait(false);
            case "targets": return await TargetsAsync(project, context).ConfigureAwait(false);
            default:
                throw new McpException($"project_build's 'action' must be 'start', 'status', or 'targets', got '{action}'.");
        }
    }

    // ---------------------------------------------------------------- start

    async Task<ProjectBuildResult> StartAsync(string? target, string? profile, string? outputPath, bool? development,
        string? project, RequestContext<CallToolRequestParams> context)
    {
        if (string.IsNullOrEmpty(target) && string.IsNullOrEmpty(profile))
            throw new McpException("project_build's 'start' action requires either 'target' or 'profile'.");
        if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(profile))
            throw new McpException("project_build: 'target' and 'profile' are mutually exclusive - a Build Profile already supplies its own target.");
        if (!string.IsNullOrEmpty(profile) && development.HasValue)
            throw new McpException("project_build: 'development' and 'profile' are mutually exclusive - the profile's own build options apply.");

        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var projectRecord = projects.Get(productGuid) ?? throw new McpException($"Unknown project '{productGuid}'.");

        // Refused BEFORE any wire call - DESIGN-03 §11.1, mirroring ProjectSettingsApplyTool's own
        // "unknown op refused before anything is sent" rule.
        string? canonicalOutputPath = null;
        if (!string.IsNullOrEmpty(outputPath))
            canonicalOutputPath = ValidateOutputPathContainment(projectRecord.Path, outputPath);

        var @params = WireJson.NewObject();
        if (!string.IsNullOrEmpty(target)) @params.SetProperty("target", WireJson.String(target));
        if (!string.IsNullOrEmpty(profile)) @params.SetProperty("profile", WireJson.String(profile));
        if (canonicalOutputPath != null) @params.SetProperty("outputPath", WireJson.String(canonicalOutputPath));
        if (development.HasValue) @params.SetProperty("development", WireJson.Bool(development.Value));

        var result = await editor.SendCommandAsync(productGuid, "project.build_start", @params).ConfigureAwait(false);

        var startedBuildId = EditorComponentTools.Str(result, "buildId");
        var startedState = EditorComponentTools.Str(result, "state");
        var startedTarget = EditorComponentTools.Str(result, "target");
        var startedOutputPath = EditorComponentTools.Str(result, "outputPath");
        var dirtyScenes = ReadStringArray(result, "dirtyScenes");

        var projectPath = projectRecord.Path;
        var operationId = operations.Start("build", productGuid, _ => PollUntilTerminal(projectPath, startedBuildId));

        return new ProjectBuildResult
        {
            BuildId = startedBuildId,
            State = startedState,
            Target = startedTarget,
            OutputPath = startedOutputPath,
            OperationId = operationId,
            DirtyScenes = dirtyScenes,
        };
    }

    /// <summary>The background poll task <see cref="OperationRegistry.Start(string,string?,Func{Action{string},object?})"/>
    /// runs - re-reads the status file at <see cref="PollInterval"/> until it reaches a terminal
    /// state FOR THIS BUILD ID specifically (never an earlier or later build that might occupy the
    /// same file), then maps it to the wire-shaped <see cref="BuildOperationResult"/>
    /// <see cref="OperationRecord.Result"/> requires. Blocks its own thread-pool thread for as long
    /// as the build runs - the accepted price, DESIGN-03 §4.3 item 1.</summary>
    static BuildOperationResult PollUntilTerminal(string projectPath, string buildId)
    {
        var statusPath = StatusPathFor(projectPath);

        while (true)
        {
            var doc = TryReadStatusFile(statusPath);
            if (doc != null && doc.BuildId == buildId && IsTerminal(doc.State))
            {
                return new BuildOperationResult
                {
                    State = doc.State!,
                    Target = doc.Target,
                    OutputPath = doc.OutputPath,
                    TotalErrors = doc.TotalErrors,
                    TotalWarnings = doc.TotalWarnings,
                    OutputSizeBytes = doc.OutputSizeBytes,
                    EditorLogPath = doc.EditorLogPath,
                };
            }

            Task.Delay(PollInterval).GetAwaiter().GetResult();
        }
    }

    // ---------------------------------------------------------------- status

    /// <summary>DESIGN-03 §1 fact 3/§4.1: makes NO wire call at all - this is the whole point of
    /// the status-file design (proven directly in ProjectBuildTests by scripting the fake Editor to
    /// fail any request and showing this still answers).</summary>
    async Task<ProjectBuildResult> StatusAsync(string? buildId, string? project, RequestContext<CallToolRequestParams> context)
    {
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var projectRecord = projects.Get(productGuid) ?? throw new McpException($"Unknown project '{productGuid}'.");

        var statusPath = StatusPathFor(projectRecord.Path);

        BuildStatusDocument? doc;
        try
        {
            doc = TryReadStatusFile(statusPath);
        }
        catch (JsonException ex)
        {
            throw new McpException(
                $"project_build: the status file for this project ('{statusPath}') exists but could not be "
                + $"parsed as JSON ({ex.Message}). If this project was just built by a much older or much "
                + "newer Hades plugin, this may be a genuine format mismatch rather than a transient write race.");
        }

        if (doc == null)
        {
            return new ProjectBuildResult
            {
                State = "none",
                Note = "No build has been started for this project this Editor session. Call project_build with action 'start' first.",
            };
        }

        if (doc.Schema > CurrentSchema)
        {
            throw new McpException(
                $"project_build: this project's build status was written by a plugin newer than this Hades "
                + $"core understands (schema {doc.Schema}, this core knows up to schema {CurrentSchema}). "
                + "Update Hades to read it.");
        }

        if (!string.IsNullOrEmpty(buildId) && buildId != doc.BuildId)
        {
            return new ProjectBuildResult
            {
                State = "unknown",
                BuildId = buildId,
                Note = $"No build with id '{buildId}' is known. The most recently started build's id is "
                     + $"'{doc.BuildId}' - omit 'buildId' to poll it, or call project_build with action "
                     + "'start' to begin a new build.",
            };
        }

        return MapDocumentToResult(doc);
    }

    /// <summary>Shared by <see cref="StatusAsync"/> and (indirectly, via
    /// <see cref="BuildOperationResult"/>'s own smaller mapping) the poll task. 'errors'/
    /// 'totalErrors'/'totalWarnings'/'outputSizeBytes' come through exactly as the plugin wrote them
    /// - null until a build actually reaches a terminal state, since that is the only point at which
    /// the plugin itself knows them (DESIGN-03 §12).</summary>
    static ProjectBuildResult MapDocumentToResult(BuildStatusDocument doc)
    {
        var startedOrQueuedMs = doc.StartedAtUtcMs ?? doc.QueuedAtUtcMs;
        int? elapsedSeconds = null;
        if (startedOrQueuedMs.HasValue)
        {
            var started = DateTimeOffset.FromUnixTimeMilliseconds(startedOrQueuedMs.Value);
            var end = doc.FinishedAtUtcMs.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(doc.FinishedAtUtcMs.Value)
                : DateTimeOffset.UtcNow;
            elapsedSeconds = Math.Max(0, (int)Math.Round((end - started).TotalSeconds));
        }

        List<BuildMessageResult>? errors = null;
        if (doc.Errors is { Count: > 0 })
        {
            errors = doc.Errors
                .Select(e => new BuildMessageResult { Step = e.Step ?? "", Type = e.Type ?? "", Content = e.Content ?? "" })
                .ToList();
        }

        return new ProjectBuildResult
        {
            BuildId = doc.BuildId,
            State = doc.State,
            Target = doc.Target,
            OutputPath = doc.OutputPath,
            Note = doc.Note,
            ElapsedSeconds = elapsedSeconds,
            TotalErrors = doc.TotalErrors,
            TotalWarnings = doc.TotalWarnings,
            OutputSizeBytes = doc.OutputSizeBytes,
            Errors = errors,
            ErrorsTruncated = doc.ErrorsTruncated,
            EditorLogPath = doc.EditorLogPath,
        };
    }

    // ---------------------------------------------------------------- targets

    async Task<ProjectBuildResult> TargetsAsync(string? project, RequestContext<CallToolRequestParams> context)
    {
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);
        var result = await editor.SendCommandAsync(productGuid, "project.build_targets").ConfigureAwait(false);

        var targetsList = new List<BuildTargetResult>();
        if (result.TryGetProperty("targets", out var targetsJson) && targetsJson!.Kind == WireKind.Array)
        {
            foreach (var t in targetsJson.Items)
            {
                targetsList.Add(new BuildTargetResult
                {
                    Target = EditorComponentTools.Str(t, "target"),
                    Group = EditorComponentTools.Str(t, "group"),
                    Supported = t.TryGetProperty("supported", out var s) && s!.Kind == WireKind.Boolean && s.AsBoolean(),
                });
            }
        }

        return new ProjectBuildResult
        {
            HostPlatform = EditorComponentTools.Str(result, "hostPlatform"),
            Targets = targetsList,
        };
    }

    // ---------------------------------------------------------------- shared

    static bool IsTerminal(string? state) =>
        state is "complete" or "failed" or "cancelled" or "abandoned";

    static string StatusPathFor(string projectRoot) =>
        Path.Combine(projectRoot, "Library", "Hades", "build-status.json");

    /// <summary>Never throws for a missing or unreadable-right-now file (the same "not ready yet,
    /// retry" posture <c>HadesConnectionFile.TryRead</c> and the plugin's own
    /// <c>BuildStatusStore.TryRead</c> take) - but DOES let a genuine <see cref="JsonException"/>
    /// (malformed content) propagate, so <see cref="StatusAsync"/> can turn it into a specific,
    /// actionable message rather than a silent "none". <see cref="FileShare.ReadWrite"/> |
    /// <see cref="FileShare.Delete"/> - DESIGN-03 §4.2.2/§11: on Windows an antivirus hold on the
    /// plugin's own temp-then-replace write is a transient READ failure here, not a parse failure,
    /// and must route back through the same retry-by-polling path as a plain "not written yet" -
    /// same idiom <c>Hades.Core.Projects.ProjectStore</c>'s own <c>ReadAllTextShared</c> uses.</summary>
    static BuildStatusDocument? TryReadStatusFile(string statusPath)
    {
        string text;
        try
        {
            using var stream = new FileStream(statusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null; // missing, or caught mid-write (a rename in flight) - not ready yet
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text)) return null; // caught between the temp write and the rename

        return JsonSerializer.Deserialize<BuildStatusDocument>(text, StatusFileJsonOptions);
    }

    /// <summary>DESIGN-03 §6/§11.1: 'outputPath' is project-relative only; absolute paths, traversal
    /// outside the project, and anything resolving inside Assets/Packages/Library/ProjectSettings
    /// are refused HERE, before any wire call - mirrors the containment idiom
    /// <c>Hades.Core.Storage.AppPaths.ProjectDir</c> already establishes (GetFullPath both sides,
    /// then an Ordinal StartsWith), and is duplicated (defence in depth) by the plugin's own
    /// <c>ValidateOutputPath</c>, which is the one that would actually be asked to write outside the
    /// project if this check were ever bypassed.</summary>
    static string ValidateOutputPathContainment(string projectRoot, string outputPath)
    {
        if (string.IsNullOrEmpty(outputPath) || outputPath.Trim() != outputPath)
        {
            throw new McpException(
                $"project_build: 'outputPath' must be a plain, non-empty project-relative path with no "
                + $"leading/trailing whitespace - refused '{outputPath}'.");
        }

        var root = Path.GetFullPath(projectRoot);
        var rootWithSep = root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, outputPath));

        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new McpException(
                $"project_build: 'outputPath' must be a project-relative path inside this project - refused '{outputPath}'.");
        }

        var relative = full[rootWithSep.Length..].Replace(Path.DirectorySeparatorChar, '/');

        foreach (var forbidden in ForbiddenOutputRoots)
        {
            if (relative == forbidden.TrimEnd('/') || relative.StartsWith(forbidden, StringComparison.Ordinal))
            {
                throw new McpException(
                    $"project_build: 'outputPath' must not resolve inside '{forbidden.TrimEnd('/')}' - a "
                    + $"player written there would be destructive (e.g. imported as an asset). Refused '{outputPath}'.");
            }
        }

        return relative;
    }

    static IReadOnlyList<string>? ReadStringArray(WireJson value, string key)
    {
        if (!value.TryGetProperty(key, out var array) || array is not { Kind: WireKind.Array }) return null;

        var result = new List<string>();
        foreach (var item in array.Items)
            if (item is { Kind: WireKind.String }) result.Add(item.AsString());
        return result;
    }
}
