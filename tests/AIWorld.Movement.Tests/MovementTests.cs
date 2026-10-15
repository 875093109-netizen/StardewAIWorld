using AIWorld.Core.Behaviors;
using AIWorld.Demo;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
public sealed class MovementTests : IDisposable
{
    private readonly GameLocation _town = new();
    private readonly NPC _npc = new() { Name = "Abigail", TilePoint = new(5, 5) };
    private readonly MonitorDouble _log = new();
    private readonly NativeTravelAdapter _travel;
    public MovementTests()
    {
        Game1.locations.Clear(); Game1.Npcs.Clear(); Game1.Warps = 0;
        Game1.locations.Add(_town); _npc.currentLocation = _town; Game1.Npcs[_npc.Name] = _npc;
        Game1.currentLocation = _town; Game1.player = new() { currentLocation = _town, TilePoint = new(9, 5) };
        Game1.IsMasterGame = true; Game1.timeOfDay = 900;
        GameLocation.FestivalClosed = false; WarpPathfindingCache.GenderRestrictions.Clear();
        NativeTravelAdapter.ForceSegmentFailureAfterWarps = -1; NativeTravelAdapter.ForceTargetIllegalMidRoute = false;
        PathFindController.ThrowOnCreate = PathFindController.NoPath = false; PathFindController.Find = null;
        SmapiHardAnchorProvider.Blocked = false;
        _travel = new(_log);
    }
    public void Dispose() { Game1.locations.Clear(); Game1.Npcs.Clear(); }
    private TravelDestination Dest(int x = 8, int y = 5) => new("Town", x, y, "test", "live");
    [Fact] public void JobObservationCannotAdvanceMovementAndDetectsControllerTakeover()
    {
        Assert.Null(_travel.ReadJob(_npc.Name, AIWorld.Core.Chat.NpcJobOverrideKind.Travel));
        Assert.Equal("STARTED", _travel.Travel(_npc.Name, Dest()).Status);
        var path = _npc.controller!; int count = path.pathToEndPoint!.Count;
        var job = _travel.ReadJob(_npc.Name, AIWorld.Core.Chat.NpcJobOverrideKind.Travel)!;
        Assert.True(job.OwnsMovement); Assert.Equal(8, job.X); Assert.Equal("Town", job.Target);
        Assert.Same(path, _npc.controller); Assert.Equal(count, path.pathToEndPoint.Count); Assert.Equal(0, Game1.Warps);
        _npc.controller = null;
        Assert.False(_travel.ReadJob(_npc.Name, AIWorld.Core.Chat.NpcJobOverrideKind.Travel)!.OwnsMovement);
        _travel.Cancel(_npc.Name);
        Assert.Null(_travel.ReadJob(_npc.Name, AIWorld.Core.Chat.NpcJobOverrideKind.Travel));
    }
    [Fact] public void ReturnJobExistsOnlyForItsOwnRecoveryExecution()
    {
        var planner = new ReturnToBaselinePlanner(_log, _travel);
        Assert.Null(planner.ReadJob(_npc.Name));
        planner.Execute(_npc.Name, new("Town", 8, 5, "schedule"));
        Assert.Equal(AIWorld.Core.Chat.NpcJobOverrideKind.ReturnToSchedule, planner.ReadJob(_npc.Name)!.Kind);
        _travel.Travel(_npc.Name, Dest(7, 5)); // another request supersedes recovery
        Assert.Null(planner.ReadJob(_npc.Name));
    }
    [Fact] public void ArrivalRejectsDestinationBlockedAfterDeparture()
    {
        _npc.TilePoint = new(8, 5); _town.Blocked.Add(new(8, 5));
        Assert.False(_travel.IsArrived(_npc.Name, Dest()));
    }
    [Fact] public void MissingPathIsFailureNotStarted()
    {
        PathFindController.NoPath = true;
        Assert.Equal("FAILED", _travel.Travel(_npc.Name, Dest()).Status);
        Assert.Null(_npc.controller); Assert.Equal(0, Game1.Warps);
    }
    [Fact] public void BaselineRecoveryCannotTeleportOnPathFailure()
    {
        PathFindController.ThrowOnCreate = true; PathFindController.NoPath = true;
        var planner = new ReturnToBaselinePlanner(_log, _travel);
        var outcome = planner.Execute(_npc.Name, new("Town", 8, 5, "schedule"));
        Assert.Equal(ReturnToBaselinePlanner.PlanResultKind.Failed, outcome.Kind);
        Assert.Equal(0, Game1.Warps); Assert.Equal(new Point(5, 5), _npc.TilePoint);
    }
    [Fact] public void RealDoorResolvesWithoutInventingReverseConnections()
    {
        var inside = Door();
        var resolved = new StardewTravelDestinationResolver(_log).Resolve(_npc.Name, "Shop");
        Assert.Equal(TravelDestinationStatus.Resolved, resolved.Status);
        Assert.Equal(2, resolved.Destination!.TileX);
        _town.doors.Data.Clear(); _town.DoorWarps.Clear();
        inside.warps.Add(new(2, 3, "Town", 7, 5));
        Assert.Equal(TravelDestinationStatus.NoArrivalTile, new StardewTravelDestinationResolver(_log).Resolve(_npc.Name, "Shop").Status);
        Assert.DoesNotContain(NpcMovementRules.Connections(_npc), c => c.SourceLocationName == "Town" && c.TargetName == "Shop");
    }
    [Fact] public void LockedDoorUsesNpcScheduleAuthorityNotPlayerKey()
    {
        Door("LockedDoorWarp 2 3 Shop 900 1700"); Game1.timeOfDay = 1800;
        Assert.Empty(NpcMovementRules.ConnectionsFrom(_npc, _town));
        _npc.Schedule = new() { [900] = new() { targetLocationName = "Shop", targetTile = new(4, 4) } };
        Assert.Single(NpcMovementRules.ConnectionsFrom(_npc, _town));
    }
    [Theory]
    [InlineData("UnknownModPortal 2 3 Shop")]
    [InlineData("LockedDoorWarp 2 3 Shop broken 1700")]
    [InlineData("LockedDoorWarp 2 3 Shop 900 1700 Someone 500")]
    public void UnknownOrUnprovenPermissionIsRejected(string action)
    { Door(action); Assert.Empty(NpcMovementRules.ConnectionsFrom(_npc, _town)); }
    [Fact] public void ExcludedMapAndNpcGenderRestrictionsAreApplied()
    {
        var inside = Door(); inside.Excluded = true;
        Assert.Empty(NpcMovementRules.ConnectionsFrom(_npc, _town));
        inside.Excluded = false; WarpPathfindingCache.GenderRestrictions["Shop"] = Gender.Male;
        Assert.Empty(NpcMovementRules.ConnectionsFrom(_npc, _town));
        _npc.Gender = Gender.Male; Assert.Single(NpcMovementRules.ConnectionsFrom(_npc, _town));
    }
    [Theory]
    [InlineData("NoPath", "Back")]
    [InlineData("NPCBarrier", "Back")]
    [InlineData("NPCBarrier", "Buildings")]
    public void TileRestrictionsRejectMovement(string property, string layer)
    {
        _town.Properties[(8, 5, property, layer)] = "T";
        Assert.Equal("FAILED", _travel.Travel(_npc.Name, Dest()).Status);
    }
    [Fact] public void BlockedIntermediateTileCannotEnterAPath()
    {
        _town.Blocked.Add(new(6, 5));
        PathFindController.Find = (_, _) => new(new[] { new Point(8, 5), new Point(6, 5), new Point(5, 5) });
        Assert.Equal("FAILED", _travel.Travel(_npc.Name, Dest()).Status);
    }
    [Fact] public void PathRechecksDynamicObstaclesAndHasNoImplicitWarpFlags()
    {
        Assert.Equal("STARTED", _travel.Travel(_npc.Name, Dest()).Status);
        var path = _npc.controller!;
        Assert.Equal(Point.Zero, path.endPoint); Assert.False(path.NPCSchedule); Assert.False(path.nonDestructivePathing);
        path.update(new()); _town.Blocked.Add(new(8, 5));
        Assert.True(path.update(new()));
        Assert.NotNull(_travel.GetRouteInfo(_npc.Name)!.FailureReason);
        Assert.False(_travel.IsArrived(_npc.Name, Dest())); Assert.False(_npc.ignoreScheduleToday); Assert.Equal(0, Game1.Warps);
    }
    [Fact] public void DoorClosingBeforeTransitionPreventsWarp()
    {
        Door("LockedDoorWarp 2 3 Shop 900 1700");
        var dest = new TravelDestination("Shop", 2, 3, "test", "door");
        Assert.Equal("STARTED", _travel.Travel(_npc.Name, dest).Status);
        FinishPath(); Game1.timeOfDay = 1800;
        Assert.False(_travel.IsArrived(_npc.Name, dest));
        Assert.Equal(0, Game1.Warps); Assert.Equal(_town, _npc.currentLocation);
    }
    [Fact] public void TransitionRequiresCurrentEntryTileAndVerifiedArrival()
    {
        var inside = Door(); var dest = new TravelDestination("Shop", 4, 3, "test", "door");
        Assert.Equal("STARTED", _travel.Travel(_npc.Name, dest).Status);
        FinishPath(); Assert.False(_travel.IsArrived(_npc.Name, dest));
        Assert.Equal(1, Game1.Warps); Assert.Equal(new Point(2, 3), _npc.TilePoint);
        Assert.False(_travel.IsArrived(_npc.Name, dest)); FinishPath();
        Assert.True(_travel.IsArrived(_npc.Name, dest)); Assert.False(_npc.ignoreScheduleToday);
    }
    [Fact] public void RemovedConnectionAndBlockedEntryAreRechecked()
    {
        var inside = Door(); var dest = new TravelDestination("Shop", 4, 3, "test", "door");
        _travel.Travel(_npc.Name, dest); FinishPath(); inside.Blocked.Add(new(2, 3));
        Assert.False(_travel.IsArrived(_npc.Name, dest)); Assert.Equal(0, Game1.Warps);
    }
    [Fact] public void StaleSameMapTargetDoesNotAdvanceOrCancelNewMovement()
    {
        _travel.Travel(_npc.Name, Dest()); _travel.Travel(_npc.Name, Dest(12));
        var current = _npc.controller;
        Assert.False(_travel.IsArrived(_npc.Name, Dest())); Assert.Same(current, _npc.controller);
        Assert.True(_travel.Owns(_npc.Name, Dest(12)));
    }
    [Fact] public void CancelPreservesAnExternalControllersOwnership()
    {
        _travel.Travel(_npc.Name, Dest());
        var other = new PathFindController(_npc, _town, new(13, 5), -1); _npc.controller = other;
        Assert.False(_travel.IsArrived(_npc.Name, Dest()));
        Assert.Same(other, _npc.controller); Assert.False(_npc.ignoreScheduleToday);
    }
    [Fact] public void FollowCannotRecruitSquadNpcOrStartDuringTravelLease()
    {
        var follow = new NativeFollowAdapter(_travel, _ => true);
        Assert.Equal("FAILED", follow.StartFollowing(_npc.Name, "1").Status);
        follow = new(_travel, _ => false); _npc.modData["TheStardewSquad/RecruiterId"] = "1";
        Assert.Equal("FAILED", follow.StartFollowing(_npc.Name, "1").Status);
        Assert.False(follow.IsFollowing(_npc.Name));
    }
    [Fact] public void FollowUsesMovementAndRestoresScheduleOnStop()
    {
        var follow = new NativeFollowAdapter(_travel, _ => false);
        Assert.Equal("SUCCESS", follow.StartFollowing(_npc.Name, "1").Status);
        Assert.True(follow.IsFollowing(_npc.Name)); Assert.True(_npc.ignoreScheduleToday);
        Assert.NotNull(_npc.controller);
        follow.StopFollowing(_npc.Name, "1");
        Assert.Null(_npc.controller); Assert.False(_npc.ignoreScheduleToday); Assert.Equal(0, Game1.Warps);
    }
    [Fact] public void FollowStopsAtHardAnchorWithoutTeleporting()
    {
        var follow = new NativeFollowAdapter(_travel, _ => false);
        follow.StartFollowing(_npc.Name, "1"); SmapiHardAnchorProvider.Blocked = true;
        string? stopped = null; follow.OnStopped = (_, reason) => stopped = reason; follow.Tick();
        Assert.False(follow.IsFollowing(_npc.Name)); Assert.Equal("followInterrupted", stopped); Assert.Equal(0, Game1.Warps);
    }
    [Fact] public void FollowDoesNotWarpAfterPlayerUsesDisconnectedTransport()
    {
        var follow = new NativeFollowAdapter(_travel, _ => false);
        Game1.player!.TilePoint = new(6, 5); follow.StartFollowing(_npc.Name, "1");
        var island = new GameLocation { NameOrUniqueName = "Island" }; Game1.locations.Add(island); Game1.player.currentLocation = island;
        follow.Tick(); follow.Tick(); follow.Tick();
        Assert.False(follow.IsFollowing(_npc.Name)); Assert.Equal(_town, _npc.currentLocation); Assert.Equal(0, Game1.Warps);
    }
    [Fact] public void FarmhandCannotMoveWorldNpc()
    { Game1.IsMasterGame = false; Assert.Equal("FAILED", _travel.Travel(_npc.Name, Dest()).Status); }
    [Fact] public void FailedTravelReleasesLeaseImmediatelyWithoutArrivalOrRejoin()
    {
        var leases = new TravelLeaseManager();
        var resolver = new DeterministicTravelDestinationResolver(new[] { ("Town", 8, 5, "test") });
        var arbiter = new BehaviorArbitrator(CapabilityRegistry.CreateDemoRegistry(), new FakeFollowAdapter(),
            new NoAnchors(), new DeterministicNpcResolver(_npc.Name), new DeterministicPlayerResolver("1"), resolver);
        var handler = new TravelIntentHandler(arbiter, resolver, _travel, leases);
        int arrivals = 0, rejoins = 0;
        handler.OnTravelArrived = (_, _, _) => arrivals++;
        handler.OnTravelReleased = (_, _) => rejoins++;
        var outcome = handler.Handle(new AIWorld.Core.Contracts.NpcIntent { IntentId = "route", Type = CapabilityRegistry.CapabilityIds.TravelToLocation,
            Target = "Town", Priority = 0.5f, SchemaVersion = "1" }, _npc.Name, "day1");
        Assert.NotNull(outcome.Lease); _town.Blocked.Add(new(8, 5));
        Assert.Equal(TravelIntentHandler.TravelCompletionStatus.Failed, handler.Complete(_npc.Name, "day1").Status);
        Assert.False(leases.IsActive(_npc.Name)); Assert.Equal(0, arrivals); Assert.Equal(0, rejoins); Assert.False(_npc.ignoreScheduleToday);
    }
    [Fact] public void RejoinDoesNotPollAReplacementWithIdenticalDestination()
    {
        var planner = new ReturnToBaselinePlanner(_log, _travel);
        planner.Execute(_npc.Name, new("Town", 8, 5, "schedule"));
        _travel.Travel(_npc.Name, Dest()); var id = _travel.GetRouteInfo(_npc.Name)!.ExecutionId;
        FinishPath(); planner.Poll();
        Assert.Equal(id, _travel.GetRouteInfo(_npc.Name)!.ExecutionId);
    }
    [Fact] public void OldMapControllerIsClearedBeforeConnectionTransition()
    {
        Door(); var dest = new TravelDestination("Shop", 4, 3, "test", "door");
        _travel.Travel(_npc.Name, dest);
        var old = _npc.controller!;
        while (old.pathToEndPoint!.Count > 0) old.update(new());
        _travel.IsArrived(_npc.Name, dest);
        Assert.Null(_npc.controller); Assert.Equal(1, Game1.Warps); Assert.Null(_travel.GetFailureReason(_npc.Name));
    }
    [Fact] public void BaselineCannotBypassBlockedOrOutOfBoundsTarget()
    {
        var planner = new ReturnToBaselinePlanner(_log, _travel);
        _town.Blocked.Add(new(8, 5));
        Assert.Equal(ReturnToBaselinePlanner.PlanResultKind.Failed, planner.Execute(_npc.Name, new("Town", 8, 5, "schedule")).Kind);
        Assert.Equal(ReturnToBaselinePlanner.PlanResultKind.Failed, planner.Execute(_npc.Name, new("Town", 400, 5, "schedule")).Kind);
        Assert.Equal(0, Game1.Warps);
    }
    [Fact] public void ClearRestoresControlAndFollowDoesNotSurviveSaveBoundary()
    {
        var follow = new NativeFollowAdapter(_travel, _ => false);
        follow.StartFollowing(_npc.Name, "1"); follow.Clear(); _travel.Clear();
        Assert.False(follow.IsFollowing(_npc.Name)); Assert.Null(_npc.controller); Assert.False(_npc.ignoreScheduleToday);
        Assert.Null(_travel.GetRouteInfo(_npc.Name));
    }
    private GameLocation Door(string action = "Warp 2 3 Shop")
    {
        var inside = new GameLocation { NameOrUniqueName = "Shop" }; Game1.locations.Add(inside);
        _town.doors.Data[new(7, 5)] = "Shop";
        _town.DoorWarps[new(7, 5)] = new(7, 5, "Shop", 2, 3);
        _town.Properties[(7, 5, "Action", "Buildings")] = action;
        return inside;
    }
    private void FinishPath()
    {
        for (int i = 0; i < 100 && _npc.controller is { } path; i++)
            if (path.update(new())) _npc.controller = null;
    }
    private sealed class MonitorDouble : IMonitor { public void Log(string message, LogLevel level) { } }
    private sealed class NoAnchors : IHardAnchorProvider
    { public bool IsHardAnchorActive(string _) => false; public string? ActiveAnchor(string _) => null; }
}
