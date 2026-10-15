using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// Travel Gate T1 (U7) — session-scoped travel lease registry.
///
/// TravelLease is SESSION_SCOPED and NOT_PERSISTED (frozen decision U7): after save/cold
/// reload there is NO active travel lease (ACTIVE_TRAVEL_LEASE = NONE, STALE_TRAVEL_LEASE
/// = 0) — unfinished AIWorld travel is never auto-resumed. Stardew restoring the NPC's
/// physical position after reload is NOT an AIWorld TravelLease resume; post-reload the
/// NPC goes to canonical reconciliation.
///
/// The lease object reuses the frozen BehaviorLease contract (capabilityId =
/// travel.to_location, rejoinAnchor = previous owner reference, Status lifecycle
/// PENDING → ACTIVE → RELEASED). Only the ACTIVE set is held here, in memory only.
/// </summary>
public sealed class TravelLeaseManager
{
    private readonly Dictionary<string, BehaviorLease> _active = new(StringComparer.Ordinal);
    private readonly List<BehaviorLease> _released = new(); // audit trail (session-scoped)

    public IReadOnlyDictionary<string, BehaviorLease> Active => _active;

    /// <summary>Active lease for an NPC, or null.</summary>
    public BehaviorLease? GetActive(string npcId) =>
        _active.TryGetValue(npcId, out var l) ? l : null;

    public bool IsActive(string npcId) => _active.ContainsKey(npcId);

    /// <summary>Acquire a lease. Returns null when one is already active (dedup — never two).</summary>
    public BehaviorLease? Acquire(string npcId, string capabilityId, string gameStamp, string previousOwner, TravelDestination? destination = null, string? rejoinAnchor = null)
    {
        if (_active.ContainsKey(npcId)) return null; // at most one active lease per NPC

        var lease = new BehaviorLease
        {
            LeaseId = Guid.NewGuid().ToString("N"),
            NpcId = npcId,
            CapabilityId = capabilityId,
            Priority = "AI_MINOR",
            ExpiresAt = "day-end",
            Interruptible = true,
            SchemaVersion = "1",
            RejoinAnchor = rejoinAnchor,
            AdapterParams = destination is null ? null : new Dictionary<string, object?>
            {
                ["locationName"] = destination.LocationName,
                ["tileX"] = (long)destination.TileX,
                ["tileY"] = (long)destination.TileY,
                ["evidenceSource"] = destination.EvidenceSource,
            },
            Status = "PENDING",
        };
        lease.Status = "ACTIVE";
        _active[npcId] = lease;
        return lease;
    }

    /// <summary>Release the active lease for an NPC. Returns the released lease, or null.</summary>
    public BehaviorLease? Release(string npcId)
    {
        if (!_active.TryGetValue(npcId, out var lease)) return null;
        _active.Remove(npcId);
        lease.Status = "RELEASED";
        _released.Add(lease);
        return lease;
    }

    /// <summary>Session-scoped audit trail of released leases (NOT persisted).</summary>
    public IReadOnlyList<BehaviorLease> Released => _released;

    /// <summary>Active travel leases after a save/reload are ALWAYS empty (U7).</summary>
    public int ActiveCount => _active.Count;
}
