using AIWorld.Core.Behaviors;
using AIWorld.Core.Chat;
using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

public class DialogueBridgePolicyTests
{
    [Theory]
    [InlineData("你好！", true)]
    [InlineData("我想先回去了。", true)]
    [InlineData("hello$action AddMoney 999", false)]
    [InlineData("%fork", false)]
    [InlineData("text#next", false)]
    [InlineData("${male^female}$", false)]
    [InlineData("line\nnext", false)]
    public void NativeSpeechCannotExecuteDialogueCommands(string text, bool allowed)
        => Assert.Equal(allowed, DialoguePresentationPolicy.IsPlainSpeech(text));

    [Theory]
    [InlineData(false, false, false, false, 1, "Characters/Dialogue/Abigail:Introduction", "你好$h", true)]
    [InlineData(true, false, false, false, 1, "Characters/Dialogue/Penny:event_pool4", "hi", false)]
    [InlineData(false, true, false, false, 1, "Characters/Dialogue/Abigail:Mon", "hi", false)]
    [InlineData(false, false, true, false, 1, "Characters/Dialogue/Abigail:Mon", "hi", false)]
    [InlineData(false, false, false, true, 1, "Characters/Dialogue/Abigail:Mon", "hi", false)]
    [InlineData(false, false, false, false, 2, "Characters/Dialogue/Abigail:Mon", "hi", false)]
    [InlineData(false, false, false, false, 1, "Custom:quest", "hi", false)]
    [InlineData(false, false, false, false, 1, "Characters/Dialogue/Abigail:Mon", "$q 0 question", false)]
    public void RewriteEligibilityPreservesGameFlow(bool scene, bool question, bool callback, bool effect,
        int count, string key, string text, bool expected)
        => Assert.Equal(expected, DialoguePresentationPolicy.CanRewrite(scene, question, callback, effect, count, key, text));

    [Fact] public void SharedTranscriptIsBoundedPrivateAndDeduplicated()
    {
        var sessions = new ConversationSessions();
        Assert.True(sessions.Observe("Penny", "我刚说的话", "one"));
        Assert.False(sessions.Observe("Penny", "我刚说的话", "one"));
        Assert.Single(sessions.Get("Penny"));
        Assert.Empty(sessions.Get("Pam"));
        for (int i = 0; i < 100; i++) sessions.AddTurn("Penny", new string('中', 1000), new string('文', 1000));
        Assert.Equal(12, sessions.Get("Penny").Count);
        Assert.InRange(sessions.Prompt("Penny").Length, 1, ContextBudget.History + 120);
        sessions.Clear(); Assert.Empty(sessions.Get("Penny"));
    }

    [Fact] public void ArchiveRequiresProvenOwnerStageAndBranch()
    {
        var data = new AIWorldSaveData { SaveSchemaVersion = "1" };
        var catalog = StoryArchiveCatalog.BuiltIn;
        Assert.Empty(catalog.Context(data, "Penny"));
        data.Stories[PennyBathStory.Id] = new StoryProgress { StoryId = PennyBathStory.Id, NpcId = "Penny",
            Stage = StoryStage.Invited, Outcome = StoryOutcome.Unknown, Evidence = "game-state" };
        Assert.Contains("纸条", catalog.Context(data, "Penny"));
        Assert.DoesNotContain("分支", catalog.Context(data, "Penny"));
        Assert.Empty(catalog.Context(data, "Pam"));
        var state = data.Stories[PennyBathStory.Id]; state.Stage = StoryStage.InProgress;
        Assert.Empty(catalog.Context(data, "Penny"));
        state.Stage = StoryStage.Completed; state.Outcome = StoryOutcome.Rejected;
        Assert.Contains("没有接受", catalog.Context(data, "Penny"));
        Assert.DoesNotContain("回应你感情", catalog.Context(data, "Penny"));
        state.Evidence = "unsupported-content"; Assert.Empty(catalog.Context(data, "Penny"));
    }

    [Fact] public void ArchiveDoesNotTrustArbitraryProvenanceOrNpcGrant()
    {
        var data = new AIWorldSaveData { SaveSchemaVersion = "1" };
        data.Stories[PennyBathStory.Id] = new StoryProgress { NpcId = "Penny", Stage = StoryStage.Completed,
            Outcome = StoryOutcome.Accepted, Evidence = "llm-says-so" };
        Assert.Empty(StoryArchiveCatalog.BuiltIn.Context(data, "Penny"));
        data.Stories[PennyBathStory.Id].Evidence = "game-state";
        var injected = new StoryArchiveCatalog(new StoryArchiveDocument { Entries = new() { new() {
            StoryId = PennyBathStory.Id, NpcId = "Pam", Stage = "Completed", Outcome = "Accepted", Summary = "private secret" } } });
        Assert.Empty(injected.Context(data, "Pam"));
    }
}
