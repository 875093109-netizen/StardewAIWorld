using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// Travel Gate T1 — objective NPC travel arrival observation producer.
///
/// Produces NPC_TRAVEL_ARRIVED (contract §2.4 whitelist extension, PM §9):
///   - characterId (NpcId)
///   - actual location (runtime-verified — the NPC's real currentLocation at observation time)
///   - target location (the canonical travel destination)
///   - arrival result (arrived / failed)
///   - occurredAt (gameDate + timeOfDay)
///   - source/provenance (runtime arrival predicate, never intent acceptance)
///
/// The observation is created ONLY from a verified runtime arrival (the handler's
/// OnTravelArrived hook fires after OBJECTIVE_ARRIVAL). Intent acceptance alone can never
/// produce it; the player's LOCATION_VISITED (Warped) is never reused for NPC arrival.
/// </summary>
public sealed class TravelObservationSource
{
    private readonly IMonitor _monitor;
    private readonly ObservationCollector _collector;
    private int _recorded;

    public TravelObservationSource(IMonitor monitor, ObservationCollector collector)
    {
        _monitor = monitor;
        _collector = collector;
    }

    public int Recorded => _recorded;

    /// <summary>Wire this to TravelIntentHandler.OnTravelArrived.</summary>
    public void OnNpcArrived(string npcId, TravelDestination destination, string gameStamp)
    {
        var (date, tod) = SplitStamp(gameStamp);
        var actualLocation = Game1.getCharacterFromName(npcId)?.currentLocation?.NameOrUniqueName ?? "unknown";
        var observation = new ObjectiveObservation
        {
            ObservationId = Guid.NewGuid().ToString("N"),
            Type = ObservationTypes.NpcTravelArrived,
            NpcId = npcId,
            GameDate = date,
            TimeOfDay = tod,
            Location = actualLocation,
            SchemaVersion = "1",
            Payload = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["targetLocation"] = System.Text.Json.JsonSerializer.SerializeToElement(destination.LocationName),
                ["arrivalResult"] = System.Text.Json.JsonSerializer.SerializeToElement("arrived"),
                ["source"] = System.Text.Json.JsonSerializer.SerializeToElement("runtime-arrival-predicate"),
            },
        };

        var result = _collector.Record(observation);
        if (!result.Accepted)
        {
            _monitor.Log($"[T1] TRAVEL_OBS_REJECTED npc={npcId} err={result.Error}", LogLevel.Warn);
            return;
        }
        _recorded++;
        _monitor.Log($"[T1] TRAVEL_OBSERVATION npc={npcId} type=NPC_TRAVEL_ARRIVED actual={actualLocation} target={destination.LocationName} src=runtime-arrival-predicate", LogLevel.Info);
    }

    private static (string Date, int TimeOfDay) SplitStamp(string stamp)
    {
        // stamp form: {season}-{day}-{year}@{HHMM}
        var at = stamp.IndexOf('@');
        if (at > 0
            && int.TryParse(stamp[(at + 1)..], out var tod))
            return (stamp[..at], tod);
        try
        {
            return ($"{Game1.currentSeason}-{Game1.dayOfMonth}-{Game1.year}", Game1.timeOfDay);
        }
        catch
        {
            return ("unknown-date", 600);
        }
    }
}
