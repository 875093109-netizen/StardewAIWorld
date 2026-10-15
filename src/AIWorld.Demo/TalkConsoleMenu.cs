using System.Globalization;
using AIWorld.Core.Chat;
using AIWorld.Core.Providers;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace AIWorld.Demo;

/// <summary>Stardew-native controls. Settings are drafts until Save; traces are read-only.</summary>
public sealed class TalkConsoleMenu : IClickableMenu
{
    private readonly TalkConsoleHost _host;
    private TalkOptions _draft;
    private readonly List<(Rectangle Bounds, Action Click)> _buttons = new();
    private readonly List<(Rectangle Bounds, string Text)> _tooltips = new();
    private readonly Dictionary<string, TextBox> _fields = new();
    private readonly string[] _tabs = { "tab.chat", "tab.trace", "tab.basic", "tab.prompt", "tab.context", "tab.model" };
    private int _tab, _scroll;
    private bool _drawingContent;
    private readonly Dictionary<string, int> _fieldRows = new();
    private string? _selected;
    private string _npc = "";
    private string _jobNpc = "", _jobLanguage = "";
    private NpcJobState? _job;
    private DateTimeOffset _jobUpdated;
    private Task<string>? _chat;
    private Task<ProviderResponse>? _probe;
    private string _probeId = "";
    private string _report = "";
    private long _reportVersion = -1;
    private string _reportLanguage = "";
    private List<string> _reportLines = new();
    public TalkConsoleMenu(TalkConsoleHost host, string? selected = null)
        : base(Math.Max(8, (Game1.uiViewport.Width - 1040) / 2), Math.Max(8, (Game1.uiViewport.Height - 730) / 2),
            Math.Min(1040, Game1.uiViewport.Width - 16), Math.Min(730, Game1.uiViewport.Height - 16), true)
    {
        _host = host; _draft = host.Options; _selected = selected;
        _npc = Game1.currentLocation.characters.Where(n => n.IsVillager).OrderBy(n => Vector2.DistanceSquared(n.Tile, Game1.player.Tile)).FirstOrDefault()?.Name ?? "";
        SetTab(selected is null ? 0 : 1);
    }
    private void SetTab(int tab)
    {
        foreach (var box in _fields.Values) box.Selected = false;
        if (_tab == 5 && tab != 5 && _fields.ContainsKey("endpoint"))
        {
            _draft.Endpoint = _fields["endpoint"].Text; _draft.Model = _fields["model"].Text;
            if (double.TryParse(_fields["temperature"].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) _draft.Temperature = t;
            if (int.TryParse(_fields["tokens"].Text, out var n)) _draft.MaxOutputTokens = n;
        }
        _fields.Clear(); _fieldRows.Clear(); _buttons.Clear(); _tab = tab; _scroll = 0;
        if (tab == 0) AddField("message", "", xPositionOnScreen + 220, yPositionOnScreen + height - 146, width - 360, 2000);
        if (tab == 5)
        {
            AddField("endpoint", _draft.Endpoint, xPositionOnScreen + 185, yPositionOnScreen + 150, width - 220, 300);
            AddField("model", _draft.Model, xPositionOnScreen + 185, yPositionOnScreen + 220, width - 220, 150);
            AddField("temperature", _draft.Temperature.ToString(CultureInfo.InvariantCulture), xPositionOnScreen + 185, yPositionOnScreen + 290, 160, 8);
            AddField("tokens", _draft.MaxOutputTokens.ToString(), xPositionOnScreen + 490, yPositionOnScreen + 290, 180, 5);
            AddField("key", "", xPositionOnScreen + 185, yPositionOnScreen + 425, width - 220, 500).PasswordBox = true;
        }
    }
    private TextBox AddField(string name, string value, int x, int y, int w, int limit)
    {
        var box = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null!, Game1.smallFont, Game1.textColor)
        { X = x, Y = y, Width = Math.Max(120, w), Height = 48, Text = value, textLimit = limit, limitWidth = false };
        box.OnEnterPressed += _ => { if (_tab == 0) Send(); };
        _fields[name] = box; _fieldRows[name] = y - yPositionOnScreen; return box;
    }
    public void LanguageChanged() { _reportVersion = -1; _buttons.Clear(); }
    private void Label(SpriteBatch b, string text, int x, int y, float scale = .9f, int? maxWidth = null)
    {
        if (_drawingContent && _tab >= 2) { y -= _scroll * 32; if (y < 120 || y + 25 > height - 135) return; }
        int available = Math.Max(0, Math.Min(maxWidth ?? width, width - x - 20));
        string shown = Fit(text, available, scale);
        if (shown != text) _tooltips.Add((new Rectangle(xPositionOnScreen + x, yPositionOnScreen + y, available, 28), text));
        b.DrawString(Game1.smallFont, shown, new Vector2(xPositionOnScreen + x, yPositionOnScreen + y), Game1.textColor, 0, Vector2.Zero, scale, SpriteEffects.None, 1);
    }
    private void Button(SpriteBatch b, string text, int x, int y, int w, Action action)
    {
        if (_drawingContent && _tab >= 2) { y -= _scroll * 32; if (y < 120 || y + 44 > height - 135) return; }
        var r = new Rectangle(xPositionOnScreen + x, yPositionOnScreen + y, w, 44);
        drawTextureBox(b, r.X, r.Y, r.Width, r.Height, Color.White);
        string shown = Fit(text, w - 20, .85f);
        if (shown != text) _tooltips.Add((r, text));
        b.DrawString(Game1.smallFont, shown, new Vector2(r.X + 10, r.Y + 9), Game1.textColor, 0, Vector2.Zero, .85f, SpriteEffects.None, 1);
        _buttons.Add((r, action));
    }
    private void Toggle(SpriteBatch b, string label, int y, bool value, Action change)
        => Button(b, "[" + UiText.T(value ? "ui.on" : "ui.off") + "] " + label, 28, y, width - 56, change);
    private void Number(SpriteBatch b, string label, int y, string value, Action minus, Action plus)
    {
        Label(b, label, 28, y + 10, maxWidth: width - 350); Label(b, value, width - 300, y + 10, maxWidth: 130);
        Button(b, "−", width - 160, y, 54, minus); Button(b, "+", width - 94, y, 54, plus);
    }
    public override void draw(SpriteBatch b)
    {
        _buttons.Clear(); _tooltips.Clear();
        b.Draw(Game1.staminaRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * .35f);
        drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White * .97f);
        Label(b, UiText.T("ui.title"), 26, 20, 1.1f, maxWidth: 300);
        var usage = _host.Server.Journal.Usage;
        Label(b, Fit(UiText.T("ui.usage", usage.Calls, usage.TotalTokens, usage.UnknownUsageCalls), width - 380, .75f), 350, 28, .75f);
        int tabWidth = (width - 56) / 6;
        for (int i = 0; i < _tabs.Length; i++) { int tab = i; Button(b, (_tab == i ? "● " : "") + UiText.T(_tabs[i]), 28 + tabWidth * i, 67, tabWidth - 4, () => SetTab(tab)); }
        _drawingContent = true;
        if (_tab == 0) DrawConversation(b);
        if (_tab == 1) DrawReport(b);
        if (_tab == 2)
        {
            Toggle(b, UiText.T("ui.overlay"), 132, _draft.ShowOverlay, () => _draft.ShowOverlay = !_draft.ShowOverlay);
            Toggle(b, UiText.T("ui.ambient"), 188, _draft.AutoConversations, () => _draft.AutoConversations = !_draft.AutoConversations);
            Number(b, UiText.T("ui.opacity"), 245, _draft.Opacity.ToString("P0"), () => _draft.Opacity = Math.Max(.35, _draft.Opacity - .05), () => _draft.Opacity = Math.Min(1, _draft.Opacity + .05));
            Number(b, UiText.T("ui.cooldown"), 301, _draft.IntervalSeconds.ToString(), () => _draft.IntervalSeconds = Math.Max(30, _draft.IntervalSeconds - 30), () => _draft.IntervalSeconds = Math.Min(900, _draft.IntervalSeconds + 30));
            Number(b, UiText.T("ui.participants"), 357, _draft.MaxParticipants.ToString(), () => _draft.MaxParticipants = Math.Max(2, _draft.MaxParticipants - 1), () => _draft.MaxParticipants = Math.Min(4, _draft.MaxParticipants + 1));
            Number(b, UiText.T("ui.turns"), 413, _draft.MaxTurns.ToString(), () => _draft.MaxTurns = Math.Max(2, _draft.MaxTurns - 1), () => _draft.MaxTurns = Math.Min(8, _draft.MaxTurns + 1));
            Number(b, UiText.T("ui.daily-limit"), 469, _draft.DailyAmbientCalls.ToString(), () => _draft.DailyAmbientCalls = Math.Max(0, _draft.DailyAmbientCalls - 4), () => _draft.DailyAmbientCalls = Math.Min(200, _draft.DailyAmbientCalls + 4));
        }
        if (_tab == 3)
        {
            Label(b, UiText.T("ui.style-hint"), 28, 138);
            var lines = Wrap(UiText.EffectivePrompt(_draft.GlobalPrompt, UiText.Language), width - 70, .85f).Take(5).ToArray();
            for (int i = 0; i < lines.Length; i++) Label(b, lines[i], 30, 176 + 28 * i, .85f);
            Button(b, UiText.T("ui.edit-prompt"), 28, 328, 220, () => Edit(() => UiText.T("ui.global-prompt"), UiText.EffectivePrompt(_draft.GlobalPrompt, UiText.Language), 6000, s => _draft.GlobalPrompt = s));
            Button(b, UiText.T("ui.reset-prompt"), 264, 328, 220, () => _draft.GlobalPrompt = new TalkOptions().GlobalPrompt);
            Button(b, UiText.T("ui.character", DisplayName(_npc)), 28, 395, 300, CycleNpc);
            Button(b, UiText.T("ui.edit-character"), 350, 395, 220, () => { if (_npc.Length > 0) Edit(() => UiText.T("ui.character-notes", DisplayName(_npc)), _draft.PersonaNotes.GetValueOrDefault(_npc, ""), 2000, s => _draft.PersonaNotes[_npc] = s); });
            Label(b, Fit(_draft.PersonaNotes.GetValueOrDefault(_npc, UiText.T("ui.no-notes")), width - 60, .8f), 30, 459, .8f);
        }
        if (_tab == 4)
        {
            Toggle(b, UiText.T("ui.environment"), 140, _draft.IncludeEnvironment, () => _draft.IncludeEnvironment = !_draft.IncludeEnvironment);
            Toggle(b, UiText.T("ui.history"), 208, _draft.IncludeHistory, () => _draft.IncludeHistory = !_draft.IncludeHistory);
            Number(b, UiText.T("ui.memories"), 276, _draft.RecentMemories.ToString(), () => _draft.RecentMemories = Math.Max(0, _draft.RecentMemories - 1), () => _draft.RecentMemories = Math.Min(6, _draft.RecentMemories + 1));
            Label(b, UiText.T("ui.guards"), 30, 360);
            Label(b, UiText.T("ui.retention"), 30, 400, .85f);
            Label(b, UiText.T("ui.settings-hint"), 30, 440, .85f);
        }
        if (_tab == 5)
        {
            Label(b, UiText.T("ui.endpoint"), 28, 160, maxWidth: 145); Label(b, UiText.T("ui.model-name"), 28, 230, maxWidth: 145);
            Label(b, UiText.T("ui.temperature"), 28, 300, maxWidth: 145); Label(b, UiText.T("ui.output-limit"), 365, 300, maxWidth: 115);
            Button(b, UiText.T("ui.thinking", UiText.T(_draft.Thinking ? "ui.on" : "ui.off")), 28, 355, 400, () => _draft.Thinking = !_draft.Thinking);
            Label(b, UiText.T("ui.new-key"), 28, 435, maxWidth: 145);
            Label(b, UiText.T("ui.key-hint"), 28, 490, .8f);
            Label(b, UiText.T("ui.credential", UiText.T(_host.Model.HasCredential ? "ui.key-set" : "ui.key-unset")), 28, 524, .8f);
        }
        _drawingContent = false;
        foreach (var pair in _fields)
        {
            var field = pair.Value; field.Y = yPositionOnScreen + _fieldRows[pair.Key] - (_tab >= 2 ? _scroll * 32 : 0);
            if (_tab < 2 || field.Y >= yPositionOnScreen + 120 && field.Y + 48 < yPositionOnScreen + height - 135) field.Draw(b);
            else field.Selected = false;
        }
        if (_tab == 5) Button(b, _probe is null ? UiText.T("ui.save-test") : UiText.T("ui.testing"), 28, height - 114, 225, Probe);
        if (_tab >= 2) Button(b, UiText.T("ui.save"), width - 192, height - 114, 160, Save);
        Label(b, Fit(_host.Notice, width - 60, .75f), 28, height - 48, .75f);
        base.draw(b);
        var tooltip = _tooltips.LastOrDefault(t => t.Bounds.Contains(Game1.getMouseX(), Game1.getMouseY())).Text;
        if (tooltip is not null) drawHoverText(b, Game1.parseText(tooltip, Game1.smallFont, Math.Min(500, width - 60)), Game1.smallFont);
        drawMouse(b);
    }
    private void DrawConversation(SpriteBatch b)
    {
        var records = _host.Server.Journal.Snapshot().Reverse().ToArray();
        int visible = Math.Max(1, (height - 380) / 52);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, records.Length - visible));
        for (int i = 0; i < visible && i + _scroll < records.Length; i++)
        {
            var record = records[i + _scroll];
            string line = record.Source == "PlayerChat" ? UiText.T("ui.you") + " → " + DisplayName(record.Speaker) + ": " + record.Trigger + "  |  " + record.Speech
                : DisplayName(record.Speaker) + " → " + record.Target + ": " + (record.Speech.Length > 0 ? record.Speech : record.Validation);
            Button(b, $"{record.Timestamp:HH:mm}  {line}", 28, 130 + i * 52, width - 56, () => { _selected = record.Id; SetTab(1); });
        }
        if (_jobNpc != _npc || _jobLanguage != UiText.Language || (DateTimeOffset.UtcNow - _jobUpdated).TotalSeconds >= .5)
        {
            _jobNpc = _npc; _jobLanguage = UiText.Language; _jobUpdated = DateTimeOffset.UtcNow;
            _job = _npc.Length == 0 ? null : _host.Server.ReadJob(_npc);
        }
        var targetName = _job?.Target == "Player" ? UiText.T("ui.you")
            : _job?.Target is { } target ? Game1.getLocationFromName(target)?.DisplayName ?? target : null;
        Label(b, UiText.T("ui.current-job", _job?.Summary(targetName) ?? UiText.T("job.phase.unknown")), 28, height - 250, .85f);
        Button(b, UiText.T("ui.target", DisplayName(_npc)), 28, height - 204, 310, CycleNpc);
        Button(b, UiText.T("ui.start-nearby"), 355, height - 204, 230, () => { _host.Director.RequestConversation(); _host.Notice = UiText.T("ui.start-hint"); });
        Label(b, Fit(_host.Director.Status + " · " + UiText.T("ui.daily-usage", _host.Director.CallsToday, _host.Options.DailyAmbientCalls), width - 640, .75f), 610, height - 192, .75f);
        Label(b, UiText.T("ui.your-message"), 28, height - 134, maxWidth: 180);
        Button(b, _chat is null ? UiText.T("ui.send") : UiText.T("ui.wait"), width - 118, height - 146, 88, Send);
    }
    private void DrawReport(SpriteBatch b)
    {
        var record = _host.Server.Journal.Snapshot().FirstOrDefault(r => r.Id == _selected);
        if (record is null) { Label(b, UiText.T("ui.select-record"), 28, 145); return; }
        if (_reportVersion != _host.Server.Journal.Version || _reportLanguage != UiText.Language || !_report.StartsWith(record.Id + "\n", StringComparison.Ordinal))
        {
            var text = TalkJournal.Report(record, UiText.Language); _report = record.Id + "\n" + text;
            _reportLines = Wrap(text, width - 80, .8f); _reportVersion = _host.Server.Journal.Version; _reportLanguage = UiText.Language;
        }
        int visible = Math.Max(1, (height - 260) / 25);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _reportLines.Count - visible));
        for (int i = 0; i < visible && _scroll + i < _reportLines.Count; i++) Label(b, _reportLines[_scroll + i], 30, 135 + i * 25, .8f);
        Button(b, UiText.T("ui.export"), 28, height - 114, 205, () => _host.Export(record));
        Button(b, UiText.T("ui.back-chat"), 251, height - 114, 150, () => SetTab(0));
        Label(b, UiText.T("ui.scroll", _scroll + 1, _reportLines.Count), width - 270, height - 102, .8f);
    }
    private void CycleNpc()
    {
        var npcs = Game1.currentLocation.characters.Where(n => n.IsVillager).Select(n => n.Name).Distinct().OrderBy(n => n).ToArray();
        if (npcs.Length == 0) { _npc = ""; _host.Notice = UiText.T("ui.no-villagers"); return; }
        _npc = npcs[(Array.IndexOf(npcs, _npc) + 1) % npcs.Length];
    }
    private void Send()
    {
        if (_chat is not null || !_fields.TryGetValue("message", out var box) || string.IsNullOrWhiteSpace(box.Text) || _npc.Length == 0) return;
        _chat = _host.Server.PlayerSayAsync(_npc, box.Text); box.Text = ""; box.Selected = false;
        _host.Notice = UiText.T("ui.wait-reply");
    }
    private bool SaveDraft()
    {
        try
        {
            if (_tab == 5)
            {
                if (!double.TryParse(_fields["temperature"].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
                    || !int.TryParse(_fields["tokens"].Text, out var tokens)) throw new ArgumentException(UiText.T("ui.numeric-error"));
                _draft.Endpoint = _fields["endpoint"].Text; _draft.Model = _fields["model"].Text;
                _draft.Temperature = temperature; _draft.MaxOutputTokens = tokens;
            }
            _host.SaveOptions(_draft, _tab == 5 ? _fields["key"].Text : null); _draft = _host.Options;
            if (_tab == 5) SetTab(5);
            return true;
        }
        catch (Exception ex) { _host.Notice = ex is ArgumentException ? ex.Message : UiText.T("ui.save-failed", ex.GetType().Name); return false; }
    }
    private void Save() => SaveDraft();
    private void Probe()
    {
        if (_probe is not null || !SaveDraft()) return;
        _probeId = _host.Server.Journal.Start("ConnectionTest", "System", "API", "Connection test requested by player", revision: _draft.Revision);
        _probe = RunProbe();
    }
    private async Task<ProviderResponse> RunProbe()
    {
        var result = await new JournalLanguageModel(_host.Model, _host.Server.Journal, _probeId)
            .GenerateRawAsync("Return a JSON object only.", "Return {\"ok\":true}.");
        _host.Server.Journal.Update(_probeId, validation: result.Status.ToString()); return result;
    }
    private void Edit(Func<string> title, string text, int limit, Action<string> accept)
    {
        foreach (var field in _fields.Values) field.Selected = false;
        Game1.activeClickableMenu = new TalkTextEditor(title, text, limit, value => { accept(value); Game1.activeClickableMenu = this; }, () => Game1.activeClickableMenu = this);
    }
    public override void update(GameTime time)
    {
        base.update(time);
        if (_chat?.IsCompleted == true)
        {
            _host.Notice = _chat.Status == TaskStatus.RanToCompletion ? _chat.Result : UiText.T("ui.chat-failed"); _chat = null;
        }
        if (_probe?.IsCompleted == true)
        {
            var result = _probe.Status == TaskStatus.RanToCompletion ? _probe.Result : null;
            _host.Notice = result?.Status == ProviderStatus.Success ? UiText.T("ui.test-ok") : UiText.T("ui.test-failed", result?.Error ?? UiText.T("ui.cancelled"));
            _host.Server.Journal.Update(_probeId, validation: _host.Notice); _probe = null;
        }
    }
    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        foreach (var box in _fields.Values)
            if (_tab < 2 || box.Y >= yPositionOnScreen + 120 && box.Y + 48 < yPositionOnScreen + height - 135) box.Update(); else box.Selected = false;
        var action = _buttons.LastOrDefault(b => b.Bounds.Contains(x, y)).Click;
        if (action is not null) { try { action(); } catch (Exception ex) { _host.Notice = UiText.T("ui.action-failed", ex.GetType().Name); } return; }
        base.receiveLeftClick(x, y, playSound);
    }
    public override void receiveScrollWheelAction(int direction)
    {
        _scroll = Math.Max(0, _scroll + (direction < 0 ? 2 : -2));
        if (_tab >= 2) _scroll = Math.Min(_scroll, Math.Max(0, (680 - height + 31) / 32));
    }
    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        width = Math.Min(1040, Game1.uiViewport.Width - 16); height = Math.Min(730, Game1.uiViewport.Height - 16);
        xPositionOnScreen = Math.Max(8, (Game1.uiViewport.Width - width) / 2); yPositionOnScreen = Math.Max(8, (Game1.uiViewport.Height - height) / 2);
        initializeUpperRightCloseButton(); SetTab(_tab); _reportVersion = -1;
    }
    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape) { exitThisMenu(); return; }
        if (_fields.Values.Any(f => f.Selected)) return;
        base.receiveKeyPress(key);
    }
    protected override void cleanupBeforeExit() { foreach (var box in _fields.Values) box.Selected = false; base.cleanupBeforeExit(); }
    public static string DisplayName(string npc) => Game1.getCharacterFromName(npc)?.displayName ?? (npc.Length == 0 ? UiText.T("ui.nobody") : npc);
    public static string Fit(string text, int width, float scale)
    {
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        if (width < 20) return "";
        int lo = 0, hi = text.Length;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (Game1.smallFont.MeasureString(text[..mid]).X * scale <= width - 20) lo = mid; else hi = mid - 1; }
        int count = lo;
        return count < text.Length ? text[..count] + "…" : text;
    }
    public static List<string> Wrap(string text, int width, float scale)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            string line = "";
            foreach (char c in paragraph)
            {
                if (line.Length > 0 && Game1.smallFont.MeasureString(line + c).X * scale > width) { result.Add(line); line = ""; }
                line += c;
            }
            result.Add(line);
        }
        return result;
    }
}

public sealed class TalkTextEditor : IClickableMenu, IKeyboardSubscriber
{
    private readonly Func<string> _title;
    private string _text;
    private int _cursor, _scroll;
    private bool _selectAll;
    private readonly int _limit;
    private readonly Action<string> _accept;
    private readonly Action _cancel;
    public bool Selected { get; set; } = true;
    public TalkTextEditor(Func<string> title, string text, int limit, Action<string> accept, Action cancel)
        : base(40, 40, Game1.uiViewport.Width - 80, Game1.uiViewport.Height - 80)
    { (_title, _text, _limit, _accept, _cancel) = (title, text, limit, accept, cancel); _cursor = text.Length; Game1.keyboardDispatcher.Subscriber = this; }
    private void Close(bool accept)
    {
        Selected = false; if (ReferenceEquals(Game1.keyboardDispatcher.Subscriber, this)) Game1.keyboardDispatcher.Subscriber = null;
        if (accept) _accept(_text); else _cancel();
    }
    public void CancelEditing() => Close(false);
    public void RecieveTextInput(char c) { if (!char.IsControl(c)) RecieveTextInput(c.ToString()); }
    public void RecieveTextInput(string text)
    {
        if (_selectAll) { _text = ""; _cursor = 0; _selectAll = false; }
        text = text.Replace("\r", ""); int remaining = Math.Max(0, _limit - _text.Length); text = text[..Math.Min(text.Length, remaining)];
        _text = _text.Insert(_cursor, text); _cursor += text.Length;
        FollowCaret();
    }
    public void RecieveCommandInput(char command)
    {
        if (command == '\b' && _selectAll) { _text = ""; _cursor = 0; _selectAll = false; return; }
        if (command == '\b' && _cursor > 0) { _text = _text.Remove(--_cursor, 1); }
        if (command is '\r' or '\n') RecieveTextInput("\n");
        FollowCaret();
    }
    public void RecieveSpecialInput(Keys key)
    {
        if (key == Keys.Left) _cursor = Math.Max(0, _cursor - 1);
        if (key == Keys.Right) _cursor = Math.Min(_text.Length, _cursor + 1);
        int lineStart = _cursor == 0 ? 0 : _text.LastIndexOf('\n', _cursor - 1) + 1;
        int nextBreak = _text.IndexOf('\n', _cursor);
        if (key == Keys.Home) _cursor = lineStart;
        if (key == Keys.End) _cursor = nextBreak < 0 ? _text.Length : nextBreak;
        if (key == Keys.Up && lineStart > 0) { int previous = lineStart <= 1 ? 0 : _text.LastIndexOf('\n', lineStart - 2) + 1; _cursor = Math.Min(lineStart - 1, previous + _cursor - lineStart); }
        if (key == Keys.Down && nextBreak >= 0) { int end = _text.IndexOf('\n', nextBreak + 1); _cursor = Math.Min(end < 0 ? _text.Length : end, nextBreak + 1 + _cursor - lineStart); }
        if (key == Keys.Delete && _cursor < _text.Length) _text = _text.Remove(_cursor, 1);
        FollowCaret();
    }
    private void FollowCaret()
    {
        int line = TalkConsoleMenu.Wrap(_text[.._cursor], width - 50, .85f).Count - 1;
        int visible = Math.Max(1, (height - 160) / 28);
        if (line < _scroll) _scroll = line; else if (line >= _scroll + visible) _scroll = line - visible + 1;
    }
    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape) Close(false);
        if (key == Keys.A && (Keyboard.GetState().IsKeyDown(Keys.LeftControl) || Keyboard.GetState().IsKeyDown(Keys.RightControl))) _selectAll = true;
    }
    public override void receiveScrollWheelAction(int direction) => _scroll = Math.Max(0, _scroll + (direction < 0 ? 4 : -4));
    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (x >= xPositionOnScreen + 16 && x < xPositionOnScreen + width - 16
            && y >= yPositionOnScreen + height - 76 && y < yPositionOnScreen + height - 16)
            Close(x < xPositionOnScreen + width / 2);
    }
    public override void draw(SpriteBatch b)
    {
        drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);
        if (_selectAll) b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + 15, yPositionOnScreen + 65, width - 30, height - 150), Color.LightBlue * .35f);
        b.DrawString(Game1.smallFont, TalkConsoleMenu.Fit(_title() + "  " + UiText.T("ui.editor-count", _text.Length, _limit), width - 44, 1), new Vector2(62, 62), Game1.textColor);
        var lines = TalkConsoleMenu.Wrap(_text.Insert(_cursor, "│"), width - 50, .85f);
        int visible = Math.Max(1, (height - 160) / 28); _scroll = Math.Clamp(_scroll, 0, Math.Max(0, lines.Count - visible));
        for (int i = 0; i < visible && i + _scroll < lines.Count; i++) b.DrawString(Game1.smallFont, lines[i + _scroll], new Vector2(62, 110 + i * 28), Game1.textColor, 0, Vector2.Zero, .85f, SpriteEffects.None, 1);
        for (int i = 0; i < 2; i++)
        {
            int x = xPositionOnScreen + 16 + i * (width - 32) / 2;
            int buttonWidth = (width - 32) / 2;
            drawTextureBox(b, x, yPositionOnScreen + height - 76, buttonWidth, 60, Color.White);
            b.DrawString(Game1.smallFont, TalkConsoleMenu.Fit(UiText.T(i == 0 ? "ui.editor-done" : "ui.editor-cancel"), buttonWidth - 24, .85f),
                new Vector2(x + 12, yPositionOnScreen + height - 55), Game1.textColor, 0, Vector2.Zero, .85f, SpriteEffects.None, 1);
        }
        drawMouse(b);
    }
}
