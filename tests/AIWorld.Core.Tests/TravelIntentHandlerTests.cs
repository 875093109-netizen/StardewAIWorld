using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// Travel Gate T1 — travel handler lifecycle tests (deterministic, offline).
/// Covers PM T1-04..T1-20: already-there, hard anchors, same/cross-location (via fake),
/// path failure, arrival timeout, adapter failure, lease activation/release, no stale
/// lease, canonical rejoin, cross-character isolation.
/// </summary>
public class TravelIntentHandlerTests
{
    private const string Abigail = "Abigail";
    private const string Harvey = "Harvey";

    private sealed class Harness
    {
        public FakeTravelAdapter Adapter { get; } = new();
        public TravelLeaseManager Leases { get; } = new();
        public DeterministicTravelDestinationResolver Resolver { get; }
        public BehaviorArbitrator Arbitrator { get; }
        public TravelIntentHandler Handler { get; }
        public ToggleableHardAnchorProvider Anchors { get; }
        public int ArrivedHooks { get; private set; }
        public int ReleasedHooks { get; private set; }
        public string? LastReleasedNpc { get; private set; }

        public Harness(bool anchorActive = false, bool following = false,
            IEnumerable<(string L, int X, int Y, string S)>? entries = null,
            IEnumerable<string>? unavailable = null)
        {
            var reg = CapabilityRegistry.CreateDemoRegistry();
            var adapter = new FakeFollowAdapter { Following = following };
            Anchors = new ToggleableHardAnchorProvider(anchorActive, anchorActive ? "EVENT/CUTSCENE" : null);
            var npcResolver = new DeterministicNpcResolver(Abigail, Harvey);
            var playerResolver = new DeterministicPlayerResolver("player-1");
            Resolver = new DeterministicTravelDestinationResolver(
                entries ?? new[] { ("SeedShop", 4, 4, "inbound-warp"), ("Beach", 20, 4, "inbound-warp"), ("Saloon", 9, 8, "inbound-warp") },
                unavailable ?? new[] { "Mine" });
            Arbitrator = new BehaviorArbitrator(reg, adapter, Anchors, npcResolver, playerResolver,
                travelResolver: Resolver, travelActiveCheck: npc => Leases.IsActive(npc));
            Handler = new TravelIntentHandler(Arbitrator, Resolver, Adapter, Leases);
            Handler.OnTravelArrived += (npc, dest, stamp) => ArrivedHooks++;
            Handler.OnTravelReleased += (npc, stamp) => { ReleasedHooks++; LastReleasedNpc = npc; };
        }

        public static NpcIntent Travel(string target) => new()
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Type = CapabilityRegistry.CapabilityIds.TravelToLocation,
            Target = target,
            Priority = 0.7f,
            SchemaVersion = "1",
            Reason = "test",
        };
    }

    // T1-09/T1-11: legal travel → authorized + adapter STARTED (movement initiated, NOT arrived).
    [Fact]
    public void T1_09_11_LegalTravel_Started_LeaseActive()
    {
        var h = new Harness();
        var outcome = h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        Assert.True(outcome.Arbitration.Authorized);
        Assert.True(outcome.AdapterInvoked);
        Assert.NotNull(outcome.Lease);
        Assert.Equal("ACTIVE", outcome.Lease!.Status);
        Assert.Equal("STARTED", outcome.Adapter!.Status); // movement initiated, NOT arrival
        Assert.Equal(1, h.Adapter.TravelCalls);
        Assert.True(h.Leases.IsActive(Abigail));
    }

    // T1-15: lease activation only after execution starts; failure → no lease.
    [Fact]
    public void T1_15_LeaseActivation_OnlyAfterExecutionStarts()
    {
        var h = new Harness();
        h.Adapter.TravelShouldFail = true;
        var outcome = h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        Assert.True(outcome.Arbitration.Authorized);
        Assert.True(outcome.AdapterInvoked);
        Assert.Null(outcome.Lease); // FAILED → no active lease
        Assert.Equal("FAILED", outcome.Adapter!.Status);
        Assert.False(h.Leases.IsActive(Abigail));
    }

    // T1-03: unavailable target → rejected, adapter invocation = 0.
    [Fact]
    public void T1_03_UnavailableTarget_Rejected_NoAdapter()
    {
        var h = new Harness();
        var outcome = h.Handler.Handle(Harness.Travel("Mine"), Abigail, "spring-10-1@0600");
        Assert.False(outcome.Arbitration.Authorized);
        Assert.Equal(BehaviorDecision.RejectedTargetNotFound, outcome.Arbitration.Decision);
        Assert.False(outcome.AdapterInvoked);
        Assert.Equal(0, h.Adapter.TravelCalls);
        Assert.Null(outcome.Lease);
    }

    // T1-04: already at destination → deterministic already-satisfied, no movement.
    [Fact]
    public void T1_04_AlreadyAtDestination_NoMovement()
    {
        var h = new Harness();
        h.Adapter.Arrived = true; // arrival predicate already true
        var outcome = h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        Assert.True(outcome.Arbitration.Authorized);
        Assert.True(outcome.AlreadyAtDestination);
        Assert.False(outcome.AdapterInvoked);
        Assert.Equal(0, h.Adapter.TravelCalls);
        Assert.Null(outcome.Lease);
    }

    // T1-05..T1-08: hard anchors reject travel, adapter = 0, no lease.
    [Fact]
    public void T1_05_08_HardAnchor_Rejected()
    {
        var h = new Harness(anchorActive: true);
        var outcome = h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        Assert.Equal(BehaviorDecision.RejectedHardAnchor, outcome.Arbitration.Decision);
        Assert.False(outcome.AdapterInvoked);
        Assert.Equal(0, h.Adapter.TravelCalls);
        Assert.Null(outcome.Lease);
    }

    // Conflict: active follow blocks travel (lease conflict).
    [Fact]
    public void T1_FollowActive_BlocksTravel()
    {
        var h = new Harness(following: true);
        var outcome = h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        Assert.Equal(BehaviorDecision.RejectedLeaseConflict, outcome.Arbitration.Decision);
        Assert.False(outcome.AdapterInvoked);
        Assert.Equal(0, h.Adapter.TravelCalls);
    }

    // T1-16: arrival → lease RELEASED + observation/rejoin hooks.
    [Fact]
    public void T1_16_Arrival_LeaseReleased_HooksInvoked()
    {
        var h = new Harness();
        var outcome = h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        Assert.NotNull(outcome.Lease);

        // Not arrived yet → Pending.
        var pending = h.Handler.Complete(Abigail, "spring-10-1@0601");
        Assert.Equal(TravelIntentHandler.TravelCompletionStatus.Pending, pending.Status);

        // Arrival verified → Arrived + lease released + hooks.
        h.Adapter.Arrived = true;
        var done = h.Handler.Complete(Abigail, "spring-10-1@0602");
        Assert.Equal(TravelIntentHandler.TravelCompletionStatus.Arrived, done.Status);
        Assert.False(h.Leases.IsActive(Abigail));
        Assert.Equal(1, h.ArrivedHooks);
        Assert.Equal(1, h.ReleasedHooks);
        Assert.Equal(Abigail, h.LastReleasedNpc);
    }

    // T1-13: arrival timeout → movement cancelled + lease released, no stale lease.
    [Fact]
    public void T1_13_ArrivalTimeout_NoStaleLease()
    {
        var h = new Harness();
        h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        // small budget so the test is fast
        var fastHandler = new TravelIntentHandler(h.Arbitrator, h.Resolver, h.Adapter, h.Leases, maxCompletionTicks: 3);
        fastHandler.OnTravelReleased += (npc, s) => { };

        TravelIntentHandler.TravelCompletionResult r = default!;
        for (int i = 0; i < 10; i++)
        {
            r = fastHandler.Complete(Abigail, "spring-10-1@0600");
            if (r.Status != TravelIntentHandler.TravelCompletionStatus.Pending) break;
        }
        Assert.Equal(TravelIntentHandler.TravelCompletionStatus.TimedOut, r.Status);
        Assert.False(h.Leases.IsActive(Abigail)); // released on timeout
        Assert.True(h.Adapter.CancelCalled);       // movement stopped
        Assert.Equal(0, h.Leases.ActiveCount);     // no stale lease
    }

    // T1-14: adapter failure at execution → FAILED, no lease, no false arrival.
    [Fact]
    public void T1_14_AdapterFailure_NoFalseArrival()
    {
        var h = new Harness();
        h.Adapter.TravelShouldUnavailable = true;
        var outcome = h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        Assert.True(outcome.Arbitration.Authorized);
        Assert.True(outcome.AdapterInvoked);
        Assert.Equal("UNAVAILABLE", outcome.Adapter!.Status);
        Assert.Null(outcome.Lease);
        Assert.False(h.Leases.IsActive(Abigail));
        Assert.Equal(0, h.ArrivedHooks); // no false arrival observation
    }

    // T1-17/T1-18: complete/cancel with no active lease → NotActive, no stale lease.
    [Fact]
    public void T1_17_18_NoActiveLease_NotActive()
    {
        var h = new Harness();
        var r = h.Handler.Complete(Abigail, "spring-10-1@0600");
        Assert.Equal(TravelIntentHandler.TravelCompletionStatus.NotActive, r.Status);
        var c = h.Handler.Cancel(Abigail, "spring-10-1@0600");
        Assert.Equal(TravelIntentHandler.TravelCompletionStatus.NotActive, c.Status);
        Assert.Equal(0, h.Leases.ActiveCount);
    }

    // T1-19: canonical rejoin — release hook fires on arrival (the Demo host wires the
    // planner to OnTravelReleased; here we verify the hook contract).
    [Fact]
    public void T1_19_CanonicalRejoin_HookFiresOnRelease()
    {
        var h = new Harness();
        h.Handler.Handle(Harness.Travel("Saloon"), Abigail, "spring-10-1@0600");
        h.Adapter.Arrived = true;
        h.Handler.Complete(Abigail, "spring-10-1@0601");
        Assert.Equal(1, h.ReleasedHooks);
        Assert.Equal(Abigail, h.LastReleasedNpc);
    }

    // T1-20: cross-character isolation — Harvey's travel doesn't touch Abigail's lease.
    [Fact]
    public void T1_20_CrossCharacterIsolation()
    {
        var h = new Harness();
        var a = h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        Assert.NotNull(a.Lease);
        Assert.True(h.Leases.IsActive(Abigail));

        var b = h.Handler.Handle(Harness.Travel("Saloon"), Harvey, "spring-10-1@0600");
        Assert.NotNull(b.Lease); // Harvey independent lease
        Assert.True(h.Leases.IsActive(Harvey));

        // Complete Abigail only → Harvey's lease untouched.
        h.Adapter.Arrived = true;
        h.Handler.Complete(Abigail, "spring-10-1@0601");
        Assert.False(h.Leases.IsActive(Abigail));
        Assert.True(h.Leases.IsActive(Harvey));

        // Harvey also arrives → all clear.
        h.Handler.Complete(Harvey, "spring-10-1@0602");
        Assert.False(h.Leases.IsActive(Harvey));
        Assert.Equal(0, h.Leases.ActiveCount);
    }

    // T1-22: arbitration rejects travel when the resolver is absent (defense in depth).
    [Fact]
    public void T1_22_NoResolver_Rejected()
    {
        var reg = CapabilityRegistry.CreateDemoRegistry();
        var adapter = new FakeFollowAdapter();
        var anchors = new DeterministicHardAnchorProvider(false, null);
        var arbitrator = new BehaviorArbitrator(reg, adapter, anchors,
            new DeterministicNpcResolver(Abigail), new DeterministicPlayerResolver("p"));
        var handler = new TravelIntentHandler(arbitrator,
            new DeterministicTravelDestinationResolver(new[] { ("Beach", 20, 4, "inbound-warp") }),
            new FakeTravelAdapter(), new TravelLeaseManager());
        var outcome = handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        Assert.Equal(BehaviorDecision.AdapterUnavailable, outcome.Arbitration.Decision);
        Assert.False(outcome.AdapterInvoked);
    }

    // ── R4: mid-travel hard anchor interrupts — no arrival observation, no rejoin ──

    // Anchor toggled ON after travel started → completion interrupts: lease released,
    // movement cancelled, NO OnTravelArrived, NO OnTravelReleased (never rejoin mid-anchor).
    [Fact]
    public void R4_MidTravelAnchor_Interrupts_NoArrival_NoRejoin()
    {
        var h = new Harness();
        var outcome = h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        Assert.NotNull(outcome.Lease);
        Assert.True(h.Leases.IsActive(Abigail));

        h.Adapter.Arrived = false;          // not yet at destination
        h.Anchors.Active = true;            // event starts mid-travel
        h.Anchors.Anchor = "EVENT/CUTSCENE";
        var r = h.Handler.Complete(Abigail, "spring-10-1@0601");

        Assert.Equal(TravelIntentHandler.TravelCompletionStatus.HardAnchorInterrupted, r.Status);
        Assert.Equal(TravelIntentHandler.TravelReleaseReason.HardAnchorEvent, r.ReleaseReason);
        Assert.False(h.Leases.IsActive(Abigail));   // lease released
        Assert.Equal(0, h.Leases.ActiveCount);      // STALE_TRAVEL_LEASE = 0
        Assert.True(h.Adapter.CancelCalled);        // movement stopped
        Assert.Equal(0, h.ArrivedHooks);            // NO NPC_TRAVEL_ARRIVED on interrupt
        Assert.Equal(0, h.ReleasedHooks);           // NO rejoin on interrupt
    }

    // Every anchor kind maps to its own release reason.
    [Theory]
    [InlineData("EVENT/CUTSCENE", TravelIntentHandler.TravelReleaseReason.HardAnchorEvent)]
    [InlineData("LOCATION_EVENT", TravelIntentHandler.TravelReleaseReason.HardAnchorLocationEvent)]
    [InlineData("FESTIVAL", TravelIntentHandler.TravelReleaseReason.HardAnchorFestival)]
    [InlineData("SLEEP", TravelIntentHandler.TravelReleaseReason.HardAnchorSleep)]
    public void R4_AnchorKinds_MapToReleaseReasons(string anchor, TravelIntentHandler.TravelReleaseReason expected)
    {
        var h = new Harness();
        h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        h.Anchors.Active = true;
        h.Anchors.Anchor = anchor;
        var r = h.Handler.Complete(Abigail, "spring-10-1@0601");
        Assert.Equal(TravelIntentHandler.TravelCompletionStatus.HardAnchorInterrupted, r.Status);
        Assert.Equal(expected, r.ReleaseReason);
        Assert.Equal(0, h.ReleasedHooks);
    }

    // Interrupt is idempotent per lease: once released, no second interrupt.
    [Fact]
    public void R4_MidTravelAnchor_SecondComplete_NotActive()
    {
        var h = new Harness();
        h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        h.Anchors.Active = true;
        h.Anchors.Anchor = "FESTIVAL";
        h.Handler.Complete(Abigail, "spring-10-1@0601");
        var r2 = h.Handler.Complete(Abigail, "spring-10-1@0602");
        Assert.Equal(TravelIntentHandler.TravelCompletionStatus.NotActive, r2.Status);
        Assert.Equal(0, h.ReleasedHooks);
    }

    // No anchor → travel still completes to Arrived with rejoin (regression).
    [Fact]
    public void R4_NoAnchor_ArrivalStillRejoins()
    {
        var h = new Harness();
        h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        h.Adapter.Arrived = true;
        var r = h.Handler.Complete(Abigail, "spring-10-1@0601");
        Assert.Equal(TravelIntentHandler.TravelCompletionStatus.Arrived, r.Status);
        Assert.Equal(TravelIntentHandler.TravelReleaseReason.Arrived, r.ReleaseReason);
        Assert.Equal(1, h.ArrivedHooks);
        Assert.Equal(1, h.ReleasedHooks); // rejoin still fires on arrival
    }

    // Anchor active the WHOLE time → Handle() already rejects; completion sees nothing to do.
    [Fact]
    public void R4_AnchorActiveFromStart_HandleRejects_NoLease()
    {
        var h = new Harness(anchorActive: true);
        var outcome = h.Handler.Handle(Harness.Travel("Beach"), Abigail, "spring-10-1@0600");
        Assert.Equal(BehaviorDecision.RejectedHardAnchor, outcome.Arbitration.Decision);
        Assert.False(h.Leases.IsActive(Abigail));
    }

    /// <summary>Deterministic anchor provider whose state can toggle mid-test (R4 needs an
    /// anchor to appear AFTER travel has started, unlike the static DeterministicHardAnchorProvider).</summary>
    private sealed class ToggleableHardAnchorProvider : IHardAnchorProvider
    {
        public bool Active { get; set; }
        public string? Anchor { get; set; }

        public ToggleableHardAnchorProvider(bool active = false, string? anchor = null)
        {
            Active = active;
            Anchor = anchor;
        }

        public bool IsHardAnchorActive(string npcId) => Active;
        public string? ActiveAnchor(string npcId) => Active ? Anchor : null;
    }
}
