using System;
using System.Collections.Generic;
using AIWorld.Core.Behaviors;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// TRAVEL 自然移动（PM HARD REQUIREMENT 2026-08-15）— 路线规划与多段执行状态机的确定性测试。
///
/// TravelRoute.Plan 是纯函数（warp 图 BFS）；TravelRouteExecution 是与游戏无关的多段状态机，
/// 离线通过 fake 动作驱动与真实生产相同的决策逻辑（R5 模式：生产算法在 Core，adapter 只提供
/// 游戏动作）。覆盖：
///   - 路线规划：直达/多跳/无连接/环路/最短优先
///   - 多段执行：走到出口 → RequestWarp → 入口确认 → 下一段 → 最终段到达
///   - 失败：首段失败（出口不可达）/ warp 后下段失败（无瞬移回滚）/ 目标移动途中非法 /
///     warp 未生效超时 / 段结束未到出口
/// </summary>
public class TravelRoutePlannerTests
{
    // 小型 warp 图：Town 与 Beach、SeedShop 与 Town 互连（双向），Hospital 只与 Town 互连，
    // Cave 孤立（无连接）。
    private static List<TravelWarpQuery> SmallGraph()
    {
        return new List<TravelWarpQuery>
        {
            new("SeedShop", 4, 18, "Town", 30, 90),
            new("Town", 29, 90, "SeedShop", 5, 19),
            new("Town", 30, 95, "Beach", 31, 6),
            new("Beach", 31, 5, "Town", 30, 96),
            new("Hospital", 2, 12, "Town", 12, 88),
            new("Town", 11, 88, "Hospital", 3, 13),
        };
    }

    // ── TravelRoute.Plan ─────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_DirectConnection_ReturnsOneHop()
    {
        var route = TravelRoute.Plan("SeedShop", "Town", 40, 20, SmallGraph());
        Assert.NotNull(route);
        Assert.Single(route!.Hops);
        Assert.Equal("SeedShop", route.Hops[0].SourceLocationName);
        Assert.Equal(4, route.Hops[0].ExitX);
        Assert.Equal(18, route.Hops[0].ExitY);
        Assert.Equal("Town", route.Hops[0].TargetLocationName);
        Assert.Equal(30, route.Hops[0].EntryX);
        Assert.Equal(90, route.Hops[0].EntryY);
        Assert.Equal(40, route.TargetTileX);
        Assert.Equal(20, route.TargetTileY);
    }

    [Fact]
    public void Plan_MultiHop_ReturnsShortestPath()
    {
        var route = TravelRoute.Plan("Hospital", "Beach", 31, 6, SmallGraph());
        Assert.NotNull(route);
        Assert.Equal(2, route!.Hops.Count);
        Assert.Equal("Hospital", route.Hops[0].SourceLocationName);
        Assert.Equal("Town", route.Hops[0].TargetLocationName);
        Assert.Equal("Town", route.Hops[1].SourceLocationName);
        Assert.Equal("Beach", route.Hops[1].TargetLocationName);
    }

    [Fact]
    public void Plan_NoConnection_ReturnsNull()
    {
        var warps = SmallGraph();
        warps.Add(new TravelWarpQuery("Cave", 1, 1, "Cave", 2, 2)); // 自环，不连接外界
        Assert.Null(TravelRoute.Plan("Cave", "Beach", 31, 6, warps));
        Assert.Null(TravelRoute.Plan("Beach", "Cave", 1, 1, warps));
    }

    [Fact]
    public void Plan_SameLocation_ReturnsNull()
    {
        Assert.Null(TravelRoute.Plan("Town", "Town", 1, 1, SmallGraph()));
    }

    [Fact]
    public void Plan_EmptyWarps_ReturnsNull()
    {
        Assert.Null(TravelRoute.Plan("Town", "Beach", 1, 1, Array.Empty<TravelWarpQuery>()));
    }

    [Fact]
    public void Plan_StartIsolatedFromTarget_ReturnsNull()
    {
        // Hospital 有连接但目标不存在于图中。
        Assert.Null(TravelRoute.Plan("Hospital", "Nowhere", 1, 1, SmallGraph()));
    }

    // ── TravelRouteExecution：多段自然移动状态机 ────────────────────────────────────

    private sealed record FakeWorld(string Loc, int X, int Y);

    /// <summary>双跳路线（Hospital → Town → Beach）+ 最终段到 (31,6)。</summary>
    private static TravelRoute TwoHopRoute() => TravelRoute.Plan("Hospital", "Beach", 31, 6, SmallGraph())!;

    private sealed class ScriptedAdapter
    {
        public int PathingCalls;
        public int WarpCalls;
        public List<string> PathingTargets = new();
        public Func<string, int, int, bool> PathingResult = (_, _, _) => true;

        public bool StartPathing(string loc, int x, int y)
        {
            PathingCalls++;
            PathingTargets.Add($"{loc}({x},{y})");
            return PathingResult(loc, x, y);
        }
    }

    [Fact]
    public void Execution_TwoHopRoute_WalksWarpsWalksArrives()
    {
        var ex = new TravelRouteExecution(TwoHopRoute());
        var actions = new ScriptedAdapter();
        Assert.True(ex.Begin(actions.StartPathing));
        Assert.Equal("Hospital(2,12)", actions.PathingTargets[0]); // 段 0 走向 Hospital 的出口

        // 段 0 行走中。
        var r = ex.Tick("Hospital", 1, 10, controllerActive: true, moving: true, actions.StartPathing);
        Assert.Equal(TravelRouteStep.ContinueWalking, r.Step);

        // 段 0 完成：controller 结束、NPC 抵达出口 (2,12) → 请求 warp。
        r = ex.Tick("Hospital", 2, 12, controllerActive: false, moving: false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.RequestWarp, r.Step);
        Assert.NotNull(r.WarpHop);
        Assert.Equal("Town", r.WarpHop!.TargetLocationName);
        Assert.Equal(0, ex.CompletedWarps);

        // adapter 执行 warp（Game1.warpCharacter 等价物）后确认。
        ex.ConfirmWarp(r.WarpHop);
        Assert.Equal(1, ex.CompletedWarps);
        Assert.True(ex.WarpPending);

        // warp 待确认：位置还没变 → 继续等待。
        r = ex.Tick("Hospital", 2, 12, controllerActive: false, moving: false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.ContinueWalking, r.Step);

        // 下一 tick 位置已在 Town 入口 (30,90) → 开始段 1（走向 Beach 出口 (30,95)）。
        r = ex.Tick("Town", 30, 90, controllerActive: false, moving: false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.ContinueWalking, r.Step);
        Assert.Equal("Town(30,95)", actions.PathingTargets[1]);

        // 段 1 完成 → 第二次 RequestWarp。
        r = ex.Tick("Town", 30, 95, controllerActive: false, moving: false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.RequestWarp, r.Step);
        Assert.Equal("Beach", r.WarpHop!.TargetLocationName);
        ex.ConfirmWarp(r.WarpHop);
        Assert.Equal(2, ex.CompletedWarps);

        // Beach 入口确认 → 最终段（走向目标 tile (31,6)）。
        r = ex.Tick("Beach", 31, 6, controllerActive: false, moving: false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.ContinueWalking, r.Step);
        Assert.Equal("Beach(31,6)", actions.PathingTargets[2]);

        // 最终段完成 → Arrived（权威到达判定交给 adapter 的 arrival predicate）。
        r = ex.Tick("Beach", 31, 6, controllerActive: false, moving: false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.Arrived, r.Step);
        Assert.False(ex.Failed);
    }

    [Fact]
    public void Execution_FirstSegmentFails_FailClosedNoMovement()
    {
        var ex = new TravelRouteExecution(TwoHopRoute());
        var actions = new ScriptedAdapter { PathingResult = (_, _, _) => false };
        Assert.False(ex.Begin(actions.StartPathing));
        Assert.True(ex.Failed);
        Assert.Equal("segmentPathFailed:leg0", ex.FailureReason);
        Assert.Equal(0, ex.CompletedWarps);
        Assert.Equal(0, actions.WarpCalls); // 失败闭环：从未请求 warp
    }

    [Fact]
    public void Execution_SegmentAfterWarpFails_NpcStopsAtEntryNoTeleport()
    {
        var ex = new TravelRouteExecution(TwoHopRoute());
        var actions = new ScriptedAdapter();
        Assert.True(ex.Begin(actions.StartPathing));

        // 段 0 完成 → warp 1。
        var r = ex.Tick("Hospital", 2, 12, false, false, actions.StartPathing);
        ex.ConfirmWarp(r.WarpHop!);
        // Town 入口确认 → 段 1 开始（此处注入失败）。
        actions.PathingResult = (_, _, _) => false;
        r = ex.Tick("Town", 30, 90, false, false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.Failed, r.Step);
        Assert.Equal("segmentPathFailed:leg1", ex.FailureReason);
        Assert.Equal(1, ex.CompletedWarps);
        // 无任何恢复性 warp（无瞬移回滚）——NPC 停在入口，由 handler 释放租约并交回 canonical。
        Assert.Equal(0, actions.WarpCalls);
    }

    [Fact]
    public void Execution_ForcedSegmentFailureAfterWarps_DeterministicSeam()
    {
        var ex = new TravelRouteExecution(TwoHopRoute(), failSegmentAfterWarps: 1);
        var actions = new ScriptedAdapter();
        Assert.True(ex.Begin(actions.StartPathing)); // 段 0 正常开始（CompletedWarps=0，强制位=1 不触发）
        var r = ex.Tick("Hospital", 2, 12, false, false, actions.StartPathing);
        ex.ConfirmWarp(r.WarpHop!);
        r = ex.Tick("Town", 30, 90, false, false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.Failed, r.Step);
        Assert.Equal("segmentPathFailed:forced:afterWarp1", ex.FailureReason);

        // failSegmentAfterWarps=0 → 首段即失败（"出口不可达"确定性代表）。
        var ex0 = new TravelRouteExecution(TwoHopRoute(), failSegmentAfterWarps: 0);
        Assert.False(ex0.Begin(actions.StartPathing));
        Assert.Equal("segmentPathFailed:forced:afterWarp0", ex0.FailureReason);
    }

    [Fact]
    public void Execution_TargetIllegalMidRoute_FailsAtFinalLeg()
    {
        var ex = new TravelRouteExecution(TwoHopRoute(), forceTargetIllegalOnFinalLeg: true);
        var actions = new ScriptedAdapter();
        Assert.True(ex.Begin(actions.StartPathing));
        // 完整走完两跳。
        var r = ex.Tick("Hospital", 2, 12, false, false, actions.StartPathing);
        ex.ConfirmWarp(r.WarpHop!);
        r = ex.Tick("Town", 30, 90, false, false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.ContinueWalking, r.Step);
        r = ex.Tick("Town", 30, 95, false, false, actions.StartPathing);
        ex.ConfirmWarp(r.WarpHop!);
        // 最终段开始 → 目标非法 → 失败（NPC 停在 Beach 入口）。
        r = ex.Tick("Beach", 31, 6, false, false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.Failed, r.Step);
        Assert.Equal("targetBecameIllegal:midRoute", ex.FailureReason);
        Assert.Equal(2, ex.CompletedWarps);
    }

    [Fact]
    public void Execution_WarpNeverApplied_TimesOut()
    {
        var ex = new TravelRouteExecution(TwoHopRoute());
        var actions = new ScriptedAdapter();
        Assert.True(ex.Begin(actions.StartPathing));
        var r = ex.Tick("Hospital", 2, 12, false, false, actions.StartPathing);
        ex.ConfirmWarp(r.WarpHop!);
        // 位置从未变成 Town（warp 未生效）→ 有限重试后失败。
        TravelRouteTickResult last = r;
        for (int i = 0; i < 6; i++)
            last = ex.Tick("Hospital", 2, 12, false, false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.Failed, last.Step);
        Assert.StartsWith("warpNeverApplied", ex.FailureReason);
    }

    [Fact]
    public void Execution_ControllerEndsShortOfExit_FailsLegEndNotReached()
    {
        var ex = new TravelRouteExecution(TwoHopRoute());
        var actions = new ScriptedAdapter();
        Assert.True(ex.Begin(actions.StartPathing));
        // controller 结束但 NPC 离出口还远（寻路被截断）→ 段失败，绝不 RequestWarp。
        var r = ex.Tick("Hospital", 5, 5, false, false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.Failed, r.Step);
        Assert.StartsWith("legEndNotReached:leg0", ex.FailureReason);
        Assert.Equal(0, actions.WarpCalls);
    }

    [Fact]
    public void Execution_ConfirmedWarpsAreRecordedInOrder()
    {
        var ex = new TravelRouteExecution(TwoHopRoute());
        var actions = new ScriptedAdapter();
        Assert.True(ex.Begin(actions.StartPathing));
        var r = ex.Tick("Hospital", 2, 12, false, false, actions.StartPathing);
        ex.ConfirmWarp(r.WarpHop!);
        r = ex.Tick("Town", 30, 90, false, false, actions.StartPathing);
        Assert.Equal(TravelRouteStep.ContinueWalking, r.Step);
        r = ex.Tick("Town", 30, 95, false, false, actions.StartPathing);
        ex.ConfirmWarp(r.WarpHop!);
        Assert.Equal(2, ex.ExecutedWarps.Count);
        Assert.Equal("Hospital", ex.ExecutedWarps[0].SourceLocationName);
        Assert.Equal("Town", ex.ExecutedWarps[1].SourceLocationName);
    }
}
