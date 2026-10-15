using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// Production follow intent handler (PM Phase 4 core chain):
/// NpcIntent → (FollowDecision) → BehaviorArbitrator → IFollowAdapter → postcondition
/// → AdapterResult → FollowSession lifecycle → BehaviorLease lifecycle.
/// DECLINE (decision layer) → no lease, no adapter call, no Squad mutation, no session.
/// Test fixtures enter here with deterministic decision; future LLM chain replaces the source.
/// </summary>
public sealed class FollowIntentHandler
{
    private readonly BehaviorArbitrator _arbitrator;
    private readonly IFollowAdapter _adapter;
    private readonly FollowSessionManager _sessions;
    private readonly IFollowDecisionSource _decisionSource;

    /// <summary>v0.2 lifecycle hook: invoked after a successful stop releases the follow
    /// state, so the host can restore the NPC to its canonical baseline (deterministic
    /// return_to_baseline — NOT an LLM intent). Core only defines the hook; the Demo side
    /// injects the real ReturnToBaselinePlanner. Nullable for offline tests.</summary>
    public Action<string, string>? OnFollowReleased { get; set; }
    public Action<string, string>? OnFollowStarted { get; set; }
    public Action<string, string>? OnFollowEnding { get; set; }

    public FollowIntentHandler(
        BehaviorArbitrator arbitrator,
        IFollowAdapter adapter,
        FollowSessionManager sessions,
        IFollowDecisionSource decisionSource)
    {
        _arbitrator = arbitrator;
        _adapter = adapter;
        _sessions = sessions;
        _decisionSource = decisionSource;
    }

    public sealed class FollowIntentOutcome
    {
        public required ArbitrationResult Arbitration { get; init; }
        public AdapterResult? Adapter { get; init; }
        public FollowSession? Session { get; init; }
        public BehaviorLease? Lease { get; init; }
        public bool AdapterInvoked { get; init; }
        public bool DecisionDeclined { get; init; }
    }

    /// <summary>Handle a follow-related intent for a specific ACTOR NPC (PM canonical semantics:
    /// actorNpcId = the NPC performing the behavior, from conversation/execution context;
    /// intent.Target = semantic target, "Player" for follow).</summary>
    public FollowIntentOutcome Handle(NpcIntent intent, string actorNpcId, string gameStamp)
    {
        // 1. Decision layer (NPC choice; DECLINE is a decision, never an adapter failure).
        var decision = _decisionSource.Decide(actorNpcId, "player", intent.Reason);
        if (decision == FollowDecision.Decline)
        {
            return new FollowIntentOutcome
            {
                Arbitration = ArbitrationResult.Reject(BehaviorDecision.RejectedUnknown, actorNpcId, intent.Type, "DECLINE"),
                AdapterInvoked = false,
                DecisionDeclined = true,
            };
        }

        // 2. Arbitration.
        var arbitration = _arbitrator.Authorize(intent, actorNpcId);
        if (!arbitration.Authorized)
        {
            return new FollowIntentOutcome { Arbitration = arbitration, AdapterInvoked = false };
        }

        // 3. Adapter execution (follow or stop) — the ACTOR is recruited/follows; the TARGET
        // (Player) is the semantic object of the action.
        AdapterResult adapterResult;
        bool invoked = true;
        if (intent.Type == CapabilityRegistry.CapabilityIds.FollowPlayer)
        {
            adapterResult = _adapter.StartFollowing(actorNpcId, arbitration.PlayerId!);
        }
        else if (intent.Type == CapabilityRegistry.CapabilityIds.StopFollowing)
        {
            adapterResult = _adapter.StopFollowing(actorNpcId, arbitration.PlayerId!);
        }
        else
        {
            return new FollowIntentOutcome
            {
                Arbitration = ArbitrationResult.Reject(BehaviorDecision.RejectedCapabilityUnavailable, actorNpcId, intent.Type, "not a follow capability"),
                AdapterInvoked = false,
            };
        }

        // 4. Postcondition: only SUCCESS with observable evidence proceeds.
        if (adapterResult.Status != "SUCCESS")
        {
            return new FollowIntentOutcome { Arbitration = arbitration, Adapter = adapterResult, AdapterInvoked = invoked };
        }

        // 5. Session + lease lifecycle (keyed on the ACTOR).
        FollowSession? session = null;
        BehaviorLease? lease = null;
        if (intent.Type == CapabilityRegistry.CapabilityIds.FollowPlayer)
        {
            session = _sessions.Start(actorNpcId, intent.Reason ?? "AI_PROPOSED", gameStamp, adapterId: _adapter.AdapterId);
            lease = CreateLease(actorNpcId, intent.Type, "ACTIVE");
            if (session is not null) OnFollowStarted?.Invoke(actorNpcId, gameStamp);
        }
        else // stop
        {
            OnFollowEnding?.Invoke(actorNpcId, gameStamp);
            var ended = _sessions.End(actorNpcId, "AI_DISMISS", gameStamp);
            lease = null; // stop releases lease (no active lease after stop)

            // v0.2 BEHAVIOR LIFECYCLE: after the Squad follow state is cleanly removed (no
            // GoHome warp — the adapter does the clean removal), hand the NPC back to the
            // canonical baseline. This is deterministic return_to_baseline, NOT an LLM intent.
            // The host injects the real ReturnToBaselinePlanner via OnFollowReleased.
            OnFollowReleased?.Invoke(actorNpcId, gameStamp);
        }

        return new FollowIntentOutcome
        {
            Arbitration = arbitration,
            Adapter = adapterResult,
            Session = session,
            Lease = lease,
            AdapterInvoked = invoked,
        };
    }

    private static BehaviorLease CreateLease(string npcId, string capabilityId, string status) => new()
    {
        LeaseId = Guid.NewGuid().ToString("N"),
        NpcId = npcId,
        CapabilityId = capabilityId,
        Priority = "AI_MINOR",
        ExpiresAt = "day-end",
        Interruptible = true,
        SchemaVersion = "1",
        Status = status,
    };
}
