using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// Travel Gate T1 — native travel adapter contract.
///
/// TRAVEL IS ASYNC BY NATURE: NPC movement (PathFindController pathing) advances only
/// when the game loop ticks. A synchronous Sleep-wait on the main thread would freeze the
/// loop and the NPC would never walk. So the adapter is FIRE-AND-CHECK:
///   - Travel(...) initiates movement (natural multi-segment route if cross-location) and
///     returns immediately with STARTED / FAILED / UNAVAILABLE.
///   - IsArrived(...) is the REAL arrival predicate, polled by the host each game tick
///     (PM §6: OBJECTIVE_ARRIVAL only when location match + controller complete + no
///     active travel path + tile within tolerance; adapter invocation != arrival).
///   - Cancel(...) stops movement (clears controller) on interrupt/timeout.
///
/// Status semantics (deliberate narrow extension of the AdapterResult status set, required
/// by async travel — documented in the T1 evidence):
///   STARTED     — movement actually initiated (execution began; NOT arrival)
///   FAILED      — setup failed (path setup failure / post-warp path failure / cancellation)
///   UNAVAILABLE — execution-time unreachable (target not loaded etc.)
/// The frozen AdapterResult object shape is unchanged; only a status string value is added.
/// </summary>
public interface ITravelAdapter
{
    string AdapterId { get; }

    /// <summary>True when the adapter can be invoked (game world available).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Initiate travel for an NPC to a resolved destination. Same-location: PathFindController
    /// pathing. Cross-location (PM HARD REQUIREMENT 2026-08-15): natural multi-segment movement —
    /// walk to each map's real exit, source-grounded warp transition only at the exit, continue
    /// from the connection's entry; never a direct warp to the destination. NO warp fallback on
    /// path failure. Failure = stop in place at the last verified legal tile (no teleport
    /// rollback). Returns STARTED (movement initiated, NOT arrived) / FAILED / UNAVAILABLE.
    /// </summary>
    AdapterResult Travel(string npcId, TravelDestination destination);

    /// <summary>
    /// The REAL arrival predicate (PM §6). True only when:
    ///   npc.currentLocation == destination location
    ///   AND controller/path execution completed (no active travel path)
    ///   AND (when tile is part of the contract) within tolerance of the resolved tile.
    /// Never true from AdapterResult.SUCCESS / warpCharacter called / controller created /
    /// location changed alone.
    /// </summary>
    bool IsArrived(string npcId, TravelDestination destination);

    /// <summary>Stop movement (clear controller) and mark the travel cancelled. Safe to call twice.</summary>
    void Cancel(string npcId);
}

/// <summary>Optional objective failure receipt for asynchronous movement; avoids holding a failed lease until timeout.</summary>
public interface ITravelFailureSource { string? GetFailureReason(string npcId); }

/// <summary>Deterministic fake travel adapter for offline tests.</summary>
public sealed class FakeTravelAdapter : ITravelAdapter
{
    public string AdapterId => "FakeTravelAdapter";

    public bool IsAvailable { get; set; } = true;
    public bool TravelShouldFail { get; set; }
    public bool TravelShouldUnavailable { get; set; }
    public bool Arrived { get; set; }               // set by tests to simulate arrival
    public bool CancelCalled { get; private set; }

    public int TravelCalls { get; private set; }
    public int CancelCount { get; private set; }
    public string? LastNpcId { get; private set; }
    public TravelDestination? LastDestination { get; private set; }

    public AdapterResult Travel(string npcId, TravelDestination destination)
    {
        TravelCalls++;
        LastNpcId = npcId;
        LastDestination = destination;
        if (!IsAvailable)
            return new AdapterResult { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.TravelToLocation, Status = "UNAVAILABLE", Message = "adapter unavailable", SchemaVersion = "1" };
        if (TravelShouldFail)
            return new AdapterResult { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.TravelToLocation, Status = "FAILED", Message = "path setup failed", Detail = new Dictionary<string, object?> { ["failureReason"] = "pathSetupFailed" }, SchemaVersion = "1" };
        if (TravelShouldUnavailable)
            return new AdapterResult { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.TravelToLocation, Status = "UNAVAILABLE", Message = "location unavailable at execution", SchemaVersion = "1" };
        // STARTED = movement initiated, NOT arrival.
        return new AdapterResult { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.TravelToLocation, Status = "STARTED", Message = "movement initiated", SchemaVersion = "1" };
    }

    public bool IsArrived(string npcId, TravelDestination destination) => Arrived;

    public void Cancel(string npcId)
    {
        CancelCount++;
        CancelCalled = true;
        Arrived = false;
    }
}
