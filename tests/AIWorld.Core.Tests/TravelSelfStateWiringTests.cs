using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// Travel Gate R3 — Travel SelfState production wiring.
/// Offline/deterministic (REAL_LLM_CALL_BUDGET=0): drives the SAME wiring closure shape
/// ModEntry.cs uses (TravelLeaseManager → isTraveling/travelTarget) through the lease
/// lifecycle and asserts the shared SelfStateDecider maps each stage to the frozen
/// PLAYER_REQUEST / CANONICAL / EVENT states. There is NO TRAVEL_TO_LOCATION activity
/// (T1 CLARIFICATION — travel reuses PLAYER_REQUEST).
/// </summary>
public class TravelSelfStateWiringTests
{
    // Mirror of the ModEntry.cs production wiring closures.
    private static (Func<string, bool> IsTraveling, Func<string, string> TravelTarget) ProductionWiring(TravelLeaseManager leases)
    {
        bool IsTraveling(string npcId) => leases.IsActive(npcId);
        string TravelTarget(string npcId)
        {
            var lease = leases.GetActive(npcId);
            if (lease?.AdapterParams?.TryGetValue("locationName", out var v) == true && v is string loc)
                return loc;
            return string.Empty;
        }
        return (IsTraveling, TravelTarget);
    }

    private static TravelLeaseManager NewLeases() => new();

    private static TravelLeaseManager Acquire(TravelLeaseManager leases, string npcId, string destination)
    {
        var lease = leases.Acquire(
            npcId, CapabilityRegistry.CapabilityIds.TravelToLocation, "spring-10-1@1000",
            previousOwner: "player",
            destination: new TravelDestination(destination, 15, 12, "inbound-warp", "test"));
        Assert.NotNull(lease);
        return leases;
    }

    // Active lease → isTraveling true + travelTarget = canonical destination → PLAYER_REQUEST.
    [Fact]
    public void R3_ActiveLease_MapsToPlayerRequest()
    {
        var leases = Acquire(NewLeases(), "Abigail", "SeedShop");
        var (isTraveling, travelTarget) = ProductionWiring(leases);

        Assert.True(isTraveling("Abigail"));
        Assert.Equal("SeedShop", travelTarget("Abigail"));

        var d = SelfStateDecider.Decide(inEvent: false, following: false, traveling: true);
        Assert.Equal(CurrentActivity.PLAYER_REQUEST, d.Activity);
        Assert.Equal(BaselineResolver.OwnerPlayerRequest, d.Owner);
    }

    // Arrival (OnTravelArrived → Release) → traveling false → CANONICAL.
    [Fact]
    public void R3_Arrival_ReleasesToCanonical()
    {
        var leases = Acquire(NewLeases(), "Abigail", "SeedShop");
        var (isTraveling, travelTarget) = ProductionWiring(leases);

        Assert.NotNull(leases.Release("Abigail")); // arrival path
        Assert.False(isTraveling("Abigail"));
        Assert.Equal(string.Empty, travelTarget("Abigail"));

        var d = SelfStateDecider.Decide(inEvent: false, following: false, traveling: false);
        Assert.Equal(CurrentActivity.CANONICAL, d.Activity);
        Assert.Equal(BaselineResolver.OwnerCanonical, d.Owner);
    }

    // Explicit cancel → Release → CANONICAL (same invariant as arrival).
    [Fact]
    public void R3_Cancel_ReleasesToCanonical()
    {
        var leases = Acquire(NewLeases(), "Abigail", "SeedShop");
        var (isTraveling, _) = ProductionWiring(leases);

        Assert.NotNull(leases.Release("Abigail")); // EXPLICIT_CANCEL path
        Assert.False(isTraveling("Abigail"));
        Assert.Equal(CurrentActivity.CANONICAL, SelfStateDecider.Decide(false, false, isTraveling("Abigail")).Activity);
    }

    // Timeout → Release → CANONICAL (same invariant).
    [Fact]
    public void R3_Timeout_ReleasesToCanonical()
    {
        var leases = Acquire(NewLeases(), "Abigail", "SeedShop");
        var (isTraveling, _) = ProductionWiring(leases);

        Assert.NotNull(leases.Release("Abigail")); // TIMEOUT path
        Assert.False(isTraveling("Abigail"));
        Assert.Equal(CurrentActivity.CANONICAL, SelfStateDecider.Decide(false, false, isTraveling("Abigail")).Activity);
    }

    // Hard-anchor takeover (inEvent) wins even while a lease is active → EVENT.
    [Fact]
    public void R3_HardAnchorEvent_WinsOverTravel()
    {
        var leases = Acquire(NewLeases(), "Abigail", "SeedShop");
        var (isTraveling, _) = ProductionWiring(leases);
        Assert.True(isTraveling("Abigail"));

        var d = SelfStateDecider.Decide(inEvent: true, following: false, traveling: true);
        Assert.Equal(CurrentActivity.EVENT, d.Activity);
        Assert.Equal(BaselineResolver.OwnerEvent, d.Owner);
    }

    // NPC scoping: Abigail's lease must not alter Sebastian's travel state.
    [Fact]
    public void R3_NpcA_Travel_DoesNotAlterNpcB()
    {
        var leases = Acquire(NewLeases(), "Abigail", "SeedShop");
        var (isTraveling, travelTarget) = ProductionWiring(leases);

        Assert.True(isTraveling("Abigail"));
        Assert.False(isTraveling("Sebastian"));
        Assert.Equal(string.Empty, travelTarget("Sebastian"));
        Assert.Equal("SeedShop", travelTarget("Abigail")); // A unchanged after reading B
    }

    // T1 CLARIFICATION: no TRAVEL_TO_LOCATION activity value exists — travel reuses PLAYER_REQUEST.
    [Fact]
    public void R3_NoTravelToLocationActivity_EnumValueAbsent()
    {
        Assert.False(Enum.IsDefined(typeof(CurrentActivity), "TRAVEL_TO_LOCATION"));
    }
}
