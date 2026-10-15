// Headless contract doubles for the real UI coordinator, NOT an assertion about rendering or actual game behavior.
// Member names/signatures used by the reflection adapter were checked against the installed DLLs.
#pragma warning disable CS0414, CS0649
using AIWorld.Core.Chat;
using StardewValley;
using StardewValley.Menus;
using StardewModdingAPI;

namespace Microsoft.Xna.Framework
{
    public struct Point { public int X, Y; }
    public struct Vector2 { public Vector2(float x, float y) { X=x; Y=y; } public float X,Y; public Point ToPoint()=>new(){X=(int)X,Y=(int)Y}; }
    public struct Rectangle { public Rectangle(int x,int y,int w,int h){X=x;Y=y;Width=w;Height=h;} public int X,Y,Width,Height; public bool Contains(Point p)=>p.X>=X&&p.X<X+Width&&p.Y>=Y&&p.Y<Y+Height; }
    public struct Color { public static Color White=>new(); }
}
namespace Microsoft.Xna.Framework.Graphics
{
    public class SpriteBatch {}
    public class SpriteFont { public Microsoft.Xna.Framework.Vector2 MeasureString(string text) => new(text.Length * 12, 24); }
}
namespace StardewModdingAPI
{
    public enum SButton { F8, F9, MouseLeft }
    public enum LogLevel { Debug, Warn }
    public interface IMonitor { void Log(string message, LogLevel level); }
    public interface IModHelper { Input Input { get; } }
    public class Input { public void Suppress(SButton b) {} public Cursor GetCursorPosition()=>new(); }
    public class Cursor { public Microsoft.Xna.Framework.Vector2 GetScaledScreenPixels()=>new(-100,-100); }
    public static class Context { public static bool IsWorldReady=true; }
}
namespace StardewModdingAPI.Events { public class ButtonPressedEventArgs : EventArgs { public SButton Button { get; set; } } }
namespace StardewValley
{
    public class NPC { public string Name="Abigail"; public Stack<Dialogue> CurrentDialogue=new(); }
    public class DialogueLine { public string Text=""; public Action? SideEffects; }
    public class Dialogue
    {
        public Dialogue(NPC npc,string key,string text){speaker=npc;TranslationKey=key;dialogues.Add(new(){Text=text});}
        public NPC speaker; public string TranslationKey; public List<DialogueLine> dialogues=new();
        public int currentDialogueIndex; public Action? onFinish,answerQuestionBehavior;
        public void exitCurrentDialogue(){currentDialogueIndex++;onFinish?.Invoke();}
    }
    public class Farmer { public bool CanMove=true; }
    public class Dispatcher { public object? Subscriber; }
    public class Viewport { public int Width=1280; }
    public class HUDMessage { public HUDMessage(string text) {} }
    public static class Game1
    {
        public static IClickableMenu? activeClickableMenu; public static bool eventUp,dialogueUp;
        public static object? CurrentEvent; public static Action? afterDialogues;
        public static Farmer player=new(); public static Dispatcher keyboardDispatcher=new();
        public static Viewport uiViewport=new(); public static Microsoft.Xna.Framework.Graphics.SpriteFont smallFont=new();
        public static Microsoft.Xna.Framework.Color textColor=new();
        public static void addHUDMessage(HUDMessage h) {}
    }
    public static class Utility { public static void drawTextWithShadow(Microsoft.Xna.Framework.Graphics.SpriteBatch b,string t,object f,Microsoft.Xna.Framework.Vector2 p,Microsoft.Xna.Framework.Color c) {} }
}
namespace StardewValley.Menus
{
    public class IClickableMenu
    {
        public static void drawTextureBox(Microsoft.Xna.Framework.Graphics.SpriteBatch b,int x,int y,int w,int h,Microsoft.Xna.Framework.Color c) {}
    }
    public class DialogueBox : IClickableMenu
    {
        public DialogueBox(Dialogue d){characterDialogue=d;characterDialoguesBrokenUp.Push(d.dialogues[0].Text);}
        public Dialogue characterDialogue; public bool isQuestion,dialogueFinished;
        public Stack<string> characterDialoguesBrokenUp=new(); public int characterIndexInDialogue, y=450;
        public string getCurrentString()=>characterDialoguesBrokenUp.TryPeek(out var s)?s:"";
        public void finishTyping()=>characterIndexInDialogue=getCurrentString().Length;
        public int Reflows;
        private void checkDialogue(Dialogue d){Reflows++;}
        private void setUpIcons(){}
        public void closeDialogue(){if(dialogueFinished&&characterDialogue.speaker.CurrentDialogue.Count>0)characterDialogue.speaker.CurrentDialogue.Pop();Game1.activeClickableMenu=null;Game1.dialogueUp=false;Game1.player.CanMove=true;}
    }
}
namespace AIWorld.Demo
{
    public class AiWorldChatServer
    {
        public ConversationSessions Conversations {get;}=new();
        public int Calls,Commits; public TaskCompletionSource<DialogueRewriteCandidate?>? Deferred;
        public void ObserveDialogue(string npc,string text,string id,string source="displayed-dialogue")=>Conversations.Observe(npc,text,id,source);
        public Task<DialogueRewriteCandidate?> RewriteDialogueAsync(string npc,string text,CancellationToken token)
        { Calls++; return Deferred?.Task ?? Task.FromResult<DialogueRewriteCandidate?>(new(Guid.NewGuid().ToString(),"今天一起聊聊吧。")); }
        public bool AcceptDialogueRewrite(DialogueRewriteCandidate c,Action<string>? present=null){present?.Invoke(c.Speech);Commits++;return true;}
    }
}
namespace SocialValley
{
    public class Entry { public string Message {get;set;}=""; public bool IsFromPlayer {get;set;} }
    public class ConversationManager
    {
        private Dictionary<string,List<Entry>> entries=new();
        public List<Entry> GetConversationHistory(NPC npc)=>entries.TryGetValue(npc.Name,out var e)?e.ToList():new();
        public void AddMessage(NPC npc,string text,bool player){if(!entries.ContainsKey(npc.Name))entries[npc.Name]=new();entries[npc.Name].Add(new(){Message=text,IsFromPlayer=player});}
    }
    public static class ModEntry
    {
        public static object ConfigManager {get;}=new();
        public static ConversationManager ConversationManager {get;set;}=new();
        public static bool IsChatOpen {get;set;}
    }
    public class UnifiedAIClient { public UnifiedAIClient(IMonitor monitor,object config){} }
    public class ChatMessage { public string Text {get;set;}="";public bool IsFromPlayer {get;set;} public bool IsTypingIndicator {get;set;} }
    public class ChatUI : IClickableMenu
    {
        private NPC npc; private bool isWaitingForResponse=false,isGeneratingWelcome=false;
        private List<ChatMessage> chatHistory=new(); public static int Welcomes; public static bool RestorationFails;
        public ChatUI(IMonitor monitor,UnifiedAIClient client,NPC speaker)
        {
            npc=speaker; foreach(var e in ModEntry.ConversationManager.GetConversationHistory(npc))chatHistory.Add(new(){Text=e.Message,IsFromPlayer=e.IsFromPlayer});
            if(chatHistory.Count==0)Welcomes++;
        }
        public void RestoreFromModal(){if(RestorationFails)throw new InvalidOperationException("UI unavailable");Game1.activeClickableMenu=this;Game1.keyboardDispatcher.Subscriber=this;ModEntry.IsChatOpen=true;}
        public void AddReply(string text)=>chatHistory.Add(new(){Text=text});
        public void SetBusy(bool value)=>isWaitingForResponse=value;
    }
}
