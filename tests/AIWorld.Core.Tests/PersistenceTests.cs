using System.Text.Json;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// P3-06 Offline persistence tests — covers State (1-10), Memory (11-17),
/// Persistence (18-28) per PM Phase 3 §24. All deterministic, no LLM.
/// </summary>
public class PersistenceTests
{
    private static (AIWorldSaveRepository Repo, NpcStateRepository State, MemoryRepository Memory) NewRepo()
    {
        var store = new InMemorySaveStore();
        var repo = new AIWorldSaveRepository(store);
        var load = repo.Load();
        Assert.Equal(AIWorldSaveRepository.LoadStatus.Empty, load.Status);
        return (repo, repo.State!, repo.Memory!);
    }

    private static StateDelta Delta(string npc, string path, string value, string reason = "test") => new()
    {
        NpcId = npc,
        Path = path,
        Value = JsonDocument.Parse($"\"{value}\"").RootElement.Clone(),
        Reason = reason,
        SchemaVersion = "1",
    };

    private static MemoryCandidate MemCandidate(string npc, string summary, string tone = "neutral", float importance = 0.5f) => new()
    {
        NpcId = npc,
        SourceObservationId = "obs-1",
        Summary = summary,
        EmotionalTone = tone,
        Importance = importance,
        SchemaVersion = "1",
    };

    // ---- State (1-10) ----

    [Fact]
    public void T01_NewNpcState_IsEmptyUntilWritten()
    {
        var (_, state, _) = NewRepo();
        Assert.Null(state.GetStateValue("Abigail", "state.mood"));
    }

    [Fact]
    public void T02_StateDelta_ValidApply()
    {
        var (_, state, _) = NewRepo();
        var applier = new StateDeltaApplier(state);
        var result = applier.ApplyBatch(new[] { Delta("Abigail", "state.mood", "calm") }, "2026-08-11");
        Assert.True(result.Applied);
        Assert.Equal(1, result.AppliedCount);
        Assert.Equal("calm", state.GetStateValue("Abigail", "state.mood")!.Value.GetString());
        Assert.True(state.Dirty);
    }

    [Fact]
    public void T03_IllegalPath_Reject()
    {
        var (_, state, _) = NewRepo();
        var applier = new StateDeltaApplier(state);
        var result = applier.ApplyBatch(new[] { Delta("Abigail", "not.a.path", "x") }, "2026-08-11");
        Assert.False(result.Applied);
        Assert.Contains("not allowlisted", string.Join(";", result.Errors));
        Assert.False(state.Dirty); // no mutation on rejection
    }

    [Fact]
    public void T04_WrongType_Reject()
    {
        var (_, state, _) = NewRepo();
        var applier = new StateDeltaApplier(state);
        var obj = new StateDelta
        {
            NpcId = "Abigail",
            Path = "state.mood",
            Value = JsonDocument.Parse("{\"nested\":true}").RootElement.Clone(), // object, not scalar
            Reason = "test",
            SchemaVersion = "1",
        };
        var result = applier.ApplyBatch(new[] { obj }, "2026-08-11");
        Assert.False(result.Applied);
        Assert.Contains("must be a JSON scalar", string.Join(";", result.Errors));
    }

    [Fact]
    public void T05_PersonaMutation_Reject()
    {
        var (_, state, _) = NewRepo();
        var applier = new StateDeltaApplier(state);
        var result = applier.ApplyBatch(new[] { Delta("Abigail", "persona.core_values", "evil") }, "2026-08-11");
        Assert.False(result.Applied);
        Assert.Contains("forbidden namespace", string.Join(";", result.Errors));
    }

    [Fact]
    public void T06_BatchTransaction_Rollback()
    {
        var (_, state, _) = NewRepo();
        var applier = new StateDeltaApplier(state);
        var batch = new[]
        {
            Delta("Abigail", "state.mood", "happy"),      // valid
            Delta("Abigail", "illegal.path", "x"),         // invalid → whole batch rejected
        };
        var result = applier.ApplyBatch(batch, "2026-08-11");
        Assert.False(result.Applied);
        Assert.Contains("STATE_DELTA_BATCH_REJECTED", string.Join(";", result.Errors));
        Assert.Null(state.GetStateValue("Abigail", "state.mood")); // no partial write
        Assert.False(state.Dirty);
    }

    [Fact]
    public void T07_Knowledge_UnknownToRumor()
    {
        var (_, state, _) = NewRepo();
        var applier = new StateDeltaApplier(state);
        var next = applier.ApplyKnowledgeTransition("Abigail", "magic.exists", "2026-08-11");
        Assert.Equal(CharacterKnowledge.StateRumor, next);
        Assert.Equal(CharacterKnowledge.StateRumor, state.GetKnowledgeState("Abigail", "magic.exists"));
    }

    [Fact]
    public void T08_Knowledge_RumorToVerified()
    {
        var (_, state, _) = NewRepo();
        var applier = new StateDeltaApplier(state);
        applier.ApplyKnowledgeTransition("Abigail", "magic.exists", "2026-08-11");
        var next = applier.ApplyKnowledgeTransition("Abigail", "magic.exists", "2026-08-12");
        Assert.Equal(CharacterKnowledge.StateVerified, next);
        Assert.Equal(CharacterKnowledge.StateVerified, state.GetKnowledgeState("Abigail", "magic.exists"));
    }

    [Fact]
    public void T09_Learned_CannotEnterCharacterKnowledge()
    {
        var (_, state, _) = NewRepo();
        Assert.False(CharacterKnowledge.IsValidState("LEARNED"));
        var applier = new StateDeltaApplier(state);
        var bad = new StateDelta
        {
            NpcId = "Abigail",
            Path = "knowledge.magic.exists",
            Value = JsonDocument.Parse("\"LEARNED\"").RootElement.Clone(),
            Reason = "test",
            SchemaVersion = "1",
        };
        var result = applier.ApplyBatch(new[] { bad }, "2026-08-11");
        Assert.False(result.Applied); // LEARNED rejected as knowledge state
    }

    [Fact]
    public void T10_MagicAdoptionState_Independent()
    {
        var (_, state, _) = NewRepo();
        var applier = new StateDeltaApplier(state);
        Assert.True(applier.ApplyBatch(new[] { Delta("Abigail", "skills.magic", "1", "adopted") }, "2026-08-11").Applied);
        Assert.True(applier.ApplyBatch(new[] { Delta("Abigail", "skills.magicStudent", "true") }, "2026-08-11").Applied);
        Assert.Equal("1", state.GetStateValue("Abigail", "skills.magic")!.Value.GetString());
        Assert.Equal("true", state.GetStateValue("Abigail", "skills.magicStudent")!.Value.GetString());
        // knowledge stays separate:
        Assert.Null(state.GetKnowledgeState("Abigail", "magic.exists"));
    }

    // ---- Memory (11-17) ----

    [Fact]
    public void T11_ValidMemoryCandidate_Persist()
    {
        var (_, _, memory) = NewRepo();
        var svc = new MemoryIngestionService(memory, "2026-08-11");
        var result = svc.Ingest(MemCandidate("Abigail", "Player was reckless in the mines."), MemoryLayer.Episodic);
        Assert.True(result.Accepted);
        Assert.NotNull(result.Record);
        Assert.Equal("mem-Abigail-2026-08-11-0", result.Record!.MemoryId);
        Assert.True(memory.Dirty);
    }

    [Fact]
    public void T12_InvalidMemoryCandidate_Reject()
    {
        var (_, _, memory) = NewRepo();
        var svc = new MemoryIngestionService(memory, "2026-08-11");
        var bad = MemCandidate("Abigail", "x");
        bad = new MemoryCandidate
        {
            NpcId = "Abigail",
            SourceObservationId = "obs-1",
            Summary = "", // invalid
            EmotionalTone = "neutral",
            Importance = 0.5f,
            SchemaVersion = "1",
        };
        var result = svc.Ingest(bad, MemoryLayer.Episodic);
        Assert.False(result.Accepted);
        Assert.Contains("missing summary", result.Error);
        Assert.False(memory.Dirty);
    }

    [Fact]
    public void T13_EpisodicRetrieval()
    {
        var (_, _, memory) = NewRepo();
        var svc = new MemoryIngestionService(memory, "2026-08-11");
        svc.Ingest(MemCandidate("Abigail", "First event."), MemoryLayer.Episodic);
        svc.Ingest(MemCandidate("Abigail", "Second event."), MemoryLayer.Episodic);
        var recent = memory.GetRecentMemories("Abigail", 10);
        Assert.Equal(2, recent.Count);
        Assert.Equal("Second event.", recent[0].Summary); // newest first
    }

    [Fact]
    public void T14_PerNpcIsolation()
    {
        var (_, _, memory) = NewRepo();
        var svc = new MemoryIngestionService(memory, "2026-08-11");
        svc.Ingest(MemCandidate("Abigail", "Abigail memory."), MemoryLayer.Episodic);
        Assert.Empty(memory.GetRecentMemories("Sebastian", 10));
        Assert.Single(memory.GetRecentMemories("Abigail", 10));
    }

    [Fact]
    public void T15_MemoryLimit_Enforced()
    {
        var (_, _, memory) = NewRepo();
        var svc = new MemoryIngestionService(memory, "2026-08-11");
        for (int i = 0; i < MemoryLimits.MaxPersistedMemoriesPerNpc + 5; i++)
        {
            svc.Ingest(MemCandidate("Abigail", $"event {i}"), MemoryLayer.Episodic);
        }
        Assert.Equal(MemoryLimits.MaxPersistedMemoriesPerNpc, memory.GetRecentMemories("Abigail", int.MaxValue).Count);
    }

    [Fact]
    public void T16_OversizedMemory_Reject()
    {
        var (_, _, memory) = NewRepo();
        var svc = new MemoryIngestionService(memory, "2026-08-11");
        var big = new MemoryCandidate
        {
            NpcId = "Abigail",
            SourceObservationId = "obs-1",
            Summary = new string('x', MemoryLimits.MaxMemorySummaryChars + 10),
            EmotionalTone = "neutral",
            Importance = 0.5f,
            SchemaVersion = "1",
        };
        var result = svc.Ingest(big, MemoryLayer.Episodic);
        Assert.False(result.Accepted);
        Assert.Contains("exceeds", result.Error);
    }

    [Fact]
    public void T17_NoAutomaticPersonaMutation()
    {
        var (_, state, memory) = NewRepo();
        var svc = new MemoryIngestionService(memory, "2026-08-11");
        svc.Ingest(MemCandidate("Abigail", "Trying to change persona."), MemoryLayer.PermanentPersona);
        // Ingest to PermanentPersona layer is structurally prevented: service has no persona target;
        // verify no persona data exists anywhere.
        Assert.Empty(state.Data.Characters["Abigail"].State.Where(kv => kv.Key.StartsWith("persona.")));
    }

    // ---- Persistence (18-28) ----

    [Fact]
    public void T18_SerializeRoundTrip()
    {
        var (_, state, memory) = NewRepo();
        var applier = new StateDeltaApplier(state);
        applier.ApplyBatch(new[] { Delta("Abigail", "state.mood", "calm") }, "2026-08-11");
        var svc = new MemoryIngestionService(memory, "2026-08-11");
        svc.Ingest(MemCandidate("Abigail", "round trip memory."), MemoryLayer.Episodic);

        var json = AIWorldSaveRepository.Serialize(state.Data);
        var restored = AIWorldSaveRepository.Deserialize(json);
        Assert.NotNull(restored);
        Assert.Equal("1", restored!.SaveSchemaVersion);
        Assert.Equal("calm", restored.Characters["Abigail"].State["mood"].GetString());
        Assert.Single(restored.Characters["Abigail"].Memories);
    }

    [Fact]
    public void T19_DeserializeRoundTrip()
    {
        var (repo, state, _) = NewRepo();
        var applier = new StateDeltaApplier(state);
        applier.ApplyBatch(new[] { Delta("Abigail", "state.energy", "80") }, "2026-08-11");
        var json = AIWorldSaveRepository.Serialize(state.Data);

        var store2 = new InMemorySaveStore { }; // slot B
        // simulate reload: store has data
        store2.Write(AIWorldSaveRepository.Deserialize(json)!);
        var repo2 = new AIWorldSaveRepository(store2);
        var load2 = repo2.Load();
        Assert.Equal(AIWorldSaveRepository.LoadStatus.Loaded, load2.Status);
        Assert.Equal("80", repo2.State!.GetStateValue("Abigail", "state.energy")!.Value.GetString());
    }

    [Fact]
    public void T20_SaveSlotIsolation()
    {
        // Slot A
        var storeA = new InMemorySaveStore();
        var repoA = new AIWorldSaveRepository(storeA);
        repoA.Load();
        var applierA = new StateDeltaApplier(repoA.State!);
        applierA.ApplyBatch(new[] { Delta("Abigail", "state.mood", "happy") }, "2026-08-11");
        repoA.Save();

        // Slot B — independent store, must be empty
        var storeB = new InMemorySaveStore();
        var repoB = new AIWorldSaveRepository(storeB);
        var loadB = repoB.Load();
        Assert.Equal(AIWorldSaveRepository.LoadStatus.Empty, loadB.Status);
        Assert.Null(repoB.State!.GetStateValue("Abigail", "state.mood"));

        // Slot A data intact
        var repoA2 = new AIWorldSaveRepository(storeA);
        Assert.Equal(AIWorldSaveRepository.LoadStatus.Loaded, repoA2.Load().Status);
        Assert.Equal("happy", repoA2.State!.GetStateValue("Abigail", "state.mood")!.Value.GetString());
    }

    [Fact]
    public void T21_ReturnedToTitle_Cleanup()
    {
        var (repo, state, _) = NewRepo();
        var applier = new StateDeltaApplier(state);
        applier.ApplyBatch(new[] { Delta("Abigail", "state.mood", "x") }, "2026-08-11");
        repo.Save();
        repo.ClearRuntime();
        Assert.False(repo.IsLoaded);
        Assert.Null(repo.State);
    }

    [Fact]
    public void T22_CurrentSchemaLoad()
    {
        var store = new InMemorySaveStore();
        store.Write(new AIWorldSaveData
        {
            SaveSchemaVersion = "1",
            Characters = new Dictionary<string, NpcPersistentData> { ["Abigail"] = new() { NpcId = "Abigail" } },
            Metadata = new Dictionary<string, string>(),
        });
        var repo = new AIWorldSaveRepository(store);
        Assert.Equal(AIWorldSaveRepository.LoadStatus.Loaded, repo.Load().Status);
    }

    [Fact]
    public void T23_OldSchemaMigrationFixture()
    {
        // v0 (hypothetical old) — loader treats "0" as supported-older and migrates (identity here).
        var store = new InMemorySaveStore();
        store.Write(new AIWorldSaveData
        {
            SaveSchemaVersion = "0",
            Characters = new Dictionary<string, NpcPersistentData> { ["Abigail"] = new() { NpcId = "Abigail" } },
            Metadata = new Dictionary<string, string>(),
        });
        var repo = new AIWorldSaveRepository(store);
        var load = repo.Load();
        Assert.Equal(AIWorldSaveRepository.LoadStatus.Loaded, load.Status); // migratable, not corrupt
    }

    [Fact]
    public void T24_FutureSchema_Protected()
    {
        var store = new InMemorySaveStore();
        store.Write(new AIWorldSaveData
        {
            SaveSchemaVersion = "99", // future unknown
            Characters = new Dictionary<string, NpcPersistentData>(),
            Metadata = new Dictionary<string, string>(),
        });
        var repo = new AIWorldSaveRepository(store);
        var load = repo.Load();
        Assert.Equal(AIWorldSaveRepository.LoadStatus.VersionUnsupported, load.Status);
        // READ/WRITE PROTECTION: repo not loaded → cannot overwrite future data
        Assert.False(repo.IsLoaded);
        Assert.Contains("READ/WRITE PROTECTION", load.Message);
    }

    [Fact]
    public void T25_CorruptData_GracefulHandling()
    {
        var store = new CorruptStore();
        var repo = new AIWorldSaveRepository(store);
        var load = repo.Load();
        Assert.Equal(AIWorldSaveRepository.LoadStatus.Corrupt, load.Status);
        // fresh fallback available but NOT auto-overwritten on save (policy: don't clobber corrupt original)
    }

    [Fact]
    public void T26_DirtyFlag_True_AfterMutation()
    {
        var (repo, state, _) = NewRepo();
        var applier = new StateDeltaApplier(state);
        applier.ApplyBatch(new[] { Delta("Abigail", "state.mood", "x") }, "2026-08-11");
        Assert.True(repo.IsDirty);
    }

    [Fact]
    public void T27_FailedSave_RetainsDirty()
    {
        var failingStore = new FailingStore();
        var repo2 = new AIWorldSaveRepository(failingStore);
        repo2.Load();
        var applier2 = new StateDeltaApplier(repo2.State!);
        applier2.ApplyBatch(new[] { Delta("Abigail", "state.mood", "y") }, "2026-08-11");
        Assert.False(repo2.Save()); // write throws → false
        Assert.True(repo2.IsDirty); // dirty retained
    }

    [Fact]
    public void T28_SuccessfulSave_ClearsDirty()
    {
        var (repo, state, _) = NewRepo();
        var applier = new StateDeltaApplier(state);
        applier.ApplyBatch(new[] { Delta("Abigail", "state.mood", "x") }, "2026-08-11");
        Assert.True(repo.Save());
        Assert.False(repo.IsDirty);
    }

    [Fact]
    public void T28b_MarkDataDirty_ThenSuccessfulSave_ClearsDirty()
    {
        // PM OVERNIGHT GATE: MarkDataDirty() (metadata/followSessions root) must be cleared
        // by a successful Save() — otherwise IsDirty stays true forever and the runtime
        // [P3] DIRTY_FALSE marker reports "STILL DIRTY" even after a real persisted write.
        var (repo, _, _) = NewRepo();
        repo.MarkDataDirty();
        Assert.True(repo.IsDirty);
        Assert.True(repo.Save());
        Assert.False(repo.IsDirty);
    }

    // ---- A1 commitment persistence (T29-T33) ----

    private static TravelCommitment Cmt(string id, TravelCommitmentStatus status, string npc = "Abigail", string target = "Beach", int day = 1, int time = 2100) => new()
    {
        CommitmentId = id,
        RequestId = $"req-{id}",
        NpcId = npc,
        GroundedTarget = target,
        TargetGameDay = day,
        TargetGameTime = time,
        AcceptedTermsHash = PlayerRequest.ComputeTermsHash(npc, PlayerRequestKind.FutureAppointment, target, day, time),
        Status = status,
        CreatedAtGameTime = $"1:900",
        SchemaVersion = "1",
    };

    [Fact]
    public void T29_TravelCommitments_SerializeRoundTrip()
    {
        var data = new AIWorldSaveData
        {
            SaveSchemaVersion = AIWorldSaveRepository.CurrentSaveSchemaVersion,
            Characters = new Dictionary<string, NpcPersistentData>(),
            Metadata = new Dictionary<string, string>(),
        };
        data.TravelCommitments.Add(Cmt("c1", TravelCommitmentStatus.WaitingForPlayer));
        data.TravelCommitments.Add(Cmt("c2", TravelCommitmentStatus.Departing, npc: "Harvey"));

        var json = AIWorldSaveRepository.Serialize(data);
        var restored = AIWorldSaveRepository.Deserialize(json);
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.TravelCommitments.Count);
        Assert.Equal(TravelCommitmentStatus.WaitingForPlayer, restored.TravelCommitments[0].Status);
        Assert.Equal("Harvey", restored.TravelCommitments[1].NpcId);
    }

    [Fact]
    public void T30_SaveDataCommitmentStore_SaveWritesRootAndMarksDirty()
    {
        var repo = NewRepo().Repo;
        var store = new SaveDataCommitmentStore(() => repo);
        store.Save(new[] { Cmt("c1", TravelCommitmentStatus.Accepted) });
        Assert.Equal(1, repo.Data!.TravelCommitments.Count);
        Assert.True(repo.IsDirty);
    }

    [Fact]
    public void T31_SaveDataCommitmentStore_LoadReadsFromRoot()
    {
        var repo = NewRepo().Repo;
        repo.Data!.TravelCommitments.Add(Cmt("c1", TravelCommitmentStatus.ArrivedEarly));
        var store = new SaveDataCommitmentStore(() => repo);
        var loaded = store.Load();
        Assert.Single(loaded);
        Assert.Equal(TravelCommitmentStatus.ArrivedEarly, loaded[0].Status);
    }

    [Fact]
    public void T32_SaveDataCommitmentStore_NullRepo_SafeDegrade()
    {
        var store = new SaveDataCommitmentStore(() => null);
        Assert.Empty(store.Load());            // no repo → empty
        store.Save(new[] { Cmt("c1", TravelCommitmentStatus.Accepted) }); // no throw
    }

    [Fact]
    public void T33_SaveDataCommitmentStore_FullRoundTrip_AcrossReload()
    {
        // 每存档持久化往返：manager.Save() → store 写根 → 新 store（同一 slot）Load 恢复。
        var store1 = new InMemorySaveStore();
        var repo1 = new AIWorldSaveRepository(store1);
        repo1.Load();
        var mgr1 = new AppointmentManager(new SaveDataCommitmentStore(() => repo1));
        mgr1.Add(Cmt("c1", TravelCommitmentStatus.WaitingForPlayer));
        mgr1.Save();
        Assert.True(repo1.Save()); // persist via IAIWorldSaveStore

        // 模拟读档：同一 store 槽位重建 repository（跨实例），再从 SaveData 恢复。
        var repo2 = new AIWorldSaveRepository(store1);
        var load2 = repo2.Load();
        Assert.Equal(AIWorldSaveRepository.LoadStatus.Loaded, load2.Status);
        var mgr2 = new AppointmentManager(new SaveDataCommitmentStore(() => repo2));
        mgr2.Load();
        var restored = mgr2.All.SingleOrDefault(c => c.CommitmentId == "c1");
        Assert.NotNull(restored);
        Assert.Equal(TravelCommitmentStatus.WaitingForPlayer, restored!.Status);
        Assert.Equal("Beach", restored.GroundedTarget);
    }

    private sealed class CorruptStore : IAIWorldSaveStore
    {
        public AIWorldSaveData? Read() => throw new InvalidOperationException("simulated corrupt read");
        public void Write(AIWorldSaveData data) { }
        public void Clear() { }
    }

    private sealed class FailingStore : IAIWorldSaveStore
    {
        private AIWorldSaveData? _data;
        public AIWorldSaveData? Read() => _data;
        public void Write(AIWorldSaveData data) => throw new InvalidOperationException("simulated write failure");
        public void Clear() { }
    }
}
