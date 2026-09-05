using System.Runtime.InteropServices;
using System.Text.Json;
using Hades.Control.Client;
using Hades.Control.Client.Dtos;

namespace Hades.Cli;

/// <summary>
/// Every <c>hades</c> command. The CLI began as a proof that the control API needed no client-side
/// computation to render, and it still holds itself to that: it is now a product surface (Spec #5
/// §5.4), but the rule that made it worth trusting has not moved.
///
/// Every value printed below is read straight off a <c>Hades.Control.Client.Dtos</c> record
/// the server already resolved, and printed verbatim - never summed, formatted, compared, or mapped
/// to a label. If a field were discovered here that genuinely needed derivation (formatting a
/// duration, comparing a timestamp, choosing a severity), the right move is to stop and record that
/// as an audit finding, not to write the logic - see the plan document's own "No-logic audit"
/// section for where any such finding belongs. As implemented, no such case has arisen: not one of
/// these responses has needed it.
///
/// <see cref="DiagnoseAsync"/> is the single deliberate exception, and a narrow one: it reports facts
/// about the MACHINE, which the core cannot see and therefore cannot author. It still prints no
/// server-derived text of its own.
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

    // -------------------------------------------------------------------------- project mutations
    //
    // Each of these is one call and one print. None inspects a ProjectRow to decide whether the
    // action is allowed, none retries, and none words its own outcome: the server's message IS the
    // outcome, and where a route answers no message at all (add) the effect itself is the feedback.

    /// <summary><c>hades add-project &lt;path&gt;</c>. The response is the new row, not a message -
    /// so the row is what gets printed. Whether the folder is actually a Unity project is the
    /// core's call, and its refusal text is printed verbatim by <see cref="RunAsync"/>.</summary>
    public static Task<int> AddProjectAsync(ControlClient client, TextWriter output, string path) =>
        RunAsync(() => client.AddProjectAsync(path), output, row =>
        {
            output.WriteLine($"added: {row.Name}");
            output.WriteLine($"  path:         {row.Path}");
            output.WriteLine($"  productGuid:  {row.ProductGuid}");
            output.WriteLine($"  indexStatus:  {row.IndexStatus}");

            return 0;
        });

    public static Task<int> RemoveProjectAsync(ControlClient client, TextWriter output, string productGuid) =>
        RunAsync(() => client.RemoveProjectAsync(productGuid), output, PrintAction(output));

    /// <summary>
    /// <c>hades rebuild &lt;guid&gt;</c>. Prints the operation id and returns - it does NOT poll to
    /// completion. Rebuilding is asynchronous server-side, and a CLI that blocked until it finished
    /// would be inventing a progress model the route does not offer; <c>hades operation &lt;id&gt;</c>
    /// is how you ask again.
    /// </summary>
    public static Task<int> RebuildAsync(ControlClient client, TextWriter output, string productGuid) =>
        RunAsync(() => client.RebuildProjectAsync(productGuid), output, started =>
        {
            output.WriteLine($"operationId: {started.OperationId}");
            return 0;
        });

    /// <summary><c>hades install-plugin &lt;guid&gt;</c>. The message already says whether a restart
    /// is needed; <c>needsRestart</c> is printed as the raw field beside it, never re-worded into a
    /// second sentence of our own.</summary>
    public static Task<int> InstallPluginAsync(ControlClient client, TextWriter output, string productGuid) =>
        RunAsync(() => client.InstallPluginAsync(productGuid), output, result =>
        {
            output.WriteLine($"success:      {result.Success}");
            output.WriteLine($"needsRestart: {result.NeedsRestart}");
            output.WriteLine($"message:      {result.Message}");

            return result.Success ? 0 : 1;
        });

    /// <summary><c>hades operation &lt;id&gt;</c> - one tracked operation's current state.</summary>
    public static Task<int> OperationAsync(ControlClient client, TextWriter output, string id) =>
        RunAsync(() => client.OperationAsync(id), output, op =>
        {
            output.WriteLine($"id:             {op.Id}");
            output.WriteLine($"kind:           {op.Kind}");
            output.WriteLine($"state:          {Wire(op.State)}");
            output.WriteLine($"elapsedSeconds: {op.ElapsedSeconds}");

            if (op.Progress is { } progress) output.WriteLine($"progress:       {progress}");
            if (op.Error is { } error) output.WriteLine($"error:          {error}");

            return op.State == OperationState.Failed ? 1 : 0;
        });

    // --------------------------------------------------------------------------- read-only views

    /// <summary>
    /// <c>hades traces [project]</c>. Sequences, failures and slow tools each come from their OWN
    /// route - deliberately not filtered client-side out of the sequences list, because the server
    /// groups calls into sequences before filtering and doing it the other way round corrupts the
    /// grouping.
    /// </summary>
    public static async Task<int> TracesAsync(ControlClient client, TextWriter output, string? project)
    {
        try
        {
            var sequences = await client.TracesSequencesAsync(project);
            var failures = await client.TracesFailuresAsync(project);
            var slow = await client.TracesSlowAsync(project);

            output.WriteLine("sequences:");
            if (sequences.Sequences.Count == 0) output.WriteLine("  (none)");
            foreach (var s in sequences.Sequences)
            {
                output.WriteLine($"  - {s.Pattern} [{Wire(s.Outcome)}]");
                output.WriteLine($"      id:         {s.Id}");
                output.WriteLine($"      callCount:  {s.CallCount}");
                output.WriteLine($"      durationMs: {s.DurationMs}");
            }
            if (sequences.Truncated) output.WriteLine("  (truncated)");

            output.WriteLine();
            output.WriteLine("failures:");
            if (failures.Failures.Count == 0) output.WriteLine("  (none)");
            foreach (var f in failures.Failures)
            {
                output.WriteLine($"  - {f.Tool}");
                output.WriteLine($"      traceId: {f.TraceId}");
                if (f.Error is { } error) output.WriteLine($"      error:   {error}");
            }

            output.WriteLine();
            output.WriteLine("slow tools:");
            if (slow.Tools.Count == 0) output.WriteLine("  (none)");
            foreach (var t in slow.Tools)
            {
                output.WriteLine($"  - {t.Tool}");
                output.WriteLine($"      callCount:         {t.CallCount}");
                output.WriteLine($"      averageDurationMs: {t.AverageDurationMs}");
                output.WriteLine($"      maxDurationMs:     {t.MaxDurationMs}");
            }

            return 0;
        }
        catch (ControlClientException ex)
        {
            output.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary><c>hades memory [project]</c> - authored documents and the proposal queue, in the
    /// one round trip the route provides.</summary>
    public static Task<int> MemoryAsync(ControlClient client, TextWriter output, string? project) =>
        RunAsync(() => client.MemoryAsync(project), output, result =>
        {
            output.WriteLine("documents:");
            if (result.Documents.Count == 0) output.WriteLine("  (none)");
            foreach (var d in result.Documents)
            {
                output.WriteLine($"  - {d.Name}");
                output.WriteLine($"      sizeDisplay:  {d.SizeDisplay}");
                if (d.LastReviewed is { } reviewed) output.WriteLine($"      lastReviewed: {reviewed}");
            }

            output.WriteLine();
            output.WriteLine("proposals:");
            if (result.Proposals.Count == 0) output.WriteLine("  (none)");
            foreach (var p in result.Proposals)
            {
                output.WriteLine($"  - {p.FileName} -> {p.TargetFile} [{p.Status}]");
                output.WriteLine($"      rationale: {p.Rationale}");
            }

            return 0;
        });

    // ----------------------------------------------------------------------------------- diagnose

    /// <summary>
    /// <c>hades diagnose</c> - one command a bug reporter can run, for the whole class of
    /// environmental failures no CI machine will ever reproduce: OneDrive placeholders, antivirus
    /// holding files open, path-length limits, a Unity Hub somewhere unexpected.
    ///
    /// <b>It works with NO core running</b>, and that is the point rather than a fallback: a
    /// reporter whose core will not start is exactly the person who needs this, and refusing with
    /// "no Hades found" would fail precisely when it matters. <paramref name="client"/> is therefore
    /// nullable, and everything that does not need a core is reported either way.
    ///
    /// <b>IT MUST NEVER PRINT A SECRET.</b> This output goes straight into bug reports, pasted by
    /// people who will not read it first. The bearer token grants full control-API access on that
    /// machine, so only whether the discovery file EXISTS and PARSES is reported - never a byte of
    /// its contents.
    /// </summary>
    public static async Task<int> DiagnoseAsync(ControlClient? client, TextWriter output, string root)
    {
        output.WriteLine("Hades diagnostics");
        output.WriteLine();

        output.WriteLine("environment:");
        output.WriteLine($"  os:          {RuntimeInformation.OSDescription}");
        output.WriteLine($"  runtime:     {RuntimeInformation.FrameworkDescription}");

        // Both architectures, because the interesting case is when they DIFFER: an x64 process on an
        // arm64 machine is running under emulation, which is a plausible cause of "it is slow" or
        // "the native SQLite library will not load" reports and is invisible from either alone.
        output.WriteLine($"  processArch: {RuntimeInformation.ProcessArchitecture}");
        output.WriteLine($"  osArch:      {RuntimeInformation.OSArchitecture}");

        if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
        {
            output.WriteLine("  NOTE: process and OS architectures differ - this process is emulated.");
        }

        if (OperatingSystem.IsWindows())
        {
            // OSDescription gives the build but not the edition, and the edition changes real
            // behaviour: Home versus Pro decides whether Developer Mode is available, which in turn
            // decides whether an unelevated process may create symlinks at all.
            if (WindowsRegistryValue("ProductName") is { } edition)
            {
                output.WriteLine($"  edition:     {edition}");

                // ProductName still reads "Windows 10 ..." on Windows 11 - Microsoft never updated
                // the key. Left as the raw value rather than rewritten, because a diagnostic must
                // report what the machine says; the caveat is added so a reader does not trust it
                // over the build number, which is authoritative. 22000 is Windows 11's first build.
                if (edition.Contains("Windows 10", StringComparison.OrdinalIgnoreCase)
                    && Environment.OSVersion.Version.Build >= 22000)
                {
                    output.WriteLine(
                        "               (registry ProductName reports \"Windows 10\" on Windows 11 too - "
                        + "the build number above is authoritative)");
                }
            }
            if (WindowsRegistryValue("DisplayVersion") is { } release) output.WriteLine($"  release:     {release}");

            // One of the four environmental failure classes this command exists for. A path over 260
            // characters fails differently depending on this, and nothing else in the report reveals
            // it - pathLength below only shows the symptom.
            output.WriteLine($"  longPaths:   {LongPathsEnabled()}");
        }

        output.WriteLine();
        output.WriteLine("storage:");
        output.WriteLine($"  root:        {root}");
        output.WriteLine($"  exists: {Directory.Exists(root)}");

        var tokenPath = Path.Combine(root, "control.token");
        var tokenExists = File.Exists(tokenPath);
        output.WriteLine($"  control.token: {tokenPath}");
        output.WriteLine($"  present: {tokenExists}");
        output.WriteLine($"  parses: {tokenExists && Parses(tokenPath)}");

        output.WriteLine();

        if (client is null)
        {
            output.WriteLine("core: not running (no control API to ask).");
            return 0;
        }

        try
        {
            var ping = await client.PingAsync();
            output.WriteLine("core:");
            output.WriteLine($"  version:       {ping.Version}");
            output.WriteLine($"  uptimeSeconds: {ping.UptimeSeconds}");
        }
        catch (ControlClientException ex)
        {
            // Reachable enough to have a client, but not answering: still a report, not an error.
            output.WriteLine($"core: not running ({ex.Message})");
            return 0;
        }

        output.WriteLine();
        output.WriteLine("projects:");

        try
        {
            var projects = await client.ProjectsAsync();
            if (projects.Projects.Count == 0) output.WriteLine("  (none)");

            foreach (var p in projects.Projects)
            {
                output.WriteLine($"  - {p.Name}");
                output.WriteLine($"      productGuid: {p.ProductGuid}");
                output.WriteLine($"      path:        {p.Path}");
                output.WriteLine($"      indexState:  {Wire(p.IndexState)}");
                output.WriteLine($"      indexStatus: {p.IndexStatus}");
                output.WriteLine($"      nodeCount:   {p.NodeCount}");
                output.WriteLine($"      edgeCount:   {p.EdgeCount}");
                output.WriteLine($"      oneDrive:    {LooksLikeOneDrivePath(p.Path)}");
                output.WriteLine($"      reparse:     {IsReparsePoint(p.Path)}");
                output.WriteLine($"      pathLength:  {p.Path.Length}");
            }
        }
        catch (ControlClientException ex)
        {
            output.WriteLine($"  error: {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// Whether a path sits under OneDrive, by name.
    ///
    /// Called out specifically because OneDrive's placeholder files are one of the failures this
    /// command exists for: a path can look entirely ordinary while its contents are not actually on
    /// disk, so an indexer reading it gets something no local reproduction ever will. A name match is
    /// a heuristic and deliberately reported as a plain fact rather than a diagnosis -
    /// <see cref="IsReparsePoint"/> is printed beside it, since a placeholder or a redirected folder
    /// shows up there too.
    /// </summary>
    public static bool LooksLikeOneDrivePath(string path) =>
        path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the path is a reparse point - a junction, a symlink, or a cloud placeholder
    /// root. Reported beside the OneDrive name check because either can explain a path that reads
    /// differently than it looks.</summary>
    static bool IsReparsePoint(string path)
    {
        try
        {
            return Directory.Exists(path)
                   && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception)
        {
            // An unreadable path is itself worth not crashing over; the row above already named it.
            return false;
        }
    }

    /// <summary>One HKLM CurrentVersion value, or null if it cannot be read. Windows-only, and
    /// guarded at the call site.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string? WindowsRegistryValue(string name)
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", name, null) as string;
        }
        catch (Exception)
        {
            // A locked-down machine that will not answer is itself worth not crashing over; the row
            // is simply omitted.
            return null;
        }
    }

    /// <summary>
    /// Whether Windows has long-path support enabled. Absent or 0 means the classic 260-character
    /// limit still applies, which is the difference between "Hades cannot index this project" and
    /// "Hades cannot index this project because the path is too long for this machine's settings".
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool LongPathsEnabled()
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem",
                "LongPathsEnabled",
                0) is int enabled && enabled == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Whether the discovery file is well-formed. Its CONTENTS are never printed.</summary>
    static bool Parses(string tokenPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(tokenPath));
            return document.RootElement.TryGetProperty("port", out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------------------------ helpers

    /// <summary>The shared renderer for every route answering a bare success/message pair.</summary>
    static Func<ActionResult, int> PrintAction(TextWriter output) => result =>
    {
        output.WriteLine($"success: {result.Success}");
        output.WriteLine($"message: {result.Message}");

        return result.Success ? 0 : 1;
    };

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
