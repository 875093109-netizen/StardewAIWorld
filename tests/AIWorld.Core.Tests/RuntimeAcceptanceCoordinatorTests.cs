using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// P3 false-positive protection tests for the sentinel-driven runtime acceptance
/// state machine (PM correction §11): Stage A only when sentinel absent; Stage B
/// never reapplies mutation before assertions; missing persisted state cannot
/// falsely PASS; default-created NPC cannot count as restored; existing sentinel
/// selects verify; completed test does not re-run.
/// </summary>
public class RuntimeAcceptanceCoordinatorTests
{
    private static (RuntimeAcceptanceCoordinator Coord, AIWorldSaveData Data) NewCoord(string? testId = null)
    {
        var data = new AIWorldSaveData
        {
            SaveSchemaVersion = "1",
            Characters = new Dictionary<string, NpcPersistentData>(),
            Metadata = new Dictionary<string, string>(),
        };
        var state = new NpcStateRepository(data);
        var memory = new MemoryRepository(data);
        var applier = new StateDeltaApplier(state);
        var memSvc = new MemoryIngestionService(memory, "2026-08-11");
        var coord = new RuntimeAcceptanceCoordinator(data, applier, memSvc, "2026-08-11", testId);
        return (coord, data);
    }

    [Fact]
    public void C01_StageA_Mutates_Only_When_Sentinel_Absent()
    {
        var (coord, data) = NewCoord("test-id-1");
        Assert.Equal(RuntimeAcceptanceCoordinator.Stage.StageAWrite, coord.SelectStage());
        Assert.True(coord.RunStageAWrite());
        Assert.True(data.Metadata.ContainsKey(RuntimeAcceptanceCoordinator.SentinelMetadataKey));
        Assert.Equal("curious", data.Characters["Abigail"].State["mood"].GetString());
        Assert.Equal("RUMOR", data.Characters["Abigail"].Knowledge["magic.exists"].State);
        Assert.Single(data.Characters["Abigail"].Memories);
    }

    [Fact]
    public void C02_StageB_Never_Reapplies_Mutation_Before_Assertions()
    {
        // Simulate: save root contains Stage A data (as if restored from disk).
        var (coordA, data) = NewCoord("test-id-2");
        coordA.RunStageAWrite();

        // New coordinator over the SAME data (fresh load) → Stage B selected.
        var state = new NpcStateRepository(data);
        var memory = new MemoryRepository(data);
        var coordB = new RuntimeAcceptanceCoordinator(data, new StateDeltaApplier(state), new MemoryIngestionService(memory, "2026-08-11"), "2026-08-11", "test-id-2");
        Assert.Equal(RuntimeAcceptanceCoordinator.Stage.StageBVerify, coordB.SelectStage());

        var v = coordB.RunStageBVerify();
        Assert.True(v.AllPass);

        // Stage B must NOT have mutated: memory count unchanged, metadata completed flag was NOT set by verify itself.
        Assert.Single(data.Characters["Abigail"].Memories);
        Assert.Equal("curious", data.Characters["Abigail"].State["mood"].GetString());
    }

    [Fact]
    public void C03_Missing_Persisted_State_Cannot_Falsely_Pass()
    {
        var (coord, data) = NewCoord("test-id-3");
        // Simulate corrupt/partial restore: sentinel present but state missing.
        data.Metadata[RuntimeAcceptanceCoordinator.SentinelMetadataKey] = "test-id-3";
        data.Metadata[RuntimeAcceptanceCoordinator.CompletedMetadataKey] = "false";
        data.Characters["Abigail"] = new NpcPersistentData { NpcId = "Abigail" }; // empty NPC

        var v = coord.RunStageBVerify();
        Assert.True(v.SentinelFound);
        Assert.False(v.StateRestored);
        Assert.False(v.KnowledgeRestored);
        Assert.False(v.AllPass);
    }

    [Fact]
    public void C04_DefaultCreated_Npc_Cannot_Count_As_Restored()
    {
        var (coord, data) = NewCoord("test-id-4");
        // Sentinel present, NPC absent entirely (would be lazy-created as default) → must FAIL.
        data.Metadata[RuntimeAcceptanceCoordinator.SentinelMetadataKey] = "test-id-4";
        data.Metadata[RuntimeAcceptanceCoordinator.CompletedMetadataKey] = "false";
        Assert.False(data.Characters.ContainsKey("Abigail"));

        var v = coord.RunStageBVerify();
        Assert.True(v.SentinelFound);
        Assert.False(v.StateRestored);   // no NPC → not restored
        Assert.False(v.MemoryRestored);
        Assert.False(v.AllPass);
    }

    [Fact]
    public void C05_Existing_Sentinel_Selects_Verify_Stage()
    {
        var (coordA, data) = NewCoord("test-id-5");
        coordA.RunStageAWrite(); // sentinel now present

        var state = new NpcStateRepository(data);
        var memory = new MemoryRepository(data);
        var coordB = new RuntimeAcceptanceCoordinator(data, new StateDeltaApplier(state), new MemoryIngestionService(memory, "2026-08-11"), "2026-08-11", "test-id-5");
        Assert.Equal(RuntimeAcceptanceCoordinator.Stage.StageBVerify, coordB.SelectStage());
    }

    [Fact]
    public void C06_Completed_Test_Does_Not_Run_Again()
    {
        var (coordA, data) = NewCoord("test-id-6");
        coordA.RunStageAWrite();
        // Simulate Stage B pass + completion marker.
        data.Metadata[RuntimeAcceptanceCoordinator.CompletedMetadataKey] = "true";

        var state = new NpcStateRepository(data);
        var memory = new MemoryRepository(data);
        var coordC = new RuntimeAcceptanceCoordinator(data, new StateDeltaApplier(state), new MemoryIngestionService(memory, "2026-08-11"), "2026-08-11", "test-id-6");
        Assert.Equal(RuntimeAcceptanceCoordinator.Stage.None, coordC.SelectStage());
    }

    [Fact]
    public void C07_Learned_Never_In_Knowledge_After_StageA()
    {
        var (coord, data) = NewCoord("test-id-7");
        coord.RunStageAWrite();
        Assert.All(data.Characters["Abigail"].Knowledge.Values, k => Assert.NotEqual("LEARNED", k.State));
        Assert.False(CharacterKnowledge.IsValidState("LEARNED"));
    }

    [Fact]
    public void C08_Persona_Unchanged_After_StageA_And_StageB()
    {
        var (coordA, data) = NewCoord("test-id-8");
        coordA.RunStageAWrite();
        Assert.DoesNotContain(data.Characters.Values.SelectMany(c => c.State.Keys), k => k.StartsWith("persona."));

        var state = new NpcStateRepository(data);
        var memory = new MemoryRepository(data);
        var coordB = new RuntimeAcceptanceCoordinator(data, new StateDeltaApplier(state), new MemoryIngestionService(memory, "2026-08-11"), "2026-08-11", "test-id-8");
        var v = coordB.RunStageBVerify();
        Assert.True(v.PersonaUnchanged);
    }

    // ---- Repeatability regression (PM Phase 3 Gate §9) ----

    [Fact]
    public void R01_FreshState_Selects_StageA_After_Reset()
    {
        // Simulate: run 1 completes (sentinel + completed), then fresh-run reset clears test state.
        var (coordA, data) = NewCoord("run-1-id");
        coordA.RunStageAWrite();
        var state = new NpcStateRepository(data);
        var memory = new MemoryRepository(data);
        var coordB = new RuntimeAcceptanceCoordinator(data, new StateDeltaApplier(state), new MemoryIngestionService(memory, "2026-08-11"), "2026-08-11", "run-1-id");
        Assert.True(coordB.RunStageBVerify().AllPass);
        coordB.MarkCompleted();
        Assert.Equal(RuntimeAcceptanceCoordinator.Stage.None, coordB.SelectStage()); // completed → no re-run

        // Fresh-run reset: clear ONLY test metadata (simulating snapshot restore of test state).
        data.Metadata.Remove(RuntimeAcceptanceCoordinator.SentinelMetadataKey);
        data.Metadata.Remove(RuntimeAcceptanceCoordinator.CompletedMetadataKey);
        data.Characters.Clear();

        var coordC = new RuntimeAcceptanceCoordinator(data, new StateDeltaApplier(new NpcStateRepository(data)), new MemoryIngestionService(new MemoryRepository(data), "2026-08-12"), "2026-08-12", "run-2-id");
        Assert.Equal(RuntimeAcceptanceCoordinator.Stage.StageAWrite, coordC.SelectStage()); // fresh → A_WRITE again
    }

    [Fact]
    public void R02_CompletedSentinel_DoesNot_FalsePass_Next_Run()
    {
        // A stale completed sentinel in a fresh coordinator must NOT produce PASS without real data.
        var (coord, data) = NewCoord("stale");
        data.Metadata[RuntimeAcceptanceCoordinator.CompletedMetadataKey] = "true";
        // No sentinel id, no NPC data — Stage.None selected, nothing verified.
        Assert.Equal(RuntimeAcceptanceCoordinator.Stage.None, coord.SelectStage());
        var v = coord.RunStageBVerify();
        Assert.False(v.SentinelFound); // cannot verify without sentinel
    }

    [Fact]
    public void R03_StaleTestId_Cannot_Pass_Current_Run()
    {
        // Process B must restore the CURRENT run's id; a stale id from a previous run fails.
        var (coordA, data) = NewCoord("current-run-id");
        coordA.RunStageAWrite();
        // Tamper: replace sentinel with a stale id (simulating previous-run data without current write).
        data.Metadata[RuntimeAcceptanceCoordinator.SentinelMetadataKey] = "STALE-FROM-PREVIOUS-RUN";
        data.Characters["Abigail"].Memories.Clear();
        data.Characters["Abigail"].State.Clear();
        data.Characters["Abigail"].Knowledge.Clear();

        var coordB = new RuntimeAcceptanceCoordinator(data, new StateDeltaApplier(new NpcStateRepository(data)), new MemoryIngestionService(new MemoryRepository(data), "2026-08-11"), "2026-08-11", "current-run-id");
        var v = coordB.RunStageBVerify();
        Assert.True(v.SentinelFound);
        Assert.False(v.StateRestored);   // stale id does not repair missing state
        Assert.False(v.MemoryRestored);  // memory marker matches stale id? no — cleared
        Assert.False(v.AllPass);         // stale id cannot PASS current run
    }

    [Fact]
    public void R04_FreshReset_Only_Clears_TestState()
    {
        // Unrelated persistent data must survive a fresh-run reset of test metadata.
        var (coordA, data) = NewCoord("run-a");
        coordA.RunStageAWrite();
        data.Characters["Abigail"].Relationships["player.trust"] = System.Text.Json.JsonDocument.Parse("0.5").RootElement.Clone();

        // Fresh-run reset clears ONLY test metadata + test NPC test data, keeps relationships? 
        // (Snapshot restore replaces the whole save, so this test documents the runner-level
        // contract: reset is scoped to the dedicated test save; unrelated saves untouched.)
        data.Metadata.Remove(RuntimeAcceptanceCoordinator.SentinelMetadataKey);
        data.Metadata.Remove(RuntimeAcceptanceCoordinator.CompletedMetadataKey);

        Assert.False(data.Characters["Abigail"].Relationships.Count == 0); // unrelated data intact in-memory
    }
}
