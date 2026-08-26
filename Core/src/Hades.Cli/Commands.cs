using Hades.Control.Client;
using Hades.Control.Client.Dtos;

namespace Hades.Cli;

/// <summary>
/// The three commands this CLI exists to prove: that <c>/control/summary</c>, <c>/control/projects</c>,
/// and <c>POST /control/leases/{id}/release</c> need no client-side computation to render (Plan 11
/// Task 7). Every value printed below is read straight off a <c>Hades.Control.Client.Dtos</c> record
/// the server already resolved, and printed verbatim - never summed, formatted, compared, or mapped
/// to a label. If a field were discovered here that genuinely needed derivation (formatting a
/// duration, comparing a timestamp, choosing a severity), the right move is to stop and record that
/// as an audit finding, not to write the logic - see the plan document's own "No-logic audit"
/// section for where any such finding belongs. As implemented, no such case arose: none of these
/// three responses needed it.
///
/// The one presence check every command below performs (is a field null / is a list empty) is not
/// that kind of derivation - it is reading the SHAPE of the response as the server already resolved
/// it (e.g. <c>Lease</c> is non-null if and only if one is actually held - see SummaryResult's own
/// doc comment), never inventing filler text ("(unknown)", a synthesized default) for a field the
/// server left null.
///
/// Built on <see cref="ControlClient"/>, not a raw <see cref="HttpClient"/>: this is
/// <c>ControlClient</c>'s one consumer that ships. <c>Hades.Cli.Tests/CommandsTests.cs</c> still runs
/// every one of these against a real loopback <c>ControlListener</c> - only the client it hands in
/// is now a <c>ControlClient</c> built over that listener's connection, not a bare
/// <see cref="HttpClient"/>.
/// </summary>
public static class Commands
{
    public static Task<int> StatusAsync(ControlClient client, TextWriter output) =>
        RunAsync(client.SummaryAsync, output, summary =>
        {
            output.WriteLine($"icon:     {Wire(summary.IconState)}");
            output.WriteLine($"headline: {summary.Headline}");
            output.WriteLine();
            output.WriteLine("projects:");

            if (summary.Rows.Count == 0)
            {
                output.WriteLine("  (none)");
            }
            else
            {
                foreach (var row in summary.Rows)
                {
                    output.WriteLine($"  - {row.Project}: {row.Status} [{Wire(row.Severity)}]");
                }
            }

            if (summary.Lease is { } lease)
            {
                output.WriteLine();
                output.WriteLine("lease:");
                output.WriteLine($"  project:           {lease.Project}");
                output.WriteLine($"  leaseId:           {lease.LeaseId}");
                output.WriteLine($"  heldForSeconds:    {lease.HeldForSeconds}");
                output.WriteLine($"  expiresInSeconds:  {lease.ExpiresInSeconds}");
                output.WriteLine($"  releasable:        {lease.Releasable}");
            }

            return 0;
        });

    public static Task<int> ProjectsAsync(ControlClient client, TextWriter output) =>
        RunAsync(client.ProjectsAsync, output, result =>
        {
            if (result.Projects.Count == 0)
            {
                output.WriteLine("(no projects)");
                return 0;
            }

            foreach (var p in result.Projects)
            {
                output.WriteLine($"- {p.Name}");
                output.WriteLine($"    path:         {p.Path}");
                output.WriteLine($"    productGuid:  {p.ProductGuid}");
                output.WriteLine($"    unityVersion: {p.UnityVersion}");
                output.WriteLine($"    indexState:   {Wire(p.IndexState)}");
                output.WriteLine($"    indexStatus:  {p.IndexStatus}");
                output.WriteLine($"    nodeCount:    {p.NodeCount}");
                output.WriteLine($"    edgeCount:    {p.EdgeCount}");
                output.WriteLine($"    editor:       {p.Editor.Status}");

                foreach (var w in p.Warnings)
                {
                    output.WriteLine($"    warning [{Wire(w.Severity)}] {w.Code}: {w.Message}");
                    output.WriteLine($"      remedy: {w.Remedy}");
                }
            }

            return 0;
        });

    public static Task<int> ReleaseAsync(ControlClient client, TextWriter output, string leaseId) =>
        RunAsync(() => client.ReleaseLeaseAsync(leaseId), output, result =>
        {
            output.WriteLine($"success: {result.Success}");
            output.WriteLine($"message: {result.Message}");

            return result.Success ? 0 : 1;
        });

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>The one call-and-render step every command above shares: await <paramref name="call"/>,
    /// and on a <see cref="ControlClientException"/> print the server's OWN message (every error
    /// response in this API includes one - see e.g. <c>ProjectsEndpoint.Remove</c>,
    /// <c>EditorsEndpoint.ReleaseAsync</c> - and <see cref="ControlClient"/> already extracts it
    /// rather than inventing one) and exit 1. Otherwise hand the decoded result to
    /// <paramref name="render"/>, which prints it and returns the exit code.</summary>
    static async Task<int> RunAsync<TResult>(
        Func<Task<TResult>> call, TextWriter output, Func<TResult, int> render)
    {
        TResult result;
        try
        {
            result = await call();
        }
        catch (ControlClientException ex)
        {
            output.WriteLine($"error: {ex.Message}");
            return 1;
        }

        return render(result);
    }

    /// <summary>The exact wire text a closed control-API enum was decoded from: the inverse of
    /// <c>Hades.Control.Client.UnknownFallbackConverter{T}</c>'s own <c>Write</c>, which lowercases
    /// only the enum member's first letter (<c>LeaseHeld</c> -&gt; <c>"leaseHeld"</c>, <c>Ok</c> -&gt;
    /// <c>"ok"</c>) - the deterministic reconstruction of the exact string every
    /// <c>[JsonStringEnumMemberName]</c> on the server side already spells this way, never a
    /// client-invented label.</summary>
    static string Wire<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
