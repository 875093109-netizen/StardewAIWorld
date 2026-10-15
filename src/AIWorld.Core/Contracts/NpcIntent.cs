using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// P2-01 Contracts Materialization — mirrors contracts-draft.md §2.1 (frozen v0.2).
/// LLM-produced high-level NPC behavior intent. LLM may ONLY produce this object;
/// it must never execute game operations directly.
/// </summary>
public sealed class NpcIntent
{
    /// <summary>Required. GUID string.</summary>
    [JsonPropertyName("intentId")]
    public required string IntentId { get; init; }

    /// <summary>
    /// Required. Directly adopts the WorldCapability capabilityId as single source of truth (§3.1).
    /// Demo registry: social.follow_player / social.stop_following / magic.observe /
    /// magic.investigate / magic.learn / INTENT_NONE.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Required. Resolvable target ("Player", valid NPC name, or valid location name).</summary>
    [JsonPropertyName("target")]
    public required string Target { get; init; }

    /// <summary>Required. Float 0.0–1.0.</summary>
    [JsonPropertyName("priority")]
    public required float Priority { get; init; }

    /// <summary>Required. Schema version, e.g. "1".</summary>
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    /// <summary>Optional. Human/LLM rationale.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Optional. Game-time expiration for L2 overrides (not used in Phase 2 execution).</summary>
    [JsonPropertyName("expiresAt")]
    public string? ExpiresAt { get; init; }

    /// <summary>Optional. Adapter-specific params (e.g. targetLocation). Never executed in Phase 2.</summary>
    [JsonPropertyName("params")]
    public Dictionary<string, object?>? Params { get; init; }
}
