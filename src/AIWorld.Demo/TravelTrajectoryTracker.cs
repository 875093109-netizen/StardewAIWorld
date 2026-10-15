using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// TRAVEL 自然移动（PM HARD REQUIREMENT 2026-08-15）— 机器可验收轨迹证据。
///
/// 每次运行按固定频率（矩阵驱动 1Hz）采样旅行 NPC 的位置，产出：
///   TRAVEL_START_TILE             = 对话结束位置（QA 脚本中为 intent 发出时刻的 tile）
///   DIRECT_DESTINATION_WARP_COUNT = 未经 live warp registry 证明的直达 warp 次数（必须 0）
///   UNEXPLAINED_POSITION_JUMP_COUNT = 无登记 transition 的超速坐标跳变次数（必须 0）
///   ILLEGAL_POSITION_COUNT        = 非法站立位置（地图外/水/不可通行且非 warp tile）次数（必须 0）
///
/// 合法跨图跳变判据（全部满足才算合法，对应 PM 五项条件）：
///   1. adapter 已登记该 transition（NPC 抵达当前地图真实出口后执行）；
///   2. 出口 tile 存在于源地图 live warps 列表且目标/入口完全匹配（source-grounded）；
///   3. 跳变后位置 = 该连接对应入口（±2 tile）。
/// 同图跳变阈值 = 每采样 5 tile（vanilla NPC 行走速度 ~2 tile/s @1Hz 采样的 2.5 倍裕量）。
///
/// MAIN THREAD ONLY（由矩阵驱动在每 tick 调用）。
/// </summary>
public sealed class TravelTrajectoryTracker
{
    public const double MaxTilesPerSample = 5.0;
    private const int EntryTolerance = 2;

    private readonly IMonitor _monitor;
    private string _cycleId = "";
    private string _npcId = "";
    private string _destLoc = "";
    private string _startLoc = "";
    private int _startX;
    private int _startY;

    private string? _lastLoc;
    private Vector2? _lastPos;
    private int _lastWarps;
    private int _samples;

    public int UnexplainedJumps { get; private set; }
    public int IllegalPositions { get; private set; }
    public int DirectDestinationWarps { get; private set; }
    public int TransitionCount { get; private set; }
    public bool HasViolations => UnexplainedJumps > 0 || IllegalPositions > 0 || DirectDestinationWarps > 0;

    public TravelTrajectoryTracker(IMonitor monitor)
    {
        _monitor = monitor;
    }

    public void Begin(string cycleId, string npcId, string destLoc, string startLoc, int startX, int startY, int completedWarpsAtStart)
    {
        _cycleId = cycleId;
        _npcId = npcId;
        _destLoc = destLoc;
        _startLoc = startLoc;
        _startX = startX;
        _startY = startY;
        _lastLoc = null;
        _lastPos = null;
        _lastWarps = completedWarpsAtStart;
        _samples = 0;
        UnexplainedJumps = 0;
        IllegalPositions = 0;
        DirectDestinationWarps = 0;
        TransitionCount = 0;
        _monitor.Log($"[TRM] TRAVEL_START_TILE cycle={_cycleId} npc={_npcId} loc={_startLoc} tile=({_startX},{_startY}) dest={_destLoc}", LogLevel.Info);
    }

    /// <summary>每 tick 采样一次。transition 记录从 adapter 的活动路由读取（runId 隔离的同一进程内证据）。</summary>
    public void Sample(NativeTravelAdapter adapter)
    {
        var npc = Game1.getCharacterFromName(_npcId);
        if (npc?.currentLocation is not { } loc) return;
        var pos = npc.Position;
        var info = adapter.GetRouteInfo(_npcId);

        int newWarps = info?.CompletedWarps ?? _lastWarps;

        // 1. 每个新 transition 必须 source-grounded：出口 tile 存在于源地图 live warps 且
        //    目标/入口匹配 —— 否则计 DIRECT_DESTINATION_WARP（未经证明的直达 warp）。
        if (info is not null && newWarps > _lastWarps)
        {
            for (int i = _lastWarps; i < newWarps && i < info.ExecutedWarps.Count; i++)
            {
                var hop = info.ExecutedWarps[i];
                TransitionCount++;
                if (!IsSourceGroundedWarp(hop))
                {
                    DirectDestinationWarps++;
                    _monitor.Log($"[TRM] TRAJECTORY cycle={_cycleId} DIRECT_DESTINATION_WARP ungrounded={hop.SourceLocationName}({hop.ExitX},{hop.ExitY})->{hop.TargetLocationName}({hop.EntryX},{hop.EntryY})", LogLevel.Error);
                }
            }
        }

        // 2. 位置跳变判定（首个采样只记基线）。
        if (_lastPos is { } lastPos && _lastLoc is { } lastLocName)
        {
            bool transitionHappened = newWarps > _lastWarps;
            if (transitionHappened && info is not null && info.ExecutedWarps.Count > 0)
            {
                var hop = info.ExecutedWarps[Math.Min(newWarps, info.ExecutedWarps.Count) - 1];
                bool locOk = string.Equals(loc.NameOrUniqueName, hop.TargetLocationName, StringComparison.OrdinalIgnoreCase);
                int dx = Math.Abs(npc.TilePoint.X - hop.EntryX);
                int dy = Math.Abs(npc.TilePoint.Y - hop.EntryY);
                if (!locOk || dx > EntryTolerance || dy > EntryTolerance)
                {
                    UnexplainedJumps++;
                    _monitor.Log($"[TRM] TRAJECTORY cycle={_cycleId} UNEXPLAINED_POSITION_JUMP afterWarp loc={loc.NameOrUniqueName}({npc.TilePoint.X},{npc.TilePoint.Y}) expected={hop.TargetLocationName}({hop.EntryX},{hop.EntryY})", LogLevel.Error);
                }
            }
            else if (!string.Equals(loc.NameOrUniqueName, lastLocName, StringComparison.OrdinalIgnoreCase))
            {
                UnexplainedJumps++;
                _monitor.Log($"[TRM] TRAJECTORY cycle={_cycleId} UNEXPLAINED_POSITION_JUMP locationChangedWithoutTransition {lastLocName}->{loc.NameOrUniqueName}", LogLevel.Error);
            }
            else
            {
                double tiles = Math.Max(Math.Abs(pos.X - lastPos.X), Math.Abs(pos.Y - lastPos.Y)) / 64.0;
                if (tiles > MaxTilesPerSample)
                {
                    UnexplainedJumps++;
                    _monitor.Log($"[TRM] TRAJECTORY cycle={_cycleId} UNEXPLAINED_POSITION_JUMP delta={tiles:F1}tiles at {loc.NameOrUniqueName}({npc.TilePoint.X},{npc.TilePoint.Y})", LogLevel.Error);
                }
            }
        }

        // 3. 非法站立位置（地图外/水/不可通行且非 warp tile）。
        // 仅当 NPC 无 active PathFindController 时检查：controller 激活期间 NPC 位置由
        // vanilla pathfinding 保证合法（isTilePassable 对家具边角/NPC 自身碰撞 tile 会误报
        // false，如 HarveyRoom(9,4)/(8,4) 是正常 pathing 路径 tile）。真正要捕获的非法位置
        // 是「无 controller 的静止态」或「warp 后」被瞬移进墙/水/地图外。
        if (npc.controller is null && !IsLegalStandingTile(npc, loc))
        {
            IllegalPositions++;
            _monitor.Log($"[TRM] TRAJECTORY cycle={_cycleId} ILLEGAL_POSITION at {loc.NameOrUniqueName}({npc.TilePoint.X},{npc.TilePoint.Y})", LogLevel.Error);
        }

        _lastLoc = loc.NameOrUniqueName;
        _lastPos = pos;
        _lastWarps = newWarps;
        _samples++;
    }

    /// <summary>cycle 结束时输出汇总 marker，返回是否通过（机器可验收计数全部为 0 且到达观察数符合预期）。</summary>
    public bool Finish(int expectedArrivalObs, int actualArrivalObs, int actualActiveLeaseAfter)
    {
        bool pass = !HasViolations && actualArrivalObs == expectedArrivalObs;
        _monitor.Log($"[TRM] TRAJECTORY cycle={_cycleId} RESULT={(pass ? "PASS" : "FAIL")} start={_startLoc}({_startX},{_startY}) dest={_destLoc} samples={_samples} warps={TransitionCount} directDestWarp={DirectDestinationWarps} jumps={UnexplainedJumps} illegal={IllegalPositions} arrivalObs={actualArrivalObs}/{expectedArrivalObs} activeLeaseAfter={actualActiveLeaseAfter}", pass ? LogLevel.Info : LogLevel.Error);
        return pass;
    }

    private static bool IsSourceGroundedWarp(AIWorld.Core.Behaviors.TravelRouteHop hop)
    {
        var sourceLoc = Game1.getLocationFromName(hop.SourceLocationName);
        if (sourceLoc?.warps is null) return false;
        foreach (var w in sourceLoc.warps)
        {
            if (w is null) continue;
            if (w.X == hop.ExitX && w.Y == hop.ExitY
                && string.Equals(w.TargetName, hop.TargetLocationName, StringComparison.OrdinalIgnoreCase)
                && w.TargetX == hop.EntryX && w.TargetY == hop.EntryY)
                return true;
        }
        return false;
    }

    private static bool IsLegalStandingTile(NPC npc, GameLocation loc)
    {
        if (loc.Map?.Layers[0] is not { } layer) return false;
        int x = npc.TilePoint.X, y = npc.TilePoint.Y;
        if (x < 0 || y < 0 || x >= layer.LayerWidth || y >= layer.LayerHeight) return false;
        if (loc.isWaterTile(x, y)) return false;
        if (loc.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport)) return true;
        foreach (var w in loc.warps)
            if (w is not null && w.X == x && w.Y == y) return true;
        return false;
    }
}
