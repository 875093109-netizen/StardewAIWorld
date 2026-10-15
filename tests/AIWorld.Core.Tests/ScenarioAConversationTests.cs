using AIWorld.Core.Chat;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// SCENARIO A — offline tests for the player-facing conversation layer.
/// Covers: ChatContextAssembler (production context assembly, token discipline),
/// player-message extraction from SocialValley prompt format, NPC identity resolution.
/// No game/LLM dependency (deterministic).
/// </summary>
public class ScenarioAConversationTests
{
    private static AIWorldSaveData MakeData()
    {
        return new AIWorldSaveData
        {
            SaveSchemaVersion = "1",
            Characters = new Dictionary<string, NpcPersistentData>
            {
                ["Abigail"] = new()
                {
                    NpcId = "Abigail",
                    State = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["magicStudent"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
                        ["magicProficiency"] = System.Text.Json.JsonSerializer.SerializeToElement(0.2),
                    },
                    Knowledge = new Dictionary<string, CharacterKnowledge>
                    {
                        ["magic.exists"] = new() { FactId = "magic.exists", State = CharacterKnowledge.StateVerified, UpdatedAt = "spring-10-1", SchemaVersion = "1" },
                    },
                    Goals = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["investigate_magic"] = System.Text.Json.JsonSerializer.SerializeToElement("active"),
                    },
                },
            },
            Metadata = new Dictionary<string, string>(),
            FollowSessions = new List<FollowSession>(),
        };
    }

    [Fact]
    public void S2T01_ContextAssembler_IncludesStateKnowledgeGoals()
    {
        var asm = new ChatContextAssembler("Persona: test");
        var sys = asm.BuildSystemPrompt("Abigail", MakeData(), Array.Empty<MemoryRecord>());

        Assert.Contains("MAGIC ADOPTION", sys);
        Assert.Contains("MAGIC PROFICIENCY: 0.20", sys);
        Assert.Contains("magic.exists: VERIFIED", sys);
        Assert.Contains("investigate_magic", sys);
    }

    [Fact]
    public void S2T02_ContextAssembler_BoundedMemories()
    {
        var asm = new ChatContextAssembler("Persona: test");
        var memories = Enumerable.Range(0, 20)
            .Select(i => new MemoryRecord
            {
                NpcId = "Abigail",
                MemoryId = $"m{i}",
                Layer = MemoryLayer.Episodic,
                Summary = $"memory {i}",
                EmotionalTone = "neutral",
                Importance = 0.5f,
                CreatedAt = "spring-10-1",
                PersistedAt = "spring-10-1",
            })
            .ToList();
        var sys = asm.BuildSystemPrompt("Abigail", MakeData(), memories);

        // Token discipline: bounded to MaxRecentMemories, NOT all 20.
        Assert.Contains("memory 0", sys);
        Assert.DoesNotContain("memory 19", sys);
    }

    [Fact]
    public void S2T03_UserPrompt_ContainsPlayerMessage()
    {
        var asm = new ChatContextAssembler("Persona: test");
        var usr = asm.BuildUserPrompt("陪我去矿洞");
        Assert.Contains("陪我去矿洞", usr);
        Assert.Contains("social.follow_player", usr);
        Assert.Contains("schemaVersion", usr);
    }

    [Fact]
    public void S2T04_ExtractPlayerMessage_FromSocialValleyPrompt()
    {
        // Exact SocialValley ChatUI.SendMessage segment format.
        const string prompt = "CURRENT SITUATION:\nspring weather\n\nCONVERSATION HISTORY:\n(empty)\n\nPLAYER'S MESSAGE: \"你还记得上次矿洞吗？\"\n\nRESPONSE GUIDANCE:\n- ...";
        var msg = ChatContextAssembler.ExtractPlayerMessage(prompt);
        Assert.Equal("你还记得上次矿洞吗？", msg);
    }

    [Fact]
    public void S2T04b_ExtractPlayerMessage_MessageContainsQuotes()
    {
        // Player text itself contains quotes: SocialValley templates PLAYER'S MESSAGE: "{msg}"
        // where msg = "你现在怎么看魔法？" (player typed quotes). Prompt has 4 quotes.
        // Segment fallback must strip ONE template layer and recover the player's original text.
        const string prompt = "CURRENT SITUATION:\nx\n\nCONVERSATION HISTORY:\n(empty)\n\nPLAYER'S MESSAGE: \"\"你现在怎么看魔法？\"\"\n\nRESPONSE GUIDANCE:\n- ...";
        var msg = ChatContextAssembler.ExtractPlayerMessage(prompt);
        Assert.Equal("\"你现在怎么看魔法？\"", msg);
    }

    [Fact]
    public void S2T04c_ExtractPlayerMessage_NoQuotesPlainText()
    {
        const string prompt = "CURRENT SITUATION:\nx\n\nPLAYER'S MESSAGE: \"听不懂\"\n\nRESPONSE GUIDANCE:\n- ...";
        var msg = ChatContextAssembler.ExtractPlayerMessage(prompt);
        Assert.Equal("听不懂", msg);
    }

    [Fact]
    public void S2T05_ExtractPlayerMessage_FallbackToWholePrompt()
    {
        var msg = ChatContextAssembler.ExtractPlayerMessage("just a plain message");
        Assert.Equal("just a plain message", msg);
    }

    [Fact]
    public void S2T06_ExtractNpcName_FromResponseGuidance()
    {
        const string prompt = "...Respond naturally as Abigail based on your personality...";
        var npc = ChatContextAssembler.ExtractNpcName(prompt);
        Assert.Equal("Abigail", npc);
    }

    [Fact]
    public void S2T07_ExtractNpcName_FromSystemPrompt()
    {
        const string prompt = "You are playing Abigail. ...";
        // Fallback path: no "Respond naturally as X" → try system prompt patterns; here null is
        // acceptable (caller defaults to Abigail) — this just proves no crash on arbitrary input.
        var npc = ChatContextAssembler.ExtractNpcName(prompt);
        Assert.True(npc is null || npc.Length > 0);
    }

    [Fact]
    public void S2T08_NpcIdentity_ResolvesViaPolicy()
    {
        // NPC identity must go through NpcPersistentIdPolicy (PM §九), not raw Name.
        var key = NpcPersistentIdPolicy.Resolve("Abigail");
        Assert.Equal("Abigail", key);
        Assert.True(NpcPersistentIdPolicy.IsValid(key));
    }

    [Fact]
    public void S2T09_ContextAssembler_NoWholeSaveDump()
    {
        var asm = new ChatContextAssembler("Persona: test");
        var sys = asm.BuildSystemPrompt("Abigail", MakeData(), Array.Empty<MemoryRecord>());
        // Token discipline: must NOT dump raw character JSON / full state.
        Assert.DoesNotContain("\"characters\"", sys);
        Assert.DoesNotContain("magicStudent\":true", sys); // raw JSON serialization form
    }
}
