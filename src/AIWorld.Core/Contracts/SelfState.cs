using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// CurrentActivity — generic semantic activity classification (PM Character Awareness §2/§3).
/// Deterministic: derived from World Truth by CurrentActivityResolver, NEVER guessed by LLM.
/// </summary>
public enum CurrentActivity
{
    /// <summary>Canonical work/schedule (the vanilla baseline).</summary>
    CANONICAL,

    /// <summary>Player-requested behavior other than active following (e.g. returning to
    /// baseline after a player request ended).</summary>
    PLAYER_REQUEST,

    /// <summary>Actively following/accompanying the player (a specific player-requested
    /// activity — PM FIX 2 2026-08-12: distinct from generic PLAYER_REQUEST).</summary>
    FOLLOW_PLAYER,

    /// <summary>AI-autonomous activity (future: investigation/travel). Reserved — not
    /// produced in current phase; enum exists so callers/LLM never see an unknown value.</summary>
    AI_AUTONOMOUS,

    /// <summary>Inside a scripted event/cutscene (hard anchor).</summary>
    EVENT,
}

/// <summary>
/// SelfState — deterministic live character state snapshot (PM Character Awareness §1).
/// Immutable. Derived from World Truth by SelfStateProvider at read time.
/// The LLM receives this as context and must NOT guess any of these values.
/// </summary>
public sealed class SelfState
{
    [JsonPropertyName("characterId")]
    public required string CharacterId { get; init; }

    [JsonPropertyName("currentLocation")]
    public required string CurrentLocation { get; init; }

    /// <summary>Game time as {season}-{day}-{year}@{HHMM} (GameTimeStamp form).</summary>
    [JsonPropertyName("currentTime")]
    public required string CurrentTime { get; init; }

    [JsonPropertyName("currentActivity")]
    public required CurrentActivity CurrentActivity { get; init; }

    /// <summary>Behavior lease owner — who currently controls this NPC's behavior.
    /// Contract (PM FIX 2 2026-08-12): CANONICAL / PLAYER_REQUEST / AI_AUTONOMOUS_GOAL / EVENT.
    /// - CANONICAL: vanilla baseline control (FollowingPlayer=false).
    /// - PLAYER_REQUEST: player-requested behavior lease (e.g. actively following; CurrentActivity=FOLLOW_PLAYER).
    /// - AI_AUTONOMOUS_GOAL: reserved future autonomous goal layer (NOT authorized yet).
    /// - EVENT: inside a scripted event/cutscene.</summary>
    [JsonPropertyName("behaviorOwner")]
    public required string BehaviorOwner { get; init; }

    [JsonPropertyName("currentGoal")]
    public string? CurrentGoal { get; init; }

    [JsonPropertyName("currentTarget")]
    public string? CurrentTarget { get; init; }

    [JsonPropertyName("followingPlayer")]
    public required bool FollowingPlayer { get; init; }

    [JsonPropertyName("currentEvent")]
    public string? CurrentEvent { get; init; }

    public AIWorld.Core.Chat.NpcScheduleKnowledge? Schedule { get; init; }
    public AIWorld.Core.Chat.NpcJobState? Job { get; init; }

    /// <summary>Human-readable one-line state description (for LLM context / logs).</summary>
    [JsonIgnore]
    public string Describe => $"{CharacterId} at {CurrentLocation} @ {CurrentTime}, activity={CurrentActivity}, owner={BehaviorOwner}, following={FollowingPlayer}, goal={CurrentGoal ?? "(none)"}, target={CurrentTarget ?? "(none)"}, event={CurrentEvent ?? "(none)"}"
        + (Schedule is null ? "" : $", schedule={Schedule.Key}, phase={Schedule.Phase}, route={Schedule.Executing?.Time}:{Schedule.Executing?.Location}:{Schedule.Executing?.X},{Schedule.Executing?.Y}:{Schedule.Executing?.Behavior}, due={Schedule.DuePlan?.Time}")
        + (Job is null ? "" : ", job=" + Job.Fingerprint);
}
