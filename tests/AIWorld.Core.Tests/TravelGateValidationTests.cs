using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Pipeline;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// Travel Gate T1 — capability + semantic validation layer tests.
/// Covers: capability registration, live-registry target validation (U5/U6),
/// invented-location rejection, unregistered-capability rejection (the
/// UnsupportedActionAuthorization guard), and follow-path non-regression.
/// </summary>
public class TravelGateValidationTests
{
    private static NpcIntent TravelIntent(string target, string? type = null) => new()
    {
        IntentId = Guid.NewGuid().ToString("N"),
        Type = type ?? CapabilityRegistry.CapabilityIds.TravelToLocation,
        Target = target,
        Priority = 0.7f,
        SchemaVersion = "1",
        Reason = "test",
    };

    private static NpcStructuredResponse WithIntent(NpcIntent intent) => new()
    {
        Speech = "ok",
        Intent = intent,
        SchemaVersion = "1",
    };

    // T1-01: legal target passes semantic validation against a live registry.
    [Fact]
    public void T1_01_LegalTarget_Valid()
    {
        var live = new[] { "SeedShop", "Saloon", "Town", "Beach", "Mine" };
        var v = StructuredResponseSemanticValidator.CreateLiveForLocations(live);
        var outcome = v.Validate(WithIntent(TravelIntent("Beach")));
        Assert.True(outcome.Valid);
        Assert.Empty(outcome.Errors);
    }

    // T1-02: invented / unknown target rejected at semantic layer (never reaches adapter).
    [Fact]
    public void T1_02_InventedTarget_Rejected()
    {
        var live = new[] { "SeedShop", "Saloon", "Town" };
        var v = StructuredResponseSemanticValidator.CreateLiveForLocations(live);
        var outcome = v.Validate(WithIntent(TravelIntent("Atlantis")));
        Assert.False(outcome.Valid);
        Assert.Contains(outcome.Errors, e => e.Contains("does not resolve"));
    }

    // T1-21: schema layer — capability must be registered (schema uses capabilityId).
    [Fact]
    public void T1_21_UnregisteredCapability_Rejected()
    {
        var v = StructuredResponseSemanticValidator.CreateLiveForLocations(new[] { "Town" });
        var intent = TravelIntent("Town", type: "travel.to_mars");
        var outcome = v.Validate(WithIntent(intent));
        Assert.False(outcome.Valid);
        Assert.Contains(outcome.Errors, e => e.Contains("not in Phase 2 registry"));
    }

    // T1-23: unsupported-action regression — a NON-travel/non-follow capability type must
    // never pass semantic validation (the UnsupportedActionAuthorization guard class).
    [Fact]
    public void T1_23_UnsupportedActionType_Rejected()
    {
        var v = StructuredResponseSemanticValidator.CreateLiveForLocations(new[] { "Town" });
        var intent = TravelIntent("Town", type: "combat.attack_monster"); // unregistered capability
        var outcome = v.Validate(WithIntent(intent));
        Assert.False(outcome.Valid);
        Assert.Contains(outcome.Errors, e => e.Contains("not in Phase 2 registry"));
    }

    // Capability registry: travel.to_location registered AVAILABLE with NATIVE backend.
    [Fact]
    public void Travel_Capability_Registered_Native()
    {
        var reg = CapabilityRegistry.CreateDemoRegistry();
        var cap = reg.Get(CapabilityRegistry.CapabilityIds.TravelToLocation);
        Assert.NotNull(cap);
        Assert.Equal("AVAILABLE", cap!.Availability);
        Assert.Equal("NATIVE", cap.Backend);
        Assert.True(reg.HasExecutableBackend(CapabilityRegistry.CapabilityIds.TravelToLocation));
    }

    // Live-registry factory: duplicates deduped, extras appended.
    [Fact]
    public void Travel_LiveFactory_DedupesAndExtends()
    {
        var v = StructuredResponseSemanticValidator.CreateLiveForLocations(
            new[] { "Town", "Town" }, extraLocations: new[] { "Beach" });
        // private — validate through behavior: Beach accepted, invented rejected.
        Assert.True(v.Validate(WithIntent(TravelIntent("Beach"))).Valid);
        Assert.False(v.Validate(WithIntent(TravelIntent("Narnia"))).Valid);
    }

    // Follow non-regression: follow_player target=Player still valid under the live factory.
    [Fact]
    public void Travel_LiveFactory_FollowStillValid()
    {
        var v = StructuredResponseSemanticValidator.CreateLiveForLocations(new[] { "Town" });
        var intent = new NpcIntent
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Type = CapabilityRegistry.CapabilityIds.FollowPlayer,
            Target = "Player",
            Priority = 0.5f,
            SchemaVersion = "1",
        };
        Assert.True(v.Validate(WithIntent(intent)).Valid);
    }
}
