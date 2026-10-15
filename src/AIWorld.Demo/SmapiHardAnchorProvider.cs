using AIWorld.Core.Behaviors;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// SMAPI-backed hard anchor provider (Phase 1.5 accepted policy + Travel Gate T1 completion).
/// Blocks on the V9-audited INDEPENDENT anchors:
///   - event/cutscene: Game1.CurrentEvent (global) — SOURCE_VERIFIED
///   - location-scoped event: GameLocation.currentEvent (the NPC's current location) —
///     SOURCE_VERIFIED (V9 matrix: independent anchor)
///   - festival: Utility.isFestivalDay — classic public API
///   - sleep: npc.isSleeping — SOURCE_VERIFIED (V9 matrix: independent anchor)
/// Travel must NEVER receive execution authorization under any of these.
/// Unknown anchors are not guessed.
/// </summary>
public sealed class SmapiHardAnchorProvider : IHardAnchorProvider
{
    public bool IsHardAnchorActive(string npcId)
    {
        if (Game1.CurrentEvent is not null) return true;                            // global event/cutscene
        if (Utility.isFestivalDay(Game1.dayOfMonth, (Season)Game1.seasonIndex)) return true; // festival
        var npc = Game1.getCharacterFromName(npcId);
        if (npc is not null)
        {
            if (npc.isSleeping.Value) return true;                                   // sleeping (independent anchor)
            if (npc.currentLocation?.currentEvent is not null) return true;          // location-scoped event
        }
        return false;
    }

    public string? ActiveAnchor(string npcId)
    {
        if (Game1.CurrentEvent is not null) return "EVENT/CUTSCENE";
        if (Utility.isFestivalDay(Game1.dayOfMonth, (Season)Game1.seasonIndex)) return "FESTIVAL";
        var npc = Game1.getCharacterFromName(npcId);
        if (npc is not null)
        {
            if (npc.isSleeping.Value) return "SLEEP";
            if (npc.currentLocation?.currentEvent is not null) return "LOCATION_EVENT";
        }
        return null;
    }
}
