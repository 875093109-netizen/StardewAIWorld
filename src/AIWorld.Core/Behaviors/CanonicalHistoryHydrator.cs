using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// CanonicalHistoryHydrator — conservative reconstruction of canonical experiences from an
/// EXISTING save when AIWorld is installed later (PM Character Awareness §7).
///
/// Rules:
///   - Reconstruct ONLY experiences the save proves occurred (caller supplies proven flags
///     from persisted canonical game history — never model knowledge).
///   - Never invent subjective details unsupported by canonical evidence.
///   - Provenance = SAVE_HISTORY_RECONSTRUCTED (distinct from LIVE_OBSERVED).
///   - Idempotent via CanonicalExperienceService.Exists (hydration never duplicates).
///
/// This class is the deterministic CORE side (no game dependency): it validates + normalizes
/// provider-supplied candidate events and delegates to CanonicalExperienceService. The Demo
/// side supplies the actual save-flag reader (e.g. Abigail's heart events from the save).
/// </summary>
public sealed class CanonicalHistoryHydrator
{
    private readonly CanonicalExperienceService _service;

    public CanonicalHistoryHydrator(CanonicalExperienceService service)
    {
        _service = service;
    }

    public sealed record HydrationResult(int Attempted, int Accepted, int Duplicates, List<string> RejectedReasons);

    /// <summary>Hydrate candidate experiences. Each must carry evidence; rejected ones are
    /// reported (conservative degradation — never fabricate).</summary>
    public HydrationResult Hydrate(IEnumerable<CanonicalExperienceEvent> candidates)
    {
        int attempted = 0, accepted = 0, duplicates = 0;
        var rejected = new List<string>();

        foreach (var candidate in candidates)
        {
            attempted++;
            var normalized = new CanonicalExperienceEvent
            {
                ExperienceId = CanonicalExperienceService.ComputeExperienceId(candidate),
                Type = candidate.Type,
                Source = candidate.Source,
                CanonicalSourceId = candidate.CanonicalSourceId,
                CanonicalEventId = candidate.CanonicalEventId,
                OccurrenceKey = candidate.OccurrenceKey,
                Actor = candidate.Actor,
                Participants = new List<string>(candidate.Participants),
                Observers = new List<string>(candidate.Observers),
                Facts = new List<string>(candidate.Facts),
                OccurredAt = "historical-date-unknown",
                Location = candidate.Location,
                KnowledgeEffects = new Dictionary<string, string>(candidate.KnowledgeEffects, StringComparer.Ordinal),
                MemoryImportance = candidate.MemoryImportance,
                Provenance = ExperienceProvenance.SAVE_HISTORY_RECONSTRUCTED, // hydration provenance
                SchemaVersion = "1",
            };

            if (normalized.Facts.Count == 0 && normalized.KnowledgeEffects.Count == 0)
            {
                rejected.Add($"{normalized.Type}: no canonical evidence (empty facts/knowledge)");
                continue;
            }

            var result = _service.Ingest(normalized);
            if (result.Accepted) accepted++;
            else if (result.Duplicate) duplicates++;
            else rejected.Add($"{normalized.Type}: {result.Error}");
        }

        return new HydrationResult(attempted, accepted, duplicates, rejected);
    }
}
