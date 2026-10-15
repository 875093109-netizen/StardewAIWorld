using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>Provenance of a canonical experience — how AIWorld learned it occurred.</summary>
public enum ExperienceProvenance
{
    /// <summary>Observed live from the running game (provider detected the event in-world).</summary>
    LIVE_OBSERVED,

    /// <summary>Reconstructed conservatively from persisted canonical save history at
    /// hydration time (existing save with AIWorld installed later).</summary>
    SAVE_HISTORY_RECONSTRUCTED,
}

/// <summary>
/// CanonicalExperienceEvent — a world-truth experience that actually occurred in the player's
/// world (PM Character Awareness §4/§5/§6).
///
/// Created ONLY when World Truth proves the experience occurred (canonical source: vanilla
/// event completion, mail/quest/invitation send actions, mod provider, or runtime observer).
/// Never created from LLM imagination or preloaded Stardew canon knowledge.
///
/// Idempotent: ExperienceId is a stable hash of canonical source identity ONLY —
/// ExperienceId = hash(CanonicalSourceId, CanonicalEventId, OccurrenceKey).
/// OccurredAt / Facts / Location / descriptive text are METADATA, never identity,
/// so LIVE_OBSERVED and SAVE_HISTORY_RECONSTRUCTED resolve to the SAME ExperienceId
/// and repeated scans across different game times never duplicate memories.
/// </summary>
public sealed class CanonicalExperienceEvent
{
    /// <summary>Stable content-hash id. Must be deterministic across save/reload.
    /// hash(CanonicalSourceId | CanonicalEventId | OccurrenceKey) — computed on ingest.</summary>
    [JsonPropertyName("experienceId")]
    public required string ExperienceId { get; init; }

    /// <summary>HEART_EVENT / MAIL_SENT / QUEST_INITIATED / INVITATION_SENT / ...</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; } // VANILLA / MOD_SPECIFIC / RUNTIME_GENERIC

    /// <summary>Stable canonical source identity — which canonical system produced this
    /// experience (e.g. "vanilla.heartEvent", "vanilla.mail", "mod.<ModId>.event").
    /// Part of ExperienceId identity; must be stable across save/reload and provenance.</summary>
    [JsonPropertyName("canonicalSourceId")]
    public required string CanonicalSourceId { get; init; }

    /// <summary>Stable canonical event id WITHIN the source (e.g. "1" for the vanilla
    /// SeedShop Prairie King heart event — the first segment of the asset key, NOT the
    /// full precondition string). Part of ExperienceId identity.</summary>
    [JsonPropertyName("canonicalEventId")]
    public required string CanonicalEventId { get; init; }

    /// <summary>Occurrence key — distinguishes REPEATABLE occurrences of the same event
    /// (e.g. quest id, day+location for repeatables). For one-shot events use a fixed
    /// value like "once" so all occurrences collapse to one identity. Part of
    /// ExperienceId identity.</summary>
    [JsonPropertyName("occurrenceKey")]
    public required string OccurrenceKey { get; init; }

    /// <summary>CharacterId of the canonical actor (who caused/participated).</summary>
    [JsonPropertyName("actor")]
    public required string Actor { get; init; }

    /// <summary>CharacterIds directly involved (knowledge writers).</summary>
    [JsonPropertyName("participants")]
    public List<string> Participants { get; init; } = new();

    /// <summary>CharacterIds who could plausibly observe (optional; knowledge is NOT
    /// auto-written to observers unless the event is public).</summary>
    [JsonPropertyName("observers")]
    public List<string> Observers { get; init; } = new();

    /// <summary>Canonical facts — evidence-backed only. NEVER subjective detail.</summary>
    [JsonPropertyName("facts")]
    public List<string> Facts { get; init; } = new();

    /// <summary>GameTimeStamp {season}-{day}-{year}@{HHMM}.</summary>
    [JsonPropertyName("occurredAt")]
    public required string OccurredAt { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }

    /// <summary>factId → target knowledge state (e.g. "heartEvent.playedPrairieKing" → VERIFIED).
    /// Applied only to participant namespaces.</summary>
    [JsonPropertyName("knowledgeEffects")]
    public Dictionary<string, string> KnowledgeEffects { get; init; } = new(StringComparer.Ordinal);

    /// <summary>0..1 memory importance hint for participants.</summary>
    [JsonPropertyName("memoryImportance")]
    public float MemoryImportance { get; init; } = 0.5f;

    [JsonPropertyName("provenance")]
    public required ExperienceProvenance Provenance { get; init; }

    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }
}
