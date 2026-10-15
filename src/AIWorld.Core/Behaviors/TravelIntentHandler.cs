using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// Travel Gate T1 — production travel intent handler.
///
/// Chain: NpcIntent → BehaviorArbitrator (capability/hard-anchor/conflict/reachability)
/// → ITravelDestinationResolver (canonical location → arrival tile)
/// → ITravelAdapter.Travel (STARTED = movement initiated, NOT arrival)
/// → TravelLease ACTIVE (only after execution actually starts — PM §7)
/// → tick-driven Complete(): OBJECTIVE_ARRIVAL → observation hook + lease RELEASE
///   + canonical rejoin hook; timeout/cancel → movement stop + lease RELEASE.
///
/// AUTHORIZED != EXECUTING: lease ACTIVE only after the adapter returns STARTED.
/// Adapter failure (FAILED/UNAVAILABLE) → NO active lease.
/// STALE_TRAVEL_LEASE = 0 (every exit path releases).
///
/// Core has no game dependency: the adapter/planner are injected. Offline tests use the
/// deterministic fake adapter; the Demo host injects NativeTravelAdapter + the rejoin hook.
/// </summary>
public sealed class TravelIntentHandler
{
    private readonly BehaviorArbitrator _arbitrator;
    private readonly ITravelDestinationResolver _resolver;
    private readonly ITravelAdapter _adapter;
    private readonly TravelLeaseManager _leases;
    private readonly int _maxCompletionTicks;

    /// <summary>Invoked on objective arrival (host creates the NPC travel observation).</summary>
    public Action<string, TravelDestination, string>? OnTravelArrived { get; set; }

    /// <summary>Invoked after lease release (host restores canonical baseline).</summary>
    public Action<string, string>? OnTravelReleased { get; set; }

    public TravelIntentHandler(
        BehaviorArbitrator arbitrator,
        ITravelDestinationResolver resolver,
        ITravelAdapter adapter,
        TravelLeaseManager leases,
        int maxCompletionTicks = 60)
    {
        _arbitrator = arbitrator;
        _resolver = resolver;
        _adapter = adapter;
        _leases = leases;
        _maxCompletionTicks = maxCompletionTicks;
    }

    public sealed class TravelIntentOutcome
    {
        public required ArbitrationResult Arbitration { get; init; }
        public TravelDestination? Destination { get; init; }
        public AdapterResult? Adapter { get; init; }
        public BehaviorLease? Lease { get; init; }
        public bool AdapterInvoked { get; init; }
        public bool AlreadyAtDestination { get; init; }
    }

    public enum TravelCompletionStatus
    {
        /// <summary>No active travel lease for the NPC (nothing to complete).</summary>
        NotActive,
        /// <summary>Objective arrival verified — lease released, hooks invoked.</summary>
        Arrived,
        /// <summary>Movement still in progress (arrival predicate not yet true).</summary>
        Pending,
        /// <summary>Completion tick budget exceeded — movement cancelled, lease released.</summary>
        TimedOut,
        /// <summary>Cancelled via Cancel() — movement stopped, lease released.</summary>
        Cancelled,
        /// <summary>Adapter reported failure during completion (arrival predicate never true).</summary>
        Failed,
        /// <summary>R4: a hard anchor (event/festival/location-event/sleep) started mid-travel —
        /// movement cancelled + lease released, NO arrival observation, NO rejoin.</summary>
        HardAnchorInterrupted,
    }

    /// <summary>Why a travel lease ended (R4). Only appropriate reasons invoke the rejoin hook;
    /// hard-anchor interrupts and adapter failure never rejoin the NPC mid-anchor.</summary>
    public enum TravelReleaseReason
    {
        Arrived,
        TimedOut,
        ExplicitCancel,
        HardAnchorEvent,
        HardAnchorLocationEvent,
        HardAnchorFestival,
        HardAnchorSleep,
        AdapterFailure,
    }

    public sealed record TravelCompletionResult(TravelCompletionStatus Status, string Detail, int TicksElapsed, TravelReleaseReason? ReleaseReason = null);

    // per-NPC completion bookkeeping (session-scoped, not persisted)
    private readonly Dictionary<string, int> _ticks = new(StringComparer.Ordinal);

    /// <summary>Handle a travel intent for a specific ACTOR NPC. Never blocks the game loop.</summary>
    public TravelIntentOutcome Handle(NpcIntent intent, string actorNpcId, string gameStamp)
    {
        // 1. Arbitration (capability / hard anchors / conflicts / reachability).
        var arbitration = _arbitrator.Authorize(intent, actorNpcId);
        if (!arbitration.Authorized)
            return new TravelIntentOutcome { Arbitration = arbitration, AdapterInvoked = false };

        // 2. Resolve the canonical location to an arrival tile (U2: unavailable → reject,
        //    no source-grounded tile → reject; never guess). Actor-scoped (R5): the default-
        //    position fallback uses the travelling NPC's own home, not a hardcoded NPC.
        var resolved = _resolver.Resolve(actorNpcId, intent.Target);
        if (resolved.Status != TravelDestinationStatus.Resolved || resolved.Destination is null)
            return new TravelIntentOutcome
            {
                Arbitration = ArbitrationResult.Reject(BehaviorDecision.RejectedTargetNotFound, actorNpcId, intent.Type,
                    $"destination '{intent.Target}' {resolved.Error ?? "unresolvable"}"),
                AdapterInvoked = false,
            };

        var destination = resolved.Destination;

        // 3. Already at destination → deterministic already-satisfied (T-E). No movement,
        //    no lease needed beyond the observation of a satisfied request.
        if (_adapter.IsArrived(actorNpcId, destination))
            return new TravelIntentOutcome
            {
                Arbitration = arbitration,
                Destination = destination,
                AdapterInvoked = false,
                AlreadyAtDestination = true,
            };

        // 4. Adapter execution — movement actually starts here.
        var adapterResult = _adapter.Travel(actorNpcId, destination);
        if (adapterResult.Status != "STARTED")
            return new TravelIntentOutcome
            {
                Arbitration = arbitration,
                Destination = destination,
                Adapter = adapterResult,
                AdapterInvoked = true,
            };

        // 5. Lease ACTIVE only after execution actually started (PM §7: AUTHORIZED != EXECUTING).
        var lease = _leases.Acquire(actorNpcId, CapabilityRegistry.CapabilityIds.TravelToLocation, gameStamp,
            previousOwner: BaselineResolver.OwnerCanonical, destination: destination, rejoinAnchor: "canonical-baseline");
        if (lease is null)
        {
            // Lease conflict (another travel already active) — undo movement.
            _adapter.Cancel(actorNpcId);
            return new TravelIntentOutcome
            {
                Arbitration = ArbitrationResult.Reject(BehaviorDecision.RejectedLeaseConflict, actorNpcId, intent.Type,
                    "another travel lease already active"),
                Destination = destination,
                Adapter = adapterResult,
                AdapterInvoked = true,
            };
        }

        _ticks[actorNpcId] = 0;
        return new TravelIntentOutcome
        {
            Arbitration = arbitration,
            Destination = destination,
            Adapter = adapterResult,
            Lease = lease,
            AdapterInvoked = true,
        };
    }

    /// <summary>
    /// Tick-driven completion — call once per game tick (host UpdateTicked) while a travel
    /// lease is active. On arrival: observation hook + lease RELEASE + rejoin hook.
    /// On timeout: movement cancel + lease RELEASE. On a mid-travel hard anchor: movement
    /// cancel + lease RELEASE with NO arrival observation and NO rejoin (the anchor owns the
    /// NPC). Every exit path releases (STALE = 0).
    /// </summary>
    public TravelCompletionResult Complete(string actorNpcId, string gameStamp)
    {
        var lease = _leases.GetActive(actorNpcId);
        if (lease is null)
        {
            _ticks.Remove(actorNpcId);
            return new TravelCompletionResult(TravelCompletionStatus.NotActive, "no active travel lease", 0);
        }

        var destination = ResolveDestinationFromLease(lease);
        if (destination is null)
        {
            // Defensive: lease without destination metadata → fail + release + rejoin.
            _leases.Release(actorNpcId);
            _adapter.Cancel(actorNpcId);
            _ticks.Remove(actorNpcId);
            OnTravelReleased?.Invoke(actorNpcId, gameStamp);
            return new TravelCompletionResult(TravelCompletionStatus.Failed, "lease missing destination", 0,
                TravelReleaseReason.AdapterFailure);
        }

        int t = _ticks.TryGetValue(actorNpcId, out var v) ? v : 0;

        // R4: a hard anchor that starts mid-travel interrupts the lease. Re-checked EVERY tick,
        // not only at Authorize — arbitration-time blocking alone cannot handle anchors that
        // begin while the NPC is en route. No NPC_TRAVEL_ARRIVED, no rejoin (never teleport an
        // NPC out of an event/festival/sleep/location-event).
        var anchor = _arbitrator.HardAnchors.ActiveAnchor(actorNpcId);
        if (_arbitrator.HardAnchors.IsHardAnchorActive(actorNpcId))
        {
            var reason = ReasonForAnchor(anchor);
            _adapter.Cancel(actorNpcId);
            _leases.Release(actorNpcId);
            _ticks.Remove(actorNpcId);
            return new TravelCompletionResult(TravelCompletionStatus.HardAnchorInterrupted,
                $"hard anchor '{anchor ?? "unknown"}' interrupted travel", t + 1, reason);
        }

        _ticks[actorNpcId] = t + 1;

        if (_adapter.IsArrived(actorNpcId, destination))
        {
            _leases.Release(actorNpcId);
            _ticks.Remove(actorNpcId);
            OnTravelArrived?.Invoke(actorNpcId, destination, gameStamp);
            OnTravelReleased?.Invoke(actorNpcId, gameStamp);
            return new TravelCompletionResult(TravelCompletionStatus.Arrived, $"objective arrival at {destination.LocationName}", t + 1, TravelReleaseReason.Arrived);
        }

        if (_adapter is ITravelFailureSource failures && failures.GetFailureReason(actorNpcId) is { } failure)
        {
            _adapter.Cancel(actorNpcId);
            _leases.Release(actorNpcId);
            _ticks.Remove(actorNpcId);
            // The adapter restored canonical ownership. Do not launch another route during a failed/hard-anchor transition.
            return new TravelCompletionResult(TravelCompletionStatus.Failed, failure, t + 1, TravelReleaseReason.AdapterFailure);
        }

        if (t + 1 >= _maxCompletionTicks)
        {
            _adapter.Cancel(actorNpcId);
            _leases.Release(actorNpcId);
            _ticks.Remove(actorNpcId);
            OnTravelReleased?.Invoke(actorNpcId, gameStamp);
            return new TravelCompletionResult(TravelCompletionStatus.TimedOut, $"arrival timeout after {t + 1} ticks", t + 1, TravelReleaseReason.TimedOut);
        }

        return new TravelCompletionResult(TravelCompletionStatus.Pending, "movement in progress", t + 1);
    }

    /// <summary>Explicit interruption (T-G): stop movement, release lease, rejoin.</summary>
    public TravelCompletionResult Cancel(string actorNpcId, string gameStamp)
    {
        var lease = _leases.GetActive(actorNpcId);
        if (lease is null)
        {
            _ticks.Remove(actorNpcId);
            return new TravelCompletionResult(TravelCompletionStatus.NotActive, "no active travel lease", 0);
        }
        _adapter.Cancel(actorNpcId);
        _leases.Release(actorNpcId);
        _ticks.Remove(actorNpcId);
        OnTravelReleased?.Invoke(actorNpcId, gameStamp);
        return new TravelCompletionResult(TravelCompletionStatus.Cancelled, "travel interrupted", 0, TravelReleaseReason.ExplicitCancel);
    }

    private static TravelReleaseReason ReasonForAnchor(string? anchor) => anchor switch
    {
        "FESTIVAL" => TravelReleaseReason.HardAnchorFestival,
        "SLEEP" => TravelReleaseReason.HardAnchorSleep,
        "LOCATION_EVENT" => TravelReleaseReason.HardAnchorLocationEvent,
        _ => TravelReleaseReason.HardAnchorEvent, // "EVENT/CUTSCENE" and any unknown anchor
    };

    /// <summary>True when the NPC has an active travel lease (for arbitration conflicts).</summary>
    public bool IsTravelActive(string npcId) => _leases.IsActive(npcId);

    /// <summary>Reconcile: any active lease after a save/reload is a stale-lease violation (U7).</summary>
    public int ActiveLeaseCount => _leases.ActiveCount;

    private static TravelDestination? ResolveDestinationFromLease(BehaviorLease lease)
        => lease.AdapterParams is not null
            && lease.AdapterParams.TryGetValue("locationName", out var name)
            && name is string loc
            && lease.AdapterParams.TryGetValue("tileX", out var tx)
            && tx is long x
            && lease.AdapterParams.TryGetValue("tileY", out var ty)
            && ty is long y
            && lease.AdapterParams.TryGetValue("evidenceSource", out var es)
            && es is string src
            ? new TravelDestination(loc, (int)x, (int)y, src, "lease")
            : null;
}
