using System.Runtime.CompilerServices;
using System.Reflection;
using AIWorld.Core.Chat;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace AIWorld.Demo;

/// <summary>Game-thread UI coordinator. Native dialogue remains the owner of its normal advancement.
/// Only a displayed plain daily page may be rewritten; scenes, choices and callbacks remain native.</summary>
public sealed class DialogueUiBridge : IDisposable
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly AiWorldChatServer _server;
    private readonly ModConfig _config;
    private readonly Func<string, string> _state;
    private readonly SocialValleyDialogueUi _social;
    private readonly ConditionalWeakTable<DialogueBox, object> _handled = new();
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private DialogueBox? _box, _returnBox;
    private DialogueBox? _mirroredBox;
    private IClickableMenu? _chat;
    private Dialogue? _synthetic;
    private NPC? _syntheticNpc;
    private string _occurrence = "", _observed = "", _source = "", _requestState = "", _cacheKey = "";
    private int _pageIndex;
    private CancellationTokenSource? _cancel;
    private Task<DialogueRewriteCandidate?>? _pending;
    private bool _disabled;
    private bool _nativeSuspended;

    public DialogueUiBridge(IModHelper helper, IMonitor monitor, AiWorldChatServer server, ModConfig config, Func<string, string> state)
    {
        (_helper, _monitor, _server, _config, _state) = (helper, monitor, server, config, state);
        _social = new(monitor);
    }
    private static bool SafeToSwitch(DialogueBox box) => !Game1.eventUp && Game1.CurrentEvent is null
        && !box.isQuestion && box.characterDialogue?.speaker is not null
        && box.characterDialogue.onFinish is null && box.characterDialogue.answerQuestionBehavior is null
        && Game1.afterDialogues is null && box.characterDialogue.currentDialogueIndex == box.characterDialogue.dialogues.Count - 1
        && box.characterDialogue.dialogues.All(l => l.SideEffects is null)
        && box.characterDialoguesBrokenUp.Count == 1 && !box.dialogueFinished;

    public void Tick()
    {
        if (_disabled || !Context.IsWorldReady) return;
        try
        {
            var menu = Game1.activeClickableMenu;
            if (SocialValleyDialogueUi.IsChat(menu))
            {
                if (!ReferenceEquals(_chat, menu)) { _chat = menu; _returnBox = null; }
                CancelRewrite(); return;
            }
            if (menu is not DialogueBox box || box.characterDialogue?.speaker is not { } npc)
            {
                CancelRewrite(); _box = null;
                if (menu is null) { FinishSuspendedNative(); _returnBox = null; _chat = null; CleanSynthetic(); }
                return;
            }
            if (!ReferenceEquals(_box, box))
            {
                CancelRewrite(); _box = box; _observed = ""; _occurrence = Guid.NewGuid().ToString("N");
                if (!_handled.TryGetValue(box, out _))
                {
                    _handled.Add(box, new());
                    _source = box.getCurrentString(); _pageIndex = box.characterDialogue.currentDialogueIndex;
                    if (_config.RewriteDailyDialogue && SafeToSwitch(box) && DialoguePresentationPolicy.CanRewrite(
                        Game1.eventUp, box.isQuestion, false, false, box.characterDialogue.dialogues.Count,
                        box.characterDialogue.TranslationKey, box.characterDialogue.dialogues[0].Text))
                    {
                        _requestState = SceneState(npc.Name);
                        var lastPlayer = _server.Conversations.Get(npc.Name).LastOrDefault(l => l.Role == "player")?.Text;
                        _cacheKey = npc.Name + "|" + _requestState + "|" + _source + "|" + lastPlayer;
                        if (_cache.TryGetValue(_cacheKey, out var cached)) ShowText(box, cached);
                        else
                        {
                            _cancel = new();
                            _pending = _server.RewriteDialogueAsync(npc.Name, _source, _cancel.Token);
                        }
                    }
                }
            }
            if (_pending?.IsCompleted == true)
            {
                var proposal = _pending.GetAwaiter().GetResult(); _pending = null;
                if (proposal is not null && SafeToSwitch(box) && box.characterDialogue.currentDialogueIndex == _pageIndex
                    && box.getCurrentString() == _source && SceneState(npc.Name) == _requestState
                    && _server.AcceptDialogueRewrite(proposal, text => ShowText(box, text)))
                {
                    if (!proposal.HasAction)
                    {
                        if (_cache.Count >= 128) _cache.Clear();
                        _cache[_cacheKey] = proposal.Speech;
                    }
                }
                else if (box.getCurrentString() == _source && _cache.Count < 128) _cache[_cacheKey] = _source;
            }
            var line = box.getCurrentString();
            // Read only what has actually appeared. Never inspect undisplayed branches for the model.
            if (box.characterIndexInDialogue >= line.Length - 1 && _observed != line
                && !ReferenceEquals(box, _mirroredBox))
            {
                _server.ObserveDialogue(npc.Name, line, _occurrence + ":" + box.characterDialogue.currentDialogueIndex + ":" + line);
                _observed = line;
            }
        }
        catch (Exception ex)
        {
            CancelRewrite(); _disabled = true;
            _monitor.Log($"[DIALOGUE] bridge disabled for this save; native dialogue remains available: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
        }
    }
    private static void ShowText(DialogueBox box, string text)
    {
        // Change the visible page, not the underlying Dialogue or its event/quest callbacks.
        var originalPages = box.characterDialoguesBrokenUp.ToArray(); var originalPosition = box.characterIndexInDialogue;
        try
        {
            box.characterDialoguesBrokenUp.Clear(); box.characterDialoguesBrokenUp.Push(text);
            box.characterIndexInDialogue = 0;
            // Reuse the game's own font-aware page splitting and next-page icons.
            typeof(DialogueBox).GetMethod("checkDialogue", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(box, new object[] { box.characterDialogue });
            typeof(DialogueBox).GetMethod("setUpIcons", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(box, null);
        }
        catch
        {
            box.characterDialoguesBrokenUp.Clear();
            foreach (var page in originalPages.Reverse()) box.characterDialoguesBrokenUp.Push(page);
            box.characterIndexInDialogue = originalPosition;
            throw;
        }
    }
    private string SceneState(string npc) => UiText.Language + "|" + _state(npc);
    private string ButtonText
    {
        get
        {
            var chat = SocialValleyDialogueUi.IsChat(Game1.activeClickableMenu);
            string key = chat ? "ui.native-box" : "ui.continue-chat";
            if (chat && SocialValleyDialogueUi.Busy(Game1.activeClickableMenu!)) key = "ui.wait-short";
            return UiText.T(key) + " [" + _config.DialogueSwitchKey + "]";
        }
    }
    private Rectangle Button
    {
        get
        {
            int width = Math.Min(Game1.uiViewport.Width - 20, Math.Max(278, (int)Math.Ceiling(Game1.smallFont.MeasureString(ButtonText).X) + 32));
            return new(Math.Max(8, Game1.uiViewport.Width - width - 12),
                Game1.activeClickableMenu is DialogueBox b ? Math.Max(8, b.y - 56) : 12, width, 48);
        }
    }
    private bool Available => !_disabled && Context.IsWorldReady && !Game1.eventUp && Game1.CurrentEvent is null
        && (Game1.activeClickableMenu is DialogueBox b && SafeToSwitch(b) || SocialValleyDialogueUi.IsChat(Game1.activeClickableMenu));
    public void Draw(SpriteBatch batch)
    {
        if (!Available) return;
        var r = Button;
        IClickableMenu.drawTextureBox(batch, r.X, r.Y, r.Width, r.Height, Color.White);
        Utility.drawTextWithShadow(batch, ButtonText, Game1.smallFont, new Vector2(r.X + 16, r.Y + 12), Game1.textColor);
    }
    public void ButtonPressed(ButtonPressedEventArgs e)
    {
        if (!Available || e.Button != _config.DialogueSwitchKey
            && !(e.Button == SButton.MouseLeft && Button.Contains(_helper.Input.GetCursorPosition().GetScaledScreenPixels().ToPoint()))) return;
        _helper.Input.Suppress(e.Button);
        var previousMenu = Game1.activeClickableMenu;
        var previousSubscriber = Game1.keyboardDispatcher.Subscriber;
        var previousDialogueUp = Game1.dialogueUp;
        try
        {
            CancelRewrite();
            if (Game1.activeClickableMenu is DialogueBox box && box.characterDialogue?.speaker is { } npc)
            {
                box.finishTyping();
                string spoken = box.getCurrentString();
                if (!ReferenceEquals(box, _mirroredBox))
                    _server.ObserveDialogue(npc.Name, spoken, _occurrence + ":" + box.characterDialogue.currentDialogueIndex + ":" + spoken);
                _returnBox = box;
                if (_chat is not null && SocialValleyDialogueUi.Speaker(_chat)?.Name == npc.Name) SocialValleyDialogueUi.Resume(_chat);
                else _chat = _social.Open(npc, spoken);
                _nativeSuspended = _chat is not null && ReferenceEquals(Game1.activeClickableMenu, _chat);
                if (_chat is null) Game1.addHUDMessage(new HUDMessage(UiText.T("ui.bridge-unavailable")));
            }
            else if (Game1.activeClickableMenu is { } chat && SocialValleyDialogueUi.IsChat(chat) && !SocialValleyDialogueUi.Busy(chat)
                && SocialValleyDialogueUi.Speaker(chat) is { } speaker && SocialValleyDialogueUi.LastReply(chat) is { } reply)
            {
                _chat = chat;
                var plain = DialoguePresentationPolicy.NativeText(reply);
                if (_returnBox?.characterDialogue?.speaker?.Name != speaker.Name)
                {
                    CleanSynthetic();
                    _synthetic = new Dialogue(speaker, "AIWorld:continuation", plain); _syntheticNpc = speaker;
                    speaker.CurrentDialogue.Push(_synthetic);
                    _returnBox = new DialogueBox(_synthetic); _handled.Add(_returnBox, new());
                }
                ShowText(_returnBox, plain);
                _mirroredBox = _returnBox; _nativeSuspended = false;
                SocialValleyDialogueUi.Suspend();
                Game1.activeClickableMenu = _returnBox; Game1.dialogueUp = true; Game1.player.CanMove = false;
            }
        }
        catch (Exception ex)
        {
            Game1.activeClickableMenu = previousMenu;
            if (!SocialValleyDialogueUi.IsChat(previousMenu)) SocialValleyDialogueUi.Suspend();
            Game1.keyboardDispatcher.Subscriber = previousSubscriber;
            Game1.dialogueUp = previousDialogueUp;
            _monitor.Log($"[DIALOGUE] UI switch failed; previous menu restored: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
        }
    }
    private void CancelRewrite() { _cancel?.Cancel(); _cancel?.Dispose(); _cancel = null; _pending = null; }
    private void FinishSuspendedNative()
    {
        if (!_nativeSuspended) return;
        _nativeSuspended = false;
        var box = _returnBox;
        // Closing the chat ends its suspended ordinary greeting through the vanilla close path.
        // Never pop somebody else's replacement dialogue or advance an active event.
        if (box is null || !SafeToSwitch(box) || box.characterDialogue.speaker.CurrentDialogue.TryPeek(out var top) != true
            || !ReferenceEquals(top, box.characterDialogue)) return;
        Game1.activeClickableMenu = box;
        box.characterDialogue.exitCurrentDialogue(); box.dialogueFinished = true;
        box.closeDialogue();
    }
    private void CleanSynthetic()
    {
        if (_synthetic is not null && _syntheticNpc?.CurrentDialogue.TryPeek(out var top) == true && ReferenceEquals(top, _synthetic))
            _syntheticNpc.CurrentDialogue.Pop();
        _synthetic = null; _syntheticNpc = null;
    }
    public void Dispose() { CancelRewrite(); CleanSynthetic(); _cache.Clear(); _chat = null; _returnBox = null; }
}
