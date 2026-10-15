using AIWorld.Core.Behaviors;
using AIWorld.Core.Chat;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>PM FIX 2 (2026-08-12): deterministic NPC SelfState injected into LLM context +
/// BehaviorOwner contract (CANONICAL / PLAYER_REQUEST / AI_AUTONOMOUS_GOAL / EVENT) +
/// CurrentActivity.FOLLOW_PLAYER semantics. All offline/deterministic.</summary>
public class SelfStatePromptTests
{
    private static AIWorldSaveData NewData() => new() { SaveSchemaVersion = "1" };

    // --- BehaviorOwner contract (PM FIX 2) ---

    [Fact]
    public void BehaviorOwnerContract_HasAllFourOwners()
    {
        Assert.Equal("CANONICAL", BaselineResolver.OwnerCanonical);
        Assert.Equal("PLAYER_REQUEST", BaselineResolver.OwnerPlayerRequest);
        Assert.Equal("AI_AUTONOMOUS_GOAL", BaselineResolver.OwnerAutonomousGoal);
        Assert.Equal("EVENT", BaselineResolver.OwnerEvent);
    }

    [Fact]
    public void CurrentActivity_HasFollowPlayer()
    {
        Assert.True(Enum.IsDefined(typeof(CurrentActivity), "FOLLOW_PLAYER"));
    }

    // --- SelfState semantics (following → PLAYER_REQUEST owner + FOLLOW_PLAYER activity) ---

    [Fact]
    public void SelfState_Following_HasPlayerRequestOwnerAndFollowActivity()
    {
        var s = new SelfState
        {
            CharacterId = "Abigail",
            CurrentLocation = "Mine", // npc.currentLocation — NOT the player's location
            CurrentTime = "spring-10-1@1000",
            CurrentActivity = CurrentActivity.FOLLOW_PLAYER,
            BehaviorOwner = BaselineResolver.OwnerPlayerRequest,
            CurrentTarget = "Player",
            FollowingPlayer = true,
        };

        Assert.Equal("PLAYER_REQUEST", s.BehaviorOwner);
        Assert.Equal(CurrentActivity.FOLLOW_PLAYER, s.CurrentActivity);
        Assert.Equal("Player", s.CurrentTarget);
        Assert.True(s.FollowingPlayer);
        Assert.Contains("activity=FOLLOW_PLAYER", s.Describe);
        Assert.Contains("owner=PLAYER_REQUEST", s.Describe);
        Assert.Contains("following=True", s.Describe);
    }

    [Fact]
    public void SelfState_Canonical_HasCanonicalOwnerAndNoFollow()
    {
        var s = new SelfState
        {
            CharacterId = "Abigail",
            CurrentLocation = "SeedShop",
            CurrentTime = "spring-10-1@1000",
            CurrentActivity = CurrentActivity.CANONICAL,
            BehaviorOwner = BaselineResolver.OwnerCanonical,
            FollowingPlayer = false,
        };

        Assert.Equal("CANONICAL", s.BehaviorOwner);
        Assert.Equal(CurrentActivity.CANONICAL, s.CurrentActivity);
        Assert.False(s.FollowingPlayer);
    }

    [Fact]
    public void SelfState_Event_HasEventOwner()
    {
        var s = new SelfState
        {
            CharacterId = "Abigail",
            CurrentLocation = "SeedShop",
            CurrentTime = "spring-10-1@1000",
            CurrentActivity = CurrentActivity.EVENT,
            BehaviorOwner = BaselineResolver.OwnerEvent,
            CurrentEvent = "1",
            FollowingPlayer = false,
        };

        Assert.Equal("EVENT", s.BehaviorOwner);
        Assert.Equal("1", s.CurrentEvent);
        Assert.Contains("event=1", s.Describe);
    }

    // --- ChatContextAssembler: SelfState injected, player context separate ---

    [Fact]
    public void BuildSystemPrompt_InjectsSelfState_AsNpcWorldState()
    {
        var data = NewData();
        var assembler = new ChatContextAssembler("Persona: test", worldContextProvider: "playerLocation=Farm season=spring");
        var selfState = new SelfState
        {
            CharacterId = "Abigail",
            CurrentLocation = "SeedShop", // npc.currentLocation (NOT player's Farm)
            CurrentTime = "spring-10-1@1000",
            CurrentActivity = CurrentActivity.CANONICAL,
            BehaviorOwner = "CANONICAL",
            FollowingPlayer = false,
        };

        var prompt = assembler.BuildSystemPrompt("Abigail", data, new List<MemoryRecord>(), selfState);

        // NPC state present with its OWN location (SeedShop), never conflated with player.
        Assert.Contains("CURRENT WORLD STATE", prompt);
        Assert.Contains("Abigail at SeedShop", prompt);
        Assert.Contains("PLAYER WORLD CONTEXT", prompt);
        Assert.Contains("playerLocation=Farm", prompt);
        // The NPC location must be the NPC's own — the player's Farm must not appear as NPC state.
        var npcStateSection = prompt.Substring(prompt.IndexOf("CURRENT WORLD STATE", StringComparison.Ordinal),
            prompt.IndexOf("PLAYER WORLD CONTEXT", StringComparison.Ordinal) - prompt.IndexOf("CURRENT WORLD STATE", StringComparison.Ordinal));
        Assert.Contains("SeedShop", npcStateSection);
        Assert.DoesNotContain("playerLocation", npcStateSection);
    }

    [Fact]
    public void BuildSystemPrompt_NoSelfState_FallsBackToPlayerContextOnly()
    {
        var data = NewData();
        var assembler = new ChatContextAssembler("Persona: test", worldContextProvider: "playerLocation=Town");
        var prompt = assembler.BuildSystemPrompt("Abigail", data, new List<MemoryRecord>(), selfState: null);

        Assert.DoesNotContain("CURRENT WORLD STATE", prompt);
        Assert.Contains("PLAYER WORLD CONTEXT", prompt);
        Assert.Contains("playerLocation=Town", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_SelfStateFollow_DescribesFollowingSemantics()
    {
        var data = NewData();
        var assembler = new ChatContextAssembler("Persona: test", worldContextProvider: "playerLocation=Mine");
        var selfState = new SelfState
        {
            CharacterId = "Abigail",
            CurrentLocation = "Mine",
            CurrentTime = "spring-10-1@1000",
            CurrentActivity = CurrentActivity.FOLLOW_PLAYER,
            BehaviorOwner = "PLAYER_REQUEST",
            CurrentTarget = "Player",
            FollowingPlayer = true,
        };

        var prompt = assembler.BuildSystemPrompt("Abigail", data, new List<MemoryRecord>(), selfState);

        Assert.Contains("CURRENT WORLD STATE", prompt);
        Assert.Contains("activity=FOLLOW_PLAYER", prompt);
        Assert.Contains("owner=PLAYER_REQUEST", prompt);
        Assert.Contains("following=True", prompt);
        Assert.Contains("target=Player", prompt);
    }
}
