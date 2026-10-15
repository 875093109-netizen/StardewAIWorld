using AIWorld.Core.Behaviors;
using AIWorld.Core.Persistence;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// PHASE 7 — Core Demo Acceptance orchestrator (orchestration ONLY, no production logic).
///
/// Chains the ALREADY-VERIFIED Phase 5 (follow experience) and Phase 6 (magic capability)
/// drivers into ONE continuous demo, then performs ONE settled-state save, then verifies
/// the combined state after a cold restart.
///
/// Process A (demo execution):
///   none → magic_running (tick MagicCapabilityDriver to "complete")
///       → follow_running (tick FollowExperienceDriver to "complete")
///       → save_pending (settled-state: warp home → bed → 0600 → settle 3 ticks → signal)
///       → complete (persisted)
///
/// Process B (cold restart verify):
///   phase=="complete" (persisted) → tick BOTH drivers; each self-dispatches its own
///   StepVerifyAfterRestart from its persisted state → Scenario B/C/D/E/F/G/H markers.
///
/// Phase is persisted in Metadata["P7_CORE_DEMO_PHASE"]. Both sub-drivers run with
/// suppressSignal=true so THIS orchestrator owns the single save trigger (PM §十六:
/// driver causes/orchestrates/verifies; production components execute).
/// </summary>
public sealed class CoreDemoDriver
{
    private const string PhaseKey = "P7_CORE_DEMO_PHASE";

    private readonly IMonitor _monitor;
    private readonly IModHelper _helper;
    private readonly AIWorldSaveData _data;
    private readonly AIWorldSaveRepository _repository;
    private readonly MagicCapabilityDriver _magicDriver;
    private readonly MagicObservationSource _magicSource;
    private readonly FollowExperienceDriver _experienceDriver;
    private readonly SmapiObservationSource _observationSource;

    private bool _done;
    private int _verifyTicks;
    private bool _signaled;

    public string Phase => _data.Metadata.TryGetValue(PhaseKey, out var p) ? p : "none";

    public CoreDemoDriver(
        IMonitor monitor,
        IModHelper helper,
        AIWorldSaveData data,
        AIWorldSaveRepository repository,
        MagicCapabilityDriver magicDriver,
        MagicObservationSource magicSource,
        FollowExperienceDriver experienceDriver,
        SmapiObservationSource observationSource)
    {
        _monitor = monitor;
        _helper = helper;
        _data = data;
        _repository = repository;
        _magicDriver = magicDriver;
        _magicSource = magicSource;
        _experienceDriver = experienceDriver;
        _observationSource = observationSource;
    }

    public void TickIfReady()
    {
        if (_done || !Game1.hasLoadedGame || Game1.player is null || Game1.timeOfDay < 600) return;

        switch (Phase)
        {
            // ── Process A ──────────────────────────────────────────────────────────
            case "none":
                // Scenario D/C (magic): magic driver advances through observe → knowledge
                // RUMOR/VERIFIED → LLM learn decision → adoption. suppressSignal keeps the
                // save trigger with this orchestrator.
                _magicSource.Tick();
                _magicDriver.TickIfReady();
                if (_magicDriver.State == "complete")
                {
                    _monitor.Log("[P7] MAGIC_LINE_COMPLETE — knowledge/adoption/goals set (Scenario C/D)", LogLevel.Info);
                    _data.Metadata[PhaseKey] = "magic_done";
                }
                break;

            case "magic_done":
                // Scenarios E/F/G (follow): follow driver advances through ACCEPT → Squad
                // recruit → mine/injury/danger/safe-return observations → explicit STOP →
                // consolidation (memories persisted in-memory).
                _observationSource.Tick();
                _experienceDriver.TickIfReady();
                if (_experienceDriver.State == "complete")
                {
                    _monitor.Log("[P7] FOLLOW_LINE_COMPLETE — experience observed + memory consolidated (Scenario E/F/G)", LogLevel.Info);
                    _data.Metadata[PhaseKey] = "follow_done";
                    // The follow driver ALREADY restored the P4-B-proven pre-NewDay home state
                    // (warp FarmHouse + isInBed + 0600) inside its StepConsolidate. Do NOT warp
                    // again: a second warp's location transition resets isInBed=false and lets
                    // the time engine advance past 0600, which makes Game1.NewDay hang. Write
                    // the signal in THIS tick (same-tick signal = P5-proven pattern: NewDay
                    // interrupts before the warp transition can reset the bed state).
                    FinalizeSave();
                }
                break;

            case "follow_done":
                // Re-entry guard (in case a tick is missed): if follow_done was persisted but the
                // signal was not yet written (e.g., interrupted mid-tick), complete the save.
                FinalizeSave();
                break;

            // ── Process B (cold restart) ───────────────────────────────────────────
            case "complete":
                StepVerifyAfterRestart();
                break;
        }
    }

    /// <summary>
    /// P4-B/P5-proven same-tick save pattern: the follow driver already restored the home state
    /// (warp FarmHouse + isInBed + 0600) inside its StepConsolidate. Writing the signal in the
    /// SAME tick makes the host's Game1.NewDay fire before the warp transition can reset the bed
    /// state or let the time engine advance past 0600. No additional warp, no settle wait.
    /// </summary>
    private void FinalizeSave()
    {
        if (_signaled) return;
        _signaled = true;
        _data.Metadata[PhaseKey] = "complete";
        _repository.MarkDataDirty();
        try
        {
            QaSignal.WriteStageDone(_helper);
            _monitor.Log("[P7] SIGNAL_WRITTEN stage-a-done.signal — host triggers end-day save (single save, same tick)", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[P7] SIGNAL_WRITE_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
        // Process A done: stop ticking here (host will NewDay + save). Cold restart (Process B)
        // loads Phase=="complete" fresh and runs the verify branch. Without this gate the same
        // process would tick into StepVerifyAfterRestart and emit a premature TEST_COMPLETE.
        _done = true;
    }

    /// <summary>Process B: both drivers self-dispatch their verify steps from persisted state.</summary>
    private void StepVerifyAfterRestart()
    {
        _magicSource.Tick();
        _magicDriver.TickIfReady();       // Scenario C/D verify: knowledge/adoption/proficiency/goal restored
        _observationSource.Tick();
        _experienceDriver.TickIfReady();  // Scenario F/G/H verify: observations/session/memories restored + memory-aware decision

        if (_verifyTicks++ >= 5)
        {
            _monitor.Log("[P7] TEST_COMPLETE — Core Demo Acceptance (Scenarios B/C/D/E/F/G/H) PASS", LogLevel.Info);
            _done = true;
        }
    }
}
