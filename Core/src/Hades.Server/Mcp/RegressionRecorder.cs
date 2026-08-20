using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Hades.Server.Mcp;

/// <summary>
/// F15's recording seam for hades_regression: a server-side singleton that, while a session is
/// active, captures every MCP tool call's name, arguments, and result - see Program.cs's own
/// CallToolFilters registration, the one place that wraps EVERY tool dispatch regardless of whether
/// the tool answers from the attached Unity Editor, the graph database, or a file on disk.
///
/// This is the fix for the measured gap: the OLD recording seam (UnityPlugin's
/// ProjectCommands._recordingCalls, offered every SUCCESSFUL wire-level dispatch by
/// CommandTable.Dispatch via ProjectCommands.CaptureIfRecording) only ever saw wire calls the
/// attached Editor itself executed. A session given six mixed tool calls captured only the two
/// Editor-routed ones - find_references_to/graph_query/trace_dependencies/project_settings are
/// graph- or disk-served and never touch the Editor at all, so they were invisible. Moving the seam
/// here, to the one layer above every tool regardless of how it answers, closes that gap uniformly
/// with no per-tool opt-in. hades_regression itself is excluded by its own caller (Program.cs), not
/// here, so this class stays a plain, tool-agnostic capture buffer with no knowledge of its own
/// caller's name.
///
/// hades_regression's own 'replay' action still understands the OLD, wire-method-shaped fixture
/// entries this seam never produces - see RegressionCallSpec/RegressionRecordedCallResult's own
/// 'Format' doc comments in EditorProjectTools.cs. This class only ever captures the NEW shape,
/// tagged <see cref="ToolFormat"/>, so the already-shipped editor-routed.json fixture (no 'format'
/// field at all) keeps replaying exactly as it did before this class existed.
/// </summary>
public sealed class RegressionRecorder
{
    /// <summary>The 'format' value hades_regression's 'stop' stamps on every entry this class
    /// captures, and 'replay' checks to route an entry to the in-process tool-invocation path
    /// instead of the legacy wire-dispatch one. Absent (null) on every entry recorded before this
    /// class existed, and on the shipped editor-routed.json fixture - see this class's own doc
    /// comment.</summary>
    public const string ToolFormat = "tool";

    readonly object _gate = new();
    List<Entry>? _entries;

    /// <summary>One captured tool call: its MCP name, the arguments it was invoked with (a defensive
    /// copy - see <see cref="Capture"/>'s own doc comment for why), and its result already reduced
    /// to a single comparable <see cref="JsonElement"/> (see <see cref="Normalize"/>).</summary>
    public sealed record Entry(string Tool, IReadOnlyDictionary<string, JsonElement>? Arguments, JsonElement Result);

    /// <summary>Begins an empty recording session. Returns false, changing nothing, if a session is
    /// already active - the caller (hades_regression's 'start' action) is what turns that into a
    /// refusal; this class only reports it, the same separation ScriptEditingSessionResult's own
    /// lease bookkeeping keeps from the tool method that surfaces it as an error.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_entries is not null) return false;
            _entries = new List<Entry>();
            return true;
        }
    }

    /// <summary>Ends the active session (if any) and returns everything captured, oldest first.
    /// Idempotent when nothing was active (never started, already stopped) - an empty list, never an
    /// error, the same "closing something never opened is a safe no-op" contract the old Editor-side
    /// session used.</summary>
    public IReadOnlyList<Entry> Stop()
    {
        lock (_gate)
        {
            IReadOnlyList<Entry> entries = _entries ?? new List<Entry>();
            _entries = null;
            return entries;
        }
    }

    /// <summary>Appends one completed tool call to the active session, if any - a no-op when no
    /// session is open. Called from Program.cs's CallToolFilters after every tool dispatch that
    /// returned rather than threw (a thrown exception has no CallToolResult worth pinning as an
    /// 'expected' value - the same "only a successful call is captured" convention the old
    /// Editor-side seam used). <paramref name="arguments"/> is copied rather than stored by
    /// reference: the SDK may reuse or mutate its own backing dictionary across calls, and a
    /// captured entry must freeze the exact values this call was actually made with.</summary>
    public void Capture(string tool, IDictionary<string, JsonElement>? arguments, CallToolResult result)
    {
        lock (_gate)
        {
            if (_entries is null) return;

            var frozenArguments = arguments is { Count: > 0 }
                ? new Dictionary<string, JsonElement>(arguments)
                : null;
            _entries.Add(new Entry(tool, frozenArguments, Normalize(result)));
        }
    }

    /// <summary>
    /// Reduces a tool's full <see cref="CallToolResult"/> to the one JSON value worth recording and
    /// later comparing: <see cref="CallToolResult.StructuredContent"/> when the tool set it (every
    /// Hades tool registered with UseStructuredContent=true does - see Program.cs's WithTools chain),
    /// otherwise a small object built from <see cref="CallToolResult.IsError"/> and the joined text
    /// content, so a tool that answers only in plain text still normalizes to something comparable
    /// rather than silently recording as null.
    ///
    /// Shared by capture (<see cref="Capture"/>, above) and replay (EditorProjectTools.HadesRegression's
    /// tool-shaped replay path) so the two can never drift into comparing values built two different
    /// ways - but the two call it at different times, for different reasons, and only replay ever
    /// hands it an IsError result in practice. Capture only ever runs for a call Program.cs's own
    /// CallToolFilters chain finished dispatching normally (see that method's own doc comment); a
    /// GUARD REFUSAL - the hoisted, validate-before-resolving kind (SettingsTools.ProjectSettings's
    /// own comment on why those checks run first) - never reaches it at all: it either throws past
    /// Capture's own call site entirely, or (F13a's unknown-parameter rejection) returns before that
    /// call site ever runs. So the IsError branch below is not, today, capturing refusals live - it
    /// exists so a REPLAYED call's fresh result (EditorProjectTools.ReplayToolCallAsync's own
    /// `RegressionRecorder.Normalize(actual)`), which certainly can be IsError, still normalizes
    /// into the same shape the recorded 'expected' value used, so the two remain comparable.
    /// </summary>
    public static JsonElement Normalize(CallToolResult result)
    {
        if (result.StructuredContent is { } structured) return structured;

        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return JsonSerializer.SerializeToElement(new { isError = result.IsError ?? false, text });
    }
}
