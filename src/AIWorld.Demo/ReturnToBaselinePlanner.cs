using AIWorld.Core.Behaviors;
using StardewModdingAPI;

namespace AIWorld.Demo;

/// <summary>Schedule recovery uses the same movement checks as travel. No teleport fallback.</summary>
public sealed class ReturnToBaselinePlanner
{
    private readonly IMonitor _monitor;
    private readonly NativeTravelAdapter _adapter;
    private readonly Dictionary<string, (TravelDestination Dest, int Ticks, long ExecutionId)> _pending = new(StringComparer.Ordinal);
    public ReturnToBaselinePlanner(IMonitor monitor, NativeTravelAdapter adapter) { _monitor = monitor; _adapter = adapter; }
    public enum PlanResultKind { AlreadyAtBaseline, PathingStarted, WarpFallback, Failed }
    public sealed record PlanOutcome(PlanResultKind Kind, string Detail);
    public AIWorld.Core.Chat.NpcJobOverride? ReadJob(string npcId) => _pending.TryGetValue(npcId, out var state)
        && _adapter.Owns(npcId, state.Dest, state.ExecutionId)
            ? _adapter.ReadJob(npcId, AIWorld.Core.Chat.NpcJobOverrideKind.ReturnToSchedule) : null;
    public PlanOutcome Execute(string npcId, BaselineResolver.BaselineTarget baseline)
    {
        var dest = new TravelDestination(baseline.LocationName, baseline.TileX, baseline.TileY, "baseline", baseline.Source);
        _pending.Remove(npcId);
        if (_adapter.IsArrived(npcId, dest)) return new(PlanResultKind.AlreadyAtBaseline, "already at a legal baseline tile");
        var result = _adapter.Travel(npcId, dest);
        if (result.Status != "STARTED") return new(PlanResultKind.Failed, result.Message);
        _pending[npcId] = (dest, 0, _adapter.GetRouteInfo(npcId)!.ExecutionId);
        return new(PlanResultKind.PathingStarted, "checked route back to schedule");
    }
    public void Poll()
    {
        foreach (var (id, state) in _pending.ToArray())
        {
            if (!_adapter.Owns(id, state.Dest, state.ExecutionId)) { _pending.Remove(id); continue; }
            if (state.Ticks >= 180 || _adapter.GetRouteInfo(id)?.FailureReason is not null)
            { _adapter.Cancel(id); _pending.Remove(id); _monitor.Log($"[MOVEMENT] REJOIN_STOPPED npc={id}", LogLevel.Info); continue; }
            if (_adapter.IsArrived(id, state.Dest)) { _pending.Remove(id); _adapter.Cancel(id); continue; }
            _pending[id] = (state.Dest, state.Ticks + 1, state.ExecutionId);
        }
    }
}
