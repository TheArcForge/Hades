using System.Text.Json.Serialization;
using Hades.Control.Client;

namespace Hades.Control.Client.Dtos;

/// <summary>Mirrors Hades.Server.Control.ProjectEditorState.</summary>
[JsonConverter(typeof(UnknownFallbackConverter<ProjectEditorState>))]
public enum ProjectEditorState { Unknown, Attached, Busy, Absent }

/// <summary>Mirrors Hades.Server.Control.ProjectIndexState. <c>Indexing</c> means work is running
/// right now; <c>NeverIndexed</c> means no index has ever completed and none is running. Those were
/// one member until 2026-09-01 - see the server-side enum for what conflating them cost.</summary>
[JsonConverter(typeof(UnknownFallbackConverter<ProjectIndexState>))]
public enum ProjectIndexState { Unknown, Indexed, Indexing, NeverIndexed }

/// <summary>Mirrors Hades.Server.Control.ProjectWarning.</summary>
public sealed record ProjectWarning
{
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("severity")] public required ControlSeverity Severity { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("remedy")] public required string Remedy { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.ProjectEditorInfo.</summary>
public sealed record ProjectEditorInfo
{
    [JsonPropertyName("state")] public required ProjectEditorState State { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("unityVersion")] public string? UnityVersion { get; init; }
    [JsonPropertyName("processId")] public long? ProcessId { get; init; }
    [JsonPropertyName("connectionAgeSeconds")] public int? ConnectionAgeSeconds { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.ProjectRow.</summary>
public sealed record ProjectRow
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("productGuid")] public required string ProductGuid { get; init; }
    [JsonPropertyName("unityVersion")] public string? UnityVersion { get; init; }
    [JsonPropertyName("indexState")] public required ProjectIndexState IndexState { get; init; }
    [JsonPropertyName("indexStatus")] public required string IndexStatus { get; init; }
    [JsonPropertyName("nodeCount")] public required int NodeCount { get; init; }
    [JsonPropertyName("edgeCount")] public required int EdgeCount { get; init; }
    [JsonPropertyName("editor")] public required ProjectEditorInfo Editor { get; init; }
    [JsonPropertyName("warnings")] public required IReadOnlyList<ProjectWarning> Warnings { get; init; }

    /// <summary>The operation indexing this project, when this row came back from an add. Null on
    /// every row from GET /control/projects. Mirrors Hades.Server.Control.ProjectRow.</summary>
    [JsonPropertyName("indexOperationId")] public string? IndexOperationId { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.ProjectsResult, the response of GET /control/projects.</summary>
public sealed record ProjectsResult
{
    [JsonPropertyName("projects")] public required IReadOnlyList<ProjectRow> Projects { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.AddProjectRequest, the body of POST /control/projects/add.</summary>
public sealed record AddProjectRequest
{
    [JsonPropertyName("path")] public required string Path { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.InstallPluginResult, the response of
/// POST /control/projects/{id}/installPlugin.</summary>
public sealed record InstallPluginResult
{
    [JsonPropertyName("success")] public required bool Success { get; init; }
    [JsonPropertyName("needsRestart")] public required bool NeedsRestart { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.RebuildStartedResult, the response of
/// POST /control/projects/{id}/rebuild.</summary>
public sealed record RebuildStartedResult
{
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.RebuildOperationResult, the result payload of a completed
/// rebuild operation polled via GET /control/operations/{id}.</summary>
public sealed record RebuildOperationResult
{
    [JsonPropertyName("nodesBefore")] public required int NodesBefore { get; init; }
    [JsonPropertyName("nodesAfter")] public required int NodesAfter { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
}
