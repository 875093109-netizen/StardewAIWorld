using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace AIWorld.Demo;

/// <summary>Main-thread observer. Reads final Content Patcher assets and the executed scene, never runs game commands.</summary>
public sealed class ScriptedStoryObserver
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly AIWorldSaveRepository _repository;
    private readonly CanonicalExperienceService _experiences;
    private readonly PennyBathStory _penny;
    private Event? _active;
    private ObservedScene? _scene;
    private StoryOutcome _outcome;
    private bool? _compatible;
    private string[] _mainCommands = Array.Empty<string>();
    private string[] _forkCommands = Array.Empty<string>();
    private int _forkIndex;
    private int _ticks;
    private bool _failureLogged;

    public ScriptedStoryObserver(IModHelper helper, IMonitor monitor, AIWorldSaveRepository repository, CanonicalExperienceService experiences)
    {
        (_helper, _monitor, _repository, _experiences) = (helper, monitor, repository, experiences);
        _penny = new PennyBathStory(repository.Data!, experiences);
    }
    public void Invalidate() { _compatible = null; _outcome = StoryOutcome.Unknown; }

    public void Tick(bool reconstruct = false, bool force = false)
    {
        if (!Context.IsWorldReady || !_repository.IsLoaded) return;
        try
        {
            var current = Game1.CurrentEvent;
            var changed = !ReferenceEquals(current, _active);
            var completedOutcome = StoryOutcome.Unknown;
            if (changed)
            {
                if (_active is not null && _scene is not null)
                {
                    var completed = Game1.player.eventsSeen.Contains(_active.id);
                    if (_active.id == "38" && Compatible()) completedOutcome = _outcome;
                    else foreach (var experience in _scene.Complete(completed))
                        if (_experiences.Ingest(experience).Accepted) _repository.MarkDataDirty();
                }
                _active = current; _outcome = StoryOutcome.Unknown;
                _scene = current is null || current.isFestival ? null : new ObservedScene(current.id,
                    Game1.currentLocation.NameOrUniqueName, Stamp(), current.actors.Select(a => a.Name));
            }
            if (current is not null && _scene is not null)
            {
                if (current.id == "38" && Compatible() && !current.skipped)
                {
                    var observed = PennyBranchObserver.Observe(current.skipped, current.forked,
                        current.eventCommands.SequenceEqual(_mainCommands), current.eventCommands.SequenceEqual(_forkCommands),
                        current.CurrentCommand, _forkIndex, current.eventCommands.Length);
                    if (observed != StoryOutcome.Unknown) _outcome = observed;
                }
                // DialogueBox holds the currently displayed line, including the chosen response.
                // Only the speaker receives this fallback memory: off-screen cast members are not assumed to hear it.
                if (Game1.activeClickableMenu is DialogueBox box && box.characterDialogue?.speaker is { } speaker)
                {
                    var line = box.getCurrentString();
                    if (box.characterIndexInDialogue >= line.Length - 1)
                        _scene.Observe(speaker.Name, line, current.actors.Select(a => a.Name));
                }
            }
            if (changed || reconstruct || force || ++_ticks % 60 == 0)
            {
                var player = Game1.player;
                var evidence = new PennyBathEvidence(Compatible(),
                    player.mailForTomorrow.Contains("pennySpa"), player.mailbox.Contains("pennySpa"), player.mailReceived.Contains("pennySpa"),
                    player.eventsSeen.Contains("38") && current?.id != "38", current?.id == "38", completedOutcome, reconstruct);
                if (_penny.Apply(evidence, Stamp())) _repository.MarkDataDirty();
            }
        }
        catch (Exception ex)
        {
            _compatible = false;
            if (!_failureLogged) { _monitor.Log($"[STORY] observation unavailable: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn); _failureLogged = true; }
        }
    }

    private bool Compatible()
    {
        if (_compatible.HasValue) return _compatible.Value;
        var events = _helper.GameContent.Load<Dictionary<string, string>>("Data/Events/BathHouse_Pool");
        var scripts = events.Where(p => p.Key.Split('/')[0] == "38").ToArray();
        var mail = _helper.GameContent.Load<Dictionary<string, string>>("Data/mail");
        var dialogue = _helper.GameContent.Load<Dictionary<string, string>>("Characters/Dialogue/Penny");
        var trigger = _helper.GameContent.Load<List<StardewValley.GameData.TriggerActionData>>("Data/TriggerActions")
            .SingleOrDefault(t => t.Id == "Mail_Penny_10heart");
        bool ok = scripts.Length == 1 && PennyContentGuard.Matches("event", scripts[0].Value)
            && events.TryGetValue("pennyHeartbroken", out var fork) && PennyContentGuard.Matches("fork", fork)
            && mail.TryGetValue("pennySpa", out var invitation) && PennyContentGuard.Matches("mail", invitation)
            && dialogue.TryGetValue("event_pool4", out var accept) && PennyContentGuard.Matches("accept", accept)
            && dialogue.TryGetValue("event_pool5", out var reject) && PennyContentGuard.Matches("reject", reject)
            && trigger?.Action == "AddMail Current pennySpa" && trigger.MarkActionApplied;
        if (ok)
        {
            _mainCommands = Event.ParseCommands(scripts[0].Value, Game1.player);
            _forkCommands = Event.ParseCommands(events["pennyHeartbroken"], Game1.player);
            _forkIndex = Array.IndexOf(_mainCommands, "fork pennyHeartbroken");
            ok = _forkIndex >= 0;
        }
        _compatible = ok;
        _monitor.Log(ok ? "[STORY] Penny invitation/event/branches verified against installed vanilla translations"
            : "[STORY] Penny content differs from verified definitions; detailed vanilla facts disabled", ok ? LogLevel.Debug : LogLevel.Warn);
        return ok;
    }
    private static string Stamp() => $"{Game1.currentSeason}-{Game1.dayOfMonth}-{Game1.year}@{Game1.timeOfDay:D4}";
}
