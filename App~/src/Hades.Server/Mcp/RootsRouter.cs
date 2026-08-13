using Hades.Core;
using Hades.Core.Projects;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Hades.Server.Mcp;

public sealed record RootResolution
{
    public string? ProductGuid { get; init; }
    public string? Error { get; init; }

    /// <summary>Set only when this call auto-adopted a brand-new project (see
    /// <see cref="RootsRouter.ResolveAsync"/>) - null for a root that matched an
    /// already-registered project, which routes silently. A tool surfacing this result appends
    /// it to the reply so the caller knows a new project just entered scope and a first index may
    /// still be running.</summary>
    public string? Announcement { get; init; }

    public bool IsResolved => ProductGuid is not null;

    public static RootResolution Resolved(string productGuid, string? announcement = null) =>
        new() { ProductGuid = productGuid, Announcement = announcement };
    public static RootResolution Failed(string error) => new() { Error = error };
}

/// <summary>Supplies the roots (workspace folders) the connected MCP client currently reports -
/// the one seam between a live session and <see cref="RootsRouter.ResolveAsync"/>. Tests inject a
/// fake; <see cref="McpRootsProvider"/> is the one production implementation.</summary>
public interface IRootsProvider
{
    /// <summary>The client's roots, as local filesystem paths (a reported root that is not a
    /// <c>file://</c> URI is dropped, not an error). Empty when the client does not support
    /// roots, the request fails or times out, or the client genuinely reports none - every one of
    /// those is just an input to <see cref="RootsRouter"/>'s resolution rules, never an exception
    /// a caller has to handle separately.</summary>
    Task<IReadOnlyList<string>> GetRootsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IRootsProvider"/>, over the SDK's roots/list request.
///
/// <see cref="McpServer.RequestRootsAsync"/>, <see cref="ClientCapabilities.Roots"/>, and
/// <see cref="RootsCapability"/> all carry <see cref="ObsoleteAttribute"/> (diagnostic MCP9005:
/// "The Roots feature is deprecated as of specification version 2026-07-28 and may be removed in
/// a future version. See SEP-2577 for more information."). Verified before writing this class -
/// by reflecting over the pinned ModelContextProtocol.Core 2.0.0 assembly, and with a real
/// `dotnet build` - that this is advisory, not disabling: a single-argument
/// <see cref="ObsoleteAttribute"/> (no <c>error: true</c>), so the default severity is a warning,
/// not a build failure, and the feature is fully wired end to end in the SDK - capability
/// negotiation, the roots/list request/response types, the list_changed notification, and this
/// exact method's own doc remarks recommending it be called via <c>RequestContext.Server</c> from
/// inside a tool handler specifically because Streamable HTTP keeps that route open for the
/// duration of the request. Claude Code, the client this server actually talks to, answers
/// roots/list and pushes roots/list_changed today - verified against Claude Code's own docs, see
/// Architecture.md §2.4. Suppressed locally, at the one call site that needs it, rather than
/// project-wide in Directory.Build.props: this is the one place in Hades that knowingly depends
/// on a feature the SDK's authors have flagged for a future, not-yet-arrived removal, and that
/// should stay visible here, not buried in a project-wide NoWarn.
/// </summary>
public sealed class McpRootsProvider(McpServer server) : IRootsProvider
{
    public async Task<IReadOnlyList<string>> GetRootsAsync(CancellationToken cancellationToken)
    {
#pragma warning disable MCP9005 // advisory-only deprecation (SEP-2577) - see class doc comment above
        if (server.ClientCapabilities?.Roots is null) return [];

        try
        {
            var result = await server.RequestRootsAsync(new ListRootsRequestParams(), cancellationToken)
                .ConfigureAwait(false);

            return result.Roots
                .Select(root => Uri.TryCreate(root.Uri, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null)
                .OfType<string>()
                .ToList();
        }
        catch (Exception)
        {
            // Every failure mode here - the client declining, a malformed response, our own
            // caller's timeout cancelling this token - collapses to the same "could not get
            // roots right now" answer RootsRouter already treats as ordinary input, the same
            // bare-catch stance ProjectService.MainThreadIsResponsiveAsync takes for its own
            // "is the main thread listening" probe: there is nothing a caller would do
            // differently based on which one occurred.
            return [];
        }
#pragma warning restore MCP9005
    }
}

/// <summary>
/// Maps a set of filesystem paths onto exactly one known project, adopting any that turn out to
/// be Unity projects. Used for path-driven entry points — startup seeding today, and the control
/// API's "add this folder" later.
///
/// <see cref="Resolve"/> below is deliberately unchanged by, and unrelated to, live per-call
/// routing: it still has no "only one project is known, so use it" fallback (that heuristic exists
/// in the v1.2 hub only because the launcher could not identify its own project, and when it
/// guesses wrong it silently routes work to the wrong project — worse than failing), and it still
/// walks up via <see cref="ProjectIdentity.FindProjectRoot"/> because every existing caller hands
/// it a path already in hand, not a client-reported root.
///
/// <see cref="ResolveAsync"/> is the per-call seam <see cref="ToolSupport.ResolveProjectAsync"/>
/// uses once an explicit handle and the sole-known-project fallback both come up empty. Despite
/// the doc comments elsewhere in this codebase (ProjectService.cs, ObservationService.cs) that
/// describe RootsRouter as if it already ran "on every routed tool call" - it did not: nothing
/// called <see cref="Resolve"/> outside its own tests before this change. See Architecture.md
/// §2.4 for the full writeup of that gap.
/// </summary>
public sealed class RootsRouter(ProjectService projects)
{
    /// <summary>How long <see cref="ResolveAsync"/> waits for the client to answer roots/list
    /// before treating the call as having reported no roots. <c>init</c>, matching
    /// <see cref="ProjectService.CharonProbeTimeout"/>'s own convention for a tunable that almost
    /// never needs changing outside a test.</summary>
    public TimeSpan RootsRequestTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a roots/list answer is reused before <see cref="ResolveAsync"/> asks
    /// the client again. Routing runs on every tool call, so without this every ambiguous call
    /// would pay a full round trip; the tradeoff is that a root added mid-session (the client's
    /// own roots/list_changed notification) is not picked up until this expires. <c>init</c>, same
    /// convention as <see cref="RootsRequestTimeout"/>.</summary>
    public TimeSpan RootsCacheDuration { get; init; } = TimeSpan.FromSeconds(10);

    (IReadOnlyList<string> Roots, DateTimeOffset At) _cache = (Array.Empty<string>(), DateTimeOffset.MinValue);

    public RootResolution Resolve(IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
            return RootResolution.Failed("No workspace roots were reported by the client.");

        var matches = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            if (ProjectIdentity.FindProjectRoot(root) is not { } projectRoot) continue;

            // Adopt, never index: routing runs on every tool call and must not trigger a scan.
            var project = projects.Adopt(projectRoot);
            if (project is not null) matches[project.ProductGuid] = project.Path;
        }

        if (matches.Count == 1) projects.EnsureIndexed(matches.Keys.First());

        return matches.Count switch
        {
            1 => RootResolution.Resolved(matches.Keys.First()),
            0 => RootResolution.Failed(
                $"No Unity project found in the current workspace. Roots checked: {string.Join(", ", roots)}"),
            _ => RootResolution.Failed(
                "Ambiguous workspace — several Unity projects are in scope: "
                + string.Join(", ", matches.Values.Select(Path.GetFileName))
                + ". Open Claude Code in a single project directory."),
        };
    }

    /// <summary>
    /// The live-session counterpart to <see cref="Resolve"/>: asks <paramref name="rootsProvider"/>
    /// for the client's current roots (short-timeout, cached — see <see cref="RootsRequestTimeout"/>
    /// and <see cref="RootsCacheDuration"/>), canonicalizes each one, and matches it against known
    /// projects or - only when the root itself is a Unity project, never an ancestor or descendant
    /// walked to by guessing - auto-adopts and indexes it.
    ///
    /// Auto-adopt is intentionally narrower than <see cref="Resolve"/>'s own
    /// <see cref="ProjectIdentity.FindProjectRoot"/> walk: registering a brand-new project and
    /// kicking off a real index is a write, unlike routing to something already known, so it only
    /// fires on a signal as unambiguous as the client's literal, reported root — the same "a wrong
    /// guess is worse than failing" stance this class's own class doc comment already takes for
    /// routing, held to a higher bar here because a wrong guess would also persist state.
    /// </summary>
    public async Task<RootResolution> ResolveAsync(IRootsProvider rootsProvider, CancellationToken cancellationToken = default)
    {
        var roots = await CachedRootsAsync(rootsProvider, cancellationToken).ConfigureAwait(false);
        if (roots.Count == 0)
            return RootResolution.Failed("No workspace roots were reported by the client.");

        // Keyed by productGuid; the value is the path to (re-)Adopt — the KNOWN project's own
        // stored path for a containment match, never the reported root itself, since a root
        // merely INSIDE a project (e.g. its Assets folder) has no ProjectSettings.asset of its
        // own for Adopt to re-read.
        var matches = new Dictionary<string, (string AdoptPath, bool WasAlreadyKnown)>(StringComparer.Ordinal);

        foreach (var raw in roots)
        {
            string canonical;
            try
            {
                canonical = Canonicalize(raw);
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                continue; // A malformed or inaccessible reported root just matches nothing.
            }

            if (MatchAgainstKnownProjects(canonical) is { } known)
            {
                matches.TryAdd(known.ProductGuid, (known.Path, WasAlreadyKnown: true));
            }
            else if (FreshUnityRootGuid(canonical) is { } freshGuid)
            {
                matches.TryAdd(freshGuid, (canonical, WasAlreadyKnown: false));
            }
        }

        if (matches.Count == 0)
        {
            return RootResolution.Failed(
                $"No Unity project found in the current workspace. Roots checked: {string.Join(", ", roots)}");
        }

        if (matches.Count > 1)
        {
            return RootResolution.Failed(
                "Ambiguous workspace — several Unity projects are in scope: "
                + string.Join(", ", matches.Keys.Select(guid => projects.Get(guid)?.Name ?? guid))
                + ". Open Claude Code in a single project directory.");
        }

        var (productGuid, (adoptPath, wasAlreadyKnown)) = matches.First();
        var project = projects.Adopt(adoptPath);
        if (project is null)
        {
            // Defensive only: MatchAgainstKnownProjects/FreshUnityRootGuid already confirmed this
            // path is a real (or already-known) Unity project, so Adopt re-reading it should
            // never fail — unless the directory vanished between the check above and here.
            return RootResolution.Failed(
                $"No Unity project found in the current workspace. Roots checked: {string.Join(", ", roots)}");
        }

        if (wasAlreadyKnown) return RootResolution.Resolved(productGuid);

        projects.EnsureIndexed(productGuid);
        return RootResolution.Resolved(productGuid,
            $"Registered '{project.Name}' from your session folder; first index may take a few seconds.");
    }

    async Task<IReadOnlyList<string>> CachedRootsAsync(IRootsProvider provider, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _cache.At < RootsCacheDuration) return _cache.Roots;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RootsRequestTimeout);

        IReadOnlyList<string> roots;
        try
        {
            roots = await provider.GetRootsAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // RootsRequestTimeout fired, not the caller's own cancellation — treated exactly like
            // the client reporting no roots, never a fault of the call this blocks.
            roots = [];
        }

        _cache = (roots, DateTimeOffset.UtcNow);
        return roots;
    }

    /// <summary>The known project that <paramref name="canonicalRoot"/> equals or is nested
    /// inside, or null when no known project contains it. Mirrors
    /// <c>Indexing.ProjectWalker.IsInside</c>'s own equals-or-nested check (trim trailing
    /// separators on both sides before comparing — an exact-string compare there once let a
    /// package pointing at the project root get treated as "outside" and rescanned as a whole
    /// extra one), applied against every known project's own stored path. This is pure path
    /// arithmetic against already-registered projects, never a filesystem walk. Returns the
    /// project's own stored <see cref="UnityProject.Path"/> alongside its guid — never
    /// <paramref name="canonicalRoot"/> itself — because a root merely INSIDE the project (its
    /// Assets folder, say) has no ProjectSettings.asset of its own for a caller to re-Adopt.</summary>
    (string ProductGuid, string Path)? MatchAgainstKnownProjects(string canonicalRoot)
    {
        foreach (var project in projects.KnownProjects())
        {
            var known = Canonicalize(project.Path);
            if (canonicalRoot.Equals(known, StringComparison.Ordinal)
                || canonicalRoot.StartsWith(known + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return (project.ProductGuid, project.Path);
            }
        }

        return null;
    }

    /// <summary>Guid of the Unity project rooted EXACTLY at <paramref name="canonicalRoot"/> —
    /// never an ancestor or descendant (see <see cref="ResolveAsync"/>'s own doc comment for why
    /// auto-adopt is held to that stricter, no-walking bar than <see cref="Resolve"/>).</summary>
    static string? FreshUnityRootGuid(string canonicalRoot) =>
        ProjectIdentity.IsUnityProject(canonicalRoot) ? ProjectIdentity.TryReadProductGuid(canonicalRoot) : null;

    /// <summary>
    /// Lexically normalizes (<see cref="Path.GetFullPath(string)"/> plus trimming a trailing
    /// separator) and resolves one level of symlink, the same two-part shape as
    /// <c>Indexing.ProjectWalker</c>'s own <c>IsInside</c>/<c>CanonicalIdentity</c> helpers. A
    /// root reported by an MCP client is exactly as likely to arrive with a trailing slash, or as
    /// a symlinked alias (macOS: <c>/tmp</c> → <c>/private/tmp</c>), as any other path this
    /// process is handed, and an exact-string compare has already been the wrong tool for that
    /// once in this codebase (see <see cref="MatchAgainstKnownProjects"/>'s own doc comment).
    /// </summary>
    static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(full)) return full; // Nothing to symlink-resolve; matches nothing downstream anyway.

        try
        {
            var target = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            return target?.TrimEnd(Path.DirectorySeparatorChar) ?? full;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }
}
