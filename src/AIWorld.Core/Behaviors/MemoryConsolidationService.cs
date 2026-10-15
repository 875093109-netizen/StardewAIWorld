using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// MemoryConsolidationService — FollowSession end → observations → interpretation →
/// MemoryCandidate → A4 validation → MemoryIngestionService persistence (Phase 5
/// "Memory Consolidation" step; REQUIREMENTS §13.4 / contracts §2.5).
///
/// Responsibilities:
///   - collect the session's objective observations
///   - run the interpretation boundary (deterministic fixture or production LLM adapter)
///   - A4-side referential validation: sourceObservationId MUST exist and belong to npcId
///     (deferred from Phase 2 semantic validator to "later phases" — this is that later phase)
///   - ingest accepted candidates via MemoryIngestionService (validates + persists)
///
/// Never writes memory directly; never repairs invalid candidates.
/// </summary>
public sealed class MemoryConsolidationService
{
    private readonly ObservationCollector _observations;
    private readonly FollowSessionManager _sessions;
    private readonly MemoryIngestionService _ingestion;
    private readonly IObservationInterpreter _interpreter;

    public MemoryConsolidationService(
        ObservationCollector observations,
        FollowSessionManager sessions,
        MemoryIngestionService ingestion,
        IObservationInterpreter interpreter)
    {
        _observations = observations;
        _sessions = sessions;
        _ingestion = ingestion;
        _interpreter = interpreter;
    }

    public sealed class ConsolidationResult
    {
        public required bool SessionFound { get; init; }
        public required bool SessionActive { get; init; }      // consolidation requires a CLOSED session
        public required int ObservationCount { get; init; }
        public required int CandidatesProduced { get; init; }
        public required int Accepted { get; init; }
        public required int Rejected { get; init; }
        public List<string> RejectionReasons { get; init; } = new();
        public string? Error { get; init; }
    }

    /// <summary>Consolidate a CLOSED FollowSession into persisted memories. Idempotent-safe:
    /// a session already consolidated (has summary) is skipped.</summary>
    public ConsolidationResult Consolidate(string npcId, string gameStamp)
    {
        var session = _sessions.GetLatest(npcId);
        if (session is null)
            return new ConsolidationResult { SessionFound = false, SessionActive = false, ObservationCount = 0, CandidatesProduced = 0, Accepted = 0, Rejected = 0, Error = "no session found" };
        if (session.IsActive)
            return new ConsolidationResult { SessionFound = true, SessionActive = true, ObservationCount = 0, CandidatesProduced = 0, Accepted = 0, Rejected = 0, Error = "session still active" };
        if (session.Summary is not null)
            return new ConsolidationResult { SessionFound = true, SessionActive = false, ObservationCount = session.Observations.Count, CandidatesProduced = 0, Accepted = 0, Rejected = 0, Error = "already consolidated" };

        // Collect the session's observations (by observationId, ordered).
        var obs = session.Observations
            .Select(id => _observations.GetById(id))
            .Where(o => o is not null)
            .Cast<ObjectiveObservation>()
            .ToList();

        // Interpretation boundary (fixture or LLM — isolated per PM §17).
        var candidates = _interpreter.Interpret(npcId, obs, gameStamp);

        int accepted = 0, rejected = 0;
        var reasons = new List<string>();
        foreach (var candidate in candidates)
        {
            // A4-side referential validation (contract §2.5): sourceObservationId must exist + belong to npcId.
            var src = _observations.GetById(candidate.SourceObservationId);
            if (src is null)
            {
                rejected++; reasons.Add($"sourceObservationId '{candidate.SourceObservationId}' not found");
                continue;
            }
            if (!string.Equals(src.NpcId, npcId, StringComparison.Ordinal))
            {
                rejected++; reasons.Add($"sourceObservationId '{candidate.SourceObservationId}' belongs to '{src.NpcId}' not '{npcId}'");
                continue;
            }

            var ingestion = _ingestion.Ingest(candidate, MemoryLayer.Episodic);
            if (ingestion.Accepted) accepted++;
            else { rejected++; reasons.Add(ingestion.Error ?? "ingestion rejected"); }
        }

        // Mark consolidated (summary) — prevents re-consolidation after reload. ONLY when at
        // least one memory was accepted; if the interpreter produced candidates but ALL were
        // rejected (referential/semantic), or produced none (provider failure), leave
        // un-consolidated so a corrected interpretation can retry — a failed consolidation
        // must not permanently poison the session (idempotency is for SUCCESS).
        if (accepted > 0)
            _sessions.SetSummary(npcId, $"consolidated {accepted}/{candidates.Count} memories from {obs.Count} observations @{gameStamp}");

        return new ConsolidationResult
        {
            SessionFound = true,
            SessionActive = false,
            ObservationCount = obs.Count,
            CandidatesProduced = candidates.Count,
            Accepted = accepted,
            Rejected = rejected,
            RejectionReasons = reasons,
        };
    }
}
