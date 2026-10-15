using System.Text.Json;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// Phase 6 — Magic Capability runtime driver (A5 test-host + A4 state).
///
/// State machine (contract §2.3 four-way separation, PM §十四/§十五 frozen boundaries):
///   none              → CAUSE Wizardry analyze-tagged object in current location (TestHost),
///                        production MagicObservationSource observes → EVENT_TRIGGERED observation
///   magic_observed    → knowledge magic.exists UNKNOWN→RUMOR (sourceObservationId linked)
///   knowledge_rumor   → CAUSE second analyze object → RUMOR→VERIFIED; set goals.investigate_magic
///   knowledge_verified→ learn decision (deterministic fixture: ADOPT) → adoption + proficiency
///   adopted           → mark dirty, signal save, done
///   complete (cold)   → verify knowledge/adoption/proficiency/goal restored
///
/// NO Spell.OnCast, NO Farmer proxy, NO Wizardry mutation, NO NPC mana (frozen). The driver
/// only manages AIWorld-owned knowledge/adoption/proficiency/goal state.
/// </summary>
public sealed class MagicCapabilityDriver
{
    public const string StateKey = "P6_MAGIC_TEST_STATE";
    public const string TestNpc = "Abigail";

    private readonly IMonitor _monitor;
    private readonly IModHelper _helper;
    private readonly AIWorldSaveData _data;
    private readonly AIWorldSaveRepository _repository;
    private readonly MagicAdoptionService _magic;
    private readonly ObservationCollector _observations;
    private readonly MagicObservationSource _source;

    private bool _done;
    private int _ticksInState;
    private int _causeCount;
    private readonly bool _llmMode;
    private readonly bool _suppressSignal; // Phase 7: CoreDemoDriver owns the single save trigger

    public MagicCapabilityDriver(
        IMonitor monitor, IModHelper helper, AIWorldSaveData data, AIWorldSaveRepository repository,
        MagicAdoptionService magic, ObservationCollector observations, MagicObservationSource source,
        bool llmMode = false, bool suppressSignal = false)
    {
        _monitor = monitor;
        _helper = helper;
        _data = data;
        _repository = repository;
        _magic = magic;
        _observations = observations;
        _source = source;
        _llmMode = llmMode;
        _suppressSignal = suppressSignal;
    }

    public string State => _data.Metadata.TryGetValue(StateKey, out var s) ? s : "none";

    public void TickIfReady()
    {
        if (_done) return;
        if (Game1.player is null || !Game1.hasLoadedGame) return;
        if (Game1.timeOfDay < 600) return;

        _ticksInState++;
        RunCurrentStep();
    }

    private void RunCurrentStep()
    {
        switch (State)
        {
            case "none": StepCauseObservation(); break;
            case "magic_observed": StepKnowledgeRumor(); break;
            case "knowledge_rumor": StepKnowledgeVerified(); break;
            case "knowledge_verified":
                if (_llmMode) StepRealLlmLearnDecision();
                else StepLearnDecision();
                break;
            case "adopted": StepSignalSave(); break;
            case "declined": StepSignalSave(); break;
            case "complete": StepVerifyAfterRestart(); break;
            default:
                _monitor.Log($"[P6] UNKNOWN_STATE '{State}'", LogLevel.Error);
                break;
        }
    }

    private void SetState(string s)
    {
        _data.Metadata[StateKey] = s;
        _ticksInState = 0;
        _settleTicks = 0;
    }

    // ── TestHost causes / Production observes ──────────────────────────────────

    private void StepCauseObservation()
    {
        if (_ticksInState < 2) return; // stability gate
        _monitor.Log("[P6] STEP = CAUSE_MAGIC_OBSERVATION (test-host semantic action)", LogLevel.Info);
        try
        {
            // Place an object with Wizardry's analyze context tag in the current location.
            // Production MagicObservationSource detects it from real world state (no cheating).
            var location = Game1.currentLocation;
            if (location is null) { _monitor.Log("[P6] NO_CURRENT_LOCATION", LogLevel.Error); return; }

            var tile = new Microsoft.Xna.Framework.Vector2(20 + _causeCount * 2, 20);
            var obj = new StardewValley.Object(tile, "96"); // generic item id; modData carries the analyze tag
            obj.modData["moonSlime.Wizardry.Analyze"] = "true";
            obj.modData[MagicObservationSource.AnalyzeContextTag] = "true";
            location.objects[tile] = obj;
            _causeCount++;
            _monitor.Log($"[P6] CAUSED analyze-tagged object placed ({_causeCount})", LogLevel.Info);

            SetState("magic_observed");
        }
        catch (Exception ex)
        {
            _monitor.Log($"[P6] CAUSE_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
    }

    private void StepKnowledgeRumor()
    {
        _monitor.Log("[P6] STEP = KNOWLEDGE_TRANSITION (UNKNOWN→RUMOR)", LogLevel.Info);
        // Find the observation the production source recorded (may lag one tick behind placement).
        var obs = _observations.GetForNpc(TestNpc).FirstOrDefault(o => o.Type == ObservationTypes.EventTriggered);
        if (obs is null)
        {
            _monitor.Log("[P6] AWAITING_FIRST_OBSERVATION — production source has not recorded yet", LogLevel.Info);
            return;
        }
        var sourceId = obs.ObservationId;

        var next = _magic.ObserveMagic(TestNpc, Now(), sourceId);
        if (next is null)
        {
            _monitor.Log("[P6] KNOWLEDGE_TRANSITION_FAILED", LogLevel.Error);
            return;
        }
        _monitor.Log($"[P6] KNOWLEDGE_TRANSITION_PASS magic.exists={next} sourceObservationId={sourceId}", LogLevel.Info);
        _repository.MarkDataDirty();
        SetState("knowledge_rumor");
    }

    private void StepKnowledgeVerified()
    {
        _monitor.Log("[P6] STEP = KNOWLEDGE_TRANSITION (RUMOR→VERIFIED) + INVESTIGATE_GOAL", LogLevel.Info);
        // Second observation → RUMOR→VERIFIED.
        if (_causeCount < 2)
        {
            // Place a second analyze object so production observes again (edge-triggered per object).
            try
            {
                var location = Game1.currentLocation;
                if (location is null) return;
                var tile = new Microsoft.Xna.Framework.Vector2(20 + _causeCount * 2, 20);
                var obj = new StardewValley.Object(tile, "96");
                obj.modData[MagicObservationSource.AnalyzeContextTag] = "true";
                location.objects[tile] = obj;
                _causeCount++;
                _monitor.Log($"[P6] CAUSED second analyze-tagged object placed", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[P6] SECOND_CAUSE_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                return;
            }
            return; // wait a tick for production to observe
        }

        var obs = _observations.GetForNpc(TestNpc).Where(o => o.Type == ObservationTypes.EventTriggered).ToList();
        if (obs.Count < 2)
        {
            _monitor.Log($"[P6] AWAITING_SECOND_OBSERVATION count={obs.Count}", LogLevel.Info);
            return;
        }

        var next = _magic.ObserveMagic(TestNpc, Now(), obs[1].ObservationId);
        if (next is null)
        {
            _monitor.Log("[P6] VERIFY_TRANSITION_FAILED", LogLevel.Error);
            return;
        }
        _magic.SetInvestigateGoal(TestNpc, Now());
        _monitor.Log($"[P6] KNOWLEDGE_VERIFIED_PASS magic.exists={next} goal=goals.investigate_magic", LogLevel.Info);
        _repository.MarkDataDirty();
        SetState("knowledge_verified");
    }

    private void StepLearnDecision()
    {
        _monitor.Log("[P6] STEP = LEARN_DECISION (deterministic fixture: ADOPT)", LogLevel.Info);
        var knowledge = _magic.GetKnowledgeState(TestNpc);
        if (knowledge != CharacterKnowledge.StateVerified)
        {
            _monitor.Log("[P6] LEARN_DECISION_SKIPPED — knowledge not VERIFIED", LogLevel.Error);
            return;
        }

        // Decision: ADOPT (LEARN_MAGIC). reason tied to verified knowledge.
        var adopt = _magic.AdoptMagic(TestNpc, Now());
        if (!adopt.Ok) { _monitor.Log($"[P6] ADOPT_FAILED {adopt.Error}", LogLevel.Error); return; }
        var prof = _magic.SetProficiency(TestNpc, 0.2, Now()); // initial AIWorld-owned proficiency
        if (!prof.Ok) { _monitor.Log($"[P6] PROFICIENCY_FAILED {prof.Error}", LogLevel.Error); return; }

        _monitor.Log("[P6] LEARN_DECISION_PASS intent=magic.learn decision=ADOPT reason=knowledge VERIFIED (magic.exists)", LogLevel.Info);
        _monitor.Log("[P6] ADOPTION_PASS state.magic_student=true goals.learn_magic=true skills.magic=0.2", LogLevel.Info);
        _repository.MarkDataDirty();
        SetState("adopted");
    }

    /// <summary>
    /// P6-C — real DeepSeek learn decision (PM Phase 6 Gate §八-§十九).
    /// CONTROL A (RUMOR) vs TEST B (VERIFIED) via the production DeepSeek path. The model only
    /// decides WHAT/WHY (intent + reason); any adoption mutation happens AFTER deterministic
    /// authorization in AIWorld code (LLM never writes adoption/proficiency).
    /// </summary>
    private void StepRealLlmLearnDecision()
    {
        _monitor.Log("[P6] STEP = REAL_LLM_LEARN_DECISION (Control RUMOR vs Test VERIFIED)", LogLevel.Info);
        try
        {
            var llm = RealLlm.Create(out var status);
            if (llm is null)
            {
                _monitor.Log($"[P6] LLM_UNAVAILABLE status={status}", LogLevel.Error);
                return;
            }

            var probe = new AIWorld.Adapters.DeepSeek.DeepSeekMagicLearnProbe(llm, PersonaText.Abigail, TestNpc);
            var result = probe.Probe();

            var controlSpeech = Truncate(result.Control?.Response?.Speech ?? result.Control?.Message ?? "(no response)", 200);
            var controlIntent = result.Control?.Response?.Intent is null ? "null" : $"{result.Control.Response.Intent.Type} priority={result.Control.Response.Intent.Priority:F2}";
            var testSpeech = Truncate(result.Test?.Response?.Speech ?? result.Test?.Message ?? "(no response)", 200);
            var testIntent = result.Test?.Response?.Intent is null ? "null" : $"{result.Test.Response.Intent.Type} priority={result.Test.Response.Intent.Priority:F2}";
            var testReason = Truncate(result.Test?.Response?.Intent?.Reason ?? "(none)", 160);

            _monitor.Log($"[P6] CONTROL_STATUS={(result.ControlProviderOk ? "SUCCESS" : "FAIL")} intent={controlIntent}", LogLevel.Info);
            _monitor.Log($"[P6] CONTROL_SPEECH: {controlSpeech}", LogLevel.Info);
            _monitor.Log($"[P6] VERIFIED_STATUS={(result.TestProviderOk ? "SUCCESS" : "FAIL")} intent={testIntent}", LogLevel.Info);
            _monitor.Log($"[P6] VERIFIED_SPEECH: {testSpeech}", LogLevel.Info);
            _monitor.Log($"[P6] VERIFIED_REASON: {testReason}", LogLevel.Info);
            _monitor.Log($"[P6] KNOWLEDGE_INFLUENCE={(result.KnowledgeInfluenceDetected ? "DETECTED" : "NOT_DETECTED")} signals={string.Join(" | ", result.InfluenceSignals)}", LogLevel.Info);

            if (!result.ControlProviderOk || !result.TestProviderOk)
            {
                _monitor.Log($"[P6] LLM_PROBE_PROVIDER_ISSUE {result.Error}", LogLevel.Error);
                return;
            }
            if (!result.KnowledgeInfluenceDetected)
            {
                _monitor.Log("[P6] KNOWLEDGE_INFLUENCE_FAIL — no semantic dependence on VERIFIED knowledge", LogLevel.Error);
                return;
            }
            _monitor.Log("[P6] KNOWLEDGE_INFLUENCE_PASS — VERIFIED knowledge materially influenced model output", LogLevel.Info);

            // Authorization boundary (§十一): the model proposes WHAT/WHY. AIWorld authorizes +
            // mutates deterministically. Only magic.learn intent authorizes adoption.
            var intentType = result.Test?.Response?.Intent?.Type;
            if (intentType == "magic.learn")
            {
                _monitor.Log($"[P6] LLM_INTENT=magic.learn — authorization PASS, deterministic adoption", LogLevel.Info);
                var adopt = _magic.AdoptMagic(TestNpc, Now());
                if (!adopt.Ok) { _monitor.Log($"[P6] ADOPT_FAILED {adopt.Error}", LogLevel.Error); return; }
                var prof = _magic.SetProficiency(TestNpc, 0.2, Now());
                if (!prof.Ok) { _monitor.Log($"[P6] PROFICIENCY_FAILED {prof.Error}", LogLevel.Error); return; }
                _monitor.Log("[P6] ADOPTION_PASS state.magic_student=true goals.learn_magic=true skills.magic=0.2 (authorized by real LLM intent)", LogLevel.Info);
                _repository.MarkDataDirty();
                SetState("adopted");
            }
            else
            {
                // VALID_DECLINE / INVESTIGATE_MORE — not a failure (PM §十六). Knowledge influence
                // is already proven. Persist knowledge-only state; adoption mutation path remains
                // covered by the deterministic P6-A/B runs.
                _monitor.Log($"[P6] LLM_INTENT={intentType ?? "null"} — VALID_DECLINE (not ADOPT); knowledge influence proven, adoption not authorized this run", LogLevel.Info);
                _repository.MarkDataDirty();
                SetState("declined");
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"[P6] REAL_LLM_LEARN_EXCEPTION {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");

    /// <summary>Abigail persona context (REQUIREMENTS-aligned, not invented history).</summary>
    private static class PersonaText
    {
        public const string Abigail =
            "Persona: Abigail is a free-spirited young woman, adventurous and a little restless. " +
            "She is curious about the world beyond Pelican Town, enjoys exploring, and is drawn to " +
            "the mysterious. She is brave but not reckless, and she genuinely cares about people " +
            "close to her — including the player.";
    }

    /// <summary>Production DeepSeek model factory (key from env, graceful failure).</summary>
    private static class RealLlm
    {
        public static AIWorld.Core.Providers.IAIWorldLanguageModel? Create(out string status)
        {
            // PM Local Provider Routing Gate: DEV_QA real mode routes to LOCAL provider.
            // DeepSeek is NEVER constructed in DEV_QA (DIRECT_DEEPSEEK_CONSTRUCTION_IN_DEV_QA = 0).
            // If localhost is unavailable the provider call fails with LOCAL_PROVIDER_UNAVAILABLE
            // and the caller reports the scenario as FAIL (no deterministic fallback).
            var model = new AIWorld.Adapters.Local.LocalLanguageModel();
            status = "local";
            return model;
        }
    }

    private int _settleTicks; // ticks waited after warp-home before writing the save signal

    private void StepSignalSave()
    {
        _monitor.Log($"[P6] STEP = SIGNAL_SAVE (end-day real save chain) settle={_settleTicks} suppress={_suppressSignal}", LogLevel.Info);

        // Phase 7 chained mode: CoreDemoDriver owns the single save trigger.
        // State machine still completes (decision metadata recorded) but the warp-home
        // + signal write are deferred to the orchestrator.
        if (_suppressSignal)
        {
            _done = true;
            var chainedDecision = State == "adopted" ? "adopted" : "declined";
            _data.Metadata[StateKey] = "complete";
            _data.Metadata["P6_DECISION"] = chainedDecision;
            _monitor.Log($"[P6] TEST_COMPLETE (chained, signal deferred) — decision={chainedDecision}, verify deferred to cold restart", LogLevel.Info);
            return;
        }

        // Restore P4-B-proven pre-NewDay state (home, in bed, 0600) so Game1.NewDay completes.
        // The warp must SETTLE before the signal is written: if the host's Game1.NewDay fires in
        // the same tick as the warp starts, the day transition hangs (player not actually home).
        if (_settleTicks == 0)
        {
            try
            {
                var farmHouse = Game1.getLocationFromName("FarmHouse");
                if (farmHouse is not null)
                {
                    Game1.warpFarmer("FarmHouse", 8, 8, false);
                    Game1.player.isInBed.Value = true;
                    Game1.timeOfDay = 600;
                    _monitor.Log("[P6] CAUSED home state restored (FarmHouse, in bed, 0600)", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"[P6] WARP_HOME_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
            }
            _settleTicks++;
            return; // wait a tick for the warp to complete
        }
        if (_settleTicks < 3)
        {
            _settleTicks++;
            _monitor.Log($"[P6] SAVE_SETTLE awaiting warp settle ({_settleTicks}/3)", LogLevel.Info);
            return;
        }

        _done = true;
        // Record the pre-save decision BEFORE overwriting StateKey, so cold-restart verify
        // knows the expected adoption state (State reads StateKey — must capture first).
        var decision = State == "adopted" ? "adopted" : "declined";
        _data.Metadata[StateKey] = "complete";
        _data.Metadata["P6_DECISION"] = decision;
        _monitor.Log($"[P6] TEST_COMPLETE — magic capability loop finished (decision={decision}, verify deferred to cold restart)", LogLevel.Info);
        SignalStepDone();
    }

    private void StepVerifyAfterRestart()
    {
        _monitor.Log("[P6] STEP = VERIFY_AFTER_COLD_RESTART", LogLevel.Info);
        var snap = _magic.Snapshot(TestNpc);
        _monitor.Log($"[P6] SNAPSHOT knowledge={snap.Knowledge} adopted={snap.Adopted} proficiency={snap.Proficiency:F2} investigateGoal={snap.InvestigateGoal} learnGoal={snap.LearnGoal}", LogLevel.Info);

        // The pre-save decision outcome (set by StepSignalSave) determines expected verify state:
        // "adopted" expects adoption+proficiency; "declined" expects no adoption (VALID_DECLINE).
        bool expectedAdopted = _data.Metadata.TryGetValue("P6_DECISION", out var d) && d == "adopted";

        var obs = _observations.GetForNpc(TestNpc).ToList();
        var knowledgeOk = snap.Knowledge == CharacterKnowledge.StateVerified;
        var adoptionOk = snap.Adopted == expectedAdopted;
        var proficiencyOk = expectedAdopted ? (snap.Proficiency > 0 && snap.Proficiency <= 1) : snap.Proficiency == 0;
        var goalOk = snap.InvestigateGoal && (expectedAdopted ? snap.LearnGoal : true);
        var observationOk = obs.Count(o => o.Type == ObservationTypes.EventTriggered) >= 1;

        _monitor.Log(knowledgeOk ? "[P6] KNOWLEDGE_RESTORE_PASS" : "[P6] KNOWLEDGE_RESTORE_FAIL", knowledgeOk ? LogLevel.Info : LogLevel.Error);
        _monitor.Log(adoptionOk ? "[P6] ADOPTION_RESTORE_PASS" : "[P6] ADOPTION_RESTORE_FAIL", adoptionOk ? LogLevel.Info : LogLevel.Error);
        _monitor.Log(proficiencyOk ? "[P6] PROFICIENCY_RESTORE_PASS" : "[P6] PROFICIENCY_RESTORE_FAIL", proficiencyOk ? LogLevel.Info : LogLevel.Error);
        _monitor.Log(goalOk ? "[P6] GOAL_RESTORE_PASS" : "[P6] GOAL_RESTORE_FAIL", goalOk ? LogLevel.Info : LogLevel.Error);
        _monitor.Log(observationOk ? "[P6] OBSERVATION_RESTORE_PASS" : "[P6] OBSERVATION_RESTORE_FAIL", observationOk ? LogLevel.Info : LogLevel.Error);
        _monitor.Log($"[P6] EXPECTED_DECISION={(expectedAdopted ? "adopted" : "declined")}", LogLevel.Info);

        bool all = knowledgeOk && adoptionOk && proficiencyOk && goalOk && observationOk;
        _monitor.Log(all ? "[P6] TEST_COMPLETE — magic capability persisted + restored (PASS)" : "[P6] VERIFY_FAILED", all ? LogLevel.Info : LogLevel.Error);
        _done = true;
    }

    private string Now() => $"{Game1.currentSeason}-{Game1.dayOfMonth}-{Game1.year}@{Game1.timeOfDay:D4}";

    private void SignalStepDone()
    {
        try
        {
            QaSignal.WriteStageDone(_helper);
            _repository.MarkDataDirty();
            _monitor.Log("[P6] SIGNAL_WRITTEN stage-a-done.signal", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[P6] SIGNAL_WRITE_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
    }
}
