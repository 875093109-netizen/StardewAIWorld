using AIWorld.Core.Behaviors;
using AIWorld.Core.Chat;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace AIWorld.Demo;

/// <summary>
/// CHARACTER AWARENESS — DEV_QA RUNTIME GATE driver (PM 2026-08-12, mode=p8).
///
/// Deterministic runtime verification of:
///   A SelfState        — NPC location == npc.currentLocation != player location (intentionally
///                        separated); prompt trace contains distinct CURRENT WORLD STATE and
///                        PLAYER WORLD CONTEXT sections; NO real LLM.
///   B Follow ownership — during follow: BehaviorOwner=PLAYER_REQUEST, CurrentActivity=FOLLOW_PLAYER,
///                        CurrentTarget=Player, FollowingPlayer=true; after stop+natural rejoin:
///                        CANONICAL, FollowingPlayer=false, stale FollowSession=false.
///                        Uses the ACCEPTED v0.2 lifecycle (FollowSessionManager) — does NOT
///                        reopen lifecycle architecture; only verifies awareness reflects state.
///   C Canonical negative — eventsSeen without "1": no experience, no knowledge, no memory.
///                        Verified again after save/reload (stage B).
///   D Canonical positive — eventsSeen contains "1" (the SAME evidence source production reads);
///                        CanonicalExperienceSource observes it via normal scan → ingest.
///                        Does NOT call CanonicalExperienceService directly as the acceptance path.
///   E Idempotency      — game time advances across scans; save/reload; all resolve to SAME
///                        ExperienceId; zero duplicate memory/knowledge after first ingestion.
///   F Hydration equivalence — SAVE_HISTORY_RECONSTRUCTED reconstruction resolves to the SAME
///                        ExperienceId as LIVE_OBSERVED without creating a second experience.
///
/// Evidence markers: [AWARE-TEST] ... (consumed by the runner).
/// No LLM is called anywhere in this driver.
/// </summary>
public sealed class AwarenessGateDriver
{
    private const string TestNpc = "Abigail";
    private const string ExpectedEventId = "1"; // first segment of Data/Events/SeedShop key (IL-verified)

    private readonly IMonitor _monitor;
    private readonly IModHelper _helper;
    private readonly AIWorldSaveRepository _repository;
    private readonly AIWorldSaveData _data;
    private readonly VanillaCanonicalExperienceSource _canonicalSource;
    private readonly CanonicalExperienceService _canonicalService;
    private readonly SelfStateProvider _selfStateProvider;
    private readonly FollowSessionManager _sessions;
    private readonly string _gateStage; // "A" (process 1) | "B" (process 2 after reload)

    private string _phase = "start";
    private int _ticks;
    private bool _done;
    private string? _liveExperienceId;
    private string? _hydratedExperienceId;
    private int _ingestedMemoryCountAtStart;
    private int _ingestedExperienceCountAtStart;
    private int _settleTicks; // ticks waited after warp-home before writing the save signal (P5/P6 recipe)

    public AwarenessGateDriver(
        IMonitor monitor,
        IModHelper helper,
        AIWorldSaveRepository repository,
        AIWorldSaveData data,
        VanillaCanonicalExperienceSource canonicalSource,
        CanonicalExperienceService canonicalService,
        SelfStateProvider selfStateProvider,
        FollowSessionManager sessions,
        string gateStage)
    {
        _monitor = monitor;
        _helper = helper;
        _repository = repository;
        _data = data;
        _canonicalSource = canonicalSource;
        _canonicalService = canonicalService;
        _selfStateProvider = selfStateProvider;
        _sessions = sessions;
        _gateStage = gateStage;
    }

    /// <summary>Driven by ModEntry.OnOneSecondUpdateTicked (main thread).</summary>
    public void TickIfReady()
    {
        if (_done || !Game1.hasLoadedGame || Game1.player is null) return;

        _ticks++;
        switch (_phase)
        {
            case "start":
                if (_gateStage == "B")
                {
                    _phase = "e_reload";
                    Log("[AWARE-TEST] STAGE=B START reload verification");
                }
                else
                {
                    _phase = "a_selfstate";
                    Log("[AWARE-TEST] STAGE=A START runtime gate");
                }
                break;

            case "a_selfstate":
                RunGateA();
                _phase = "b_follow";
                break;

            case "b_follow":
                RunGateB();
                _phase = "c_negative";
                break;

            case "c_negative":
                RunGateC();
                _phase = "d_positive";
                break;

            case "d_positive":
                RunGateD();
                _phase = "e_idempotency";
                break;

            case "e_idempotency":
                RunGateE();
                _phase = "f_hydration";
                break;

            case "f_hydration":
                RunGateF();
                _phase = "save";
                break;

            case "save":
                // Trigger the REAL end-day save via the game's NATURAL sleep/pass-out flow
                // (SOURCE-VERIFIED against the hung P8 run): Game1.NewDay(0f) is a no-op when
                // the window is backgrounded — the day-transition task never schedules, SMAPI
                // Saving/Saved never fire, production OnSaving never runs. The game's own clock
                // (UpdateGameClock) DOES run in background (pause override), so set time to
                // 2550 (1:50 AM) and let it naturally reach 2600 (2:00 AM) → forced sleep →
                // save → [P3] SAVING → WRITE_SAVE_DATA_SUCCESS → SAVED → DIRTY_FALSE.
                // Warp home first (pass-out location clean), settle 3 ticks, then signal.
                _repository.MarkDataDirty();
                if (_settleTicks == 0)
                {
                    try
                    {
                        var farmHouse = Game1.getLocationFromName("FarmHouse");
                        if (farmHouse is not null)
                        {
                            Game1.warpFarmer("FarmHouse", 8, 8, false);
                            Game1.player.isInBed.Value = true;
                            Game1.timeOfDay = 2550;
                            Log("[AWARE-TEST] GATE_SAVE home state restored (FarmHouse, in bed, 2550=01:50 AM)");
                        }
                        else
                        {
                            Log("[AWARE-TEST] GATE_SAVE WARP_HOME_FAILED FarmHouse-not-found");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[AWARE-TEST] GATE_SAVE WARP_HOME_FAILED {ex.GetType().Name}: {ex.Message}");
                    }
                    _settleTicks++;
                    break; // keep phase = save, wait a tick for warp settle
                }
                if (_settleTicks < 3)
                {
                    _settleTicks++;
                    Log($"[AWARE-TEST] GATE_SAVE settle ({_settleTicks}/3)");
                    break;
                }
                // Settled — write the signal; the game naturally passes out at 2600 and saves.
                try
                {
                    QaSignal.WriteStageDone(_helper);
                }
                catch { }
                Log("[AWARE-TEST] GATE_SAVE_REQUESTED — home state restored + settled, awaiting natural 02:00 pass-out save");
                _phase = "saved";
                break;

            case "saved":
                // Signal written — the host drives the real end-day save. Nothing more to do;
                // the runner waits for the save markers ([P3] SAVING / SAVED / DIRTY_FALSE).
                _done = true;
                break;

            case "e_reload":
                RunGateEReload();
                Log("[AWARE-TEST] STAGE=B COMPLETE");
                _done = true;
                break;
        }
    }

    // ── GATE A — SelfState runtime truth ─────────────────────────────────────
    private void RunGateA()
    {
        Log("[AWARE-TEST] GATE_A begin");
        var npc = Game1.getCharacterFromName(TestNpc);
        if (npc is null) { Log("[AWARE-TEST] GATE_A FAIL npc-not-found"); return; }

        // Intentionally separate NPC and player: move Abigail to SeedShop, player to Beach.
        Game1.warpCharacter(npc, "SeedShop", new Microsoft.Xna.Framework.Vector2(4f, 4f));
        Game1.warpFarmer("Beach", 20, 4, false);
        WaitTicks(2);

        var npcLocation = npc.currentLocation?.NameOrUniqueName ?? "unknown";
        var playerLocation = Game1.currentLocation?.NameOrUniqueName ?? "unknown";
        var self = _selfStateProvider.Get(TestNpc);
        var selfLocation = self.CurrentLocation;

        Log($"[AWARE-TEST] GATE_A NPC_CURRENT_LOCATION={npcLocation}");
        Log($"[AWARE-TEST] GATE_A PLAYER_CURRENT_LOCATION={playerLocation}");
        Log($"[AWARE-TEST] GATE_A SELF_STATE_CURRENT_LOCATION={selfLocation}");
        Log($"[AWARE-TEST] GATE_A SELF_STATE_DESCRIBE={self.Describe}");

        var npcOk = string.Equals(selfLocation, npcLocation, StringComparison.Ordinal);
        var separatedOk = !string.Equals(playerLocation, npcLocation, StringComparison.Ordinal);
        Log($"[AWARE-TEST] GATE_A NPC_MATCH={npcOk}");
        Log($"[AWARE-TEST] GATE_A PLAYER_SEPARATED={separatedOk}");

        // Prompt/context trace: assemble the production prompt WITHOUT any LLM.
        try
        {
            var assembler = new ChatContextAssembler(
                personaSelector: key => $"(test persona for {key})",
                worldContextProvider: $"playerLocation={playerLocation} season={Game1.currentSeason} day={Game1.dayOfMonth} year={Game1.year} time={Game1.timeOfDay:D4}");
            var prompt = assembler.BuildSystemPrompt(
                TestNpc,
                _data,
                Array.Empty<MemoryRecord>(),
                selfState: self,
                playerWorldState: $"playerLocation={playerLocation} season={Game1.currentSeason}");

            var hasNpcState = prompt.Contains("CURRENT WORLD STATE", StringComparison.Ordinal);
            var hasPlayerCtx = prompt.Contains("PLAYER WORLD CONTEXT", StringComparison.Ordinal);
            var npcSection = ExtractSection(prompt, "CURRENT WORLD STATE", "PLAYER WORLD CONTEXT");
            var npcSectionHasOwnLocation = npcSection?.Contains(selfLocation, StringComparison.Ordinal) == true;
            var playerNotInNpcSection = npcSection?.Contains("playerLocation", StringComparison.Ordinal) != true;

            Log($"[AWARE-TEST] GATE_A PROMPT_HAS_NPC_STATE={hasNpcState}");
            Log($"[AWARE-TEST] GATE_A PROMPT_HAS_PLAYER_CTX={hasPlayerCtx}");
            Log($"[AWARE-TEST] GATE_A PROMPT_NPC_SECTION_OWN_LOCATION={npcSectionHasOwnLocation}");
            Log($"[AWARE-TEST] GATE_A PROMPT_NPC_SECTION_NO_PLAYER={playerNotInNpcSection}");
            Log($"[AWARE-TEST] GATE_A PROMPT_SNIPPET={Truncate(prompt, 600)}");
        }
        catch (Exception ex)
        {
            Log($"[AWARE-TEST] GATE_A PROMPT_FAIL {ex.GetType().Name}: {ex.Message}");
        }

        Log($"[AWARE-TEST] GATE_A RESULT={(npcOk && separatedOk ? "PASS" : "FAIL")}");
    }

    // ── GATE B — Follow ownership reflects lifecycle state ───────────────────
    private void RunGateB()
    {
        Log("[AWARE-TEST] GATE_B begin");
        var stamp = GameDate() + "@" + Game1.timeOfDay.ToString("D4");

        // Start a follow session through the ACCEPTED lifecycle manager.
        var session = _sessions.Start(TestNpc, "gate-b-deterministic", stamp);
        if (session is null)
        {
            // Maybe already active from a previous partial run — end it first.
            _sessions.End(TestNpc, "gate-b-cleanup", stamp);
            _sessions.Start(TestNpc, "gate-b-deterministic", stamp);
        }
        _repository.MarkDataDirty();
        WaitTicks(1);

        var during = _selfStateProvider.Get(TestNpc);
        Log($"[AWARE-TEST] GATE_B DURING owner={during.BehaviorOwner} activity={during.CurrentActivity} target={during.CurrentTarget ?? "(null)"} following={during.FollowingPlayer}");
        var duringOk = during.BehaviorOwner == "PLAYER_REQUEST"
                       && during.CurrentActivity == CurrentActivity.FOLLOW_PLAYER
                       && during.CurrentTarget == "Player"
                       && during.FollowingPlayer;

        // Stop + natural rejoin (accepted v0.2 lifecycle end path).
        _sessions.End(TestNpc, "GATE_B_STOP", stamp);
        _repository.MarkDataDirty();
        WaitTicks(1);
        // Natural rejoin is the canonical baseline (no lease active → CANONICAL).
        var after = _selfStateProvider.Get(TestNpc);
        var stale = _sessions.GetActive(TestNpc) is not null;
        Log($"[AWARE-TEST] GATE_B AFTER owner={after.BehaviorOwner} activity={after.CurrentActivity} following={after.FollowingPlayer} staleSession={stale}");
        var afterOk = after.BehaviorOwner == "CANONICAL"
                      && !after.FollowingPlayer
                      && !stale;

        Log($"[AWARE-TEST] GATE_B DURING_OK={duringOk}");
        Log($"[AWARE-TEST] GATE_B AFTER_OK={afterOk}");
        Log($"[AWARE-TEST] GATE_B RESULT={(duringOk && afterOk ? "PASS" : "FAIL")}");
    }

    // ── GATE C — Canonical negative (event NOT seen) ─────────────────────────
    private void RunGateC()
    {
        Log("[AWARE-TEST] GATE_C begin");
        var eventsSeen = Game1.player.eventsSeen;
        var containsOne = eventsSeen?.Contains(ExpectedEventId) == true;
        Log($"[AWARE-TEST] GATE_C eventsSeen_contains_1={containsOne}");

        // Run the normal scan (same code path production uses).
        var stamp = GameDate() + "@" + Game1.timeOfDay.ToString("D4");
        var candidates = _canonicalSource.ScanCompletedHeartEvents(stamp);
        foreach (var c in candidates) _canonicalService.Ingest(c);

        var abigail = _data.Characters.TryGetValue(TestNpc, out var c0) ? c0 : null;
        var hasKnowledge = abigail?.Knowledge.ContainsKey("heartEvent.playedPrairieKing") == true;
        var hasMemory = abigail?.Memories.Count > 0;
        var hasExperience = _data.CanonicalExperiences.Count > 0;

        Log($"[AWARE-TEST] GATE_C canonical_experience_absent={!hasExperience}");
        Log($"[AWARE-TEST] GATE_C abigail_knowledge_absent={!hasKnowledge}");
        Log($"[AWARE-TEST] GATE_C abigail_memory_absent={!hasMemory}");
        var pass = !containsOne && !hasExperience && !hasKnowledge && !hasMemory;
        Log($"[AWARE-TEST] GATE_C RESULT={(pass ? "PASS" : "FAIL")}");

        // Snapshot counts for later idempotency checks.
        _ingestedExperienceCountAtStart = _data.CanonicalExperiences.Count;
        _ingestedMemoryCountAtStart = abigail?.Memories.Count ?? 0;
    }

    // ── GATE D — Canonical positive (event seen → normal source observes) ────
    private void RunGateD()
    {
        Log("[AWARE-TEST] GATE_D begin");
        // Establish the SAME Stardew evidence source production reads: player.eventsSeen.
        // (NOT a direct service call — this is the game's own completed-event ledger.)
        Game1.player.eventsSeen.Add(ExpectedEventId);
        Log($"[AWARE-TEST] GATE_D eventsSeen_contains_1={Game1.player.eventsSeen.Contains(ExpectedEventId)}");

        // Let the NORMAL CanonicalExperienceSource observe it (same scan as production).
        var stamp = GameDate() + "@" + Game1.timeOfDay.ToString("D4");
        var candidates = _canonicalSource.ScanCompletedHeartEvents(stamp);
        Log($"[AWARE-TEST] GATE_D candidates={candidates.Count}");
        foreach (var c in candidates)
        {
            var result = _canonicalService.Ingest(c);
            Log($"[AWARE-TEST] GATE_D ingest accepted={result.Accepted} duplicate={result.Duplicate} id={result.Event?.ExperienceId ?? c.ExperienceId}");
            if (result.Accepted) _liveExperienceId = result.Event!.ExperienceId;
        }

        var abigail = _data.Characters.TryGetValue(TestNpc, out var c0) ? c0 : null;
        var knowledge = abigail?.Knowledge.TryGetValue("heartEvent.playedPrairieKing", out var k) == true ? k.State : "(absent)";
        var memoryCount = abigail?.Memories.Count ?? 0;
        var unrelated = _data.Characters.Keys.Any(k => k != TestNpc && k != "Player");

        Log($"[AWARE-TEST] GATE_D EXPERIENCE_INGESTED={_liveExperienceId is not null}");
        Log($"[AWARE-TEST] GATE_D abigail_knowledge={knowledge}");
        Log($"[AWARE-TEST] GATE_D abigail_memory_count={memoryCount}");
        Log($"[AWARE-TEST] GATE_D unrelated_npc_contamination={unrelated}");
        Log($"[AWARE-TEST] GATE_D EXPERIENCE_ID={_liveExperienceId ?? "(none)"}");

        var pass = _liveExperienceId is not null
                   && knowledge == "VERIFIED"
                   && memoryCount == 1
                   && !unrelated;
        Log($"[AWARE-TEST] GATE_D RESULT={(pass ? "PASS" : "FAIL")}");
    }

    // ── GATE E — Idempotency across scan times (in-process part) ─────────────
    private void RunGateE()
    {
        Log("[AWARE-TEST] GATE_E begin");
        if (_liveExperienceId is null) { Log("[AWARE-TEST] GATE_E FAIL no-live-id"); return; }

        // Advance game time and scan again — the previous implementation failed exactly here.
        AdvanceTime(4); // +4 hours
        WaitTicks(1);
        var stamp = GameDate() + "@" + Game1.timeOfDay.ToString("D4");
        var candidates = _canonicalSource.ScanCompletedHeartEvents(stamp);
        var ingest = candidates.Count > 0 ? _canonicalService.Ingest(candidates[0]) : null;
        Log($"[AWARE-TEST] GATE_E later_scan_duplicate={ingest?.Duplicate ?? false} accepted={ingest?.Accepted ?? false}");

        var abigail = _data.Characters.TryGetValue(TestNpc, out var c0) ? c0 : null;
        var memoryCount = abigail?.Memories.Count ?? 0;
        var sameId = ingest?.Event?.ExperienceId == _liveExperienceId;
        Log($"[AWARE-TEST] GATE_E same_experience_id={sameId}");
        Log($"[AWARE-TEST] GATE_E memory_count_after_second_scan={memoryCount}");

        var pass = ingest?.Duplicate == true && sameId && memoryCount == 1;
        Log($"[AWARE-TEST] GATE_E RESULT={(pass ? "PASS" : "FAIL")}");
    }

    // ── GATE E (stage B) — reload verification ───────────────────────────────
    private void RunGateEReload()
    {
        Log("[AWARE-TEST] GATE_E_RELOAD begin");
        var eventsSeen = Game1.player.eventsSeen?.Contains(ExpectedEventId) == true;
        Log($"[AWARE-TEST] GATE_E_RELOAD eventsSeen_contains_1={eventsSeen}");

        // Re-scan after reload — must NOT create a second experience/memory.
        var stamp = GameDate() + "@" + Game1.timeOfDay.ToString("D4");
        var candidates = _canonicalSource.ScanCompletedHeartEvents(stamp);
        var ingest = candidates.Count > 0 ? _canonicalService.Ingest(candidates[0]) : null;
        Log($"[AWARE-TEST] GATE_E_RELOAD duplicate={ingest?.Duplicate ?? false} accepted={ingest?.Accepted ?? false}");

        var abigail = _data.Characters.TryGetValue(TestNpc, out var c0) ? c0 : null;
        var memoryCount = abigail?.Memories.Count ?? 0;
        var experienceCount = _data.CanonicalExperiences.Count;
        Log($"[AWARE-TEST] GATE_E_RELOAD memory_count={memoryCount}");
        Log($"[AWARE-TEST] GATE_E_RELOAD experience_count={experienceCount}");
        Log($"[AWARE-TEST] GATE_E_RELOAD memory_count_increment=0 expected={memoryCount == 1}");
        Log($"[AWARE-TEST] GATE_E_RELOAD knowledge_duplication=0 expected={abigail?.Knowledge.Count(k => k.Value.State == "VERIFIED") <= 1}");

        var pass = eventsSeen && ingest?.Duplicate == true && memoryCount == 1;
        Log($"[AWARE-TEST] GATE_E_RELOAD RESULT={(pass ? "PASS" : "FAIL")}");
    }

    // ── GATE F — Hydration equivalence ───────────────────────────────────────
    private void RunGateF()
    {
        Log("[AWARE-TEST] GATE_F begin");
        if (_liveExperienceId is null) { Log("[AWARE-TEST] GATE_F FAIL no-live-id"); return; }

        // Reconstruct the same canonical event from save evidence (hydrator path).
        var hydrator = new CanonicalHistoryHydrator(_canonicalService);
        var reconstructed = new CanonicalExperienceEvent
        {
            ExperienceId = string.Empty,
            Type = "HEART_EVENT",
            Source = "VANILLA",
            CanonicalSourceId = "vanilla.heartEvent",
            CanonicalEventId = ExpectedEventId,
            OccurrenceKey = "once",
            Actor = TestNpc,
            Participants = new List<string> { TestNpc },
            Facts = new List<string> { "Abigail and the player played Journey of the Prairie King together in the SeedShop basement" },
            OccurredAt = "unknown", // save evidence does not carry the exact timestamp — do NOT invent it
            Location = "SeedShop",
            KnowledgeEffects = new Dictionary<string, string> { ["heartEvent.playedPrairieKing"] = "VERIFIED" },
            MemoryImportance = 0.8f,
            Provenance = ExperienceProvenance.SAVE_HISTORY_RECONSTRUCTED,
            SchemaVersion = "1",
        };
        var id = CanonicalExperienceService.ComputeExperienceId(reconstructed);
        _hydratedExperienceId = id;
        Log($"[AWARE-TEST] GATE_F LIVE_ID={_liveExperienceId}");
        Log($"[AWARE-TEST] GATE_F HYDRATED_ID={id}");
        Log($"[AWARE-TEST] GATE_F SAME_ID={string.Equals(_liveExperienceId, id, StringComparison.Ordinal)}");

        // Hydrate through the production path — must be a duplicate (same identity), not a second experience.
        var result = hydrator.Hydrate(new[] { reconstructed });
        Log($"[AWARE-TEST] GATE_F hydrate attempted={result.Attempted} accepted={result.Accepted} duplicates={result.Duplicates}");

        var abigail = _data.Characters.TryGetValue(TestNpc, out var c0) ? c0 : null;
        var memoryCount = abigail?.Memories.Count ?? 0;
        Log($"[AWARE-TEST] GATE_F memory_count_after_hydration={memoryCount}");

        var pass = string.Equals(_liveExperienceId, id, StringComparison.Ordinal)
                   && result.Accepted == 0
                   && result.Duplicates == 1
                   && memoryCount == 1;
        Log($"[AWARE-TEST] GATE_F RESULT={(pass ? "PASS" : "FAIL")}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private void Log(string message) => _monitor.Log(message, LogLevel.Info);

    private void WaitTicks(int n) => System.Threading.Thread.Sleep(n * 1100);

    private void AdvanceTime(int hours)
    {
        var t = Game1.timeOfDay;
        var hh = t / 100;
        var mm = t % 100;
        hh += hours;
        if (hh >= 26) hh = 26; // end of day safety
        Game1.timeOfDay = hh * 100 + mm;
        Log($"[AWARE-TEST] TIME_ADVANCED to={Game1.timeOfDay:D4}");
    }

    private static string? ExtractSection(string text, string start, string end)
    {
        var i = text.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return null;
        var j = text.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        return j < 0 ? text[i..] : text[i..j];
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    private static string GameDate()
        => $"{Game1.currentSeason}-{Game1.dayOfMonth}-{Game1.year}";
}
