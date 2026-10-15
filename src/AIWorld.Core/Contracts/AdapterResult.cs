using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// AdapterResult — standardized adapter execution receipt (contracts-draft.md §2.9, frozen v0.2).
/// SUCCESS requires observable evidence (postcondition), never "no exception".
/// </summary>
public sealed class AdapterResult
{
    [JsonPropertyName("adapterId")]
    public required string AdapterId { get; init; }

    [JsonPropertyName("capabilityId")]
    public required string CapabilityId { get; init; }

    /// <summary>SUCCESS / FAILED / UNAVAILABLE / TIMEOUT / REJECTED</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("intentId")]
    public string? IntentId { get; init; }

    [JsonPropertyName("detail")]
    public Dictionary<string, object?>? Detail { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}
