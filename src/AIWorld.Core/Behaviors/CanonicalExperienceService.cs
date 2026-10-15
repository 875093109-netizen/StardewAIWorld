using System.Security.Cryptography;
using System.Text;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// CanonicalExperienceService — deterministic ingestion of world-truth experiences into
/// per-character knowledge/memory namespaces (PM Character Awareness §4/§5/§6/§9).
///
/// Invariants:
///   - Canonical memories are created ONLY when World Truth proves the experience occurred
///     (caller supplies a CanonicalExperienceEvent from a canonical source).
///   - Idempotent: ExperienceId is a stable content hash; re-delivery (save/reload,
///     hydration, duplicate provider events) never duplicates.
///   - Participant-scoped: knowledge effects + memories are written ONLY to the event's
///     participant CharacterIds. No broadcast into unrelated NPC namespaces.
///   - Actor knowledge and recipient knowledge stay temporally distinct — the event's
///     OccurredAt gates when the actor knows; recipient-side delivery is a separate event
///     (e.g. MAIL_SENT vs MAIL_RECEIVED) created by the caller.
///   - LLM never creates canonical history; this service is fully deterministic.
/// </summary>
public sealed class CanonicalExperienceService
{
    private readonly AIWorldSaveData _data;
    private readonly NpcStateRepository _state;
    private readonly MemoryRepository _memory;
    private readonly string _gameDate;

    public CanonicalExperienceService(AIWorldSaveData data, NpcStateRepository state, MemoryRepository memory, string gameDate)
    {
        _data = data;
        _state = state;
        _memory = memory;
        _gameDate = gameDate;
    }

    public sealed class IngestionResult
    {
        public required bool Accepted { get; init; }
        public required bool Duplicate { get; init; }
        public CanonicalExperienceEvent? Event { get; init; }
        public List<string> WrittenNamespaces { get; init; } = new();
        public string? Error { get; init; }
    }

    /// <summary>Stable identity hash — canonical source identity ONLY.
    /// ExperienceId = hash(CanonicalSourceId, CanonicalEventId, OccurrenceKey).
    /// OccurredAt/Facts/Location are metadata and never affect identity, so the same
    /// event observed live at different scan times or hydrated from an existing save
    /// resolves to the same ExperienceId (idempotent, no duplicates).</summary>
    public static string ComputeExperienceId(CanonicalExperienceEvent e)
    {
        var raw = string.Join("|", new[]
        {
            e.CanonicalSourceId ?? string.Empty,
            e.CanonicalEventId ?? string.Empty,
            e.OccurrenceKey ?? string.Empty,
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return "exp-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public bool Exists(string experienceId) =>
        _data.CanonicalExperiences.Any(x => string.Equals(x.ExperienceId, experienceId, StringComparison.Ordinal));

    /// <summary>Ingest a canonical experience. Idempotent; participant-scoped.</summary>
    public IngestionResult Ingest(CanonicalExperienceEvent e)
    {
        if (!CharacterId.IsValid(e.Actor))
            return new IngestionResult { Accepted = false, Duplicate = false, Error = $"invalid actor '{e.Actor}'" };
        if (string.IsNullOrWhiteSpace(e.CanonicalSourceId))
            return new IngestionResult { Accepted = false, Duplicate = false, Error = "missing canonicalSourceId" };
        if (string.IsNullOrWhiteSpace(e.CanonicalEventId))
            return new IngestionResult { Accepted = false, Duplicate = false, Error = "missing canonicalEventId" };
        if (string.IsNullOrWhiteSpace(e.OccurrenceKey))
            return new IngestionResult { Accepted = false, Duplicate = false, Error = "missing occurrenceKey" };

        var id = ComputeExperienceId(e);
        if (Exists(id))
        {
            // Return the EXISTING canonical event (stable identity) so callers can observe
            // the real ExperienceId on duplicate ingestion — not the caller's raw event,
            // whose ExperienceId field is unset (identity is computed here).
            var existing = _data.CanonicalExperiences.First(x => string.Equals(x.ExperienceId, id, StringComparison.Ordinal));
            return new IngestionResult { Accepted = false, Duplicate = true, Event = existing };
        }

        // Persist the event itself (idempotency ledger).
        var canonical = new CanonicalExperienceEvent
        {
            ExperienceId = id,
            Type = e.Type,
            Source = e.Source,
            CanonicalSourceId = e.CanonicalSourceId,
            CanonicalEventId = e.CanonicalEventId,
            OccurrenceKey = e.OccurrenceKey,
            Actor = e.Actor,
            Participants = new List<string>(e.Participants),
            Observers = new List<string>(e.Observers),
            Facts = new List<string>(e.Facts),
            OccurredAt = e.OccurredAt,
            Location = e.Location,
            KnowledgeEffects = new Dictionary<string, string>(e.KnowledgeEffects, StringComparer.Ordinal),
            MemoryImportance = e.MemoryImportance,
            Provenance = e.Provenance,
            SchemaVersion = "1",
        };
        _data.CanonicalExperiences.Add(canonical);

        // Participant-scoped knowledge + memory (STRICT identity isolation §9).
        var written = new List<string>();
        foreach (var participant in e.Participants.Distinct(StringComparer.Ordinal))
        {
            if (!CharacterId.IsValid(participant)) continue;
            EnsureCharacter(participant);

            // Knowledge effects (factId → state), only for this participant.
            foreach (var (factId, state) in e.KnowledgeEffects)
            {
                var current = _state.GetKnowledgeState(participant, factId);
                // One-way transitions UNKNOWN → RUMOR → VERIFIED; never regress.
                if (current is null || CharacterKnowledge.NextState(current) is not null)
                    _state.ApplyKnowledge(participant, factId, state, _gameDate, sourceObservationId: id);
            }

            // Canonical episodic memory (evidence-backed, deterministic text).
            var record = new MemoryRecord
            {
                MemoryId = $"canon-{participant}-{_gameDate}-{_memory.TotalPersistedCount()}",
                Layer = MemoryLayer.Episodic,
                NpcId = participant,
                Summary = BuildCanonicalSummary(canonical),
                EmotionalTone = "neutral",
                Importance = e.MemoryImportance,
                CreatedAt = _gameDate,
                PersistedAt = _gameDate,
                InternalMetadata = new Dictionary<string, string>
                {
                    ["ingestedBy"] = "CanonicalExperienceService",
                    ["experienceId"] = id,
                    ["experienceType"] = e.Type,
                    ["provenance"] = e.Provenance.ToString(),
                    ["canonicalSource"] = e.Source,
                },
            };
            _memory.Append(participant, record);
            written.Add(participant);
        }

        return new IngestionResult { Accepted = true, Duplicate = false, Event = canonical, WrittenNamespaces = written };
    }

    private void EnsureCharacter(string characterId)
    {
        if (!_data.Characters.ContainsKey(characterId))
            _data.Characters[characterId] = new NpcPersistentData { NpcId = characterId };
    }

    /// <summary>Deterministic canonical summary — evidence-backed facts only, NO invented detail.</summary>
    private static string BuildCanonicalSummary(CanonicalExperienceEvent e)
    {
        var facts = e.Facts.Count > 0 ? string.Join("；", e.Facts) : e.Type;
        return $"[canonical {e.Type}] {e.OccurredAt} @ {e.Location ?? "?"}: {facts}";
    }
}
