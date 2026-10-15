namespace AIWorld.Core.Behaviors;

/// <summary>
/// NPC-level decision about a follow proposal (REQUIREMENTS Scenario E).
/// DECLINE belongs to the DECISION layer — it must never be simulated by making the
/// adapter fail. DECLINE → no lease, no adapter call, no Squad mutation (PM D-P4-2).
/// </summary>
public enum FollowDecision
{
    Accept,
    Decline,
}

/// <summary>
/// Source of the NPC follow decision. Phase 4 uses a deterministic source (test fixtures);
/// the future LLM persona/memory/relationship chain plugs in here (Phase 7 Scenario E).
/// </summary>
public interface IFollowDecisionSource
{
    FollowDecision Decide(string npcId, string playerId, string? reason);
}

/// <summary>Deterministic decision source for Phase 4 acceptance (PM D-P4-2).</summary>
public sealed class DeterministicFollowDecisionSource : IFollowDecisionSource
{
    private readonly FollowDecision _decision;

    public DeterministicFollowDecisionSource(FollowDecision decision) => _decision = decision;

    public FollowDecision Decide(string npcId, string playerId, string? reason) => _decision;
}
