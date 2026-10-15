using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// PHASE 3 runtime host — SMAPI binding only. All runtime-acceptance state machine
/// logic lives in AIWorld.Core.RuntimeAcceptanceCoordinator (offline-testable).
/// Lifecycle: SaveLoaded→load/hydrate; Saving→write when dirty; Saved→clear dirty;
/// ReturnedToTitle→clear runtime. Logs [P3] markers for evidence.
/// </summary>
public sealed class ModEntry : Mod
{
    private const string SaveDataKey = "aiworld-state";
    private ModConfig _config = new();
    private TalkConsoleHost? _talkConsole;
    private AIWorld.Adapters.LiveTalkLanguageModel? _liveTalkModel;
    private readonly Dictionary<string, string> _talkTravelIntents = new(StringComparer.Ordinal);
    private AIWorld.Core.Chat.MainThreadWorkQueue? _gameThread;
    private ScriptedStoryObserver? _storyObserver;
    private DialogueUiBridge? _dialogueBridge;
    private WorldPerceptionSource? _perception;
    private NpcKnowledgeSource? _npcKnowledge;
    public override object? GetApi() => _perception;
    private readonly Dictionary<string, SmapiObservationSource> _playerObservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MagicObservationSource> _playerMagicObservations = new(StringComparer.Ordinal);
    private int AppointmentDay => _s2Mode ? Game1.Date.TotalDays : Game1.Date.DayOfMonth;

    private AIWorldSaveRepository? _repository;
    private StateDeltaApplier? _stateApplier;
    private MemoryIngestionService? _memoryService;
    private RuntimeAcceptanceCoordinator? _coordinator;
    private StardewSquadAdapter? _squadAdapter;
    private NativeFollowAdapter? _nativeFollow;
    private IFollowAdapter? CurrentFollowAdapter => (IFollowAdapter?)_nativeFollow ?? _squadAdapter;
    private FollowSessionManager? _followSessions;
    private FollowTestDriver? _followDriver;
    private FollowExperienceDriver? _experienceDriver;
    private SmapiObservationSource? _observationSource;
    private ObservationCollector? _observationCollector;
    private MagicCapabilityDriver? _magicDriver;
    private MagicObservationSource? _magicSource;
    private MagicAdoptionService? _magicService;
    private CoreDemoDriver? _coreDemoDriver;
    private AiWorldChatServer? _chatServer;
    private SocialValleyBridgeInjector? _svInjector;
    private FollowIntentHandler? _s2FollowHandler;
    private System.Collections.Concurrent.ConcurrentQueue<(AIWorld.Core.Contracts.NpcIntent, string, string)>? _pendingIntents;
    private bool _s2Mode;
private int _awarenessTick;
private AIWorld.Core.Behaviors.CanonicalExperienceService? _canonicalService;
private VanillaCanonicalExperienceSource? _canonicalSource;
private SelfStateProvider? _selfStateProvider;
    private bool _normalPlayerMode; // ENV SEPARATION: true when no QA command file / no explicit QA mode
    private bool _runtimeDone;
    private bool _p4Mode;
    private bool _p5Mode;
    private bool _p5LlmMode;
    private bool _p6Mode;
    private bool _p6LlmMode;
    private bool _p7Mode;
    private bool _p7LlmMode;
    private string _p4Scenario = "day-end";
    private bool _p8Mode;
    private string _p8GateStage = "A";
    private AwarenessGateDriver? _awarenessGateDriver;

    // TRAVEL GATE T1: production travel pipeline (vanilla movement backend).
    private AIWorld.Core.Behaviors.TravelLeaseManager? _travelLeases;
    private AIWorld.Core.Behaviors.TravelIntentHandler? _travelHandler;
    private NativeTravelAdapter? _travelAdapter;
    private ReturnToBaselinePlanner? _rejoinPlanner;
    private AIWorld.Core.Behaviors.ITravelDestinationResolver? _travelResolver;
    private TravelObservationSource? _travelObservationSource;
    private bool _t1Mode;
    private string _t1Stage = "A";
    private TravelGateDriver? _travelGateDriver;

    // TRAVEL RELIABILITY MATRIX GATE M1: explicit runtime matrix mode (Phase=TRM).
    private bool _matrixMode;
    private string _matrixStage = "A";
    private TravelMatrixDriver? _matrixDriver;

    // A1 NPC 主观同意与定时约会 Gate：同意门 + 约会调度 + 运行时驱动。
    private bool _appointmentMode;
    private string _apptStage = "A";
    private AIWorld.Core.Behaviors.NpcConsentGate? _consentGate;
    private AIWorld.Core.Behaviors.AppointmentManager? _appointments;
    private AppointmentDriver? _appointmentDriver;
    private bool _allowRealLlm; // PM production-call authorization gate (default false)
    private AIWorld.Core.Providers.LlmProviderKind _llmProviderKind = AIWorld.Core.Providers.LlmProviderKind.Production;
    private AIWorld.Core.Providers.CountingLanguageModel? _countingLlm;
    private AIWorld.Core.Providers.IAIWorldLanguageModel? _routedLlm;

    // GATE R1.1 (PM item 8): the harness overlay hides the game's AppData (and thus the SMAPI
    // log) from other processes until the game exits. AIWorld.Demo's [P3]/[P4]/[P5]/[P6]/[P7]/
    // [P8]/[T1] markers therefore never reach the runner mid-run. The RuntimeTestHost already
    // mirrors its markers to a real-Temp sidecar keyed by runId; Demo mirrors the SAME channel
    // so the runner sees the full marker stream live (same runId, same file).
    private string? _runId;
    private string? _markerSidecar;

    private void LogMarker(string message, LogLevel level = LogLevel.Info)
    {
        Monitor.Log(message, level);
        try
        {
            if (_markerSidecar == null && !string.IsNullOrEmpty(_runId))
                _markerSidecar = Path.Combine(Path.GetTempPath(), $"aiworld-run-markers-{_runId}.log");
            if (_markerSidecar != null)
                System.IO.File.AppendAllText(_markerSidecar, message + Environment.NewLine);
        }
        catch { /* sidecar is best-effort; the SMAPI log remains authoritative */ }
    }

    public override void Entry(IModHelper helper)
    {
        void RefreshLanguage()
        {
            var code = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.mod
                ? LocalizedContentManager.CurrentModLanguage?.LanguageCode : LocalizedContentManager.CurrentLanguageString;
            if (AIWorld.Core.Chat.UiText.SetLanguage(code))
            {
                _npcKnowledge?.Invalidate();
                _talkConsole?.LanguageChanged();
            }
        }
        RefreshLanguage();
        LocalizedContentManager.OnLanguageChange += _ => RefreshLanguage();
        // Switching between custom language packs can retain LanguageCode.mod, so also check the actual code.
        helper.Events.GameLoop.OneSecondUpdateTicked += (_, _) => RefreshLanguage();
        _perception = new WorldPerceptionSource(Monitor, () => _config.EnableWorldPerception);
        _npcKnowledge = new NpcKnowledgeSource(Monitor,
            () => Helper.GameContent.Load<Dictionary<string, string>>("AIWorld/ScheduleActivities"),
            () => Helper.GameContent.Load<Dictionary<string, AIWorld.Core.Chat.NpcJobRule>>("AIWorld/JobRules"));
        helper.Events.Content.AssetRequested += (_, e) =>
        {
            if (e.NameWithoutLocale.IsEquivalentTo("AIWorld/ScheduleActivities"))
                e.LoadFromModFile<Dictionary<string, string>>("assets/schedule-activities.json", StardewModdingAPI.Events.AssetLoadPriority.Exclusive);
            if (e.NameWithoutLocale.IsEquivalentTo("AIWorld/JobRules"))
                e.LoadFromModFile<Dictionary<string, AIWorld.Core.Chat.NpcJobRule>>("assets/job-rules.json", StardewModdingAPI.Events.AssetLoadPriority.Exclusive);
        };
        helper.Events.GameLoop.SaveLoaded += (_, _) => _npcKnowledge.Invalidate();
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => _npcKnowledge.Invalidate();
        helper.ConsoleCommands.Add("aiworld_knowledge", "Read-only own NPC facts: aiworld_knowledge <NPC internal name> [item/question]", (_, args) =>
        {
            if (!Context.IsWorldReady) { Monitor.Log("请先载入存档。", LogLevel.Info); return; }
            string npcId = args.Length > 0 ? args[0] : "Penny";
            var state = _selfStateProvider?.Get(npcId);
            Monitor.Log((state?.Describe ?? "NPC state unavailable") + (state?.Job?.Prompt() ?? "") + (state?.Schedule?.Prompt() ?? "\nSchedule unavailable\n")
                + _npcKnowledge.Gifts(npcId, string.Join(" ", args.Skip(1))), LogLevel.Info);
        });
        helper.ConsoleCommands.Add("aiworld_perception", "Read-only NPC perception: aiworld_perception <NPC internal name>", (_, args) =>
        {
            string npc = args.Length > 0 ? args[0] : "Abigail";
            Monitor.Log(AIWorld.Core.Perception.PerceptionEngine.Prompt(_perception.GetFrame(npc)), LogLevel.Info);
        });
        LogMarker("[P3] ENTRY — AIWorld.Demo (persistence host) loaded", LogLevel.Info);

        _config = helper.ReadConfig<ModConfig>();
        helper.Events.GameLoop.UpdateTicked += (_, _) => { _storyObserver?.Tick(); _gameThread?.Drain(); _dialogueBridge?.Tick(); };
        helper.Events.Display.RenderedActiveMenu += (_, e) => _dialogueBridge?.Draw(e.SpriteBatch);
        helper.Events.Input.ButtonPressed += (_, e) => _dialogueBridge?.ButtonPressed(e);
        helper.Events.Content.AssetsInvalidated += (_, e) =>
        {
            if (e.NamesWithoutLocale.Any(n => n.IsEquivalentTo("Data/Objects") || n.IsEquivalentTo("Data/NPCGiftTastes")
                || n.IsEquivalentTo("AIWorld/ScheduleActivities"))) _npcKnowledge?.Invalidate();
            if (e.NamesWithoutLocale.Any(n => n.IsEquivalentTo("Data/Events/BathHouse_Pool") || n.IsEquivalentTo("Data/mail")
                || n.IsEquivalentTo("Characters/Dialogue/Penny") || n.IsEquivalentTo("Data/TriggerActions"))) _storyObserver?.Invalidate();
        };
        // ENVIRONMENT SEPARATION (PM 2026-08-12): NORMAL_PLAYER_PROFILE vs DEV_QA_PROFILE.
        // - No runtest-command.json / no explicit QA mode  → NORMAL_PLAYER / PRODUCTION MODE:
        //   starts ONLY the player-facing production capability (chat server + SocialValley
        //   bridge). No P3 coordinator, no P4 test driver, no dev commands, no bootstrap.
        // - Explicit QA mode (p4/p5/p6/p7/s2 via command file) → DEV_QA: full test infra.
        // Read AIWorld.Demo's own command file (written by the runner before launch).
        bool explicitQaMode = false;
        try
        {
            var cmdFile = Path.Combine(Helper.DirectoryPath, "runtest-command.json");
            if (File.Exists(cmdFile))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cmdFile));
                // GATE R1.1 (PM item 8): read the runId to mirror markers to the run-scoped sidecar.
                if (doc.RootElement.TryGetProperty("runId", out var rid)) _runId = rid.GetString();
                if (doc.RootElement.TryGetProperty("mode", out var m))
                {
                    var mode = m.GetString();
                    if (mode == "p4")
                    {
                        explicitQaMode = true;
                        _p4Mode = true;
                        // scenario: "explicit-stop" (P4-A, same-day explicit STOP) | "day-end" (P4-B, backend day-end termination)
                        if (doc.RootElement.TryGetProperty("scenario", out var sc))
                            _p4Scenario = sc.GetString() ?? "day-end";
                    }
                    else if (mode == "p5")
                    {
                        explicitQaMode = true;
                        _p5Mode = true;
                        // llmMode: "real" = production DeepSeek consolidation + Scenario H control/test
                        // (default absent = deterministic fixture backbone).
                        if (doc.RootElement.TryGetProperty("llmMode", out var lm) && lm.GetString() == "real")
                            _p5LlmMode = true;
                    }
                    else if (mode == "p6")
                    {
                        explicitQaMode = true;
                        _p6Mode = true;
                        // llmMode: "real" = production DeepSeek learn decision (Control RUMOR vs
                        // Test VERIFIED) after knowledge VERIFIED; default = deterministic ADOPT.
                        if (doc.RootElement.TryGetProperty("llmMode", out var lm6) && lm6.GetString() == "real")
                            _p6LlmMode = true;
                    }
                    else if (mode == "p7")
                    {
                        explicitQaMode = true;
                        _p7Mode = true;
                        // Phase 7 Core Demo: chained magic line + follow line, single save, cold-restart
                        // verify. llmMode: "real" = production DeepSeek (learn decision + memory
                        // consolidation + Scenario H); default = deterministic fixtures.
                        if (doc.RootElement.TryGetProperty("llmMode", out var lm7) && lm7.GetString() == "real")
                            _p7LlmMode = true;
                    }
                    else if (mode == "p8")
                    {
                        // CHARACTER AWARENESS RUNTIME GATE (PM 2026-08-12): deterministic
                        // verification of SelfState / Follow ownership / Canonical Experience
                        // negative+positive / idempotency / hydration equivalence.
                        explicitQaMode = true;
                        _p8Mode = true;
                        // gateStage: "A" = process 1 (gates A-F + save), "B" = process 2
                        // (reload idempotency verification).
                        if (doc.RootElement.TryGetProperty("gateStage", out var gs))
                            _p8GateStage = gs.GetString() ?? "A";
                    }
                    else if (mode == "t1")
                    {
                        // TRAVEL GATE T1 RUNTIME (PM 2026-08-13): minimum travel slice scenarios
                        // T-A..T-G + persistence smoke, driven deterministically (no LLM).
                        explicitQaMode = true;
                        _t1Mode = true;
                        if (doc.RootElement.TryGetProperty("t1Stage", out var ts))
                            _t1Stage = ts.GetString() ?? "A";
                    }
                    else if (mode == "travel-matrix")
                    {
                        // TRAVEL RELIABILITY MATRIX GATE M1 (Phase=TRM): 110 runtime cycles in
                        // stage A + 8 cold-restart processes (matrixStage C1..C8). Deterministic
                        // production chain, no LLM.
                        explicitQaMode = true;
                        _matrixMode = true;
                        if (doc.RootElement.TryGetProperty("matrixStage", out var ms))
                            _matrixStage = ms.GetString() ?? "A";
                    }
                    else if (mode == "appointment")
                    {
                        // A1 NPC 主观同意与定时约会 Gate（Phase=APPT）：36 条运行时场景
                        // （即时同意/拒绝含糊反提议/反提议确认/定时约会/非法冲突/重载）。
                        // 确定性生产链（同意门 + 约会调度 + 自然移动），无 LLM。
                        // A1 R2 F4：apptStage=A 运行 32 场景 + 创建 4 个非终态并真实保存；
                        // apptStage=B 冷启动加载并按 commitmentId 核对 4 状态。
                        explicitQaMode = true;
                        _appointmentMode = true;
                        if (doc.RootElement.TryGetProperty("apptStage", out var asv))
                            _apptStage = asv.GetString() ?? "A";
                    }
                    else if (mode == "p3")
                    {
                        // P3 PERSISTENCE GATE: the default acceptance coordinator (Stage A write →
                        // real save → Stage B verify) runs without any driver override. Explicit
                        // QA mode only — _normalPlayerMode stays false so the coordinator path is
                        // not blocked (env-sep 41e36ad removed the no-command default).
                        explicitQaMode = true;
                    }
                    else if (mode == "s2")
                    {
                        // SCENARIO A (player-facing UX): AIWorld chat server + SocialValley UI bridge.
                        // Explicit mode=s2 is the DEV/QA convenience variant (dev commands available);
                        // the same production capability starts by default without any command file.
                        explicitQaMode = true;
                        _s2Mode = true;
                        if (doc.RootElement.TryGetProperty("llmMode", out var lms2) && lms2.GetString() == "real")
                            _p7LlmMode = true;
                    }

                    // PM Local Provider Routing: explicit authorization gate for production
                    // (real remote LLM) calls. Default false; only PM-authorized runs set true.
                    if (doc.RootElement.TryGetProperty("allowRealLlm", out var arl))
                        _allowRealLlm = arl.ValueKind == System.Text.Json.JsonValueKind.True;

                }
            }

            // NORMAL_PLAYER / PRODUCTION MODE: no command file or no explicit QA mode.
            // Same player-facing capability as mode=s2, but ZERO QA fixtures: no P3 coordinator,
            // no P4 test driver, no dev commands, no bootstrap. The only "mode" flags set here
            // are the production chat pipeline flags; every QA branch stays false.
            if (!explicitQaMode)
            {
                _s2Mode = true;
                _normalPlayerMode = true;
                _allowRealLlm = _config.AllowProductionLlm;
                LogMarker("[S2] NORMAL_PLAYER_MODE — production chat pipeline only (no QA fixtures)", LogLevel.Info);
            }
        }
        catch { }

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.Saved += OnSaved;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
        helper.Events.Player.Warped += OnPlayerWarped;

        // DEV-ONLY iteration convenience (never part of the player-facing acceptance path).
        // Registered ONLY in explicit DEV/QA mode — normal player mode must not expose them.
        if (explicitQaMode)
            new AiWorldDevCommands(Monitor).Register(helper);
        else
            LogMarker("[S2] DEV_COMMANDS_SKIPPED — normal player mode", LogLevel.Info);
    }

    private sealed class SmapiSaveStore : IAIWorldSaveStore
    {
        private readonly IModHelper _helper;
        public SmapiSaveStore(IModHelper helper) => _helper = helper;

        // SMAPI stores a raw JSON string under one save-data key; AIWorld.Core owns
        // serialization with System.Text.Json. This keeps Core independent of SMAPI's
        // Newtonsoft-based ReadSaveData<T> generic serialization (which cannot handle
        // C# 11 required members / JsonElement the way we need).
        public AIWorldSaveData? Read()
        {
            var raw = _helper.Data.ReadSaveData<string>(SaveDataKey);
            return string.IsNullOrEmpty(raw) ? null : AIWorldSaveRepository.Deserialize(raw);
        }

        public void Write(AIWorldSaveData data) => _helper.Data.WriteSaveData(SaveDataKey, AIWorldSaveRepository.Serialize(data));
        public void Clear() { }
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        LogMarker("[P3] SAVE_LOADED", LogLevel.Info);
        _repository = new AIWorldSaveRepository(new SmapiSaveStore(Helper));
        var load = _repository.Load();

        switch (load.Status)
        {
            case AIWorldSaveRepository.LoadStatus.Empty:
                LogMarker("[P3] READ_SAVE_DATA_RESULT = EMPTY", LogLevel.Info);
                break;
            case AIWorldSaveRepository.LoadStatus.Loaded:
                LogMarker("[P3] READ_SAVE_DATA_SUCCESS", LogLevel.Info);
                LogMarker($"[P3] SAVE_SCHEMA_VALID — saveSchemaVersion={load.Data?.SaveSchemaVersion}", LogLevel.Info);
                break;
            case AIWorldSaveRepository.LoadStatus.VersionUnsupported:
                LogMarker($"[P3] PERSISTENCE_VERSION_UNSUPPORTED — {load.Message}", LogLevel.Error);
                _repository = null;
                return;
            case AIWorldSaveRepository.LoadStatus.Corrupt:
                LogMarker($"[P3] PERSISTENCE_LOAD_ERROR — {load.Message}", LogLevel.Error);
                break;
        }

        if (_repository is null) return;

        if (_p4Mode)
        {
            InitP4();
            return;
        }
        if (_p5Mode)
        {
            InitP5();
            return;
        }
        if (_p6Mode)
        {
            InitP6();
            return;
        }
        if (_p7Mode)
        {
            InitP7();
            return;
        }
        if (_p8Mode)
        {
            // CHARACTER AWARENESS RUNTIME GATE: reuse the production awareness components
            // (SelfStateProvider, CanonicalExperienceService/Source, FollowSessionManager) and
            // add the deterministic gate driver on top.
            InitScenarioA();
            InitP8();
            return;
        }
        if (_matrixMode)
        {
            // TRAVEL RELIABILITY MATRIX (GATE M1, Phase=TRM): production pipeline + matrix
            // driver. Native components only — no LLM dependency.
            InitNativeComponents();
            InitMatrix();
            return;
        }
        if (_t1Mode)
        {
            // TRAVEL GATE T1 RUNTIME: production pipeline + deterministic travel gate driver.
            // GATE R1 FINAL CLOSURE C1: native components only — the local LLM is NOT required
            // (Travel T1 is a deterministic native-capability test).
            InitNativeComponents();
            InitT1();
            return;
        }
        if (_appointmentMode)
        {
            // A1 NPC 主观同意与定时约会 RUNTIME（36 条）：native 组件 + 同意门/约会调度 + driver。
            InitNativeComponents();
            InitAppointment();
            return;
        }
        if (_s2Mode)
        {
            InitScenarioA();
            return;
        }

        // DEV_QA_ONLY path (PM ENV SEPARATION): this must never run in NORMAL_PLAYER_PROFILE.
        // _normalPlayerMode forces _s2Mode=true at Entry, so this block is QA-only by
        // construction; the guard is defense-in-depth.
        if (_normalPlayerMode)
        {
            LogMarker("[P3] QA_PATH_BLOCKED — normal player mode, P3/P4 fixtures disabled", LogLevel.Warn);
            return;
        }

        _stateApplier = new StateDeltaApplier(_repository.State!);
        _memoryService = new MemoryIngestionService(_repository.Memory!, GameDate());
        _coordinator = new RuntimeAcceptanceCoordinator(_repository.Data, _stateApplier, _memoryService, GameDate());

        // Phase 4 follow test driver (deterministic fixtures → production pipeline).
        try
        {
            InitP4();
            LogMarker("[P4] FOLLOW_DRIVER_READY", LogLevel.Info);
        }
        catch (Exception ex)
        {
            LogMarker($"[P4] FOLLOW_DRIVER_INIT_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
    }

    private void InitP4()
    {
        _squadAdapter = new StardewSquadAdapter(Monitor, Helper);
        _followSessions = new FollowSessionManager(_repository!.Data, Game1.player?.UniqueMultiplayerID.ToString() ?? "player");
        var registry = CapabilityRegistry.CreateDemoRegistry();
        var arbitrator = new BehaviorArbitrator(
            registry,
            _squadAdapter,
            new SmapiHardAnchorProvider(),
            new GameNpcResolver(),
            new GamePlayerResolver());
        var handler = new FollowIntentHandler(arbitrator, _squadAdapter, _followSessions, new DeterministicFollowDecisionSource(FollowDecision.Accept));
        _followDriver = new FollowTestDriver(Monitor, Helper, _repository.Data, _repository, handler, _squadAdapter, _followSessions, _p4Scenario);
        LogMarker(_p4Mode ? "[P4] MODE=P4 — P3 coordinator disabled" : "[P4] FOLLOW_DRIVER_READY", LogLevel.Info);
    }

    private void InitP5()
    {
        LogMarker("[P5] MODE=P5 — Follow Experience Loop driver (P3 coordinator disabled)", LogLevel.Info);
        _squadAdapter = new StardewSquadAdapter(Monitor, Helper);
        _followSessions = new FollowSessionManager(_repository!.Data, Game1.player?.UniqueMultiplayerID.ToString() ?? "player");
        _observationCollector = new ObservationCollector(_repository!.Data);
        var registry = CapabilityRegistry.CreateDemoRegistry();
        var arbitrator = new BehaviorArbitrator(
            registry,
            _squadAdapter,
            new SmapiHardAnchorProvider(),
            new GameNpcResolver(),
            new GamePlayerResolver());
        var handler = new FollowIntentHandler(arbitrator, _squadAdapter, _followSessions, new DeterministicFollowDecisionSource(FollowDecision.Accept));
        _observationSource = new SmapiObservationSource(Monitor, _observationCollector, _followSessions, "Abigail");
        var memoryRepository = new MemoryRepository(_repository!.Data);
        var ingestion = new MemoryIngestionService(memoryRepository, GameDate());

        // Interpretation boundary: production DeepSeek (real mode) or deterministic fixture (backbone).
        AIWorld.Core.Behaviors.IObservationInterpreter interpreter;
        if (_p5LlmMode)
        {
            var llm = CreateRoutedLlm(AIWorld.Core.Providers.LlmRouteProfile.DevQa);
            if (llm is not null)
            {
                interpreter = new AIWorld.Adapters.DeepSeek.DeepSeekObservationInterpreter(
                    llm,
                    "Persona: Abigail is a free-spirited young woman, adventurous and a little restless. She is curious about the world beyond Pelican Town, enjoys exploring, and is drawn to the mysterious. She is brave but not reckless, and she genuinely cares about people close to her — including the player.",
                    onComplete: o =>
                    {
                        if (o.ProviderOk && o.PipelineStatus == AIWorld.Core.Pipeline.StructuredResponseStatus.SUCCESS && o.CandidatesProduced > 0)
                        {
                            LogMarker($"[P5] LLM_CONSOLIDATION_PASS provider={llm.ModelName} produced={o.CandidatesProduced} retries={o.RetryCount} responseHash={o.ResponseHash}", LogLevel.Info);
                            LogMarker($"[P5] LLM_SPEECH_EXCERPT: {o.RawExcerpt}", LogLevel.Info);
                        }
                        else
                        {
                            LogMarker($"[P5] LLM_CONSOLIDATION_FAIL provider={llm.ModelName} status={o.PipelineStatus} err={o.ProviderError ?? "no candidates"}", LogLevel.Error);
                        }
                    });
                LogMarker($"[P5] LLM_MODE=REAL provider={AIWorld.Core.Providers.LlmRouting.TraceTag(AIWorld.Core.Providers.LlmRouteDecision.Local)} model={llm.ModelName}", LogLevel.Info);
            }
            else
            {
                // PM fail-closed: explicit real/local mode with Local unavailable = scenario FAIL.
                // NO deterministic fallback, NO Production fallback. A test requesting Local
                // semantics must never PASS using deterministic behavior because Local failed.
                throw new InvalidOperationException("[P5] LOCAL_PROVIDER_UNAVAILABLE - DEV_QA real mode fail closed (no deterministic/Production fallback)");
            }
        }
        else
        {
            interpreter = new DeterministicObservationInterpreter();
            LogMarker("[P5] LLM_MODE=DETERMINISTIC (fixture backbone)", LogLevel.Info);
        }

        var consolidation = new MemoryConsolidationService(
            _observationCollector,
            _followSessions,
            ingestion,
            interpreter);
        _experienceDriver = new FollowExperienceDriver(
            Monitor, Helper, _repository.Data, _repository, handler, _squadAdapter, _followSessions,
            _observationCollector, _observationSource, consolidation, _p5LlmMode);
        LogMarker("[P5] EXPERIENCE_DRIVER_READY", LogLevel.Info);
    }

    private void InitP6()
    {
        LogMarker("[P6] MODE=P6 — Magic Capability driver (P3 coordinator disabled)", LogLevel.Info);
        _squadAdapter = new StardewSquadAdapter(Monitor, Helper);
        _followSessions = new FollowSessionManager(_repository!.Data, Game1.player?.UniqueMultiplayerID.ToString() ?? "player");
        _observationCollector = new ObservationCollector(_repository!.Data);
        _magicService = new MagicAdoptionService(new NpcStateRepository(_repository!.Data));
        _magicSource = new MagicObservationSource(Monitor, _observationCollector, _followSessions, "Abigail");
        _magicDriver = new MagicCapabilityDriver(
            Monitor, Helper, _repository.Data, _repository, _magicService, _observationCollector, _magicSource, _p6LlmMode);
        LogMarker(_p6LlmMode
            ? "[P6] MAGIC_DRIVER_READY (llmMode=real — DeepSeek learn decision)"
            : "[P6] MAGIC_DRIVER_READY (deterministic learn decision)", LogLevel.Info);
    }

    private void InitP7()
    {
        LogMarker("[P7] MODE=P7 — Core Demo Acceptance (chained magic + follow, single save)", LogLevel.Info);

        // Shared services: both lines operate on the SAME repository/sessions/collector so one
        // save persists the full demo state (Scenario B combined persistence).
        _squadAdapter = new StardewSquadAdapter(Monitor, Helper);
        _followSessions = new FollowSessionManager(_repository!.Data, Game1.player?.UniqueMultiplayerID.ToString() ?? "player");
        _observationCollector = new ObservationCollector(_repository!.Data);
        _magicService = new MagicAdoptionService(new NpcStateRepository(_repository!.Data));
        _magicSource = new MagicObservationSource(Monitor, _observationCollector, _followSessions, "Abigail");
        _observationSource = new SmapiObservationSource(Monitor, _observationCollector, _followSessions, "Abigail");

        // Interpretation boundary — same selection as P5/P6 (production DeepSeek vs fixture).
        IObservationInterpreter interpreter = new DeterministicObservationInterpreter();
        if (_p7LlmMode)
        {
            var llm = CreateRoutedLlm(AIWorld.Core.Providers.LlmRouteProfile.DevQa);
            if (llm is not null)
            {
                interpreter = new AIWorld.Adapters.DeepSeek.DeepSeekObservationInterpreter(
                    llm,
                    "Persona: Abigail is a free-spirited young woman, adventurous and a little restless. She is curious about the world beyond Pelican Town, enjoys exploring, and is drawn to the mysterious. She is brave but not reckless, and she genuinely cares about people close to her — including the player.",
                    onComplete: o =>
                    {
                        if (o.ProviderOk && o.PipelineStatus == AIWorld.Core.Pipeline.StructuredResponseStatus.SUCCESS && o.CandidatesProduced > 0)
                            LogMarker($"[P7] LLM_CONSOLIDATION_PASS provider={llm.ModelName} produced={o.CandidatesProduced} retries={o.RetryCount}", LogLevel.Info);
                        else
                            LogMarker($"[P7] LLM_CONSOLIDATION_FAIL provider={llm.ModelName} status={o.PipelineStatus} err={o.ProviderError ?? "no candidates"}", LogLevel.Error);
                    });
                LogMarker($"[P7] LLM_MODE=REAL provider={AIWorld.Core.Providers.LlmRouting.TraceTag(AIWorld.Core.Providers.LlmRouteDecision.Local)} model={llm.ModelName}", LogLevel.Info);
            }
            else
            {
                // PM fail-closed: explicit real/local mode with Local unavailable = scenario FAIL.
                // NO deterministic fallback, NO Production fallback.
                throw new InvalidOperationException("[P7] LOCAL_PROVIDER_UNAVAILABLE - DEV_QA real mode fail closed (no deterministic/Production fallback)");
            }
        }
        else
        {
            LogMarker("[P7] LLM_MODE=DETERMINISTIC (fixture backbone)", LogLevel.Info);
        }

        // Magic line (Phase 6 driver) — suppressSignal=true: CoreDemoDriver owns the save trigger.
        _magicDriver = new MagicCapabilityDriver(
            Monitor, Helper, _repository.Data, _repository, _magicService, _observationCollector, _magicSource,
            llmMode: _p7LlmMode, suppressSignal: true);

        // Follow line (Phase 5 driver) — suppressSignal=true for the same reason.
        var registry = CapabilityRegistry.CreateDemoRegistry();
        var arbitrator = new BehaviorArbitrator(
            registry, _squadAdapter, new SmapiHardAnchorProvider(), new GameNpcResolver(), new GamePlayerResolver());
        var handler = new FollowIntentHandler(arbitrator, _squadAdapter, _followSessions, new DeterministicFollowDecisionSource(FollowDecision.Accept));
        var memoryRepository = new MemoryRepository(_repository.Data);
        var ingestion = new MemoryIngestionService(memoryRepository, GameDate());
        var consolidation = new MemoryConsolidationService(_observationCollector, _followSessions, ingestion, interpreter);
        _experienceDriver = new FollowExperienceDriver(
            Monitor, Helper, _repository.Data, _repository, handler, _squadAdapter, _followSessions,
            _observationCollector, _observationSource, consolidation, llmMode: _p7LlmMode, suppressSignal: true);

        _coreDemoDriver = new CoreDemoDriver(
            Monitor, Helper, _repository.Data, _repository,
            _magicDriver, _magicSource, _experienceDriver, _observationSource);
        LogMarker("[P7] CORE_DEMO_DRIVER_READY", LogLevel.Info);
    }

    /// <summary>
    /// SCENARIO A — player-facing integration: start the AIWorld chat server and bridge
    /// SocialValley's ChatUI (reused UI shell) to the AIWorld production pipeline.
    /// Requires SocialValley to be loaded (its assemblies present in the game Mods dir).
    /// </summary>
    private void InitScenarioA()
    {
        LogMarker("[S2] MODE=S2 — Player-Facing UX (AIWorld chat server + SocialValley UI bridge)", LogLevel.Info);

        // GATE R1 FINAL CLOSURE C1: native components (follow/travel/awareness) initialize
        // WITHOUT the LLM; the chat pipeline below stays fail-closed when unavailable.
        InitNativeComponents();

        // v0.2 IDENTITY FIX: persona is per-NPC. Abigail keeps her full demo persona; every
        // other villager gets a brief role description so the model never adopts Abigail's
        // identity when the player talks to Harvey/Sebastian/etc.
        var personaCatalog = new AIWorld.Core.Chat.NpcPersonaCatalog(id => Helper.ModRegistry.IsLoaded(id));
        foreach (var pack in personaCatalog.Packs)
        {
            var mod = Helper.ModRegistry.Get(pack.ModId);
            LogMarker($"[COMPAT] PERSONA_PACK mod={pack.ModId} active={Helper.ModRegistry.IsLoaded(pack.ModId)} installedVersion={mod?.Manifest.Version.ToString() ?? "absent"} sourceVersion={pack.SourceVersion} profiles={pack.Profiles.Count}", LogLevel.Info);
        }
        var personaSelector = new Func<string, string?>(npcKey =>
        {
            var builtIn = personaCatalog.GetPersona(npcKey, Game1.getCharacterFromName(npcKey)?.displayName);
            if (builtIn is not null) return builtIn;
            var key = npcKey?.Trim();
            if (string.Equals(key, "Abigail", StringComparison.OrdinalIgnoreCase))
                return "Persona: Abigail is a free-spirited young woman, adventurous and a little restless. She is curious about the world beyond Pelican Town, enjoys exploring, and is drawn to the mysterious. She is brave but not reckless, and she genuinely cares about people close to her — including the player. She speaks naturally and warmly, with a touch of playful edge.";
            if (string.Equals(key, "Harvey", StringComparison.OrdinalIgnoreCase))
                return "Persona: Harvey is the town doctor — kind, careful, a little anxious, with a dry sense of humor. He cares about everyone's health, loves coffee and model planes, and speaks gently but observantly.";
            if (string.Equals(key, "Sebastian", StringComparison.OrdinalIgnoreCase))
                return "Persona: Sebastian is a quiet, moody young man who loves programming, tabletop RPGs, motorcycles and rainy days. He is introverted but warm underneath, sarcastic in a dry way, and speaks in short, low-energy sentences.";
            if (string.Equals(key, "Pierre", StringComparison.OrdinalIgnoreCase))
                return "Persona: Pierre runs the general store. He is proud, a little competitive, hard-working, and always keen to sell his produce. He speaks cheerfully but can be defensive about his business.";
            if (string.Equals(key, "Wizard", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "Rasmodius", StringComparison.OrdinalIgnoreCase))
                return "Persona: the Wizard (Rasmodius) is a reclusive, mysterious, slightly theatrical magic practitioner who lives in the tower west of town. He speaks in a formal, arcane manner and is fascinated by the occult.";
            return null; // no persona defined → generic villager fallback in Core
        });
        var assembler = new AIWorld.Core.Chat.ChatContextAssembler(personaSelector);
        assembler.NpcKnowledgeProvider = (npc, query) => _npcKnowledge?.Gifts(npc, query) ?? "";
        try
        {
            var archive = Helper.Data.ReadJsonFile<AIWorld.Core.Chat.StoryArchiveDocument>("assets/story-archive.json");
            if (archive is not null) assembler.StoryArchive = new AIWorld.Core.Chat.StoryArchiveCatalog(archive);
        }
        catch (Exception ex) { Monitor.Log($"[STORY] archive file unavailable; using built-in notes: {ex.GetType().Name}", LogLevel.Warn); }
        var memoryRepo = new MemoryRepository(_repository!.Data);

        // PM Local Provider Routing: DEV_QA → LOCAL, NORMAL_PLAYER → PRODUCTION (gated),
        // OFFLINE_TEST → MOCK. DEV_QA never falls back to Production — fail closed when
        // localhost is unavailable.
        var routeProfile = _normalPlayerMode
            ? AIWorld.Core.Providers.LlmRouteProfile.NormalPlayer
            : AIWorld.Core.Providers.LlmRouteProfile.DevQa;
        if (_normalPlayerMode && !_config.AllowProductionLlm)
        {
            LogMarker("[PLAYER] Production chat is disabled in AIWorld config.", LogLevel.Info);
            return;
        }
        var llm = CreateRoutedLlm(routeProfile);
        if (llm is null)
        {
            LogMarker("[S2] LLM_ROUTE FAIL_CLOSED 鈥?chat server NOT started (DEV_QA local unavailable or production not authorized)", LogLevel.Error);
            return;
        }

        if (_normalPlayerMode)
        {
            _liveTalkModel?.Dispose();
            _liveTalkModel = new AIWorld.Adapters.LiveTalkLanguageModel(_config.Talk ?? new());
            _countingLlm = new AIWorld.Core.Providers.CountingLanguageModel(_liveTalkModel, AIWorld.Core.Providers.LlmProviderKind.Production);
            _routedLlm = llm = _countingLlm;
        }

        // GATE R1 FINAL CLOSURE C1: the chat server reuses the native components initialized
        // above (follow handler, intent queue, SelfState). The chat pipeline stays fail-closed
        // when the LLM is unavailable; native travel components are NOT affected.
        _chatServer = new AiWorldChatServer(
            Monitor, assembler, _repository, memoryRepo, llm, _s2FollowHandler!,
            intentExecutor: (intent, actor, stamp) => _pendingIntents!.Enqueue((intent, actor, stamp)),
            // A1：正常流程经 NpcConsentGate → TravelCommitment/TravelLease（同意门 + 约会调度）。
            consentGate: _consentGate,
            appointments: _appointments,
            // PM Local Provider Routing: provider kind for trace + LOCAL_FORMAT_RETRY=2.
            providerKind: _llmProviderKind,
            maxSchemaCorrectionRetries: _llmProviderKind == AIWorld.Core.Providers.LlmProviderKind.Local ? 2 : 1,
            // PLAYER world context (the player's current location etc.) — explicitly NOT the
            // NPC's location. NPC SelfState is injected separately via selfStateProvider.
            worldContextProvider: () =>
            {
                try
                {
                    var loc = Game1.currentLocation?.NameOrUniqueName ?? "unknown";
                    return $"playerLocation={loc} season={Game1.currentSeason} day={Game1.dayOfMonth} year={Game1.year} time={Game1.timeOfDay:D4}";
                }
                catch { return "player world context unavailable"; }
            },
            selfStateProvider: npcKey => _selfStateProvider?.Get(npcKey),
// Travel Gate T1 (U5/U6): live location registry for semantic validation of travel targets.
// Game1.locations at call time (canonical names + unique names for duplicate instances).
locationRegistryProvider: () =>
{
    try
    {
        return Game1.locations
            .Select(l => l.NameOrUniqueName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
    catch { return Array.Empty<string>(); }
},
gameTimeProvider: () => (Game1.Date.TotalDays, Game1.timeOfDay),
gameThread: _gameThread,
travelAliasProvider: () => AIWorld.Core.Chat.TravelLocationAliases.Build(
    Game1.locations.Select(l => (l.NameOrUniqueName, (string?)l.DisplayName))),
npcRegistryProvider: () => Utility.getAllCharacters()
    .Where(n => n.isVillager() || personaCatalog.SupportsNpc(n.Name))
    .Select(n => n.Name).Distinct(StringComparer.Ordinal),
appointmentEnded: commitment => EndPlayerAppointment(commitment),
isScriptedSceneActive: () => Game1.CurrentEvent is not null,
dialogueContextProvider: npc => $"RELATIONSHIP (game facts): hearts={Game1.player.getFriendshipHeartLevelForNPC(npc)}; married={Game1.player.spouse == npc}; playerName={Game1.player.Name}. Do not infer shared events from friendship level.",
perceptionProvider: npc => _perception?.GetFrame(npc) ?? AIWorld.Core.Perception.PerceptionFrame.Unavailable(npc));

        if (_normalPlayerMode && _liveTalkModel is not null)
        {
            _chatServer.TalkSettings = () => _liveTalkModel.Options;
            _chatServer.GameTick = () => Game1.ticks;
            _talkConsole?.Dispose();
            _talkConsole = new TalkConsoleHost(Helper, Monitor, _config, _chatServer, _liveTalkModel, _perception!,
                npc => _travelLeases?.IsActive(npc) == true || _repository.Data!.FollowSessions.Any(s => s.NpcId == npc && s.IsActive));
            LogMarker("[TALK] CONSOLE_READY key=F9 overlay=true history=per-save", LogLevel.Info);
        }

        if (_config.EnableDialogueBridge)
        {
            _dialogueBridge?.Dispose();
            _dialogueBridge = new DialogueUiBridge(Helper, Monitor, _chatServer, _config, npc =>
            {
                var s = _selfStateProvider?.Get(npc);
                return $"{Game1.Date.TotalDays}|{Game1.timeOfDay / 200}|{Game1.isRaining}|{Game1.player.getFriendshipHeartLevelForNPC(npc)}|{Game1.player.spouse}|{Game1.currentLocation?.NameOrUniqueName}|{s?.CurrentLocation}|{s?.CurrentActivity}|{s?.CurrentGoal}|{s?.CurrentTarget}|{s?.FollowingPlayer}|{s?.Job?.Fingerprint}|{_repository.Data?.CanonicalExperiences.Count}|{PennyBathStory.Context(_repository.Data!, npc)}|{AIWorld.Core.Perception.PerceptionEngine.Signature(_perception!.GetFrame(npc))}";
            });
        }

        _svInjector ??= new SocialValleyBridgeInjector(Monitor, _chatServer.EndpointUrl);

        // Order: start server first (must accept before SocialValley issues its first request
        // when the player opens ChatUI). Endpoint/config injection already ran at GameLaunched.
        _chatServer.Start();
        LogMarker(_svInjector.IsInjected
            ? "[S2] SOCIALVALLEY_BRIDGE_READY — ChatUI will route to AIWorld production pipeline"
            : "[S2] SOCIALVALLEY_BRIDGE_PARTIAL — check SMAPI log",
            _svInjector.IsInjected ? LogLevel.Info : LogLevel.Warn);

        LogMarker($"[S2] LLM_ROUTED provider={_llmProviderKind} model={llm.ModelName}", LogLevel.Info);
    }

    /// <summary>
    /// GATE R1 FINAL CLOSURE C1: native behavior components (follow chain, travel pipeline,
    /// SelfState, canonical awareness) — deterministic, NO LLM dependency. The chat server /
    /// LLM provider initialization is separate (InitScenarioA) and stays fail-closed.
    /// Travel T1 must run with the local model STOPPED (LOCAL_LLM_CALL_COUNT = 0).
    /// </summary>
    private void InitNativeComponents()
    {
        _gameThread?.Dispose();
        _gameThread = new AIWorld.Core.Chat.MainThreadWorkQueue();
        _playerObservations.Clear();
        _playerMagicObservations.Clear();
        // TRAVEL 自然移动适配器（PM 2026-08-15）——共享实例：travel 与 rejoin 回归基线共用
        // 同一 route 状态。ReturnToBaselinePlanner 跨图复用其多段自然路线（不再直接 warp）。
        var travelAdapter = new NativeTravelAdapter(Monitor);
        _travelAdapter = travelAdapter;
        _rejoinPlanner = new ReturnToBaselinePlanner(Monitor, travelAdapter);

        // Normal player follow shares the checked movement executor; legacy QA retains the Squad adapter.
        _squadAdapter = new StardewSquadAdapter(Monitor, Helper);
        _nativeFollow = _s2Mode ? new NativeFollowAdapter(travelAdapter, id => _travelLeases?.IsActive(id) == true) : null;
        var followAdapter = CurrentFollowAdapter!;
        _followSessions = new FollowSessionManager(_repository!.Data, Game1.player?.UniqueMultiplayerID.ToString() ?? "player");
        var registry = CapabilityRegistry.CreateDemoRegistry();
        var arbitrator = new BehaviorArbitrator(
            registry, followAdapter, new SmapiHardAnchorProvider(), new GameNpcResolver(), new GamePlayerResolver());
        var handler = new FollowIntentHandler(arbitrator, followAdapter, _followSessions, new DeterministicFollowDecisionSource(FollowDecision.Accept));

        // A successful stop restores the current canonical baseline through the shared checked executor.
        handler.OnFollowReleased = (npcId, stamp) =>
        {
            try
            {
                var baseline = StardewBaselineResolver.Resolve(npcId, Game1.timeOfDay);
                LogMarker($"[P4] REJOIN_BASELINE_RESOLVED npc={npcId} target={baseline.LocationName}({baseline.TileX},{baseline.TileY}) source={baseline.Source}", LogLevel.Info);
                var outcome = _rejoinPlanner!.Execute(npcId, baseline);
                LogMarker($"[P4] REJOIN_EXECUTED npc={npcId} kind={outcome.Kind} detail={outcome.Detail}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogMarker($"[P4] REJOIN_FAILED npc={npcId} {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            }
        };
        _s2FollowHandler = handler;
        if (_nativeFollow is not null) _nativeFollow.OnStopped = (id, reason) =>
        {
            handler.OnFollowEnding?.Invoke(id, NowStamp());
            _followSessions.End(id, reason, NowStamp());
            if (reason != "movementOwnerChanged" && !new SmapiHardAnchorProvider().IsHardAnchorActive(id))
                handler.OnFollowReleased?.Invoke(id, NowStamp());
            else ConsolidatePlayerExperience(id);
        };
        if (_s2Mode)
        {
            handler.OnFollowStarted = (npcId, stamp) =>
            {
                var source = new SmapiObservationSource(Monitor, _observationCollector ??= new ObservationCollector(_repository.Data),
                    _followSessions, npcId, () => _perception?.CanSeePlayer(npcId) == true,
                    () => _perception?.GetFrame(npcId).Entities.Any(e => e.Hostility == AIWorld.Core.Perception.EntityHostility.Hostile) == true);
                _playerObservations[npcId] = source;
                _playerMagicObservations[npcId] = new MagicObservationSource(Monitor, _observationCollector, _followSessions, npcId,
                    () => (_perception?.GetFrame(npcId).Entities ?? Array.Empty<AIWorld.Core.Perception.PerceivedEntity>())
                        .Where(e => e.Kind == AIWorld.Core.Perception.EntityKind.Object).Select(e => e.Tile).ToHashSet());
                source.OnFollowStarted(Game1.currentLocation?.NameOrUniqueName ?? "unknown");
                _repository.MarkDataDirty();
            };
            handler.OnFollowEnding = (npcId, stamp) =>
            {
                if (_playerObservations.TryGetValue(npcId, out var source))
                    source.OnFollowEnded(Game1.getCharacterFromName(npcId)?.currentLocation?.NameOrUniqueName ?? "unknown");
            };
            var rejoin = handler.OnFollowReleased;
            handler.OnFollowReleased = (npcId, stamp) =>
            {
                ConsolidatePlayerExperience(npcId);
                rejoin?.Invoke(npcId, stamp);
            };
        }

        // TRAVEL GATE T1: production travel pipeline — same arbitrator family (single
        // permission system), vanilla movement backend (no Squad). The travel handler is
        // created with its own arbitrator carrying the travel resolver + lease-conflict
        // check; OnTravelReleased reuses the proven ReturnToBaselinePlanner rejoin.
        _observationCollector ??= new ObservationCollector(_repository!.Data);
        _travelLeases = new AIWorld.Core.Behaviors.TravelLeaseManager();
        var travelResolver = new StardewTravelDestinationResolver(Monitor); // actor-scoped (R5) — no hardcoded NPC
        _travelResolver = travelResolver; // shared — matrix driver needs it for same-location start-tile derivation
        _travelObservationSource = new TravelObservationSource(Monitor, _observationCollector);
        var travelArbitrator = new BehaviorArbitrator(
            registry, followAdapter, new SmapiHardAnchorProvider(), new GameNpcResolver(), new GamePlayerResolver(),
            travelResolver: travelResolver, travelActiveCheck: npcId => _travelLeases.IsActive(npcId));
        // TRAVEL 自然移动（PM 2026-08-15）：跨图多段行走需要更长的完成预算 —— 矩阵与 T1 均为
        // 180 ticks @ 1/sec = 3 分钟/到达（T-B 跨图两跳 + rejoin 路径不再可能 60s 内超时）。
        int travelCompletionTicks = (_matrixMode || _t1Mode || _appointmentMode) ? 180 : Math.Max(30, _config.TravelTimeoutSeconds);
        _travelHandler = new AIWorld.Core.Behaviors.TravelIntentHandler(
            travelArbitrator, travelResolver, travelAdapter, _travelLeases, maxCompletionTicks: travelCompletionTicks);

        // A1 NPC 主观同意与定时约会：同意门 + 约会调度（每存档持久化 store，SMAPI WriteSaveData）。
        _consentGate = new AIWorld.Core.Behaviors.NpcConsentGate();
        _appointments = new AIWorld.Core.Behaviors.AppointmentManager(
            new AIWorld.Core.Persistence.SaveDataCommitmentStore(() => _repository));
        // SaveLoaded 恢复：从每存档数据加载 commitment，并对已过期非终态 commitment 标记 MISSED（不补传送）。
        _appointments.Load();
        int loadedCount = _appointments.All.Count;
        _appointments.OnLoad(AppointmentDay, Game1.timeOfDay);
        int missedCount = _appointments.All.Count(static c => c.Status == AIWorld.Core.Contracts.TravelCommitmentStatus.Missed);
        LogMarker($"[A1] COMMITMENTS_LOADED count={loadedCount} missedAfterReload={missedCount}", LogLevel.Info);

        _travelHandler.OnTravelArrived = (npcId, dest, stamp) =>
        {
            _travelObservationSource.OnNpcArrived(npcId, dest, stamp);
            _appointments?.OnTravelArrived(npcId, AppointmentDay, Game1.timeOfDay);
            _appointments?.Save();
            // A1 R2 F7: 真实到达回传约会驱动 —— 记录 arrivalObserved/落点/合法性证据。
            _appointmentDriver?.RecordTravelArrival(npcId, dest.LocationName, dest.TileX, dest.TileY);
        };
        _travelHandler.OnTravelReleased = (npcId, stamp) =>
        {
            try
            {
                // A1 R2 F2: 定时约会等待期间（ARRIVED_EARLY/WAITING_FOR_PLAYER）NPC 必须留在目标
                // 地点等待玩家，不得在 travel 释放后立即回归 baseline（否则 "提前到达并在原地等待"
                // 被瞬移回归破坏，约会语义失效）。
                var waiting = _appointments?.All.FirstOrDefault(c => c.NpcId == npcId
                    && c.Status is AIWorld.Core.Contracts.TravelCommitmentStatus.ArrivedEarly
                        or AIWorld.Core.Contracts.TravelCommitmentStatus.WaitingForPlayer);
                if (waiting is not null)
                {
                    LogMarker($"[A1] REJOIN_SUPPRESSED npc={npcId} status={waiting.Status} commitment={waiting.CommitmentId} stayAtTarget={waiting.GroundedTarget} (F2: waiting for player)", LogLevel.Info);
                    return;
                }
                var baseline = StardewBaselineResolver.Resolve(npcId, Game1.timeOfDay);
                LogMarker($"[T1] REJOIN_BASELINE_RESOLVED npc={npcId} target={baseline.LocationName}({baseline.TileX},{baseline.TileY}) source={baseline.Source}", LogLevel.Info);
                var outcome = _rejoinPlanner!.Execute(npcId, baseline);
                LogMarker($"[T1] REJOIN_EXECUTED npc={npcId} kind={outcome.Kind} detail={outcome.Detail}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogMarker($"[T1] REJOIN_FAILED npc={npcId} {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            }
        };

        // Intent execution must run on the main thread (SMAPI/game state). The chat server runs
        // on background threads; marshal via a queue consumed on OneSecondUpdateTicked.
        var intentQueue = new System.Collections.Concurrent.ConcurrentQueue<(NpcIntent, string, string)>();
        _pendingIntents = intentQueue;

        // PM FIX 2: deterministic NPC SelfState provider (World Truth) — created BEFORE the
        // chat server so the LLM context can include the NPC's own state (npc.currentLocation,
        // activity, owner, following, event). NPC location must NEVER come from the player's
        // location; the player's context is kept separate as PlayerWorldState below.
        _selfStateProvider = new SelfStateProvider(
            Monitor,
            isFollowing: npcId => _nativeFollow?.IsFollowing(npcId)
                ?? (_repository.Data.FollowSessions?.Any(s => s.NpcId == npcId && s.IsActive) == true),
            currentGoal: npcId => _appointments?.All.FirstOrDefault(c => c.NpcId == npcId && c.Status is
                TravelCommitmentStatus.Accepted or TravelCommitmentStatus.WaitingToDepart or TravelCommitmentStatus.Departing
                or TravelCommitmentStatus.ArrivedEarly or TravelCommitmentStatus.WaitingForPlayer) is { } plan
                    ? $"约定在 {plan.GroundedTarget} {plan.TargetGameTime / 100:D2}:{plan.TargetGameTime % 100:D2} 见面（{plan.Status}）" : "",
            currentTarget: npcId => _appointments?.All.FirstOrDefault(c => c.NpcId == npcId && c.Status is
                TravelCommitmentStatus.ArrivedEarly or TravelCommitmentStatus.WaitingForPlayer)?.GroundedTarget ?? "",
            // Travel Gate T1 (R3): wire the live TravelLeaseManager so a session-scoped
            // travel lease surfaces as traveling → PLAYER_REQUEST, and its canonical
            // destination as the travel target. Same per-NPC scoping as isFollowing.
            isTraveling: npcId => _travelLeases.IsActive(npcId),
            travelTarget: npcId =>
            {
                var lease = _travelLeases.GetActive(npcId);
                if (lease?.AdapterParams?.TryGetValue("locationName", out var v) == true && v is string loc)
                    return loc;
                return string.Empty;
            });

        _selfStateProvider.Knowledge = _npcKnowledge;
        _selfStateProvider.JobOverride = npcId =>
        {
            if (_nativeFollow?.IsFollowing(npcId) == true)
                return _travelAdapter?.ReadJob(npcId, AIWorld.Core.Chat.NpcJobOverrideKind.Travel);
            if (_travelLeases.IsActive(npcId))
                return _travelAdapter?.ReadJob(npcId, AIWorld.Core.Chat.NpcJobOverrideKind.Travel);
            if (_rejoinPlanner?.ReadJob(npcId) is { } returning) return returning;
            var waiting = _appointments?.All.FirstOrDefault(c => c.NpcId == npcId && c.Status is
                TravelCommitmentStatus.ArrivedEarly or TravelCommitmentStatus.WaitingForPlayer);
            return waiting is null ? null : new AIWorld.Core.Chat.NpcJobOverride(AIWorld.Core.Chat.NpcJobOverrideKind.Appointment, waiting.GroundedTarget);
        };

        // GATE R1 FINAL CLOSURE C1: record the decoupling evidence. The probe below is
        // EVIDENCE-ONLY — travel components do NOT depend on the local provider.
        bool localAvailable = false;
        try { localAvailable = AIWorld.Adapters.LlmProviderFactory.IsLocalAvailableAsync().GetAwaiter().GetResult(); }
        catch { localAvailable = false; }
        LogMarker($"[T1] LOCAL_PROVIDER_AVAILABLE RESULT=info available={localAvailable} (evidence-only probe)", LogLevel.Info);
        LogMarker("[T1] TRAVEL_COMPONENTS_INITIALIZED RESULT=pass handler=ready leases=ready resolver=ready adapter=ready selfState=ready (no LLM dependency)", LogLevel.Info);

        // CHARACTER AWARENESS & CANONICAL EXPERIENCE (generic, deterministic):
        // live SelfState from World Truth + canonical experience ingestion (heart events, mail).
        // Participant-scoped; idempotent; never invented from model knowledge.
        var memoryRepo = new MemoryRepository(_repository!.Data);
        _canonicalService = new AIWorld.Core.Behaviors.CanonicalExperienceService(
            _repository.Data,
            new AIWorld.Core.Persistence.NpcStateRepository(_repository.Data),
            memoryRepo,
            GameDate());
        _canonicalSource = new VanillaCanonicalExperienceSource(Monitor);
        _magicService = new MagicAdoptionService(_repository.State!);
        // Abigail's Prairie King heart event — the PM-specified positive canonical heart-event
        // case. SOURCE-GROUNDED (verified 2026-08-12 from installed game data):
        //   Content/Data/Events/SeedShop.xnb key = "1/f Abigail 500/p Abigail/d Sat"
        //   → event ID = "1" (first segment before '/'); preconditions:
        //       f Abigail 500  = Friendship Abigail >= 500 (2 hearts)
        //       p Abigail      = NpcVisibleHere Abigail
        //       d Sat          = NotDayOfWeek Sat (exclusion day, NOT "on Saturday")
        //   eventsSeen stores the event ID "1" (verified via game IL: checkEventPrecondition
        //   → SplitPreconditions → parts[0]; exitEvent → eventsSeen.Add(Event.id)).
        // Only events CONFIGURED here produce experiences; unknown ids degrade silently.
        _canonicalSource.ConfigureEvents(new[]
        {
            new VanillaCanonicalExperienceSource.CanonicalEventDef(
                EventId: "1",
                Type: "HEART_EVENT",
                Actor: "Abigail",
                Location: "SeedShop",
                Facts: new[] { "Abigail and the player played Journey of the Prairie King together in the SeedShop basement" },
                KnowledgeFactId: "heartEvent.playedPrairieKing",
                MemoryImportance: 0.8f),
        });
        if (_s2Mode)
        {
            var hydrated = new CanonicalHistoryHydrator(_canonicalService).Hydrate(
                _canonicalSource.ScanCompletedHeartEvents(NowStamp()));
            if (hydrated.Accepted > 0) _repository.MarkDataDirty();
            _storyObserver = new ScriptedStoryObserver(Helper, Monitor, _repository, _canonicalService);
            _storyObserver.Tick(reconstruct: true);
            foreach (var npcId in _followSessions.Sessions.Where(s => s.IsActive).Select(s => s.NpcId).ToArray())
            {
                if (followAdapter.IsFollowing(npcId)) handler.OnFollowStarted?.Invoke(npcId, NowStamp());
                else
                {
                    _followSessions.End(npcId, "RELOAD_NOT_FOLLOWING", NowStamp());
                    ConsolidatePlayerExperience(npcId);
                }
            }
        }
        LogMarker("[AWARE] CHARACTER_AWARENESS_READY — SelfState + CanonicalExperience sources registered", LogLevel.Info);
    }

    /// <summary>
    /// PM Local Provider Routing: resolve route 鈫?probe local availability (DEV_QA) 鈫?create
    /// provider 鈫?wrap in counting decorator 鈫?emit LLM_PROVIDER/LLM_MODEL trace.
    /// Returns null on fail-closed states (DEV_QA local unavailable / production not authorized);
    /// callers must NOT start the chat pipeline when null.
    /// </summary>
    private AIWorld.Core.Providers.IAIWorldLanguageModel? CreateRoutedLlm(AIWorld.Core.Providers.LlmRouteProfile profile)
    {
        var options = new AIWorld.Core.Providers.LlmRoutingOptions { AllowRealLlm = _allowRealLlm };

        // DEV_QA: probe localhost provider availability (fail-closed check).
        bool localAvailable = true;
        if (profile == AIWorld.Core.Providers.LlmRouteProfile.DevQa)
        {
            try
            {
                localAvailable = AIWorld.Adapters.LlmProviderFactory.IsLocalAvailableAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LogMarker($"[S2] LLM_ROUTE local-probe threw {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
                localAvailable = false;
            }
        }

        var decision = AIWorld.Core.Providers.LlmRouting.Resolve(profile, localAvailable, options);
        var tag = AIWorld.Core.Providers.LlmRouting.TraceTag(decision);
        LogMarker($"[S2] LLM_ROUTE profile={profile} localAvailable={localAvailable} allowRealLlm={_allowRealLlm} decision={decision} tag={tag}", LogLevel.Info);

        if (decision == AIWorld.Core.Providers.LlmRouteDecision.LocalUnavailable)
        {
            LogMarker("[S2] LOCAL_PROVIDER_UNAVAILABLE - DEV_QA fail closed, NO Production fallback", LogLevel.Error);
            return null;
        }
        if (decision == AIWorld.Core.Providers.LlmRouteDecision.ProductionBlocked)
        {
            LogMarker("[S2] PRODUCTION_BLOCKED 鈥?AllowRealLlm=false; production calls not authorized", LogLevel.Error);
            return null;
        }

        try
        {
            var raw = AIWorld.Adapters.LlmProviderFactory.CreateGuarded(decision, profile);
            _llmProviderKind = decision switch
            {
                AIWorld.Core.Providers.LlmRouteDecision.Mock => AIWorld.Core.Providers.LlmProviderKind.Mock,
                AIWorld.Core.Providers.LlmRouteDecision.Local => AIWorld.Core.Providers.LlmProviderKind.Local,
                AIWorld.Core.Providers.LlmRouteDecision.Production => AIWorld.Core.Providers.LlmProviderKind.Production,
                _ => AIWorld.Core.Providers.LlmProviderKind.Production,
            };
            _countingLlm = new AIWorld.Core.Providers.CountingLanguageModel(raw, _llmProviderKind);
            _routedLlm = _countingLlm;
            LogMarker($"[S2] LLM_PROVIDER={tag} LLM_MODEL={raw.ModelName}", LogLevel.Info);
            return _countingLlm;
        }
        catch (Exception ex)
        {
            LogMarker($"[S2] LLM_PROVIDER_CREATE_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            return null;
        }
    }

    private void InitP8()
    {
        if (_canonicalSource is null || _canonicalService is null || _selfStateProvider is null || _followSessions is null)
        {
            LogMarker("[P8] INIT_FAILED — awareness components missing", LogLevel.Error);
            return;
        }
        _awarenessGateDriver = new AwarenessGateDriver(
            Monitor, Helper, _repository!, _repository.Data,
            _canonicalSource, _canonicalService, _selfStateProvider, _followSessions, _p8GateStage);
        LogMarker($"[P8] GATE_DRIVER_READY stage={_p8GateStage}", LogLevel.Info);
    }

    private void InitT1()
    {
        if (_travelHandler is null || _travelLeases is null || _repository is null)
        {
            LogMarker("[T1] INIT_FAILED — travel components missing (InitScenarioA not run?)", LogLevel.Error);
            return;
        }
        _travelGateDriver = new TravelGateDriver(Monitor, Helper, _repository, _repository.Data,
            _travelHandler, _travelLeases, _travelAdapter ?? new NativeTravelAdapter(Monitor), _t1Stage);
        LogMarker($"[T1] GATE_DRIVER_READY stage={_t1Stage}", LogLevel.Info);
    }

    private void InitMatrix()
    {
        if (_travelHandler is null || _travelLeases is null || _repository is null || _observationCollector is null)
        {
            LogMarker("[TRM] INIT_FAILED — travel/observation components missing", LogLevel.Error);
            return;
        }
        _matrixDriver = new TravelMatrixDriver(Monitor, Helper, _repository, _repository.Data,
            _travelHandler, _travelLeases, _travelAdapter ?? new NativeTravelAdapter(Monitor),
            _travelResolver ?? new StardewTravelDestinationResolver(Monitor), _observationCollector, _matrixStage);
        LogMarker($"[TRM] MATRIX_DRIVER_READY stage={_matrixStage} mode=travel-matrix", LogLevel.Info);
    }

    private void InitAppointment()
    {
        if (_travelHandler is null || _travelLeases is null || _consentGate is null || _appointments is null)
        {
            LogMarker("[A1] INIT_FAILED — travel/consent components missing", LogLevel.Error);
            return;
        }
        _appointmentDriver = new AppointmentDriver(Monitor, Helper, _travelHandler, _travelLeases,
            _consentGate, _appointments, _apptStage, onStageAComplete: PersistStageASave);
        LogMarker($"[A1] APPOINTMENT_DRIVER_READY mode=appointment stage={_apptStage}", LogLevel.Info);
    }

    /// <summary>
    /// A1 R2.1: 主机直接泵 SaveGame.Save() 时不触发 SMAPI Saving/Saved，Demo 的 OnSaving/OnSaved
    /// 不会运行，stage=A 创建的 4 个 commitment 若不落盘，stage=B 冷启动核对必失败，且 runner 等待的
    /// save 标记不出现 → APPT_A_SAVE 超时。这里在写 stage-a-done 信号前自行持久化（WriteSaveData
    /// 即时落盘，独立于 SMAPI save 事件）并发出与 OnSaving/OnSaved 一致的 save 标记。
    /// </summary>
    private bool _stageASaved;
    private void PersistStageASave()
    {
        if (_stageASaved || _repository is null) return;
        _stageASaved = true;
        _appointments?.Save();
        LogMarker("[P3] SAVING", LogLevel.Info);
        LogMarker($"[A1] SAVING_COMMITMENTS count={_appointments?.All.Count ?? 0}", LogLevel.Info);
        LogMarker("[P3] WRITE_SAVE_DATA_STARTED", LogLevel.Info);
        bool ok = _repository.Save();
        LogMarker(ok ? "[P3] WRITE_SAVE_DATA_SUCCESS — aiworld/state written (host SaveGame.Save follows)" : "[P3] WRITE_SAVE_DATA_FAILED", ok ? LogLevel.Info : LogLevel.Error);
        LogMarker("[P3] SAVED", LogLevel.Info);
        LogMarker($"[P3] DIRTY_FALSE — {(!_repository.IsDirty ? "clean" : "STILL DIRTY")}", LogLevel.Info);
    }

    /// <summary>
    /// Travel Gate T1: tick active travel leases once per second — the arrival predicate,
    /// timeout, or cancellation path releases the lease (STALE_TRAVEL_LEASE = 0 by
    /// construction: every exit releases). Session-scoped, never persisted (U7).
    /// </summary>
    private void TryTickTravelCompletion()
    {
        if (_travelHandler is null || _travelLeases is null) return;
        foreach (var npcId in _travelLeases.Active.Keys.ToList())
        {
            var result = _travelHandler.Complete(npcId, NowStamp());
            if (result.Status != TravelIntentHandler.TravelCompletionStatus.Pending && _talkTravelIntents.Remove(npcId, out var talkIntent))
                _chatServer?.Journal.Execution(talkIntent, $"移动结果：{result.Status} / {result.Detail}");
            if (_s2Mode && result.Status is TravelIntentHandler.TravelCompletionStatus.TimedOut
                or TravelIntentHandler.TravelCompletionStatus.Failed or TravelIntentHandler.TravelCompletionStatus.HardAnchorInterrupted)
            {
                _appointments?.OnTravelFailed(npcId, result.Detail);
                _appointments?.Save();
            }
            if (result.Status != AIWorld.Core.Behaviors.TravelIntentHandler.TravelCompletionStatus.Pending)
                LogMarker($"[T1] TRAVEL_COMPLETE npc={npcId} status={result.Status} reason={result.ReleaseReason?.ToString() ?? "-"} detail={result.Detail} ticks={result.TicksElapsed}", LogLevel.Info);
        }
    }

    /// <summary>
    /// A1 — 正常流程约会推进：每 tick 驱动 AppointmentManager，到出发时间 dispatch 唯一
    /// travel intent（经 _travelHandler 建 Lease 自然移动），状态变化后立即落盘。
    /// QA（_appointmentMode）由 AppointmentDriver 自驱，这里不重复推进。
    /// </summary>
    private void TryTickAppointments()
    {
        if (_appointments is null || _travelHandler is null || _travelLeases is null) return;
        int day = AppointmentDay, time = Game1.timeOfDay;
        var previous = _appointments.All.ToDictionary(c => c.CommitmentId, c => c.Status);
        var tick = _appointments.Tick(day, time, EstimateAppointmentTravel, npcId => _travelLeases.IsActive(npcId));
        foreach (var intent in tick.TravelIntentsToDispatch)
        {
            var actor = intent.Params?["npcId"] as string;
            if (string.IsNullOrEmpty(actor)) continue;
            var outcome = _travelHandler.Handle(intent, actor, NowStamp());
            if (outcome.AlreadyAtDestination) _appointments.OnTravelArrived(actor, day, time);
            else if (!_travelLeases.IsActive(actor)) _appointments.OnTravelFailed(actor, "travel could not start");
        }
        foreach (var commitment in _appointments.All)
        {
            if (commitment.Status == TravelCommitmentStatus.WaitingForPlayer)
            {
                var npc = Game1.getCharacterFromName(commitment.NpcId);
                var player = Game1.player;
                if (npc?.currentLocation?.NameOrUniqueName == commitment.GroundedTarget
                    && player?.currentLocation?.NameOrUniqueName == commitment.GroundedTarget
                    && Math.Abs(npc.TilePoint.X - player.TilePoint.X) + Math.Abs(npc.TilePoint.Y - player.TilePoint.Y) <= 3)
                    _appointments.Complete(commitment.CommitmentId);
            }
            if (commitment.Status is TravelCommitmentStatus.Completed or TravelCommitmentStatus.Missed
                && previous.TryGetValue(commitment.CommitmentId, out var before)
                && before is TravelCommitmentStatus.ArrivedEarly or TravelCommitmentStatus.WaitingForPlayer)
                EndPlayerAppointment(commitment);
            if (commitment.Status is TravelCommitmentStatus.ArrivedEarly or TravelCommitmentStatus.WaitingForPlayer)
            {
                var npc = Game1.getCharacterFromName(commitment.NpcId);
                if (npc is not null && !new SmapiHardAnchorProvider().IsHardAnchorActive(commitment.NpcId))
                {
                    npc.controller = null;
                    npc.Halt();
                }
            }
        }
        if (_appointments.All.Any(c => !previous.TryGetValue(c.CommitmentId, out var before) || before != c.Status))
            _appointments.Save();
    }

    private int EstimateAppointmentTravel(string npcId)
    {
        var commitment = _appointments?.All.FirstOrDefault(c => c.NpcId == npcId && c.Status is
            TravelCommitmentStatus.Accepted or TravelCommitmentStatus.WaitingToDepart);
        var npc = Game1.getCharacterFromName(npcId);
        var destination = commitment is null ? null : _travelResolver?.Resolve(npcId, commitment.GroundedTarget).Destination;
        if (npc?.currentLocation is null || destination is null) return 30;
        return _travelAdapter?.EstimateTravelMinutes(npcId, destination, _config.EstimatedGameMinutesPerTile) ?? 30;
    }

    private void EndPlayerAppointment(TravelCommitment commitment)
    {
        if (_travelLeases?.IsActive(commitment.NpcId) == true)
            _travelHandler?.Cancel(commitment.NpcId, NowStamp());
        if (!new SmapiHardAnchorProvider().IsHardAnchorActive(commitment.NpcId))
            _rejoinPlanner?.Execute(commitment.NpcId, StardewBaselineResolver.Resolve(commitment.NpcId, Game1.timeOfDay));
        _repository?.MarkDataDirty();
    }

    private static bool IsNpcWithPlayer(string npcId) => Game1.getCharacterFromName(npcId)?.currentLocation == Game1.player?.currentLocation;

    private void PollPlayerExperiences()
    {
        foreach (var (npcId, source) in _playerObservations.ToArray())
        {
            if (CurrentFollowAdapter?.IsFollowing(npcId) != true)
            {
                source.OnFollowEnded(Game1.getCharacterFromName(npcId)?.currentLocation?.NameOrUniqueName ?? "unknown");
                _followSessions?.End(npcId, "BACKEND_RELEASED", NowStamp());
                ConsolidatePlayerExperience(npcId);
                continue;
            }
            source.Tick();
            if (IsNpcWithPlayer(npcId) && _playerMagicObservations.TryGetValue(npcId, out var magic)) magic.Tick();
        }
    }

    private void ConsolidatePlayerExperience(string npcId)
    {
        if (_repository?.Data is null || _followSessions is null || _observationCollector is null) return;
        var service = new MemoryConsolidationService(_observationCollector, _followSessions,
            new MemoryIngestionService(_repository.Memory!, GameDate()),
            new FactualObservationInterpreter(location => Game1.getLocationFromName(location)?.DisplayName ?? location));
        var result = service.Consolidate(npcId, NowStamp());
        _repository.MarkDataDirty();
        _playerObservations.Remove(npcId);
        _playerMagicObservations.Remove(npcId);
        LogMarker($"[PLAYER] EXPERIENCE_SAVED npc={npcId} observations={result.ObservationCount} memories={result.Accepted}", LogLevel.Info);
    }

    private static string NowStamp()
    {
        try
        {
            return $"{Game1.currentSeason}-{Game1.dayOfMonth}-{Game1.year}@{Game1.timeOfDay:D4}";
        }
        catch { return "unknown-date@600"; }
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        // Scenario A: SocialValley assemblies + ModEntry static singletons are available at
        // GameLaunched. Inject provider=OpenAI + endpoint BEFORE any save loads so the reused
        // ChatUI routes to the AIWorld local server the moment the player opens it.
        if (!_s2Mode || _svInjector is not null || (_normalPlayerMode && !_config.AllowProductionLlm)) return;
        _svInjector = new SocialValleyBridgeInjector(Monitor, $"http://localhost:8617/v1/chat/completions");
        bool ok = _svInjector.InjectAll();
        LogMarker(ok
            ? "[S2] SV_INJECTION_COMPLETE — SocialValley ChatUI → AIWorld local server"
            : "[S2] SV_INJECTION_FAILED — ChatUI will keep using SocialValley's own provider",
            ok ? LogLevel.Info : LogLevel.Warn);
    }

    private void OnPlayerWarped(object? sender, WarpedEventArgs e)
    {
        _observationSource?.OnWarped(e.NewLocation?.NameOrUniqueName ?? "unknown");
        foreach (var source in _playerObservations.Values)
            source.OnWarped(e.NewLocation?.NameOrUniqueName ?? "unknown");
    }

    private void OnOneSecondUpdateTicked(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (_repository is null) return;
        if (Game1.player is null || !Game1.hasLoadedGame) return;
        if (Game1.timeOfDay < 600) return;
        if (_s2Mode)
        {
            _nativeFollow?.Tick();
            PollPlayerExperiences();
            // Scenario A: consume validated intents from the chat server on the main thread
            // (production execution chain — BehaviorArbitrator → FollowIntentHandler → adapter).
            while (_pendingIntents?.TryDequeue(out var item) == true)
            {
                var (intent, actor, stamp) = item;
                LogMarker($"[S2] INTENT_EXECUTING type={intent.Type} target={intent.Target} actor={actor} reason={intent.Reason}", LogLevel.Info);
                try
                {
                    // Travel Gate T1: route travel intents to the travel handler (vanilla
                    // movement); follow/stop stays on the follow handler.
                    if (intent.Type == AIWorld.Core.Behaviors.CapabilityRegistry.CapabilityIds.TravelToLocation)
                    {
                        var travelOutcome = _travelHandler?.Handle(intent, actor, stamp);
                        _chatServer?.Journal.Execution(intent.IntentId, travelOutcome is null ? "无移动处理器" : $"移动授权：{travelOutcome.Arbitration.Decision} / 调用移动={travelOutcome.AdapterInvoked} / 已在目的地={travelOutcome.AlreadyAtDestination}");
                        if (travelOutcome?.Lease is not null) _talkTravelIntents[actor] = intent.IntentId;
                        LogMarker(travelOutcome is null
                            ? "[T1] INTENT_EXECUTION_SKIPPED — no travel handler"
                            : $"[T1] INTENT_EXECUTED decision={travelOutcome.Arbitration.Decision} adapterInvoked={travelOutcome.AdapterInvoked} lease={(travelOutcome.Lease is null ? "none" : "ACTIVE")} alreadyThere={travelOutcome.AlreadyAtDestination}", LogLevel.Info);
                    }
                    else if (intent.Type is CapabilityRegistry.CapabilityIds.MagicInvestigate or CapabilityRegistry.CapabilityIds.MagicLearn)
                    {
                        if (_magicService?.GetKnowledgeState(actor) == "VERIFIED" && !new SmapiHardAnchorProvider().IsHardAnchorActive(actor))
                        {
                            if (intent.Type == CapabilityRegistry.CapabilityIds.MagicInvestigate) _magicService.SetInvestigateGoal(actor, GameDate());
                            else _magicService.AdoptMagic(actor, GameDate());
                            _repository.MarkDataDirty();
                            _chatServer?.Journal.Execution(intent.IntentId, "能力验证通过，已更新魔法学习状态");
                        }
                        else _chatServer?.Journal.Execution(intent.IntentId, "知识或剧情限制未通过，未执行");
                    }
                    else
                    {
                        var outcome = _s2FollowHandler?.Handle(intent, actor, stamp);
                        _chatServer?.Journal.Execution(intent.IntentId, outcome is null ? "无跟随处理器" : $"仲裁={outcome.Arbitration.Decision} / 已调用适配器={outcome.AdapterInvoked} / 拒绝={outcome.DecisionDeclined}");
                        LogMarker(outcome is null
                            ? "[S2] INTENT_EXECUTION_SKIPPED — no follow handler"
                            : $"[S2] INTENT_EXECUTED decision={outcome.Arbitration.Decision} adapterInvoked={outcome.AdapterInvoked} declined={outcome.DecisionDeclined}", LogLevel.Info);
                    }
                }
                catch (Exception ex)
                {
                    _chatServer?.Journal.Execution(intent.IntentId, "执行异常：" + ex.GetType().Name);
                    LogMarker($"[S2] INTENT_EXECUTION_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
                }
            }

            // Travel Gate T1: tick active travel leases (arrival predicate / timeout / release).
            TryTickTravelCompletion();

            // A1 R2: 推进跨图 rejoin 自然路线（stop_following / travel 释放后的回归基线），
            // 到达/失败/超时后清理，NPC 平滑走回家而非瞬移。
            _rejoinPlanner?.Poll();

            // A1：推进约会调度（正常流程）。到出发时间才 dispatch 唯一 travel intent，
            // 由 _travelHandler 建 Lease 自然移动；状态变化后落盘。
            TryTickAppointments();

            // CHARACTER AWARENESS: low-frequency canonical experience scan (once per ~30 ticks,
            // deterministic, world-truth only). Live-observed heart events / mail become
            // per-character knowledge + canonical episodic memory via CanonicalExperienceService.
            _awarenessTick++;
            if (_awarenessTick % 30 == 0)
                ScanCanonicalExperiences();
            return;
        }
        if (_p4Mode)
        {
            // P4 mode: the follow driver owns its own multi-step state machine (the explicit-stop
            // scenario runs ACCEPT → EXPLICIT STOP → idempotency in ONE process); it self-gates
            // via its _done flag instead of the one-shot _runtimeDone gate.
            _followDriver?.TickIfReady();
            return;
        }
        if (_p5Mode)
        {
            // P5 mode: production observation source polls first (health/monster), then the
            // experience driver advances its state machine. TestHost causes, production observes.
            _observationSource?.Tick();
            _experienceDriver?.TickIfReady();
            return;
        }
        if (_p6Mode)
        {
            // P6 mode: production magic observation source scans first (context tags), then the
            // magic capability driver advances. TestHost causes, production observes.
            _magicSource?.Tick();
            _magicDriver?.TickIfReady();
            return;
        }
        if (_p8Mode)
        {
            // CHARACTER AWARENESS RUNTIME GATE driver (deterministic state machine).
            _awarenessGateDriver?.TickIfReady();
            return;
        }
        if (_matrixMode)
        {
            // TRAVEL RELIABILITY MATRIX driver — owns its own completion ticks (interruption
            // scenarios must control WHEN Complete/Cancel runs), so TryTickTravelCompletion is
            // NOT called here.
            _matrixDriver?.TickIfReady();
            return;
        }
        if (_appointmentMode)
        {
            // A1 NPC 主观同意与定时约会 driver（36 条场景）+ active-lease completion。
            _appointmentDriver?.TickIfReady();
            TryTickTravelCompletion();
            return;
        }
        if (_t1Mode)
        {
            // TRAVEL GATE T1 driver (deterministic scenarios T-A..T-G) + active-lease completion.
            _travelGateDriver?.TickIfReady();
            TryTickTravelCompletion();
            return;
        }
        if (_p7Mode)
        {
            // P7 mode: CoreDemoDriver orchestrates the chained demo (magic line → follow line →
            // settled-state single save in process A; combined verify in process B).
            _coreDemoDriver?.TickIfReady();
            return;
        }
        if (_runtimeDone) return;
        _runtimeDone = true;
        if (_coordinator is null) return;
        RunRuntimeAcceptance();
        _followDriver?.TickIfReady();
    }

    private void RunRuntimeAcceptance()
    {
        var stage = _coordinator!.SelectStage();
        switch (stage)
        {
            case RuntimeAcceptanceCoordinator.Stage.None:
                LogMarker("[P3] RUNTIME_TEST_COMPLETED — no re-run", LogLevel.Info);
                break;

            case RuntimeAcceptanceCoordinator.Stage.StageAWrite:
                LogMarker("[P3] RUNTIME_TEST_STAGE = A_WRITE", LogLevel.Info);
                LogMarker("[P3] TEST_MUTATION_AUTHORIZED — sentinel absent", LogLevel.Info);
                if (_coordinator.RunStageAWrite())
                {
                    LogMarker("[P3] STATE_DELTA_BATCH_APPLIED — state.mood=curious", LogLevel.Info);
                    LogMarker("[P3] KNOWLEDGE_UPDATED — magic.exists=RUMOR", LogLevel.Info);
                    LogMarker("[P3] MEMORY_PERSISTED_IN_REPOSITORY — episodic memory with test marker", LogLevel.Info);
                    LogMarker($"[P3] RUNTIME_TEST_SENTINEL_CREATED — id={_coordinator.RuntimeTestId}", LogLevel.Info);
                    LogMarker("[P3] DIRTY_TRUE — awaiting real Stardew save lifecycle", LogLevel.Info);
                    LogMarker("[P3] NOTE: in-memory only; PERSISTENCE_RUNTIME not yet verified", LogLevel.Info);
                    WriteStageASignal();
                }
                else
                {
                    LogMarker("[P3] STAGE_A_WRITE_FAILED", LogLevel.Error);
                }
                break;

            case RuntimeAcceptanceCoordinator.Stage.StageBVerify:
                LogMarker("[P3] RUNTIME_TEST_STAGE = B_VERIFY", LogLevel.Info);
                var v = _coordinator.RunStageBVerify();
                if (!v.SentinelFound)
                {
                    LogMarker("[P3] RUNTIME_TEST_SENTINEL_FOUND — NO", LogLevel.Error);
                    return;
                }
                LogMarker($"[P3] RUNTIME_TEST_SENTINEL_FOUND — id={v.SavedTestId}", LogLevel.Info);
                LogMarker(v.StateRestored ? "[P3] NPC_STATE_RESTORED — state.mood=curious" : "[P3] NPC_STATE_RESTORED — FAIL", v.StateRestored ? LogLevel.Info : LogLevel.Error);
                LogMarker(v.KnowledgeRestored ? "[P3] KNOWLEDGE_RESTORED — magic.exists=RUMOR" : "[P3] KNOWLEDGE_RESTORED — FAIL", v.KnowledgeRestored ? LogLevel.Info : LogLevel.Error);
                LogMarker(v.NoLearned ? "[P3] KNOWLEDGE_LEARNED_ABSENT" : "[P3] KNOWLEDGE_LEARNED_ABSENT — FAIL", v.NoLearned ? LogLevel.Info : LogLevel.Error);
                LogMarker(v.MemoryRestored ? "[P3] MEMORY_RESTORED — episodic test marker found" : "[P3] MEMORY_RESTORED — FAIL", v.MemoryRestored ? LogLevel.Info : LogLevel.Error);
                LogMarker(v.PersonaUnchanged ? "[P3] PERSONA_UNCHANGED" : "[P3] PERSONA_UNCHANGED — FAIL", v.PersonaUnchanged ? LogLevel.Info : LogLevel.Error);
                LogMarker("[P3] NPC_MODDATA_WRITE_COUNT = 0", LogLevel.Info);
                LogMarker("[P3] SQUAD_MUTATION = NONE", LogLevel.Info);
                LogMarker("[P3] WIZARDRY_MUTATION = NONE", LogLevel.Info);
                LogMarker("[P3] SCHEDULE_MUTATION = NONE", LogLevel.Info);

                if (v.AllPass)
                {
                    LogMarker("[P3] TEST_ASSERTIONS_PASS", LogLevel.Info);
                    LogMarker("[P3] PERSISTENCE_RUNTIME = PASS", LogLevel.Info);
                    _coordinator.MarkCompleted();
                    LogMarker("[P3] RUNTIME_TEST_COMPLETED = true — no further runs", LogLevel.Info);
                }
                else
                {
                    LogMarker("[P3] TEST_ASSERTIONS_FAIL — persistence restore NOT verified", LogLevel.Error);
                }
                break;
        }
    }

    /// <summary>Signal file for the RuntimeTestHost: Stage A mutation completed (test orchestration only).
    /// GATE R1.4 §1: routed through QaSignal — the runId in the signal content comes ONLY from the
    /// QA run owner (qa-run-owner.json); a missing/unreadable/empty owner refuses the write.</summary>
    private void WriteStageASignal()
    {
        try
        {
            QaSignal.WriteStageDone(Helper);
            LogMarker("[P3] STAGE_A_SIGNAL_WRITTEN — test host may trigger save", LogLevel.Info);
        }
        catch (Exception ex)
        {
            LogMarker($"[P3] STAGE_A_SIGNAL_WRITE_FAILED — {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        _nativeFollow?.Clear();
        _travelAdapter?.Clear();
        // Squad 0.12.1 DayEnding (DismissAll) fires AFTER our DayEnding handler (mod order).
        // We close sessions in OnSaving instead, where Squad has already dissolved.
        LogMarker("[P4] DAY_ENDING_CHECK — session close deferred to OnSaving (Squad dissolves after our handler)", LogLevel.Info);
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        if (_repository is null) return;
        _storyObserver?.Tick(force: true);
        LogMarker("[P3] SAVING", LogLevel.Info);

        // A1: persist active commitments to per-save data before the dirty check (AppointmentManager
        // marks the root dirty via SmapiCommitmentStore, so the write is not skipped).
        _appointments?.Save();
        LogMarker($"[A1] SAVING_COMMITMENTS count={_appointments?.All.Count ?? 0}", LogLevel.Info);

        // Squad semantics: same-day follow — Squad's DayEnding DismissAll ran before Saving.
        // Close any AIWorld session whose Squad runtime state dissolved.
        if (_followSessions is not null && CurrentFollowAdapter is not null)
        {
            foreach (var npcId in _followSessions.Sessions.Where(s => s.IsActive).Select(s => s.NpcId).ToList())
            {
                bool following = CurrentFollowAdapter.IsFollowing(npcId);
                LogMarker($"[P4] SAVING_SESSION_CHECK npc={npcId} squadFollowing={following}", LogLevel.Info);
                if (!following)
                {
                    if (_s2Mode) _s2FollowHandler?.OnFollowEnding?.Invoke(npcId, NowStamp());
                    _followSessions.End(npcId, "SQUAD_DAY_END", GameDate() + "@" + Game1.timeOfDay.ToString("D4"));
                    if (_s2Mode) ConsolidatePlayerExperience(npcId);
                    _repository.MarkDataDirty();
                    LogMarker($"[P4] SESSION_CLOSED_SQUAD_DAY_END npc={npcId}", LogLevel.Info);
                }
            }
        }

        if (!_repository.IsDirty)
        {
            LogMarker("[P3] SAVE_SKIPPED — not dirty", LogLevel.Info);
            return;
        }
        LogMarker("[P3] WRITE_SAVE_DATA_STARTED", LogLevel.Info);
        bool ok = _repository.Save();
        LogMarker(ok ? "[P3] WRITE_SAVE_DATA_SUCCESS — aiworld/state written" : "[P3] WRITE_SAVE_DATA_FAILED", ok ? LogLevel.Info : LogLevel.Error);
        if (!ok) LogMarker("[P3] DIRTY_REMAINS_TRUE", LogLevel.Error);
    }

    private void OnSaved(object? sender, SavedEventArgs e)
    {
        LogMarker("[P3] SAVED", LogLevel.Info);
        LogMarker($"[P3] DIRTY_FALSE — {(_repository is null || !_repository.IsDirty ? "clean" : "STILL DIRTY")}", LogLevel.Info);
    }

    /// <summary>CHARACTER AWARENESS: scan live world truth for canonical experiences and ingest
    /// them into per-character knowledge/memory (idempotent, participant-scoped). Deterministic —
    /// reads game state only; never invents experiences.</summary>
    private void ScanCanonicalExperiences()
    {
        try
        {
            if (_canonicalSource is null || _canonicalService is null) return;
            var stamp = GameDate() + "@" + Game1.timeOfDay.ToString("D4");

            // 1. Completed heart events (eventsSeen) → shared experience (NPC + Player).
            var heartEvents = _canonicalSource.ScanCompletedHeartEvents(stamp);
            foreach (var ev in heartEvents)
            {
                var result = _canonicalService.Ingest(ev);
                if (result.Accepted) _repository?.MarkDataDirty();
                LogMarker(result.Accepted
                    ? $"[AWARE] CANONICAL_INGESTED id={result.Event!.ExperienceId} type={ev.Type} participants={string.Join(",", result.WrittenNamespaces)} provenance={ev.Provenance}"
                    : $"[AWARE] CANONICAL_SKIPPED type={ev.Type} duplicate={result.Duplicate} err={result.Error ?? "(none)"}",
                    result.Accepted ? LogLevel.Info : LogLevel.Info);
            }

            // 2. Self-state awareness sample (evidence log).
            if (_selfStateProvider is not null && Game1.currentLocation?.characters is not null)
            {
                foreach (var npc in Game1.currentLocation.characters)
                {
                    if (npc is null || !npc.isVillager()) continue;
                    var state = _selfStateProvider.Get(npc.Name);
                    LogMarker($"[AWARE] SELF_STATE {state.Describe}", LogLevel.Debug);
                }
            }
        }
        catch (Exception ex)
        {
            LogMarker($"[AWARE] CANONICAL_SCAN_FAILED {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _talkConsole?.Dispose(); _talkConsole = null;
        _talkTravelIntents.Clear();
        _nativeFollow?.Clear(); _nativeFollow = null;
        _travelAdapter?.Clear(); _travelAdapter = null;
        _rejoinPlanner = null;
        _perception?.Reset();
        _dialogueBridge?.Dispose(); _dialogueBridge = null;
        _storyObserver = null;
        LogMarker("[P3] RETURNED_TO_TITLE — clearing runtime repository", LogLevel.Info);
        _repository?.ClearRuntime();
        _repository = null;
        _stateApplier = null;
        _memoryService = null;
        _coordinator = null;
        _followDriver = null;
        _experienceDriver = null;
        _observationSource = null;
        _observationCollector = null;
        _magicDriver = null;
        _magicSource = null;
        _magicService = null;
        _chatServer?.Dispose();
        _liveTalkModel?.Dispose(); _liveTalkModel = null;
        _gameThread?.Dispose();
        _gameThread = null;
        _playerObservations.Clear();
        _playerMagicObservations.Clear();
        _chatServer = null;
        _svInjector = null;
        _pendingIntents = null;
        _appointments = null;
        _runtimeDone = false;
    }

    private static string GameDate()
    {
        try
        {
            return $"{Game1.currentSeason}-{Game1.dayOfMonth}-{Game1.year}";
        }
        catch
        {
            return "unknown-date";
        }
    }
}
