using Hades.Server.Control;

namespace Hades.Control.Client.Tests;

/// <summary>One exemplar per wire DTO, built from the SERVER's own types so each fixture is what
/// the server would really send. Nullable fields are left null in at least one exemplar per type
/// that has them: WhenWritingNull means they must be ABSENT from the JSON, and that absence is
/// exactly what a reflection test cannot see. Every wire property is <c>required</c>, so a missing
/// one below is a compile error - a fully compiling file is itself proof each exemplar is
/// complete.</summary>
public static class Exemplars
{
    public static IEnumerable<(string Name, object Value)> All()
    {
        // -------------------------------------------------------------------------------- traces

        var spanAttributeRow = new SpanAttributeRow
        {
            Key = "resultSizeBytes",
            ValueDisplay = "1024",
        };
        yield return ("span_attribute_row", spanAttributeRow);

        var spanRowPresent = new SpanRow
        {
            SpanId = "span-1",
            ParentSpanId = "span-0",
            Name = "hades_search",
            Kind = "tool",
            StartUtcMs = 1_700_000_000_000,
            EndUtcMs = 1_700_000_000_120,
            DurationMs = 120,
            Status = "ok",
            Attributes = [spanAttributeRow],
            Events = [spanAttributeRow],
        };
        var spanRowAbsent = spanRowPresent with
        {
            ParentSpanId = null,
            EndUtcMs = null,
            DurationMs = null,
            Status = null,
            Attributes = null,
            Events = null,
        };
        yield return ("span_row", spanRowPresent);
        yield return ("span_row_absent", spanRowAbsent);

        var traceDetailResultPresent = new TraceDetailResult
        {
            TraceId = "trace-1",
            Tool = "hades_search",
            StartUtcMs = 1_700_000_000_000,
            EndUtcMs = 1_700_000_000_120,
            DurationMs = 120,
            Outcome = TraceOutcome.Ok,
            Spans = [spanRowPresent],
        };
        var traceDetailResultAbsent = traceDetailResultPresent with { EndUtcMs = null, DurationMs = null };
        yield return ("trace_detail_result", traceDetailResultPresent);
        yield return ("trace_detail_result_absent", traceDetailResultAbsent);

        var traceSequenceRow = new TraceSequenceRow
        {
            Id = "seq-1",
            Tools = ["hades_search", "hades_read"],
            Pattern = "hades_search -> hades_read",
            CallCount = 2,
            StartUtcMs = 1_700_000_000_000,
            EndUtcMs = 1_700_000_000_500,
            DurationMs = 500,
            Outcome = TraceOutcome.Ok,
            TraceIds = ["trace-1", "trace-2"],
        };
        yield return ("trace_sequence_row", traceSequenceRow);

        yield return ("trace_sequences_result", new TraceSequencesResult
        {
            Sequences = [traceSequenceRow],
            Truncated = false,
        });

        var slowToolRow = new SlowToolRow
        {
            Tool = "hades_search",
            CallCount = 12,
            AverageDurationMs = 57.9,
            MaxDurationMs = 210,
        };
        yield return ("slow_tool_row", slowToolRow);

        yield return ("slow_tools_result", new SlowToolsResult
        {
            Tools = [slowToolRow],
            Truncated = false,
        });

        var failedCallRowPresent = new FailedCallRow
        {
            TraceId = "trace-1",
            Tool = "hades_search",
            StartUtcMs = 1_700_000_000_000,
            DurationMs = 42,
            Error = "Timed out waiting for the Editor.",
        };
        var failedCallRowAbsent = failedCallRowPresent with { DurationMs = null, Error = null };
        yield return ("failed_call_row", failedCallRowPresent);
        yield return ("failed_call_row_absent", failedCallRowAbsent);

        yield return ("failed_calls_result", new FailedCallsResult
        {
            Failures = [failedCallRowPresent],
            Truncated = false,
        });

        // ------------------------------------------------------------------------------ projects

        var projectWarning = new ProjectWarning
        {
            Code = "missingPath",
            Severity = ControlSeverity.Warning,
            Message = "The project's folder could not be found on disk.",
            Remedy = "Reconnect the project or remove it from the list.",
        };
        yield return ("project_warning", projectWarning);

        var projectEditorInfoPresent = new ProjectEditorInfo
        {
            State = ProjectEditorState.Attached,
            Status = "Editor attached",
            UnityVersion = "2022.3.10f1",
            ProcessId = 4321,
            ConnectionAgeSeconds = 17,
        };
        var projectEditorInfoAbsent = projectEditorInfoPresent with
        {
            State = ProjectEditorState.Absent,
            Status = "No Editor attached",
            UnityVersion = null,
            ProcessId = null,
            ConnectionAgeSeconds = null,
        };
        yield return ("project_editor_info", projectEditorInfoPresent);
        yield return ("project_editor_info_absent", projectEditorInfoAbsent);

        var projectRowPresent = new ProjectRow
        {
            Name = "Hades-Unity-Client",
            Path = "/Users/mike/Projects/Hades-Unity-Client",
            ProductGuid = "15c012f27331e49229cef25e74537816",
            UnityVersion = "2022.3.10f1",
            IndexState = ProjectIndexState.Indexed,
            IndexStatus = "Indexed, 1204 nodes",
            NodeCount = 1204,
            EdgeCount = 3820,
            Editor = projectEditorInfoPresent,
            Warnings = [projectWarning],
        };
        var projectRowAbsent = projectRowPresent with { UnityVersion = null };
        yield return ("project_row", projectRowPresent);
        yield return ("project_row_absent", projectRowAbsent);

        yield return ("projects_result", new ProjectsResult { Projects = [projectRowPresent] });

        yield return ("add_project_request", new AddProjectRequest
        {
            Path = "/Users/mike/Projects/Hades-Unity-Client",
        });

        yield return ("action_result", new ActionResult
        {
            Success = true,
            Message = "Released the reload lease for 'Hades-Unity-Client'.",
        });

        yield return ("install_plugin_result", new InstallPluginResult
        {
            Success = true,
            NeedsRestart = true,
            Message = "Plugin installed. Restart Unity to pick it up.",
        });

        yield return ("rebuild_started_result", new RebuildStartedResult { OperationId = "op-1" });

        var rebuildOperationResult = new RebuildOperationResult
        {
            NodesBefore = 1180,
            NodesAfter = 1204,
            Message = "Rebuilt: 24 nodes added.",
        };
        yield return ("rebuild_operation_result", rebuildOperationResult);

        var operationResultPresent = new OperationResult
        {
            Id = "op-1",
            Kind = "rebuild",
            State = OperationState.Done,
            StartedAtUtc = DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
            FinishedAtUtc = DateTimeOffset.Parse("2026-06-01T12:00:05Z"),
            ElapsedSeconds = 5,
            Progress = "120 of 400 files",
            Error = null,
            Result = rebuildOperationResult,
        };
        var operationResultAbsent = operationResultPresent with
        {
            State = OperationState.Running,
            FinishedAtUtc = null,
            Progress = null,
            Error = null,
            Result = null,
        };
        yield return ("operation_result", operationResultPresent);
        yield return ("operation_result_absent", operationResultAbsent);

        // -------------------------------------------------------------------------------- editors

        var editorRowPresent = new EditorRow
        {
            Project = "Hades-Unity-Client",
            ProductGuid = "15c012f27331e49229cef25e74537816",
            State = ProjectEditorState.Attached,
            Status = "Editor attached",
            UnityVersion = "2022.3.10f1",
            ProcessId = 4321,
            ConnectionAgeSeconds = 17,
        };
        var editorRowAbsent = editorRowPresent with
        {
            UnityVersion = null,
            ProcessId = null,
            ConnectionAgeSeconds = null,
        };
        yield return ("editor_row", editorRowPresent);
        yield return ("editor_row_absent", editorRowAbsent);

        yield return ("editors_result", new EditorsResult { Editors = [editorRowPresent] });

        // ------------------------------------------------------------------------------- summary

        var summaryRow = new SummaryRow
        {
            Project = "Hades-Unity-Client",
            ProductGuid = "15c012f27331e49229cef25e74537816",
            Status = "Indexed, 1204 nodes",
            Severity = ControlSeverity.Ok,
        };
        yield return ("summary_row", summaryRow);

        var summaryLease = new SummaryLease
        {
            Project = "Hades-Unity-Client",
            LeaseId = "15c012f27331e49229cef25e74537816",
            HeldForSeconds = 42,
            ExpiresInSeconds = 18,
            Releasable = true,
        };
        yield return ("summary_lease", summaryLease);

        var summaryResultPresent = new SummaryResult
        {
            IconState = ControlIconState.LeaseHeld,
            Headline = "1 project holds a reload lease",
            Rows = [summaryRow],
            Lease = summaryLease,
        };
        var summaryResultAbsent = summaryResultPresent with
        {
            IconState = ControlIconState.Idle,
            Headline = "No Unity Editor attached",
            Lease = null,
        };
        yield return ("summary_result", summaryResultPresent);
        yield return ("summary_result_absent", summaryResultAbsent);

        // -------------------------------------------------------------------------------- memory

        var memoryDocumentRowPresent = new MemoryDocumentRow
        {
            Name = "project_hades_overview.md",
            SizeBytes = 4096,
            SizeDisplay = "4.0 KB",
            LastReviewed = "2026-06-01",
        };
        var memoryDocumentRowAbsent = memoryDocumentRowPresent with { LastReviewed = null };
        yield return ("memory_document_row", memoryDocumentRowPresent);
        yield return ("memory_document_row_absent", memoryDocumentRowAbsent);

        var memoryProposalRowPresent = new MemoryProposalRow
        {
            FileName = "20260601-observation.md",
            TargetFile = "project_hades_overview.md",
            CreatedAtUtc = DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
            CreatedAgo = "3 days ago",
            Rationale = "Captures a durable fact worth remembering.",
            Status = "pending",
            Content = "Observed: the control API omits null fields rather than writing them as null.",
        };
        var memoryProposalRowAbsent = memoryProposalRowPresent with { CreatedAtUtc = null, CreatedAgo = null };
        yield return ("memory_proposal_row", memoryProposalRowPresent);
        yield return ("memory_proposal_row_absent", memoryProposalRowAbsent);

        yield return ("memory_result", new MemoryResult
        {
            Documents = [memoryDocumentRowPresent],
            Proposals = [memoryProposalRowPresent],
        });

        yield return ("memory_document_result", new MemoryDocumentResult
        {
            Name = "project_hades_overview.md",
            Content = "# Hades overview\n\nThree-process architecture...",
        });

        yield return ("write_memory_document_request", new WriteMemoryDocumentRequest
        {
            Content = "# Hades overview\n\nUpdated content...",
        });

        // ------------------------------------------------------------------------------ settings

        var mcpPortSetting = new McpPortSetting
        {
            Port = 61234,
            InUse = false,
            Message = "Port is free.",
        };
        yield return ("mcp_port_setting", mcpPortSetting);

        var logLevelSetting = new LogLevelSetting { Level = "info" };
        yield return ("log_level_setting", logLevelSetting);

        yield return ("settings_result", new SettingsResult
        {
            McpPort = mcpPortSetting,
            LogLevel = logLevelSetting,
        });
    }
}
