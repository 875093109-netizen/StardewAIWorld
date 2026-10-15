using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// ABIGAIL BEHAVIOR LIFECYCLE v0.2 — BaselineResolver.
///
/// Resolves an NPC's CURRENT canonical baseline: the position/state the vanilla Stardew
/// schedule says the NPC should be in RIGHT NOW. This is the deterministic target for
/// return-to-baseline after an AI behavior lease ends.
///
/// PM invariant: LLM decides WHAT/WHY; C#/SMAPI decides CAN/WHEN/HOW. The canonical Stardew
/// schedule remains the baseline/fallback. We never delete or globally override an NPC's
/// vanilla schedule.
///
/// This class is the deterministic CORE side (no game dependency): it consumes a
/// schedule-snapshot abstraction supplied by the caller and produces a baseline target.
/// The Demo side supplies the live schedule source (Stardew NPC.Schedule / DefaultMap).
/// </summary>
public sealed class BaselineResolver
{
    /// <summary>Owner of the NPC's behavior at rest (before any AI lease).</summary>
    public const string OwnerCanonical = "CANONICAL";

    /// <summary>Owner during a player-requested behavior lease (e.g. actively following the
    /// player — CurrentActivity=FOLLOW_PLAYER). PM FIX 2 2026-08-12.</summary>
    public const string OwnerPlayerRequest = "PLAYER_REQUEST";

    /// <summary>Owner while inside a scripted event/cutscene (hard anchor). PM FIX 2 2026-08-12.</summary>
    public const string OwnerEvent = "EVENT";

    /// <summary>Future owner (not implemented in v0.2; reserved).</summary>
    public const string OwnerAutonomousGoal = "AI_AUTONOMOUS_GOAL";

    public sealed record BaselineTarget(
        string LocationName,
        int TileX,
        int TileY,
        string Source); // "schedule" | "default" | "current"

    /// <summary>A single schedule entry: time-of-day gate + destination.</summary>
    public sealed record ScheduleEntry(int TimeOfDay, string LocationName, int TileX, int TileY);

    /// <summary>
    /// Resolve the canonical baseline for an NPC given its schedule snapshot.
    /// Mirrors upstream Squad GetCurrentScheduleEntryFor semantics: pick the entry with the
    /// largest TimeOfDay &lt;= currentTime; fall back to the NPC's default position.
    /// </summary>
    public static BaselineTarget ResolveBaseline(
        string npcId,
        IReadOnlyList<ScheduleEntry> schedule,
        int currentTimeOfDay,
        string defaultLocation,
        int defaultTileX,
        int defaultTileY,
        string? currentLocation = null,
        int currentTileX = 0,
        int currentTileY = 0)
    {
        ScheduleEntry? best = null;
        foreach (var e in schedule)
        {
            if (e.TimeOfDay <= currentTimeOfDay && (best is null || e.TimeOfDay > best.TimeOfDay))
                best = e;
        }

        if (best is not null)
            return new BaselineTarget(best.LocationName, best.TileX, best.TileY, "schedule");

        // No schedule entry applies: canonical baseline is the NPC's default home position.
        if (!string.IsNullOrEmpty(defaultLocation))
            return new BaselineTarget(defaultLocation, defaultTileX, defaultTileY, "default");

        // No default known either: stay where the NPC currently is (no teleport — the safest
        // fallback is to leave the NPC where they stand).
        return new BaselineTarget(currentLocation ?? npcId, currentTileX, currentTileY, "current");
    }

    /// <summary>True when the NPC is already at the baseline target (no movement needed).</summary>
    public static bool IsAtBaseline(BaselineTarget target, string currentLocation, int currentTileX, int currentTileY)
        => string.Equals(target.LocationName, currentLocation, StringComparison.OrdinalIgnoreCase)
           && target.TileX == currentTileX
           && target.TileY == currentTileY;
}
