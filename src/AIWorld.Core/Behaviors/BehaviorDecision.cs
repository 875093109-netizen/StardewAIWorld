using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// Structured arbitration decision (PM D-P4-1). Follow-specific safe arbitration slice.
/// </summary>
public enum BehaviorDecision
{
    Authorized,

    // rejection reasons
    RejectedTargetNotFound,
    RejectedCapabilityUnavailable,
    RejectedHardAnchor,
    RejectedLeaseConflict,
    RejectedAlreadyFollowing,
    RejectedNotFollowing,
    AdapterUnavailable,
    RejectedUnknown,
}

public sealed class ArbitrationResult
{
    public required BehaviorDecision Decision { get; init; }
    public string? CapabilityId { get; init; }
    public string? NpcId { get; init; }
    public string? PlayerId { get; init; }
    public string? Message { get; init; }
    public BehaviorLease? Lease { get; init; }

    public bool Authorized => Decision == BehaviorDecision.Authorized;

    public static ArbitrationResult Reject(BehaviorDecision decision, string npcId, string capabilityId, string message) =>
        new() { Decision = decision, NpcId = npcId, CapabilityId = capabilityId, Message = message };
}
