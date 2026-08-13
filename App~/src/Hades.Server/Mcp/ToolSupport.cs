using Hades.Core;
using Hades.Core.Projects;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Hades.Server.Mcp;

/// <summary>Shared across every tool. The saved-state clause in particular must be identical
/// everywhere — a tool that quietly omits it is claiming freshness it does not have.</summary>
public static class ToolSupport
{
    /// <summary>Appended to every read-through tool's description. Callers act on these answers;
    /// they need to know the answer predates any unsaved Editor changes.</summary>
    public const string SavedStateClause =
        " Reflects the last saved state on disk — unsaved Editor changes are not visible.";

    /// <summary>Appended to every class-4 (live-state read) tool's description — the mirror of
    /// <see cref="SavedStateClause"/> above, for the three tools that clause could never honestly
    /// apply to. project_get_console_log, project_get_test_results, and inspector_inspect answer
    /// from the attached Editor's own live memory (the console scrollback, an in-progress test
    /// run, a GameObject's current serialized state) — there is no file on disk holding that
    /// answer for a read-through tool to parse instead, which is exactly why these three need a
    /// live Editor connection at all, unlike every SavedStateClause tool.</summary>
    public const string LiveStateClause =
        " Reflects only the attached Editor's live, in-memory state — there is no saved-file equivalent this could be read from instead.";

    /// <summary>
    /// Resolves an explicit handle, or falls back to the sole known project - a one-line forward
    /// to <see cref="ProjectResolver.Resolve"/>, the ONE implementation of this logic (also used
    /// directly by <c>Hades.Core.Editors.EditorProxy</c>, which cannot call this Server-side copy
    /// — see that class's own doc comment for why the logic itself lives in Hades.Core). Kept
    /// here, under this name, purely so none of the existing MCP tool call sites
    /// (<c>ToolSupport.ResolveProject(projects, project)</c>) had to change.
    ///
    /// Unchanged by, and does not need, live MCP roots: see <see cref="ResolveProjectAsync"/> for
    /// the roots-aware call sites need instead once this can no longer decide alone (no explicit
    /// handle, and Hades knows zero or several projects).
    /// </summary>
    public static string ResolveProject(ProjectService projects, string? handle) =>
        ProjectResolver.Resolve(projects, handle);

    /// <summary>
    /// The full per-call resolution order: an explicit <paramref name="handle"/> always wins
    /// (forwarded to <see cref="ResolveProject"/>, unchanged); failing that, a single known
    /// project still wins the same way (also unchanged, and — like the explicit-handle case —
    /// decided without ever asking <paramref name="rootsProvider"/> for anything, so the common
    /// case pays no round trip at all); only once neither of those can decide alone does this
    /// fall to <paramref name="router"/>'s live <see cref="RootsRouter.ResolveAsync"/> — a unique
    /// match against a known project, or an auto-adopted brand-new one, either routes; anything
    /// else it reports (ambiguous roots, no roots capability, a timeout, or no match) is
    /// deliberately NOT surfaced verbatim — it collapses back to the exact same "needs a project
    /// argument" <see cref="McpException"/> <see cref="ResolveProject"/> already throws, naming
    /// every known handle, so a caller sees one consistent error shape regardless of which step
    /// inside this method failed to decide.
    /// </summary>
    public static async Task<(string ProductGuid, string? Announcement)> ResolveProjectAsync(
        ProjectService projects, string? handle, RootsRouter router, IRootsProvider rootsProvider,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(handle) || projects.KnownProjects().Count == 1)
            return (ResolveProject(projects, handle), null);

        var resolution = await router.ResolveAsync(rootsProvider, cancellationToken).ConfigureAwait(false);
        if (resolution.IsResolved) return (resolution.ProductGuid!, resolution.Announcement);

        // Roots did not decide it either. handle is still null/blank and KnownProjects().Count is
        // still 0 or >1 here, so this deterministically throws ProjectResolver's own "needs a
        // project argument" McpException — the one place that error is worded, reused rather
        // than re-derived.
        return (ResolveProject(projects, handle), null);
    }

    /// <summary>
    /// Convenience overload for the shape every wired <see cref="McpServerToolAttribute"/> method
    /// actually has: one live <see cref="RequestContext{TParams}"/>, not a pre-built
    /// <see cref="RootsRouter"/>/<see cref="IRootsProvider"/> pair. Sources
    /// <see cref="RootsRouter"/> from DI — <c>Program.cs</c> registers ONE singleton every tool
    /// call shares, which is what makes its own roots cache (<see cref="RootsRouter.RootsCacheDuration"/>)
    /// actually save round trips across calls, not just within one — and <see cref="IRootsProvider"/>
    /// from DI too, but only when something has registered one there (a test standing in a
    /// <c>RootsRouterTests.FakeRootsProvider</c>: production wiring never registers one), falling
    /// back to a fresh <see cref="McpRootsProvider"/> over <paramref name="context"/>'s own live
    /// <see cref="McpServer"/> otherwise — the exact same "read the server off THIS call's own
    /// RequestContext, not off DI" move <see cref="EditorProjectTools.ReplayToolCallAsync"/>
    /// already makes for the same reason (a server-scoped DI registration cannot hand back the one
    /// live session a particular in-flight call belongs to; only that call's own context can).
    ///
    /// Also records a non-null <see cref="RootResolution.Announcement"/> onto
    /// <paramref name="context"/>'s own <see cref="MessageContext.Items"/>, keyed by
    /// <see cref="AnnouncementItemsKey"/> — the seam <see cref="AppendAnnouncement"/> reads back
    /// from afterward, once, in Program.cs's own CallToolFilters chain, to append it to the
    /// OUTGOING result as an extra content block. A tool calling THIS overload does not need to do
    /// anything else for a caller to see the announcement: it only ever needs this tuple's
    /// <c>ProductGuid</c>.
    /// </summary>
    public static async Task<(string ProductGuid, string? Announcement)> ResolveProjectAsync(
        ProjectService projects, string? handle, RequestContext<CallToolRequestParams> context)
    {
        if (context.Services is not { } services || context.Server is not { } server)
        {
            // Every real call — live or a hades_regression tool-format replay, which reuses the
            // OUTER call's own Server/Services rather than leaving either null (see
            // EditorProjectTools.ReplayToolCallAsync) — has both. Defensive only.
            throw new McpException(
                "This tool call has no live request context to resolve a project against — this "
                + "should never happen for a real MCP client call.");
        }

        var router = services.GetRequiredService<RootsRouter>();
        var rootsProvider = services.GetService<IRootsProvider>() ?? new McpRootsProvider(server);

        var resolution = await ResolveProjectAsync(projects, handle, router, rootsProvider).ConfigureAwait(false);

        if (context.Items is { } items)
        {
            // Always recorded, not just on auto-adopt: Program.cs's RecordTrace files the trace
            // under this guid. The synchronous ResolveProject it falls back to cannot re-derive a
            // roots-based answer (several known projects, no handle - it throws), so without this
            // hand-off exactly the calls the seamless path serves would be the untraced ones.
            items[ResolvedProjectItemsKey] = resolution.ProductGuid;
            if (resolution.Announcement is not null)
                items[AnnouncementItemsKey] = resolution.Announcement;
        }

        return resolution;
    }

    /// <summary>Key <see cref="ResolveProjectAsync(ProjectService,string?,RequestContext{CallToolRequestParams})"/>
    /// records a non-null announcement under, on the current call's own request-scoped
    /// <see cref="MessageContext.Items"/> — read back by <see cref="AppendAnnouncement"/>, and by
    /// nothing else (not part of any wire-visible contract).</summary>
    internal const string AnnouncementItemsKey = "hades.rootsAnnouncement";

    /// <summary>Key the same overload records the resolved <c>ProductGuid</c> under, on every
    /// successful resolution — read back by Program.cs's RecordTrace so the trace is filed under
    /// the project the call actually ran against, even when only roots could have picked it.
    /// Like <see cref="AnnouncementItemsKey"/>, never part of any wire-visible contract.</summary>
    public const string ResolvedProjectItemsKey = "hades.resolvedProject";

    /// <summary>
    /// Appends <paramref name="context"/>'s own recorded auto-adopt announcement (if any — see
    /// <see cref="AnnouncementItemsKey"/>) to <paramref name="result"/> as one more
    /// <see cref="TextContentBlock"/>, so a caller whose call silently auto-registered a brand-new
    /// project actually sees that happened — see <see cref="RootResolution.Announcement"/>'s own
    /// doc comment: "A tool surfacing this result appends it to the reply so the caller knows a new
    /// project just entered scope". Called exactly once, uniformly, from Program.cs's own
    /// CallToolFilters chain, after EVERY tool call — not from each tool individually, and not
    /// wired into any individual result record's own shape — the "extra content block" design
    /// chosen over adding an Announcement field to each of the many result record types this
    /// server returns, so no existing result type or its already-advertised outputSchema changes.
    /// Appended regardless of <see cref="CallToolResult.IsError"/>: the announcement names an
    /// already-committed side effect (the project genuinely is now registered) independent of
    /// whether the rest of THIS particular call went on to succeed. Touches only
    /// <see cref="CallToolResult.Content"/> — appending, never replacing — so
    /// <see cref="CallToolResult.StructuredContent"/> and every existing entry already in
    /// <see cref="CallToolResult.Content"/> (including index 0, which every error-text assertion in
    /// this codebase's own tests reads) are untouched.
    /// </summary>
    public static CallToolResult AppendAnnouncement(RequestContext<CallToolRequestParams> context, CallToolResult result)
    {
        if (context.Items is not { } items
            || !items.TryGetValue(AnnouncementItemsKey, out var raw)
            || raw is not string announcement)
        {
            return result;
        }

        result.Content = [.. result.Content, new TextContentBlock { Text = announcement }];
        return result;
    }
}
