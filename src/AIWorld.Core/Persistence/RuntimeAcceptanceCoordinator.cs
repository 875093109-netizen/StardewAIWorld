using AIWorld.Core.Contracts;

namespace AIWorld.Core.Persistence;

/// <summary>
/// P3 runtime acceptance coordinator — sentinel-driven two-stage state machine
/// (PM correction §1-§10). Pure logic, no SMAPI dependency; testable offline.
///
/// Stage selection:
///   completed sentinel present            → NONE (no re-run)
///   test sentinel absent                  → STAGE_A_WRITE (mutate only when absent)
///   test sentinel present, not completed  → STAGE_B_VERIFY (assert BEFORE any mutation)
///
/// Stage B asserts strictly from the deserialized save root; lazy-created NPCs or
/// default values can never count as "restored".
/// </summary>
public sealed class RuntimeAcceptanceCoordinator
{
    public const string SentinelMetadataKey = "PHASE3_RUNTIME_TEST_ID";
    public const string CompletedMetadataKey = "PHASE3_RUNTIME_TEST_COMPLETED";
    public const string TestNpc = "Abigail";
    public const string TestMemorySource = "phase3-runtime-acceptance";

    public enum Stage
    {
        None,
        StageAWrite,
        StageBVerify,
    }

    private readonly AIWorldSaveData _data;
    private readonly StateDeltaApplier _stateApplier;
    private readonly MemoryIngestionService _memoryService;
    private readonly string _gameDate;
    private readonly string _runtimeTestId;

    public RuntimeAcceptanceCoordinator(
        AIWorldSaveData data,
        StateDeltaApplier stateApplier,
        MemoryIngestionService memoryService,
        string gameDate,
        string? runtimeTestId = null)
    {
        _data = data;
        _stateApplier = stateApplier;
        _memoryService = memoryService;
        _gameDate = gameDate;
        _runtimeTestId = runtimeTestId ?? Guid.NewGuid().ToString("N");
    }

    /// <summary>Select the current stage based on sentinels (no mutation here).</summary>
    public Stage SelectStage()
    {
        bool completed = _data.Metadata.TryGetValue(CompletedMetadataKey, out var done) && done == "true";
        if (completed) return Stage.None;
        bool sentinel = _data.Metadata.TryGetValue(SentinelMetadataKey, out _);
        return sentinel ? Stage.StageBVerify : Stage.StageAWrite;
    }

    /// <summary>Stage A: apply state + knowledge RUMOR + episodic memory + sentinel. In-memory only.</summary>
    public bool RunStageAWrite()
    {
        var delta = new StateDelta
        {
            NpcId = TestNpc,
            Path = "state.mood",
            Value = System.Text.Json.JsonDocument.Parse("\"curious\"").RootElement.Clone(),
            Reason = $"{TestMemorySource}:{_runtimeTestId}",
            SchemaVersion = "1",
        };
        var apply = _stateApplier.ApplyBatch(new[] { delta }, _gameDate);
        if (!apply.Applied) return false;

        var next = _stateApplier.ApplyKnowledgeTransition(TestNpc, "magic.exists", _gameDate, sourceObservationId: $"{TestMemorySource}:{_runtimeTestId}");
        if (next != CharacterKnowledge.StateRumor) return false;

        var candidate = new MemoryCandidate
        {
            NpcId = TestNpc,
            SourceObservationId = $"{TestMemorySource}:{_runtimeTestId}",
            Summary = $"Phase 3 runtime test: Abigail felt curious about magic (test id {_runtimeTestId}).",
            EmotionalTone = "positive",
            Importance = 0.7f,
            SchemaVersion = "1",
        };
        var ingest = _memoryService.Ingest(candidate, MemoryLayer.Episodic);
        if (!ingest.Accepted) return false;

        _data.Metadata[SentinelMetadataKey] = _runtimeTestId;
        _data.Metadata[CompletedMetadataKey] = "false";
        return true;
    }

    /// <summary>Stage B: assert restored data strictly from save root, BEFORE any mutation. Never mutates.</summary>
    public sealed class VerifyResult
    {
        public bool AllPass { get; init; }
        public bool SentinelFound { get; init; }
        public bool StateRestored { get; init; }
        public bool KnowledgeRestored { get; init; }
        public bool NoLearned { get; init; }
        public bool MemoryRestored { get; init; }
        public bool PersonaUnchanged { get; init; }
        public string? SavedTestId { get; init; }
    }

    public VerifyResult RunStageBVerify()
    {
        bool sentinelFound = _data.Metadata.TryGetValue(SentinelMetadataKey, out var savedTestId);
        if (!sentinelFound)
            return new VerifyResult { SentinelFound = false };

        bool npcExists = _data.Characters.TryGetValue(NpcPersistentIdPolicy.Resolve(TestNpc), out var npc);
        bool stateOk = npcExists && npc!.State.TryGetValue("mood", out var mood) && mood.GetString() == "curious";
        bool knowledgeOk = npcExists && npc!.Knowledge.TryGetValue("magic.exists", out var kn) && kn.State == CharacterKnowledge.StateRumor;
        bool noLearned = npcExists && !npc!.Knowledge.Values.Any(k => k.State == "LEARNED");
        bool memoryOk = npcExists && npc!.Memories.Any(m =>
            m.Layer == MemoryLayer.Episodic &&
            m.SourceObservationId == $"{TestMemorySource}:{savedTestId}");
        bool personaUnchanged = !_data.Characters.Values.Any(c => c.State.Keys.Any(k => k.StartsWith("persona.")));

        bool allPass = stateOk && knowledgeOk && noLearned && memoryOk && personaUnchanged;
        return new VerifyResult
        {
            AllPass = allPass,
            SentinelFound = true,
            StateRestored = stateOk,
            KnowledgeRestored = knowledgeOk,
            NoLearned = noLearned,
            MemoryRestored = memoryOk,
            PersonaUnchanged = personaUnchanged,
            SavedTestId = savedTestId,
        };
    }

    /// <summary>Mark the runtime test completed (after Stage B pass) so a third load does not re-run.</summary>
    public void MarkCompleted() => _data.Metadata[CompletedMetadataKey] = "true";

    public string RuntimeTestId => _runtimeTestId;
}
