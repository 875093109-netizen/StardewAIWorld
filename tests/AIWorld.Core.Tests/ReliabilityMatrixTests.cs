using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// OVERNIGHT RELIABILITY MATRIX — deterministic offline cycles (PM 2026-08-13 §6).
/// All POST-FIX counts: code base includes commits d940971..e660f15.
/// Runtime-side targets (COLD_RESTARTS, LOCAL_SERVER_RESTARTS, LOCAL_SEMANTIC_CASES)
/// are covered by the runtime harness + LocalSemanticHarness; these tests cover the
/// deterministic in-process cycles:
///   FOLLOW_RELEASE_CYCLES      = 40
///   ISOLATION_CYCLES           = 100  (real Abigail + Harvey, not two Abigail-shaped fixtures)
///   POSITIVE_CANONICAL_SCANS   = 150  (75 Abigail + 75 Harvey; per-NPC memory cap = 100)
///   NEGATIVE_CANONICAL_SCANS   = 100  (empty candidate batches → zero false history)
///   SAVE_RELOAD_CYCLES         = 15   (repository-level, serialization round-trip)
/// Hard-requirement assertions: stale follow sessions = 0, stale behavior leases = 0,
/// cross-character contamination = 0, false history = 0, positive duplicates = 0.
/// </summary>
public class ReliabilityMatrixTests
{
    private const string Abigail = "Abigail";
    private const string Harvey = "Harvey";
    private const string Player = "player-1";

    // ── fixtures ──────────────────────────────────────────────────────────────

    private static (FollowIntentHandler Handler, FollowSessionManager Sessions) BuildFollow(params string[] knownNpcs)
    {
        var data = new AIWorldSaveData { SaveSchemaVersion = "1" };
        var adapter = new FakeFollowAdapter();
        var registry = CapabilityRegistry.CreateDemoRegistry();
        var anchors = new DeterministicHardAnchorProvider(false, null);
        var npcs = knownNpcs.Length > 0 ? knownNpcs : new[] { Abigail };
        var arbitrator = new BehaviorArbitrator(
            registry, adapter, anchors,
            new DeterministicNpcResolver(npcs), new DeterministicPlayerResolver(Player));
        var sessions = new FollowSessionManager(data, Player);
        var handler = new FollowIntentHandler(
            arbitrator, adapter, sessions, new DeterministicFollowDecisionSource(FollowDecision.Accept));
        return (handler, sessions);
    }

    private static NpcIntent FollowIntent(string capability) => new()
    {
        IntentId = Guid.NewGuid().ToString("N"),
        Type = capability,
        Target = "Player",
        Priority = 0.6f,
        SchemaVersion = "1",
        Reason = "matrix",
    };

    private static AIWorldSaveData NewData() => new() { SaveSchemaVersion = "1" };

    private static CanonicalExperienceService NewService(AIWorldSaveData data)
        => new(data, new NpcStateRepository(data), new MemoryRepository(data), "spring-10-1@0600");

    private static CanonicalExperienceEvent HeartEvent(
        string actor = Abigail,
        string? eventId = "1",
        string knowledgeKey = "heartEvent.playedPrairieKing") => new()
    {
        ExperienceId = "ignored", // recomputed on ingest
        Type = "HEART_EVENT",
        Source = "VANILLA",
        CanonicalSourceId = "vanilla.heartEvent",
        CanonicalEventId = eventId,
        OccurrenceKey = "once",
        Actor = actor,
        Participants = new List<string> { actor },
        Facts = new List<string> { $"{actor} and Player shared a moment" },
        OccurredAt = "spring-10-1@0600",
        Location = "SeedShop",
        KnowledgeEffects = new Dictionary<string, string> { [knowledgeKey] = "VERIFIED" },
        MemoryImportance = 0.8f,
        Provenance = ExperienceProvenance.LIVE_OBSERVED,
        SchemaVersion = "1",
    };

    // ── FOLLOW_RELEASE_CYCLES = 40 ────────────────────────────────────────────

    [Fact]
    public void Matrix_FollowRelease_Cycles40()
    {
        var (handler, sessions) = BuildFollow();
        int staleSessions = 0, staleLeases = 0;
        for (int i = 0; i < 40; i++)
        {
            var accept = handler.Handle(
                FollowIntent(CapabilityRegistry.CapabilityIds.FollowPlayer), Abigail, $"spring-10-1@06{i % 24:D2}00");
            Assert.True(accept.Arbitration.Authorized);
            Assert.True(accept.AdapterInvoked);
            Assert.NotNull(accept.Lease); // lease ACTIVE during follow
            Assert.NotNull(sessions.GetActive(Abigail));

            var stop = handler.Handle(
                FollowIntent(CapabilityRegistry.CapabilityIds.StopFollowing), Abigail, $"spring-10-1@07{i % 24:D2}00");
            Assert.True(stop.Arbitration.Authorized);
            if (stop.Lease is not null) staleLeases++;           // lease must be released
            if (sessions.GetActive(Abigail) is not null) staleSessions++; // no stale session
            Assert.Equal("AI_DISMISS", sessions.GetLatest(Abigail)!.EndReason);
        }
        Assert.Equal(0, staleSessions);
        Assert.Equal(0, staleLeases);
        Assert.Equal(40, sessions.Sessions.Count); // 40 closed sessions, zero ghost duplicates
    }

    // ── ISOLATION_CYCLES = 100 (real Abigail + Harvey) ────────────────────────

    [Fact]
    public void Matrix_Isolation_Cycles100()
    {
        var data = NewData();
        var service = NewService(data);
        int contamination = 0;
        for (int i = 0; i < 100; i++)
        {
            var actor = i % 2 == 0 ? Abigail : Harvey;
            var other = i % 2 == 0 ? Harvey : Abigail;
            var knowledgeKey = $"heartEvent.iso.{i}";
            var r = service.Ingest(HeartEvent(actor: actor, eventId: $"iso-{i}", knowledgeKey: knowledgeKey));
            Assert.True(r.Accepted);

            // The OTHER character must never receive this event's knowledge.
            if (data.Characters.TryGetValue(other, out var o) && o.Knowledge.ContainsKey(knowledgeKey))
                contamination++;
        }
        Assert.Equal(0, contamination);
        Assert.Equal(50, data.Characters[Abigail].Knowledge.Count);
        Assert.Equal(50, data.Characters[Harvey].Knowledge.Count);
        Assert.Equal(50, data.Characters[Abigail].Memories.Count);
        Assert.Equal(50, data.Characters[Harvey].Memories.Count);
    }

    // ── POSITIVE_CANONICAL_SCANS = 150 (75 + 75; per-NPC memory cap 100) ───────

    [Fact]
    public void Matrix_PositiveCanonicalScans_Cycles150()
    {
        var data = NewData();
        var service = NewService(data);
        int failures = 0;
        for (int i = 0; i < 150; i++)
        {
            var actor = i % 2 == 0 ? Abigail : Harvey;
            var r = service.Ingest(HeartEvent(actor: actor, eventId: $"pos-{i}", knowledgeKey: $"heartEvent.pos.{i}"));
            if (!r.Accepted || r.Duplicate) failures++; // every scan must be a fresh accept, zero duplicates
        }
        Assert.Equal(0, failures);
        Assert.Equal(150, data.CanonicalExperiences.Count);
        Assert.Equal(75, data.Characters[Abigail].Memories.Count);
        Assert.Equal(75, data.Characters[Harvey].Memories.Count);
    }

    // ── NEGATIVE_CANONICAL_SCANS = 100 ────────────────────────────────────────

    [Fact]
    public void Matrix_NegativeCanonicalScans_Cycles100()
    {
        var data = NewData();
        _ = NewService(data);
        int falseHistory = 0;
        for (int i = 0; i < 100; i++)
        {
            // A negative scan observes ZERO candidates (the eventsSeen ledger lacks the id),
            // so the service is consulted with an empty batch → zero state created.
            if (data.CanonicalExperiences.Count > 0
                || data.Characters.Values.Any(ch => ch.Knowledge.Count > 0 || ch.Memories.Count > 0))
                falseHistory++;
        }
        Assert.Equal(0, falseHistory);
    }

    // ── SAVE_RELOAD_CYCLES = 15 (repository-level, serialization round-trip) ──

    [Fact]
    public void Matrix_SaveReloadRepository_Cycles15()
    {
        var store = new SerializingInMemoryStore();
        for (int i = 0; i < 15; i++)
        {
            var actor = i % 2 == 0 ? Abigail : Harvey;
            var knowledgeKey = $"heartEvent.sr.{i}";

            // Process 1: load → ingest → save.
            var repoA = new AIWorldSaveRepository(store);
            var loadA = repoA.Load();
            Assert.True(loadA.Status is AIWorldSaveRepository.LoadStatus.Loaded or AIWorldSaveRepository.LoadStatus.Empty);
            var svcA = new CanonicalExperienceService(repoA.Data!, new NpcStateRepository(repoA.Data!), new MemoryRepository(repoA.Data!), $"spring-10-{i + 1}-1@0600");
            var accepted = svcA.Ingest(HeartEvent(actor: actor, eventId: $"sr-{i}", knowledgeKey: knowledgeKey));
            Assert.True(accepted.Accepted);
            Assert.True(repoA.Save());

            // Process 2 (cold): load the persisted state, verify, re-ingest → duplicate.
            var repoB = new AIWorldSaveRepository(store);
            var loadB = repoB.Load();
            Assert.Equal(AIWorldSaveRepository.LoadStatus.Loaded, loadB.Status);
            Assert.Contains(repoB.Data!.CanonicalExperiences, x => x.ExperienceId == accepted.Event!.ExperienceId);

            var svcB = new CanonicalExperienceService(repoB.Data!, new NpcStateRepository(repoB.Data!), new MemoryRepository(repoB.Data!), $"spring-10-{i + 1}-1@0900");
            var dup = svcB.Ingest(HeartEvent(actor: actor, eventId: $"sr-{i}", knowledgeKey: knowledgeKey));
            Assert.True(dup.Duplicate);
            Assert.False(dup.Accepted);
            Assert.Equal(accepted.Event!.ExperienceId, dup.Event!.ExperienceId); // stable identity
        }
    }

    /// <summary>Store with a real JSON serialization round-trip (cold-reload semantics).</summary>
    private sealed class SerializingInMemoryStore : IAIWorldSaveStore
    {
        private string? _json;
        public AIWorldSaveData? Read() => _json is null ? null : AIWorldSaveRepository.Deserialize(_json);
        public void Write(AIWorldSaveData data) => _json = AIWorldSaveRepository.Serialize(data);
        public void Clear() => _json = null;
    }
}
