using AIWorld.Core.Behaviors;
using Microsoft.Xna.Framework;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// ABIGAIL BEHAVIOR LIFECYCLE v0.2 — StardewBaselineResolver.
///
/// Bridges the deterministic Core BaselineResolver to the live Stardew world: extracts an
/// NPC's current vanilla schedule snapshot (NPC.Schedule — the canonical baseline), plus its
/// DefaultMap/DefaultPosition fallback, and resolves where the NPC should be RIGHT NOW.
///
/// Mirrors upstream Squad GetCurrentScheduleEntryFor semantics (largest schedule time <=
/// current time), so the baseline target is exactly what vanilla considers "her place now".
/// The vanilla schedule is NEVER modified or overridden.
/// </summary>
public static class StardewBaselineResolver
{
    public static BaselineResolver.BaselineTarget Resolve(string npcId, int timeOfDay)
    {
        var npc = Game1.getCharacterFromName(npcId);
        if (npc is null)
            return new BaselineResolver.BaselineTarget(npcId, 0, 0, "current");

        var scheduleEntries = new List<BaselineResolver.ScheduleEntry>();
        var schedule = npc.Schedule;
        if (schedule is not null)
        {
            foreach (var pair in schedule)
            {
                // pair: KeyValuePair<int, SchedulePathDescription>
                var entry = pair.Value;
                if (entry is null) continue;
                scheduleEntries.Add(new BaselineResolver.ScheduleEntry(
                    pair.Key,
                    entry.targetLocationName ?? npc.DefaultMap,
                    entry.targetTile.X,
                    entry.targetTile.Y));
            }
        }

        var defaultPos = npc.DefaultPosition;
        var defaultTile = defaultPos == Vector2.Zero
            ? new Point(npc.TilePoint.X, npc.TilePoint.Y)
            : new Point((int)(defaultPos.X / 64f), (int)(defaultPos.Y / 64f));

        var current = npc.currentLocation;

        return BaselineResolver.ResolveBaseline(
            npcId,
            scheduleEntries,
            timeOfDay,
            npc.DefaultMap ?? current?.NameOrUniqueName ?? npcId,
            defaultTile.X,
            defaultTile.Y,
            current?.NameOrUniqueName,
            npc.TilePoint.X,
            npc.TilePoint.Y);
    }
}
