using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// Travel Gate T1 (R3) — frozen SelfState decision, ONE shared truth for the production
/// SelfStateProvider and the offline wiring tests. World Truth flags → activity + owner.
/// Travel reuses PLAYER_REQUEST (no CurrentActivity extension per T1 CLARIFICATION — there
/// is NO TRAVEL_TO_LOCATION activity). EVENT is the hardest anchor and wins over everything.
/// </summary>
public static class SelfStateDecider
{
    public readonly record struct Outcome(CurrentActivity Activity, string Owner, bool InEvent);

    public static Outcome Decide(bool inEvent, bool following, bool traveling)
    {
        if (inEvent) return new Outcome(CurrentActivity.EVENT, BaselineResolver.OwnerEvent, true);
        if (following) return new Outcome(CurrentActivity.FOLLOW_PLAYER, BaselineResolver.OwnerPlayerRequest, false);
        if (traveling) return new Outcome(CurrentActivity.PLAYER_REQUEST, BaselineResolver.OwnerPlayerRequest, false);
        return new Outcome(CurrentActivity.CANONICAL, BaselineResolver.OwnerCanonical, false);
    }
}
