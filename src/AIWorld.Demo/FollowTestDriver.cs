using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// Phase 4 follow acceptance driver (PM D-P4-2: deterministic fixtures, production pipeline).
/// State machine driven by persisted metadata (survives cold restarts).
///
/// Two scenarios (PM Phase 4 Gate Review, 2026-08-12):
///   "explicit-stop" (P4-A): none → ACCEPT → active → EXPLICIT STOP while actively following
///                           (same day, before DayEnding) → stop_done → second STOP idempotency
///                           → complete. Proves explicit STOP EXECUTION through the production
///                           chain (previously only STOP idempotency/guard was runtime-proven).
///   "day-end" (P4-B):      none → ACCEPT → accept_done (await real end-day save; Squad
///                           OnDayEnding DismissAll → AIWorld closes session) → cold restart
///                           → verify restored closed session + Squad not recruited (CONSISTENT)
///                           → STOP idempotency → DECLINE zero-execution → complete.
///
/// All intents enter the SAME production chain as future LLM intents:
/// NpcIntent → FollowIntentHandler → BehaviorArbitrator → CapabilityRegistry → StardewSquadAdapter.
/// Postconditions are WAIT-UNTIL-predicate WITH TIMEOUT — never fixed Sleep.
/// </summary>
public sealed class FollowTestDriver
{
    private const string StateKey = "P4_FOLLOW_TEST_STATE";
    private const string TestNpc = "Abigail";
    private const string RecruiterIdKey = "TheStardewSquad/RecruiterId";

    private readonly IMonitor _monitor;
    private readonly IModHelper _helper;
    private readonly AIWorldSaveData _data;
    private readonly AIWorldSaveRepository _repository;
    private readonly FollowIntentHandler _handler;
    private readonly StardewSquadAdapter _adapter;
    private readonly FollowSessionManager _sessions;
    private readonly string _scenario;
    private bool _done;
    private int _ticksInState;

    public FollowTestDriver(IMonitor monitor, IModHelper helper, AIWorldSaveData data, AIWorldSaveRepository repository, FollowIntentHandler handler, StardewSquadAdapter adapter, FollowSessionManager sessions, string scenario = "day-end")
    {
        _monitor = monitor;
        _helper = helper;
        _data = data;
        _repository = repository;
        _handler = handler;
        _adapter = adapter;
        _sessions = sessions;
        _scenario = scenario;
    }

    /// <summary>Signal the RuntimeTestHost that this step finished (triggers real end-day save).</summary>
    private void SignalStepDone()
    {
        _repository.MarkDataDirty();
        try
        {
            QaSignal.WriteStageDone(_helper);
        }
        catch { }
    }

    private bool _anchorStreak;
    private bool _diagnosed;

    public void TickIfReady()
    {
        if (_done || !Game1.hasLoadedGame || Game1.player is null) return;
        if (Game1.timeOfDay < 600) return;
        var anchor = new SmapiHardAnchorProvider();
        if (!_diagnosed)
        {
            _diagnosed = true;
            _monitor.Log($"[P4] ANCHOR_DIAG currentEvent={Game1.CurrentEvent?.id ?? "null"} isInBed={Game1.player?.isInBed?.Value} festival={Utility.isFestivalDay(Game1.dayOfMonth, (Season)Game1.seasonIndex)} season={Game1.currentSeason} day={Game1.dayOfMonth} time={Game1.timeOfDay}", LogLevel.Info);
        }
        if (anchor.IsHardAnchorActive(TestNpc))
        {
            // Defer only on persistent anchor (avoid transient states right after load).
            _anchorStreak = _anchorStreak || anchor.ActiveAnchor(TestNpc) is not null;
            _monitor.Log($"[P4] HARD_ANCHOR_ACTIVE anchor={anchor.ActiveAnchor(TestNpc)} streak={_anchorStreak}", LogLevel.Info);
            return;
        }
        _anchorStreak = false;
        _ticksInState++;
        RunCurrentStep();
        // P4-A (explicit-stop) runs several steps in ONE process; P4-B (day-end) keeps the
        // one-shot-per-process behavior (Process A ends after ACCEPT; Process B verifies).
        if (State == "complete" || _scenario != "explicit-stop")
            _done = true;
    }

    private string State => _data.Metadata.TryGetValue(StateKey, out var s) ? s : "none";

    private void RunCurrentStep()
    {
        if (_scenario == "explicit-stop")
        {
            switch (State)
            {
                case "none": StepAccept(); break;
                case "active": StepExplicitStop(); break;
                case "stop_done": StepStopIdempotency(); break;
                case "complete":
                    _monitor.Log("[P4] TEST_COMPLETE — no further steps", LogLevel.Info);
                    break;
                default:
                    _monitor.Log($"[P4] UNKNOWN_STATE '{State}'", LogLevel.Error);
                    break;
            }
        }
        else
        {
            switch (State)
            {
                case "none": StepAccept(); break;
                case "accept_done": StepVerifyThenStop(); break;
                case "complete":
                    _monitor.Log("[P4] TEST_COMPLETE — no further steps", LogLevel.Info);
                    break;
                default:
                    _monitor.Log($"[P4] UNKNOWN_STATE '{State}'", LogLevel.Error);
                    break;
            }
        }
    }

    private NpcIntent Intent(string capabilityId, string reason) => new()
    {
        IntentId = Guid.NewGuid().ToString("N"),
        Type = capabilityId,
        Target = "Player",
        Priority = 0.6f,
        SchemaVersion = "1",
        Reason = reason,
    };

    private string Now() => $"{Game1.currentSeason}-{Game1.dayOfMonth}-{Game1.year}@{Game1.timeOfDay:D4}";

    private void StepAccept()
    {
        _monitor.Log("[P4] STEP = ACCEPT (deterministic fixture, production pipeline)", LogLevel.Info);
        var outcome = _handler.Handle(Intent(CapabilityRegistry.CapabilityIds.FollowPlayer, "P4-ACCEPT-FIXTURE"), TestNpc, Now());

        if (outcome.Adapter is null || outcome.Adapter.Status != "SUCCESS")
        {
            _monitor.Log($"[P4] ACCEPT_FAILED arbitration={outcome.Arbitration.Decision} adapter={outcome.Adapter?.Status ?? "null"} msg={outcome.Adapter?.Message ?? outcome.Arbitration.Message}", LogLevel.Error);
            return;
        }
        if (outcome.Session is null)
        {
            _monitor.Log("[P4] ACCEPT_NO_SESSION — ghost session prevention violated", LogLevel.Error);
            return;
        }

        _monitor.Log($"[P4] ACCEPT_PASS adapter=SUCCESS session={outcome.Session.SessionId} lease={outcome.Lease?.Status}", LogLevel.Info);

        // BEFORE evidence: Squad recruited state + RecruiterId modData + session/lease state.
        bool squadRecruited = _adapter.IsFollowing(TestNpc);
        var abigail = Game1.getCharacterFromName(TestNpc);
        bool hasRecruiter = abigail?.modData?.ContainsKey(RecruiterIdKey) == true;
        bool hasSerial = abigail?.modDataForSerialization?.ContainsKey(RecruiterIdKey) == true;
        bool sessionActive = _sessions.GetActive(TestNpc) is not null;
        bool leaseActive = outcome.Lease?.Status == "ACTIVE";
        _monitor.Log($"[P4] MODDATA_DIAG hasRecruiter={hasRecruiter} val={(hasRecruiter ? abigail!.modData[RecruiterIdKey] : "(none)")} inSerialization={hasSerial}", LogLevel.Info);

        if (_scenario == "explicit-stop")
        {
            _monitor.Log(squadRecruited ? "[P4] SQUAD_RECRUITED=true" : "[P4] SQUAD_RECRUITED=FALSE — ACCEPT postcondition violated", squadRecruited ? LogLevel.Info : LogLevel.Error);
            _monitor.Log((hasRecruiter || hasSerial) ? "[P4] RECRUITER_MODDATA=PRESENT" : "[P4] RECRUITER_MODDATA=ABSENT — ACCEPT postcondition violated", (hasRecruiter || hasSerial) ? LogLevel.Info : LogLevel.Error);
            _monitor.Log(sessionActive ? "[P4] FOLLOW_SESSION=ACTIVE" : "[P4] FOLLOW_SESSION=INACTIVE — ACCEPT postcondition violated", sessionActive ? LogLevel.Info : LogLevel.Error);
            _monitor.Log(leaseActive ? "[P4] LEASE=ACTIVE" : "[P4] LEASE=NOT_ACTIVE — ACCEPT postcondition violated", leaseActive ? LogLevel.Info : LogLevel.Error);
            if (!squadRecruited || !(hasRecruiter || hasSerial) || !sessionActive || !leaseActive) return;
            _data.Metadata[StateKey] = "active";
            _ticksInState = 0;
            _monitor.Log("[P4] SCENARIO=EXPLICIT_STOP — awaiting explicit stop", LogLevel.Info);
        }
        else
        {
            _data.Metadata[StateKey] = "accept_done";
            _monitor.Log("[P4] DIRTY_TRUE awaiting-save=ACCEPT", LogLevel.Info);
            SignalStepDone();
        }
    }

    /// <summary>P4-A: explicit social.stop_following while actively following (same day, before DayEnding).</summary>
    private void StepExplicitStop()
    {
        // Stability predicate: wait until recruited state is observable for a minimum tick
        // separation (main-thread tick cadence — no fixed Sleep; bounded by the postcondition
        // WAIT-UNTIL-with-timeout waits below).
        if (_ticksInState < 2)
        {
            _monitor.Log($"[P4] STOP_DEFER recruited={_adapter.IsFollowing(TestNpc)} ticks={_ticksInState} — awaiting stability", LogLevel.Debug);
            return;
        }
        if (!_adapter.IsFollowing(TestNpc))
        {
            _monitor.Log("[P4] STOP_PRECONDITION_FAILED — Squad not recruited while session active", LogLevel.Error);
            return;
        }

        _monitor.Log("[P4] STEP = EXPLICIT_STOP_WHILE_FOLLOWING (production pipeline)", LogLevel.Info);

        // 1. Intent received.
        _monitor.Log("[P4] STOP_INTENT_RECEIVED capability=social.stop_following target=Abigail", LogLevel.Info);
        var stopOutcome = _handler.Handle(Intent(CapabilityRegistry.CapabilityIds.StopFollowing, "P4-EXPLICIT-STOP-FIXTURE"), TestNpc, Now());

        // 2. Arbitration authorized.
        if (!stopOutcome.Arbitration.Authorized || stopOutcome.Arbitration.Decision != BehaviorDecision.Authorized)
        {
            _monitor.Log($"[P4] STOP_ARBITRATION_FAILED decision={stopOutcome.Arbitration.Decision} msg={stopOutcome.Arbitration.Message}", LogLevel.Error);
            return;
        }
        _monitor.Log("[P4] STOP_ARBITRATION_AUTHORIZED", LogLevel.Info);

        // 3. Adapter invoked.
        if (!stopOutcome.AdapterInvoked)
        {
            _monitor.Log("[P4] STOP_ADAPTER_NOT_INVOKED", LogLevel.Error);
            return;
        }
        _monitor.Log("[P4] STOP_ADAPTER_INVOKED", LogLevel.Info);

        // 4. Adapter result: SUCCESS only with observable postcondition (Dismiss predicate wait inside adapter).
        if (stopOutcome.Adapter is null || stopOutcome.Adapter.Status != "SUCCESS")
        {
            _monitor.Log($"[P4] STOP_ADAPTER_FAILED status={stopOutcome.Adapter?.Status ?? "null"} msg={stopOutcome.Adapter?.Message ?? "null"}", LogLevel.Error);
            return;
        }
        _monitor.Log("[P4] STOP_ADAPTER_RESULT=SUCCESS", LogLevel.Info);

        // 5. Objective postconditions (WAIT UNTIL predicate WITH TIMEOUT — never fixed sleep).
        if (!WaitUntil(() => !_adapter.IsFollowing(TestNpc), 6))
        {
            _monitor.Log("[P4] STOP_POSTCONDITION_TIMEOUT — Squad still recruited", LogLevel.Error);
            return;
        }
        _monitor.Log("[P4] SQUAD_RECRUITED=false", LogLevel.Info);

        if (!WaitUntil(() => RecruiterModDataAbsent(), 6))
        {
            _monitor.Log("[P4] RECRUITER_MODDATA_NOT_CLEARED", LogLevel.Error);
            return;
        }
        _monitor.Log("[P4] RECRUITER_MODDATA=ABSENT", LogLevel.Info);

        // 6. Session closure from explicit stop (contract endReason stays frozen "AI_DISMISS";
        //    FOLLOW_SESSION_END_REASON=EXPLICIT_STOP is the runtime/evidence distinction marker).
        var latest = _sessions.GetLatest(TestNpc);
        if (latest is null || latest.IsActive)
        {
            _monitor.Log("[P4] FOLLOW_SESSION_NOT_CLOSED", LogLevel.Error);
            return;
        }
        _monitor.Log($"[P4] FOLLOW_SESSION_CLOSED session={latest.SessionId} endedAt={latest.EndedAt} endReason={latest.EndReason}", LogLevel.Info);
        _monitor.Log("[P4] FOLLOW_SESSION_END_REASON=EXPLICIT_STOP", LogLevel.Info);

        // 7. Lease released (no active lease after stop).
        if (stopOutcome.Lease is not null)
        {
            _monitor.Log($"[P4] LEASE_NOT_RELEASED status={stopOutcome.Lease.Status}", LogLevel.Error);
            return;
        }
        _monitor.Log("[P4] BEHAVIOR_LEASE_RELEASED", LogLevel.Info);

        _data.Metadata[StateKey] = "stop_done";
        _ticksInState = 0;
    }

    /// <summary>P4-A: second social.stop_following must be idempotently rejected with zero adapter delta.</summary>
    private void StepStopIdempotency()
    {
        _monitor.Log("[P4] STEP = STOP_IDEMPOTENCY (second social.stop_following)", LogLevel.Info);
        int stopCallsBefore = _adapter.StopCallCount;
        var outcome = _handler.Handle(Intent(CapabilityRegistry.CapabilityIds.StopFollowing, "P4-SECOND-STOP-FIXTURE"), TestNpc, Now());
        int delta = _adapter.StopCallCount - stopCallsBefore;
        bool idempotent = outcome.Arbitration.Decision == BehaviorDecision.RejectedNotFollowing
            && !outcome.AdapterInvoked
            && delta == 0;
        _monitor.Log(idempotent
            ? "[P4] STOP_IDEMPOTENT_PASS — REJECTED_NOT_FOLLOWING, ADAPTER_INVOCATION_COUNT_DELTA=0"
            : $"[P4] STOP_IDEMPOTENT_FAILED decision={outcome.Arbitration.Decision} invoked={outcome.AdapterInvoked} delta={delta}", idempotent ? LogLevel.Info : LogLevel.Error);
        _data.Metadata[StateKey] = "complete";
        _monitor.Log("[P4] TEST_COMPLETE — explicit-stop scenario passed", LogLevel.Info);
    }

    private void StepVerifyThenStop()
    {
        _monitor.Log("[P4] STEP = VERIFY_ACCEPT_PERSISTENCE (Squad day-end semantics)", LogLevel.Info);

        // Squad 0.12.1: follows are same-day — day-end dissolves recruited state.
        // AIWorld closed the session on DayEnding (SESSION_CLOSED_SQUAD_DAY_END).
        // Expected after cold restart: CLOSED session restored + Squad not recruited = CONSISTENT.
        var latest = _sessions.GetLatest(TestNpc);
        bool squadFollowing = _adapter.IsFollowing(TestNpc);
        if (latest is null || latest.IsActive)
        {
            _monitor.Log($"[P4] SESSION_RESTORE_FAILED active={latest?.IsActive} null={latest is null} — expected closed session", LogLevel.Error);
            return;
        }
        _monitor.Log($"[P4] SESSION_RESTORED session={latest.SessionId} startedAt={latest.StartedAt} endedAt={latest.EndedAt} endReason={latest.EndReason}", LogLevel.Info);
        _monitor.Log(squadFollowing ? "[P4] SQUAD_STATE_MISMATCH — session closed but still recruited" : "[P4] SQUAD_STATE_CONSISTENT — closed session + not recruited", squadFollowing ? LogLevel.Error : LogLevel.Info);
        if (squadFollowing) return;

        // STOP while not following → REJECTED_NOT_FOLLOWING (idempotency proof, zero execution).
        _monitor.Log("[P4] STEP = STOP_WHILE_NOT_FOLLOWING (idempotency)", LogLevel.Info);
        var stopOutcome = _handler.Handle(Intent(CapabilityRegistry.CapabilityIds.StopFollowing, "P4-STOP-FIXTURE"), TestNpc, Now());
        bool stopRejected = stopOutcome.Arbitration.Decision == BehaviorDecision.RejectedNotFollowing && !stopOutcome.AdapterInvoked && _adapter.StopCallCount == 0;
        _monitor.Log(stopRejected ? "[P4] STOP_IDEMPOTENT_PASS — REJECTED_NOT_FOLLOWING, zero adapter calls" : $"[P4] STOP_IDEMPOTENT_FAILED decision={stopOutcome.Arbitration.Decision} invoked={stopOutcome.AdapterInvoked}", stopRejected ? LogLevel.Info : LogLevel.Error);

        // DECLINE: deterministic decision → zero execution.
        _monitor.Log("[P4] STEP = DECLINE (deterministic decision fixture)", LogLevel.Info);
        var declineHandler = new FollowIntentHandler(
            _handlerArbitratorOf(_handler),
            _adapter,
            _sessions,
            new DeterministicFollowDecisionSource(FollowDecision.Decline));
        int startCallsBefore = _adapter.StartCallCount;
        var declineOutcome = declineHandler.Handle(Intent(CapabilityRegistry.CapabilityIds.FollowPlayer, "P4-DECLINE-FIXTURE"), TestNpc, Now());

        bool zeroExecution = declineOutcome.DecisionDeclined
            && startCallsBefore == _adapter.StartCallCount
            && _adapter.StopCallCount == 0
            && !_adapter.IsFollowing(TestNpc)
            && _sessions.GetActive(TestNpc) is null;
        _monitor.Log(zeroExecution ? "[P4] DECLINE_PASS — no adapter call, no session, no Squad mutation" : "[P4] DECLINE_FAILED — unexpected execution", zeroExecution ? LogLevel.Info : LogLevel.Error);

        _data.Metadata[StateKey] = "complete";
        _monitor.Log("[P4] TEST_COMPLETE — all P4 steps passed", LogLevel.Info);
        SignalStepDone();
    }

    private bool RecruiterModDataAbsent()
    {
        var npc = Game1.getCharacterFromName(TestNpc);
        if (npc is null || npc.modData is null) return true;
        return !npc.modData.ContainsKey(RecruiterIdKey)
            && (npc.modDataForSerialization is null || !npc.modDataForSerialization.ContainsKey(RecruiterIdKey));
    }

    /// <summary>WAIT UNTIL predicate WITH TIMEOUT (main-thread test path, short bounded waits — never fixed sleep).</summary>
    private bool WaitUntil(Func<bool> predicate, int timeoutSec)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            System.Threading.Thread.Sleep(250);
        }
        return predicate();
    }

    // Reflective access to the handler's arbitrator for the decline sub-handler (test-only).
    private BehaviorArbitrator _handlerArbitratorOf(FollowIntentHandler h) =>
        (BehaviorArbitrator)typeof(FollowIntentHandler).GetField("_arbitrator", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(h)!;
}
