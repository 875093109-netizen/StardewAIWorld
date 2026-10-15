using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// BehaviorArbitrator — MINIMUM PRODUCTION ARBITRATION SLICE (PM D-P4-1).
/// Follow-specific safe arbitration: target resolution → capability resolution →
/// capability availability → adapter availability → current follow state/dedup →
/// hard-anchor protection → lease/conflict check → authorize/reject.
///
/// NOT implemented (explicit): full §9 priority system
/// (EVENT &gt; HARD_GAME_RULE &gt; AI_MAJOR &gt; AI_MINOR &gt; CANONICAL_BASELINE).
/// The model is structured so Phase 5+ can extend it without rewriting the API.
/// </summary>
public sealed class BehaviorArbitrator
{
    private readonly CapabilityRegistry _capabilities;
    private readonly IFollowAdapter _adapter;
    private readonly IHardAnchorProvider _anchors;
    private readonly INpcResolver _npcResolver;
    private readonly IPlayerResolver _playerResolver;
    private readonly ITravelDestinationResolver? _travelResolver;   // Travel T1: destination reachability
    private readonly Func<string, bool>? _travelActiveCheck;        // Travel T1: lease conflict (travel ↔ other)

    public BehaviorArbitrator(
        CapabilityRegistry capabilities,
        IFollowAdapter adapter,
        IHardAnchorProvider anchors,
        INpcResolver npcResolver,
        IPlayerResolver playerResolver,
        ITravelDestinationResolver? travelResolver = null,
        Func<string, bool>? travelActiveCheck = null)
    {
        _capabilities = capabilities;
        _adapter = adapter;
        _anchors = anchors;
        _npcResolver = npcResolver;
        _playerResolver = playerResolver;
        _travelResolver = travelResolver;
        _travelActiveCheck = travelActiveCheck;
    }

    /// <summary>Hard-anchor source — shared by arbitration AND the travel completion loop
    /// (R4: an anchor that starts mid-travel must interrupt, not just block at Authorize).</summary>
    public IHardAnchorProvider HardAnchors => _anchors;

    /// <summary>Resolve and authorize a follow-related intent. Never touches the adapter here.
    /// CANONICAL ACTOR/TARGET (PM Scenario A): actorNpcId = the NPC that performs the behavior
    /// (from conversation/execution context); intent.Target = the semantic target ("Player" for
    /// follow). These are distinct — the historical conflation (target=NPC name) is removed.</summary>
    public ArbitrationResult Authorize(NpcIntent intent, string actorNpcId)
    {
        if (!_npcResolver.NpcExists(actorNpcId))
            return ArbitrationResult.Reject(BehaviorDecision.RejectedTargetNotFound, actorNpcId, intent.Type, $"NPC actor '{actorNpcId}' not found");

        var capability = _capabilities.Get(intent.Type);
        if (capability is null)
            return ArbitrationResult.Reject(BehaviorDecision.RejectedCapabilityUnavailable, actorNpcId, intent.Type, $"capability '{intent.Type}' not registered");
        if (!_capabilities.IsAvailable(intent.Type))
            return ArbitrationResult.Reject(BehaviorDecision.RejectedCapabilityUnavailable, actorNpcId, intent.Type, $"capability '{intent.Type}' unavailable");

        if (intent.Type is CapabilityRegistry.CapabilityIds.FollowPlayer or CapabilityRegistry.CapabilityIds.StopFollowing)
        {
            // EXECUTION PRECONDITION (defense-in-depth, PM §5): follow intents target the Player.
            // Canonical SemanticValidator enforces this; this is a narrow invariant assertion,
            // NOT a second configurable semantic policy. Prevents "validator says Player,
            // execution silently accepts Abigail" drift.
            if (!string.Equals(intent.Target, "Player", StringComparison.OrdinalIgnoreCase))
                return ArbitrationResult.Reject(BehaviorDecision.RejectedTargetNotFound, actorNpcId, intent.Type, $"follow intent target must be 'Player' (got '{intent.Target}'); actor is '{actorNpcId}'");

            if (!_capabilities.HasExecutableBackend(intent.Type))
                return ArbitrationResult.Reject(BehaviorDecision.AdapterUnavailable, actorNpcId, intent.Type, $"capability '{intent.Type}' has no executable backend");
            if (!_adapter.IsAvailable)
                return ArbitrationResult.Reject(BehaviorDecision.AdapterUnavailable, actorNpcId, intent.Type, $"adapter '{_adapter.AdapterId}' unavailable");

            // Hard anchors: event/cutscene/festival/sleep block follow mutations.
            if (_anchors.IsHardAnchorActive(actorNpcId))
                return ArbitrationResult.Reject(BehaviorDecision.RejectedHardAnchor, actorNpcId, intent.Type, $"hard anchor active: {_anchors.ActiveAnchor(actorNpcId)}");
        }

        // Travel branch (Travel Gate T1): target = canonical location (U5/U6 single truth).
        if (intent.Type == CapabilityRegistry.CapabilityIds.TravelToLocation)
        {
            // Execution precondition: travel targets a location, not "Player" (defense-in-depth
            // mirroring the follow target precondition).
            if (string.Equals(intent.Target, "Player", StringComparison.OrdinalIgnoreCase))
                return ArbitrationResult.Reject(BehaviorDecision.RejectedTargetNotFound, actorNpcId, intent.Type,
                    $"travel intent target must be a location (got 'Player'); actor is '{actorNpcId}'");

            if (!_capabilities.HasExecutableBackend(intent.Type))
                return ArbitrationResult.Reject(BehaviorDecision.AdapterUnavailable, actorNpcId, intent.Type, $"capability '{intent.Type}' has no executable backend");
            if (_travelResolver is null)
                return ArbitrationResult.Reject(BehaviorDecision.AdapterUnavailable, actorNpcId, intent.Type, "travel destination resolver not configured");

            // Hard anchors: event/cutscene/festival/sleep/location-event block travel.
            if (_anchors.IsHardAnchorActive(actorNpcId))
                return ArbitrationResult.Reject(BehaviorDecision.RejectedHardAnchor, actorNpcId, intent.Type, $"hard anchor active: {_anchors.ActiveAnchor(actorNpcId)}");

            // Conflict: an active follow lease blocks travel; an active travel blocks anything
            // else (checked via the injected travel-active predicate).
            if (_adapter.IsFollowing(actorNpcId))
                return ArbitrationResult.Reject(BehaviorDecision.RejectedLeaseConflict, actorNpcId, intent.Type, "NPC is following — travel blocked");
            if (_travelActiveCheck?.Invoke(actorNpcId) == true)
                return ArbitrationResult.Reject(BehaviorDecision.RejectedLeaseConflict, actorNpcId, intent.Type, "another travel lease active");

            // Reachability (E1/E2): the resolver decides Unavailable / NoArrivalTile → reject.
            // Actor-scoped (R5): fallback uses the travelling NPC's own default home.
            var resolved = _travelResolver.Resolve(actorNpcId, intent.Target);
            if (resolved.Status != TravelDestinationStatus.Resolved)
                return ArbitrationResult.Reject(BehaviorDecision.RejectedTargetNotFound, actorNpcId, intent.Type,
                    $"destination '{intent.Target}' {resolved.Error ?? "unreachable"}");
        }

        // Follow-state checks (dedup / idempotency) — keyed on the ACTOR, not the target.
        bool isFollowing = _adapter.IsFollowing(actorNpcId);
        if (intent.Type == CapabilityRegistry.CapabilityIds.FollowPlayer && isFollowing)
            return ArbitrationResult.Reject(BehaviorDecision.RejectedAlreadyFollowing, actorNpcId, intent.Type, "NPC already following");
        if (intent.Type == CapabilityRegistry.CapabilityIds.StopFollowing && !isFollowing)
            return ArbitrationResult.Reject(BehaviorDecision.RejectedNotFollowing, actorNpcId, intent.Type, "NPC not currently following");

        return new ArbitrationResult
        {
            Decision = BehaviorDecision.Authorized,
            CapabilityId = intent.Type,
            NpcId = actorNpcId,
            PlayerId = _playerResolver.ResolvePlayerId(),
        };
    }
}

/// <summary>NPC target resolution abstraction (Core has no game dependency).</summary>
public interface INpcResolver
{
    bool NpcExists(string npcName);
}

/// <summary>Player identity resolution abstraction.</summary>
public interface IPlayerResolver
{
    string ResolvePlayerId();
}

/// <summary>Deterministic resolvers for offline tests.</summary>
public sealed class DeterministicNpcResolver : INpcResolver
{
    private readonly HashSet<string> _known;
    public DeterministicNpcResolver(params string[] known) => _known = new HashSet<string>(known, StringComparer.Ordinal);
    public bool NpcExists(string npcName) => _known.Contains(npcName);
}

public sealed class DeterministicPlayerResolver : IPlayerResolver
{
    private readonly string _playerId;
    public DeterministicPlayerResolver(string playerId) => _playerId = playerId;
    public string ResolvePlayerId() => _playerId;
}
