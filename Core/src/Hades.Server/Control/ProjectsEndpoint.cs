using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Editors;
using Hades.Core.Projects;
using Hades.Core.Reading;

// No dedicated AssemblyInfo.cs in this project, so this lives on the one method it exists for:
// lets Hades.Server.Tests exercise UnityHubEditorExecutablePath (internal, not public - see that
// method's own doc comment) directly instead of only through OpenInUnity's public surface.
[assembly: InternalsVisibleTo("Hades.Server.Tests")]

namespace Hades.Server.Control;

/// <summary>One attached-Editor state a project row can be in - see
/// <see cref="ProjectsEndpoint.Resolve"/> for how it is decided. The shell maps this straight to
/// an indicator and does nothing else, same rule as <see cref="ControlIconState"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectEditorState
{
    [JsonStringEnumMemberName("attached")] Attached,
    [JsonStringEnumMemberName("busy")] Busy,
    [JsonStringEnumMemberName("absent")] Absent,
}

/// <summary>One project's index state - see <see cref="ProjectsEndpoint.Resolve"/>.
/// <see cref="Indexing"/> is the same honest proxy <see cref="SummaryEndpoint"/> uses for
/// <see cref="ControlIconState.Indexing"/>: "never completed an index in this process", not a
/// live progress signal nothing in <see cref="Hades.Core.Observation.ObservationService"/> exposes
/// yet.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectIndexState
{
    [JsonStringEnumMemberName("indexed")] Indexed,
    [JsonStringEnumMemberName("indexing")] Indexing,
}

/// <summary>One resolved, human-readable warning about a project - see this file's own class doc
/// comment for the four warnings spec #3 §3.2 names and which of them this type's instances are
/// ever actually built from. <see cref="Code"/> is a plain string, not a closed enum: Plan 11 Task
/// 3 deliberately reserves room for a future <c>"oracleConformanceMismatch"</c> value (spec #1
/// §4.4) without this file needing to be touched again just to declare it.</summary>
public sealed record ProjectWarning
{
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("severity")] public required ControlSeverity Severity { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("remedy")] public required string Remedy { get; init; }
}

/// <summary>The attached-Editor half of a project row, fully resolved - see
/// <see cref="ProjectsEndpoint.Resolve"/>.</summary>
public sealed record ProjectEditorInfo
{
    [JsonPropertyName("state")] public required ProjectEditorState State { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }

    /// <summary>The live attached Editor's OWN reported version (Hello-derived) - present only
    /// when <see cref="State"/> is not <see cref="ProjectEditorState.Absent"/>. Deliberately
    /// separate from <see cref="ProjectRow.UnityVersion"/>: spec #3 §3.2 lists "Unity version" and
    /// "attached Editor state" as two different bullets, and spec #1 §6 lists Unity version as a
    /// per-ATTACHED-EDITOR fact - the two can disagree (a project last opened with one version,
    /// currently attached from a different one).</summary>
    [JsonPropertyName("unityVersion")] public string? UnityVersion { get; init; }

    [JsonPropertyName("processId")] public long? ProcessId { get; init; }
    [JsonPropertyName("connectionAgeSeconds")] public int? ConnectionAgeSeconds { get; init; }
}

/// <summary>One project's fully-resolved row for <c>GET /control/projects</c> - see
/// <see cref="ProjectsEndpoint"/>'s own class doc comment.</summary>
public sealed record ProjectRow
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("productGuid")] public required string ProductGuid { get; init; }

    /// <summary>The project's own last-known Unity version - the live attached Editor's version
    /// when one is attached (freshest truth), otherwise <c>ProjectSettings/ProjectVersion.txt</c>
    /// (<see cref="ProjectIdentity.TryReadUnityVersion"/>). Null only when neither source is
    /// available (never attached in this process AND no ProjectVersion.txt on disk yet).</summary>
    [JsonPropertyName("unityVersion")] public string? UnityVersion { get; init; }

    [JsonPropertyName("indexState")] public required ProjectIndexState IndexState { get; init; }
    [JsonPropertyName("indexStatus")] public required string IndexStatus { get; init; }
    [JsonPropertyName("nodeCount")] public required int NodeCount { get; init; }
    [JsonPropertyName("edgeCount")] public required int EdgeCount { get; init; }
    [JsonPropertyName("editor")] public required ProjectEditorInfo Editor { get; init; }
    [JsonPropertyName("warnings")] public required IReadOnlyList<ProjectWarning> Warnings { get; init; }
}

/// <summary>The full <c>GET /control/projects</c> response.</summary>
public sealed record ProjectsResult
{
    [JsonPropertyName("projects")] public required IReadOnlyList<ProjectRow> Projects { get; init; }
}

/// <summary>Body of <c>POST /control/projects/add</c>.</summary>
public sealed record AddProjectRequest
{
    [JsonPropertyName("path")] public required string Path { get; init; }
}

/// <summary>The common shape for an action whose only job is to say whether it worked - see
/// <see cref="ProjectsEndpoint.Remove"/>, <see cref="ProjectsEndpoint.RevealInFinder"/>, and
/// <see cref="ProjectsEndpoint.OpenInUnity"/>. <see cref="Message"/> is always the complete,
/// human-readable sentence to display - never a bare boolean for the shell to caption itself.</summary>
public sealed record ActionResult
{
    [JsonPropertyName("success")] public required bool Success { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
}

/// <summary>Response of <c>POST /control/projects/{id}/installPlugin</c> - see
/// <see cref="ProjectsEndpoint.InstallPluginAsync"/> for why <see cref="NeedsRestart"/> exists at
/// all.</summary>
public sealed record InstallPluginResult
{
    [JsonPropertyName("success")] public required bool Success { get; init; }
    [JsonPropertyName("needsRestart")] public required bool NeedsRestart { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
}

/// <summary>Response of <c>POST /control/projects/{id}/rebuild</c> - see
/// <see cref="ProjectsEndpoint.Rebuild"/>. <see cref="OperationId"/> is pollable via
/// <c>GET /control/operations/{id}</c> (Plan 11 Task 5 - see <see cref="Operations"/>) from the
/// moment this response returns.</summary>
public sealed record RebuildStartedResult
{
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }
}

/// <summary>The <c>result</c> payload of a completed <c>rebuild</c> operation, polled back via
/// <c>GET /control/operations/{id}</c> - see <see cref="ProjectsEndpoint.Rebuild"/>. A wire-shaped
/// mapping of <see cref="RebuildResult"/> (Hades.Core, no JsonPropertyName attributes of its own),
/// never that type embedded directly - same translation discipline every other Control response
/// follows (e.g. <see cref="ProjectsEndpoint.BuildRow"/> mapping <see cref="ProjectStateSnapshot"/>
/// to <see cref="ProjectRow"/>).</summary>
public sealed record RebuildOperationResult
{
    [JsonPropertyName("nodesBefore")] public required int NodesBefore { get; init; }
    [JsonPropertyName("nodesAfter")] public required int NodesAfter { get; init; }

    /// <summary>Plan 11 Task 7 audit fix: without this, a shell wanting to say "N nodes added" had
    /// to subtract <see cref="NodesBefore"/> from <see cref="NodesAfter"/> itself - exactly the
    /// "counts the client must combine" violation the audit looks for. Built by
    /// <see cref="ProjectsEndpoint.BuildRebuildMessage"/>, the same pure/tested pattern as every
    /// other resolved message in this API.</summary>
    [JsonPropertyName("message")] public required string Message { get; init; }
}

/// <summary>
/// One project's already-resolved state, the input <see cref="ProjectsEndpoint.Resolve"/> turns
/// into a <see cref="ProjectRow"/>. Same two-layer reasoning as <see cref="SummaryEndpoint"/>'s own
/// <c>ProjectSnapshot</c>: every field here is pre-derived so <see cref="ProjectsEndpoint.Resolve"/>
/// can be tested with plain data, no live project/editor/database state at all.
/// </summary>
public sealed record ProjectStateSnapshot
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string ProductGuid { get; init; }
    public string? UnityVersion { get; init; }
    public required bool PathExists { get; init; }
    public required bool Attached { get; init; }
    public required bool Busy { get; init; }
    public string? EditorUnityVersion { get; init; }
    public long? EditorProcessId { get; init; }
    public TimeSpan? ConnectionAge { get; init; }
    public DateTimeOffset? LastIndexedUtc { get; init; }
    public required int NodeCount { get; init; }
    public required int EdgeCount { get; init; }

    /// <summary>Unity's own raw <c>m_SerializationMode</c> value - 0 (Mixed), 1 (Force Binary), 2
    /// (Force Text) - or null when unknown (unreadable, or the project path does not exist).
    /// See this file's own class doc comment for why this is read from
    /// <c>ProjectSettings/EditorSettings.asset</c>, not <c>ProjectSettings.asset</c>.</summary>
    public int? SerializationMode { get; init; }

    /// <summary>The plugin version to compare against <see cref="AppPluginVersion"/>: the live
    /// attached Editor's own self-reported version when one is attached (freshest truth - spec #4
    /// §6, "the plugin reports its version on connect"), otherwise a file scan of
    /// <c>Assets/Hades/Runtime/HadesBoot.cs</c> (see <see cref="PluginInstaller.InstalledPluginVersion"/>).
    /// Same "live wins, else fall back to what is on disk" rule <see cref="UnityVersion"/> above
    /// already uses, and for the same reason. Null when neither is available - nothing attached
    /// AND the plugin is not installed in this project at all.</summary>
    public string? InstalledPluginVersion { get; init; }

    /// <summary>The version this APP would install - see
    /// <see cref="PluginInstaller.AppPluginVersion"/>. Always known in practice (the
    /// embedded resource ships with this assembly); null is a defensive allowance for a corrupt
    /// build, not an expected runtime state.</summary>
    public string? AppPluginVersion { get; init; }
}

/// <summary>
/// <c>GET /control/projects</c> and its six actions (<c>add</c>, <c>remove</c>, <c>rebuild</c>,
/// <c>installPlugin</c>, <c>revealInFinder</c>, <c>openInUnity</c>) - the Projects surface, spec #3
/// §3.2. Same two-layer split as <see cref="SummaryEndpoint"/>: <see cref="Resolve"/> is pure (a
/// <see cref="ProjectStateSnapshot"/> list plus "now" in, a <see cref="ProjectsResult"/> out - no
/// I/O), <see cref="BuildAsync"/> is the async orchestrator that gathers real state and hands it to
/// <see cref="Resolve"/>.
///
/// <b>The four warnings (spec #3 §3.2), and how each is actually detected:</b>
/// <list type="bullet">
/// <item><b>Force Binary or Mixed serialization.</b> Read from
/// <c>ProjectSettings/EditorSettings.asset</c>'s <c>m_SerializationMode</c> - see
/// <see cref="TryReadSerializationMode"/>. <b>Corrected from the plan text, which names
/// <c>ProjectSettings/ProjectSettings.asset</c>:</b> confirmed empirically against the real
/// Hades-Unity-Client checkout (<c>ProjectSettings/EditorSettings.asset:7: m_SerializationMode:
/// 2</c>; <c>ProjectSettings.asset</c> has no such key at all) and against Unity's own documented
/// behaviour - <c>m_SerializationMode</c> is an EDITOR preference (how Unity serializes OTHER
/// assets), not a PLAYER/project setting, so it lives in EditorSettings.asset. Implementing it
/// against the path the plan states would have made this - the plan's own words - "the most
/// important" warning never fire against any real project.</item>
/// <item><b>Plugin version mismatch (Plan 14 Task 5, spec #4 §6 - "degrade, never refuse").</b>
/// The live attached Editor's own self-reported version (<c>CharonStatus.PluginVersion</c>,
/// hello-derived - spec #4 §6: "the plugin reports its version on connect") when one is attached,
/// otherwise <see cref="PluginInstaller.InstalledPluginVersion"/> (reads the installed
/// <c>Assets/Hades/Runtime/HadesBoot.cs</c> on disk) - compared against
/// <see cref="PluginInstaller.AppPluginVersion"/> (reads the SAME embedded resource bytes
/// <c>PluginInstaller.Install</c> itself writes). Preferring the live value when attached is not
/// merely "freshest wins": it is what keeps this warning alive for an Editor that is STILL RUNNING
/// the old build right after <c>installPlugin</c> has already written the new bytes to disk (see
/// <see cref="InstallPluginResult.NeedsRestart"/>) - a file-scan-only comparison would wrongly read
/// "matches" the instant the new bytes land, before Unity has actually reloaded. Severity and
/// wording scale with <see cref="Editors.PluginVersionSkew"/>: a same-major skew, regardless of
/// direction or magnitude, keeps the ordinary "does not match" wording (matching what this app
/// already shipped for a two-minor-version gap before this task); a different major version
/// escalates the WORDING only, never the severity (still <see cref="ControlSeverity.Warning"/> -
/// some tools, and every file-derived one, still work regardless) and never refuses the underlying
/// connection at all (proved at the transport layer in EditorListenerTests, where Hello.PluginVersion
/// is never even inspected).</item>
/// <item><b>Path missing or volume unmounted.</b> <c>Directory.Exists(project.Path)</c> - the same
/// check <see cref="SummaryEndpoint"/> already uses for its own Error condition.</item>
/// <item><b>Oracle conformance mismatch.</b> RESERVED, per the plan: the check itself is spec #1
/// §4.4 and explicitly out of scope for this task. Nothing in <see cref="ProjectStateSnapshot"/>,
/// <see cref="BuildWarnings"/>, or the wire shape computes or fakes this - <see cref="ProjectWarning.Code"/>
/// is a plain string specifically so a future <c>"oracleConformanceMismatch"</c> value needs no
/// change here, only a new detector feeding <see cref="BuildWarnings"/>.</item>
/// </list>
///
/// <b>Design decisions the plan left to this task:</b>
/// <list type="bullet">
/// <item><b>Warning severity.</b> Force Binary and path-missing are <see cref="ControlSeverity.Error"/>
/// (both are total, unconditional failures - nothing under the project can be scanned, or the
/// project cannot be reached at all); Mixed serialization and a plugin mismatch are
/// <see cref="ControlSeverity.Warning"/> (both are partial/uncertain - Mixed may still leave many
/// assets readable depending on per-type overrides, and a mismatched plugin still runs, just not
/// verified against this app's current tool set).</item>
/// <item><b><c>remove</c> never deletes anything on disk - not even Hades' own project.json.</b>
/// Implemented as <see cref="Projects.UnityProject.Removed"/>, a flag REWRITTEN into project.json
/// (an update, never a delete) - see <see cref="Projects.ProjectStore.Remove"/>'s own doc comment.
/// <see cref="Projects.ProjectStore.All"/> excludes a removed project, which is what makes it
/// disappear from this endpoint and from <c>/control/summary</c> at once; memory, the graph
/// database, and project.json itself are all untouched, and re-<c>add</c>ing the same project
/// (re-<see cref="ProjectService.Adopt"/>, always Removed=false on a fresh record) makes it visible
/// again.</item>
/// <item><b><c>installPlugin</c>'s <see cref="InstallPluginResult.NeedsRestart"/>.</b> Plan 7
/// established that a plugin installed into an ALREADY-RUNNING Editor does not attach until
/// restart, cause unexplained. This endpoint checks <see cref="ProjectService.GetCharonStatus"/>
/// BEFORE installing - "was an Editor attached at the moment we wrote new plugin files" - and
/// reports true only then, since installing into a project with no Editor currently open needs no
/// restart at all (the plugin loads normally the next time Unity opens it).</item>
/// <item><b><c>rebuild</c>'s operation id.</b> Plan 11 Task 5 (<see cref="Operations"/>,
/// <see cref="OperationRegistry"/>) owns real operation tracking - <c>GET /control/operations/{id}</c>,
/// state (running/done/failed), progress, retention - and this action's id is pollable through it
/// from the moment this call returns: <see cref="Rebuild"/> registers the actual
/// <see cref="ProjectService.RebuildGraph"/> call with the SAME <see cref="OperationRegistry"/>
/// <see cref="ControlListener"/> serves <c>/control/operations</c> from (see that class's own
/// "operations" constructor parameter doc comment) - never the bare, un-awaited
/// <see cref="Task.Run(Action)"/> with no store behind it this action used before Task 5
/// existed.</item>
/// <item><b><c>openInUnity</c> assumes the default Unity Hub install location</b>
/// (<c>/Applications/Unity/Hub/Editor/&lt;version&gt;/Unity.app</c>) rather than actually
/// discovering installed Editors. Real discovery is Unity Hub discovery (spec #1 §5.3), which the
/// plan explicitly places out of scope ("its own piece of work... the API reserves the endpoint
/// shape; the implementation lands with it") - unlike the oracle-conformance warning, the plan does
/// not say to leave <c>openInUnity</c> unimplemented, so this uses the one well-documented,
/// version-independent fact available without Hub discovery (Unity Hub's own default per-version
/// install path) and fails cleanly, with a specific message, when nothing is installed there.</item>
/// <item><b><c>add</c> does not wire live file-watching.</b> It calls
/// <see cref="ProjectService.AdoptAndIndex"/>, so a newly added project is fully indexed before
/// this action's response returns. It does NOT call <see cref="Hades.Core.Observation.ObservationService.Watch"/>
/// to start a live <see cref="Hades.Core.Observation.ProjectWatcher"/> for it immediately - that
/// would require threading <c>ObservationService</c> through <see cref="ControlListener"/> and
/// Program.cs for a gap that already degrades gracefully: <c>ObservationService</c>'s own periodic
/// sweep iterates <see cref="ProjectService.KnownProjects"/> fresh on every tick and already picks
/// up newly-known projects within its own interval, so the newly added project is never silently
/// stuck un-synced forever, only synced on the next periodic tick rather than instantly.</item>
/// </list>
/// </summary>
public static class ProjectsEndpoint
{
    const string PathMissingMessage = "Project path not found — check that the volume is mounted or the drive is connected.";

    /// <summary>Launches a local process (or reports it could not). A seam so
    /// <see cref="RevealInFinder"/>/<see cref="OpenInUnity"/> are testable without actually
    /// spawning Finder or Unity; <see cref="DefaultProcessLauncher"/> is the real implementation
    /// <see cref="ControlListener"/> wires by default.</summary>
    public delegate bool ProcessLauncher(string executable, IReadOnlyList<string> arguments);

    /// <summary>The real <see cref="ProcessLauncher"/>: starts the process and reports whether it
    /// started, never throwing - the same "fail cleanly" contract <see cref="RevealInFinder"/> and
    /// <see cref="OpenInUnity"/> promise their own callers.</summary>
    public static bool DefaultProcessLauncher(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------------------------- GET

    /// <summary>Orchestrates real state into a <see cref="ProjectsResult"/>: every known project's
    /// Charon status, index summary, path existence, serialization mode, and installed plugin
    /// version, fanned out concurrently across projects (same reasoning as
    /// <see cref="SummaryEndpoint.BuildAsync"/> - one stuck Editor's probe timeout must not stall
    /// every other project's row) and fed to the pure <see cref="Resolve"/>.</summary>
    public static async Task<ProjectsResult> BuildAsync(ProjectService projects, Func<DateTimeOffset> utcNow)
    {
        var appPluginVersion = PluginInstaller.AppPluginVersion();
        var known = projects.KnownProjects();

        var snapshots = await Task.WhenAll(
            known.Select(project => BuildSnapshotAsync(projects, project, appPluginVersion))
        ).ConfigureAwait(false);

        return Resolve(snapshots, utcNow());
    }

    static async Task<ProjectStateSnapshot> BuildSnapshotAsync(ProjectService projects, UnityProject project, string? appPluginVersion)
    {
        var charon = await projects.GetCharonStatus(project.ProductGuid).ConfigureAwait(false);
        var summary = projects.Summary(project.ProductGuid);
        var pathExists = Directory.Exists(project.Path);

        return new ProjectStateSnapshot
        {
            Name = project.Name,
            Path = project.Path,
            ProductGuid = project.ProductGuid,
            UnityVersion = charon?.UnityVersion ?? (pathExists ? ProjectIdentity.TryReadUnityVersion(project.Path) : null),
            PathExists = pathExists,
            Attached = charon?.Attached ?? false,
            Busy = charon?.Busy ?? false,
            EditorUnityVersion = charon?.UnityVersion,
            EditorProcessId = charon?.ProcessId,
            ConnectionAge = charon?.ConnectionAge,
            LastIndexedUtc = summary?.LastIndexedUtc,
            NodeCount = summary?.TotalNodes ?? 0,
            EdgeCount = summary?.TotalEdges ?? 0,
            SerializationMode = pathExists ? TryReadSerializationMode(project.Path) : null,
            InstalledPluginVersion = charon?.PluginVersion ?? (pathExists ? PluginInstaller.InstalledPluginVersion(project.Path) : null),
            AppPluginVersion = appPluginVersion,
        };
    }

    /// <summary>The pure resolution core - see this class's own doc comment for the two-layer
    /// design and for exactly how each warning is decided.</summary>
    public static ProjectsResult Resolve(IReadOnlyList<ProjectStateSnapshot> projects, DateTimeOffset now) =>
        new() { Projects = projects.Select(p => BuildRow(p, now)).ToList() };

    static ProjectRow BuildRow(ProjectStateSnapshot p, DateTimeOffset now) => new()
    {
        Name = p.Name,
        Path = p.Path,
        ProductGuid = p.ProductGuid,
        UnityVersion = p.UnityVersion,
        IndexState = p.LastIndexedUtc is null ? ProjectIndexState.Indexing : ProjectIndexState.Indexed,
        IndexStatus = p.LastIndexedUtc is { } lastIndexedUtc ? $"indexed {FormatAge(now - lastIndexedUtc)} ago" : "not yet indexed",
        NodeCount = p.NodeCount,
        EdgeCount = p.EdgeCount,
        Editor = BuildEditorInfo(p),
        Warnings = BuildWarnings(p),
    };

    static ProjectEditorInfo BuildEditorInfo(ProjectStateSnapshot p)
    {
        var state = p switch
        {
            { Attached: true, Busy: true } => ProjectEditorState.Busy,
            { Attached: true, Busy: false } => ProjectEditorState.Attached,
            _ => ProjectEditorState.Absent,
        };

        var status = state switch
        {
            ProjectEditorState.Busy => "Editor attached (busy)",
            ProjectEditorState.Attached => "Editor attached",
            _ => "No Editor attached",
        };

        return new ProjectEditorInfo
        {
            State = state,
            Status = status,
            UnityVersion = p.EditorUnityVersion,
            ProcessId = p.EditorProcessId,
            ConnectionAgeSeconds = p.ConnectionAge is { } age ? Math.Max(0, (int)Math.Round(age.TotalSeconds)) : null,
        };
    }

    /// <summary>Builds every warning that actually fires for one project - see this class's own
    /// doc comment for the four warnings and which of them are real here. Order is fixed
    /// (path-missing, serialization, plugin mismatch) rather than severity-sorted, so a shell
    /// rendering warnings in response order is stable across calls.</summary>
    static IReadOnlyList<ProjectWarning> BuildWarnings(ProjectStateSnapshot p)
    {
        var warnings = new List<ProjectWarning>();

        if (!p.PathExists)
        {
            warnings.Add(new ProjectWarning
            {
                Code = "pathMissing",
                Severity = ControlSeverity.Error,
                Message = PathMissingMessage,
                Remedy = "Reconnect the volume, or remove this project from Hades if it no longer exists.",
            });
        }

        if (p.SerializationMode == 1)
        {
            warnings.Add(new ProjectWarning
            {
                Code = "serializationMode",
                Severity = ControlSeverity.Error,
                Message = "Asset serialization is set to Force Binary. Hades reads Unity's YAML directly from disk, so scenes, prefabs, and other serialized assets cannot be scanned at all — the graph is silently incomplete.",
                Remedy = "In Unity: Edit → Project Settings → Editor → Asset Serialization → Mode → Force Text.",
            });
        }
        else if (p.SerializationMode == 0)
        {
            warnings.Add(new ProjectWarning
            {
                Code = "serializationMode",
                Severity = ControlSeverity.Warning,
                Message = "Asset serialization is set to Mixed. Hades reads Unity's YAML directly from disk, so any asset serialized as binary under this mode is invisible to the graph — the graph may be silently incomplete.",
                Remedy = "In Unity: Edit → Project Settings → Editor → Asset Serialization → Mode → Force Text.",
            });
        }

        if (p.InstalledPluginVersion is { } installed && p.AppPluginVersion is { } app)
        {
            // Same/Unknown: no claim made - see PluginVersionSkew's own doc comment for why an
            // unparseable version string is treated as "nothing to compare", not as a problem.
            var message = PluginVersionComparison.Classify(installed, app) switch
            {
                PluginVersionSkew.Minor =>
                    $"The installed Hades plugin (v{installed}) does not match this app (v{app}). Editor-dependent tools may not work correctly until it is updated.",
                PluginVersionSkew.Major =>
                    $"The installed Hades plugin (v{installed}) is a different major version from this app (v{app}) — compatibility is not assured, and most Editor-dependent tools should be expected to fail until it is updated.",
                _ => null,
            };

            if (message is not null)
            {
                warnings.Add(new ProjectWarning
                {
                    Code = "pluginVersionMismatch",
                    Severity = ControlSeverity.Warning,
                    Message = message,
                    // Same remedy regardless of skew - one writer either way (spec #4 §6: "the app
                    // offers an in-place update", never a second installer path).
                    Remedy = "Use Install/Update Plugin for this project, then restart Unity if it is already running.",
                });
            }
        }

        return warnings;
    }

    /// <summary>Reads <c>m_SerializationMode</c> from <c>ProjectSettings/EditorSettings.asset</c> -
    /// see this class's own doc comment for why THIS file, not ProjectSettings.asset. Null for
    /// every failure mode (file missing, unreadable, unparseable, or a value that is not a plain
    /// integer) - the warning that would come from this simply does not fire rather than guessing.</summary>
    static int? TryReadSerializationMode(string projectRoot)
    {
        try
        {
            var settings = ReadThrough.GetSettingsAsset(projectRoot, "ProjectSettings/EditorSettings.asset");
            return settings.GetValueOrDefault("m_SerializationMode") is string raw && int.TryParse(raw, out var mode)
                ? mode
                : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>"12s" under a minute, "2m" at or beyond - duplicated from
    /// <see cref="SummaryEndpoint"/>'s own private FormatAge rather than shared, matching that
    /// method's own doc comment: kept local so this task does not need to modify Task 2's
    /// already-reviewed file.</summary>
    static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalMinutes < 1 ? $"{age.TotalSeconds:F0}s" : $"{age.TotalMinutes:F0}m";
    }

    // --------------------------------------------------------------------------------- actions

    /// <summary><c>POST /control/projects/add</c>. Adopts and fully indexes the project at
    /// <paramref name="request"/>'s path before returning - see this class's own "design
    /// decisions" note on why this does not also wire live file-watching. 400 with a resolved
    /// message when the path is blank or is not a Unity project (mirrors Program.cs's own CLI-arg
    /// adoption message).</summary>
    public static async Task<IResult> AddAsync(ProjectService projects, Func<DateTimeOffset> utcNow, AddProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return Results.Json(new { error = "Path must not be blank." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var project = projects.AdoptAndIndex(request.Path);
        if (project is null)
        {
            return Results.Json(
                new { error = $"'{request.Path}' is not a Unity project (no readable ProjectSettings/ProjectSettings.asset)." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var appPluginVersion = PluginInstaller.AppPluginVersion();
        var snapshot = await BuildSnapshotAsync(projects, project, appPluginVersion).ConfigureAwait(false);
        return Results.Json(BuildRow(snapshot, utcNow()));
    }

    /// <summary><c>POST /control/projects/{id}/remove</c>. See this class's own "design
    /// decisions" note: deregisters only, never deletes anything on disk. 404 when the guid is
    /// unknown.</summary>
    public static IResult Remove(ProjectService projects, string productGuid)
    {
        var project = projects.Get(productGuid);
        if (project is null)
        {
            return Results.Json(new { error = $"Unknown project '{productGuid}'." }, statusCode: StatusCodes.Status404NotFound);
        }

        projects.RemoveProject(productGuid);

        return Results.Json(new ActionResult
        {
            Success = true,
            Message = $"{project.Name} removed from Hades. Nothing was deleted from disk — the project itself, its indexed graph, and its authored memory all remain untouched.",
        });
    }

    /// <summary><c>POST /control/projects/{id}/rebuild</c>. Returns an operation id immediately,
    /// pollable via <c>GET /control/operations/{id}</c> from the moment this call returns - see
    /// this class's own "design decisions" note and <see cref="Operations"/>/
    /// <see cref="OperationRegistry"/> for the full mechanism. 404 when the guid is unknown.</summary>
    public static IResult Rebuild(ProjectService projects, OperationRegistry operations, string productGuid)
    {
        if (projects.Get(productGuid) is null)
        {
            return Results.Json(new { error = $"Unknown project '{productGuid}'." }, statusCode: StatusCodes.Status404NotFound);
        }

        var operationId = operations.Start("rebuild", () =>
        {
            // RebuildGraph itself only returns null for an unknown productGuid (see its own doc
            // comment) - already confirmed known above, but a project can still be REMOVED between
            // that check and this background work actually running. Thrown, not swallowed: the
            // OperationRegistry's own catch turns this into a Failed state with THIS message,
            // rather than a silently-Done operation with no result (see that class's own doc
            // comment on why a failure must report why, actionably).
            var result = projects.RebuildGraph(productGuid) ?? throw new InvalidOperationException(
                $"Project '{productGuid}' is no longer known to Hades — it may have been removed while the rebuild was queued.");

            // I5's last wiring step: the per-file diagnostics RebuildGraph now carries (a file
            // that could not be read or parsed) reach the operation's user-visible Message
            // instead of being dropped at this mapping - the count plus the first warning keeps
            // the message bounded no matter how many files a hostile project trips on.
            var warningSuffix = result.Warnings.Count switch
            {
                0 => "",
                1 => $" 1 file could not be fully indexed: {result.Warnings[0]}",
                var n => $" {n} files could not be fully indexed; first: {result.Warnings[0]}",
            };

            return new RebuildOperationResult
            {
                NodesBefore = result.NodesBefore,
                NodesAfter = result.NodesAfter,
                Message = BuildRebuildMessage(result.NodesBefore, result.NodesAfter) + warningSuffix,
            };
        });

        return Results.Json(new RebuildStartedResult { OperationId = operationId });
    }

    /// <summary>The pure core of <see cref="RebuildOperationResult.Message"/> - see that property's
    /// own doc comment. A leading "+" on a non-negative delta (never bare "0", always "+0") so the
    /// sign is never ambiguous between "no change" and "a negative number that happens to print the
    /// same" the way an unsigned zero could read.</summary>
    public static string BuildRebuildMessage(int nodesBefore, int nodesAfter)
    {
        var delta = nodesAfter - nodesBefore;
        var sign = delta >= 0 ? "+" : "";
        return $"Rebuild complete — {nodesAfter} nodes ({sign}{delta} from before).";
    }

    /// <summary><c>POST /control/projects/{id}/installPlugin</c>. See this class's own "design
    /// decisions" note for exactly how <see cref="InstallPluginResult.NeedsRestart"/> is decided.
    /// 404 when the guid is unknown; a resolved, non-throwing failure when the project's path is
    /// gone.</summary>
    public static async Task<IResult> InstallPluginAsync(ProjectService projects, string productGuid)
    {
        var project = projects.Get(productGuid);
        if (project is null)
        {
            return Results.Json(new { error = $"Unknown project '{productGuid}'." }, statusCode: StatusCodes.Status404NotFound);
        }

        if (!Directory.Exists(project.Path))
        {
            return Results.Json(new InstallPluginResult { Success = false, NeedsRestart = false, Message = PathMissingMessage });
        }

        // Checked BEFORE installing: installing itself never disconnects an attached Editor (that
        // is exactly the Plan 7 defect this reports on), so "attached right now" and "attached at
        // the moment we wrote new plugin files" are the same fact either way - checked first only
        // so the intent ("was it already running when this happened") reads plainly here.
        var charon = await projects.GetCharonStatus(productGuid).ConfigureAwait(false);
        var wasAttached = charon?.Attached ?? false;

        PluginInstaller.Install(project.Path);

        var message = wasAttached
            ? "Plugin installed. Restart Unity to load it — an Editor already running when the plugin is installed will not pick it up until restart."
            : "Plugin installed. It will load automatically the next time this project opens in Unity.";

        return Results.Json(new InstallPluginResult { Success = true, NeedsRestart = wasAttached, Message = message });
    }

    /// <summary><c>POST /control/projects/{id}/revealInFinder</c>. Fails cleanly (never throws)
    /// when the guid is unknown or the path is gone.</summary>
    public static IResult RevealInFinder(ProjectService projects, string productGuid, ProcessLauncher launch)
    {
        var project = projects.Get(productGuid);
        if (project is null)
        {
            return Results.Json(new { error = $"Unknown project '{productGuid}'." }, statusCode: StatusCodes.Status404NotFound);
        }

        if (!Directory.Exists(project.Path))
        {
            return Results.Json(new ActionResult { Success = false, Message = PathMissingMessage });
        }

        // The route keeps its macOS-flavoured name deliberately: renaming
        // /control/projects/{id}/revealInFinder would break the shipped Swift client for a
        // cosmetic gain. Route verbs stay platform-neutral in NAME; platform-specific behaviour
        // lives here.
        //
        // explorer.exe takes the selection as one comma-joined argument, not two.
        var launched = OperatingSystem.IsWindows()
            ? launch("explorer.exe", [$"/select,{project.Path}"])
            : launch("open", ["-R", project.Path]);

        return Results.Json(new ActionResult
        {
            Success = launched,
            Message = launched ? $"Revealed {project.Name} in Finder." : "Could not launch Finder.",
        });
    }

    /// <summary><c>POST /control/projects/{id}/openInUnity</c>. See this class's own "design
    /// decisions" note for the default-Unity-Hub-install-location assumption this rests on. Fails
    /// cleanly (never throws) when the guid is unknown, the path is gone, the project's Unity
    /// version is unknown, or no Editor is installed at the conventional location for that
    /// version.</summary>
    public static IResult OpenInUnity(ProjectService projects, string productGuid, ProcessLauncher launch)
    {
        var project = projects.Get(productGuid);
        if (project is null)
        {
            return Results.Json(new { error = $"Unknown project '{productGuid}'." }, statusCode: StatusCodes.Status404NotFound);
        }

        if (!Directory.Exists(project.Path))
        {
            return Results.Json(new ActionResult { Success = false, Message = PathMissingMessage });
        }

        var version = ProjectIdentity.TryReadUnityVersion(project.Path);
        if (version is null)
        {
            return Results.Json(new ActionResult
            {
                Success = false,
                Message = "This project's Unity version is unknown — it has no ProjectSettings/ProjectVersion.txt yet. Open it once from Unity Hub, then try again.",
            });
        }

        var executable = UnityHubEditorExecutablePath(version);
        if (!File.Exists(executable))
        {
            return Results.Json(new ActionResult
            {
                Success = false,
                Message = $"Unity {version} was not found at the default Unity Hub install location ({executable}). Open this project from Unity Hub instead.",
            });
        }

        var launched = launch(executable, ["-projectPath", project.Path]);

        return Results.Json(new ActionResult
        {
            Success = launched,
            Message = launched ? $"Opening {project.Name} in Unity {version}…" : $"Could not launch Unity {version}.",
        });
    }

    /// <summary>Unity Hub's own default per-version install location on each platform - see this
    /// class's own "design decisions" note on why this convention, rather than real Hub discovery,
    /// is what backs <see cref="OpenInUnity"/>. The cost of the convention is higher on Windows,
    /// where users relocate editors to another drive far more often than Mac users move
    /// /Applications - so a miss here is expected more often, and OpenInUnity's existing
    /// "not found at the default location, open from Unity Hub instead" message carries it.</summary>
    internal static string UnityHubEditorExecutablePath(string version) =>
        OperatingSystem.IsWindows()
            ? $@"C:\Program Files\Unity\Hub\Editor\{version}\Editor\Unity.exe"
            : $"/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents/MacOS/Unity";
}
