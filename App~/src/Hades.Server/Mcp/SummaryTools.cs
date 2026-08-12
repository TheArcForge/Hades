using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using Hades.Core;
using Hades.Core.Editors;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Hades.Server.Mcp;

public sealed record RecentlyChangedHit
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("mtimeUtc")] public required DateTimeOffset MtimeUtc { get; init; }
    [JsonPropertyName("sizeBytes")] public required long SizeBytes { get; init; }
}

public sealed record RecentlyChangedResult
{
    [JsonPropertyName("results")] public required IReadOnlyList<RecentlyChangedHit> Results { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
    [JsonPropertyName("totalReturned")] public required int TotalReturned { get; init; }
}

public sealed record PingResult
{
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("uptimeSeconds")] public required double UptimeSeconds { get; init; }
}

public sealed record CharonStatusResult
{
    [JsonPropertyName("attached")] public required bool Attached { get; init; }

    /// <summary>True only when <see cref="Attached"/> is also true and the main thread has not
    /// answered the busy probe within the timeout — see ProjectService.GetCharonStatus. Never
    /// true while Attached is false: there is no "busy but not attached" state.</summary>
    [JsonPropertyName("busy")] public required bool Busy { get; init; }

    /// <summary>Absent (see the MCP SDK's WhenWritingNull default) unless <see cref="Attached"/>
    /// is true — hello-derived, so present for both idle and busy.</summary>
    [JsonPropertyName("unityVersion")] public string? UnityVersion { get; init; }

    /// <summary>The attached plugin's own self-reported version — hello-derived, same presence
    /// rule as <see cref="UnityVersion"/>. Spec #4 §6: "the plugin reports its version on connect".
    /// A mismatch against this app's own version is never silent: see <see cref="Detail"/>, which
    /// names the gap plainly rather than swallowing it — but is never a reason this tool reports
    /// <see cref="Attached"/> false either; see <see cref="Editors.PluginVersionSkew"/>'s own class
    /// doc comment for why degrading, not refusing, is the whole point.</summary>
    [JsonPropertyName("pluginVersion")] public string? PluginVersion { get; init; }

    [JsonPropertyName("projectPath")] public string? ProjectPath { get; init; }
    [JsonPropertyName("processId")] public long? ProcessId { get; init; }
    [JsonPropertyName("connectionAgeSeconds")] public double? ConnectionAgeSeconds { get; init; }

    /// <summary>True while this app believes Hades is holding Unity's reload lock for this
    /// project — see <see cref="Editors.LeaseRegistry"/>. Independent of <see cref="Attached"/>:
    /// a believed-held lease survives a disconnect until the next reconnect reconciles it (see
    /// <see cref="Editors.LeaseRegistry.ReconcileAsync"/>), so a stale "still held" belief is
    /// reported here rather than hidden just because nobody is attached to ask right now — a held
    /// reload lock must never be silent. The other direction is covered too: this reads false once
    /// the believed lease's own reported TTL has passed, even with the Editor still attached and no
    /// lease.release ever called — see <see cref="Editors.LeaseRegistry.Get"/>'s self-expiry, which
    /// this property reads through unchanged. Without that, this field would keep reading true for
    /// a lock the plugin's own TTL watchdog had already released, silently, minutes ago.</summary>
    [JsonPropertyName("leaseHeld")] public required bool LeaseHeld { get; init; }
    [JsonPropertyName("leaseId")] public string? LeaseId { get; init; }
    [JsonPropertyName("leaseHeldForSeconds")] public double? LeaseHeldForSeconds { get; init; }
    [JsonPropertyName("leaseExpiresAtUtc")] public DateTimeOffset? LeaseExpiresAtUtc { get; init; }

    [JsonPropertyName("detail")] public required string Detail { get; init; }
}

/// <summary>
/// Summary and lifecycle tools: whole-scene/prefab rollups, the recently-changed file list,
/// forcing a reindex, and two "is the app itself okay" diagnostics. Same conventions as
/// HadesTools/GraphTools throughout — see HadesTools' class doc comment for why project routing
/// uses an explicit handle rather than MCP roots. Kept as its own file rather than folded into
/// GraphTools.cs — these are not relationship queries, and GraphTools was already sized to stay
/// under its own ~400-line guideline.
/// </summary>
[McpServerToolType]
public sealed class SummaryTools(ProjectService projects, LeaseRegistry leases)
{
    [McpServerTool(Name = "get_scene_summary", Title = "Scene Summary", ReadOnly = true, UseStructuredContent = true)]
    [Description("GameObject count, root-GameObject count, and a per-kind component breakdown "
               + "for a scene or prefab. \"Root\" means a top-level GameObject: its Transform has "
               + "no parent. Takes the project-relative path exactly as search_by_name returns it."
               + ToolSupport.SavedStateClause)]
    public SceneSummary GetSceneSummary(
        [Description("Project-relative scene or prefab path, as returned by search_by_name")] string path,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException(
                "get_scene_summary needs a 'path' — the project-relative scene (or prefab) path "
                + "to summarise, e.g. {\"path\": \"Assets/Scenes/Main.unity\"}. search_by_name "
                + "returns paths in exactly this form.");
        }

        var productGuid = ToolSupport.ResolveProject(projects, project);

        return projects.GetSceneSummary(productGuid, path)
            ?? throw new McpException(
                $"'{path}' is not in the graph, so it cannot be summarised. Check the path with "
                + "search_by_name — it must be project-relative (\"Assets/...\" or "
                + "\"Packages/...\"), not absolute. Note that an asset with no .meta file cannot "
                + "be resolved.");
    }

    [McpServerTool(Name = "get_recently_changed", Title = "Recently Changed Files", ReadOnly = true, UseStructuredContent = true)]
    [Description("Files touched most recently, newest first — sourced from the on-disk "
               + "modification time recorded when each file was last indexed. Useful for picking "
               + "up 'what have I been working on'." + ToolSupport.SavedStateClause)]
    public RecentlyChangedResult GetRecentlyChanged(
        [Description("Only include files changed at or after this ISO-8601 timestamp, e.g. "
                    + "\"2026-08-01T00:00:00Z\". Omit for no lower bound.")] string? since = null,
        [Description("Maximum files to return (1-500, default 50)")] int limit = 50,
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        DateTimeOffset? sinceParsed = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                throw new McpException(
                    $"'{since}' is not a recognisable timestamp for 'since' — use ISO-8601, e.g. "
                    + "{\"since\": \"2026-08-01T00:00:00Z\"}.");
            }

            sinceParsed = parsed;
        }

        var productGuid = ToolSupport.ResolveProject(projects, project);

        // Clamped to this tool's own documented maximum BEFORE the "+1" below - see
        // InspectTool.FindUnsetReferences' identical clampedLimit pattern. Without this, a
        // caller-supplied limit above 500 skips the clamp entirely: the raw limit + 1 is what
        // reaches the database's own shared ceiling (GraphDatabase.MaxSearchFetch), and
        // 'truncated' below - computed against the UNCLAMPED limit - can go right on reporting
        // false while real matches beyond the documented max are silently cut.
        var clampedLimit = Math.Clamp(limit, 1, 500);

        var found = projects.RecentlyChanged(productGuid, sinceParsed, clampedLimit + 1);
        var truncated = found.Count > clampedLimit;

        var hits = found.Take(clampedLimit).Select(f => new RecentlyChangedHit
        {
            Path = f.Path,
            MtimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(f.MTimeUtcMs),
            SizeBytes = f.Size,
        }).ToList();

        return new RecentlyChangedResult { Results = hits, Truncated = truncated, TotalReturned = hits.Count };
    }

    [McpServerTool(Name = "hades_rebuild_graph", Title = "Rebuild Graph", ReadOnly = false, UseStructuredContent = true)]
    [Description("Forces a full reindex of a project from scratch, ignoring whatever this process "
               + "already had cached, and reports the node count before and after. Full rebuilds "
               + "can take 10-60 seconds on large projects; the graph stays queryable throughout "
               + "but results may be stale until it completes. Hades' incremental sync already "
               + "keeps the graph current on its own — call this when you suspect the graph has "
               + "drifted from disk, not as routine maintenance." + ToolSupport.SavedStateClause)]
    public RebuildResult RebuildGraph(
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        var productGuid = ToolSupport.ResolveProject(projects, project);

        return projects.RebuildGraph(productGuid)
            ?? throw new McpException($"Project {productGuid} is known but has nothing to rebuild yet.");
    }

    [McpServerTool(Name = "hades_ping", Title = "Ping", ReadOnly = true, UseStructuredContent = true)]
    [Description("Confirms the Hades server process itself is running and responsive — version "
               + "and process uptime, nothing else. Deliberately touches no project and no "
               + "database, so it still answers when a project's graph is unhealthy: that "
               + "independence is the entire point. Call this first when another tool is failing "
               + "or hanging, to tell apart 'the server is down' from 'something project-specific "
               + "is wrong'.")]
    public PingResult Ping()
    {
        using var process = Process.GetCurrentProcess();
        var uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();

        return new PingResult { Version = HadesTools.ServerVersion, UptimeSeconds = uptime.TotalSeconds };
    }

    [McpServerTool(Name = "hades_charon_status", Title = "Charon Status", ReadOnly = true, UseStructuredContent = true)]
    [Description("Whether a Unity Editor is attached over Charon, Hades' plugin transport, and "
               + "whether its main thread is currently responsive. Three states: not attached (no "
               + "Editor connected for this project), attached (Unity version, project path, pid "
               + "and connection age are reported), or busy — attached, but the main thread has "
               + "not answered within the probe window (e.g. mid-compile, importing assets, or "
               + "blocked on a long-running operation). Busy is distinct from not attached: the "
               + "connection itself is alive, only the Editor is momentarily unresponsive. Roughly "
               + "50 of Hades' Editor-dependent tools — scene, prefab, component, material and "
               + "animation authoring; play-mode and console access; live trace capture — do not "
               + "appear in tools/list here regardless of attachment: they cannot be proxied yet, "
               + "so listing them would only mean discovering the failure on every call. Everything "
               + "derivable from files on disk (search, references, dependencies, scene/prefab "
               + "summaries) works regardless of Editor state. Call this to confirm an Editor-only "
               + "capability is genuinely unavailable, or to tell 'busy' apart from 'gone'.")]
    public async Task<CharonStatusResult> CharonStatus(
        [Description("Project handle from hades_status. Omit when Hades knows only one project.")] string? project = null)
    {
        var productGuid = ToolSupport.ResolveProject(projects, project);

        var status = await projects.GetCharonStatus(productGuid).ConfigureAwait(false)
            ?? throw new McpException($"Project {productGuid} is known but has no Charon status to report.");

        // Read regardless of Attached: a lease this app believes is held survives a disconnect
        // until the next reconnect reconciles it (LeaseRegistry.ReconcileAsync), and "not
        // attached" — the Editor crashed, or simply has not reconnected yet — is exactly when a
        // stale belief matters most. Hiding it behind Attached would recreate the silent-lock
        // failure this whole plan exists to prevent.
        var lease = leases.Get(productGuid);

        return new CharonStatusResult
        {
            Attached = status.Attached,
            Busy = status.Busy,
            UnityVersion = status.UnityVersion,
            PluginVersion = status.PluginVersion,
            ProjectPath = status.ProjectPath,
            ProcessId = status.ProcessId,
            ConnectionAgeSeconds = status.ConnectionAge?.TotalSeconds,
            LeaseHeld = lease is not null,
            LeaseId = lease?.LeaseId,
            LeaseHeldForSeconds = lease is not null ? (DateTimeOffset.UtcNow - lease.AcquiredAtUtc).TotalSeconds : null,
            LeaseExpiresAtUtc = lease?.ExpiresAtUtc,
            Detail = DescribeCharonStatus(status, lease),
        };
    }

    static string DescribeCharonStatus(CharonStatus status, LeaseStatus? lease)
    {
        string baseDetail;
        if (!status.Attached)
        {
            baseDetail = "No Unity Editor is attached. Editor-dependent tools remain unavailable until "
                 + "one connects — not hidden, not failing, simply not part of this server's tool "
                 + "surface until then.";
        }
        else if (status.Busy)
        {
            baseDetail = $"Unity Editor {status.UnityVersion} is attached at '{status.ProjectPath}' "
                 + $"(pid {status.ProcessId}), but its main thread has not answered within the "
                 + "probe window — likely mid-compile, importing assets, or blocked on a "
                 + "long-running operation. The connection itself is alive; Editor-dependent tools "
                 + "would currently be slow or unresponsive rather than failing outright.";
        }
        else
        {
            baseDetail = $"Unity Editor {status.UnityVersion} is attached at '{status.ProjectPath}' "
                 + $"(pid {status.ProcessId}), connected for {FormatAge(status.ConnectionAge!.Value)}.";
        }

        // A held lock is the one thing this tool is deliberately loud about, in every state above
        // — see this method's own reasoning in CharonStatus for why it is read regardless of
        // Attached.
        var withLease = lease is null ? baseDetail : baseDetail + " " + DescribeLease(lease);

        // Plugin version skew (spec #4 §6, Plan 14 Task 5: "degrade, never refuse") is the other
        // thing this tool is deliberately loud about — never a reason Attached reads false above,
        // only ever an addition to Detail. Only meaningful once attached: nothing to compare
        // against when nothing is connected.
        var skew = status.Attached ? DescribePluginVersionSkew(status.PluginVersion) : null;
        return skew is null ? withLease : withLease + " " + skew;
    }

    /// <summary>Names a live plugin/app version gap plainly — or returns null for Same/Unknown
    /// (nothing to say; see <see cref="PluginVersionSkew"/>'s own doc comment for why an
    /// unparseable version is treated as "nothing to compare", not a problem). Points at the SAME
    /// remedy <see cref="ProjectsEndpoint"/>'s own "pluginVersionMismatch" warning does
    /// (Install/Update Plugin) rather than implying this MCP surface can fix it itself — installing
    /// the plugin is a Control-API action the Hades app's Projects view offers, not an MCP tool an
    /// agent can call directly.</summary>
    static string? DescribePluginVersionSkew(string? pluginVersion)
    {
        var appVersion = PluginInstaller.AppPluginVersion();

        return PluginVersionComparison.Classify(pluginVersion, appVersion) switch
        {
            PluginVersionSkew.Minor =>
                $"The attached plugin (v{pluginVersion}) does not match this app (v{appVersion}) — "
                + "Editor-dependent tools may not work correctly until it is updated (Install/Update "
                + "Plugin, in the Hades app's Projects view).",
            PluginVersionSkew.Major =>
                $"The attached plugin (v{pluginVersion}) is a different major version from this app "
                + $"(v{appVersion}) — compatibility is not assured, and most Editor-dependent tools "
                + "should be expected to fail until it is updated (Install/Update Plugin, in the "
                + "Hades app's Projects view).",
            _ => null,
        };
    }

    static string DescribeLease(LeaseStatus lease)
    {
        var heldFor = FormatAge(DateTimeOffset.UtcNow - lease.AcquiredAtUtc);
        var expiresIn = FormatAge(lease.ExpiresAtUtc - DateTimeOffset.UtcNow);
        return $"Hades is holding Unity's reload lock (lease '{lease.LeaseId}', held for {heldFor}, "
             + $"expires in {expiresIn} unless renewed) — Unity will not recompile scripts until "
             + "it is released (lease.release) or the lease expires.";
    }

    static string FormatAge(TimeSpan age) =>
        age.TotalMinutes < 1 ? $"{age.TotalSeconds:F0}s" : $"{age.TotalMinutes:F0}m";
}
