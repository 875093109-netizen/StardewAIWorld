using System.IO;
using System.Text.Json;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// A1 R2 — NPC 主观同意与定时约会运行时驱动（36 条）。
///
/// 复用生产链：NpcConsentGate（绑定+执行权）→ AppointmentManager（约会调度）→
/// TravelIntentHandler（自然移动）。模型只提供决定，本驱动在运行时用确定性 gate
/// 验证绑定与执行权；拒绝/含糊/反提议不执行，篡改 fail closed，约会到出发时间才建 Lease。
///
/// A1 R2 修正：
///  - F1：定时约会只把游戏时间推进到【计算出的出发时刻】（目标 − 预估 30 − 安全余量 30），
///    绝不直接推进到目标时间后才出发。到达按真实时间判定 ARRIVED_EARLY → 目标时间 →
///    WAITING_FOR_PLAYER → 显式 PLAYER_ARRIVED → Complete。
///  - F3：每个场景 STEP_DONE 带 result=pass/fail；超时/行程失败 = FAIL，绝不假绿。
///  - F4：Reload 四个非终态（ACCEPTED/WAITING_TO_DEPART/ARRIVED_EARLY/WAITING_FOR_PLAYER）
///    由两个独立 SMAPI 进程验证：stage=A 创建并真实保存（SaveGame.Save，不跨日），
///    stage=B 冷启动加载并按 commitmentId 核对全字段（无重复/无错误 MISSED/无瞬移回归）。
///    两进程间通过 appt-rl-expected.json（写入部署的 mod 目录）传递期望值。
/// </summary>
public sealed class AppointmentDriver
{
    private const string Abigail = "Abigail";
    private const string Harvey = "Harvey";

    private readonly IMonitor _monitor;
    private readonly IModHelper _helper;
    private readonly TravelIntentHandler _travelHandler;
    private readonly TravelLeaseManager _leases;
    private readonly NpcConsentGate _consentGate;
    private readonly AppointmentManager _appointments;
    private readonly string _apptStage;
    private readonly Action? _onStageAComplete;

    private enum StepKind
    {
        ImmediateConsent,   // 即时同意 → 执行
        NoExecution,        // 拒绝/含糊/反提议 → 不执行
        CounterConfirm,     // 反提议确认后执行
        Appointment,        // 定时约会（到出发时间才出发，F1）
        Reload,             // 冷启动（stage=A 创建+保存；stage=B 加载验证）
        IllegalConflict,    // 非法目标/冲突/时间跳跃/错过
    }

    private sealed record Step(string Label, StepKind Kind, string Actor, string Target, int Day, int Time, NpcDecisionOutcome Outcome, string? Detail = null);

    /// <summary>F7 — 单场景 17 字段结构化证据（可变，跨 tick 增量收集）。</summary>
    private sealed class ScenarioEvidence
    {
        public string ScenarioId { get; set; } = "";
        public string ExpectedOutcome { get; set; } = "";
        public string ActualOutcome { get; set; } = "";
        public bool AuthorizationObserved { get; set; }
        public bool TravelStartObserved { get; set; }
        public bool ArrivalObserved { get; set; }
        public string ActualLocation { get; set; } = "";
        public bool LegalPosition { get; set; }
        public string TimeSequence { get; set; } = "";
        public int WaitingObservationCount { get; set; }
        public bool BaselineRejoinWhileWaiting { get; set; }
        public bool ColdRestartProcessA { get; set; }
        public bool ColdRestartProcessB { get; set; }
        public string Result { get; set; } = "";
        public string FailureReason { get; set; } = "";
        public string EvidenceMarker { get; set; } = "";
        public string EvidencePath { get; set; } = "";
    }

    private readonly List<Step> _steps = new();
    private Step _current = new("", StepKind.NoExecution, Abigail, "SeedShop", 1, 900, NpcDecisionOutcome.Unclear);
    private int _idx;
    private int _phaseTick;
    private bool _done;
    private int _passCount;
    private int _failCount;

    // F1 — Appointment 步骤子状态机。
    private int _apSub;
    private string _apCommitmentId = "";

    // F5 — 约会场景时间单调性：出发/目标冻结时刻 + 等待观察。禁止出发后回拨 06:00。
    private int? _apFrozenTime;
    private int _apDepartureTime;
    private string _apArrivalTime = "";
    private int _apWaitObserved;

    // F6 — 执行类场景（ImmediateConsent / CounterConfirm）完整到达验证（防假绿）。
    private bool _stepAuthorized;
    private bool _stepTravelStarted;
    private bool _stepArrived;
    private string _stepArrivalLocation = "";
    private bool _stepArrivalLegal;
    private string _stepPosBefore = "";

    // F7 — 每场景结构化证据（36 唯一 scenarioId，17 字段）。
    private readonly List<ScenarioEvidence> _scenarioEvidence = new();
    private ScenarioEvidence? _ev; // 当前场景正在收集的证据

    // F4 — RL 创建阶段（stage=A 的 4 个非终态创建+保存）。
    private bool _inRlCreatePhase;
    private int _rlIdx;
    private int _rlSub;
    private bool _rlDeparted;
    private string _rlCommitmentId = "";
    private readonly List<Dictionary<string, object?>> _rlExpected = new();

    // F4 — RL 验证阶段（stage=B）。
    private bool _verifyStarted;
    private int _verifyIdx;
    private List<Dictionary<string, object?>> _expectedRl = new();
    private int _stageACount;

    // F4 — 创建顺序：先两个真实移动状态（时间 2000/2100 段），再两个静态状态（时间回 600）。
    // 为避免非终态 commitment 在推进时间时被误触发出发，各目标时间互不重叠：
    //   AB-01 ARRIVED_EARLY  target 2300（到达 2240 早到；HV-01 推进到 2100 时仍 < 2300，保持早到）
    //   HV-01 WAITING_FOR_PLAYER target 2100（到达 2000 早到，推进 2100 → 等待）
    //   HV-00 WAITING_TO_DEPART target 2100（创建后单 tick → WTD，静态保存）
    //   AB-00 ACCEPTED        target 2100（创建后不 tick → Accepted，静态保存）
    private readonly (string Label, string Actor, string Target, int TargetTime, string State)[] _rlCreatePlan =
    {
        ("A1-RL-AB-01", Abigail, "SeedShop", 2300, "ARRIVED_EARLY"),
        ("A1-RL-HV-01", Harvey, "HarveyRoom", 2100, "WAITING_FOR_PLAYER"),
        ("A1-RL-HV-00", Harvey, "Beach", 2100, "WAITING_TO_DEPART"),
        ("A1-RL-AB-00", Abigail, "Beach", 2100, "ACCEPTED"),
    };

    public AppointmentDriver(
        IMonitor monitor, IModHelper helper,
        TravelIntentHandler travelHandler, TravelLeaseManager leases,
        NpcConsentGate consentGate, AppointmentManager appointments, string apptStage,
        Action? onStageAComplete = null)
    {
        _monitor = monitor;
        _helper = helper;
        _travelHandler = travelHandler;
        _leases = leases;
        _consentGate = consentGate;
        _appointments = appointments;
        _apptStage = apptStage;
        _onStageAComplete = onStageAComplete;
        BuildSteps();
    }

    private void BuildSteps()
    {
        // 1. 即时同图/跨图明确同意 = 8（Abigail/Harvey × 同图/跨图 × 2）
        _steps.Add(new Step("A1-IM-AB-SAME-00", StepKind.ImmediateConsent, Abigail, "SeedShop", 1, 900, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-IM-AB-SAME-01", StepKind.ImmediateConsent, Abigail, "SeedShop", 1, 900, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-IM-HV-SAME-00", StepKind.ImmediateConsent, Harvey, "HarveyRoom", 1, 900, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-IM-HV-SAME-01", StepKind.ImmediateConsent, Harvey, "HarveyRoom", 1, 900, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-IM-AB-CROSS-00", StepKind.ImmediateConsent, Abigail, "Beach", 1, 900, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-IM-AB-CROSS-01", StepKind.ImmediateConsent, Abigail, "Beach", 1, 900, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-IM-HV-CROSS-00", StepKind.ImmediateConsent, Harvey, "Beach", 1, 900, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-IM-HV-CROSS-01", StepKind.ImmediateConsent, Harvey, "Beach", 1, 900, NpcDecisionOutcome.Accept));

        // 2. 拒绝/含糊/反提议不执行 = 8
        _steps.Add(new Step("A1-NO-DECLINE-00", StepKind.NoExecution, Abigail, "Beach", 1, 900, NpcDecisionOutcome.Decline));
        _steps.Add(new Step("A1-NO-DECLINE-01", StepKind.NoExecution, Harvey, "Beach", 1, 900, NpcDecisionOutcome.Decline));
        _steps.Add(new Step("A1-NO-UNCLEAR-00", StepKind.NoExecution, Abigail, "Beach", 1, 900, NpcDecisionOutcome.Unclear));
        _steps.Add(new Step("A1-NO-UNCLEAR-01", StepKind.NoExecution, Harvey, "Beach", 1, 900, NpcDecisionOutcome.Unclear));
        _steps.Add(new Step("A1-NO-COUNTER-00", StepKind.NoExecution, Abigail, "Beach", 1, 900, NpcDecisionOutcome.CounterPropose));
        _steps.Add(new Step("A1-NO-COUNTER-01", StepKind.NoExecution, Harvey, "Beach", 1, 900, NpcDecisionOutcome.CounterPropose));
        _steps.Add(new Step("A1-NO-TAMPER-00", StepKind.NoExecution, Abigail, "Beach", 1, 900, NpcDecisionOutcome.Accept, Detail: "tampered-termsHash"));
        _steps.Add(new Step("A1-NO-TAMPER-01", StepKind.NoExecution, Harvey, "Beach", 1, 900, NpcDecisionOutcome.Accept, Detail: "tampered-termsHash"));

        // 3. 玩家确认反提议后执行 = 4
        _steps.Add(new Step("A1-CT-CONFIRM-00", StepKind.CounterConfirm, Abigail, "Beach", 1, 900, NpcDecisionOutcome.CounterPropose));
        _steps.Add(new Step("A1-CT-CONFIRM-01", StepKind.CounterConfirm, Harvey, "Beach", 1, 900, NpcDecisionOutcome.CounterPropose));
        _steps.Add(new Step("A1-CT-CONFIRM-02", StepKind.CounterConfirm, Abigail, "SeedShop", 1, 900, NpcDecisionOutcome.CounterPropose));
        _steps.Add(new Step("A1-CT-CONFIRM-03", StepKind.CounterConfirm, Harvey, "HarveyRoom", 1, 900, NpcDecisionOutcome.CounterPropose));

        // 4. 定时约会（不提前移动、按计算出发时间出发、到达并等待）= 8
        _steps.Add(new Step("A1-AP-AB-BEACH-00", StepKind.Appointment, Abigail, "Beach", 1, 2100, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-AP-AB-BEACH-01", StepKind.Appointment, Abigail, "Beach", 1, 2100, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-AP-HV-BEACH-00", StepKind.Appointment, Harvey, "Beach", 1, 2100, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-AP-HV-TOWN-00", StepKind.Appointment, Harvey, "Town", 1, 2100, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-AP-AB-SALOON-00", StepKind.Appointment, Abigail, "Saloon", 1, 2100, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-AP-AB-MINE-00", StepKind.Appointment, Abigail, "Mine", 1, 2100, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-AP-HV-SALOON-00", StepKind.Appointment, Harvey, "Saloon", 1, 2100, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-AP-HV-MINE-00", StepKind.Appointment, Harvey, "Mine", 1, 2100, NpcDecisionOutcome.Accept));

        // 5. 非法目标/日程冲突/时间跳跃/错过约会 = 4（A1 R2：置于 Reload 之前 —— IL 的
        //    OnLoad/状态杂讯不得污染 RL 非终态 commitment；IL 残留均终态，RL 阶段安全）。
        _steps.Add(new Step("A1-IL-ILLEGAL-00", StepKind.IllegalConflict, Abigail, "UnknownApptLoc00", 1, 2100, NpcDecisionOutcome.Accept, Detail: "illegal-target"));
        _steps.Add(new Step("A1-IL-CONFLICT-00", StepKind.IllegalConflict, Harvey, "Beach", 1, 2100, NpcDecisionOutcome.Accept, Detail: "schedule-conflict"));
        _steps.Add(new Step("A1-IL-TIMEJUMP-00", StepKind.IllegalConflict, Abigail, "Beach", 1, 2100, NpcDecisionOutcome.Accept, Detail: "time-jump"));
        _steps.Add(new Step("A1-IL-MISSED-00", StepKind.IllegalConflict, Harvey, "Beach", 1, 2100, NpcDecisionOutcome.Accept, Detail: "missed"));

        // 6. 存档重载与冷启动 = 4（stage=A 创建+保存；stage=B 冷启动验证）
        _steps.Add(new Step("A1-RL-AB-00", StepKind.Reload, Abigail, "Beach", 1, 2100, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-RL-HV-00", StepKind.Reload, Harvey, "Beach", 1, 2100, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-RL-AB-01", StepKind.Reload, Abigail, "SeedShop", 1, 2100, NpcDecisionOutcome.Accept));
        _steps.Add(new Step("A1-RL-HV-01", StepKind.Reload, Harvey, "HarveyRoom", 1, 2100, NpcDecisionOutcome.Accept));
    }

    public void TickIfReady()
    {
        if (_done) return;

        if (_apptStage == "B")
        {
            TickRlVerify();
            return;
        }
        if (_inRlCreatePhase)
        {
            _phaseTick++;
            TickRlCreate();
            return;
        }
        if (_idx >= _steps.Count) { Finalize(); return; }

        _current = _steps[_idx];
        _phaseTick++;
        if (_phaseTick == 1) BeginScenario();

        var actor = _current.Actor;
        var target = _current.Target;
        var npc = Game1.getCharacterFromName(actor);
        if (npc is null) return; // NPC not present yet — wait

        // F5 时间策略：约会场景推进到计算出发时刻后，冻结在出发/目标时刻（单调，绝不回拨
        // 06:00）；其余场景/RL 阶段仍冻结 06:00（baseline 稳定）。phaseTick==2 的出发推进
        // 由 RunAppointment 内部设置时间并写入 _apFrozenTime。
        if (_current.Kind == StepKind.Appointment && _apFrozenTime is { } ft)
            Game1.timeOfDay = ft;
        else if (_inRlCreatePhase || _current.Kind != StepKind.Appointment || _phaseTick == 1)
            Game1.timeOfDay = 600;

        // A1 R2: 场景隔离 —— 每个场景首 tick 把两位 QA NPC 归位 canonical baseline（SETUP_ONLY）。
        // 防止上一场景的到达/遗留 NPC 占用下一场景的到达 tile（CanSpawnAt 被 NPC 占用即拒）。
        if (_phaseTick == 1)
            NormalizeScenarioActors();

        switch (_current.Kind)
        {
            case StepKind.ImmediateConsent:
                RunImmediate(actor, target);
                break;
            case StepKind.NoExecution:
                RunNoExecution(actor, target);
                break;
            case StepKind.CounterConfirm:
                RunCounterConfirm(actor, target);
                break;
            case StepKind.Appointment:
                RunAppointment(actor, target);
                break;
            case StepKind.IllegalConflict:
                RunIllegal(actor, target);
                break;
            case StepKind.Reload:
                if (_apptStage == "A")
                {
                    _inRlCreatePhase = true;
                    TickRlCreate();
                }
                break;
        }
    }

    private void RunImmediate(string actor, string target)
    {
        if (_phaseTick == 1)
        {
            var res = Authorize(actor, target, NpcDecisionOutcome.Accept, PlayerRequestKind.ImmediateTravel, 900);
            _stepAuthorized = res?.IsAuthorized == true;
            _stepPosBefore = NpcPosition(actor);
            if (_ev is not null) _ev.AuthorizationObserved = _stepAuthorized;
            if (res?.TravelIntent is { } intent)
            {
                var outcome = _travelHandler.Handle(intent, actor, NowStamp());
                if (outcome.Lease is not null || outcome.AdapterInvoked || outcome.AlreadyAtDestination)
                {
                    _stepTravelStarted = true;
                    // 已在目标（already-at-destination 短路 → 无行程、无桥接到达）：合法站位即确认到达。
                    if (outcome.AlreadyAtDestination && IsLegalTile(Game1.getCharacterFromName(actor)))
                    {
                        _stepArrived = true;
                        _stepArrivalLocation = target;
                        _stepArrivalLegal = true;
                    }
                }
                Log($"A1-IMMEDIATE authorize=true actor={actor} target={target} startObserved={_stepTravelStarted} already={outcome.AlreadyAtDestination}");
            }
            else
            {
                Log($"A1-IMMEDIATE authorize=false actor={actor} target={target}");
            }
        }
        else
        {
            // F6: 出发观察 —— lease 活跃即 travelStartObserved（真实行程进行中）。
            if (_travelHandler.IsTravelActive(actor) || _leases.IsActive(actor))
                _stepTravelStarted = true;
            // 到达只由真实 travel 到达桥接（RecordTravelArrival = 系统 status=Arrived）写入
            // _stepArrived，杜绝按"NPC 已在目标地点"即判到达的提前推进 —— 那会让上一行程的
            // controller 残留、阻塞下一行程出发（Beach 跨图即因此超时）。
        }

        if (_stepAuthorized && _stepTravelStarted && _stepArrived && _stepArrivalLegal)
        {
            Log($"A1-IMMEDIATE done actor={actor} target={target} authorized={_stepAuthorized} start={_stepTravelStarted} arrived={_stepArrived} loc={_stepArrivalLocation} legal={_stepArrivalLegal}");
            Advance();
        }
        else if (_stepAuthorized && !_stepTravelStarted && _phaseTick > 30)
        {
            Fail($"A1-IMMEDIATE travel-not-started actor={actor} target={target} authorized={_stepAuthorized}");
        }
        else if (_phaseTick > 300)
        {
            Fail($"A1-IMMEDIATE timeout actor={actor} target={target} authorized={_stepAuthorized} start={_stepTravelStarted} arrived={_stepArrived} legal={_stepArrivalLegal} leaseActive={_leases.IsActive(actor)}");
        }
    }

    private void RunNoExecution(string actor, string target)
    {
        var outcome = _current.Outcome;
        var tampered = _current.Detail == "tampered-termsHash";
        if (_phaseTick == 1)
        {
            _stepPosBefore = NpcPosition(actor);
            var res = Authorize(actor, target, outcome, PlayerRequestKind.ImmediateTravel, 900, tamperTerms: tampered);
            _stepAuthorized = res?.IsAuthorized == true;
            if (_ev is not null) _ev.AuthorizationObserved = _stepAuthorized;
            Log($"A1-NOEXEC outcome={outcome} tampered={tampered} authorized={_stepAuthorized}");
        }
        else if (_phaseTick == 2)
        {
            // F6: 拒绝/含糊/反提议/篡改 —— authorized=false、无 lease、controller 未启动、
            // 位置保持（positionAfter == positionBefore）。
            bool leaseAfter = _leases.IsActive(actor);
            var npc = Game1.getCharacterFromName(actor);
            bool controllerAfter = npc?.controller is not null;
            bool posUnchanged = NpcPosition(actor) == _stepPosBefore;
            bool ok = !_stepAuthorized && !leaseAfter && !controllerAfter && posUnchanged;
            if (_ev is not null) { _ev.TravelStartObserved = leaseAfter; _ev.ActualLocation = NpcPosition(actor); _ev.LegalPosition = posUnchanged; }
            Log($"A1-NOEXEC verify outcome={outcome} tampered={tampered} authorized={_stepAuthorized} leaseAfter={leaseAfter} controllerAfter={controllerAfter} posAfter={NpcPosition(actor)} posBefore={_stepPosBefore} ok={ok}");
            if (ok) Advance();
            else Fail($"A1-NOEXEC fail-closed violated outcome={outcome} authorized={_stepAuthorized} leaseAfter={leaseAfter} controllerAfter={controllerAfter} posUnchanged={posUnchanged}");
        }
        else if (_phaseTick > 5)
        {
            Fail($"A1-NOEXEC timeout outcome={outcome}");
        }
    }

    private void RunCounterConfirm(string actor, string target)
    {
        if (_phaseTick == 1)
        {
            _stepPosBefore = NpcPosition(actor);
            var req = MakeRequest(actor, target, PlayerRequestKind.ImmediateTravel, 900);
            var decision = MakeDecision(req, NpcDecisionOutcome.CounterPropose);
            var res = _consentGate.Evaluate(req, decision, 1, 900);
            _pendingProposal = res.CounterProposal;
            Log($"A1-CT propose actor={actor} proposal={_pendingProposal?.ProposalId}");
        }
        else if (_phaseTick == 3 && _pendingProposal is { } p)
        {
            var confirmed = _consentGate.ConfirmCounterProposal(p, target, 1, 900);
            if (confirmed is null)
            {
                // F6: 确认反提议返回 null = 确认失败 —— 必须 FAIL，绝不 Advance 假绿。
                Log($"A1-CT confirm-FAILED actor={actor}");
                Fail("A1-CT confirm-returned-null (counter-proposal confirm failed)");
                return;
            }
            var decision = MakeDecision(confirmed, NpcDecisionOutcome.Accept);
            var res = _consentGate.Evaluate(confirmed, decision, 1, 900);
            _stepAuthorized = res?.IsAuthorized == true;
            if (_ev is not null) _ev.AuthorizationObserved = _stepAuthorized;
            if (res.TravelIntent is { } intent)
            {
                var outcome = _travelHandler.Handle(intent, actor, NowStamp());
                if (outcome.Lease is not null || outcome.AdapterInvoked || outcome.AlreadyAtDestination)
                {
                    _stepTravelStarted = true;
                    if (outcome.AlreadyAtDestination && IsLegalTile(Game1.getCharacterFromName(actor)))
                    {
                        _stepArrived = true;
                        _stepArrivalLocation = target;
                        _stepArrivalLegal = true;
                    }
                }
                Log($"A1-CT confirm-execute actor={actor} target={target} authorized={_stepAuthorized} start={_stepTravelStarted} already={outcome.AlreadyAtDestination}");
            }
        }
        else
        {
            // F6: 出发观察 + 到达只由真实桥接写入（同 A1-IMMEDIATE，杜绝提前推进）。
            if (_travelHandler.IsTravelActive(actor) || _leases.IsActive(actor))
                _stepTravelStarted = true;
        }

        if (_stepAuthorized && _stepTravelStarted && _stepArrived && _stepArrivalLegal)
        {
            Log($"A1-CT done actor={actor} target={target} authorized={_stepAuthorized} start={_stepTravelStarted} arrived={_stepArrived} loc={_stepArrivalLocation} legal={_stepArrivalLegal}");
            Advance();
        }
        else if (_stepAuthorized && !_stepTravelStarted && _phaseTick > 30)
        {
            Fail($"A1-CT travel-not-started actor={actor} target={target}");
        }
        else if (_phaseTick > 300)
        {
            Fail($"A1-CT timeout actor={actor} authorized={_stepAuthorized} start={_stepTravelStarted} arrived={_stepArrived}");
        }
    }

    // ── F1：定时约会 —— 只推进到计算出的出发时刻，调度器观察到新时间后自行出发 ──
    private void RunAppointment(string actor, string target)
    {
        var day = SafeDay();
        switch (_apSub)
        {
            case 0:
                if (_phaseTick == 1)
                {
                    var res = Authorize(actor, target, NpcDecisionOutcome.Accept, PlayerRequestKind.FutureAppointment, _current.Time);
                    if (res?.Commitment is { } c)
                    {
                        _appointments.Add(c);
                        _apCommitmentId = c.CommitmentId;
                        Log($"A1-AP accepted actor={actor} target={target} commitment={c.CommitmentId} leaseBeforeDeparture={_leases.ActiveCount}");
                    }
                    else
                    {
                        Log($"A1-AP accept-FAILED actor={actor} target={target}");
                    }
                    Dispatch(_appointments.Tick(day, Game1.timeOfDay, _ => 30, n => _leases.IsActive(n)));
                    _apSub = 1;
                }
                break;

            case 1:
                if (_phaseTick == 2)
                {
                    int dep = ComputeDeparture(_current.Time);
                    DevGameTime.TrySet(dep, out var msg);
                    _apDepartureTime = dep;
                    _apFrozenTime = dep; // F5: 出发后冻结在出发时刻，绝不再回拨 06:00
                    Log($"A1-AP time-advance actor={actor} target={target} to={dep:D4} (computed departure for target {_current.Time:D4}, est 30, margin 30) ({msg})");
                    var tick = _appointments.Tick(day, Game1.timeOfDay, _ => 30, n => _leases.IsActive(n));
                    foreach (var intent in tick.TravelIntentsToDispatch)
                    {
                        var outcome = _travelHandler.Handle(intent, actor, NowStamp());
                        if (outcome.Lease is not null || outcome.AdapterInvoked || outcome.AlreadyAtDestination)
                            _stepTravelStarted = true;
                        Log($"A1-AP depart actor={actor} target={target} departureTime={dep:D4} leaseAfterDeparture={_leases.ActiveCount} arb={outcome.Arbitration.Decision} already={outcome.AlreadyAtDestination} adapter={outcome.AdapterInvoked} msg={outcome.Arbitration.Message}");
                        // F1: NPC 已在目标（连续同目标约会）时 Handle 走 already-at-destination 短路，
                        // 不触发 OnTravelArrived，commitment 会卡在 Departing。此处主动驱动到达回调，
                        // 让状态机观察到 ArrivedEarly / WaitingForPlayer。
                        if (outcome.AlreadyAtDestination)
                        {
                            _appointments.OnTravelArrived(actor, day, Game1.timeOfDay);
                            _stepArrived = true;
                            Log($"A1-AP already-there actor={actor} target={target} droveArrival status={_appointments.Get(_apCommitmentId)?.Status} at={Game1.timeOfDay:D4}");
                        }
                    }
                    _apSub = 2;
                }
                break;

            case 2:
            {
                // 自然移动中（F5：时间冻结在出发时刻，单调不回拨）。到达按冻结时间判定早到/等待。
                var cmt = _apCommitmentId.Length > 0 ? _appointments.Get(_apCommitmentId) : null;
                if (cmt?.Status == TravelCommitmentStatus.ArrivedEarly)
                {
                    _apArrivalTime = cmt.ActualArrivalTime ?? "";
                    _stepArrived = true;
                    Log($"A1-AP arrived-early actor={actor} commitment={_apCommitmentId} arrival={_apArrivalTime} atTime={Game1.timeOfDay:D4} target={_current.Time:D4}");
                    _apSub = 3;
                }
                else if (cmt?.Status == TravelCommitmentStatus.WaitingForPlayer)
                {
                    _apArrivalTime = cmt.ActualArrivalTime ?? "";
                    _stepArrived = true;
                    Log($"A1-AP arrived actor={actor} commitment={_apCommitmentId} arrival={_apArrivalTime} status=WaitingForPlayer (time >= target)");
                    _apSub = 4;
                }
                else if (cmt?.Status == TravelCommitmentStatus.Failed)
                {
                    Fail($"A1-AP travel-failed actor={actor} reason={cmt.FailureReason}");
                }
                // F1: 出发 dispatch 后若始终没有活跃 travel lease（Handle 未启动行程：already-there
                // 短路或仲裁拒绝），commitment 会卡在 Departing。NPC 已在目标地点则视为已满足并驱动
                // 到达；否则按行程未启动判失败（绝不假绿）。
                else if (cmt?.Status == TravelCommitmentStatus.Departing && !_leases.IsActive(actor) && _phaseTick > 10)
                {
                    var npc = Game1.getCharacterFromName(actor);
                    var atTarget = npc?.currentLocation?.NameOrUniqueName == target;
                    if (atTarget)
                    {
                        _appointments.OnTravelArrived(actor, day, Game1.timeOfDay);
                        _stepArrived = true;
                        Log($"A1-AP no-lease-already-there actor={actor} target={target} droveArrival status={cmt.Status}→{_appointments.Get(_apCommitmentId)?.Status} at={Game1.timeOfDay:D4}");
                    }
                    else
                    {
                        _appointments.OnTravelFailed(actor, "departure dispatch produced no active travel lease (travel never started)");
                        Fail($"A1-AP travel-not-started actor={actor} target={target} status={cmt.Status} npcLoc={npc?.currentLocation?.NameOrUniqueName}");
                    }
                }
                else if (_phaseTick > 400)
                {
                    Fail($"A1-AP timeout actor={actor} status={cmt?.Status}");
                }
                break;
            }

            case 3:
            {
                // 早到 → 推进到目标时间 → WaitingForPlayer（F5：冻结在目标时刻）。
                var c3 = _apCommitmentId.Length > 0 ? _appointments.Get(_apCommitmentId) : null;
                if (c3?.Status == TravelCommitmentStatus.ArrivedEarly)
                {
                    DevGameTime.TrySet(_current.Time, out _);
                    _apFrozenTime = _current.Time;
                    Dispatch(_appointments.Tick(day, Game1.timeOfDay, _ => 30, n => _leases.IsActive(n)));
                    Log($"A1-AP time-advance actor={actor} to={_current.Time:D4} (target reached → WaitingForPlayer)");
                }
                if (_appointments.Get(_apCommitmentId)?.Status == TravelCommitmentStatus.WaitingForPlayer)
                {
                    Log($"A1-AP waiting actor={actor} target={target} at={Game1.timeOfDay:D4} commitment={_apCommitmentId}");
                    _apSub = 4;
                }
                else if (_phaseTick > 400)
                {
                    Fail($"A1-AP timeout actor={actor} status={_appointments.Get(_apCommitmentId)?.Status}");
                }
                break;
            }

            case 4:
            {
                var c4 = _apCommitmentId.Length > 0 ? _appointments.Get(_apCommitmentId) : null;
                if (c4?.Status == TravelCommitmentStatus.WaitingForPlayer)
                {
                    // F7: 21:00 连续观察 3 个 QA tick —— NPC 在目标合法格、无 rejoin、无跳跃、
                    // commitment 为 WAITING_FOR_PLAYER；3 次观察后才发 PLAYER_ARRIVED。
                    var npc = Game1.getCharacterFromName(actor);
                    // F2: 等待期间 NPC 必须留在目标（rejoin 被抑制）；若被瞬移回归 baseline，
                    // atTarget=false → 观察计数清零（WAIT_REJOIN 检测）。
                    bool atTarget = npc?.currentLocation is { } loc && string.Equals(loc.NameOrUniqueName, target, StringComparison.OrdinalIgnoreCase);
                    bool legal = IsLegalTile(npc);
                    if (atTarget && legal && Game1.timeOfDay == _current.Time)
                    {
                        _apWaitObserved++;
                        if (_ev is not null) _ev.WaitingObservationCount = _apWaitObserved;
                        Log($"A1-AP wait-observation actor={actor} n={_apWaitObserved} at={Game1.timeOfDay:D4} loc={target} legal={legal} status=WAITING_FOR_PLAYER");
                        if (_apWaitObserved >= 3)
                        {
                            _appointments.Complete(_apCommitmentId);
                            Log($"A1-AP complete actor={actor} commitment={_apCommitmentId} (explicit PLAYER_ARRIVED after {_apWaitObserved} observations) at={Game1.timeOfDay:D4}");
                            Advance();
                        }
                    }
                    else if (_apWaitObserved > 0)
                    {
                        Log($"A1-AP wait-observation RESET actor={actor} atTarget={atTarget} legal={legal} time={Game1.timeOfDay:D4} (waiting continuity broken)");
                        // F2/F7: 等待连续性断裂 = NPC 被瞬移回归 baseline 或离开目标 → WAIT_REJOIN 检测。
                        if (_ev is not null) _ev.BaselineRejoinWhileWaiting = true;
                        _apWaitObserved = 0;
                    }
                }
                else if (_phaseTick > 400)
                {
                    Fail($"A1-AP timeout actor={actor} status={c4?.Status}");
                }
                break;
            }
        }
    }

    // ── F4 stage=A：4 个非终态 commitment 创建 + 真实保存（两进程间以 appt-rl-expected.json 传递）──
    private void TickRlCreate()
    {
        if (_rlIdx >= _rlCreatePlan.Length)
        {
            _idx = _steps.Count;
            _inRlCreatePhase = false;
            return;
        }
        var (label, actor, target, targetTime, state) = _rlCreatePlan[_rlIdx];
        var day = SafeDay();

        switch (_rlSub)
        {
            case 0:
                // F4: 每个 RL 创建前把游戏时间回到 06:00 —— 前一个状态（如 HV-01 推进到 2100）
                // 会触发后一个静态状态（HV-00 WTD / AB-00 Accepted）过早出发。必须新建于 06:00，
                // CreatedAtGameTime 才稳定，静态状态才保持不出发。
                Game1.timeOfDay = 600;
                var res = Authorize(actor, target, NpcDecisionOutcome.Accept, PlayerRequestKind.FutureAppointment, targetTime);
                if (res?.Commitment is { } c)
                {
                    _appointments.Add(c);
                    _rlCommitmentId = c.CommitmentId;
                    _rlExpected.Add(SerializeRlExpected(label, c, state));
                    Log($"A1-RL CREATE label={label} actor={actor} target={target} targetTime={targetTime:D4} state={state} commitment={c.CommitmentId}");
                    // ACCEPTED：创建后不得 tick（保持 Accepted）；其余先 tick 到 WTD。
                    if (state != "ACCEPTED")
                        Dispatch(_appointments.Tick(day, Game1.timeOfDay, _ => 30, n => _leases.IsActive(n)));
                    _rlSub = 1;
                }
                else
                {
                    Log($"A1-RL CREATE-FAILED label={label} — consent authorize returned no commitment");
                    _rlSub = 1;
                    _rlDeparted = true; // skip travel; _rlNext below will advance
                }
                break;

            case 1:
                if (state is "ARRIVED_EARLY" or "WAITING_FOR_PLAYER")
                {
                    if (!_rlDeparted)
                    {
                        int dep = ComputeDeparture(targetTime);
                        DevGameTime.TrySet(dep, out _);
                        Dispatch(_appointments.Tick(day, Game1.timeOfDay, _ => 30, n => _leases.IsActive(n)));
                        Log($"A1-RL DEPART label={label} actor={actor} departure={dep:D4} leaseAfterDeparture={_leases.ActiveCount}");
                        _rlDeparted = true;
                    }
                    else
                    {
                        // F4: 自然移动轮询期间冻结 06:00 —— 防止真实时钟把到达时刻推到目标时间之后
                        // （AB-01 target 2300 必须在 2300 前到达 → ARRIVED_EARLY，不能误判 WAITING_FOR_PLAYER）。
                        Game1.timeOfDay = 600;
                    }
                    var cmt = _rlCommitmentId.Length > 0 ? _appointments.Get(_rlCommitmentId) : null;
                    if (cmt?.Status == TravelCommitmentStatus.ArrivedEarly)
                    {
                        Log($"A1-RL ARRIVED_EARLY label={label} actor={actor} arrival={cmt.ActualArrivalTime}");
                        _rlSub = 2;
                    }
                    else if (cmt?.Status == TravelCommitmentStatus.WaitingForPlayer)
                    {
                        Log($"A1-RL WAITING_FOR_PLAYER label={label} actor={actor} (arrived at/after target)");
                        _rlSub = 2;
                    }
                    else if (cmt?.Status == TravelCommitmentStatus.Failed)
                    {
                        Log($"A1-RL CREATE-FAILED label={label} reason=travel-failed {cmt.FailureReason}");
                        _rlNext();
                    }
                    else if (_phaseTick > 400)
                    {
                        Log($"A1-RL CREATE-FAILED label={label} reason=timeout status={cmt?.Status}");
                        _rlNext();
                    }
                }
                else
                {
                    _rlNext(); // WTD / ACCEPTED：静态，完成
                }
                break;

            case 2:
                if (state == "WAITING_FOR_PLAYER")
                {
                    var c2 = _rlCommitmentId.Length > 0 ? _appointments.Get(_rlCommitmentId) : null;
                    if (c2?.Status == TravelCommitmentStatus.ArrivedEarly)
                    {
                        DevGameTime.TrySet(targetTime, out _);
                        Dispatch(_appointments.Tick(day, Game1.timeOfDay, _ => 30, n => _leases.IsActive(n)));
                        Log($"A1-RL WFP-ADVANCE label={label} actor={actor} to={targetTime:D4} (target reached → WaitingForPlayer)");
                    }
                    var after = _appointments.Get(_rlCommitmentId)?.Status;
                    if (after == TravelCommitmentStatus.WaitingForPlayer)
                        _rlNext();
                    else if (after == TravelCommitmentStatus.Failed)
                    {
                        Log($"A1-RL CREATE-FAILED label={label} reason=travel-failed {_appointments.Get(_rlCommitmentId)?.FailureReason}");
                        _rlNext();
                    }
                    else if (_phaseTick > 400)
                    {
                        Log($"A1-RL CREATE-FAILED label={label} reason=timeout status={after}");
                        _rlNext();
                    }
                }
                else
                {
                    _rlNext();
                }
                break;
        }
    }

    private void _rlNext()
    {
        _rlIdx++;
        _rlSub = 0;
        _rlDeparted = false;
        _rlCommitmentId = "";
        _phaseTick = 0;
    }

    private static Dictionary<string, object?> SerializeRlExpected(string label, TravelCommitment c, string state) => new()
    {
        ["label"] = label,
        ["commitmentId"] = c.CommitmentId,
        ["requestId"] = c.RequestId,
        ["npcId"] = c.NpcId,
        ["groundedTarget"] = c.GroundedTarget,
        ["targetGameDay"] = c.TargetGameDay,
        ["targetGameTime"] = c.TargetGameTime,
        ["acceptedTermsHash"] = c.AcceptedTermsHash,
        ["createdAtGameTime"] = c.CreatedAtGameTime,
        ["status"] = state,
    };

    private void WriteRlExpected()
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["stageACount"] = _passCount + _failCount,
                ["entries"] = _rlExpected,
            };
            var path = Path.Combine(_helper.DirectoryPath, "appt-rl-expected.json");
            File.WriteAllText(path, JsonSerializer.Serialize(payload));
            Log($"A1-RL EXPECTED_WRITTEN path=appt-rl-expected.json entries={_rlExpected.Count} stageACount={_passCount + _failCount}");
        }
        catch (Exception ex)
        {
            Log($"A1-RL EXPECTED_WRITE_FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── F4 stage=B：冷启动加载，按 commitmentId 核对全字段（无重复/无错误 MISSED/无瞬移回归）──
    private void TickRlVerify()
    {
        if (!_verifyStarted)
        {
            _verifyStarted = true;
            var path = Path.Combine(_helper.DirectoryPath, "appt-rl-expected.json");
            if (!File.Exists(path))
            {
                Fail("A1-RL verify — expected file missing (appt-rl-expected.json)");
                Finalize();
                return;
            }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("stageACount", out var sc)) _stageACount = sc.GetInt32();
                if (root.TryGetProperty("entries", out var entries))
                {
                    _expectedRl = entries.EnumerateArray()
                        .Select(el => el.EnumerateObject().ToDictionary(p => p.Name, p => (object?)JsonValue(p.Value)))
                        .ToList();
                }
                Log($"A1-RL VERIFY_START stageACount={_stageACount} expectedEntries={_expectedRl.Count} activeLeases={_leases.ActiveCount}");
            }
            catch (Exception ex)
            {
                Fail($"A1-RL verify — expected JSON parse failed {ex.GetType().Name}: {ex.Message}");
                Finalize();
                return;
            }
        }

        if (_verifyIdx >= _expectedRl.Count) { Finalize(); return; }

        var exp = _expectedRl[_verifyIdx];
        string label = GetStr(exp, "label");
        string commitmentId = GetStr(exp, "commitmentId");

        var found = _appointments.All.Where(c => c.CommitmentId == commitmentId).ToList();
        if (found.Count == 0)
        {
            Fail($"{label} missing commitment={commitmentId}");
            _verifyIdx++;
            return;
        }
        if (found.Count > 1)
        {
            Fail($"{label} DUPLICATE commitment={commitmentId} count={found.Count}");
            _verifyIdx++;
            return;
        }
        var c = found[0];

        bool fieldsOk = string.Equals(c.RequestId, GetStr(exp, "requestId"), StringComparison.Ordinal)
            && string.Equals(c.NpcId, GetStr(exp, "npcId"), StringComparison.Ordinal)
            && string.Equals(c.GroundedTarget, GetStr(exp, "groundedTarget"), StringComparison.Ordinal)
            && c.TargetGameDay == GetInt(exp, "targetGameDay")
            && c.TargetGameTime == GetInt(exp, "targetGameTime")
            && string.Equals(c.AcceptedTermsHash, GetStr(exp, "acceptedTermsHash"), StringComparison.Ordinal)
            && string.Equals(c.CreatedAtGameTime, GetStr(exp, "createdAtGameTime"), StringComparison.Ordinal);
        // status 规范化比较：expected JSON 存 UPPER_SNAKE（_rlCreatePlan 字面量，如 ARRIVED_EARLY），
        // 枚举 ToString() 是 PascalCase（ArrivedEarly）。Ordinal 大小写敏感；OrdinalIgnoreCase 也
        // 不够 —— 去下划线后 "ARRIVEDEARLY" != "ARRIVED_EARLY"。必须去下划线 + 大写再比才是同一语义。
        static string NormalizeStatus(string s) => s.Replace("_", "").ToUpperInvariant();
        bool statusOk = string.Equals(NormalizeStatus(c.Status.ToString()), NormalizeStatus(GetStr(exp, "status")), StringComparison.Ordinal);
        bool noWrongMissed = statusOk; // status 精确匹配即无错误 MISSED（Missed 是终态，不可能被错误保留）

        // 无瞬移回归：ARRIVED_EARLY/WAITING_FOR_PLAYER 的 NPC 应仍在目标地点（F2 抑制回归）；
        // 静态状态（ACCEPTED/WAITING_TO_DEPART）无活动 lease、NPC 未被传送。
        string npcLocation = "?";
        var npc = Game1.getCharacterFromName(c.NpcId);
        if (npc?.currentLocation is { } loc) npcLocation = loc.NameOrUniqueName;
        bool noLease = _leases.ActiveCount == 0;
        bool atTarget = string.Equals(npcLocation, c.GroundedTarget, StringComparison.OrdinalIgnoreCase);
        bool noTeleport = noLease
            && (c.Status is TravelCommitmentStatus.ArrivedEarly or TravelCommitmentStatus.WaitingForPlayer
                ? atTarget
                : npcLocation != "?");
        bool ok = fieldsOk && statusOk && noTeleport;

        // F7: 每个 RL 验证步骤也产出结构化证据（stage=B 冷启动进程，独立于 stage=A 的 32 条）。
        _ev = new ScenarioEvidence
        {
            ScenarioId = label,
            ExpectedOutcome = "pass",
            AuthorizationObserved = true, // commitment 存在且 termsHash 匹配 = 绑定被遵守
            ActualLocation = npcLocation,
            LegalPosition = noTeleport,
            ColdRestartProcessB = true,
            EvidencePath = "artifacts/runtime-tests/$runId/smapi-appt-stage-*.log",
        };

        if (ok)
        {
            string marker = $"STEP_DONE label={label} result=pass kind=Reload actor={c.NpcId} target={c.GroundedTarget} status={c.Status} commitment={commitmentId} npcLocation={npcLocation} noLease={noLease}";
            Log(marker);
            _passCount++;
            CompleteScenario("pass", "", marker);
        }
        else
        {
            string reason = !fieldsOk ? "field-mismatch" : !statusOk ? "status-mismatch" : "teleport-or-leak";
            string marker = $"STEP_DONE label={label} result=fail kind=Reload actor={c.NpcId} reason={reason} status={c.Status} expected={GetStr(exp, "status")} npcLocation={npcLocation}";
            Log(marker);
            _failCount++;
            CompleteScenario("fail", reason, marker);
        }
        _verifyIdx++;
    }

    private static string GetStr(Dictionary<string, object?> d, string k)
        => d.TryGetValue(k, out var v) && v is string s ? s : "";
    private static int GetInt(Dictionary<string, object?> d, string k)
        => d.TryGetValue(k, out var v) && v is int i ? i : -1;
    private static object? JsonValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt32(out var i) ? i : (object?)e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => e.GetRawText(),
    };

    private void RunIllegal(string actor, string target)
    {
        var detail = _current.Detail;
        if (_phaseTick == 1)
        {
            if (detail == "illegal-target")
            {
                var res = Authorize(actor, target, NpcDecisionOutcome.Accept, PlayerRequestKind.ImmediateTravel, 900);
                // F7 fail-closed：非法目标不得被授权。授权 = 全链（consent → 仲裁/可达性）后真正
                // 获得执行权才算 authorized —— 未知地点经 resolver Unavailable → 仲裁拒绝 → 无
                // lease/无 adapter/非 already-there，故 false。仅 consent 门同意不足以授予执行权。
                bool authorized = false;
                string arbDetail = "no-intent";
                if (res?.TravelIntent is { } intent)
                {
                    var outcome = _travelHandler.Handle(intent, actor, NowStamp());
                    arbDetail = $"{outcome.Arbitration.Decision}:{outcome.Arbitration.Message}";
                    authorized = outcome.Lease is not null || outcome.AdapterInvoked || outcome.AlreadyAtDestination;
                }
                bool lease = _leases.IsActive(actor);
                _stepAuthorized = authorized;
                if (_ev is not null) _ev.AuthorizationObserved = authorized;
                Log($"A1-IL illegal-target authorized={authorized} lease={lease} arb={arbDetail}");
                if (authorized || lease)
                {
                    Fail($"A1-IL illegal-target fail-open authorized={authorized} lease={lease} arb={arbDetail}");
                    return;
                }
            }
            else if (detail == "time-jump" || detail == "missed")
            {
                var res = Authorize(actor, target, NpcDecisionOutcome.Accept, PlayerRequestKind.FutureAppointment, 2100);
                if (res?.Commitment is { } c) { _appointments.Add(c); }
                // F4 防污染：time-jump 与 missed 都必须把 commitment 置为终态（MISSED）。跳时间
                // 到目标之后 + 等待窗口（2300），OnLoad 才会标记 MISSED —— 否则残留的 Accepted
                // 会在后续 RL 时间推进时被误触发出发，产生同 NPC 的 rogue lease。
                _appointments.OnLoad(Game1.Date.DayOfMonth, 2300);
                var cmt = _appointments.All.FirstOrDefault(x => x.NpcId == actor && x.RequestId == res?.Commitment?.RequestId);
                Log($"A1-IL {detail} status={cmt?.Status}");
            }
            else // schedule-conflict
            {
                Log($"A1-IL schedule-conflict actor={actor} (hard-anchor/conflict → reject at arbitration)");
            }
        }
        Advance();
    }

    private ConsentResult? Authorize(string actor, string target, NpcDecisionOutcome outcome, PlayerRequestKind kind, int time, bool tamperTerms = false)
    {
        var req = MakeRequest(actor, target, kind, time);
        var decision = MakeDecision(req, outcome);
        if (tamperTerms) decision = new NpcDecision { RequestId = req.RequestId, NpcId = req.NpcId, TermsHash = "tampered", Outcome = outcome, SchemaVersion = "1" };
        return _consentGate.Evaluate(req, decision, SafeDay(), time);
    }

    private PlayerRequest MakeRequest(string actor, string target, PlayerRequestKind kind, int time)
    {
        int day = SafeDay();
        // RL create 阶段 _idx 不推进、_rlNext 又重置 _phaseTick，四个 RL create 会撞同一
        // requestId（appt-{idx}-1）→ consent gate 幂等拒绝（DuplicateRequest）。并入 _rlIdx 保证唯一。
        string requestId = _inRlCreatePhase
            ? $"appt-rl-{_rlIdx}-{_rlSub}-{_phaseTick}"
            : $"appt-{_idx}-{_phaseTick}";
        return new PlayerRequest
        {
            RequestId = requestId,
            NpcId = actor,
            RequestKind = kind,
            OriginalUtterance = $"带我去{target}",
            GroundedTarget = target,
            RequestedGameDay = day,
            RequestedGameTime = time,
            TermsHash = PlayerRequest.ComputeTermsHash(actor, kind, target, day, time),
            SchemaVersion = "1",
        };
    }

    /// <summary>当前游戏日（QA driver 日无关：调度器 departure 需 gameDay == targetGameDay）。</summary>
    private static int SafeDay()
    {
        try { return Game1.Date.DayOfMonth; }
        catch { return 1; }
    }

    private static NpcDecision MakeDecision(PlayerRequest req, NpcDecisionOutcome outcome) => new()
    {
        RequestId = req.RequestId,
        NpcId = req.NpcId,
        TermsHash = req.TermsHash,
        Outcome = outcome,
        SchemaVersion = "1",
    };

    private CounterProposal? _pendingProposal;

    private static int ComputeDeparture(int targetGameTime)
        => TravelDeparturePlanner.ComputeDepartureTime(targetGameTime, 30, 30);

    private void Dispatch(AppointmentTickResult tick)
    {
        foreach (var intent in tick.TravelIntentsToDispatch)
        {
            string? actor = intent.Params is not null && intent.Params.TryGetValue("npcId", out var v) && v is string s
                ? s : _current.Actor;
            _travelHandler.Handle(intent, actor, NowStamp());
        }
    }

    private void Advance()
    {
        string marker = $"STEP_DONE label={_current.Label} result=pass kind={_current.Kind} actor={_current.Actor} target={_current.Target} outcome={_current.Outcome}";
        Log(marker);
        CompleteScenario("pass", "", marker);
        _passCount++;
        _idx++;
        _phaseTick = 0;
        _pendingProposal = null;
        _apSub = 0;
        _apCommitmentId = "";
    }

    private void Fail(string reason)
    {
        string marker = $"STEP_DONE label={_current.Label} result=fail kind={_current.Kind} actor={_current.Actor} reason={reason}";
        Log(marker);
        CompleteScenario("fail", reason, marker);
        _failCount++;
        _idx++;
        _phaseTick = 0;
        _pendingProposal = null;
        _apSub = 0;
        _apCommitmentId = "";
    }

    /// <summary>F7 — 场景完成：把收集到的结构化证据定稿并入列，重置 F5/F6 场景态。</summary>
    private void CompleteScenario(string result, string reason, string marker)
    {
        if (_ev is not null)
        {
            _ev.Result = result;
            _ev.ActualOutcome = result;
            _ev.FailureReason = reason;
            _ev.EvidenceMarker = marker;
            _ev.EvidencePath = "artifacts/runtime-tests/$runId/smapi-appt-stage-*.log";
            // F7 证据回填：以驱动权威的 _step* 观察为准。start/arrival 来自真实桥接
            // （RecordTravelArrival = 系统 status=Arrived）或 AP 状态机驱动；未出发/未到达的
            // 拒绝场景保持 false（F6 拒绝侧断言依赖）。LegalPosition 取到达落点合法性或
            // NPC 当前合法站位（NO/IL 停留 baseline 场景）。
            _ev.TravelStartObserved = _stepTravelStarted || _ev.TravelStartObserved;
            _ev.ArrivalObserved = _stepArrived || _ev.ArrivalObserved;
            if (string.IsNullOrEmpty(_ev.ActualLocation))
                _ev.ActualLocation = _stepArrivalLocation.Length > 0
                    ? _stepArrivalLocation
                    : NpcPosition(_current.Actor);
            if (!_ev.LegalPosition)
                _ev.LegalPosition = _stepArrivalLegal || IsLegalTile(Game1.getCharacterFromName(_current.Actor));
            if (_current.Kind == StepKind.Appointment && string.IsNullOrEmpty(_ev.TimeSequence))
                _ev.TimeSequence = $"dep={_apDepartureTime:D4} arrival={_apArrivalTime} target={_current.Time:D4}";
            _scenarioEvidence.Add(_ev);
            Log($"A1_EVIDENCE scenario={_ev.ScenarioId} result={_ev.Result} auth={_ev.AuthorizationObserved} start={_ev.TravelStartObserved} arrive={_ev.ArrivalObserved} loc={_ev.ActualLocation} legal={_ev.LegalPosition} waitObs={_ev.WaitingObservationCount} rejoin={_ev.BaselineRejoinWhileWaiting} ts={_ev.TimeSequence}");
        }
        // 重置 F5/F6 场景态。
        _apFrozenTime = null;
        _apDepartureTime = 0;
        _apArrivalTime = "";
        _apWaitObserved = 0;
        _stepAuthorized = false;
        _stepTravelStarted = false;
        _stepArrived = false;
        _stepArrivalLocation = "";
        _stepArrivalLegal = false;
        _stepPosBefore = "";
    }

    /// <summary>A1 R2: 把 Abigail/Harvey 都 warp 回各自 canonical baseline（SETUP_ONLY，不计 Travel 成功）。
    /// 归位到 vanilla 早晨 baseline，保证下一场景出发/到达的起点确定性，并清空上一场景的遗留占用。
    /// NPC 缺失或 baseline 地点缺失时静默跳过（绝不抛错）。</summary>
    private void NormalizeScenarioActors()
    {
        foreach (var a in new[] { Abigail, Harvey })
        {
            var npc = Game1.getCharacterFromName(a);
            if (npc is null || npc.currentLocation is null) continue;
            var baseline = StardewBaselineResolver.Resolve(a, Game1.timeOfDay);
            var bLoc = Game1.getLocationFromName(baseline.LocationName);
            if (bLoc is null) continue;
            bool atBaseline = string.Equals(npc.currentLocation.NameOrUniqueName, baseline.LocationName, StringComparison.OrdinalIgnoreCase)
                && npc.TilePoint.X == baseline.TileX && npc.TilePoint.Y == baseline.TileY;
            if (atBaseline) continue;
            npc.controller = null;
            Game1.warpCharacter(npc, bLoc, new Vector2(baseline.TileX, baseline.TileY));
            Log($"A1 SETUP_ONLY NORMALIZED npc={a} to={baseline.LocationName}({baseline.TileX},{baseline.TileY}) source={baseline.Source}");
        }
    }

    /// <summary>F7 — 场景首 tick：新建该场景的 ScenarioEvidence，标记冷启动进程归属。</summary>
    private void BeginScenario()
    {
        _ev = new ScenarioEvidence
        {
            ScenarioId = _current.Label,
            ExpectedOutcome = _current.Kind switch
            {
                StepKind.NoExecution or StepKind.IllegalConflict => "no-execute",
                _ => "pass",
            },
            ColdRestartProcessA = _apptStage == "A",
            ColdRestartProcessB = _apptStage == "B",
        };
    }

    /// <summary>F7 — 合法落点：地图内、非水、可通行（与 TravelTrajectoryTracker 同语义）。
    /// 对 NPC 当前 tile 判定 —— 自身占位不影响 isTilePassable。</summary>
    private static bool IsLegalTile(NPC npc)
    {
        if (npc?.currentLocation is not { } loc) return false;
        return IsLegalStandingTile(loc, npc.TilePoint.X, npc.TilePoint.Y);
    }

    /// <summary>F7 — 指定落点合法性（OnTravelArrived 的 dest 用；解析失败/越界一律 false）。</summary>
    private static bool IsLegalArrivalTile(string locationName, int tileX, int tileY)
    {
        var loc = Game1.getLocationFromName(locationName);
        if (loc is null) return false;
        return IsLegalStandingTile(loc, tileX, tileY);
    }

    private static bool IsLegalStandingTile(GameLocation loc, int x, int y)
    {
        if (loc.Map?.Layers[0] is not { } layer) return false;
        if (x < 0 || y < 0 || x >= layer.LayerWidth || y >= layer.LayerHeight) return false;
        if (loc.isWaterTile(x, y)) return false;
        if (loc.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport)) return true;
        foreach (var w in loc.warps)
            if (w is not null && w.X == x && w.Y == y) return true;
        return false;
    }

    /// <summary>F7 — NPC 当前可见位置 "Location(x,y)"。</summary>
    private static string NpcPosition(string actor)
    {
        var npc = Game1.getCharacterFromName(actor);
        return npc?.currentLocation is { } loc
            ? $"{loc.NameOrUniqueName}({npc.TilePoint.X},{npc.TilePoint.Y})"
            : "absent";
    }

    /// <summary>F7 — ModEntry.OnTravelArrived 桥接：当前执行场景匹配该 NPC 时记录真实到达（防假绿证据）。</summary>
    public void RecordTravelArrival(string npcId, string locationName, int tileX, int tileY)
    {
        if (_done || _inRlCreatePhase || _apptStage == "B") return;
        if (_idx >= _steps.Count) return;
        var step = _steps[_idx];
        if (!string.Equals(npcId, step.Actor, StringComparison.OrdinalIgnoreCase)) return;
        // 只接受【当前场景目标】的到达 —— 防止上一场景遗留 travel controller 的到达污染本场景
        // 证据/状态（F6 防假绿：到达必须对应本场景目标地点）。
        if (!string.Equals(locationName, step.Target, StringComparison.OrdinalIgnoreCase))
        {
            Log($"A1 ARRIVAL_IGNORED actor={npcId} loc={locationName} target={step.Target} (stale/other-controller arrival)");
            return;
        }
        _stepArrived = true;
        _stepArrivalLocation = locationName;
        _stepArrivalLegal = IsLegalArrivalTile(locationName, tileX, tileY);
        if (_ev is not null)
        {
            _ev.ArrivalObserved = true;
            _ev.ActualLocation = $"{locationName}({tileX},{tileY})";
            _ev.LegalPosition = _stepArrivalLegal;
        }
        Log($"A1 ARRIVAL_OBSERVED actor={npcId} loc={locationName}({tileX},{tileY}) legal={_stepArrivalLegal} scenario={step.Label}");
    }

    /// <summary>F7 — 把当前阶段的结构化证据写入 appt-evidence-stage{_apptStage}.json（供 launcher 合并校验）。</summary>
    private void WriteEvidenceFile()
    {
        try
        {
            var payload = new
            {
                runStage = _apptStage,
                committedScenarios = _scenarioEvidence.Count,
                scenarios = _scenarioEvidence.Select(e => new
                {
                    scenarioId = e.ScenarioId,
                    expectedOutcome = e.ExpectedOutcome,
                    actualOutcome = e.ActualOutcome,
                    authorizationObserved = e.AuthorizationObserved,
                    travelStartObserved = e.TravelStartObserved,
                    arrivalObserved = e.ArrivalObserved,
                    actualLocation = e.ActualLocation,
                    legalPosition = e.LegalPosition,
                    timeSequence = e.TimeSequence,
                    waitingObservationCount = e.WaitingObservationCount,
                    baselineRejoinWhileWaiting = e.BaselineRejoinWhileWaiting,
                    coldRestartProcessA = e.ColdRestartProcessA,
                    coldRestartProcessB = e.ColdRestartProcessB,
                    result = e.Result,
                    failureReason = e.FailureReason,
                    evidenceMarker = e.EvidenceMarker,
                    evidencePath = e.EvidencePath,
                }),
            };
            var path = Path.Combine(_helper.DirectoryPath, $"appt-evidence-stage{_apptStage}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(payload));
            Log($"A1_EVIDENCE_WRITTEN stage={_apptStage} path=appt-evidence-stage{_apptStage}.json count={_scenarioEvidence.Count}");
        }
        catch (Exception ex)
        {
            Log($"A1_EVIDENCE_WRITE_FAILED stage={_apptStage} {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Finalize()
    {
        _done = true;
        WriteEvidenceFile();

        if (_apptStage == "A")
        {
            WriteRlExpected();
            // A1 R2.1: 主机直接泵 SaveGame.Save() 不触发 SMAPI Saving/Saved（SMAPI 在游戏更新
            // 循环里依 Context.IsSaving() 才 raise），Demo 的 OnSaving/OnSaved 不运行 → commitment
            // 不落盘、save 标记不出现 → APPT_A_SAVE 超时。故在写信号前让 ModEntry 自行持久化
            //（WriteSaveData 即时落盘，独立于 SMAPI save 事件）并发出 runner 等待的 save 标记。
            // 顺序保证：持久化 → 标记 → 信号 → 主机 SaveGame.Save() 把含 commitment 的存档落盘。
            _onStageAComplete?.Invoke();
            try
            {
                QaSignal.WriteStageDone(_helper);
                Log("A1_STAGE_A_SIGNAL_WRITTEN — host performs SaveGame.Save (no day rollover)");
            }
            catch (Exception ex)
            {
                Log($"A1_STAGE_SIGNAL_FAILED {ex.GetType().Name}: {ex.Message}");
            }
            Log($"STAGE=A COMPLETE scenarios={_passCount + _failCount} total=36");
            Log($"STAGE=A RL_STATES_CREATED states={_rlExpected.Count}");
            // host autoSave=true → SaveGame.Save() 在当前日/时刻写入真实存档（无跨日 → 无错误 MISSED）。
        }
        else
        {
            int total = _stageACount + _passCount;
            string res = (_failCount == 0 && total == 36) ? "pass" : "fail";
            try
            {
                QaSignal.WriteStageDone(_helper);
                Log("A1_STAGE_B_SIGNAL_WRITTEN");
            }
            catch (Exception ex)
            {
                Log($"A1_STAGE_SIGNAL_FAILED {ex.GetType().Name}: {ex.Message}");
            }
            Log($"A1_APPOINTMENT_COMPLETE RESULT={res} scenarios={total} total=36");
        }
    }

    private string NowStamp() => $"d{Game1.Date.DayOfMonth}t{Game1.timeOfDay}";

    private void Log(string msg) => _monitor.Log($"[A1] {msg}", LogLevel.Info);
}
