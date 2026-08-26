using System.Text.Json.Serialization;
using Hades.Control.Client;

namespace Hades.Control.Client.Dtos;

/// <summary>Mirrors Hades.Server.Control.TraceOutcome.</summary>
[JsonConverter(typeof(UnknownFallbackConverter<TraceOutcome>))]
public enum TraceOutcome { Unknown, Ok, Error }

/// <summary>Mirrors Hades.Server.Control.TraceSequenceRow.</summary>
public sealed record TraceSequenceRow
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("tools")] public required IReadOnlyList<string> Tools { get; init; }
    [JsonPropertyName("pattern")] public required string Pattern { get; init; }
    [JsonPropertyName("callCount")] public required int CallCount { get; init; }
    [JsonPropertyName("startUtcMs")] public required long StartUtcMs { get; init; }
    [JsonPropertyName("endUtcMs")] public required long EndUtcMs { get; init; }
    [JsonPropertyName("durationMs")] public required long DurationMs { get; init; }
    [JsonPropertyName("outcome")] public required TraceOutcome Outcome { get; init; }
    [JsonPropertyName("traceIds")] public required IReadOnlyList<string> TraceIds { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.TraceSequencesResult, the response of
/// GET /control/traces/sequences.</summary>
public sealed record TraceSequencesResult
{
    [JsonPropertyName("sequences")] public required IReadOnlyList<TraceSequenceRow> Sequences { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.SpanAttributeRow.</summary>
public sealed record SpanAttributeRow
{
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("valueDisplay")] public required string ValueDisplay { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.SpanRow.</summary>
public sealed record SpanRow
{
    [JsonPropertyName("spanId")] public required string SpanId { get; init; }
    [JsonPropertyName("parentSpanId")] public string? ParentSpanId { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("startUtcMs")] public required long StartUtcMs { get; init; }
    [JsonPropertyName("endUtcMs")] public long? EndUtcMs { get; init; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("attributes")] public IReadOnlyList<SpanAttributeRow>? Attributes { get; init; }
    [JsonPropertyName("events")] public IReadOnlyList<SpanAttributeRow>? Events { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.TraceDetailResult, the response of
/// GET /control/traces/{traceId}.</summary>
public sealed record TraceDetailResult
{
    [JsonPropertyName("traceId")] public required string TraceId { get; init; }
    [JsonPropertyName("tool")] public required string Tool { get; init; }
    [JsonPropertyName("startUtcMs")] public required long StartUtcMs { get; init; }
    [JsonPropertyName("endUtcMs")] public long? EndUtcMs { get; init; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; init; }
    [JsonPropertyName("outcome")] public required TraceOutcome Outcome { get; init; }
    [JsonPropertyName("spans")] public required IReadOnlyList<SpanRow> Spans { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.SlowToolRow.</summary>
public sealed record SlowToolRow
{
    [JsonPropertyName("tool")] public required string Tool { get; init; }
    [JsonPropertyName("callCount")] public required int CallCount { get; init; }
    [JsonPropertyName("averageDurationMs")] public required double AverageDurationMs { get; init; }
    [JsonPropertyName("maxDurationMs")] public required long MaxDurationMs { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.SlowToolsResult, the response of GET /control/traces/slow.</summary>
public sealed record SlowToolsResult
{
    [JsonPropertyName("tools")] public required IReadOnlyList<SlowToolRow> Tools { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.FailedCallRow.</summary>
public sealed record FailedCallRow
{
    [JsonPropertyName("traceId")] public required string TraceId { get; init; }
    [JsonPropertyName("tool")] public required string Tool { get; init; }
    [JsonPropertyName("startUtcMs")] public required long StartUtcMs { get; init; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.FailedCallsResult, the response of
/// GET /control/traces/failures.</summary>
public sealed record FailedCallsResult
{
    [JsonPropertyName("failures")] public required IReadOnlyList<FailedCallRow> Failures { get; init; }
    [JsonPropertyName("truncated")] public required bool Truncated { get; init; }
}
