using System.Text.Json.Serialization;

namespace AIWorld.Core.Persistence;

/// <summary>
/// P3 Memory layers (REQUIREMENTS memory architecture):
/// Episodic / Semantic / Relationship / RecentContext / LongTermSummary / PermanentPersona.
/// Phase 3 implements Episodic + Semantic + Relationship concretely; RecentContext is
/// runtime-only (bounded, not persisted); LongTermSummary is model-only (no auto LLM summary
/// in Phase 3); PermanentPersona is NEVER written by MemoryCandidate (read-only layer).
/// </summary>
public enum MemoryLayer
{
    Episodic,
    Semantic,
    Relationship,
    RecentContext,
    LongTermSummary,
    PermanentPersona,
}

/// <summary>A persisted AIWorld memory record. Internal identity assigned deterministically by MemoryIngestionService.</summary>
public sealed class MemoryRecord
{
    /// <summary>Internal AIWorld memory id (deterministic, assigned on ingestion).</summary>
    [JsonPropertyName("memoryId")]
    public required string MemoryId { get; init; }

    [JsonPropertyName("layer")]
    public required MemoryLayer Layer { get; init; }

    [JsonPropertyName("npcId")]
    public required string NpcId { get; init; }

    /// <summary>Source observation id from the MemoryCandidate (may be empty for semantic/relationship memories).</summary>
    [JsonPropertyName("sourceObservationId")]
    public string? SourceObservationId { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("emotionalTone")]
    public required string EmotionalTone { get; init; }

    [JsonPropertyName("importance")]
    public required float Importance { get; init; }

    [JsonPropertyName("createdAt")]
    public required string CreatedAt { get; init; }

    [JsonPropertyName("persistedAt")]
    public required string PersistedAt { get; init; }

    /// <summary>AIWorld persistence metadata only — never used to "repair" invalid candidates.</summary>
    [JsonPropertyName("internalMetadata")]
    public Dictionary<string, string>? InternalMetadata { get; init; }
}
