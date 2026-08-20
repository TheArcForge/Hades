using System.Net.Http.Json;
using System.Text.Json;

namespace Hades.Cli;

/// <summary>
/// The three commands this CLI exists to prove: that <c>/control/summary</c>, <c>/control/projects</c>,
/// and <c>POST /control/leases/{id}/release</c> need no client-side computation to render (Plan 11
/// Task 7). Every value printed below is read straight off a JSON field the server already resolved,
/// and printed verbatim - never summed, formatted, compared, or mapped to a label. If a field were
/// discovered here that genuinely needed derivation (formatting a duration, comparing a timestamp,
/// choosing a severity), the right move is to stop and record that as an audit finding, not to write
/// the logic - see the plan document's own "No-logic audit" section for where any such finding
/// belongs. As implemented, no such case arose: none of these three responses needed it.
///
/// The one presence check every command below performs (is a field null / is an array empty) is not
/// that kind of derivation - it is reading the SHAPE of the response as the server already resolved
/// it (e.g. <c>lease</c> is present in the JSON if and only if one is actually held - see
/// SummaryResult's own doc comment), never inventing filler text ("(unknown)", a synthesized default)
/// for a field the server left null. See <see cref="OrNull"/>.
/// </summary>
public static class Commands
{
    public static async Task<int> StatusAsync(HttpClient client, TextWriter output)
    {
        var response = await client.GetAsync("/control/summary");
        if (await ReadBodyAsync(response, output) is not { } summary) return 1;

        output.WriteLine($"icon:     {summary.GetProperty("iconState").GetString()}");
        output.WriteLine($"headline: {summary.GetProperty("headline").GetString()}");
        output.WriteLine();
        output.WriteLine("projects:");

        var rows = summary.GetProperty("rows");
        if (rows.GetArrayLength() == 0)
        {
            output.WriteLine("  (none)");
        }
        else
        {
            foreach (var row in rows.EnumerateArray())
            {
                output.WriteLine($"  - {row.GetProperty("project").GetString()}: {row.GetProperty("status").GetString()} [{row.GetProperty("severity").GetString()}]");
            }
        }

        if (summary.TryGetProperty("lease", out var lease) && lease.ValueKind != JsonValueKind.Null)
        {
            output.WriteLine();
            output.WriteLine("lease:");
            output.WriteLine($"  project:           {lease.GetProperty("project").GetString()}");
            output.WriteLine($"  leaseId:           {lease.GetProperty("leaseId").GetString()}");
            output.WriteLine($"  heldForSeconds:    {lease.GetProperty("heldForSeconds").GetInt32()}");
            output.WriteLine($"  expiresInSeconds:  {lease.GetProperty("expiresInSeconds").GetInt32()}");
            output.WriteLine($"  releasable:        {lease.GetProperty("releasable").GetBoolean()}");
        }

        return 0;
    }

    public static async Task<int> ProjectsAsync(HttpClient client, TextWriter output)
    {
        var response = await client.GetAsync("/control/projects");
        if (await ReadBodyAsync(response, output) is not { } result) return 1;

        var projects = result.GetProperty("projects");
        if (projects.GetArrayLength() == 0)
        {
            output.WriteLine("(no projects)");
            return 0;
        }

        foreach (var p in projects.EnumerateArray())
        {
            output.WriteLine($"- {p.GetProperty("name").GetString()}");
            output.WriteLine($"    path:         {p.GetProperty("path").GetString()}");
            output.WriteLine($"    productGuid:  {p.GetProperty("productGuid").GetString()}");
            output.WriteLine($"    unityVersion: {OrNull(p, "unityVersion")}");
            output.WriteLine($"    indexState:   {p.GetProperty("indexState").GetString()}");
            output.WriteLine($"    indexStatus:  {p.GetProperty("indexStatus").GetString()}");
            output.WriteLine($"    nodeCount:    {p.GetProperty("nodeCount").GetInt32()}");
            output.WriteLine($"    edgeCount:    {p.GetProperty("edgeCount").GetInt32()}");
            output.WriteLine($"    editor:       {p.GetProperty("editor").GetProperty("status").GetString()}");

            foreach (var w in p.GetProperty("warnings").EnumerateArray())
            {
                output.WriteLine($"    warning [{w.GetProperty("severity").GetString()}] {w.GetProperty("code").GetString()}: {w.GetProperty("message").GetString()}");
                output.WriteLine($"      remedy: {w.GetProperty("remedy").GetString()}");
            }
        }

        return 0;
    }

    public static async Task<int> ReleaseAsync(HttpClient client, TextWriter output, string leaseId)
    {
        var response = await client.PostAsync($"/control/leases/{Uri.EscapeDataString(leaseId)}/release", content: null);
        if (await ReadBodyAsync(response, output) is not { } result) return 1;

        var success = result.GetProperty("success").GetBoolean();
        output.WriteLine($"success: {success}");
        output.WriteLine($"message: {result.GetProperty("message").GetString()}");

        return success ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>The one JSON-reading step every command above shares: parse the body, and on a
    /// non-2xx response print the server's OWN "error" field (every error response in this API
    /// includes one - see e.g. ProjectsEndpoint.Remove, EditorsEndpoint.ReleaseAsync) rather than a
    /// message invented here. Every command treats a null return as "already reported, exit 1".</summary>
    static async Task<JsonElement?> ReadBodyAsync(HttpResponseMessage response, TextWriter output)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        if (!response.IsSuccessStatusCode)
        {
            var message = body.TryGetProperty("error", out var error) ? error.GetString() : response.ReasonPhrase;
            output.WriteLine($"error: {message}");
            return null;
        }

        return body;
    }

    /// <summary>A field's string value, or null when absent - not a fallback label like "(unknown)":
    /// see this class's own doc comment on why inventing display text here would be exactly the kind
    /// of client-side decision this CLI exists to flag, not perform quietly. TryGetProperty, not
    /// GetProperty: every Control endpoint's JSON options set DefaultIgnoreCondition.WhenWritingNull
    /// (see ControlListener's own doc comment on why - "a field the shell should treat as simply not
    /// there... must actually be absent from the JSON"), so a null field is OMITTED entirely, never
    /// present as a literal JSON null - GetProperty would throw KeyNotFoundException on exactly the
    /// case this helper exists to handle.</summary>
    static string? OrNull(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;
}
