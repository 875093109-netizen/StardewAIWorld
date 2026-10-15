using AIWorld.Core.Contracts;

namespace AIWorld.Core.Persistence;

/// <summary>
/// P3-03 MemoryIngestionService — MemoryCandidate → validate → authorize → persist.
/// A MemoryCandidate is an LLM proposal, NOT an automatic permanent memory.
/// The service assigns deterministic internal identity + persistence metadata.
/// It NEVER "repairs" schema-invalid candidates (invalid → REJECT, not guess-and-repair).
/// Permanent Persona is never written here.
/// </summary>
public sealed class MemoryIngestionService
{
    private readonly MemoryRepository _repository;
    private readonly string _gameDate;
    private int _sequence;

    public MemoryIngestionService(MemoryRepository repository, string gameDate)
    {
        _repository = repository;
        _gameDate = gameDate;
    }

    public sealed class IngestionResult
    {
        public required bool Accepted { get; init; }
        public MemoryRecord? Record { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>Validate + persist one candidate. Returns accepted record or rejection reason.</summary>
    public IngestionResult Ingest(MemoryCandidate candidate, MemoryLayer layer)
    {
        var err = Validate(candidate);
        if (err is not null)
            return new IngestionResult { Accepted = false, Error = err };

        while (_repository.GetRecentMemories(candidate.NpcId, int.MaxValue)
            .Any(m => m.MemoryId == $"mem-{candidate.NpcId}-{_gameDate}-{_sequence}")) _sequence++;
        var record = new MemoryRecord
        {
            MemoryId = $"mem-{candidate.NpcId}-{_gameDate}-{_sequence++}",
            Layer = layer,
            NpcId = NpcPersistentIdPolicy.Resolve(candidate.NpcId),
            SourceObservationId = candidate.SourceObservationId,
            Summary = candidate.Summary,
            EmotionalTone = candidate.EmotionalTone,
            Importance = candidate.Importance,
            CreatedAt = _gameDate,
            PersistedAt = _gameDate,
            InternalMetadata = new Dictionary<string, string>
            {
                ["ingestedBy"] = "MemoryIngestionService",
                ["sourceTransportSchemaVersion"] = candidate.SchemaVersion,
            },
        };

        bool appended = _repository.Append(record.NpcId, record, persist: true);
        if (!appended)
            return new IngestionResult { Accepted = false, Error = "memory limit reached or record too large" };

        return new IngestionResult { Accepted = true, Record = record };
    }

    /// <summary>Add runtime-only recent context (not persisted).</summary>
    public bool AddRecentContext(MemoryCandidate candidate)
    {
        var err = Validate(candidate);
        if (err is not null) return false;
        var record = new MemoryRecord
        {
            MemoryId = $"ctx-{candidate.NpcId}-{_gameDate}-{_sequence++}",
            Layer = MemoryLayer.RecentContext,
            NpcId = NpcPersistentIdPolicy.Resolve(candidate.NpcId),
            SourceObservationId = candidate.SourceObservationId,
            Summary = candidate.Summary,
            EmotionalTone = candidate.EmotionalTone,
            Importance = candidate.Importance,
            CreatedAt = _gameDate,
            PersistedAt = _gameDate,
        };
        return _repository.Append(record.NpcId, record, persist: false);
    }

    private static string? Validate(MemoryCandidate c)
    {
        if (string.IsNullOrWhiteSpace(c.NpcId)) return "missing npcId";
        if (string.IsNullOrWhiteSpace(c.Summary)) return "missing summary";
        if (c.Summary.Length > MemoryLimits.MaxMemorySummaryChars) return $"summary exceeds {MemoryLimits.MaxMemorySummaryChars} chars";
        if (c.Importance is < 0f or > 1f) return "importance out of [0,1]";
        if (Array.IndexOf(new[] { "positive", "neutral", "negative", "ambivalent" }, c.EmotionalTone) < 0)
            return $"invalid emotionalTone '{c.EmotionalTone}'";
        // Permanent Persona is never ingested from candidates — enforced by caller policy (no persona layer here).
        return null;
    }
}
