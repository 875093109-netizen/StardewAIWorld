using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Chat;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// SelfStateProvider — deterministic live SelfState from World Truth (PM Character Awareness §1).
/// Reads the actual game runtime (location/time/event/follow state) — the LLM never guesses
/// these; the provider determines truth. MAIN THREAD ONLY.
/// </summary>
public sealed class SelfStateProvider
{
    private readonly IMonitor _monitor;
    private readonly Func<string, bool> _isFollowing;      // characterId → following player?
    private readonly Func<string, string> _currentGoal;    // characterId → goal string or ""
    private readonly Func<string, string> _currentTarget;  // characterId → target or ""
    private readonly Func<string, bool>? _isTraveling;     // Travel T1: characterId → travel active?
    private readonly Func<string, string>? _travelTarget;  // Travel T1: characterId → canonical destination or ""
    public NpcKnowledgeSource? Knowledge { get; set; }
    public Func<string, NpcJobOverride?> JobOverride { get; set; } = _ => null;

    public SelfStateProvider(
        IMonitor monitor,
        Func<string, bool> isFollowing,
        Func<string, string>? currentGoal = null,
        Func<string, string>? currentTarget = null,
        Func<string, bool>? isTraveling = null,
        Func<string, string>? travelTarget = null)
    {
        _monitor = monitor;
        _isFollowing = isFollowing;
        _currentGoal = currentGoal ?? (_ => string.Empty);
        _currentTarget = currentTarget ?? (_ => string.Empty);
        _isTraveling = isTraveling;
        _travelTarget = travelTarget;
    }

    public SelfState Get(string characterId)
    {
        var npc = Game1.getCharacterFromName(CharacterId.Name(characterId));
        var scene = Game1.CurrentEvent;
        var actor = scene?.actors.FirstOrDefault(a => a.Name == CharacterId.Name(characterId));
        var loc = actor?.currentLocation ?? npc?.currentLocation;
        var now = GameDate();
        var inEvent = SceneMembership.Contains(CharacterId.Name(characterId), scene?.actors.Select(a => a.Name));
        var following = _isFollowing(characterId);
        var traveling = _isTraveling?.Invoke(characterId) == true;
        var jobOverride = JobOverride(characterId);
        var travelDest = traveling ? (_travelTarget?.Invoke(characterId) ?? string.Empty) : string.Empty;
        // Single shared truth (Core): EVENT > FOLLOW_PLAYER > travel-as-PLAYER_REQUEST > CANONICAL.
        var decision = SelfStateDecider.Decide(inEvent, following, traveling || jobOverride is not null);
        var schedule = npc is null ? null : Knowledge?.Schedule(npc, decision.Activity);
        var job = NpcJobState.Resolve(inEvent, following, npc?.isMoving() == true,
            npc?.controller?.pathToEndPoint?.Count > 0, loc?.NameOrUniqueName ?? "unknown",
            npc?.TilePoint.X ?? -1, npc?.TilePoint.Y ?? -1, schedule, jobOverride,
            npc is null ? null : Knowledge?.JobActivity(npc, schedule));

        return new SelfState
        {
            CharacterId = characterId,
            CurrentLocation = loc?.NameOrUniqueName ?? "unknown",
            CurrentTime = now,
            CurrentActivity = decision.Activity,
            // PM FIX 2 + Travel T1: actively following → PLAYER_REQUEST lease; travel →
            // PLAYER_REQUEST lease (reused — travel is a player-requested non-follow behavior,
            // the existing enum expresses it unambiguously; NO model extension per T1 CLARIFICATION);
            // canonical control otherwise; EVENT during scripted content.
            BehaviorOwner = decision.Owner,
            CurrentGoal = inEvent || string.IsNullOrEmpty(_currentGoal(characterId)) ? null : _currentGoal(characterId),
            CurrentTarget = inEvent ? null : following ? "Player" : traveling ? (string.IsNullOrEmpty(travelDest) ? null : travelDest) : (string.IsNullOrEmpty(_currentTarget(characterId)) ? null : _currentTarget(characterId)),
            FollowingPlayer = following && !inEvent,
            CurrentEvent = inEvent ? (Game1.CurrentEvent?.id ?? "unknown") : null,
            Schedule = schedule,
            Job = job,
        };
    }

    /// <summary>CurrentActivity from World Truth: EVENT anchor → EVENT; actively following →
    /// FOLLOW_PLAYER; traveling → PLAYER_REQUEST (reused, no model extension); otherwise CANONICAL.
    /// AI_AUTONOMOUS is reserved (not produced in current phase). Delegates to the shared
    /// Core decider so production and offline tests use one truth.</summary>
    public CurrentActivity ResolveActivity(string characterId, bool inEvent, bool following, bool traveling = false)
        => SelfStateDecider.Decide(inEvent, following, traveling).Activity;

    private static string GameDate()
    {
        try
        {
            var season = Game1.currentSeason ?? "spring";
            var day = Game1.dayOfMonth;
            var year = Game1.year;
            var time = Game1.timeOfDay.ToString("D4");
            return $"{season}-{day}-{year}@{time}";
        }
        catch
        {
            return "unknown";
        }
    }
}
