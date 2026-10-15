using AIWorld.Core.Behaviors;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// Travel Gate T1 — destination resolver tests (deterministic layer).
/// U2 semantics: missing/unloaded location → Unavailable (REJECT/UNAVAILABLE, never
/// attempt-load); no source-grounded arrival tile → NoArrivalTile (REJECT, no guessing).
/// R5: resolution is ACTOR-SCOPED — the default-position fallback uses the travelling
/// NPC's OWN default home, never a hardcoded named NPC.
/// </summary>
public class TravelDestinationResolverTests
{
    private const string Abigail = "Abigail";

    private static DeterministicTravelDestinationResolver Resolver() => new(
        entries: new[]
        {
            ("SeedShop", 4, 4, "inbound-warp"),
            ("Saloon", 9, 8, "inbound-warp"),
            ("Beach", 20, 4, "inbound-warp"),
        },
        unavailable: new[] { "Mine" });

    // T1-03: unavailable target → Unavailable (never attempt-load).
    [Fact]
    public void T1_03_UnavailableTarget_Unavailable()
    {
        var r = Resolver().Resolve(Abigail, "Mine");
        Assert.Equal(TravelDestinationStatus.Unavailable, r.Status);
        Assert.Null(r.Destination);
        Assert.NotNull(r.Error);
    }

    // T1-11 (resolution layer): known target → resolved with evidence source.
    [Fact]
    public void T1_11_CrossLocation_ResolvedWithEvidence()
    {
        var r = Resolver().Resolve(Abigail, "Beach");
        Assert.Equal(TravelDestinationStatus.Resolved, r.Status);
        Assert.NotNull(r.Destination);
        Assert.Equal("Beach", r.Destination!.LocationName);
        Assert.Equal(20, r.Destination.TileX);
        Assert.Equal(4, r.Destination.TileY);
        Assert.Equal("inbound-warp", r.Destination.EvidenceSource);
    }

    // Unknown location (not in the game at all) → NoArrivalTile (U2: no guessing).
    [Fact]
    public void T1_UnknownLocation_NoArrivalTile()
    {
        var r = Resolver().Resolve(Abigail, "Atlantis");
        Assert.Equal(TravelDestinationStatus.NoArrivalTile, r.Status);
        Assert.Null(r.Destination);
    }

    // Deterministic resolver must never guess coordinates for an unknown target.
    [Fact]
    public void T1_NoCoordinateGuessing_ForUnknown()
    {
        var r = Resolver().Resolve(Abigail, "Narnia");
        Assert.NotEqual(TravelDestinationStatus.Resolved, r.Status);
        Assert.Null(r.Destination);
    }

    // Status→decision mapping: Resolved authorizes, Unavailable/NoArrivalTile reject.
    [Fact]
    public void T1_ResolutionStatus_DecisionMapping()
    {
        Assert.Equal(TravelDestinationStatus.Resolved, Resolver().Resolve(Abigail, "SeedShop").Status);
        Assert.NotEqual(TravelDestinationStatus.Resolved, Resolver().Resolve(Abigail, "Mine").Status);
        Assert.NotEqual(TravelDestinationStatus.Resolved, Resolver().Resolve(Abigail, "Nowhere").Status);
    }

    // ── R5: actor-scoped default-position fallback (never a hardcoded NPC) ──

    private static DeterministicTravelDestinationResolver ActorResolver() => new(
        entries: new[] { ("SeedShop", 4, 4, "inbound-warp") },
        unavailable: null,
        defaultHomes: new[]
        {
            ("Harvey", "Clinic", 5, 3),    // Harvey's own home
            ("Sebastian", "SebastianRoom", 5, 5), // Sebastian's own home
            ("ModNpc", "ModHouse", 8, 8),  // a mod-added NPC's home
        });

    // Harvey's travel to HIS OWN home resolves via his default position (no inbound warp
    // in the entries table for Clinic).
    [Fact]
    public void R5_Harvey_OwnHome_FallbackResolves()
    {
        var r = ActorResolver().Resolve("Harvey", "Clinic");
        Assert.Equal(TravelDestinationStatus.Resolved, r.Status);
        Assert.Equal("Clinic", r.Destination!.LocationName);
        Assert.Equal(5, r.Destination.TileX);
        Assert.Equal(3, r.Destination.TileY);
        Assert.Equal("default-position", r.Destination.EvidenceSource);
    }

    // Mod NPC's own home fallback resolves (non-vanilla NPC, R5 non-Abigail coverage).
    [Fact]
    public void R5_ModNpc_OwnHome_FallbackResolves()
    {
        var r = ActorResolver().Resolve("ModNpc", "ModHouse");
        Assert.Equal(TravelDestinationStatus.Resolved, r.Status);
        Assert.Equal("ModHouse", r.Destination!.LocationName);
        Assert.Equal(8, r.Destination.TileX);
        Assert.Equal(8, r.Destination.TileY);
        Assert.Equal("default-position", r.Destination.EvidenceSource);
    }

    // Actor isolation: Harvey's home is NOT resolvable for Sebastian (or Abigail) — the
    // fallback is scoped to the travelling actor.
    [Fact]
    public void R5_AnotherActorsHome_DoesNotResolveForThisActor()
    {
        var r = ActorResolver().Resolve("Sebastian", "Clinic"); // Clinic is Harvey's home
        Assert.NotEqual(TravelDestinationStatus.Resolved, r.Status);
        Assert.Null(r.Destination);

        var rAbigail = ActorResolver().Resolve("Abigail", "Clinic");
        Assert.NotEqual(TravelDestinationStatus.Resolved, rAbigail.Status);
    }

    // No default home and no table entry → NoArrivalTile (never guesses).
    [Fact]
    public void R5_NoDefaultHome_NoArrivalTile()
    {
        var r = ActorResolver().Resolve("Harvey", "SecretWoods"); // not Harvey's home, not in table
        Assert.Equal(TravelDestinationStatus.NoArrivalTile, r.Status);
        Assert.Null(r.Destination);
    }
}
