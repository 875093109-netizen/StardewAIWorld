using AIWorld.Core.Chat;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>Local, host-only, bounded ambient conversation. Never takes a movement controller.</summary>
public sealed class VillageChatDirector : IDisposable
{
    private readonly AiWorldChatServer _server;
    private readonly Func<TalkOptions> _settings;
    private readonly Func<string, bool> _owned;
    private readonly WorldPerceptionSource _perception;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, DateTimeOffset> _cooldown = new(StringComparer.Ordinal);
    private CancellationTokenSource _cancel = new();
    private VillageConversation? _session;
    private Task<bool>? _pending;
    private DateTimeOffset _next;
    private int _revision;
    public int BudgetDay { get; set; } = -1;
    public int CallsToday { get; set; }
    private string _statusKey = "director.wait";
    private object?[] _statusArgs = Array.Empty<object?>();
    public string Status => UiText.T(_statusKey, _statusArgs);
    private void SetStatus(string key, params object?[] args) { _statusKey = key; _statusArgs = args; }
    public VillageChatDirector(AiWorldChatServer server, Func<TalkOptions> settings, WorldPerceptionSource perception, Func<string, bool> owned, Func<DateTimeOffset>? now = null)
    {
        (_server, _settings, _perception, _owned) = (server, settings, perception, owned);
        _now = now ?? (() => DateTimeOffset.UtcNow); _next = _now().AddSeconds(15);
    }
    private static bool WorldAvailable => Context.IsWorldReady && Context.IsMainPlayer && !Game1.eventUp
        && Game1.CurrentEvent is null && Game1.activeClickableMenu is null && Game1.shouldTimePass();
    private VillageParticipant[] Candidates() => Game1.currentLocation.characters.Where(n => n.IsVillager)
        .Where(n => Math.Abs(n.TilePoint.X - Game1.player.TilePoint.X) + Math.Abs(n.TilePoint.Y - Game1.player.TilePoint.Y) <= 10)
        .Select(n => new VillageParticipant(n.Name, n.currentLocation.NameOrUniqueName, n.TilePoint.X, n.TilePoint.Y,
            !new SmapiHardAnchorProvider().IsHardAnchorActive(n.Name) && !n.isMoving() && n.controller is null
            && !_owned(n.Name) && !n.modData.Keys.Any(k => k.Contains("Squad", StringComparison.OrdinalIgnoreCase)))).ToArray();
    private bool MembersValid(string[] members)
    {
        if (!WorldAvailable || _settings().Revision != _revision) return false;
        var current = Candidates().Where(p => members.Contains(p.Id) && p.Available).ToArray();
        return current.Length == members.Length && current.All(a => current.All(b => a.Id == b.Id || VillageConversation.Near(a, b))
            && _perception.CanSeeNpcs(a.Id, members.Where(id => id != a.Id)));
    }
    public void RequestConversation() { _next = DateTimeOffset.MinValue; _manual = true; }
    private bool _manual;
    public void Tick()
    {
        if (!Context.IsWorldReady) return;
        if (BudgetDay != Game1.Date.TotalDays) { Stop(); BudgetDay = Game1.Date.TotalDays; CallsToday = 0; _cooldown.Clear(); }
        var options = _settings();
        if (_pending is not null)
        {
            if (!WorldAvailable || _revision != options.Revision) _cancel.Cancel();
            if (!_pending.IsCompleted) return;
            var success = _pending.Status == TaskStatus.RanToCompletion && _pending.Result;
            _pending = null;
            if (!success) { SetStatus("director.abort"); Stop(); _next = _now().AddSeconds(options.IntervalSeconds); return; }
            _next = _now().AddSeconds(8);
        }
        if (!WorldAvailable || _now() < _next) return;
        if (!options.AutoConversations && !_manual) { Stop(); SetStatus("director.off"); return; }
        if (CallsToday >= options.DailyAmbientCalls) { Stop(); SetStatus("director.limit"); return; }
        var candidates = Candidates();
        if (_session is null)
        {
            var available = candidates.Where(p => p.Available && (!_cooldown.TryGetValue(p.Id, out var until) || until <= _now())).ToArray();
            if (available.Length < 2) { SetStatus("director.need-two"); return; }
            _session = new(available, options.MaxParticipants);
        }
        else _session.Refresh(candidates, options.MaxParticipants);
        var speaker = _session.NextSpeaker();
        var members = _session.Members.ToArray();
        _revision = options.Revision;
        if (speaker is null || _session.Turns >= options.MaxTurns || !MembersValid(members))
        { Stop(); _next = _now().AddSeconds(options.IntervalSeconds); SetStatus("director.end"); return; }
        if (_cancel.IsCancellationRequested) { _cancel.Dispose(); _cancel = new(); }
        CallsToday++;
        SetStatus("director.responding", speaker, members.Length);
        var session = _session;
        _pending = _server.VillageTurnAsync(session.Id, speaker, members, session.HistoryFor(speaker),
            () => ReferenceEquals(session, _session) && MembersValid(members), speech =>
            {
                session.Commit(speaker, speech);
                Game1.getCharacterFromName(speaker)?.showTextAboveHead(speech);
                SetStatus("director.turn", members.Length, session.Turns);
            }, _cancel.Token);
    }
    public void Stop()
    {
        _cancel.Cancel();
        if (_session is not null) foreach (var member in _session.Members) _cooldown[member] = _now().AddSeconds(_settings().IntervalSeconds);
        _session = null; _manual = false;
    }
    public void Dispose() { Stop(); _cancel.Dispose(); }
}
