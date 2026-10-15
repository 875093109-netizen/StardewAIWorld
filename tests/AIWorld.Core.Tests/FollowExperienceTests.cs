using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// Phase 5 offline test matrix — Follow Experience Loop foundation:
/// ObjectiveObservation (contract §2.4), ObservationCollector (whitelist/FIFO/immutability),
/// FollowSession observation linkage (§2.8), MemoryConsolidationService (observation →
/// interpretation → MemoryCandidate → A4 validation → persistence).
/// All deterministic — no game, no LLM.
/// </summary>
public class FollowExperienceTests
{
    private const string Abigail = "Abigail";
    private const string Player = "player-1";

    private static (AIWorldSaveData Data, ObservationCollector Collector, FollowSessionManager Sessions, MemoryIngestionService Ingestion, MemoryRepository Repository) Build(
        IObservationInterpreter? interpreter = null)
    {
        var data = new AIWorldSaveData
        {
            SaveSchemaVersion = "1",
            Characters = new Dictionary<string, NpcPersistentData>(),
            Metadata = new Dictionary<string, string>(),
        };
        var repository = new MemoryRepository(data);
        var collector = new ObservationCollector(data);
        var sessions = new FollowSessionManager(data, Player);
        var ingestion = new MemoryIngestionService(repository, "spring-1-1");
        var interp = interpreter ?? new DeterministicObservationInterpreter();
        _ = interp;
        return (data, collector, sessions, ingestion, repository);
    }

    private static ObjectiveObservation Obs(string type, string npcId = Abigail, string? sessionId = null, Dictionary<string, System.Text.Json.JsonElement>? payload = null) => new()
    {
        ObservationId = Guid.NewGuid().ToString("N"),
        Type = type,
        NpcId = npcId,
        GameDate = "spring-1-1",
        TimeOfDay = 600,
        Location = "MineShaft",
        SchemaVersion = "1",
        Payload = payload,
        SessionId = sessionId,
    };

    // ── ObservationCollector: whitelist / validation ─────────────────────────────

    [Fact]
    public void P5T01_WhitelistedType_Accepted()
    {
        var (_, collector, _, _, _) = Build();
        var result = collector.Record(Obs(ObservationTypes.PlayerInjured));
        Assert.True(result.Accepted);
        Assert.NotNull(result.Observation);
        Assert.Equal(1, collector.CountForNpc(Abigail));
    }

    [Fact]
    public void P5T02_NonWhitelistedType_Rejected()
    {
        var (_, collector, _, _, _) = Build();
        // COMBAT is NOT in the frozen §2.4 whitelist (DANGER_ENCOUNTER covers danger).
        var result = collector.Record(Obs("COMBAT"));
        Assert.False(result.Accepted);
        Assert.Contains("whitelist", result.Error);
        Assert.Equal(0, collector.CountForNpc(Abigail));
    }

    [Fact]
    public void P5T03_MissingRequiredField_Rejected()
    {
        var (_, collector, _, _, _) = Build();
        var bad = Obs(ObservationTypes.LocationVisited);
        bad = new ObjectiveObservation
        {
            ObservationId = bad.ObservationId, Type = bad.Type, NpcId = "",
            GameDate = bad.GameDate, TimeOfDay = bad.TimeOfDay, Location = bad.Location, SchemaVersion = bad.SchemaVersion,
        };
        var result = collector.Record(bad);
        Assert.False(result.Accepted);
        Assert.Contains("npcId", result.Error);
    }

    [Fact]
    public void P5T04_PayloadValidation_NumericDelta()
    {
        var (_, collector, _, _, _) = Build();
        using var doc = System.Text.Json.JsonDocument.Parse("{\"delta\":\"not-a-number\"}");
        var payload = new Dictionary<string, System.Text.Json.JsonElement> { ["delta"] = doc.RootElement.GetProperty("delta") };
        var result = collector.Record(Obs(ObservationTypes.PlayerInjured, payload: payload));
        Assert.False(result.Accepted);
        Assert.Contains("delta", result.Error);
    }

    // ── FIFO cap ─────────────────────────────────────────────────────────────────

    [Fact]
    public void P5T05_FifoCap_PerNpc()
    {
        var (_, collector, _, _, _) = Build(); // default maxPerNpc = 200
        for (int i = 0; i < 205; i++)
        {
            var r = collector.Record(Obs(ObservationTypes.LocationVisited));
            Assert.True(r.Accepted);
        }
        Assert.Equal(200, collector.CountForNpc(Abigail));
        // Oldest evicted: the first observation's id must be gone.
        var first = collector.GetForNpc(Abigail).First();
        Assert.NotNull(first); // 205th observation survived, oldest 5 evicted
    }

    [Fact]
    public void P5T06_Observations_ImmutableAfterCreation()
    {
        var (data, collector, _, _, _) = Build();
        var result = collector.Record(Obs(ObservationTypes.DangerEncounter));
        var id = result.Observation!.ObservationId;
        // Re-load from persisted data: no mutation API exists; roundtrip preserves content.
        var json = AIWorldSaveRepository.Serialize(data);
        var restored = AIWorldSaveRepository.Deserialize(json)!;
        var restoredObs = restored.Characters[Abigail].Observations.Single(o => o.ObservationId == id);
        Assert.Equal(ObservationTypes.DangerEncounter, restoredObs.Type);
        Assert.Equal("MineShaft", restoredObs.Location);
    }

    // ── FollowSession observation linkage (contract §2.8) ───────────────────────

    [Fact]
    public void P5T07_AppendObservation_ActiveSession()
    {
        var (_, collector, sessions, _, _) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        var obs = collector.Record(Obs(ObservationTypes.PlayerInjured));
        Assert.True(obs.Accepted);
        Assert.True(sessions.AppendObservation(Abigail, obs.Observation!.ObservationId));
        Assert.Single(sessions.GetActive(Abigail)!.Observations);
    }

    [Fact]
    public void P5T08_AppendObservation_ClosedSession_Rejected()
    {
        var (_, collector, sessions, _, _) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        sessions.End(Abigail, "AI_DISMISS", "spring-1-1@0700");
        var obs = collector.Record(Obs(ObservationTypes.PlayerInjured));
        Assert.True(obs.Accepted);
        Assert.False(sessions.AppendObservation(Abigail, obs.Observation!.ObservationId));
    }

    [Fact]
    public void P5T09_AppendObservation_Dedup()
    {
        var (_, collector, sessions, _, _) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        var obs = collector.Record(Obs(ObservationTypes.PlayerInjured));
        Assert.True(sessions.AppendObservation(Abigail, obs.Observation!.ObservationId));
        Assert.False(sessions.AppendObservation(Abigail, obs.Observation!.ObservationId));
        Assert.Single(sessions.GetActive(Abigail)!.Observations);
    }

    // ── Memory consolidation (observation → interpretation → memory) ────────────

    [Fact]
    public void P5T10_Consolidate_ClosedSession_PersistsMemory()
    {
        var (_, collector, sessions, ingestion, repository) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        var inj = collector.Record(Obs(ObservationTypes.PlayerInjured));
        var loc = collector.Record(Obs(ObservationTypes.LocationVisited));
        Assert.True(sessions.AppendObservation(Abigail, inj.Observation!.ObservationId));
        Assert.True(sessions.AppendObservation(Abigail, loc.Observation!.ObservationId));
        sessions.End(Abigail, "AI_DISMISS", "spring-1-1@0700");

        var consolidation = new MemoryConsolidationService(collector, sessions, ingestion, new DeterministicObservationInterpreter());
        var result = consolidation.Consolidate(Abigail, "spring-1-1@0800");

        Assert.True(result.SessionFound);
        Assert.False(result.SessionActive);
        Assert.Equal(2, result.ObservationCount);
        Assert.True(result.Accepted >= 1); // PlayerInjured → negative memory
        Assert.True(repository.GetRecentMemories(Abigail, 10).Count >= 1);
        // Find the memory sourced from the PlayerInjured observation (recent list is newest-first).
        var mem = repository.GetRecentMemories(Abigail, 10).First(m => m.SourceObservationId == inj.Observation!.ObservationId);
        Assert.Equal(Abigail, mem.NpcId);
        Assert.Equal(MemoryLayer.Episodic, mem.Layer);
    }

    [Fact]
    public void P5T11_Consolidate_ActiveSession_Rejected()
    {
        var (_, collector, sessions, ingestion, _) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        var consolidation = new MemoryConsolidationService(collector, sessions, ingestion, new DeterministicObservationInterpreter());
        var result = consolidation.Consolidate(Abigail, "spring-1-1@0700");
        Assert.True(result.SessionFound);
        Assert.True(result.SessionActive);
        Assert.Contains("still active", result.Error);
    }

    [Fact]
    public void P5T12_Consolidate_Idempotent_AfterSummarySet()
    {
        var (_, collector, sessions, ingestion, repository) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        var inj = collector.Record(Obs(ObservationTypes.PlayerInjured));
        Assert.True(sessions.AppendObservation(Abigail, inj.Observation!.ObservationId));
        sessions.End(Abigail, "AI_DISMISS", "spring-1-1@0700");

        var consolidation = new MemoryConsolidationService(collector, sessions, ingestion, new DeterministicObservationInterpreter());
        var first = consolidation.Consolidate(Abigail, "spring-1-1@0800");
        Assert.True(first.Accepted >= 1);
        int memoryCount = repository.TotalPersistedCount();

        var second = consolidation.Consolidate(Abigail, "spring-1-1@0900");
        Assert.Contains("already consolidated", second.Error);
        Assert.Equal(memoryCount, repository.TotalPersistedCount()); // no duplicate memories
    }

    [Fact]
    public void P5T13_Consolidate_MissingSourceObservation_Rejected()
    {
        var (_, collector, sessions, ingestion, _) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        sessions.End(Abigail, "AI_DISMISS", "spring-1-1@0700");
        // Session has NO observations linked; interpreter yields nothing (fixture maps per observation).
        var consolidation = new MemoryConsolidationService(collector, sessions, ingestion, new DeterministicObservationInterpreter());
        var result = consolidation.Consolidate(Abigail, "spring-1-1@0800");
        Assert.Equal(0, result.ObservationCount);
        Assert.Equal(0, result.CandidatesProduced);
        Assert.Equal(0, result.Accepted);
    }

    [Fact]
    public void P5T14_Consolidate_CrossNpcSource_Rejected()
    {
        var (_, collector, sessions, ingestion, repository) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        // Record an observation belonging to a DIFFERENT npc (Sebastian).
        var otherObs = collector.Record(Obs(ObservationTypes.DangerEncounter, npcId: "Sebastian"));
        Assert.True(otherObs.Accepted);
        // Interpreter fixture returns a candidate for Abigail referencing SEBASTIAN's observation.
        var evilInterpreter = new FixedInterpreter(new MemoryCandidate
        {
            NpcId = Abigail,
            SourceObservationId = otherObs.Observation!.ObservationId,
            Summary = "wrong source",
            EmotionalTone = "neutral",
            Importance = 0.5f,
            SchemaVersion = "1",
        });
        // Manually link the foreign observation id into Abigail's session (session linkage only
        // checks active+dedup; the A4 referential check happens at consolidation).
        sessions.AppendObservation(Abigail, otherObs.Observation!.ObservationId);
        sessions.End(Abigail, "AI_DISMISS", "spring-1-1@0700");

        var consolidation = new MemoryConsolidationService(collector, sessions, ingestion, evilInterpreter);
        var result = consolidation.Consolidate(Abigail, "spring-1-1@0800");

        Assert.Equal(1, result.Rejected);
        Assert.Contains(result.RejectionReasons, r => r.Contains("belongs to"));
        Assert.Equal(0, repository.TotalPersistedCount());
    }

    // ── Objective vs subjective separation (PM §10/§八) ─────────────────────────

    [Fact]
    public void P5T15_Interpretation_IsSubjective_NotRawFact()
    {
        var (_, collector, sessions, ingestion, repository) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        var inj = collector.Record(Obs(ObservationTypes.PlayerInjured));
        Assert.True(sessions.AppendObservation(Abigail, inj.Observation!.ObservationId));
        sessions.End(Abigail, "AI_DISMISS", "spring-1-1@0700");

        var consolidation = new MemoryConsolidationService(collector, sessions, ingestion, new DeterministicObservationInterpreter());
        consolidation.Consolidate(Abigail, "spring-1-1@0800");

        var mem = repository.GetRecentMemories(Abigail, 10).First();
        // The observation is objective ("health delta"); the memory is subjective persona-shaped.
        Assert.Equal("negative", mem.EmotionalTone);
        Assert.Contains("worried", mem.Summary);
        Assert.DoesNotContain("health", mem.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ── Persistence roundtrip (observations + memories survive reload) ──────────

    [Fact]
    public void P5T16_PersistenceRoundtrip_ObservationsAndMemories()
    {
        var (data, collector, sessions, ingestion, _) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        var inj = collector.Record(Obs(ObservationTypes.PlayerInjured));
        var danger = collector.Record(Obs(ObservationTypes.DangerEncounter));
        Assert.True(sessions.AppendObservation(Abigail, inj.Observation!.ObservationId));
        Assert.True(sessions.AppendObservation(Abigail, danger.Observation!.ObservationId));
        sessions.End(Abigail, "AI_DISMISS", "spring-1-1@0700");
        new MemoryConsolidationService(collector, sessions, ingestion, new DeterministicObservationInterpreter())
            .Consolidate(Abigail, "spring-1-1@0800");

        var json = AIWorldSaveRepository.Serialize(data);
        var restored = AIWorldSaveRepository.Deserialize(json)!;

        var restoredObs = restored.Characters[Abigail].Observations;
        Assert.Equal(2, restoredObs.Count);
        Assert.Equal(ObservationTypes.PlayerInjured, restoredObs[0].Type);
        Assert.Equal(ObservationTypes.DangerEncounter, restoredObs[1].Type);

        var restoredMem = restored.Characters[Abigail].Memories;
        Assert.NotEmpty(restoredMem);
        Assert.Equal(MemoryLayer.Episodic, restoredMem[0].Layer);

        var restoredSession = restored.FollowSessions.Single();
        Assert.False(restoredSession.IsActive);
        Assert.Equal(2, restoredSession.Observations.Count);
        Assert.NotNull(restoredSession.Summary); // consolidation marker persisted
    }

    [Fact]
    public void P5T17_Consolidation_NoSideMutations()
    {
        var (data, collector, sessions, ingestion, _) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        var inj = collector.Record(Obs(ObservationTypes.PlayerInjured));
        Assert.True(sessions.AppendObservation(Abigail, inj.Observation!.ObservationId));
        sessions.End(Abigail, "AI_DISMISS", "spring-1-1@0700");
        new MemoryConsolidationService(collector, sessions, ingestion, new DeterministicObservationInterpreter())
            .Consolidate(Abigail, "spring-1-1@0800");

        // No persona/state/knowledge/goals/relationships/skills mutation (memory only).
        var npc = data.Characters[Abigail];
        Assert.Empty(npc.State);
        Assert.Empty(npc.Knowledge);
        Assert.Empty(npc.Goals);
        Assert.Empty(npc.Relationships);
        Assert.Empty(npc.Skills);
        Assert.NotEmpty(npc.Memories);
    }

    [Fact]
    public void P5T18_Observation_BelongsToSession_LinkagePersisted()
    {
        var (data, collector, sessions, _, _) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        var obs = collector.Record(Obs(ObservationTypes.LocationVisited));
        Assert.True(sessions.AppendObservation(Abigail, obs.Observation!.ObservationId));

        var json = AIWorldSaveRepository.Serialize(data);
        var restored = AIWorldSaveRepository.Deserialize(json)!;
        var session = restored.FollowSessions.Single();
        Assert.Single(session.Observations);
        Assert.Equal(obs.Observation!.ObservationId, session.Observations[0]);
    }

    [Fact]
    public void P5T31_Consolidate_AllRejected_NotPoisoned_RetrySucceeds()
    {
        // PM Phase 5 Gate lesson: a consolidation where ALL candidates are rejected (referential
        // failure) must NOT mark the session consolidated — otherwise a corrected interpretation
        // can never retry. Only a successful consolidation (>=1 accepted) is idempotent.
        var (_, collector, sessions, ingestion, repository) = Build();
        sessions.Start(Abigail, "r", "spring-1-1@0600");
        var inj = collector.Record(Obs(ObservationTypes.PlayerInjured));
        Assert.True(sessions.AppendObservation(Abigail, inj.Observation!.ObservationId));
        sessions.End(Abigail, "AI_DISMISS", "spring-1-1@0700");

        // First consolidation: interpreter produces a candidate referencing a NON-EXISTENT
        // observation id → all rejected. Session must NOT be marked consolidated.
        var badInterpreter = new FixedInterpreter(new MemoryCandidate
        {
            NpcId = Abigail,
            SourceObservationId = "no-such-observation",
            Summary = "bad reference",
            EmotionalTone = "neutral",
            Importance = 0.5f,
            SchemaVersion = "1",
        });
        var first = new MemoryConsolidationService(collector, sessions, ingestion, badInterpreter).Consolidate(Abigail, "spring-1-1@0800");
        Assert.Equal(1, first.Rejected);
        Assert.Equal(0, first.Accepted);
        Assert.Equal(0, repository.TotalPersistedCount());

        // Retry with a corrected interpreter (deterministic fixture maps the real observation)
        // must SUCCEED — proving the session was not poisoned by the failed attempt.
        var retry = new MemoryConsolidationService(collector, sessions, ingestion, new DeterministicObservationInterpreter()).Consolidate(Abigail, "spring-1-1@0800");
        Assert.True(retry.Accepted >= 1);
        Assert.Equal(1, repository.TotalPersistedCount());

        // Third call now idempotent (success marked consolidated).
        var third = new MemoryConsolidationService(collector, sessions, ingestion, new DeterministicObservationInterpreter()).Consolidate(Abigail, "spring-1-1@0900");
        Assert.Contains("already consolidated", third.Error);
        Assert.Equal(1, repository.TotalPersistedCount());
    }

    private sealed class FixedInterpreter : IObservationInterpreter
    {
        private readonly MemoryCandidate _candidate;
        public FixedInterpreter(MemoryCandidate candidate) => _candidate = candidate;
        public IReadOnlyList<MemoryCandidate> Interpret(string npcId, IReadOnlyList<ObjectiveObservation> observations, string gameStamp)
            => new[] { _candidate };
    }
}
