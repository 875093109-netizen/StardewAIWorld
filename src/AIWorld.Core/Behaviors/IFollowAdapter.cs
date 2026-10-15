using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// Semantic follow adapter interface (PM: upstream implementation details must stay inside
/// the adapter). Core defines the interface; AIWorld.Adapters implements StardewSquadAdapter.
/// All calls are MAIN-THREAD ONLY.
/// </summary>
public interface IFollowAdapter
{
    string AdapterId { get; }

    /// <summary>Start following. Returns result with objective postcondition evidence.</summary>
    AdapterResult StartFollowing(string npcId, string playerId);

    /// <summary>Stop following. Returns result with objective postcondition evidence.</summary>
    AdapterResult StopFollowing(string npcId, string playerId);

    /// <summary>Read current follow state (true when the NPC is currently recruited/following).</summary>
    bool IsFollowing(string npcId);

    /// <summary>True when the adapter can be invoked (mod resolved, runtime available).</summary>
    bool IsAvailable { get; }
}

/// <summary>Deterministic fake adapter for offline tests (records invocations, configurable outcomes).</summary>
public sealed class FakeFollowAdapter : IFollowAdapter
{
    public string AdapterId => "FakeFollowAdapter";
    public bool IsAvailable { get; set; } = true;
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public bool Following { get; set; }
    public bool StartShouldFail { get; set; }
    public bool StopShouldFail { get; set; }
    public bool StartShouldTimeout { get; set; }
    public bool StopShouldTimeout { get; set; }

    public AdapterResult StartFollowing(string npcId, string playerId)
    {
        StartCalls++;
        if (!IsAvailable) return new AdapterResult { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.FollowPlayer, Status = "UNAVAILABLE", Message = "adapter unavailable", SchemaVersion = "1" };
        if (StartShouldTimeout) return new AdapterResult { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.FollowPlayer, Status = "FAILED", Message = "POSTCONDITION_TIMEOUT", Detail = new Dictionary<string, object?> { ["failureReason"] = "timeout" }, SchemaVersion = "1" };
        if (StartShouldFail) return new AdapterResult { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.FollowPlayer, Status = "FAILED", Message = "recruit failed", Detail = new Dictionary<string, object?> { ["failureReason"] = "squadFull" }, SchemaVersion = "1" };
        Following = true;
        return new AdapterResult { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.FollowPlayer, Status = "SUCCESS", Message = "recruited", SchemaVersion = "1" };
    }

    public AdapterResult StopFollowing(string npcId, string playerId)
    {
        StopCalls++;
        if (!IsAvailable) return new AdapterResult { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.StopFollowing, Status = "UNAVAILABLE", Message = "adapter unavailable", SchemaVersion = "1" };
        if (StopShouldTimeout) return new AdapterResult { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.StopFollowing, Status = "FAILED", Message = "POSTCONDITION_TIMEOUT", Detail = new Dictionary<string, object?> { ["failureReason"] = "timeout" }, SchemaVersion = "1" };
        if (StopShouldFail) return new AdapterResult { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.StopFollowing, Status = "FAILED", Message = "dismiss failed", Detail = new Dictionary<string, object?> { ["failureReason"] = "dismissError" }, SchemaVersion = "1" };
        Following = false;
        return new AdapterResult { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.StopFollowing, Status = "SUCCESS", Message = "dismissed", SchemaVersion = "1" };
    }

    public bool IsFollowing(string npcId) => Following;
}
