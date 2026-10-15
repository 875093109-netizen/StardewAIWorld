using AIWorld.Core.Behaviors;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Pathfinding;

namespace AIWorld.Demo;

/// <summary>Read-only, main-thread movement rules shared by travel, follow and rejoin.
/// Unknown door actions are denied; this never invokes player tile actions to probe access.</summary>
public static class NpcMovementRules
{
    public static bool CanEnter(NPC npc, GameLocation? location) => location is not null
        && location.currentEvent is null && !location.ShouldExcludeFromNpcPathfinding()
        && (!WarpPathfindingCache.GenderRestrictions.TryGetValue(location.NameOrUniqueName, out var gender) || gender == npc.Gender);

    public static bool CanStand(NPC npc, GameLocation? location, int x, int y)
    {
        if (location is null || !location.isTileOnMap(x, y) || location.isWaterTile(x, y)) return false;
        if (location.doesTileHaveProperty(x, y, "NoPath", "Back") is not null
            || location.doesTileHaveProperty(x, y, "NPCBarrier", "Back") is not null
            || location.doesTileHaveProperty(x, y, "NPCBarrier", "Buildings") is not null) return false;
        // Native collision includes objects, furniture, terrain and map properties. Read-only probe.
        return !location.isCollidingPosition(new Rectangle(x * 64 + 1, y * 64 + 1, 62, 62), Game1.viewport,
            false, 0, false, npc, pathfinding: true, skipCollisionEffects: true);
    }

    public static IReadOnlyList<TravelWarpQuery> Connections(NPC npc)
    {
        var result = new List<TravelWarpQuery>();
        Utility.ForEachLocation(location => { result.AddRange(ConnectionsFrom(npc, location)); return true; },
            includeInteriors: true, includeGenerated: false);
        return result.Distinct().ToArray();
    }

    public static IEnumerable<TravelWarpQuery> ConnectionsFrom(NPC npc, GameLocation source)
    {
        foreach (var warp in source.warps)
            if (ConnectionAllowed(npc, source, warp, isDoor: false)) yield return Query(source, warp);

        var doors = source.doors.Pairs.Select(p => p.Key)
            .Concat(source.buildings.Where(b => b.HasIndoors()).Select(b => b.getPointForHumanDoor())).Distinct();
        foreach (var point in doors)
        {
            // Actor-aware engine API handles building interiors and character-specific door destinations.
            var warp = source.getWarpFromDoor(point, npc);
            if (warp is not null && ConnectionAllowed(npc, source, warp, isDoor: true)) yield return Query(source, warp);
        }
    }

    public static bool StillConnected(NPC npc, TravelRouteHop hop) =>
        Game1.getLocationFromName(hop.SourceLocationName) is { } source && ConnectionsFrom(npc, source).Any(w =>
            w.X == hop.ExitX && w.Y == hop.ExitY && w.TargetName == hop.TargetLocationName
            && w.TargetX == hop.EntryX && w.TargetY == hop.EntryY);

    private static TravelWarpQuery Query(GameLocation source, Warp w) =>
        new(source.NameOrUniqueName, w.X, w.Y, w.TargetName, w.TargetX, w.TargetY);

    private static bool ConnectionAllowed(NPC npc, GameLocation source, Warp warp, bool isDoor)
    {
        var target = Game1.getLocationFromName(warp.TargetName);
        if (!CanEnter(npc, target) || !CanStand(npc, target, warp.TargetX, warp.TargetY)) return false;
        var action = source.doesTileHaveProperty(warp.X, warp.Y, "Action", "Buildings");
        if (string.IsNullOrWhiteSpace(action)) return !isDoor || source.buildings.Any(b =>
            b.HasIndoors() && b.getPointForHumanDoor() == new Point(warp.X, warp.Y)
            && b.GetIndoors()?.NameOrUniqueName == warp.TargetName);
        var words = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (words[0])
        {
            case "Warp": return true;
            case "WarpMensLocker": return npc.Gender == Gender.Male;
            case "WarpWomensLocker": return npc.Gender == Gender.Female;
            case "LockedDoorWarp":
                if (words.Length < 6 || !int.TryParse(words[4], out var open) || !int.TryParse(words[5], out var close)) return false;
                // Residents and today's scheduled visitors have NPC-specific authority; never borrow player friendship/keys.
                bool scheduled = npc.DefaultMap == warp.TargetName || npc.Schedule?.Values.Any(s => s.targetLocationName == warp.TargetName) == true;
                if (scheduled) return true;
                if (words.Length > 7 && (!int.TryParse(words[7], out var friendship) || friendship > 0)) return false;
                return Game1.timeOfDay >= open && Game1.timeOfDay < close && !GameLocation.AreStoresClosedForFestival();
            case "WarpCommunityCenter":
                return Game1.MasterPlayer.mailReceived.Contains("ccDoorUnlock") || Game1.MasterPlayer.mailReceived.Contains("JojaMember");
            default: return false; // Custom/conditional transport needs explicit game/mod evidence.
        }
    }
}
