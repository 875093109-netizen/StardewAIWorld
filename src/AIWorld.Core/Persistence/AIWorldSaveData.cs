using System.Text.Json;
using System.Text.Json.Serialization;
using AIWorld.Core.Contracts;

namespace AIWorld.Core.Persistence;

/// <summary>
/// P3-01 Persistent Root Model.
/// The AIWorld save root — persisted via IAIWorldSaveStore (SMAPI ReadSaveData/WriteSaveData in runtime).
/// saveSchemaVersion is DISTINCT from the transport/contract schemaVersion in NpcStructuredResponse
/// (PM Phase 3 §4: TRANSPORT_SCHEMA_VERSION vs SAVE_SCHEMA_VERSION are independent version systems).
/// </summary>
public sealed class AIWorldSaveData
{
    [JsonPropertyName("stories")]
    public Dictionary<string, AIWorld.Core.Behaviors.StoryProgress> Stories { get; init; } = new(StringComparer.Ordinal);
    /// <summary>SAVE_SCHEMA_VERSION — the persistence format version (currently "1"). Independent of transport schemaVersion.</summary>
    [JsonPropertyName("saveSchemaVersion")]
    public required string SaveSchemaVersion { get; init; }

    [JsonPropertyName("characters")]
    public Dictionary<string, NpcPersistentData> Characters { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// FollowSession persistence (contract §2.8: persisted in AIWorld SaveData).
    /// Additive field — older saves lacking this key deserialize to an empty list,
    /// so saveSchemaVersion stays "1" (no migration needed; additive only).
    /// </summary>
    [JsonPropertyName("followSessions")]
    public List<FollowSession> FollowSessions { get; init; } = new();

    /// <summary>
    /// Canonical experience ledger (PM Character Awareness §4/§6). World-truth events that
    /// actually occurred. Idempotency ledger: ExperienceId content hash prevents duplicates
    /// across save/reload and hydration. Additive field — older saves deserialize to empty.
    /// </summary>
    [JsonPropertyName("canonicalExperiences")]
    public List<CanonicalExperienceEvent> CanonicalExperiences { get; init; } = new();

    /// <summary>
    /// A1 定时约会承诺（TravelCommitment）持久化。Additive field — 旧存档缺此键反序列化为空。
    /// 每存档数据（SMAPI WriteSaveData），非全局配置。
    /// </summary>
    [JsonPropertyName("travelCommitments")]
    public List<TravelCommitment> TravelCommitments { get; set; } = new();
}

/// <summary>Per-NPC persistent data. Keyed by the stable NPC persistent id (see NPC_PERSISTENT_ID_POLICY).</summary>
public sealed class NpcPersistentData
{
    [JsonPropertyName("npcId")]
    public required string NpcId { get; init; }

    /// <summary>AIWorld-owned mutable state (state.mood/stress/energy + adoption state magicStudent/magicProficiency).</summary>
    [JsonPropertyName("state")]
    public Dictionary<string, JsonElement> State { get; init; } = new(StringComparer.Ordinal);

    /// <summary>CharacterKnowledge epistemic states (factId → knowledge). UNKNOWN/RUMOR/VERIFIED only.</summary>
    [JsonPropertyName("knowledge")]
    public Dictionary<string, CharacterKnowledge> Knowledge { get; init; } = new(StringComparer.Ordinal);

    /// <summary>NPC-owned goals (goals.investigate_magic etc.).</summary>
    [JsonPropertyName("goals")]
    public Dictionary<string, JsonElement> Goals { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Relationship state (relationships.player.trust etc.).</summary>
    [JsonPropertyName("relationships")]
    public Dictionary<string, JsonElement> Relationships { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Skills / adoption state (skills.magic, magicStudent, magicProficiency).</summary>
    [JsonPropertyName("skills")]
    public Dictionary<string, JsonElement> Skills { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Persistent memory records (bounded).</summary>
    [JsonPropertyName("memories")]
    public List<MemoryRecord> Memories { get; init; } = new();

    /// <summary>
    /// Persistent objective observations (contract §2.4: AIWorld/observations, per-NPC FIFO cap).
    /// Additive field — older saves lacking this key deserialize to an empty list.
    /// Immutable per observation; only A4 compacts/cleans (FIFO).
    /// </summary>
    [JsonPropertyName("observations")]
    public List<ObjectiveObservation> Observations { get; init; } = new();

    /// <summary>Runtime-only recent context (NOT persisted; explicit policy — see MemoryLimits).</summary>
    [JsonIgnore]
    public List<MemoryRecord> RecentContext { get; } = new();
}
