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

/// <summary>play_mode's result - shaped by 'state' exactly as <see cref="TestResultsResult"/> is
/// shaped by 'status' (see that record's own doc comment). 'state' is always populated;
/// 'sessionId'/'ownedByHades' are populated whenever the plugin's own wire response carries them
/// (every action except a not-yet-answered 'status' short-circuit); 'expiresAtUtc' only for
/// 'enter'/'status' while a Hades-owned session is live; 'frame' for 'step'/'status' while playing;
/// 'playingForSeconds' only for 'status' while a Hades-owned session is actually playing; 'note' for
/// the 'detached'/busy short-circuits DESIGN-01 §10 describes.</summary>
public sealed record PlayModeResult
{
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("ownedByHades")] public bool OwnedByHades { get; init; }
    [JsonPropertyName("expiresAtUtc")] public DateTimeOffset? ExpiresAtUtc { get; init; }
    [JsonPropertyName("frame")] public long? Frame { get; init; }
    [JsonPropertyName("playingForSeconds")] public double? PlayingForSeconds { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
}

/// <summary>
/// DESIGN-01 (docs/design/DESIGN-01-play-mode-control.md): one new tool, <c>play_mode</c>, for
/// exploratory running - "author, then run, observe, isolate, stop, fix again" - complementing
/// <c>project_run_tests</c> rather than overlapping it (§12: PlayMode *tests* are a different thing).
///
/// Structurally a copy of <see cref="EditorProjectTools.ScriptEditingSession"/>'s action switch,
/// minus any <see cref="LeaseRegistry"/> calls - DESIGN-01 §7.4: a play session takes no reload
/// lease, so there is nothing here for the app's lease-belief mirror to track. The core stores no
/// play-session belief of its own either (§7.4); every fact this tool reports comes straight from
/// the plugin's own <c>SessionState</c>, polled fresh on every call.
///
/// <para><b>The 'status' short-circuit (DESIGN-01 §10).</b> Every other action goes straight through
/// <see cref="EditorProxy.SendCommandAsync"/> and lets a not-attached/busy Editor throw the standard
/// error, exactly like any other Editor tool. 'status' is the one deliberate exception: BOTH of
/// <see cref="ProjectService.GetCharonStatus"/>'s pre-send rejections (not attached; attached but
/// busy) are expected, informative states while polling an in-flight 'enter' - the domain reload
/// 'enter' triggers drops the socket, and the frames either side of it can leave the busy probe
/// unanswered. So 'status' calls <see cref="ProjectService.GetCharonStatus"/> itself, first, and
/// short-circuits both cases to <c>state:"detached"</c> with an explanatory note, rather than
/// throwing - see <see cref="StatusAsync"/>. Every other action is unaffected: this is a
/// single-tool, single-action deviation, not a change to <see cref="EditorProxy"/> itself.</para>
///
/// <para><b>No ack-gap verifier.</b> Play Mode exists only in the attached Editor's own memory, never
/// in project state <see cref="ProjectService"/> can re-read - DESIGN-01 §10 point 3: "no verifier
/// can be written for this." An interrupted 'enter'/'exit' surfaces the ordinary "interrupted, state
/// unverified, re-query before retrying" answer, and the agent's own next 'status' poll is the
/// recovery path (the whole reason 'enter' stamps SessionState before scheduling the transition -
/// DESIGN-01 §7.2 step 3).</para>
/// </summary>
[McpServerToolType]
public sealed class PlayModeTool(EditorProxy editor, ProjectService projects)
{
    [McpServerTool(Name = "play_mode", Title = "Play Mode Control", ReadOnly = false, UseStructuredContent = true)]
    [Description("Controls this Unity Editor's Play Mode for exploratory running - author a scene, "
               + "then run/observe/isolate/stop/fix. Not for test assertions - use project_run_tests "
               + "for those. RETURNS IMMEDIATELY: only 'enter' can trigger a domain reload (and only "
               + "when the project's Enter Play Mode Options have domain reload enabled) - 'exit' "
               + "never reloads. Either way, poll action:'status' rather than assuming the transition "
               + "already finished. NEVER WRITES TO DISK, in any action - no "
               + "scene, no asset; dirty scenes stay exactly as dirty as they were (call "
               + "scene_manage's save op first if you want the current scene persisted). EVERY "
               + "MUTATING HADES TOOL IS REFUSED while this Editor is in Play Mode - a scene change "
               + "would be discarded when Play Mode exits, an asset-file write would persist, and the "
               + "result would look the same either way, so Hades refuses both uniformly; call "
               + "action:'exit' first, then retry. A session HADES starts auto-exits after "
               + "'ttlSeconds' of inactivity; a session the USER started by pressing Play is never "
               + "auto-exited (reported as ownedByHades:false) - an explicit action:'exit' still "
               + "stops it. Needs a live Editor - call hades_charon_status first if unsure.")]
    public async Task<PlayModeResult> PlayMode(
        [Description("'enter', 'exit', 'pause', 'resume', 'step', or 'status'")] string action,
        [Description("enter only: auto-exit deadline in seconds if never renewed, clamped to [10, 600]. Omit for the plugin's default (120s).")] double? ttlSeconds = null,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null,
        RequestContext<CallToolRequestParams> context = null!)
    {
        EditorComponentTools.RequireNonBlank(action, nameof(action), "play_mode");

        switch (action.Trim().ToLowerInvariant())
        {
            case "enter":
            {
                var @params = WireJson.NewObject();
                if (ttlSeconds is { } t) @params.SetProperty("ttlSeconds", WireJson.Float(t));

                var result = await editor.SendCommandAsync(project, "play.enter", @params).ConfigureAwait(false);
                return new PlayModeResult
                {
                    State = EditorComponentTools.Str(result, "state"),
                    SessionId = OptionalStr(result, "sessionId"),
                    OwnedByHades = true, // 'enter' only ever returns for a Hades-owned session (or refuses)
                    ExpiresAtUtc = OptionalUnixMs(result, "expiresAtUtcMs"),
                };
            }

            case "exit":
            {
                var result = await editor.SendCommandAsync(project, "play.exit").ConfigureAwait(false);
                var sessionId = OptionalStr(result, "sessionId");
                return new PlayModeResult
                {
                    State = EditorComponentTools.Str(result, "state"),
                    SessionId = sessionId,
                    OwnedByHades = sessionId is not null,
                };
            }

            case "pause":
            case "resume":
            {
                var @params = WireJson.NewObject()
                    .SetProperty("paused", WireJson.Bool(string.Equals(action.Trim(), "pause", StringComparison.OrdinalIgnoreCase)));
                var result = await editor.SendCommandAsync(project, "play.set_paused", @params).ConfigureAwait(false);
                return FromSessionFields(result);
            }

            case "step":
            {
                var result = await editor.SendCommandAsync(project, "play.step").ConfigureAwait(false);
                var mapped = FromSessionFields(result);
                return mapped with { Frame = OptionalLong(result, "frame") };
            }

            case "status":
                return await StatusAsync(project, context).ConfigureAwait(false);

            default:
                throw new McpException($"play_mode's 'action' must be 'enter', 'exit', 'pause', 'resume', 'step', or 'status', got '{action}'.");
        }
    }

    /// <summary>DESIGN-01 §10's deliberate, single-tool deviation: short-circuits BOTH of
    /// <see cref="ProjectService.GetCharonStatus"/>'s pre-send rejections (not attached; attached but
    /// busy) to <c>state:"detached"</c> with an explanatory note instead of throwing, since both are
    /// expected, informative states while polling an in-flight 'enter' - never a caller mistake.
    /// Makes NO wire call at all in either case (proven by <c>Status_WhileBusyOrDetached_MakesNoWireCall</c>
    /// on the test side) - this is a short-circuit, not a catch around a failed send.</summary>
    async Task<PlayModeResult> StatusAsync(string? project, RequestContext<CallToolRequestParams> context)
    {
        var (productGuid, _) = await ToolSupport.ResolveProjectAsync(projects, project, context).ConfigureAwait(false);

        var status = await projects.GetCharonStatus(productGuid).ConfigureAwait(false);
        if (status is null || !status.Attached)
        {
            return new PlayModeResult
            {
                State = "detached",
                Note = "No Unity Editor is attached right now. If you just called play_mode with "
                     + "action 'enter' or 'exit', this is a domain reload in progress - poll again.",
            };
        }

        if (status.Busy)
        {
            return new PlayModeResult
            {
                State = "detached",
                Note = "A Unity Editor is attached but its main thread has not answered within the "
                     + "probe window - typically the frame either side of a play-mode transition, or "
                     + "a heavy first frame. Nothing has gone wrong; poll again.",
            };
        }

        var result = await editor.SendCommandAsync(productGuid, "play.status").ConfigureAwait(false);
        return new PlayModeResult
        {
            State = EditorComponentTools.Str(result, "state"),
            SessionId = OptionalStr(result, "sessionId"),
            OwnedByHades = result.TryGetProperty("ownedByHades", out var o) && o!.Kind == WireKind.Boolean && o.AsBoolean(),
            ExpiresAtUtc = OptionalUnixMs(result, "expiresAtUtcMs"),
            Frame = OptionalLong(result, "frame"),
            PlayingForSeconds = OptionalDouble(result, "playingForSeconds"),
        };
    }

    static PlayModeResult FromSessionFields(WireJson result) => new()
    {
        State = EditorComponentTools.Str(result, "state"),
        SessionId = OptionalStr(result, "sessionId"),
        OwnedByHades = result.TryGetProperty("ownedByHades", out var o) && o!.Kind == WireKind.Boolean && o.AsBoolean(),
    };

    static string? OptionalStr(WireJson value, string key) =>
        value.TryGetProperty(key, out var v) && v!.Kind == WireKind.String ? v.AsString() : null;

    static long? OptionalLong(WireJson value, string key) =>
        value.TryGetProperty(key, out var v) && v!.Kind == WireKind.Integer ? v.AsInteger() : null;

    static double? OptionalDouble(WireJson value, string key) =>
        value.TryGetProperty(key, out var v) && v!.Kind is WireKind.Integer or WireKind.Float ? v.AsDouble() : null;

    static readonly long MinUnixTimeMilliseconds = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();
    static readonly long MaxUnixTimeMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>Same clamp-rather-than-throw convention as
    /// <see cref="EditorProjectTools.ClampToUnixTimeMilliseconds"/> - an untrusted plugin-reported
    /// millisecond value must never fault this call outright.</summary>
    static DateTimeOffset? OptionalUnixMs(WireJson value, string key)
    {
        if (!value.TryGetProperty(key, out var v) || v!.Kind != WireKind.Integer) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(v.AsInteger(), MinUnixTimeMilliseconds, MaxUnixTimeMilliseconds));
    }
}
