using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// P2-01 Contracts Materialization — top-level structured LLM response envelope.
/// Per REQUIREMENTS Phase 2: speech / intent / memoryCandidates / stateDelta.
/// This is the AIWorld-owned structured channel shape (NOT SocialValley's chat format).
/// </summary>
public sealed class NpcStructuredResponse
{
    /// <summary>Required. NPC spoken dialogue text.</summary>
    [JsonPropertyName("speech")]
    public required string Speech { get; init; }

    /// <summary>
    /// Optional (nullable). A single high-level intent, or null for no intent.
    /// Phase 2: VALIDATE + LOG ONLY — never executed.
    /// </summary>
    [JsonPropertyName("intent")]
    public NpcIntent? Intent { get; init; }

    /// <summary>Optional. Memory proposals (validated, not persisted in Phase 2).</summary>
    [JsonPropertyName("memoryCandidates")]
    public MemoryCandidate[]? MemoryCandidates { get; init; }

    /// <summary>Optional. State change proposals (validated, never applied in Phase 2).</summary>
    [JsonPropertyName("stateDelta")]
    public StateDelta[]? StateDelta { get; init; }

    /// <summary>
    /// Optional (nullable). A1 NPC 对玩家请求的主观决定（NpcDecision）。模型只提供决定，
    /// 不直接创建 TravelLease/移动；执行权由 NpcConsentGate 确定性裁决。
    /// </summary>
    [JsonPropertyName("decision")]
    public NpcDecision? Decision { get; init; }

    /// <summary>Required. Schema version.</summary>
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }
}
