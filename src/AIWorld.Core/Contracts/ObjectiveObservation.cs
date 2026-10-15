using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// ObjectiveObservation — deterministic record of an objectively observable world event
/// (contracts-draft.md §2.4, frozen v0.2). Producer: ObservationCollector (game event
/// listeners + adapter polling). Consumer: Memory Consolidation (input for MemoryCandidate),
/// CharacterKnowledge updater, QA.
///
/// Phase 5 rules (PM §10): observations describe ONLY system-observable facts — never
/// interpretation ("Abigail became scared" is interpretation/memory, not observation).
/// Immutable after creation; only A4 may compact/clean (per-NPC FIFO cap).
/// </summary>
public sealed class ObjectiveObservation
{
    [JsonPropertyName("observationId")]
    public required string ObservationId { get; init; }

    /// <summary>Contract §2.4 whitelist: LOCATION_VISITED / DANGER_ENCOUNTER / PLAYER_INJURED /
    /// NPC_INJURED / VALUABLE_DISCOVERY / ITEM_GIFTED / EVENT_TRIGGERED / TIME_SPENT_TOGETHER /
    /// FOLLOW_STARTED / FOLLOW_ENDED. (COMBAT is NOT in the frozen whitelist — DANGER_ENCOUNTER covers
    /// danger at Demo granularity; see PHASE_5_SCOPE_EXTRACTION D1.)</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("npcId")]
    public required string NpcId { get; init; }

    /// <summary>Game date, shared GameTimeStamp format "{season}-{day}-{year}".</summary>
    [JsonPropertyName("gameDate")]
    public required string GameDate { get; init; }

    [JsonPropertyName("timeOfDay")]
    public required int TimeOfDay { get; init; }

    [JsonPropertyName("location")]
    public required string Location { get; init; }

    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    /// <summary>Per-type payload: playerHealth / dangerLevel / itemsFound etc. (validated per type).</summary>
    [JsonPropertyName("payload")]
    public Dictionary<string, JsonElement>? Payload { get; init; }

    /// <summary>Optional: links this observation to a FollowSession.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

/// <summary>Observation type constants (contract §2.4 whitelist).</summary>
public static class ObservationTypes
{
    public const string LocationVisited = "LOCATION_VISITED";
    public const string DangerEncounter = "DANGER_ENCOUNTER";
    public const string PlayerInjured = "PLAYER_INJURED";
    public const string NpcInjured = "NPC_INJURED";
    public const string ValuableDiscovery = "VALUABLE_DISCOVERY";
    public const string ItemGifted = "ITEM_GIFTED";
    public const string EventTriggered = "EVENT_TRIGGERED";
    public const string TimeSpentTogether = "TIME_SPENT_TOGETHER";
    public const string FollowStarted = "FOLLOW_STARTED";
    public const string FollowEnded = "FOLLOW_ENDED";

    /// <summary>Travel Gate T1: NPC objective arrival (runtime-verified, never from intent acceptance).</summary>
    public const string NpcTravelArrived = "NPC_TRAVEL_ARRIVED";

    /// <summary>The full frozen whitelist (§2.4) + Travel T1 extension (NPC_TRAVEL_ARRIVED).</summary>
    public static readonly IReadOnlySet<string> Whitelist = new HashSet<string>(StringComparer.Ordinal)
    {
        LocationVisited, DangerEncounter, PlayerInjured, NpcInjured, ValuableDiscovery,
        ItemGifted, EventTriggered, TimeSpentTogether, FollowStarted, FollowEnded,
        NpcTravelArrived,
    };

    public static bool IsValid(string type) => Whitelist.Contains(type);
}
