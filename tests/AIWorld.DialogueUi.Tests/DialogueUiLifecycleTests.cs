using AIWorld.Core.Chat;
using AIWorld.Demo;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
public class DialogueUiLifecycleTests
{
    [Fact] public void RewriteCacheIsSeparatedByGameLanguage()
    {
        string previous = UiText.Language;
        try
        {
            UiText.SetLanguage("en"); using var f = new Fixture(); f.Bridge.Tick();
            Assert.Equal(1, f.Server.Calls);
            Game1.activeClickableMenu = new DialogueBox(f.Box.characterDialogue); f.Bridge.Tick();
            Assert.Equal(1, f.Server.Calls); // same language, source and state reuse the cache
            UiText.SetLanguage("ja");
            Game1.activeClickableMenu = new DialogueBox(f.Box.characterDialogue); f.Bridge.Tick();
            Assert.Equal(2, f.Server.Calls);
        }
        finally { UiText.SetLanguage(previous); }
    }
    [Fact] public void LanguageChangeRejectsDeferredRewriteBeforePresentation()
    {
        string previous = UiText.Language;
        try
        {
            UiText.SetLanguage("en"); using var f = new Fixture(); f.Server.Deferred = new(); f.Bridge.Tick();
            UiText.SetLanguage("es"); f.Server.Deferred.SetResult(new("late", "old-language", true)); f.Bridge.Tick();
            Assert.Equal(0, f.Server.Commits); Assert.Contains("新来的农夫", f.Box.getCurrentString());
        }
        finally { UiText.SetLanguage(previous); }
    }
    private sealed class Host : IModHelper, IMonitor { public Input Input {get;}=new();public void Log(string m,LogLevel l){} }
    private sealed class Fixture : IDisposable
    {
        public AiWorldChatServer Server=new(); public NPC Npc=new();public DialogueBox Box; public DialogueUiBridge Bridge;
        public Fixture(bool rewrite=true)
        {
            Game1.activeClickableMenu=null;Game1.eventUp=false;Game1.CurrentEvent=null;Game1.afterDialogues=null;Game1.keyboardDispatcher.Subscriber=null;
            SocialValley.ModEntry.ConversationManager=new();SocialValley.ChatUI.Welcomes=0;SocialValley.ChatUI.RestorationFails=false;
            var d=new Dialogue(Npc,"Characters/Dialogue/Abigail:Introduction","你好，新来的农夫。");Npc.CurrentDialogue.Push(d);
            Box=new(d);Game1.activeClickableMenu=Box;
            var host=new Host();Bridge=new(host,host,Server,new ModConfig{RewriteDailyDialogue=rewrite},_=>"stable");
        }
        public void Switch()=>Bridge.ButtonPressed(new ButtonPressedEventArgs{Button=SButton.F8});
        public void Dispose()=>Bridge.Dispose();
    }
    [Fact] public void RoundTripSharesOriginalAndReplyWithoutSecondWelcomeOrDuplicateTranscript()
    {
        using var f=new Fixture(false);f.Bridge.Tick();f.Switch();
        var chat=Assert.IsType<SocialValley.ChatUI>(Game1.activeClickableMenu);
        Assert.Equal(0,SocialValley.ChatUI.Welcomes);Assert.Same(chat,Game1.keyboardDispatcher.Subscriber);
        Assert.Contains("新来的农夫",Assert.Single(f.Server.Conversations.Get("Abigail")).Text);
        f.Bridge.Tick();chat.AddReply("接着刚才的话题聊。");f.Server.Conversations.AddTurn("Abigail","继续聊","接着刚才的话题聊。");
        f.Switch();Assert.Same(f.Box,Game1.activeClickableMenu);Assert.Null(Game1.keyboardDispatcher.Subscriber);
        Assert.Equal("接着刚才的话题聊。",f.Box.getCurrentString());Assert.Equal(0,f.Box.characterDialogue.currentDialogueIndex);
        f.Box.finishTyping();f.Bridge.Tick();f.Switch();Assert.Same(chat,Game1.activeClickableMenu);
        Assert.Equal(3,f.Server.Conversations.Get("Abigail").Count);Assert.Equal(0,f.Server.Calls);
    }
    [Fact] public void RewritePreservesUnderlyingDialogueAndRunsOnlyOnceForSameBox()
    {
        using var f=new Fixture();var original=f.Box.characterDialogue;f.Bridge.Tick();
        Assert.Equal(1,f.Server.Calls);Assert.Equal(1,f.Server.Commits);Assert.Same(original,f.Box.characterDialogue);
        Assert.Contains("新来的农夫",original.dialogues[0].Text);Assert.True(f.Box.Reflows>0);
        f.Switch();f.Bridge.Tick();f.Switch();f.Bridge.Tick();Assert.Equal(1,f.Server.Calls);
    }
    [Fact] public void ClosedWindowDiscardsLateRewriteWithoutActionCommit()
    {
        using var f=new Fixture();f.Server.Deferred=new();f.Bridge.Tick();Game1.activeClickableMenu=null;f.Bridge.Tick();
        f.Server.Deferred.SetResult(new("late","迟到的台词。",true));f.Bridge.Tick();Assert.Equal(0,f.Server.Commits);
        Assert.Contains("新来的农夫",f.Box.getCurrentString());
    }
    [Fact] public void EventAndQuestionNeverRewriteOrSwitch()
    {
        using var f=new Fixture();Game1.eventUp=true;f.Bridge.Tick();f.Switch();Assert.Same(f.Box,Game1.activeClickableMenu);Assert.Equal(0,f.Server.Calls);
        Game1.eventUp=false;f.Box.isQuestion=true;f.Switch();Assert.Same(f.Box,Game1.activeClickableMenu);
    }
    [Fact] public void ClosingChatFinishesSuspendedGreetingThroughNativeClose()
    {
        using var f=new Fixture(false);f.Bridge.Tick();f.Switch();f.Bridge.Tick();
        Game1.activeClickableMenu=null;f.Bridge.Tick();Assert.Empty(f.Npc.CurrentDialogue);
        Assert.True(Game1.player.CanMove);Assert.Equal(1,f.Box.characterDialogue.currentDialogueIndex);
    }
    [Fact] public void BusyChatDoesNotSwitchAndLateReplyIsNotDuplicated()
    {
        using var f=new Fixture(false);f.Bridge.Tick();f.Switch();f.Bridge.Tick();
        var chat=Assert.IsType<SocialValley.ChatUI>(Game1.activeClickableMenu);chat.SetBusy(true);f.Switch();Assert.Same(chat,Game1.activeClickableMenu);
    }
    [Fact] public void DirectChatToNativeDoesNotPopPreexistingNpcDialogue()
    {
        using var f=new Fixture(false);var original=f.Npc.CurrentDialogue.Peek();
        var chat=new SocialValley.ChatUI(new Host(),new SocialValley.UnifiedAIClient(new Host(),new object()),f.Npc);
        chat.AddReply("普通回复。$action malicious");Game1.activeClickableMenu=chat;f.Bridge.Tick();f.Switch();
        var native=Assert.IsType<DialogueBox>(Game1.activeClickableMenu);
        Assert.DoesNotContain("$",native.getCurrentString());Assert.Equal(2,f.Npc.CurrentDialogue.Count);
        Game1.activeClickableMenu=null;f.Bridge.Tick();Assert.Same(original,Assert.Single(f.Npc.CurrentDialogue));
    }
    [Fact] public void MultiPartGreetingCanContinueAfterLastNativePart()
    {
        using var f=new Fixture(false);f.Box.characterDialogue.dialogues.Add(new(){Text="最后再介绍一下这里。"});
        f.Bridge.Tick();f.Switch();Assert.Same(f.Box,Game1.activeClickableMenu);
        f.Box.characterDialogue.currentDialogueIndex=1;f.Box.characterDialoguesBrokenUp.Clear();f.Box.characterDialoguesBrokenUp.Push("最后再介绍一下这里。");
        f.Switch();Assert.IsType<SocialValley.ChatUI>(Game1.activeClickableMenu);
        Assert.Equal(0,SocialValley.ChatUI.Welcomes);
    }
    [Fact] public void FailedUiSwitchRestoresOriginalWindowAndKeyboard()
    {
        using var f=new Fixture(false);f.Bridge.Tick();SocialValley.ChatUI.RestorationFails=true;f.Switch();
        Assert.Same(f.Box,Game1.activeClickableMenu);Assert.Null(Game1.keyboardDispatcher.Subscriber);
        Assert.False(SocialValley.ModEntry.IsChatOpen);Assert.Equal(0,f.Box.characterDialogue.currentDialogueIndex);
    }
}
