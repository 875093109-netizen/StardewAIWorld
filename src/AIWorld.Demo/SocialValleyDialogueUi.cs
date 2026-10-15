using System.Collections;
using System.Reflection;
using AIWorld.Core.Chat;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace AIWorld.Demo;

/// <summary>Small version-checked UI adapter. No patches or writes to upstream binaries.</summary>
internal sealed class SocialValleyDialogueUi
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly IMonitor _monitor;
    private object? _client;
    public SocialValleyDialogueUi(IMonitor monitor) => _monitor = monitor;
    private static Type? TypeOf(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType("SocialValley." + name)).FirstOrDefault(t => t is not null);
    public static bool IsChat(IClickableMenu? menu) => menu?.GetType().FullName == "SocialValley.ChatUI";
    private static object? Field(object obj, string name) => obj.GetType().GetField(name, Fields)?.GetValue(obj);
    public static NPC? Speaker(IClickableMenu menu) => Field(menu, "npc") as NPC;
    public static bool Busy(IClickableMenu menu) => Field(menu, "isWaitingForResponse") is not false
        || Field(menu, "isGeneratingWelcome") is not false;
    public static string? LastReply(IClickableMenu menu)
    {
        if (Field(menu, "chatHistory") is not IEnumerable history) return null;
        return history.Cast<object>().Reverse().FirstOrDefault(m =>
            m.GetType().GetProperty("IsFromPlayer")?.GetValue(m) is false
            && m.GetType().GetProperty("IsTypingIndicator")?.GetValue(m) is false)
            is { } last ? last.GetType().GetProperty("Text")?.GetValue(last) as string : null;
    }
    public IClickableMenu? Open(NPC npc, string displayedLine)
    {
        var entry = TypeOf("ModEntry"); var chat = TypeOf("ChatUI"); var client = TypeOf("UnifiedAIClient");
        var config = entry?.GetProperty("ConfigManager")?.GetValue(null);
        var history = entry?.GetProperty("ConversationManager")?.GetValue(null);
        if (config is null || history is null || chat is null || client is null
            || chat.GetField("npc", Fields) is null || chat.GetField("isWaitingForResponse", Fields) is null
            || chat.GetMethod("RestoreFromModal") is null) return null;
        // Seed the real spoken line BEFORE construction: suppress the otherwise automatic welcome LLM call.
        var previous = (history.GetType().GetMethod("GetConversationHistory")?.Invoke(history, new object[] { npc }) as IEnumerable)?.Cast<object>().LastOrDefault();
        if (previous?.GetType().GetProperty("Message")?.GetValue(previous) as string != displayedLine
            || previous?.GetType().GetProperty("IsFromPlayer")?.GetValue(previous) is not false)
            history.GetType().GetMethod("AddMessage")!.Invoke(history, new object[] { npc, displayedLine, false });
        _client ??= Activator.CreateInstance(client, _monitor, config);
        var menu = Activator.CreateInstance(chat, _monitor, _client!, npc) as IClickableMenu;
        if (menu is not null) Resume(menu);
        return menu;
    }
    public static void Resume(IClickableMenu chat)
    {
        // RestoreFromModal requires a vacant active menu; setting it does not advance native dialogue.
        Game1.activeClickableMenu = null;
        Game1.dialogueUp = false;
        chat.GetType().GetMethod("RestoreFromModal")!.Invoke(chat, null);
        if (!ReferenceEquals(Game1.activeClickableMenu, chat)) throw new InvalidOperationException("SocialValley did not restore its chat menu");
    }
    public static void Suspend()
    {
        Game1.keyboardDispatcher.Subscriber = null;
        TypeOf("ModEntry")?.GetProperty("IsChatOpen")?.SetValue(null, false);
    }
}
