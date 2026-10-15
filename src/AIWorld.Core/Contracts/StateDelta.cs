using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// P2-01 Contracts Materialization — mirrors contracts-draft.md §2.6 (frozen v0.2).
/// LLM-proposed NPC mutable-state change. Only whitelisted paths allowed;
/// persona.* / identity.* / memory.* are forbidden.
/// </summary>
public sealed class StateDelta
{
    /// <summary>Required. NPC identifier.</summary>
    [JsonPropertyName("npcId")]
    public required string NpcId { get; init; }

    /// <summary>
    /// Required. Dot-path, e.g. state.mood / relationships.player.trust /
    /// state.magic_student / goals.investigate_magic. Must hit whitelist.
    /// </summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>Required. JSON scalar; type must match registered path type.</summary>
    [JsonPropertyName("value")]
    public required JsonElement Value { get; init; }

    /// <summary>Required. Rationale.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>Required. Schema version.</summary>
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    /// <summary>Optional. Float confidence.</summary>
    [JsonPropertyName("confidence")]
    public float? Confidence { get; init; }
}
