using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// BehaviorLease — approved behavior execution license (contracts-draft.md §2.7, frozen v0.2).
/// Phase 4 implements the follow lifecycle only: AUTHORIZED → adapter SUCCESS → lease ACTIVE;
/// adapter failure → NO ACTIVE LEASE; stop success → lease RELEASED.
/// </summary>
public sealed class BehaviorLease
{
    [JsonPropertyName("leaseId")]
    public required string LeaseId { get; init; }

    [JsonPropertyName("npcId")]
    public required string NpcId { get; init; }

    [JsonPropertyName("capabilityId")]
    public required string CapabilityId { get; init; }

    /// <summary>EVENT / HARD_GAME_RULE / AI_MAJOR / AI_MINOR / CANONICAL_BASELINE</summary>
    [JsonPropertyName("priority")]
    public required string Priority { get; init; }

    [JsonPropertyName("expiresAt")]
    public required string ExpiresAt { get; init; }

    [JsonPropertyName("interruptible")]
    public required bool Interruptible { get; init; }

    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("rejoinAnchor")]
    public string? RejoinAnchor { get; init; }

    [JsonPropertyName("adapterParams")]
    public Dictionary<string, object?>? AdapterParams { get; init; }

    /// <summary>Runtime lifecycle (not persisted): PENDING → ACTIVE → RELEASED / FAILED.</summary>
    [JsonIgnore]
    public string Status { get; set; } = "PENDING";
}
