using AIWorld.Core.Behaviors;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>Uses real permitted incoming connections; never synthesizes a reverse exit.</summary>
public sealed class StardewTravelDestinationResolver : ITravelDestinationResolver
{
    public StardewTravelDestinationResolver(IMonitor monitor) { }
    public TravelDestinationResult Resolve(string actorNpcId, string canonicalLocation)
    {
        try
        {
            var npc = Game1.getCharacterFromName(actorNpcId);
            var location = Game1.getLocationFromName(canonicalLocation);
            if (npc is null || !NpcMovementRules.CanEnter(npc, location))
                return TravelDestinationResult.Fail(TravelDestinationStatus.Unavailable, "location unavailable or restricted for this NPC");
            foreach (var connection in NpcMovementRules.Connections(npc).Where(w => w.TargetName == canonicalLocation))
                return TravelDestinationResult.Ok(new(canonicalLocation, connection.TargetX, connection.TargetY, "live-entry", connection.Detail));
            int x = (int)(npc.DefaultPosition.X / 64f), y = (int)(npc.DefaultPosition.Y / 64f);
            if (npc.DefaultMap == canonicalLocation && npc.DefaultPosition != Microsoft.Xna.Framework.Vector2.Zero && NpcMovementRules.CanStand(npc, location, x, y))
                return TravelDestinationResult.Ok(new(canonicalLocation, x, y, "default-position", actorNpcId));
            return TravelDestinationResult.Fail(TravelDestinationStatus.NoArrivalTile, "no legal game-defined entry or actor default position");
        }
        catch (Exception) { return TravelDestinationResult.Fail(TravelDestinationStatus.Unavailable, "movement data unavailable"); }
    }
}
