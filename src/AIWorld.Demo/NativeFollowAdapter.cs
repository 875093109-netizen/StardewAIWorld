using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using Microsoft.Xna.Framework;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>AIWorld-owned follow uses checked walking. It does not recruit or alter manual Squad companions.</summary>
public sealed class NativeFollowAdapter : IFollowAdapter
{
    private sealed class Following(NPC npc, bool ignore)
    {
        public NPC Npc = npc;
        public bool PreviousIgnoreSchedule = ignore;
        public TravelDestination? Destination;
        public int FailedTicks;
        public int TravelTicks;
        public long ExecutionId;
    }
    private readonly NativeTravelAdapter _movement;
    private readonly Func<string, bool> _travelLease;
    private readonly Dictionary<string, Following> _followers = new(StringComparer.Ordinal);
    public NativeFollowAdapter(NativeTravelAdapter movement, Func<string, bool> travelLease) { _movement = movement; _travelLease = travelLease; }
    public string AdapterId => "NativeFollowAdapter";
    public bool IsAvailable => _movement.IsAvailable;
    public bool IsFollowing(string npcId) => _followers.ContainsKey(npcId);
    public Action<string, string>? OnStopped { get; set; }

    public AdapterResult StartFollowing(string npcId, string playerId)
    {
        var npc = Game1.getCharacterFromName(npcId);
        if (!IsAvailable || npc?.currentLocation is null || Game1.player?.currentLocation is null
            || Game1.player.UniqueMultiplayerID.ToString() != playerId || _travelLease(npcId)
            || npc.modData.ContainsKey("TheStardewSquad/RecruiterId") || new SmapiHardAnchorProvider().IsHardAnchorActive(npcId))
            return Result(false, "follow precondition rejected");
        if (IsFollowing(npcId)) return Result(true, "already following");
        var state = new Following(npc, npc.ignoreScheduleToday);
        // First legal route must exist before accepting the follow request.
        if (!StartTowardPlayer(state)) return Result(false, "no legal route to player");
        _followers[npcId] = state;
        npc.ignoreScheduleToday = true;
        npc.temporaryController = null; npc.DirectionsToNewLocation = null; npc.IsWalkingInSquare = false;
        return Result(true, "checked follow active");
    }

    public AdapterResult StopFollowing(string npcId, string playerId)
    { Stop(npcId); return Result(true, "follow stopped", CapabilityRegistry.CapabilityIds.StopFollowing); }

    private void Stop(string id)
    {
        if (!_followers.Remove(id, out var state)) return;
        // Do not cancel a new route that superseded our particular target.
        if (state.Destination is not null && _movement.Owns(id, state.Destination, state.ExecutionId)) _movement.Cancel(id);
        state.Npc.ignoreScheduleToday = state.PreviousIgnoreSchedule;
    }
    public void Clear() { foreach (var id in _followers.Keys.ToArray()) Stop(id); }

    /// <summary>Called once per second, not per frame; no LLM or global NPC scan.</summary>
    public void Tick()
    {
        foreach (var (id, state) in _followers.ToArray())
        {
            string? stop = null;
            try
            {
                var player = Game1.player;
                if (!IsAvailable || player?.currentLocation is null || Game1.getCharacterFromName(id) != state.Npc
                    || new SmapiHardAnchorProvider().IsHardAnchorActive(id) || state.Npc.modData.ContainsKey("TheStardewSquad/RecruiterId")) stop = "followInterrupted";
                else if (_travelLease(id)) stop = "movementOwnerChanged";
                else if (state.Destination is { } dest)
                {
                    if (!_movement.Owns(id, dest, state.ExecutionId)) stop = "movementOwnerChanged";
                    else
                    {
                        bool arrived = _movement.IsArrived(id, dest);
                        if (_movement.GetRouteInfo(id)?.FailureReason is not null || ++state.TravelTicks > 180) stop = "followRouteFailed";
                        else if (arrived) { _movement.Cancel(id); state.Destination = null; state.Npc.ignoreScheduleToday = true; }
                    }
                }
                else if (!NearPlayer(state.Npc) && !StartTowardPlayer(state))
                { if (++state.FailedTicks >= 3) stop = "noLegalFollowRoute"; }
                else state.FailedTicks = 0;
            }
            catch (Exception) { stop = "followDataUnavailable"; }
            if (stop is not null) { Stop(id); OnStopped?.Invoke(id, stop); }
        }
    }

    private static bool NearPlayer(NPC npc) => Game1.player is { } player && npc.currentLocation == player.currentLocation
        && Math.Abs(npc.TilePoint.X - player.TilePoint.X) + Math.Abs(npc.TilePoint.Y - player.TilePoint.Y) <= 2
        && NpcMovementRules.CanEnter(npc, npc.currentLocation) && NpcMovementRules.CanStand(npc, npc.currentLocation, npc.TilePoint.X, npc.TilePoint.Y);

    private bool StartTowardPlayer(Following state)
    {
        if (NearPlayer(state.Npc)) { _movement.Cancel(state.Npc.Name); state.Npc.controller = null; state.Npc.Halt(); return true; }
        var player = Game1.player!;
        var map = player.currentLocation;
        if (!NpcMovementRules.CanEnter(state.Npc, map)) return false;
        var p = player.TilePoint;
        foreach (var tile in new[] { new Point(p.X, p.Y + 1), new Point(p.X, p.Y - 1), new Point(p.X - 1, p.Y), new Point(p.X + 1, p.Y) })
        {
            if (!NpcMovementRules.CanStand(state.Npc, map, tile.X, tile.Y)) continue;
            var destination = new TravelDestination(map!.NameOrUniqueName, tile.X, tile.Y, "player-neighbor", "live player position");
            if (_movement.Travel(state.Npc.Name, destination).Status != "STARTED") continue;
            state.Destination = destination; state.ExecutionId = _movement.GetRouteInfo(state.Npc.Name)!.ExecutionId;
            state.TravelTicks = 0; return true;
        }
        return false;
    }
    private AdapterResult Result(bool success, string message, string capability = CapabilityRegistry.CapabilityIds.FollowPlayer) => new()
    { AdapterId = AdapterId, CapabilityId = capability, Status = success ? "SUCCESS" : "FAILED", Message = message, SchemaVersion = "1" };
}
