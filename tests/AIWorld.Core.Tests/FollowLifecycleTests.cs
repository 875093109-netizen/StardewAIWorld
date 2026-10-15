using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>ABIGAIL BEHAVIOR LIFECYCLE v0.2 — FollowIntentHandler rejoin hook tests.</summary>
public class FollowLifecycleTests
{
    private static NpcIntent Intent(string type) => new()
    {
        IntentId = Guid.NewGuid().ToString("N"),
        Type = type,
        Target = "Player",
        Priority = 0.8f,
        SchemaVersion = "1",
    };

    private static FollowIntentHandler CreateHandler(FakeFollowAdapter adapter, FollowSessionManager sessions)
    {
        var registry = CapabilityRegistry.CreateDemoRegistry();
        var arbitrator = new BehaviorArbitrator(registry, adapter, new DeterministicHardAnchorProvider(), new DeterministicNpcResolver("Abigail"), new DeterministicPlayerResolver("player"));
        return new FollowIntentHandler(arbitrator, adapter, sessions, new DeterministicFollowDecisionSource(FollowDecision.Accept));
    }

    private static AIWorld.Core.Persistence.AIWorldSaveData NewSaveData() => new()
    {
        SaveSchemaVersion = "1",
    };

    [Fact]
    public void StopSuccess_InvokesOnFollowReleased_WithActorNpcId()
    {
        var adapter = new FakeFollowAdapter();
        var data = NewSaveData();
        var sessions = new FollowSessionManager(data, "player");
        var handler = CreateHandler(adapter, sessions);

        string? releasedNpc = null;
        string? releasedStamp = null;
        handler.OnFollowReleased = (npcId, stamp) => { releasedNpc = npcId; releasedStamp = stamp; };

        // Follow first (creates session + lease).
        handler.Handle(Intent(CapabilityRegistry.CapabilityIds.FollowPlayer), "Abigail", "spring-10-1@0600");
        Assert.True(adapter.Following);

        // Then stop — must release + invoke the rejoin hook.
        var outcome = handler.Handle(Intent(CapabilityRegistry.CapabilityIds.StopFollowing), "Abigail", "spring-10-1@0900");

        Assert.Equal("SUCCESS", outcome.Adapter!.Status);
        Assert.False(adapter.Following);
        Assert.Equal("Abigail", releasedNpc);
        Assert.Equal("spring-10-1@0900", releasedStamp);
        Assert.Null(outcome.Lease); // lease released on stop
    }

    [Fact]
    public void StopWhileNotFollowing_DoesNotInvokeRejoinHook()
    {
        var adapter = new FakeFollowAdapter();
        var data = NewSaveData();
        var sessions = new FollowSessionManager(data, "player");
        var handler = CreateHandler(adapter, sessions);

        int hookCalls = 0;
        handler.OnFollowReleased = (_, _) => hookCalls++;

        // Stop with no active follow → arbitration rejects (RejectedNotFollowing), adapter not invoked.
        var outcome = handler.Handle(Intent(CapabilityRegistry.CapabilityIds.StopFollowing), "Abigail", "spring-10-1@0900");

        Assert.False(outcome.AdapterInvoked);
        Assert.Equal(0, hookCalls); // no rejoin for a no-op stop
    }

    [Fact]
    public void Follow_ThenStop_FollowSessionClosed_WithAiDismiss()
    {
        var adapter = new FakeFollowAdapter();
        var data = NewSaveData();
        var sessions = new FollowSessionManager(data, "player");
        var handler = CreateHandler(adapter, sessions);

        handler.Handle(Intent(CapabilityRegistry.CapabilityIds.FollowPlayer), "Abigail", "spring-10-1@0600");
        var active = sessions.GetActive("Abigail");
        Assert.NotNull(active);

        handler.Handle(Intent(CapabilityRegistry.CapabilityIds.StopFollowing), "Abigail", "spring-10-1@0900");

        var after = sessions.GetLatest("Abigail");
        Assert.NotNull(after);
        Assert.NotNull(after.EndedAt);
        Assert.Equal("AI_DISMISS", after.EndReason);
        Assert.Null(sessions.GetActive("Abigail")); // no stale session
    }
}
