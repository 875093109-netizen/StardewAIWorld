using AIWorld.Core.Behaviors;
using AIWorld.Core.Chat;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

public class StoryContextTests
{
    [Theory]
    [InlineData(true, false, true, false, 60, StoryOutcome.Unknown)]
    [InlineData(false, false, true, false, 10, StoryOutcome.Unknown)]
    [InlineData(false, false, true, false, 41, StoryOutcome.Accepted)]
    [InlineData(false, false, true, false, 100, StoryOutcome.Unknown)]
    [InlineData(false, true, false, true, 1, StoryOutcome.Rejected)]
    [InlineData(false, true, false, false, 1, StoryOutcome.Unknown)]
    public void BranchRequiresExecutedVerifiedCommands(bool skip, bool fork, bool main, bool rejection, int command, StoryOutcome result)
        => Assert.Equal(result, PennyBranchObserver.Observe(skip, fork, main, rejection, command, 40, 80));

    [Fact] public void GenericScene_KeepsOnlySpeakersActualLines_NotOtherActorsOrFutureScript()
    {
        var scene = new ObservedScene("sve-test", "Custom_Map", "today", new[] { "Sophia", "Olivia" });
        scene.Observe("Sophia", "今天我在葡萄园工作。", new[] { "Sophia", "Olivia" });
        scene.Observe("Sophia", "今天我在葡萄园工作。", new[] { "Sophia", "Olivia" });
        scene.Observe("Pam", "should not exist", new[] { "Sophia", "Olivia" });
        Assert.Empty(scene.Complete(false));
        var fact = Assert.Single(scene.Complete(true));
        Assert.Equal(new[] { "Sophia" }, fact.Participants);
        Assert.DoesNotContain("should not exist", fact.Facts[0]);
        Assert.False(SceneMembership.Contains("Pam", new[] { "Penny" }));
    }
    [Fact] public void CompletedStoryContextIsPrivateAndOverridesEarlierInvitation()
    {
        var data = Data(); Story(data).Apply(Evidence(sent: true, seen: true, outcome: StoryOutcome.Rejected), "now");
        var assembler = new ChatContextAssembler("persona");
        var penny = assembler.BuildSystemPrompt("Penny", data, new MemoryRepository(data).GetRelevantMemories("Penny", "澡堂", 6));
        var pam = assembler.BuildSystemPrompt("Pam", data, new MemoryRepository(data).GetRelevantMemories("Pam", "澡堂", 6));
        Assert.Contains("拒绝你的感情", penny);
        Assert.DoesNotContain("澡堂", pam);
    }
    [Fact] public void ReadingInvitationMarksDataDirtyWithoutTellingPennyItWasRead()
    {
        var d = Data(); var s = Story(d); s.Apply(Evidence(sent: true), "sent");
        Assert.True(s.Apply(Evidence(sent: true, read: true), "read"));
        var fact = Assert.Single(d.CanonicalExperiences.Where(e => e.Type == "MAIL_READ"));
        Assert.Equal(new[] { "Player" }, fact.Participants);
    }
    [Fact] public void HistoryBudgetKeepsRecentDialogueAndDoesNotExpandChineseEscapes()
    {
        var text = ContextBudget.HistoryBlock(Enumerable.Range(0, 1000).Select(i => ($"第{i}轮" + new string('中', 300), new string('文', 300))));
        Assert.True(text.Length < ContextBudget.History + 100);
        Assert.Contains("第999轮", text);
        Assert.DoesNotContain("第0轮", text);
        Assert.DoesNotContain("\\u4", text);
    }
    private static AIWorldSaveData Data() => new() { SaveSchemaVersion = "1" };
    private static PennyBathStory Story(AIWorldSaveData d) => new(d,
        new CanonicalExperienceService(d, new NpcStateRepository(d), new MemoryRepository(d), "spring-10-1"));
    private static PennyBathEvidence Evidence(bool sent = false, bool seen = false, bool running = false,
        StoryOutcome outcome = StoryOutcome.Unknown, bool old = false, bool compatible = true, bool read = false)
        => new(compatible, sent, false, read, seen, running, outcome, old);

    [Fact] public void UntriggeredStory_DoesNotCreateMemories()
    {
        var d = Data(); Story(d).Apply(Evidence(), "now");
        Assert.Empty(d.CanonicalExperiences);
        Assert.Contains("尚无邀请", PennyBathStory.Context(d, "Penny"));
    }
    [Fact] public void Invitation_DoesNotProveReadOrAttendance_OnlySenderKnows()
    {
        var d = Data(); var story = Story(d); story.Apply(Evidence(sent: true), "now");
        Assert.Equal(StoryStage.Invited, d.Stories[PennyBathStory.Id].Stage);
        Assert.Equal(new[] { "Penny" }, Assert.Single(d.CanonicalExperiences).Participants);
        Assert.False(d.Characters.ContainsKey("Player"));
        Assert.Equal("", PennyBathStory.Context(d, "Pam"));
        Assert.False(story.Apply(Evidence(sent: true), "later"));
    }
    [Theory] [InlineData(StoryOutcome.Accepted)] [InlineData(StoryOutcome.Rejected)]
    public void LiveOutcome_SurvivesReload_AndIsNotBroadcast(StoryOutcome outcome)
    {
        var d = Data(); var story = Story(d);
        story.Apply(Evidence(sent: true, running: true), "start");
        story.Apply(Evidence(seen: true, outcome: outcome), "end");
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<AIWorldSaveData>(System.Text.Json.JsonSerializer.Serialize(d))!;
        Story(reloaded).Apply(Evidence(seen: true, old: true), "load");
        Assert.Equal(outcome, reloaded.Stories[PennyBathStory.Id].Outcome);
        Assert.All(reloaded.CanonicalExperiences, e => Assert.DoesNotContain("Pam", e.Participants));
        Assert.Equal(d.CanonicalExperiences.Count, reloaded.CanonicalExperiences.Count);
    }
    [Fact] public void HistoricalFlag_DoesNotInventChoiceOrDate()
    {
        var d = Data(); Story(d).Apply(Evidence(seen: true, outcome: StoryOutcome.Accepted, old: true), "today");
        Assert.Equal(StoryOutcome.Unknown, d.Stories[PennyBathStory.Id].Outcome);
        Assert.Equal("historical-date-unknown", Assert.Single(d.CanonicalExperiences).OccurredAt);
    }
    [Fact] public void AbortedScene_ReturnsToInvitation_WithoutCompletedMemory()
    {
        var d = Data(); var s = Story(d); s.Apply(Evidence(sent: true, running: true), "start");
        s.Apply(Evidence(), "abort");
        Assert.Equal(StoryStage.Invited, d.Stories[PennyBathStory.Id].Stage);
        Assert.DoesNotContain(d.CanonicalExperiences, e => e.Type == "HEART_EVENT");
    }
    [Fact] public void ReplacedContent_DoesNotAssertVanillaFacts()
    {
        var d = Data(); Story(d).Apply(Evidence(sent: true, seen: true, compatible: false), "now");
        Assert.Empty(d.CanonicalExperiences);
        Assert.Contains("未知", PennyBathStory.Context(d, "Penny"));
    }
    [Fact] public void KnowledgeAndGoals_HaveHardBudgets()
    {
        var d = Data(); d.Characters["Penny"] = new() { NpcId = "Penny" };
        for (int i = 0; i < 5000; i++)
        {
            var id = "fact-" + i;
            d.Characters["Penny"].Knowledge[id] = new() { FactId = id, State = "VERIFIED", UpdatedAt = "today", SchemaVersion = "1" };
            d.Characters["Penny"].Goals[id] = System.Text.Json.JsonSerializer.SerializeToElement(new string('x', 300));
        }
        var prompt = new ChatContextAssembler("persona").BuildSystemPrompt("Penny", d, Array.Empty<MemoryRecord>());
        Assert.True(prompt.Length < 5500, $"Unbounded prompt: {prompt.Length}");
    }
    [Fact] public void EmbeddedQuotes_DoNotLoseTheRestOfPlayerMessage()
    {
        Assert.Equal("你说\"晚上\"去澡堂吗？", ChatContextAssembler.ExtractPlayerMessage(
            "PLAYER'S MESSAGE: \"你说\"晚上\"去澡堂吗？\"\nRESPONSE GUIDANCE:\n- Respond naturally as Penny based on your personality"));
    }
    [Fact] public void CanonicalLedger_RemainsRetrievableWhenMemoryStoreIsFull()
    {
        var d = Data(); var repo = new MemoryRepository(d);
        for (int i = 0; i < 100; i++) repo.Append("Penny", new() { MemoryId = "m"+i, NpcId = "Penny", Summary = "filler", Layer = MemoryLayer.Episodic, EmotionalTone = "neutral", Importance = 0.1f, CreatedAt = "old", PersistedAt = "old" });
        Story(d).Apply(Evidence(sent: true, seen: true, outcome: StoryOutcome.Rejected), "now");
        Assert.Contains(repo.GetRelevantMemories("Penny", "澡堂", 6), m => m.Summary.Contains("没有接受"));
        Assert.Empty(repo.GetRelevantMemories("Pam", "澡堂", 6));
    }
    [Fact] public void Lifecycle_RejectsInvalidTransitionsAndDifferentSave()
    {
        var r = new ChatRequestLifecycle();
        Assert.Throws<InvalidOperationException>(() => r.MoveTo(ChatRequestPhase.Completed));
        r.MoveTo(ChatRequestPhase.Preparing); r.MoveTo(ChatRequestPhase.Generating); r.MoveTo(ChatRequestPhase.Validating); r.MoveTo(ChatRequestPhase.Expired);
        Assert.Throws<InvalidOperationException>(() => r.MoveTo(ChatRequestPhase.Completed));
        var data = Data(); var now = DateTimeOffset.UtcNow;
        Assert.True(ChatRequestLifecycle.IsCurrent(data, data, "state", "state", now, now));
        Assert.False(ChatRequestLifecycle.IsCurrent(data, Data(), "state", "state", now, now));
        Assert.False(ChatRequestLifecycle.IsCurrent(data, data, "old", "new", now, now));
        Assert.False(ChatRequestLifecycle.IsCurrent(data, data, "state", "state", now.AddMinutes(-2), now));
    }
}
