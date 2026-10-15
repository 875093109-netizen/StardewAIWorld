using System.Text.Json;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// Phase 6 — Magic Capability offline tests. Four-way separation (PM §十五):
/// KNOWLEDGE (UNKNOWN/RUMOR/VERIFIED one-way, no LEARNED) ≠ ADOPTION (magic_student)
/// ≠ PROFICIENCY (magic_proficiency + skills.magic) ≠ GOAL (investigate/learn).
/// No spell execution — state management only.
/// </summary>
public class MagicCapabilityTests
{
    private const string Abigail = "Abigail";
    private static readonly string NpcKey = NpcPersistentIdPolicy.Resolve(Abigail);

    private static (AIWorldSaveData Data, NpcStateRepository Repo, MagicAdoptionService Magic) Build()
    {
        var data = new AIWorldSaveData
        {
            SaveSchemaVersion = "1",
            Characters = new Dictionary<string, NpcPersistentData>
            {
                [NpcKey] = new() { NpcId = NpcKey },
            },
        };
        var repo = new NpcStateRepository(data);
        return (data, repo, new MagicAdoptionService(repo));
    }

    [Fact]
    public void M01_ObserveMagic_UnknownToRumor()
    {
        var (_, _, magic) = Build();
        var next = magic.ObserveMagic(NpcKey, "spring-1-1@0600", "obs-1");
        Assert.Equal(CharacterKnowledge.StateRumor, next);
        Assert.Equal(CharacterKnowledge.StateRumor, magic.GetKnowledgeState(NpcKey));
    }

    [Fact]
    public void M02_ObserveMagic_RumorToVerified()
    {
        var (_, _, magic) = Build();
        Assert.Equal(CharacterKnowledge.StateRumor, magic.ObserveMagic(NpcKey, "spring-1-1@0600"));
        var next = magic.ObserveMagic(NpcKey, "spring-1-1@0700");
        Assert.Equal(CharacterKnowledge.StateVerified, next);
        Assert.Equal(CharacterKnowledge.StateVerified, magic.GetKnowledgeState(NpcKey));
    }

    [Fact]
    public void M03_Knowledge_OneWay_NoRegression()
    {
        var (_, _, magic) = Build();
        magic.ObserveMagic(NpcKey, "spring-1-1@0600");
        magic.ObserveMagic(NpcKey, "spring-1-1@0700"); // → VERIFIED
        // Already VERIFIED: further observe returns null (no regression, no change).
        Assert.Null(magic.ObserveMagic(NpcKey, "spring-1-1@0800"));
        Assert.Equal(CharacterKnowledge.StateVerified, magic.GetKnowledgeState(NpcKey));
    }

    [Fact]
    public void M04_Knowledge_NoLearnedState()
    {
        // LEARNED is not a valid CharacterKnowledge state (PM v0.2 removal).
        Assert.DoesNotContain("LEARNED", CharacterKnowledge.ValidStates);
        Assert.False(CharacterKnowledge.IsValidState("LEARNED"));
    }

    [Fact]
    public void M05_AdoptMagic_SetsStudentAndLearnGoal()
    {
        var (_, _, magic) = Build();
        var result = magic.AdoptMagic(NpcKey, "spring-1-1@0600");
        Assert.True(result.Ok);
        Assert.True(magic.GetAdoption(NpcKey));
        Assert.True(magic.HasGoal(NpcKey, MagicAdoptionService.GoalLearn));
    }

    [Fact]
    public void M06_Proficiency_BoundsAndDualPath()
    {
        var (_, repo, magic) = Build();
        Assert.False(magic.SetProficiency(NpcKey, 1.5, "spring-1-1@0600").Ok); // out of [0,1]
        Assert.True(magic.SetProficiency(NpcKey, 0.3, "spring-1-1@0600").Ok);
        Assert.Equal(0.3, magic.GetProficiency(NpcKey), 3);
        // Dual path: state.magic_proficiency + skills.magic both written.
        Assert.NotNull(repo.GetStateValue(NpcKey, MagicAdoptionService.ProficiencyPath));
        Assert.NotNull(repo.GetStateValue(NpcKey, MagicAdoptionService.SkillPath));
    }

    [Fact]
    public void M07_Adoption_IndependentOfKnowledge()
    {
        // Adoption does NOT require knowledge; knowledge does NOT imply adoption (four-way separation).
        var (_, _, magic) = Build();
        Assert.False(magic.GetAdoption(NpcKey)); // no adoption before any call
        Assert.Null(magic.GetKnowledgeState(NpcKey)); // no knowledge recorded yet (absent ≠ UNKNOWN)
        // Adopt without knowledge → adoption true, knowledge still absent.
        magic.AdoptMagic(NpcKey, "spring-1-1@0600");
        Assert.True(magic.GetAdoption(NpcKey));
        Assert.Null(magic.GetKnowledgeState(NpcKey));
    }

    [Fact]
    public void M08_Snapshot_AllFourLayers()
    {
        var (_, _, magic) = Build();
        magic.ObserveMagic(NpcKey, "spring-1-1@0600");
        magic.ObserveMagic(NpcKey, "spring-1-1@0700");
        magic.SetInvestigateGoal(NpcKey, "spring-1-1@0700");
        magic.AdoptMagic(NpcKey, "spring-1-1@0800");
        magic.SetProficiency(NpcKey, 0.2, "spring-1-1@0800");

        var snap = magic.Snapshot(NpcKey);
        Assert.Equal(CharacterKnowledge.StateVerified, snap.Knowledge);
        Assert.True(snap.Adopted);
        Assert.Equal(0.2, snap.Proficiency, 3);
        Assert.True(snap.InvestigateGoal);
        Assert.True(snap.LearnGoal);
    }

    [Fact]
    public void M09_Persistence_Roundtrip_Survives()
    {
        var (data, _, magic) = Build();
        magic.ObserveMagic(NpcKey, "spring-1-1@0600");
        magic.ObserveMagic(NpcKey, "spring-1-1@0700");
        magic.SetInvestigateGoal(NpcKey, "spring-1-1@0700");
        magic.AdoptMagic(NpcKey, "spring-1-1@0800");
        magic.SetProficiency(NpcKey, 0.2, "spring-1-1@0800");

        var json = AIWorldSaveRepository.Serialize(data);
        var restored = AIWorldSaveRepository.Deserialize(json)!;
        var restoredMagic = new MagicAdoptionService(new NpcStateRepository(restored));
        var snap = restoredMagic.Snapshot(NpcKey);

        Assert.Equal(CharacterKnowledge.StateVerified, snap.Knowledge);
        Assert.True(snap.Adopted);
        Assert.Equal(0.2, snap.Proficiency, 3);
        Assert.True(snap.InvestigateGoal);
        Assert.True(snap.LearnGoal);
    }

    [Fact]
    public void M10_StateDeltaWhitelist_MagicPathsValid()
    {
        // The semantic validator whitelist must accept magic adoption/proficiency/goal paths.
        var validator = AIWorld.Core.Pipeline.StructuredResponseSemanticValidator.CreateDemoDefault();
        var response = new AIWorld.Core.Contracts.NpcStructuredResponse
        {
            Speech = "I want to learn magic.",
            Intent = null,
            MemoryCandidates = null,
            StateDelta = new[]
            {
                new AIWorld.Core.Contracts.StateDelta
                {
                    NpcId = Abigail, Path = "state.magic_student", Value = JsonDocument.Parse("true").RootElement.Clone(),
                    Reason = "adopt", SchemaVersion = "1", Confidence = 1f,
                },
                new AIWorld.Core.Contracts.StateDelta
                {
                    NpcId = Abigail, Path = "skills.magic", Value = JsonDocument.Parse("0.2").RootElement.Clone(),
                    Reason = "proficiency", SchemaVersion = "1", Confidence = 1f,
                },
                new AIWorld.Core.Contracts.StateDelta
                {
                    NpcId = Abigail, Path = "goals.investigate_magic", Value = JsonDocument.Parse("true").RootElement.Clone(),
                    Reason = "goal", SchemaVersion = "1", Confidence = 1f,
                },
            },
            SchemaVersion = "1",
        };
        var outcome = validator.Validate(response);
        Assert.True(outcome.Valid, string.Join("; ", outcome.Errors));
    }
}
