using System;
using System.Collections.Generic;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// TRAVEL 自然移动（PM HARD REQUIREMENT 2026-08-15）— 一条 source-grounded 的跨图连接跳。
/// 来源/出口与目标/入口全部取自当前游戏世界的 live warp registry（GameLocation.warps），
/// AIWorld 不维护任何硬编码地图路线表。
/// </summary>
public sealed record TravelRouteHop(
    string SourceLocationName, int ExitX, int ExitY,
    string TargetLocationName, int EntryX, int EntryY);

/// <summary>
/// TRAVEL 自然移动 — 跨图路线规划（纯函数，Core 无游戏依赖，离线可测）。
///
/// 路线 = 在 live warp 图（当前已加载 GameLocation.warps）上从起始地点到目标地点的最短跳序列。
/// 移动语义：逐段走到当前地图的真实出口 → 仅在抵达出口后执行该连接的 transition → 从对应
/// 入口出现 → 继续走到下一出口或最终目标。禁止直接 warp 到目标地图/目标 tile。
/// 同图移动不需要路线（null）；无连接路线 → null（fail closed，不得 warp）。
/// </summary>
public sealed class TravelRoute
{
    public required string StartLocation { get; init; }
    public required string TargetLocation { get; init; }
    public required int TargetTileX { get; init; }
    public required int TargetTileY { get; init; }

    /// <summary>跨图跳序列（按执行顺序）；空列表表示无需跨图（调用方自行处理同图）。</summary>
    public required IReadOnlyList<TravelRouteHop> Hops { get; init; }

    /// <summary>
    /// BFS 规划跨图路线。起点与目标相同时返回 null（同图移动不是路线规划的职责）。
    /// warps 为调用方预过滤后的连接表（live：仅已加载地点）；空/无路径 → null。
    /// </summary>
    public static TravelRoute? Plan(string startLocation, string targetLocation, int targetTileX, int targetTileY, IReadOnlyList<TravelWarpQuery> warps)
    {
        if (string.IsNullOrWhiteSpace(startLocation) || string.IsNullOrWhiteSpace(targetLocation))
            return null;
        if (string.Equals(startLocation, targetLocation, StringComparison.OrdinalIgnoreCase))
            return null;

        var adjacency = new Dictionary<string, List<TravelRouteHop>>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in warps)
        {
            if (string.IsNullOrWhiteSpace(w.SourceLocationName) || string.IsNullOrWhiteSpace(w.TargetName)) continue;
            if (!adjacency.TryGetValue(w.SourceLocationName, out var list))
                adjacency[w.SourceLocationName] = list = new List<TravelRouteHop>();
            list.Add(new TravelRouteHop(w.SourceLocationName, w.X, w.Y, w.TargetName, w.TargetX, w.TargetY));
        }

        var prev = new Dictionary<string, TravelRouteHop?>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startLocation };
        var queue = new Queue<string>();
        queue.Enqueue(startLocation);
        prev[startLocation] = null;
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (string.Equals(cur, targetLocation, StringComparison.OrdinalIgnoreCase)) break;
            if (!adjacency.TryGetValue(cur, out var edges)) continue;
            foreach (var hop in edges)
            {
                if (visited.Contains(hop.TargetLocationName)) continue;
                visited.Add(hop.TargetLocationName);
                prev[hop.TargetLocationName] = hop;
                queue.Enqueue(hop.TargetLocationName);
            }
        }

        if (!visited.Contains(targetLocation)) return null;

        var hops = new List<TravelRouteHop>();
        var cursor = targetLocation;
        while (true)
        {
            if (!prev.TryGetValue(cursor, out var hop) || hop is null) break;
            hops.Add(hop);
            cursor = hop.SourceLocationName;
        }
        hops.Reverse();
        return new TravelRoute
        {
            StartLocation = startLocation,
            TargetLocation = targetLocation,
            TargetTileX = targetTileX,
            TargetTileY = targetTileY,
            Hops = hops,
        };
    }
}

/// <summary>路线执行状态机的每 tick 结论。</summary>
public enum TravelRouteStep
{
    /// <summary>当前段仍在行走（或等待 warp 生效）。</summary>
    ContinueWalking,
    /// <summary>已抵达当前地图的真实出口，请求 adapter 执行该 source-grounded transition。</summary>
    RequestWarp,
    /// <summary>最终段完成（抵达目标 tile 附近）— 权威到达判定仍由 adapter 的 arrival predicate 完成。</summary>
    Arrived,
    /// <summary>失败（原因见 Detail）— NPC 停在最后一个已验证合法 tile，绝不瞬移回滚。</summary>
    Failed,
}

public sealed record TravelRouteTickResult(TravelRouteStep Step, string Detail, TravelRouteHop? WarpHop = null);

/// <summary>
/// TRAVEL 自然移动 — 多段路线执行状态机（Core，离线可测）。
///
/// adapter 每个 tick 喂入 NPC 的真实状态（location/tile/controller/移动中），并提供两个游戏
/// 动作回调：startPathing（创建 PathFindController，false = 该段路径建立失败）与 warp（由
/// adapter 在收到 RequestWarp 后执行并回填 ConfirmWarp）。
///
/// 失败语义（PM 硬性要求）：任何一段失败 → 停止当前 controller，NPC 停在最后一个已验证合法
/// tile，释放 TravelLease（由 handler 完成），绝不为了"回滚"把 NPC 瞬移回原点。本状态机不含
/// 任何恢复性瞬移；EMERGENCY_RECOVERY 由 adapter 在 NPC 处于非法状态时另行执行并计 FAIL。
/// </summary>
public sealed class TravelRouteExecution
{
    public const int ArrivalTileTolerance = 2;
    private const int WarpPendingTickLimit = 5;

    private readonly TravelRoute _route;
    private readonly int _failSegmentAfterWarps;      // QA seam：完成 N 次 warp 后强制下段失败（-1 = 关闭）
    private readonly bool _forceTargetIllegalOnFinalLeg; // QA seam：最终段开始时目标变为非法
    private readonly List<TravelRouteHop> _executedWarps = new();
    private int _warpPendingTicks;

    public TravelRouteExecution(TravelRoute route, int failSegmentAfterWarps = -1, bool forceTargetIllegalOnFinalLeg = false)
    {
        _route = route;
        _failSegmentAfterWarps = failSegmentAfterWarps;
        _forceTargetIllegalOnFinalLeg = forceTargetIllegalOnFinalLeg;
        LegIndex = 0;
        CurrentLegLocation = route.StartLocation;
        CurrentLegEnd = route.Hops.Count > 0
            ? (route.Hops[0].ExitX, route.Hops[0].ExitY)
            : (route.TargetTileX, route.TargetTileY);
    }

    public TravelRoute Route => _route;
    public int LegIndex { get; private set; }
    public bool WarpPending { get; private set; }
    public int CompletedWarps { get; private set; }
    public string? FailureReason { get; private set; }
    public bool Failed => FailureReason is not null;
    public IReadOnlyList<TravelRouteHop> ExecutedWarps => _executedWarps;

    /// <summary>当前段的目标 tile（下一出口或最终目标）。</summary>
    public (int X, int Y) CurrentLegEnd { get; private set; }

    /// <summary>当前段所在的地点。</summary>
    public string CurrentLegLocation { get; private set; }

    /// <summary>段 0 起步（Travel() 时刻调用）。false = 首段路径建立失败（fail closed，无任何移动）。</summary>
    public bool Begin(Func<string, int, int, bool> startPathing) => StartLeg(0, startPathing);

    /// <summary>adapter 实际执行了 warp 后调用（登记 transition + 进入 warp 待确认态）。</summary>
    public void ConfirmWarp(TravelRouteHop hop)
    {
        if (FailureReason is not null) return;
        _executedWarps.Add(hop);
        CompletedWarps++;
        WarpPending = true;
        _warpPendingTicks = 0;
        LegIndex++;
        // TARGET_ILLEGAL 场景：最后一次 warp 可能把 NPC 直接落在最终目标 tile（如
        // Town→Beach 入口 (38,0) == 最终 tile），IsArrived 会在同一次调用里判到达，不给
        // 下一个 tick 的 WarpPending→StartLeg 机会触发目标非法。这里在 warp 完成后立即
        // 标记失败，确保强制失败语义在任何到达谓词之前生效。
        if (_forceTargetIllegalOnFinalLeg && LegIndex == _route.Hops.Count && CompletedWarps >= 1)
        {
            FailureReason = "targetBecameIllegal:midRoute";
            WarpPending = false;
        }
    }

    /// <summary>外部原因失败（如目标 tile 移动途中被验证为非法）。</summary>
    public void MarkFailed(string reason)
    {
        if (FailureReason is null) FailureReason = reason;
    }

    /// <summary>
    /// 每 tick 推进。参数为 NPC 的实时状态；startPathing(loc, x, y) 由 adapter 提供。
    /// </summary>
    public TravelRouteTickResult Tick(
        string npcLocation, int npcTileX, int npcTileY,
        bool controllerActive, bool moving,
        Func<string, int, int, bool> startPathing)
    {
        if (FailureReason is not null)
            return new TravelRouteTickResult(TravelRouteStep.Failed, FailureReason);

        if (WarpPending)
        {
            var expected = LegIndex > 0 && LegIndex - 1 < _route.Hops.Count
                ? _route.Hops[LegIndex - 1].TargetLocationName
                : _route.TargetLocation;
            if (string.Equals(npcLocation, expected, StringComparison.OrdinalIgnoreCase))
            {
                WarpPending = false;
                _warpPendingTicks = 0;
                if (!StartLeg(LegIndex, startPathing))
                    return new TravelRouteTickResult(TravelRouteStep.Failed, FailureReason ?? "segmentPathFailed");
                return new TravelRouteTickResult(TravelRouteStep.ContinueWalking, $"leg {LegIndex} started after warp");
            }
            if (++_warpPendingTicks > WarpPendingTickLimit)
            {
                FailureReason = $"warpNeverApplied:expected={expected} actual={npcLocation}";
                return new TravelRouteTickResult(TravelRouteStep.Failed, FailureReason);
            }
            return new TravelRouteTickResult(TravelRouteStep.ContinueWalking, "warp pending");
        }

        if (!controllerActive && !moving)
        {
            var (ex, ey) = CurrentLegEnd;
            if (Math.Abs(npcTileX - ex) <= ArrivalTileTolerance && Math.Abs(npcTileY - ey) <= ArrivalTileTolerance)
            {
                if (LegIndex >= _route.Hops.Count)
                    return new TravelRouteTickResult(TravelRouteStep.Arrived, "final leg reached destination tile");
                var hop = _route.Hops[LegIndex];
                return new TravelRouteTickResult(TravelRouteStep.RequestWarp,
                    $"reached exit of '{hop.SourceLocationName}' at ({ex},{ey})", hop);
            }
            FailureReason = $"legEndNotReached:leg{LegIndex} end=({ex},{ey}) npc=({npcTileX},{npcTileY})";
            return new TravelRouteTickResult(TravelRouteStep.Failed, FailureReason);
        }

        return new TravelRouteTickResult(TravelRouteStep.ContinueWalking, $"walking leg {LegIndex}");
    }

    private bool StartLeg(int legIndex, Func<string, int, int, bool> startPathing)
    {
        LegIndex = legIndex;
        CurrentLegLocation = legIndex < _route.Hops.Count
            ? _route.Hops[legIndex].SourceLocationName
            : _route.TargetLocation;
        CurrentLegEnd = legIndex < _route.Hops.Count
            ? (_route.Hops[legIndex].ExitX, _route.Hops[legIndex].ExitY)
            : (_route.TargetTileX, _route.TargetTileY);

        if (_failSegmentAfterWarps >= 0 && CompletedWarps == _failSegmentAfterWarps)
        {
            FailureReason = $"segmentPathFailed:forced:afterWarp{CompletedWarps}";
            return false;
        }
        if (_forceTargetIllegalOnFinalLeg && legIndex == _route.Hops.Count && CompletedWarps >= 1)
        {
            FailureReason = "targetBecameIllegal:midRoute";
            return false;
        }
        if (!startPathing(CurrentLegLocation, CurrentLegEnd.X, CurrentLegEnd.Y))
        {
            FailureReason = $"segmentPathFailed:leg{legIndex}";
            return false;
        }
        return true;
    }
}
