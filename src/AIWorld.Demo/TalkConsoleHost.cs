using System.Text.Json;
using AIWorld.Adapters;
using AIWorld.Core.Chat;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace AIWorld.Demo;

public sealed record TalkSaveLog(TalkArchive Journal, int BudgetDay, int CallsToday);

/// <summary>HUD and settings lifecycle. No game object is read from a background task.</summary>
public sealed class TalkConsoleHost : IDisposable
{
    public readonly AiWorldChatServer Server;
    public readonly LiveTalkLanguageModel Model;
    public readonly VillageChatDirector Director;
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly ModConfig _config;
    private readonly string _archivePath;
    private bool _collapsed, _dragging, _resizing, _disposed;
    private Point _offset;
    private DateTimeOffset _lastFlush = DateTimeOffset.UtcNow;
    private long _savedVersion = -1;
    private int _savedCalls = -1;
    private Task _writeTask = Task.CompletedTask;
    public string Notice { get; set; } = UiText.T("ui.initial");
    public TalkOptions Options => Model.Options;
    public void LanguageChanged()
    {
        Director.Stop(); Notice = UiText.T("ui.initial");
        if (Game1.activeClickableMenu is TalkConsoleMenu menu) menu.LanguageChanged();
    }
    public TalkConsoleHost(IModHelper helper, IMonitor monitor, ModConfig config, AiWorldChatServer server,
        LiveTalkLanguageModel model, WorldPerceptionSource perception, Func<string, bool> owned)
    {
        (_helper, _monitor, _config, Server, Model) = (helper, monitor, config, server, model);
        Director = new(server, () => Options, perception, owned);
        _archivePath = Path.Combine(helper.DirectoryPath, "talk-history", $"{Game1.uniqueIDForThisGame}-{Game1.player.UniqueMultiplayerID}.json");
        try
        {
            if (File.Exists(_archivePath) && new FileInfo(_archivePath).Length < 80_000_000)
            {
                var archive = JsonSerializer.Deserialize<TalkSaveLog>(File.ReadAllText(_archivePath));
                if (archive is not null) { Server.Journal.Restore(archive.Journal); Director.BudgetDay = archive.BudgetDay; Director.CallsToday = archive.CallsToday; }
            }
        }
        catch (Exception ex) { monitor.Log("[TALK] history load failed: " + ex.GetType().Name, LogLevel.Warn); }
        helper.Events.Display.RenderedHud += Draw;
        helper.Events.Input.ButtonPressed += Pressed;
        helper.Events.Input.ButtonReleased += Released;
        helper.Events.GameLoop.UpdateTicked += Update;
        helper.Events.GameLoop.OneSecondUpdateTicked += Tick;
        helper.Events.GameLoop.Saving += Saving;
        helper.Events.GameLoop.DayEnding += DayEnding;
    }
    public void SaveOptions(TalkOptions options, string? newKey = null)
    {
        var normalized = options.Normalize() with { Revision = Options.Revision + 1 };
        if (!TalkOptions.IsAllowedEndpoint(normalized.Endpoint)) throw new ArgumentException(UiText.T("ui.endpoint-error"));
        if (!TalkOptions.IsDeepSeek(normalized.Endpoint) && string.IsNullOrWhiteSpace(normalized.Model)) throw new ArgumentException(UiText.T("ui.model-required"));
        if (!string.IsNullOrWhiteSpace(newKey))
        {
            Environment.SetEnvironmentVariable(LiveTalkLanguageModel.CredentialVariable(normalized.Endpoint), newKey.Trim(),
                OperatingSystem.IsWindows() ? EnvironmentVariableTarget.User : EnvironmentVariableTarget.Process);
        }
        _config.Talk = normalized;
        _helper.WriteConfig(_config);
        Model.Update(normalized); Director.Stop();
        Notice = UiText.T("ui.saved");
    }
    public void Open(string? id = null)
    {
        if (!Context.IsWorldReady || Game1.eventUp || Game1.activeClickableMenu is not null) return;
        Game1.activeClickableMenu = new TalkConsoleMenu(this, id);
    }
    private Rectangle Bounds
    {
        get
        {
            var o = Options; int width = Math.Min(o.Width, Math.Max(240, Game1.uiViewport.Width - 16));
            int height = _collapsed ? 44 : Math.Min(o.Height, Math.Max(100, Game1.uiViewport.Height - 16));
            return new(Math.Clamp(o.Left, 8, Math.Max(8, Game1.uiViewport.Width - width - 8)),
                Math.Clamp(o.Top, 8, Math.Max(8, Game1.uiViewport.Height - height - 8)), width, height);
        }
    }
    private bool HudVisible => Context.IsWorldReady && Options.ShowOverlay && !Game1.eventUp && Game1.activeClickableMenu is null;
    private void Draw(object? sender, RenderedHudEventArgs e)
    {
        if (!HudVisible) return;
        var r = Bounds; var b = e.SpriteBatch;
        IClickableMenu.drawTextureBox(b, r.X, r.Y, r.Width, r.Height, Color.White * (float)Options.Opacity);
        b.DrawString(Game1.smallFont, TalkConsoleMenu.Fit(UiText.T("ui.hud-title"), r.Width - 64, .8f), new(r.X + 16, r.Y + 10), Game1.textColor, 0, Vector2.Zero, .8f, SpriteEffects.None, 1);
        b.DrawString(Game1.smallFont, _collapsed ? "+" : "−", new(r.Right - 34, r.Y + 8), Game1.textColor);
        if (_collapsed) return;
        int count = Math.Max(1, (r.Height - 70) / 44);
        var records = Server.Journal.Snapshot().Where(t => t.Speech.Length > 0).TakeLast(count).ToArray();
        for (int i = 0; i < records.Length; i++)
        {
            var line = records[i]; int y = r.Y + 46 + i * 44;
            var npc = Game1.getCharacterFromName(line.Speaker);
            if (npc?.Portrait is { } portrait) b.Draw(portrait, new Rectangle(r.X + 14, y, 34, 34), new Rectangle(0, 0, 64, 64), Color.White);
            var name = npc?.displayName ?? line.Speaker;
            b.DrawString(Game1.smallFont, TalkConsoleMenu.Fit(name + ": " + line.Speech, r.Width - 82, .78f), new(r.X + 55, y + 6), Game1.textColor, 0, Vector2.Zero, .78f, SpriteEffects.None, 1);
        }
        b.DrawString(Game1.smallFont, "⋰", new(r.Right - 24, r.Bottom - 28), Game1.textColor);
    }
    private void Pressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button == _config.TalkConsoleKey && Context.IsWorldReady && !Game1.eventUp)
        {
            if (Game1.activeClickableMenu is TalkConsoleMenu menu) menu.exitThisMenu(); else Open();
            _helper.Input.Suppress(e.Button); return;
        }
        if (!HudVisible || e.Button != SButton.MouseLeft) return;
        var p = e.Cursor.GetScaledScreenPixels().ToPoint(); var r = Bounds;
        if (!r.Contains(p)) return;
        _helper.Input.Suppress(e.Button);
        if (p.Y < r.Y + 44)
        {
            if (p.X > r.Right - 48) _collapsed = !_collapsed;
            else { _dragging = true; _offset = p - r.Location; }
        }
        else if (p.X > r.Right - 28 && p.Y > r.Bottom - 28) _resizing = true;
        else
        {
            int count = Math.Max(1, (r.Height - 70) / 44);
            var lines = Server.Journal.Snapshot().Where(t => t.Speech.Length > 0).TakeLast(count).ToArray();
            int index = (p.Y - r.Y - 46) / 44;
            Open(index >= 0 && index < lines.Length ? lines[index].Id : null);
        }
    }
    private void Update(object? sender, UpdateTickedEventArgs e)
    {
        if ((!_dragging && !_resizing) || !HudVisible) return;
        var p = _helper.Input.GetCursorPosition().GetScaledScreenPixels().ToPoint(); var o = Options;
        if (_dragging) { o.Left = p.X - _offset.X; o.Top = p.Y - _offset.Y; }
        else { o.Width = p.X - Bounds.X; o.Height = p.Y - Bounds.Y; }
        Model.Update(o);
    }
    private void Released(object? sender, ButtonReleasedEventArgs e)
    {
        if (e.Button != SButton.MouseLeft || (!_dragging && !_resizing)) return;
        _dragging = _resizing = false; _config.Talk = Options; _helper.WriteConfig(_config);
    }
    private void Tick(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        try { Director.Tick(); }
        catch (Exception ex) { Director.Stop(); Notice = UiText.T("ui.ambient-failed", ex.GetType().Name); }
        if (DateTimeOffset.UtcNow - _lastFlush > TimeSpan.FromSeconds(60)) Flush();
    }
    public string Export(TalkRecord record)
    {
        var path = Path.Combine(_helper.DirectoryPath, "reports", "AIWorld-" + record.Id + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, TalkJournal.Report(record, UiText.Language));
        Notice = UiText.T("ui.exported"); return path;
    }
    private void Saving(object? sender, SavingEventArgs e) => Flush();
    private void DayEnding(object? sender, DayEndingEventArgs e) { Director.Stop(); Flush(); }
    private void Flush(bool wait = false)
    {
        _lastFlush = DateTimeOffset.UtcNow;
        if (!_writeTask.IsCompleted) { if (!wait) return; _writeTask.GetAwaiter().GetResult(); }
        if (_savedVersion == Server.Journal.Version && _savedCalls == Director.CallsToday) return;
        var archive = new TalkSaveLog(Server.Journal.Archive(), Director.BudgetDay, Director.CallsToday);
        _savedVersion = archive.Journal.Revision; _savedCalls = Director.CallsToday;
        _writeTask = Task.Run(() =>
        {
            try
            {
            Directory.CreateDirectory(Path.GetDirectoryName(_archivePath)!);
            File.WriteAllText(_archivePath + ".tmp", JsonSerializer.Serialize(archive));
            File.Move(_archivePath + ".tmp", _archivePath, true);
            }
            catch (Exception ex) { _savedVersion = -1; _monitor.Log("[TALK] history save failed: " + ex.GetType().Name, LogLevel.Warn); }
        });
        if (wait) _writeTask.GetAwaiter().GetResult();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        Director.Dispose(); Flush(wait: true);
        _helper.Events.Display.RenderedHud -= Draw; _helper.Events.Input.ButtonPressed -= Pressed;
        _helper.Events.Input.ButtonReleased -= Released; _helper.Events.GameLoop.UpdateTicked -= Update;
        _helper.Events.GameLoop.OneSecondUpdateTicked -= Tick; _helper.Events.GameLoop.Saving -= Saving; _helper.Events.GameLoop.DayEnding -= DayEnding;
        if (Game1.activeClickableMenu is TalkTextEditor editor) editor.CancelEditing();
        if (Game1.activeClickableMenu is TalkConsoleMenu menu) menu.exitThisMenu();
    }
}
