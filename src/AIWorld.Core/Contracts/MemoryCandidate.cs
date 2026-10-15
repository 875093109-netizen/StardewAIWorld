using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// P2-01 Contracts Materialization — mirrors contracts-draft.md §2.5 (frozen v0.2).
/// LLM-generated subjective memory proposal from objective observation.
/// Same objective fact may form different interpretations per persona.
/// </summary>
public sealed class MemoryCandidate
{
    /// <summary>Required. NPC identifier.</summary>
    [JsonPropertyName("npcId")]
    public required string NpcId { get; init; }

    /// <summary>Required. Must exist and belong to this npcId.</summary>
    [JsonPropertyName("sourceObservationId")]
    public required string SourceObservationId { get; init; }

    /// <summary>Required. Non-empty, ≤200 chars.</summary>
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    /// <summary>Required. Enum: positive / neutral / negative / ambivalent.</summary>
    [JsonPropertyName("emotionalTone")]
    public required string EmotionalTone { get; init; }

    /// <summary>Required. Float 0–1.</summary>
    [JsonPropertyName("importance")]
    public required float Importance { get; init; }

    /// <summary>Required. Schema version.</summary>
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    /// <summary>Optional. Related fact ids.</summary>
    [JsonPropertyName("relatedFactIds")]
    public string[]? RelatedFactIds { get; init; }

    /// <summary>Optional. Tags.</summary>
    [JsonPropertyName("tags")]
    public string[]? Tags { get; init; }
}
