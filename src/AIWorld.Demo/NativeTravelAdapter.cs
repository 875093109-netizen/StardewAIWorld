using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace AIWorld.Demo;

/// <summary>One checked movement executor for travel, follow and schedule recovery. Main thread only.</summary>
public sealed class NativeTravelAdapter : ITravelAdapter, ITravelFailureSource
{
    private readonly IMonitor _monitor;
    private readonly Dictionary<string, TravelRouteExecution> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _executionIds = new(StringComparer.Ordinal);
    private long _sequence;
    private readonly Dictionary<string, (NPC Npc, bool IgnoreSchedule, GuardedNpcPath? Path)> _owners = new(StringComparer.Ordinal);
    public static int ForceSegmentFailureAfterWarps { get; set; } = -1;
    public static bool ForceTargetIllegalMidRoute { get; set; }
    public NativeTravelAdapter(IMonitor monitor) => _monitor = monitor;
    public string AdapterId => "NativeTravelAdapter";
    public string? GetFailureReason(string npcId) => GetRouteInfo(npcId)?.FailureReason;
    public bool IsAvailable => Game1.hasLoadedGame && Game1.IsMasterGame;
    public sealed record RouteInfo(bool Active, int CompletedWarps, string? FailureReason, int LegIndex, bool WarpPending,
        IReadOnlyList<TravelRouteHop> ExecutedWarps, string DestinationLocation) { public long ExecutionId { get; init; } }
    public RouteInfo? GetRouteInfo(string npcId) => _routes.TryGetValue(npcId, out var ex)
        ? new(true, ex.CompletedWarps, ex.FailureReason, ex.LegIndex, ex.WarpPending, ex.ExecutedWarps, ex.Route.TargetLocation) { ExecutionId = _executionIds[npcId] } : null;
    public bool Owns(string npcId, TravelDestination destination, long? executionId = null) => _routes.TryGetValue(npcId, out var ex)
        && ex.Route.TargetLocation == destination.LocationName && ex.Route.TargetTileX == destination.TileX && ex.Route.TargetTileY == destination.TileY
        && (executionId is null || _executionIds[npcId] == executionId);

    /// <summary>Pure observation: never calls IsArrived/Tick, advances a route or warps the NPC.</summary>
    public AIWorld.Core.Chat.NpcJobOverride? ReadJob(string npcId, AIWorld.Core.Chat.NpcJobOverrideKind kind)
    {
        if (!_routes.TryGetValue(npcId, out var route)) return null;
        bool owns = _owners.TryGetValue(npcId, out var owner) && owner.Npc.controller == owner.Path
            && owner.Npc.temporaryController is null;
        return new(kind, route.Route.TargetLocation, route.Route.TargetTileX, route.Route.TargetTileY, owns, route.FailureReason);
    }

    public AdapterResult Travel(string npcId, TravelDestination destination)
    {
        try { return Start(npcId, destination); }
        catch (Exception ex) { Cancel(npcId); return Fail("movementQueryFailed", ex.GetType().Name); }
    }
    private AdapterResult Start(string npcId, TravelDestination destination)
    {
        var npc = Game1.getCharacterFromName(npcId);
        if (!IsAvailable || npc?.currentLocation is not { } source) return Fail("worldUnavailable", "NPC/world not available on host");
        if (new SmapiHardAnchorProvider().IsHardAnchorActive(npcId)) return Fail("hardAnchor", "event, sleep or festival");
        if (npc.modData.ContainsKey("TheStardewSquad/RecruiterId")) return Fail("externalMovementOwner", "NPC is recruited by Squad");
        var target = Game1.getLocationFromName(destination.LocationName);
        if (!NpcMovementRules.CanEnter(npc, target) || !NpcMovementRules.CanStand(npc, target, destination.TileX, destination.TileY))
            return Fail("illegalDestinationTile", "destination is restricted or blocked");
        if (!NpcMovementRules.CanStand(npc, source, npc.TilePoint.X, npc.TilePoint.Y)) return Fail("startTileIllegal", "starting tile is blocked");
        var route = source.NameOrUniqueName == destination.LocationName
            ? new TravelRoute { StartLocation = source.NameOrUniqueName, TargetLocation = destination.LocationName,
                TargetTileX = destination.TileX, TargetTileY = destination.TileY, Hops = Array.Empty<TravelRouteHop>() }
            : TravelRoute.Plan(source.NameOrUniqueName, destination.LocationName, destination.TileX, destination.TileY, NpcMovementRules.Connections(npc));
        if (route is null) return Fail("noConnectedRoute", "no permitted directed route");
        Cancel(npcId);
        var execution = new TravelRouteExecution(route, ForceSegmentFailureAfterWarps, ForceTargetIllegalMidRoute);
        _routes[npcId] = execution;
        _executionIds[npcId] = ++_sequence;
        _owners[npcId] = (npc, npc.ignoreScheduleToday, null);
        npc.ignoreScheduleToday = true;
        npc.temporaryController = null;
        npc.DirectionsToNewLocation = null;
        npc.IsWalkingInSquare = false;
        npc.Sprite.StopAnimation();
        npc.Halt();
        if (!execution.Begin((loc, x, y) => StartLeg(npc, execution, loc, x, y)))
        { ReleaseControl(npcId); return Fail(execution.FailureReason ?? "pathSetupFailed", "no legal path"); }
        _monitor.Log($"[T1] TRAVEL_START npc={npcId} to={destination.LocationName}({destination.TileX},{destination.TileY}) hops={route.Hops.Count}", LogLevel.Info);
        return new() { AdapterId = AdapterId, CapabilityId = CapabilityRegistry.CapabilityIds.TravelToLocation,
            Status = "STARTED", Message = "checked movement started", SchemaVersion = "1" };
    }

    private bool StartLeg(NPC npc, TravelRouteExecution ex, string location, int x, int y)
    {
        var map = Game1.getLocationFromName(location);
        if (map is null || npc.currentLocation != map || !NpcMovementRules.CanEnter(npc, map)) return false;
        bool exit = ex.LegIndex < ex.Route.Hops.Count;
        if (exit && !NpcMovementRules.StillConnected(npc, ex.Route.Hops[ex.LegIndex])) return false;
        var ends = new List<Point> { new(x, y) };
        if (exit) ends.AddRange(new[] { new Point(x, y + 1), new Point(x, y - 1), new Point(x - 1, y), new Point(x + 1, y) });
        foreach (var end in ends)
        {
            if (!NpcMovementRules.CanStand(npc, map, end.X, end.Y)) continue;
            var path = PathFindController.findPath(npc.TilePoint, end, PathFindController.isAtEndPoint, map, npc, 2000);
            if (path is null || (path.Count == 0 && npc.TilePoint != end)) continue;
            // Vanilla exempts its endpoint from collision checks; validate all returned tiles explicitly.
            if (path.Any(p => !NpcMovementRules.CanStand(npc, map, p.X, p.Y))) continue;
            var guard = new GuardedNpcPath(npc, map, path, reason => ex.MarkFailed(reason));
            npc.controller = guard;
            var owner = _owners[npc.Name];
            _owners[npc.Name] = (owner.Npc, owner.IgnoreSchedule, guard);
            return true;
        }
        return false;
    }

    public bool IsArrived(string npcId, TravelDestination destination)
    {
        try { return CheckArrival(npcId, destination); }
        catch (Exception) { if (_routes.TryGetValue(npcId, out var ex)) ex.MarkFailed("movementQueryFailed"); ReleaseControl(npcId); return false; }
    }
    private bool CheckArrival(string npcId, TravelDestination destination)
    {
        var npc = Game1.getCharacterFromName(npcId);
        if (npc?.currentLocation is not { } map || !IsAvailable) return false;
        if (new SmapiHardAnchorProvider().IsHardAnchorActive(npcId)) { Cancel(npcId); return false; }
        if (_routes.TryGetValue(npcId, out var ex))
        {
            if (!Owns(npcId, destination)) return false;
            if (_owners.TryGetValue(npcId, out var owner) && npc.controller is not null && npc.controller != owner.Path)
                ex.MarkFailed("movementOwnerChanged");
            if (!NpcMovementRules.CanEnter(npc, Game1.getLocationFromName(destination.LocationName))
                || !NpcMovementRules.CanStand(npc, Game1.getLocationFromName(destination.LocationName), destination.TileX, destination.TileY))
                ex.MarkFailed("targetBecameIllegal:runtime");
            if (ex.Failed) { ReleaseControl(npcId); return false; }
            var step = ex.Tick(map.NameOrUniqueName, npc.TilePoint.X, npc.TilePoint.Y,
                npc.controller?.pathToEndPoint?.Count > 0, npc.isMoving(),
                (loc, x, y) => StartLeg(npc, ex, loc, x, y));
            if (step.Step == TravelRouteStep.RequestWarp)
            {
                var hop = step.WarpHop!;
                if (map.NameOrUniqueName != hop.SourceLocationName || Math.Abs(npc.TilePoint.X - hop.ExitX) + Math.Abs(npc.TilePoint.Y - hop.ExitY) > 1
                    || !NpcMovementRules.StillConnected(npc, hop) || Game1.getLocationFromName(hop.TargetLocationName) is not { } targetMap)
                    ex.MarkFailed("transitionNoLongerPermitted");
                else
                {
                    // The finished controller belongs to the old map; it must not tick after the transition.
                    if (_owners.TryGetValue(npcId, out var current) && npc.controller == current.Path) npc.controller = null;
                    Game1.warpCharacter(npc, targetMap, new Vector2(hop.EntryX, hop.EntryY));
                    if (npc.currentLocation != targetMap || npc.TilePoint != new Point(hop.EntryX, hop.EntryY))
                        ex.MarkFailed("transitionPostconditionFailed");
                    else ex.ConfirmWarp(hop);
                }
                if (ex.Failed) ReleaseControl(npcId);
                return false;
            }
            if (step.Step == TravelRouteStep.Failed || ex.Failed) { ReleaseControl(npcId); return false; }
            if (ex.WarpPending || ex.LegIndex != ex.Route.Hops.Count) return false;
        }
        if (map.NameOrUniqueName != destination.LocationName || npc.isMoving() || npc.controller?.pathToEndPoint?.Count > 0) return false;
        bool arrived = npc.TilePoint == new Point(destination.TileX, destination.TileY)
            && NpcMovementRules.CanEnter(npc, map) && NpcMovementRules.CanStand(npc, map, destination.TileX, destination.TileY);
        if (arrived) ReleaseControl(npcId);
        return arrived;
    }
    private void ReleaseControl(string npcId)
    {
        if (!_owners.Remove(npcId, out var owner)) return;
        if (owner.Npc.controller == owner.Path) { owner.Npc.controller = null; owner.Npc.Halt(); }
        owner.Npc.ignoreScheduleToday = owner.IgnoreSchedule;
    }
    public void Cancel(string npcId) { ReleaseControl(npcId); _routes.Remove(npcId); _executionIds.Remove(npcId); }
    public void Clear() { foreach (var id in _owners.Keys.ToArray()) Cancel(id); _routes.Clear(); _executionIds.Clear(); }
    public int EstimateTravelMinutes(string npcId, TravelDestination destination, double gameMinutesPerTile)
    {
        var npc = Game1.getCharacterFromName(npcId);
        if (npc?.currentLocation is null) return 30;
        var route = npc.currentLocation.NameOrUniqueName == destination.LocationName ? null
            : TravelRoute.Plan(npc.currentLocation.NameOrUniqueName, destination.LocationName, destination.TileX, destination.TileY, NpcMovementRules.Connections(npc));
        if (route is null && npc.currentLocation.NameOrUniqueName != destination.LocationName) return 60;
        int x = npc.TilePoint.X, y = npc.TilePoint.Y, tiles = 0;
        foreach (var hop in route?.Hops ?? Array.Empty<TravelRouteHop>())
        { tiles += Math.Abs(x - hop.ExitX) + Math.Abs(y - hop.ExitY); x = hop.EntryX; y = hop.EntryY; }
        tiles += Math.Abs(x - destination.TileX) + Math.Abs(y - destination.TileY);
        return (int)Math.Ceiling(tiles * Math.Clamp(gameMinutesPerTile, 0.1, 10) * 2 / Math.Max(1, npc.speed) / 10) * 10;
    }
    private AdapterResult Fail(string reason, string message) => new() { AdapterId = AdapterId,
        CapabilityId = CapabilityRegistry.CapabilityIds.TravelToLocation, Status = "FAILED", Message = message,
        SchemaVersion = "1", Detail = new Dictionary<string, object?> { ["failureReason"] = reason } };
}
