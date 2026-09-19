using System.ComponentModel;
using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Editors;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WireJson = Hades.Contract.Wire.JsonValue;
using WireKind = Hades.Contract.Wire.JsonValueKind;

namespace Hades.Server.Mcp;

/// <summary>scene_bake's result, shaped by 'status' - same action-shaped convention as
/// <see cref="PlayModeResult"/> (never a parallel record per action). 'jobId'/'target'/'status'/
/// 'detail' are populated whenever the plugin's own wire response carries them (every case except
/// 'none'/'unknown', which have no job to report on);
/// 'progress'/'backend' only while 'status' is 'running'; 'durationSeconds'/'backend' once a
/// terminal state has actually been observed; 'error' only for 'status':'failed'.</summary>
public sealed record SceneBakeResult
{
    [JsonPropertyName("jobId")] public string? JobId { get; init; }
    [JsonPropertyName("target")] public string? Target { get; init; }

    /// <summary>started|running|complete|cancelled|failed|indeterminate|unavailable|none|unknown -
    /// a plain string, not a closed enum (DESIGN-08 §5; DESIGN-06 drops 'started' from this
    /// vocabulary - see ROADMAP.md's own ground rules - so this app never actually receives it, but
    /// the field itself stays untyped like every other set-wide status field).</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }

    /// <summary>0.0-1.0, null when the target reports none (occlusion and navmesh, in this
    /// version - see Hades.Tools.BakeTargetSeams.Progress's own doc comment on the plugin
    /// side).</summary>
    [JsonPropertyName("progress")] public double? Progress { get; init; }

    [JsonPropertyName("startedAtUtc")] public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>Present once a terminal status has actually been observed. DESIGN-06 §4.4's own
    /// six-key SessionState budget records no separate "finished" timestamp, so this is measured to
    /// the moment it is READ, not to the (unrecorded) moment the transition actually happened - an
    /// honest approximation, not silently hidden (see the plugin's own SceneBakeCommands.TerminalResult
    /// doc comment).</summary>
    [JsonPropertyName("durationSeconds")] public double? DurationSeconds { get; init; }

    /// <summary>Lighting only. The CONFIGURED lightmapper (Lightmapping.lightingSettings.lightmapper),
    /// never the effective one - whether the GPU lightmapper's CPU fallback is detectable at all is
    /// H5, unresolved (DESIGN-06 §10.2/P12). Null means the active scene has no Lighting Settings
    /// asset at all (every newly created scene, until one is assigned) - Unity throws reading
    /// Lightmapping.lightingSettings in that case, caught on the plugin side and reported as this
    /// same "configured lightmapper unknown" null rather than failing the whole call (F3).</summary>
    [JsonPropertyName("backend")] public string? Backend { get; init; }

    /// <summary>Always populated: one actionable sentence, in every state - the same "successful
    /// call reporting an unavailable capability, never a thrown error" posture DESIGN-06 §6.3
    /// specifies for 'unavailable', extended here to every other status too.</summary>
    [JsonPropertyName("detail")] public required string Detail { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>
/// DESIGN-06 (docs/design/DESIGN-06-baking-and-subsystems.md): one new tool, <c>scene_bake</c>
/// (action='start'|'status'|'cancel'), targeting 'lighting'|'occlusion'|'navmesh'. Thin wire
/// proxy over the plugin's own <c>scene.bake</c> command - every state-model rule (the epoch
/// ownership token, the sticky Terminal, the three-valued Resolve predicate) lives entirely in
/// Hades.Tools.SceneBakeCommands on the plugin side (UnityPlugin/Assets/Hades/Tools/SceneBakeCommands.cs);
/// this class only builds the request and maps the response, the same shape as
/// <see cref="PlayModeTool"/>.
///
/// <b>No lease belief of its own</b> (DESIGN-06 §4.3: a bake holds no <see cref="Editors.LeaseRegistry"/>
/// lease for its duration) - unlike <c>script_editing_session</c>, there is nothing here for the
/// app's lease-belief mirror to track.
/// </summary>
[McpServerToolType]
public sealed class SceneBakeTool(EditorProxy editor, ProjectService projects)
{
    [McpServerTool(Name = "scene_bake", Title = "Scene Bake", ReadOnly = false, UseStructuredContent = true)]
    [Description("Starts, polls, or cancels a lighting/occlusion/navmesh bake. NO UNDO for any of "
               + "the three targets - lighting, NavMesh and occlusion data are assets Unity writes "
               + "directly, never Undo-recorded (same posture as asset_manage's own writes). "
               + "action='start' RETURNS IMMEDIATELY: a bake can run for seconds (navmesh) to HOURS "
               + "(lighting, GPU-dependent) - poll action='status' rather than assuming 'start' "
               + "already finished. No wall-clock timeout is ever imposed - only the user or an "
               + "explicit action='cancel' stops one. 'complete' means the bake ran to completion "
               + "WITHOUT this plugin observing an abort - not that the output asset was "
               + "independently verified. 'indeterminate' means Hades genuinely cannot currently "
               + "confirm whether this job finished, is still running, or was replaced by someone "
               + "else's bake - re-poll rather than assuming either outcome; it is NEVER silently "
               + "upgraded to 'complete'. A domain reload (recompiling scripts, entering Play Mode) "
               + "CANCELS any in-progress bake - Hades holds no reload lock across one, deliberately, "
               + "so the user's own recompiles are never blocked for a bake's entire duration. "
               + "'backend' (lighting only) reports the CONFIGURED lightmapper - whether the GPU "
               + "lightmapper actually ran, versus silently falling back to CPU, is not currently "
               + "readable from script, so this is the configured value, never a verified-effective "
               + "one. action='start' for target='navmesh' needs the com.unity.ai.navigation package "
               + "- check get_project_summary's 'capabilities' or hades_charon_status's own "
               + "'capabilities' first if unsure; an unavailable target answers with "
               + "status='unavailable' rather than an error. Needs a live Editor for 'start'/'cancel' "
               + "- call hades_charon_status first if unsure.")]
    public async Task<SceneBakeResult> SceneBake(
        [Description("'start', 'status', or 'cancel'")] string action,
        [Description("start: 'lighting', 'occlusion', or 'navmesh'. Required for 'start'; ignored otherwise.")] string? target = null,
        [Description("status/cancel: the jobId 'start' returned. Omit to act on the most recently started job.")] string? jobId = null,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        EditorComponentTools.RequireNonBlank(action, nameof(action), "scene_bake");

        switch (action.Trim().ToLowerInvariant())
        {
            case "start":
                EditorComponentTools.RequireNonBlank(target, nameof(target), "scene_bake's 'start' action");
                return await SendAsync("start", target, null, project, context).ConfigureAwait(false);

            case "status":
                return await SendAsync("status", null, jobId, project, context).ConfigureAwait(false);

            case "cancel":
                return await SendAsync("cancel", null, jobId, project, context).ConfigureAwait(false);

            default:
                throw new McpException($"scene_bake's 'action' must be 'start', 'status', or 'cancel', got '{action}'.");
        }
    }

    async Task<SceneBakeResult> SendAsync(string action, string? target, string? jobId, string? project, RequestContext<CallToolRequestParams> context)
    {
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);

        var @params = WireJson.NewObject().SetProperty("action", WireJson.String(action));
        if (!string.IsNullOrEmpty(target)) @params.SetProperty("target", WireJson.String(target));
        if (!string.IsNullOrEmpty(jobId)) @params.SetProperty("jobId", WireJson.String(jobId));

        var result = await editor.SendCommandAsync(productGuid, "scene.bake", @params).ConfigureAwait(false);
        return MapResult(result);
    }

    static SceneBakeResult MapResult(WireJson result) => new()
    {
        JobId = OptionalStr(result, "jobId"),
        Target = OptionalStr(result, "target"),
        Status = EditorComponentTools.Str(result, "status"),
        Progress = OptionalDouble(result, "progress"),
        StartedAtUtc = OptionalUnixMs(result, "startedAtUtcMs"),
        DurationSeconds = OptionalDouble(result, "durationSeconds"),
        Backend = OptionalStr(result, "backend"),
        Detail = EditorComponentTools.Str(result, "detail"),
        Error = OptionalStr(result, "error"),
    };

    static string? OptionalStr(WireJson value, string key) =>
        value.TryGetProperty(key, out var v) && v!.Kind == WireKind.String ? v.AsString() : null;

    static double? OptionalDouble(WireJson value, string key) =>
        value.TryGetProperty(key, out var v) && v!.Kind is WireKind.Integer or WireKind.Float ? v.AsDouble() : null;

    static readonly long MinUnixTimeMilliseconds = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    static readonly long MaxUnixTimeMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>Same clamp-rather-than-throw convention as <see cref="PlayModeTool"/>'s identical
    /// helper - an untrusted plugin-reported millisecond value must never fault this call
    /// outright.</summary>
    static DateTimeOffset? OptionalUnixMs(WireJson value, string key)
    {
        if (!value.TryGetProperty(key, out var v) || v!.Kind != WireKind.Integer) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(v.AsInteger(), MinUnixTimeMilliseconds, MaxUnixTimeMilliseconds));
    }
}
