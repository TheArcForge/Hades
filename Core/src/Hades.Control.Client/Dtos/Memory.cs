using System.Text.Json.Serialization;

namespace Hades.Control.Client.Dtos;

/// <summary>Mirrors Hades.Server.Control.MemoryDocumentRow.</summary>
public sealed record MemoryDocumentRow
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("sizeBytes")] public required long SizeBytes { get; init; }
    [JsonPropertyName("sizeDisplay")] public required string SizeDisplay { get; init; }
    [JsonPropertyName("lastReviewed")] public string? LastReviewed { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.MemoryProposalRow.</summary>
public sealed record MemoryProposalRow
{
    [JsonPropertyName("fileName")] public required string FileName { get; init; }
    [JsonPropertyName("targetFile")] public required string TargetFile { get; init; }
    [JsonPropertyName("createdAtUtc")] public DateTimeOffset? CreatedAtUtc { get; init; }
    [JsonPropertyName("createdAgo")] public string? CreatedAgo { get; init; }
    [JsonPropertyName("rationale")] public required string Rationale { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("content")] public required string Content { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.MemoryResult, the response of GET /control/memory.</summary>
public sealed record MemoryResult
{
    [JsonPropertyName("documents")] public required IReadOnlyList<MemoryDocumentRow> Documents { get; init; }
    [JsonPropertyName("proposals")] public required IReadOnlyList<MemoryProposalRow> Proposals { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.MemoryDocumentResult, the response of
/// GET /control/memory/document.</summary>
public sealed record MemoryDocumentResult
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("content")] public required string Content { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.WriteMemoryDocumentRequest, the body of
/// POST /control/memory/document.</summary>
public sealed record WriteMemoryDocumentRequest
{
    [JsonPropertyName("content")] public required string Content { get; init; }
}
