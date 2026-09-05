using System.Text.Json.Serialization;
using Hades.Control.Client;

namespace Hades.Control.Client.Dtos;

/// <summary>Mirrors Hades.Server.Control.ControlIconState.</summary>
[JsonConverter(typeof(UnknownFallbackConverter<ControlIconState>))]
public enum ControlIconState { Unknown, Idle, Indexing, Attached, LeaseHeld, Error }

/// <summary>Mirrors Hades.Server.Control.ControlSeverity.</summary>
[JsonConverter(typeof(UnknownFallbackConverter<ControlSeverity>))]
public enum ControlSeverity { Unknown, Ok, Warning, Error }

/// <summary>Mirrors Hades.Server.Control.SummaryRow.</summary>
public sealed record SummaryRow
{
    [JsonPropertyName("project")] public required string Project { get; init; }
    [JsonPropertyName("productGuid")] public required string ProductGuid { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("severity")] public required ControlSeverity Severity { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.SummaryLease.</summary>
public sealed record SummaryLease
{
    [JsonPropertyName("project")] public required string Project { get; init; }
    [JsonPropertyName("leaseId")] public required string LeaseId { get; init; }
    [JsonPropertyName("heldForSeconds")] public required int HeldForSeconds { get; init; }
    [JsonPropertyName("expiresInSeconds")] public required int ExpiresInSeconds { get; init; }
    [JsonPropertyName("releasable")] public required bool Releasable { get; init; }
}

/// <summary>Mirrors Hades.Server.Control.SummaryResult, the response of GET /control/summary.</summary>
public sealed record SummaryResult
{
    [JsonPropertyName("iconState")] public required ControlIconState IconState { get; init; }
    [JsonPropertyName("headline")] public required string Headline { get; init; }
    [JsonPropertyName("rows")] public required IReadOnlyList<SummaryRow> Rows { get; init; }
    [JsonPropertyName("lease")] public SummaryLease? Lease { get; init; }
}
