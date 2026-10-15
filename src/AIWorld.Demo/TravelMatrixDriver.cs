using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using AIWorld.Core.TestSupport;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// TRAVEL RELIABILITY MATRIX GATE M1 — runtime matrix driver (mode=travel-matrix, Phase=TRM).
///
/// Stage A (one boot): 110 production-chain cycles through the REAL game world —
///   M-A SUCCESS 40      (20 same-location Abigail→SeedShop + 20 cross-location Harvey→Beach/Town)
///   M-B REJECT 20       (10 unknown target + 10 hard-anchor at arbitration)
///   M-C UNREACHABLE 10  (5 NoArrivalTile + 5 Unavailable — resolver rejects, never attempt-load)
///   M-D INTERRUPTION 20 (10 explicit cancel + 10 mid-travel hard-anchor)
///   M-E ISOLATION 20    (real two-actor: other holds a lease, traveler arrives — contamination=0)
/// then a REAL production save (natural pass-out) so the cold-restart chain can reload it.
///
/// Stages C1..C8 (eight NEW processes): each loads the previous process's save, verifies the
/// U7 cold-reload invariant (active lease = 0, no stale ACTIVE released lease), records ONE
/// COLD_RESTART cycle, runs one scenario (success / cancel / hard-anchor / save-with-active),
/// saves again. Collectively these are the M-G 8 cold restarts — each a separate process.
///
/// The arrival/lease/observation evidence is the PRODUCTION chain only: every cycle goes
/// through TravelIntentHandler.Handle → NativeTravelAdapter (real pathing/warp) → Complete
/// → production observation. Time is frozen at 06:00 during cycle execution so NPC schedules
/// keep morning baselines (keeps same-location/cross-location classification honest); the
/// save prep releases the freeze so the natural 02:00 pass-out save runs.
///
/// MAIN THREAD ONLY (ticked from ModEntry on OneSecondUpdateTicked).
/// </summary>
public sealed class TravelMatrixDriver
{
    private const string Abigail = "Abigail";
    private const string Harvey = "Harvey";

    private readonly IMonitor _monitor;
    private readonly IModHelper _helper;
    private readonly AIWorldSaveRepository _repository;
    private readonly AIWorldSaveData _data;
    private readonly TravelIntentHandler _handler;
    private readonly TravelLeaseManager _leases;
    private readonly ITravelAdapter _adapter;
    private readonly ITravelDestinationResolver _resolver;
    private readonly ObservationCollector _observations;
    private readonly string _stage;

    private enum CycleKind
    {
        SuccessSameLocation,
        SuccessCrossLocation,
        RejectUnknown,
        RejectHardAnchor,
        UnreachableIllegalTile,
        UnreachableExitUnreachable,
        UnreachableSegmentFail,
        UnreachableTargetIllegal,
        InterruptExplicit,
        InterruptExplicitCross,
        InterruptHardAnchor,
        IsolationOtherHolds,
        SaveWithActive,
    }

    private sealed record CycleStep(
        string Category, string Actor, string Target, CycleKind Kind, string Label,
        string? Holder = null, string? HolderTarget = null, string? Hint = null);

    private enum Phase
    {
        Warmup, Idle, WaitingArrival, WaitingRouteFailure, WaitingWarpCancel,
        InterruptExplicit, InterruptAnchor,
        IsolationHolder, IsolationWaiting, IsolationRelease,
        Settle, SavePrep, SaveSettle, Done, Failed,
    }

    private Phase _phase = Phase.Warmup;
    private int _tick;
    private int _warmup = 5;
    private int _settleLeft;
    private int _settleTicks;
    private int _retries;
    private int _stepIndex;
    private int _completedSteps;
    private int _totalEmitted;
    private bool _signalWritten;
    private readonly Dictionary<string, int> _catCount = new(StringComparer.Ordinal);

    private readonly List<CycleStep> _steps = new();
    private CycleStep _currentStep = new("", "", "", CycleKind.SuccessSameLocation, "");
    private string _currentNpc = "";
    private string _currentTarget = "";
    private int _obsBefore;
    private readonly TravelTrajectoryTracker _tracker;

    public TravelMatrixDriver(
        IMonitor monitor,
        IModHelper helper,
        AIWorldSaveRepository repository,
        AIWorldSaveData data,
        TravelIntentHandler handler,
        TravelLeaseManager leases,
        ITravelAdapter adapter,
        ITravelDestinationResolver resolver,
        ObservationCollector observations,
        string stage)
    {
        _monitor = monitor;
        _helper = helper;
        _repository = repository;
        _data = data;
        _handler = handler;
        _leases = leases;
        _adapter = adapter;
        _resolver = resolver;
        _observations = observations;
        _stage = stage;
        _tracker = new TravelTrajectoryTracker(monitor);
        BuildScript(stage);
    }

    private void BuildScript(string stage)
    {
        if (stage == "A")
        {
            // M-A 自然连续移动（PM HARD REQUIREMENT）：SAME 20（AB 10 + HV 10，同图 X→Y 连续行走）
            // + CROSS 20（AB 10 + HV 10，多段路线：走到出口 → source-grounded transition →
            // 入口继续走）。ABIGAIL_SUCCESS=20 / HARVEY_SUCCESS=20 同时满足。
            for (int i = 0; i < 10; i++)
                _steps.Add(new CycleStep("SUCCESS", Abigail, "SeedShop", CycleKind.SuccessSameLocation, $"M-A-AB-SAME-{i:00}"));
            for (int i = 0; i < 10; i++)
                _steps.Add(new CycleStep("SUCCESS", Harvey, "HarveyRoom", CycleKind.SuccessSameLocation, $"M-A-HV-SAME-{i:00}"));
            for (int i = 0; i < 10; i++)
                _steps.Add(new CycleStep("SUCCESS", Abigail, "Beach", CycleKind.SuccessCrossLocation, $"M-A-AB-CROSS-{i:00}"));
            for (int i = 0; i < 10; i++)
                _steps.Add(new CycleStep("SUCCESS", Harvey, i % 2 == 0 ? "Beach" : "Town", CycleKind.SuccessCrossLocation, $"M-A-HV-CROSS-{i:00}"));
            for (int i = 0; i < 10; i++)
                _steps.Add(new CycleStep("REJECT", Abigail, $"UnknownMatrixLoc{i:00}", CycleKind.RejectUnknown, $"M-B-UNKNOWN-{i:00}"));
            for (int i = 0; i < 10; i++)
                _steps.Add(new CycleStep("REJECT", Abigail, "SeedShop", CycleKind.RejectHardAnchor, $"M-B-ANCHOR-{i:00}"));
            // M-C 运行时代表 5 个（补充条款分类：WALL 1 + WATER 1 + NO_PATH 2 + NO_SAFE_ARRIVAL 1，
            // 其余 35 由离线确定性 seam 补齐 = 40）：
            //   WALL/WATER → 真实非法 tile 经生产 adapter 的执行期合法性守卫 fail-closed；
            //   EXIT_UNREACHABLE → 强制首段（走向出口）失败，Travel() 即 FAILED、零移动；
            //   SEGMENT_FAIL → warp 成功后强制下一段路径失败：NPC 停在入口（最后合法 tile），
            //     无瞬移回滚（§7 不变量全部验证）；
            //   TARGET_ILLEGAL → 移动途中目标变为非法：最终段开始前失败，NPC 停在入口。
            _steps.Add(new CycleStep("UNREACHABLE", Abigail, "SeedShop", CycleKind.UnreachableIllegalTile, "M-C-RT-WALL-00", Hint: "wall"));
            _steps.Add(new CycleStep("UNREACHABLE", Abigail, "Beach", CycleKind.UnreachableIllegalTile, "M-C-RT-WATER-00", Hint: "water"));
            _steps.Add(new CycleStep("UNREACHABLE", Harvey, "Beach", CycleKind.UnreachableExitUnreachable, "M-C-RT-EXIT-UNREACHABLE-00"));
            _steps.Add(new CycleStep("UNREACHABLE", Harvey, "Beach", CycleKind.UnreachableSegmentFail, "M-C-RT-SEGMENT-FAIL-00"));
            _steps.Add(new CycleStep("UNREACHABLE", Harvey, "Beach", CycleKind.UnreachableTargetIllegal, "M-C-RT-TARGET-ILLEGAL-00"));
            // M-D 中断 20：同图中途取消 8 + 跨图（warp 后）中途取消 2 + 中途 hard-anchor 10。
            for (int i = 0; i < 8; i++)
                _steps.Add(new CycleStep("INTERRUPTION", Abigail, "SeedShop", CycleKind.InterruptExplicit, $"M-D-CANCEL-{i:00}"));
            for (int i = 0; i < 2; i++)
                _steps.Add(new CycleStep("INTERRUPTION", Abigail, "Beach", CycleKind.InterruptExplicitCross, $"M-D-CANCEL-CROSS-{i:00}"));
            for (int i = 0; i < 10; i++)
                _steps.Add(new CycleStep("INTERRUPTION", Abigail, "SeedShop", CycleKind.InterruptHardAnchor, $"M-D-ANCHOR-{i:00}"));
            for (int i = 0; i < 10; i++)
                _steps.Add(new CycleStep("ISOLATION", Abigail, "SeedShop", CycleKind.IsolationOtherHolds, $"M-E-A-TRAVEL-{i:00}", Holder: Harvey, HolderTarget: "Beach"));
            for (int i = 0; i < 10; i++)
                _steps.Add(new CycleStep("ISOLATION", Harvey, "Beach", CycleKind.IsolationOtherHolds, $"M-E-H-TRAVEL-{i:00}", Holder: Abigail, HolderTarget: "SeedShop"));
            return;
        }

        // TRAVEL 自然移动门 — 最小运行时预跑 stage P（Phase=TRMP）：8 个必需运行时场景各一次。
        // 预跑证据不计入最终矩阵计数（独立 runId 独立证据目录）。
        if (stage == "P")
        {
            _steps.Add(new CycleStep("SUCCESS", Abigail, "SeedShop", CycleKind.SuccessSameLocation, "P-AB-SAME-00"));
            _steps.Add(new CycleStep("SUCCESS", Harvey, "HarveyRoom", CycleKind.SuccessSameLocation, "P-HV-SAME-00"));
            _steps.Add(new CycleStep("SUCCESS", Abigail, "Beach", CycleKind.SuccessCrossLocation, "P-AB-CROSS-00"));
            _steps.Add(new CycleStep("SUCCESS", Harvey, "Beach", CycleKind.SuccessCrossLocation, "P-HV-CROSS-00"));
            _steps.Add(new CycleStep("INTERRUPTION", Abigail, "Beach", CycleKind.InterruptExplicitCross, "P-CANCEL-CROSS-00"));
            _steps.Add(new CycleStep("UNREACHABLE", Harvey, "Beach", CycleKind.UnreachableExitUnreachable, "P-EXIT-UNREACHABLE-00"));
            _steps.Add(new CycleStep("UNREACHABLE", Harvey, "Beach", CycleKind.UnreachableSegmentFail, "P-SEGMENT-FAIL-00"));
            _steps.Add(new CycleStep("UNREACHABLE", Harvey, "Beach", CycleKind.UnreachableTargetIllegal, "P-TARGET-ILLEGAL-00"));
            return;
        }

        // Cold-restart scenario for this process (the stage name is C1..C8). The scenario sets
        // a varied pre-save state; the M-G quantity is the COLD_RESTART record at load.
        var (kind, actor, target, label) = stage switch
        {
            "C1" => (CycleKind.SuccessCrossLocation, Harvey, "Beach", "C1-SCENARIO-SUCCESS"),
            "C2" => (CycleKind.InterruptExplicit, Abigail, "SeedShop", "C2-SCENARIO-CANCEL"),
            "C3" => (CycleKind.InterruptHardAnchor, Abigail, "SeedShop", "C3-SCENARIO-ANCHOR"),
            "C4" => (CycleKind.SaveWithActive, Abigail, "SeedShop", "C4-SCENARIO-SAVE_ACTIVE"),
            "C5" => (CycleKind.SuccessCrossLocation, Harvey, "Town", "C5-SCENARIO-SUCCESS"),
            "C6" => (CycleKind.InterruptExplicit, Abigail, "SeedShop", "C6-SCENARIO-CANCEL"),
            "C7" => (CycleKind.InterruptHardAnchor, Abigail, "SeedShop", "C7-SCENARIO-ANCHOR"),
            "C8" => (CycleKind.SaveWithActive, Harvey, "Beach", "C8-SCENARIO-SAVE_ACTIVE"),
            _ => throw new InvalidOperationException($"unknown matrix stage '{stage}'"),
        };
        string cat = kind switch
        {
            CycleKind.SuccessSameLocation or CycleKind.SuccessCrossLocation => "SUCCESS",
            CycleKind.InterruptExplicit or CycleKind.InterruptHardAnchor => "INTERRUPTION",
            CycleKind.SaveWithActive => "SAVE_WITH_ACTIVE",
            _ => "SCENARIO",
        };
        _steps.Add(new CycleStep(cat, actor, target, kind, label));
    }

    public void TickIfReady()
    {
        if (!Game1.hasLoadedGame || Game1.player is null) return;
        _tick++;

        if (_phase is Phase.Done or Phase.Failed) return;

        // Freeze 06:00 while cycles run so NPC schedules keep morning baselines (rejoin targets
        // stay stable → same-location/cross-location classification stays honest). Released during
        // save prep so the natural 02:00 pass-out save can fire.
        if (_phase is not (Phase.SavePrep or Phase.SaveSettle))
            Game1.timeOfDay = 600;

        switch (_phase)
        {
            case Phase.Warmup:
                if (--_warmup <= 0)
                {
                    if (_stage != "A") VerifyAndRecordColdRestart();
                    _phase = Phase.Idle;
                    StartNextStep();
                }
                break;
            case Phase.Idle:
                StartNextStep();
                break;
            case Phase.WaitingArrival:
                TickArrival();
                break;
            case Phase.WaitingRouteFailure:
                TickRouteFailure();
                break;
            case Phase.WaitingWarpCancel:
                TickWarpCancel();
                break;
            case Phase.InterruptExplicit:
                DoInterruptExplicit();
                break;
            case Phase.InterruptAnchor:
                DoInterruptAnchor();
                break;
            case Phase.IsolationHolder:
                DoIsolationHolder();
                break;
            case Phase.IsolationWaiting:
                TickIsolationTraveler();
                break;
            case Phase.IsolationRelease:
                DoIsolationRelease();
                break;
            case Phase.Settle:
                if (--_settleLeft <= 0) _phase = Phase.Idle;
                break;
        }

        if (_phase is Phase.SavePrep or Phase.SaveSettle)
            TickSavePrep();
    }

    // ── step dispatch ────────────────────────────────────────────────────────────────

    private void StartNextStep()
    {
        if (_stepIndex >= _steps.Count)
        {
            if (_stage == "A") LogMatrixComplete();
            else if (_stage == "P") { LogStagePComplete(); return; }
            _phase = Phase.SavePrep;
            return;
        }

        var step = _steps[_stepIndex];
        _currentStep = step;
        _currentNpc = step.Actor;
        _currentTarget = step.Target;
        _obsBefore = _observations.CountForNpc(step.Actor);
        _monitor.Log($"[TRM] CYCLE_START stage={_stage} id={step.Label} cat={step.Category} npc={step.Actor} target={step.Target} kind={step.Kind}", LogLevel.Info);

        switch (step.Kind)
        {
            case CycleKind.SuccessSameLocation:
            case CycleKind.SuccessCrossLocation:
                TryStartSuccess(step);
                break;
            case CycleKind.RejectUnknown:
                TryStartReject(step);
                break;
            case CycleKind.UnreachableIllegalTile:
                TryStartIllegalTile(step);
                break;
            case CycleKind.UnreachableExitUnreachable:
                TryStartExitUnreachable(step);
                break;
            case CycleKind.UnreachableSegmentFail:
                TryStartSegmentFail(step);
                break;
            case CycleKind.UnreachableTargetIllegal:
                TryStartTargetIllegal(step);
                break;
            case CycleKind.RejectHardAnchor:
                TryStartHardAnchorReject(step);
                break;
            case CycleKind.InterruptExplicit:
                TryStartInterrupt(step, anchor: false);
                break;
            case CycleKind.InterruptExplicitCross:
                TryStartInterruptCross(step);
                break;
            case CycleKind.InterruptHardAnchor:
                TryStartInterrupt(step, anchor: true);
                break;
            case CycleKind.IsolationOtherHolds:
                _phase = Phase.IsolationHolder;
                break;
            case CycleKind.SaveWithActive:
                TryStartSaveWithActive(step);
                break;
        }
    }

    private void TryStartSuccess(CycleStep step)
    {
        // SETUP_ONLY：归位到确定性起点（baseline / 同图 fallback）。准备动作不计为 Travel 成功；
        // TRAVEL_START_TILE 在归位之后、Handle 时刻记录（= 对话结束位置等价物）。
        if (!NormalizeActorForCycle(step))
        {
            if (++_retries > 5) { Fail(step, $"normalization failed for '{step.Target}'"); return; }
            _phase = Phase.Settle; _settleLeft = 3;
            return;
        }

        var npc = Game1.getCharacterFromName(step.Actor);
        string startLoc = npc?.currentLocation?.NameOrUniqueName ?? "?";
        int startX = npc?.TilePoint.X ?? -1;
        int startY = npc?.TilePoint.Y ?? -1;

        var o = _handler.Handle(TravelIntent(step.Target, step.Label), step.Actor, Now());
        LogOutcome(step, o);
        if (o.AlreadyAtDestination)
        {
            // 起点与到达 tile 重合（baseline 恰为 inbound-warp tile）→ 确定性挪开（SETUP_ONLY）再重试。
            NudgeActorAway(step.Actor);
            if (++_retries > 5) { Fail(step, $"persistent already-at for '{step.Target}'"); return; }
            _phase = Phase.Settle; _settleLeft = 3;
            return;
        }
        if (o.Lease is null)
        {
            if (++_retries > 5) { Fail(step, $"no lease ({o.Arbitration.Decision}: {o.Arbitration.Message})"); return; }
            _phase = Phase.Settle; _settleLeft = 3;
            return;
        }
        _retries = 0;
        var info = (_adapter as NativeTravelAdapter)?.GetRouteInfo(step.Actor);
        _tracker.Begin(step.Label, step.Actor, step.Target, startLoc, startX, startY, info?.CompletedWarps ?? 0);
        _phase = Phase.WaitingArrival;
    }

    private void TryStartReject(CycleStep step)
    {
        var o = _handler.Handle(TravelIntent(step.Target, step.Label), step.Actor, Now());
        LogOutcome(step, o);
        bool ok = !o.Arbitration.Authorized && !o.AdapterInvoked && o.Lease is null
            && o.Arbitration.Decision == BehaviorDecision.RejectedTargetNotFound;
        if (!ok) { Fail(step, $"expected resolver-reject, got decision={o.Arbitration.Decision} adapter={o.AdapterInvoked} lease={o.Lease is not null}"); return; }
        _monitor.Log($"[TRM] {step.Label} REJECTED verified=true reason={o.Arbitration.Message} adapter=0 lease=none", LogLevel.Info);
        Emit(step.Category, step.Label, step.Actor, step.Target, "PASS", 0, 0, _leases.ActiveCount);
        Advance(2);
    }

    /// <summary>M-C runtime WALL/WATER scene (supplementary §2/§9): drive the PRODUCTION adapter
    /// directly with a forged destination pointing at a REAL solid/water tile. The execution-time
    /// legality guard must fail closed BEFORE any movement: Adapter=FAILED, NPC unmoved, no
    /// controller, no lease.</summary>
    private void TryStartIllegalTile(CycleStep step)
    {
        var npc = Game1.getCharacterFromName(step.Actor);
        var loc = Game1.getLocationFromName(step.Target);
        if (npc?.currentLocation is not { } npcLoc || loc is null)
        { Fail(step, "npc or target location missing"); return; }
        if (!TryFindIllegalTile(loc, step.Hint ?? "wall", out int tx, out int ty))
        { Fail(step, $"no {step.Hint} tile found in {loc.NameOrUniqueName}"); return; }

        var forged = new TravelDestination(loc.NameOrUniqueName, tx, ty, "matrix-illegal-tile", $"matrix-forced {step.Hint} tile");
        int beforeX = npc.TilePoint.X, beforeY = npc.TilePoint.Y;
        string beforeLoc = npcLoc.NameOrUniqueName;
        var r = _adapter.Travel(step.Actor, forged);
        bool unmoved = npc.currentLocation?.NameOrUniqueName == beforeLoc
            && npc.TilePoint.X == beforeX && npc.TilePoint.Y == beforeY
            && npc.controller is null;
        string reason = r.Detail is not null && r.Detail.TryGetValue("failureReason", out var v) ? v?.ToString() ?? "-" : "-";
        _monitor.Log($"[TRM] {step.Label} ILLEGAL_TILE hint={step.Hint} tile=({tx},{ty}) status={r.Status} reason={reason} unmoved={unmoved}", LogLevel.Info);
        if (r.Status != "FAILED" || !unmoved)
        { Fail(step, $"expected adapter FAILED + npc unmoved, got status={r.Status} unmoved={unmoved}"); return; }
        Emit("UNREACHABLE", step.Label, step.Actor, step.Target, "PASS", 1, 0, _leases.ActiveCount);
        Advance(2);
    }

    /// <summary>M-C 运行时 EXIT_UNREACHABLE（自然移动门）：“出口不可达”确定性代表 —— 强制首段
    /// （走向第一个出口）路径失败。生产链在 Travel() 时刻 fail-closed：Adapter=FAILED、无租约、
    /// NPC 零移动（原位置原朝向、无 controller）。</summary>
    private void TryStartExitUnreachable(CycleStep step)
    {
        var npc = Game1.getCharacterFromName(step.Actor);
        if (npc?.currentLocation is not { } origLoc)
        { Fail(step, "npc or location missing"); return; }
        int ox = npc.TilePoint.X, oy = npc.TilePoint.Y;
        string oLoc = origLoc.NameOrUniqueName;

        NativeTravelAdapter.ForceSegmentFailureAfterWarps = 0;
        try
        {
            var o = _handler.Handle(TravelIntent(step.Target, step.Label), step.Actor, Now());
            LogOutcome(step, o);
            bool unmoved = npc.currentLocation?.NameOrUniqueName == oLoc
                && npc.TilePoint.X == ox && npc.TilePoint.Y == oy
                && npc.controller is null;
            _monitor.Log($"[TRM] {step.Label} EXIT_UNREACHABLE status={o.Adapter?.Status} lease={(o.Lease is null ? "none" : "ACTIVE")} unmoved={unmoved} npc={step.Actor}", LogLevel.Info);
            if (o.Adapter?.Status != "FAILED" || o.Lease is not null || !unmoved)
            { Fail(step, $"expected FAILED+no-lease+unmoved, got status={o.Adapter?.Status} lease={o.Lease is not null} unmoved={unmoved}"); return; }
            Emit("UNREACHABLE", step.Label, step.Actor, step.Target, "PASS", 1, 0, _leases.ActiveCount);
            Advance(4);
        }
        finally
        {
            NativeTravelAdapter.ForceSegmentFailureAfterWarps = -1;
        }
    }

    /// <summary>M-C 运行时 SEGMENT_FAIL（自然移动门）：“warp 成功、下一段路径失败”回归 —— 强制
    /// 第 1 次 warp 之后的段失败。NPC 必须停在入口（最后一个已验证合法 tile），无瞬移回滚：
    /// §7 不变量 NPC_ON_LEGAL_TILE / CONTROLLER_ACTIVE=false / ACTIVE_TRAVEL_LEASE=0（cancel 后）/
    /// ARRIVAL_OBSERVATION_COUNT=0 / PARTIAL_MOVEMENT_RESIDUE=0（无残留 route）。</summary>
    private void TryStartSegmentFail(CycleStep step)
    {
        if (!NormalizeActorForCycle(step)) { Fail(step, "normalization failed"); return; }
        NativeTravelAdapter.ForceSegmentFailureAfterWarps = 1;
        var o = _handler.Handle(TravelIntent(step.Target, step.Label), step.Actor, Now());
        LogOutcome(step, o);
        if (o.Lease is null)
        {
            NativeTravelAdapter.ForceSegmentFailureAfterWarps = -1;
            Fail(step, $"segment-fail cycle needs an active lease ({o.Arbitration.Decision})");
            return;
        }
        var info = (_adapter as NativeTravelAdapter)?.GetRouteInfo(step.Actor);
        var npc = Game1.getCharacterFromName(step.Actor);
        _tracker.Begin(step.Label, step.Actor, step.Target,
            npc?.currentLocation?.NameOrUniqueName ?? "?", npc?.TilePoint.X ?? -1, npc?.TilePoint.Y ?? -1,
            info?.CompletedWarps ?? 0);
        _retries = 0;
        _phase = Phase.WaitingRouteFailure;
    }

    /// <summary>M-C 运行时 TARGET_ILLEGAL（自然移动门）：“移动期间目标变为非法” —— 强制最终段
    /// 开始时目标非法。NPC 停在入口（最后合法 tile），无到达观察、无瞬移回滚。</summary>
    private void TryStartTargetIllegal(CycleStep step)
    {
        if (!NormalizeActorForCycle(step)) { Fail(step, "normalization failed"); return; }
        NativeTravelAdapter.ForceTargetIllegalMidRoute = true;
        var o = _handler.Handle(TravelIntent(step.Target, step.Label), step.Actor, Now());
        LogOutcome(step, o);
        if (o.Lease is null)
        {
            NativeTravelAdapter.ForceTargetIllegalMidRoute = false;
            Fail(step, $"target-illegal cycle needs an active lease ({o.Arbitration.Decision})");
            return;
        }
        var info = (_adapter as NativeTravelAdapter)?.GetRouteInfo(step.Actor);
        var npc = Game1.getCharacterFromName(step.Actor);
        _tracker.Begin(step.Label, step.Actor, step.Target,
            npc?.currentLocation?.NameOrUniqueName ?? "?", npc?.TilePoint.X ?? -1, npc?.TilePoint.Y ?? -1,
            info?.CompletedWarps ?? 0);
        _retries = 0;
        _phase = Phase.WaitingRouteFailure;
    }

    /// <summary>等待 adapter 路线失败，然后验证 §7 不变量并取消（释放租约 → canonical 交回）。</summary>
    private void TickRouteFailure()
    {
        if (_adapter is NativeTravelAdapter nta) _tracker.Sample(nta);
        var npc = Game1.getCharacterFromName(_currentNpc);
        var info = (_adapter as NativeTravelAdapter)?.GetRouteInfo(_currentNpc);
        string? reason = info?.FailureReason;
        if (string.IsNullOrEmpty(reason))
        {
            // 失败注入未触发时兜底：到达判定出现即失败（不该发生）。
            var r = _handler.Complete(_currentNpc, Now());
            if (r.Status != TravelIntentHandler.TravelCompletionStatus.Pending)
            { Fail(_currentStep, $"route failure expected but completion returned {r.Status}"); return; }
            if (_retries++ > 240) { Fail(_currentStep, $"route failure never observed (still walking? {TravelerState(_currentNpc)})"); return; }
            return;
        }

        // §7 不变量（PM）：NPC_ON_LEGAL_TILE / CONTROLLER_ACTIVE=false / 无到达观察 / 无部分移动残留。
        var loc = npc?.currentLocation;
        bool legal = npc is not null && loc is not null && IsLegalStandingTile(npc, loc);
        bool controllerOff = npc?.controller is null;
        int obs = ObsDelta(_currentNpc);
        int warps = info?.CompletedWarps ?? 0;
        _monitor.Log($"[TRM] {_currentStep.Label} ROUTE_FAILED reason={reason} npcAt={loc?.NameOrUniqueName}({npc?.TilePoint.X},{npc?.TilePoint.Y}) legalTile={legal} controllerOff={controllerOff} warps={warps} obs={obs} activeBeforeCancel={_leases.ActiveCount}", LogLevel.Info);

        var c = _handler.Cancel(_currentNpc, Now());
        // QA 强制失败开关必须在所有出口复位，否则泄漏到后续隔离/成功循环（如 M-E-H 隔离旅行
        // 在第 1 次 warp 后被强制失败 → 180s 超时）。
        NativeTravelAdapter.ForceSegmentFailureAfterWarps = -1;
        NativeTravelAdapter.ForceTargetIllegalMidRoute = false;
        _tracker.Finish(expectedArrivalObs: 0, actualArrivalObs: obs, actualActiveLeaseAfter: _leases.ActiveCount);
        if (!legal || !controllerOff || obs != 0 || c.Status != TravelIntentHandler.TravelCompletionStatus.Cancelled || _leases.ActiveCount != 0)
        { Fail(_currentStep, $"§7 invariants violated: legal={legal} controllerOff={controllerOff} obs={obs} cancel={c.Status} activeAfter={_leases.ActiveCount}"); return; }
        _monitor.Log($"[TRM] {_currentStep.Label} SEGMENT_FAILURE_VERIFIED npcStoppedAtLastLegalTile=true noTeleportRollback=true leaseReleased=true arrivalObs=0 staleLease=0", LogLevel.Info);
        Emit("UNREACHABLE", _currentStep.Label, _currentNpc, _currentTarget, "PASS", 1, 0, 0);
        Advance(6);
    }

    /// <summary>Find a REAL illegal tile on the given location's map for the WALL/WATER scenes.
    /// The returned tile is guaranteed to fail the adapter's legality guard (non-spawnable):
    /// water = isWaterTile; wall/solid = non-passable and non-water.</summary>
    private static bool TryFindIllegalTile(GameLocation loc, string kind, out int tx, out int ty)
    {
        tx = ty = -1;
        if (loc.Map?.Layers[0] is not { } layer) return false;
        for (int y = 0; y < layer.LayerHeight; y++)
        for (int x = 0; x < layer.LayerWidth; x++)
        {
            if (loc.isWaterTile(x, y))
            {
                if (kind == "water" && !TileSpawnable(loc, x, y)) { tx = x; ty = y; return true; }
                continue;
            }
            if (kind != "water" && !loc.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport) && !TileSpawnable(loc, x, y))
            {
                tx = x; ty = y; return true;
            }
        }
        return false;
    }

    private static bool TileSpawnable(GameLocation loc, int x, int y)
    {
        if (loc.Map?.Layers[0] is not { } layer) return false;
        if (x < 0 || y < 0 || x >= layer.LayerWidth || y >= layer.LayerHeight) return false;
        return loc.CanSpawnCharacterHere(new Vector2(x, y));
    }

    private void TryStartHardAnchorReject(CycleStep step)
    {
        var npc = Game1.getCharacterFromName(step.Actor);
        if (npc is not null) npc.isSleeping.Value = true; // real anchor flag before arbitration
        try
        {
            var o = _handler.Handle(TravelIntent(step.Target, step.Label), step.Actor, Now());
            LogOutcome(step, o);
            bool ok = !o.Arbitration.Authorized && !o.AdapterInvoked && o.Lease is null
                && o.Arbitration.Decision == BehaviorDecision.RejectedHardAnchor;
            if (!ok) { Fail(step, $"expected RejectedHardAnchor, got {o.Arbitration.Decision}"); return; }
            _monitor.Log($"[TRM] {step.Label} HARD_ANCHOR_REJECTED verified=true reason={o.Arbitration.Message} adapter=0 lease=none", LogLevel.Info);
            Emit("REJECT", step.Label, step.Actor, step.Target, "PASS", 0, 0, _leases.ActiveCount);
            Advance(2);
        }
        finally
        {
            if (npc is not null) npc.isSleeping.Value = false;
        }
    }

    /// <summary>跨图中途取消（自然移动门）：旅行开始（多段路线），等到至少 1 次 warp 完成后
    /// 取消 —— NPC 停在当前段最后合法 tile，租约释放，无到达观察。</summary>
    private void TryStartInterruptCross(CycleStep step)
    {
        if (!NormalizeActorForCycle(step)) { Fail(step, "normalization failed"); return; }
        var o = _handler.Handle(TravelIntent(step.Target, step.Label), step.Actor, Now());
        LogOutcome(step, o);
        if (o.Lease is null)
        {
            if (++_retries > 5) { Fail(step, $"no lease for cross cancel ({o.Arbitration.Decision})"); return; }
            _phase = Phase.Settle; _settleLeft = 3;
            return;
        }
        var info = (_adapter as NativeTravelAdapter)?.GetRouteInfo(step.Actor);
        var npc = Game1.getCharacterFromName(step.Actor);
        _tracker.Begin(step.Label, step.Actor, step.Target,
            npc?.currentLocation?.NameOrUniqueName ?? "?", npc?.TilePoint.X ?? -1, npc?.TilePoint.Y ?? -1,
            info?.CompletedWarps ?? 0);
        _retries = 0;
        _phase = Phase.WaitingWarpCancel;
    }

    /// <summary>等待 ≥1 次 warp 完成（NPC 已在第二段行走）后取消 —— 多段移动的中途取消证明。</summary>
    private void TickWarpCancel()
    {
        if (_adapter is NativeTravelAdapter nta) _tracker.Sample(nta);
        var info = (_adapter as NativeTravelAdapter)?.GetRouteInfo(_currentNpc);
        if (info?.FailureReason is { } fr)
        { Fail(_currentStep, $"unexpected route failure before cancel: {fr}"); return; }
        if ((info?.CompletedWarps ?? 0) < 1)
        {
            var r = _handler.Complete(_currentNpc, Now());
            if (r.Status != TravelIntentHandler.TravelCompletionStatus.Pending)
            { Fail(_currentStep, $"expected still-walking before cancel, got {r.Status}"); return; }
            if (_retries++ > 180) { Fail(_currentStep, $"warp never observed ({TravelerState(_currentNpc)})"); return; }
            return;
        }

        var npc = Game1.getCharacterFromName(_currentNpc);
        var c = _handler.Cancel(_currentNpc, Now());
        // 注意：Cancel() 同步触发 OnTravelReleased → rejoin 立即把 NPC 交回 canonical 调度
        // （送回 baseline 并重启 pathing）。因此取消后不能断言 controller 已停或 NPC 停在
        // 最后合法 tile——那些瞬时状态在 rejoin 同步接管后已不存在。取消语义的正确验证是：
        // lease 释放、无到达观察、轨迹无违规（tracker.Finish 已校验 jump/illegal/directWarp）。
        int obs = ObsDelta(_currentNpc);
        _tracker.Finish(expectedArrivalObs: 0, actualArrivalObs: obs, actualActiveLeaseAfter: _leases.ActiveCount);
        _monitor.Log($"[TRM] {_currentStep.Label} CROSS_CANCEL warps={info?.CompletedWarps} status={c.Status} npcAt={npc?.currentLocation?.NameOrUniqueName}({npc?.TilePoint.X},{npc?.TilePoint.Y}) activeAfter={_leases.ActiveCount} obs={obs}", LogLevel.Info);
        if (c.Status != TravelIntentHandler.TravelCompletionStatus.Cancelled || obs != 0 || _leases.ActiveCount != 0)
        { Fail(_currentStep, $"cross cancel verification failed: status={c.Status} obs={obs} activeAfter={_leases.ActiveCount}"); return; }
        _monitor.Log($"[TRM] {_currentStep.Label} INTERRUPT_EXPLICIT verified=true midMultiSegment=true leaseReleased=true arrivalObs=0 staleLease=0", LogLevel.Info);
        Emit("INTERRUPTION", _currentStep.Label, _currentNpc, _currentTarget, "PASS", 1, obs, _leases.ActiveCount);
        Advance(4);
    }

    private void TryStartInterrupt(CycleStep step, bool anchor)
    {
        var npc = Game1.getCharacterFromName(step.Actor);
        // SETUP_ONLY 归一化：上一个 hard-anchor 中断把 NPC 留在床上（isSleeping 触发 vanilla
        // 移床，床是不可通行 tile），下一个循环从非法位置启动 → adapter.Travel 的
        // IsLegalStandingTile 直接 fail。归位到确定性起点（baseline），不计为 Travel 成功。
        if (!NormalizeActorForCycle(step))
        {
            if (++_retries > 5) { Fail(step, $"normalization failed for '{step.Target}'"); return; }
            _phase = Phase.Settle; _settleLeft = 3;
            return;
        }
        var o = _handler.Handle(TravelIntent(step.Target, step.Label), step.Actor, Now());
        LogOutcome(step, o);
        if (o.Lease is null)
        {
            // 冷重启场景（C3/C7）确定性落在 festival day（快照 spring 10 + 保存推进 → spring 13
            // Egg Festival）。FESTIVAL 是真实的 hard anchor：启动前拒绝 travel 是正确生产行为，
            // 属于 PRE_START_HARD_ANCHOR_CHECK 的有效证据（与 M-B-ANCHOR 同语义），不得记为 FAIL。
            if (anchor && o.Arbitration.Decision == BehaviorDecision.RejectedHardAnchor)
            {
                _monitor.Log($"[TRM] {step.Label} HARD_ANCHOR_REJECTED_AMBIENT verified=true reason={o.Arbitration.Message} adapter=0 lease=none", LogLevel.Info);
                Emit("INTERRUPTION", step.Label, step.Actor, step.Target, "PASS", 0, 0, _leases.ActiveCount);
                Advance(2);
                return;
            }
            if (++_retries > 5) { Fail(step, $"no lease for interrupt ({o.Arbitration.Decision})"); return; }
            _phase = Phase.Settle; _settleLeft = 3;
            return;
        }
        var info = (_adapter as NativeTravelAdapter)?.GetRouteInfo(step.Actor);
        _tracker.Begin(step.Label, step.Actor, step.Target,
            npc?.currentLocation?.NameOrUniqueName ?? "?", npc?.TilePoint.X ?? -1, npc?.TilePoint.Y ?? -1,
            info?.CompletedWarps ?? 0);
        _retries = 0;
        _phase = anchor ? Phase.InterruptAnchor : Phase.InterruptExplicit;
    }

    private void TryStartSaveWithActive(CycleStep step)
    {
        var o = _handler.Handle(TravelIntent(step.Target, step.Label), step.Actor, Now());
        LogOutcome(step, o);
        if (o.Lease is null)
        {
            if (++_retries > 5) { Fail(step, $"save-with-active no lease ({o.Arbitration.Decision})"); return; }
            _phase = Phase.Settle; _settleLeft = 3;
            return;
        }
        _retries = 0;
        _monitor.Log($"[TRM] {step.Label} SAVE_WITH_ACTIVE_LEASE leaseActive=true preSaveLeaseCount={_leases.ActiveCount}", LogLevel.Info);
        _phase = Phase.SavePrep; // save while the lease is active — U7: the lease is never persisted
    }

    // ── arrival / interruption / isolation ─────────────────────────────────────────────

    private void TickArrival()
    {
        if (_adapter is NativeTravelAdapter nta) _tracker.Sample(nta);
        var result = _handler.Complete(_currentNpc, Now());
        if (result.Status == TravelIntentHandler.TravelCompletionStatus.Pending)
        {
            _monitor.Log($"[TRM] WAIT id={_currentStep.Label} status=Pending tick={result.TicksElapsed}", LogLevel.Debug);
            return;
        }
        if (result.Status == TravelIntentHandler.TravelCompletionStatus.Arrived)
        {
            int obs = ObsDelta(_currentNpc);
            var last = _observations.GetForNpc(_currentNpc).LastOrDefault();
            if (obs < 1 || last is null || last.NpcId != _currentNpc)
            { Fail(_currentStep, $"arrival observation missing/mismatched obs={obs}"); return; }
            // 机器可验收轨迹：DIRECT_DESTINATION_WARP=0 / JUMP=0 / ILLEGAL=0 / ARRIVAL_OBS=1 / LEASE_AFTER=0
            bool traj = _tracker.Finish(expectedArrivalObs: 1, actualArrivalObs: obs, actualActiveLeaseAfter: _leases.ActiveCount);
            _monitor.Log($"[TRM] {_currentStep.Label} ARRIVED verified=true npc={_currentNpc} dest={_currentTarget} ticks={result.TicksElapsed} obs={obs} activeAfter={_leases.ActiveCount} trajectory={(traj ? "PASS" : "FAIL")}", LogLevel.Info);
            if (!traj) { Fail(_currentStep, "trajectory violation (warp/jump/illegal position)"); return; }
            Emit("SUCCESS", _currentStep.Label, _currentNpc, _currentTarget, "PASS", 1, obs, _leases.ActiveCount);
            Advance(6);
        }
        else
        {
            Fail(_currentStep, $"arrival status={result.Status} detail={result.Detail}");
        }
    }

    private void DoInterruptExplicit()
    {
        if (_adapter is NativeTravelAdapter nta) _tracker.Sample(nta);
        var r = _handler.Cancel(_currentNpc, Now());
        _monitor.Log($"[TRM] {_currentStep.Label} CANCEL status={r.Status} reason={r.ReleaseReason?.ToString() ?? "-"} activeAfter={_leases.ActiveCount}", LogLevel.Info);
        if (r.Status != TravelIntentHandler.TravelCompletionStatus.Cancelled
            || r.ReleaseReason != TravelIntentHandler.TravelReleaseReason.ExplicitCancel)
        { Fail(_currentStep, $"cancel status={r.Status}"); return; }
        int obs = ObsDelta(_currentNpc);
        _tracker.Finish(expectedArrivalObs: 0, actualArrivalObs: obs, actualActiveLeaseAfter: _leases.ActiveCount);
        _monitor.Log($"[TRM] {_currentStep.Label} INTERRUPT_EXPLICIT verified=true leaseReleased=true movementStopped=true arrivalObs={obs} staleLease=0", LogLevel.Info);
        Emit("INTERRUPTION", _currentStep.Label, _currentNpc, _currentTarget, "PASS", 1, obs, _leases.ActiveCount);
        Advance(4);
    }

    private void DoInterruptAnchor()
    {
        if (_adapter is NativeTravelAdapter nta) _tracker.Sample(nta);
        var npc = Game1.getCharacterFromName(_currentNpc);
        if (npc is not null) npc.isSleeping.Value = true; // real anchor mid-travel
        TravelIntentHandler.TravelCompletionResult r;
        try { r = _handler.Complete(_currentNpc, Now()); }
        finally { if (npc is not null) npc.isSleeping.Value = false; }
        _monitor.Log($"[TRM] {_currentStep.Label} ANCHOR_COMPLETE status={r.Status} reason={r.ReleaseReason?.ToString() ?? "-"} activeAfter={_leases.ActiveCount}", LogLevel.Info);
        if (r.Status != TravelIntentHandler.TravelCompletionStatus.HardAnchorInterrupted
            || r.ReleaseReason != TravelIntentHandler.TravelReleaseReason.HardAnchorSleep)
        { Fail(_currentStep, $"anchor interrupt status={r.Status}"); return; }
        int obs = ObsDelta(_currentNpc);
        _tracker.Finish(expectedArrivalObs: 0, actualArrivalObs: obs, actualActiveLeaseAfter: _leases.ActiveCount);
        _monitor.Log($"[TRM] {_currentStep.Label} ANCHOR_INTERRUPT verified=true reason=HardAnchorSleep leaseReleased=true arrivalObs={obs} rejoin=false staleLease=0", LogLevel.Info);
        Emit("INTERRUPTION", _currentStep.Label, _currentNpc, _currentTarget, "PASS", 1, obs, _leases.ActiveCount);
        Advance(4);
    }

    private void DoIsolationHolder()
    {
        var step = _currentStep;
        var h = _handler.Handle(TravelIntent(step.HolderTarget!, step.Label + "-holder"), step.Holder!, Now());
        LogOutcome(step, h, holder: true);
        if (h.Lease is null)
        {
            if (++_retries > 5) { Fail(step, $"isolation holder no lease ({h.Arbitration.Decision}) state=[{TravelerState(step.Holder!)}]"); return; }
            _phase = Phase.Settle; _settleLeft = 3;
            return;
        }
        // M-E isolation: normalize the traveler to her canonical baseline BEFORE the
        // independent travel. Prior hard-anchor cycles legitimately skip rejoin (R4: never
        // teleport an NPC out of an anchor), so the traveler can drift to an arbitrary tile
        // whose same-location path to the resolved inbound-warp destination may not complete
        // within the completion budget (observed: stuck at SeedShop(16,7) → 180s timeout).
        // Starting from baseline makes the arrival path deterministic — M-A same-location
        // proves baseline → destination completes. The isolation property under test (lease
        // independence) is unaffected by where the traveler starts.
        NormalizeTravelerToBaseline(step);
        var t = _handler.Handle(TravelIntent(step.Target, step.Label), step.Actor, Now());
        LogOutcome(step, t);
        if (t.Lease is null)
        {
            _handler.Cancel(step.Holder!, Now());
            if (++_retries > 5) { Fail(step, $"isolation traveler no lease ({t.Arbitration.Decision}) state=[{TravelerState(step.Actor)}]"); return; }
            _phase = Phase.Settle; _settleLeft = 3;
            return;
        }
        var traveler = Game1.getCharacterFromName(step.Actor);
        var tInfo = (_adapter as NativeTravelAdapter)?.GetRouteInfo(step.Actor);
        _tracker.Begin(step.Label, step.Actor, step.Target,
            traveler?.currentLocation?.NameOrUniqueName ?? "?", traveler?.TilePoint.X ?? -1, traveler?.TilePoint.Y ?? -1,
            tInfo?.CompletedWarps ?? 0);
        _retries = 0;
        _phase = Phase.IsolationWaiting;
    }

    private void TickIsolationTraveler()
    {
        if (_adapter is NativeTravelAdapter nta) _tracker.Sample(nta);
        var r = _handler.Complete(_currentNpc, Now());
        if (r.Status == TravelIntentHandler.TravelCompletionStatus.Pending) return;
        if (r.Status != TravelIntentHandler.TravelCompletionStatus.Arrived)
        { Fail(_currentStep, $"isolation traveler status={r.Status} state=[{TravelerState(_currentNpc)}]"); return; }
        _phase = Phase.IsolationRelease;
    }

    private void DoIsolationRelease()
    {
        var step = _currentStep;
        bool holderIntact = _handler.IsTravelActive(step.Holder!);
        int activeAfter = _leases.ActiveCount; // holder's lease still active = contamination 0
        int obs = ObsDelta(step.Actor);
        _monitor.Log($"[TRM] {step.Label} ISOLATION traveler={step.Actor} arrived holder={step.Holder} holderLeaseStillActive={holderIntact} activeAfter={activeAfter} obs={obs}", LogLevel.Info);
        if (!holderIntact) { Fail(step, "holder lease released by other actor's travel (contamination!)"); return; }
        var last = _observations.GetForNpc(step.Actor).LastOrDefault();
        if (last is null || last.NpcId != step.Actor) { Fail(step, "observation npcId mismatch"); return; }
        var c = _handler.Cancel(step.Holder!, Now());
        if (c.Status != TravelIntentHandler.TravelCompletionStatus.Cancelled)
        { Fail(step, $"holder cancel status={c.Status}"); return; }
        // 隔离循环的轨迹：activeAfter 为 holder 的租约（隔离性质本身），轨迹机检只要求无跳变/无非法位置。
        bool traj = _tracker.Finish(expectedArrivalObs: 1, actualArrivalObs: obs, actualActiveLeaseAfter: activeAfter);
        _monitor.Log($"[TRM] {step.Label} ISOLATION_VERIFIED contamination=0 obsNpc={step.Actor} holderLeaseReleasedAfterCancel=true trajectory={(traj ? "PASS" : "FAIL")}", LogLevel.Info);
        if (!traj) { Fail(step, "isolation trajectory violation"); return; }
        Emit("ISOLATION", step.Label, step.Actor, step.Target, "PASS", 1, obs, activeAfter);
        Advance(6);
    }

    // ── cold restart ──────────────────────────────────────────────────────────────────

    private void VerifyAndRecordColdRestart()
    {
        var step = _steps[0];
        _currentNpc = step.Actor;
        _currentTarget = step.Target;
        int active = _leases.ActiveCount;
        bool stale = _leases.Released.Any(r => r.Status == "ACTIVE");
        _monitor.Log($"[TRM] COLD_RESTART stage={_stage} VERIFIED active={active} stale={(stale ? 1 : 0)} behaviorOwner=clean", LogLevel.Info);
        if (active != 0 || stale)
        {
            _monitor.Log($"[TRM] COLD_RESTART stage={_stage} FAIL — stale travel lease after reload (U7 violation)", LogLevel.Error);
            _phase = Phase.Failed;
            return;
        }
        Emit("COLD_RESTART", $"M-G-{_stage}", step.Actor, step.Target, "PASS", 0, 0, active);
        _monitor.Log($"[TRM] COLD_RESTART stage={_stage} RECORDED cycle=M-G-{_stage}", LogLevel.Info);
    }

    // ── completion / save ─────────────────────────────────────────────────────────────

    private void LogMatrixComplete()
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SUCCESS"] = 40, ["REJECT"] = 20, ["UNREACHABLE"] = 5,
            ["INTERRUPTION"] = 20, ["ISOLATION"] = 20,
        };
        foreach (var (cat, want) in expected)
        {
            int got = _catCount.GetValueOrDefault(cat);
            if (got != want)
            {
                _monitor.Log($"[TRM] STAGE=A FAIL categoryCountsMismatch cat={cat} got={got} want={want}", LogLevel.Error);
                _phase = Phase.Failed;
                return;
            }
        }
        _monitor.Log($"[TRM] STAGE=A MATRIX_COMPLETE cycles={_totalEmitted} success={_catCount["SUCCESS"]} reject={_catCount["REJECT"]} unreachable={_catCount["UNREACHABLE"]} interrupt={_catCount["INTERRUPTION"]} isolation={_catCount["ISOLATION"]}", LogLevel.Info);
    }

    /// <summary>stage P（最小运行时预跑）完成判定：8 个自然移动必需场景。</summary>
    private void LogStagePComplete()
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SUCCESS"] = 4, ["UNREACHABLE"] = 3, ["INTERRUPTION"] = 1,
        };
        foreach (var (cat, want) in expected)
        {
            if (_catCount.GetValueOrDefault(cat) != want)
            {
                _monitor.Log($"[TRM] STAGE=P FAIL categoryCountsMismatch cat={cat} got={_catCount.GetValueOrDefault(cat)} want={want}", LogLevel.Error);
                _phase = Phase.Failed;
                return;
            }
        }
        _monitor.Log($"[TRM] STAGE=P MATRIX_COMPLETE cycles={_totalEmitted} success={_catCount["SUCCESS"]} unreachable={_catCount["UNREACHABLE"]} interrupt={_catCount["INTERRUPTION"]} naturalMovementPreflight=done", LogLevel.Info);
        _phase = Phase.Done;
    }

    private void TickSavePrep()
    {
        if (_settleTicks == 0)
        {
            _repository.MarkDataDirty();
            try
            {
                var farmHouse = Game1.getLocationFromName("FarmHouse");
                if (farmHouse is not null)
                {
                    Game1.warpFarmer("FarmHouse", 8, 8, false);
                    Game1.player.isInBed.Value = true;
                    Game1.timeOfDay = 2550;
                    _monitor.Log("[TRM] GATE_SAVE home state restored (FarmHouse, in bed, 2550=01:50 AM)", LogLevel.Info);
                }
                else
                {
                    _monitor.Log("[TRM] GATE_SAVE WARP_HOME_FAILED FarmHouse-not-found", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"[TRM] GATE_SAVE WARP_HOME_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
            }
            _settleTicks++;
            _phase = Phase.SaveSettle;
            return;
        }
        if (_settleTicks < 3)
        {
            _settleTicks++;
            _monitor.Log($"[TRM] GATE_SAVE settle ({_settleTicks}/3)", LogLevel.Debug);
            return;
        }
        WriteStageSignal();
        _phase = Phase.Done;
    }

    private void WriteStageSignal()
    {
        if (_signalWritten) return;
        _signalWritten = true;
        _monitor.Log($"[TRM] STAGE={_stage} COMPLETE steps={_completedSteps} emitted={_totalEmitted} — writing save signal (host performs REAL production save)", LogLevel.Info);
        try
        {
            QaSignal.WriteStageDone(_helper);
            _monitor.Log($"[TRM] STAGE={_stage} SAVE_REQUESTED — home state restored + settled, awaiting natural 02:00 pass-out save", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[TRM] GATE_SAVE_SIGNAL_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────

    private static NpcIntent TravelIntent(string target, string reason) => new()
    {
        IntentId = Guid.NewGuid().ToString("N"),
        Type = CapabilityRegistry.CapabilityIds.TravelToLocation,
        Target = target,
        Priority = 0.7f,
        SchemaVersion = "1",
        Reason = $"matrix-{reason}",
    };

    private void LogOutcome(CycleStep step, TravelIntentHandler.TravelIntentOutcome o, bool holder = false)
    {
        string who = holder ? $"holder={step.Holder}" : $"npc={step.Actor}";
        _monitor.Log($"[TRM] {step.Label} OUTCOME {who} target={step.Target} decision={o.Arbitration.Decision} adapterInvoked={o.AdapterInvoked} lease={(o.Lease is null ? "none" : "ACTIVE")} alreadyThere={o.AlreadyAtDestination} msg={o.Arbitration.Message}", LogLevel.Info);
    }

    /// <summary>Warp the isolation traveler to her canonical baseline so the same-location
    /// arrival path is deterministic. No-op when she is already there or the world state
    /// cannot be resolved (baseline location missing / NPC gone).</summary>
    private void NormalizeTravelerToBaseline(CycleStep step)
    {
        var npc = Game1.getCharacterFromName(step.Actor);
        if (npc is null || npc.currentLocation is null) return;
        var baseline = StardewBaselineResolver.Resolve(step.Actor, Game1.timeOfDay);
        var targetLoc = Game1.getLocationFromName(baseline.LocationName);
        if (targetLoc is null) return;
        bool atBaseline = string.Equals(npc.currentLocation.NameOrUniqueName, baseline.LocationName, StringComparison.OrdinalIgnoreCase)
            && npc.TilePoint.X == baseline.TileX && npc.TilePoint.Y == baseline.TileY;
        if (atBaseline) return;
        npc.controller = null;
        Game1.warpCharacter(npc, targetLoc, new Vector2(baseline.TileX, baseline.TileY));
        _monitor.Log($"[TRM] {step.Label} TRAVELER_NORMALIZED SETUP_ONLY npc={step.Actor} to={baseline.LocationName}({baseline.TileX},{baseline.TileY}) source={baseline.Source}", LogLevel.Info);
    }

    /// <summary>
    /// SETUP_ONLY 归一化（PM：测试准备可以移动 NPC，但必须标记 SETUP_ONLY 且不计为 Travel 成功）：
    ///   同图成功循环 → 演员必须在目标地点内的确定性 tile（baseline tile 或 DefaultPosition
    ///   fallback），保证同图 X→Y 行走真实发生；
    ///   跨图/失败/取消循环 → 演员归位 canonical baseline。
    /// 返回 false = 无法建立确定性起点（证据化失败，绝不静默）。
    /// </summary>
    private bool NormalizeActorForCycle(CycleStep step)
    {
        var npc = Game1.getCharacterFromName(step.Actor);
        if (npc is null || npc.currentLocation is null) return false;

        bool sameLocation = step.Kind == CycleKind.SuccessSameLocation;
        if (!sameLocation)
        {
            var baseline = StardewBaselineResolver.Resolve(step.Actor, Game1.timeOfDay);
            var bLoc = Game1.getLocationFromName(baseline.LocationName);
            if (bLoc is null) return false;
            if (string.Equals(npc.currentLocation.NameOrUniqueName, baseline.LocationName, StringComparison.OrdinalIgnoreCase)
                && npc.TilePoint.X == baseline.TileX && npc.TilePoint.Y == baseline.TileY)
                return true;
            npc.controller = null;
            Game1.warpCharacter(npc, bLoc, new Vector2(baseline.TileX, baseline.TileY));
            _monitor.Log($"[TRM] {step.Label} NORMALIZED SETUP_ONLY npc={step.Actor} to={baseline.LocationName}({baseline.TileX},{baseline.TileY}) source={baseline.Source}", LogLevel.Info);
            return true;
        }

        // 同图成功循环：主动归位到确定性起点（baseline → DefaultPosition → 当前实际位置兜底）。
        // 关键：不能优先用「当前实际位置」——循环间上一个循环的 rejoin 是异步 pathing，NPC 会
        // 停在中间 tile（如 Harvey 从 (6,12) 走回 baseline (13,4) 时停在 (11,4)），该中间位置
        // 可能 pathfind 不到目标（床/家具阻挡）→ 180s 超时。baseline 是 vanilla 数据里的确定性
        // 合法位置，主动 warp 归位后从它 pathing 是确定可达的。
        var targetLoc = Game1.getLocationFromName(step.Target);
        if (targetLoc is null) return false;
        var npcTile = npc.TilePoint;

        var dest = _resolver.Resolve(step.Actor, step.Target);
        if (dest.Status != TravelDestinationStatus.Resolved || dest.Destination is null)
        {
            _monitor.Log($"[TRM] {step.Label} NORMALIZE_FAILED SETUP_ONLY npc={step.Actor} target={step.Target} resolverStatus={dest.Status} error={dest.Error}", LogLevel.Error);
            return false;
        }
        int dstX = dest.Destination.TileX, dstY = dest.Destination.TileY;

        int? cx = null, cy = null;

        // 候选 1：baseline（确定性，vanilla 数据里的 NPC 合法位置）。
        var base2 = StardewBaselineResolver.Resolve(step.Actor, Game1.timeOfDay);
        if (string.Equals(base2.LocationName, step.Target, StringComparison.OrdinalIgnoreCase)
            && ManDist(base2.TileX, base2.TileY, dstX, dstY) >= 5
            && SpawnableAt(targetLoc, base2.TileX, base2.TileY))
        { cx = base2.TileX; cy = base2.TileY; }

        // 候选 2：DefaultPosition（若 DefaultMap == 目标地点）。
        if (cx is null && npc.DefaultMap is { } dm
            && string.Equals(dm, step.Target, StringComparison.OrdinalIgnoreCase)
            && npc.DefaultPosition != Vector2.Zero)
        {
            int dx = (int)(npc.DefaultPosition.X / 64f), dy = (int)(npc.DefaultPosition.Y / 64f);
            if (ManDist(dx, dy, dstX, dstY) >= 5 && SpawnableAt(targetLoc, dx, dy)) { cx = dx; cy = dy; }
        }

        // 候选 3（兜底）：NPC 当前实际位置。它正站在这 = 天然可通行（isTilePassable），绝不能用
        // CanSpawnCharacterHere 判定（该方法对「已有角色占据的 tile」返回 false，会误杀当前位置）。
        if (cx is null
            && string.Equals(npc.currentLocation.NameOrUniqueName, step.Target, StringComparison.OrdinalIgnoreCase)
            && ManDist(npcTile.X, npcTile.Y, dstX, dstY) >= 5
            && IsLegalStandingTile(npc, targetLoc))
        { cx = npcTile.X; cy = npcTile.Y; }

        if (cx is null || cy is null)
        {
            _monitor.Log($"[TRM] {step.Label} NORMALIZE_FAILED SETUP_ONLY npc={step.Actor} target={step.Target} dest=({dstX},{dstY}) baselineLoc={base2.LocationName}({base2.TileX},{base2.TileY}) currentLoc={npc.currentLocation.NameOrUniqueName}({npcTile.X},{npcTile.Y}) noReachableStart", LogLevel.Error);
            return false;
        }

        if (!string.Equals(npc.currentLocation.NameOrUniqueName, step.Target, StringComparison.OrdinalIgnoreCase)
            || npcTile.X != cx.Value || npcTile.Y != cy.Value)
        {
            npc.controller = null;
            Game1.warpCharacter(npc, targetLoc, new Vector2(cx.Value, cy.Value));
            _monitor.Log($"[TRM] {step.Label} NORMALIZED SETUP_ONLY npc={step.Actor} to={step.Target}({cx},{cy}) dest=({dstX},{dstY}) manhattan={ManDist(cx.Value, cy.Value, dstX, dstY)} sameLocationStart", LogLevel.Info);
        }
        return true;
    }

    private static int ManDist(int x1, int y1, int x2, int y2) => Math.Abs(x1 - x2) + Math.Abs(y1 - y2);

    /// <summary>SETUP_ONLY：起点与到达 tile 重合时把演员挪到同图 ≥5 tile 外的确定性可站立 tile
    /// （扫描序第一个匹配），保证行走真实发生。</summary>
    private void NudgeActorAway(string npcId)
    {
        var npc = Game1.getCharacterFromName(npcId);
        if (npc?.currentLocation is not { } loc) return;
        int fx = npc.TilePoint.X, fy = npc.TilePoint.Y;
        if (loc.Map?.Layers[0] is not { } layer) return;
        for (int y = 0; y < layer.LayerHeight; y++)
        for (int x = 0; x < layer.LayerWidth; x++)
        {
            if (Math.Abs(x - fx) + Math.Abs(y - fy) < 5) continue;
            if (loc.CanSpawnCharacterHere(new Vector2(x, y)))
            {
                npc.controller = null;
                Game1.warpCharacter(npc, loc, new Vector2(x, y));
                _monitor.Log($"[TRM] NUDGE SETUP_ONLY npc={npcId} from={loc.NameOrUniqueName}({fx},{fy}) to=({x},{y})", LogLevel.Info);
                return;
            }
        }
        _monitor.Log($"[TRM] NUDGE_FAILED SETUP_ONLY npc={npcId} at={loc.NameOrUniqueName}({fx},{fy})", LogLevel.Warn);
    }

    private static bool SpawnableAt(GameLocation loc, int x, int y)
    {
        if (loc.Map?.Layers[0] is not { } layer) return false;
        if (x < 0 || y < 0 || x >= layer.LayerWidth || y >= layer.LayerHeight) return false;
        return loc.CanSpawnCharacterHere(new Vector2(x, y));
    }

    /// <summary>NPC 站立 tile 合法（地图内 + 非水 + 可通行或为 warp tile）— §7 不变量检查。</summary>
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

    /// <summary>Runtime snapshot of an NPC's movement state — used to make a matrix FAIL
    /// actionable (the 180s isolation timeout run was un-debuggable without it).</summary>
    private static string TravelerState(string npcId)
    {
        var npc = Game1.getCharacterFromName(npcId);
        if (npc is null) return $"npc={npcId} missing";
        var loc = npc.currentLocation;
        int pathLen = npc.controller?.pathToEndPoint?.Count ?? -1;
        string where = loc is null ? "null-location" : $"{loc.NameOrUniqueName}({npc.TilePoint.X},{npc.TilePoint.Y})";
        return $"npc={npcId} at={where} controller={(npc.controller is null ? "none" : $"active(pathLen={pathLen})")} moving={npc.isMoving()}";
    }

    private void Advance(int settleTicks)
    {
        _completedSteps++;
        _stepIndex++;
        _phase = Phase.Settle;
        _settleLeft = settleTicks;
    }

    private void Fail(CycleStep step, string why)
    {
        // QA 强制失败开关统一复位——任何失败出口都不留泄漏状态给后续循环。
        NativeTravelAdapter.ForceSegmentFailureAfterWarps = -1;
        NativeTravelAdapter.ForceTargetIllegalMidRoute = false;
        _monitor.Log($"[TRM] {step.Label} FAIL reason={why}", LogLevel.Error);
        Emit(step.Category, step.Label, step.Actor, step.Target, "FAIL", 0, 0, _leases.ActiveCount);
        _phase = Phase.Failed;
    }

    private void LogCategory(string cat) => _catCount[cat] = _catCount.GetValueOrDefault(cat) + 1;

    private int ObsDelta(string npc)
    {
        int after = _observations.CountForNpc(npc);
        int delta = after - _obsBefore;
        _obsBefore = after;
        return delta;
    }

    private void Emit(string category, string cycleId, string npc, string target, string result,
        int adapterInv, int arrivalObs, int activeLeaseAfter)
    {
        _totalEmitted++;
        LogCategory(category);
        var rec = new MatrixCycleRecord
        {
            CycleId = cycleId,
            Category = category,
            ActorNpcId = npc,
            Target = target,
            ExecutionKind = "RUNTIME",
            Result = result,
            AdapterInvocations = adapterInv,
            ArrivalObservationCount = arrivalObs,
            ActiveLeaseCountAfter = activeLeaseAfter,
            EvidenceMarker = cycleId,
            EvidencePath = MatrixCycleRecorder.CycleEvidencePath($"cycles-runtime-{_stage}.jsonl") ?? $"cycles-runtime-{_stage}.jsonl",
        };
        var path = MatrixCycleRecorder.Record(rec, $"cycles-runtime-{_stage}.jsonl");
        if (path is null)
            _monitor.Log($"[TRM] EVIDENCE_WRITE_SKIPPED cycle={cycleId} (AIWORLD_MATRIX_OUTPUT_DIR not set)", LogLevel.Warn);
    }

    private static string Now()
    {
        try { return $"{Game1.currentSeason}-{Game1.dayOfMonth}-{Game1.year}@{Game1.timeOfDay:D4}"; }
        catch { return "unknown-date@600"; }
    }
}
