using AIWorld.Core.Perception;
using System.Text.Json;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Chat;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using AIWorld.Core.Providers;
using AIWorld.Demo;
using StardewModdingAPI;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>Exercises the actual production player-chat entry, with scripted models and a game clock snapshot.</summary>
public class PlayerChatEntryTests
{
    private static SelfState JobState(string npc, NpcJobState job) => new()
    {
        CharacterId = npc, CurrentLocation = "Town", CurrentTime = "spring-1-1@0900",
        CurrentActivity = CurrentActivity.CANONICAL, BehaviorOwner = job.Owner, FollowingPlayer = job.Phase == NpcJobPhase.Following, Job = job
    };
    [Fact] public async Task CurrentJobReachesEveryConversationEntryAndIsRecordedWithoutExposingAnotherNpcsTask()
    {
        using var f = new Fixture();
        f.Self = id => JobState(id, new(NpcJobPhase.Doing, "CANONICAL", id == "Abigail" ? "VERIFIED_CURRENT_TASK" : "OTHER_PRIVATE_TASK", "Town", "test"));
        await f.Chat("正在干什么"); await f.Rewrite();
        var turn = f.Server.VillageTurnAsync("job", "Abigail", new[] { "Abigail", "Harvey" }, "hello", () => true, _ => { }, CancellationToken.None);
        while (!turn.IsCompleted) { f.Queue.Drain(); await Task.Delay(1); }
        Assert.True(await turn); Assert.Equal(3, f.Model.Prompts.Count);
        Assert.All(f.Model.Prompts, prompt => { Assert.Contains("CURRENT JOB", prompt); Assert.Contains("VERIFIED_CURRENT_TASK", prompt); Assert.DoesNotContain("OTHER_PRIVATE_TASK", prompt); });
        Assert.All(f.Server.Journal.Snapshot(), r => Assert.Contains("VERIFIED_CURRENT_TASK", r.Attempts[0].SystemPrompt));
    }
    [Fact] public async Task AcceptedActionChangesNextTurnsStateButSpokenPromisesCannotWriteIt()
    {
        using var f = new Fixture();
        bool executorFollowing = false;
        f.Self = id => JobState(id, NpcJobState.Resolve(false, executorFollowing, false, false, "Town", 1, 1, null));
        f.OnAction = intent => { if (intent.Type == "social.follow_player") executorFollowing = true; };
        f.Model.Respond = (sys, _) => Decision(sys, "ACCEPT");
        await f.Chat("陪我去沙滩"); Assert.True(executorFollowing);
        f.Model.Respond = (_, _) => RewriteResponse("我已经开始在医院上班了。");
        await f.Chat("现在干嘛");
        Assert.Contains("\"Phase\":\"Following\"", f.Model.Prompts[^1]);
        Assert.Equal(NpcJobPhase.Following, f.Server.ReadJob("Abigail")!.Phase);
        executorFollowing = false; await f.Chat("现在呢");
        Assert.Equal(NpcJobPhase.Unknown, f.Server.ReadJob("Abigail")!.Phase);
    }
    [Theory] [InlineData("player")] [InlineData("rewrite")] [InlineData("village")]
    public async Task JobChangeInvalidatesAnInFlightResponse(string entry)
    {
        using var f = new Fixture(); bool changed = false;
        f.Self = id => JobState(id, new(changed ? NpcJobPhase.Interrupted : NpcJobPhase.Doing, "CANONICAL", null, "Town", "test"));
        f.Model.Respond = (_, _) => { changed = true; return Fixtures.ValidSpeechOnly; };
        if (entry == "player") Assert.Contains("状态已经变化", await f.Chat("你好"));
        else if (entry == "rewrite") Assert.Null(await f.Rewrite());
        else
        {
            bool displayed = false;
            var turn = f.Server.VillageTurnAsync("job", "Abigail", new[] { "Abigail", "Harvey" }, "hello", () => true, _ => displayed = true, CancellationToken.None);
            while (!turn.IsCompleted) { f.Queue.Drain(); await Task.Delay(1); }
            Assert.False(await turn); Assert.False(displayed);
        }
        Assert.Empty(f.Actions);
    }
    [Theory]
    [InlineData("en")][InlineData("zh-CN")][InlineData("zh-TW")][InlineData("ja")][InlineData("ko")][InlineData("es")]
    public async Task AllConversationEntrypointsFollowTheGameLanguage(string locale)
    {
        using var f = new Fixture(); f.Server.LanguageProvider = () => locale;
        f.Server.TalkSettings = () => new() { GlobalPrompt = TalkOptions.LegacyDefaultPrompt };
        await f.Chat("你好"); await f.Rewrite();
        var village = f.Server.VillageTurnAsync("locale", "Abigail", new[] { "Abigail", "Harvey" }, "hello",
            () => true, _ => { }, CancellationToken.None);
        while (!village.IsCompleted) { f.Queue.Drain(); await Task.Delay(1); }
        Assert.True(await village); Assert.Equal(3, f.Model.Prompts.Count);
        foreach (var prompt in f.Model.Prompts)
        {
            Assert.Contains(UiText.OutputInstruction(locale), prompt);
            Assert.Contains(UiText.Get(locale, "prompt.default"), prompt);
            Assert.DoesNotContain("in simplified Chinese", prompt);
            Assert.DoesNotContain("Answer in the player's language", prompt);
            Assert.DoesNotContain("Use the input line's language", prompt);
            Assert.Contains("JSON", prompt); // all JSON-mode requests have the provider's required instruction
        }
        Assert.Equal(UiText.Get(locale, "chat.missing-npc"), await f.Chat("hello", "Missing"));
        Assert.Empty(f.Actions);
    }
    [Fact] public async Task LanguageChangeDropsAnInFlightPlayerReplyWithoutExecutingItsAction()
    {
        using var f = new Fixture(); string locale = "en"; f.Server.LanguageProvider = () => locale;
        f.Model.Respond = (_, _) => { locale = "ja"; return Fixtures.ValidSpeechOnly; };
        Assert.Equal(UiText.Get("ja", "chat.changed"), await f.Chat("hello"));
        Assert.Empty(f.Actions); Assert.Empty(f.Server.Conversations.Get("Abigail"));
    }
    [Fact] public async Task LanguageChangeDropsAnInFlightVillageTurn()
    {
        using var f = new Fixture(); string locale = "en"; bool displayed = false; f.Server.LanguageProvider = () => locale;
        f.Model.Respond = (_, _) => { locale = "ko"; return Fixtures.ValidSpeechOnly; };
        var turn = f.Server.VillageTurnAsync("locale", "Abigail", new[] { "Abigail", "Harvey" }, "hello",
            () => true, _ => displayed = true, CancellationToken.None);
        while (!turn.IsCompleted) { f.Queue.Drain(); await Task.Delay(1); }
        Assert.False(await turn); Assert.False(displayed); Assert.Empty(f.Actions);
    }
    [Fact] public async Task LanguageChangeInvalidatesPendingNativeRewrite()
    {
        using var f = new Fixture(); string locale = "en"; f.Server.LanguageProvider = () => locale;
        var pending = await f.Rewrite(); Assert.NotNull(pending);
        locale = "zh-TW";
        Assert.False(f.Server.AcceptDialogueRewrite(pending!)); Assert.Empty(f.Actions);
    }
    [Fact] public async Task OwnKnowledgeIsInjectedInPlayerRewriteAndVillagePaths()
    {
        using var f = new Fixture(new[] { "Penny", "Harvey" });
        var queries = new List<(string Npc, string Query)>();
        f.Knowledge = (npc, query) => { queries.Add((npc, query)); return "\nFACTS_FOR_" + npc + ": 虞美人=最爱\n"; };
        await f.Chat("你喜欢虞美人吗？", "Penny");
        Assert.Contains("FACTS_FOR_Penny", f.Model.Prompts[^1]);
        Assert.Contains(("Penny", "你喜欢虞美人吗？"), queries);
        var rewrite = f.Server.RewriteDialogueAsync("Penny", "你好", CancellationToken.None);
        while (!rewrite.IsCompleted) { f.Queue.Drain(); await Task.Delay(1); }
        await rewrite;
        Assert.Contains("FACTS_FOR_Penny", f.Model.Prompts[^1]);
        var village = f.Server.VillageTurnAsync("session", "Penny", new[] { "Penny", "Harvey" }, "Harvey: 你好",
            () => true, _ => { }, CancellationToken.None);
        while (!village.IsCompleted) { f.Queue.Drain(); await Task.Delay(1); }
        Assert.True(await village);
        Assert.Contains("FACTS_FOR_Penny", f.Model.Prompts[^1]); Assert.DoesNotContain("FACTS_FOR_Harvey", f.Model.Prompts[^1]);
        Assert.All(queries, q => Assert.Equal("Penny", q.Npc)); Assert.Empty(f.Actions);
    }
    [Fact] public async Task VillageTalkUsesOnlyTheCurrentSpeakersPrivateContext()
    {
        using var f = new Fixture(new[] { "Abigail", "Harvey", "Penny" }, id => "PRIVATE_PERSONA_" + id);
        string? spoken = null;
        var task = f.Server.VillageTurnAsync("group", "Abigail", new[] { "Abigail", "Harvey", "Penny" }, "Harvey: hello",
            () => true, s => spoken = s, CancellationToken.None);
        while (!task.IsCompleted) { f.Queue.Drain(); await Task.Delay(1); }
        Assert.True(await task); Assert.NotNull(spoken);
        Assert.Contains("PRIVATE_PERSONA_Abigail", f.Model.Prompts[0]); Assert.DoesNotContain("PRIVATE_PERSONA_Harvey", f.Model.Prompts[0]);
        Assert.NotEmpty(f.Server.Conversations.Get("Penny")); Assert.Empty(f.Actions);
        var trace = Assert.Single(f.Server.Journal.Snapshot()); Assert.Equal(3, trace.Participants.Length); Assert.Single(trace.Attempts);
    }
    [Fact] public async Task VillageTalkRejectsWorldActionsEvenWhenSchemaIsValid()
    {
        using var f = new Fixture();
        f.Model.Respond = (_, _) => "{\"schemaVersion\":\"1\",\"speech\":\"跟你走。\",\"intent\":{\"intentId\":\"i\",\"type\":\"social.follow_player\",\"target\":\"Player\",\"priority\":0.5,\"schemaVersion\":\"1\"},\"memoryCandidates\":[],\"stateDelta\":[]}";
        bool displayed = false;
        var task = f.Server.VillageTurnAsync("group", "Abigail", new[] { "Abigail", "Harvey" }, "",
            () => true, _ => displayed = true, CancellationToken.None);
        while (!task.IsCompleted) { f.Queue.Drain(); await Task.Delay(1); }
        Assert.False(await task); Assert.False(displayed); Assert.Empty(f.Actions);
        Assert.Empty(f.Server.Conversations.Get("Harvey"));
    }
    [Fact] public async Task VillageTalkDropsReplyAfterParticipantsLeave()
    {
        using var f = new Fixture(); bool valid = true, displayed = false;
        f.Model.Respond = (_, _) => { valid = false; return Fixtures.ValidSpeechOnly; };
        var task = f.Server.VillageTurnAsync("group", "Abigail", new[] { "Abigail", "Harvey" }, "",
            () => valid, _ => displayed = true, CancellationToken.None);
        while (!task.IsCompleted) { f.Queue.Drain(); await Task.Delay(1); }
        Assert.False(await task); Assert.False(displayed); Assert.Empty(f.Actions);
        Assert.Contains("场景已变化", Assert.Single(f.Server.Journal.Snapshot()).Validation);
    }
    [Fact] public async Task ConsolePlayerChatAndSharedTranscriptUseExistingPipeline()
    {
        using var f = new Fixture();
        f.Server.TalkSettings = () => new() { GlobalPrompt = "CUSTOM_STYLE", PersonaNotes = new() { ["Abigail"] = "CHARACTER_STYLE" } };
        var task = f.Server.PlayerSayAsync("Abigail", "你好");
        while (!task.IsCompleted) { f.Queue.Drain(); await Task.Delay(1); }
        Assert.NotEmpty(await task); Assert.Contains("CUSTOM_STYLE", f.Model.Prompts[0]); Assert.Contains("CHARACTER_STYLE", f.Model.Prompts[0]);
        Assert.Equal(2, f.Server.Conversations.Get("Abigail").Count);
        Assert.Equal("PlayerChat", Assert.Single(f.Server.Journal.Snapshot()).Source);
    }
    [Fact]
    public async Task ExplicitStop_IsProgrammaticEvenWhenModelWouldFail()
    {
        using var f = new Fixture();
        f.Model.Respond = (_, _) => throw new InvalidOperationException("must not call model");
        Assert.Contains("已收到", await f.Chat("不用跟着我了！"));
        Assert.Equal("social.stop_following", Assert.Single(f.Actions).Type);
        Assert.Empty(f.Model.Prompts);
    }

    [Fact]
    public async Task CancellationQuestion_DoesNotCancelAppointment()
    {
        using var f = new Fixture(); f.Model.Respond = (sys, _) => Decision(sys, "ACCEPT");
        await f.Chat("今晚9点在酒吧见");
        f.Model.Respond = (_, _) => Fixtures.ValidSpeechOnly;
        await f.Chat("取消约会吗？");
        Assert.Equal(TravelCommitmentStatus.Accepted, Assert.Single(f.Appointments.All).Status);
        Assert.Equal(2, f.Model.Prompts.Count);
    }

    private sealed class TestMonitor : IMonitor { public void Log(string message, LogLevel level = LogLevel.Debug) { } }
    private sealed class Model : IAIWorldLanguageModel
    {
        public Func<string, string, string> Respond = (_, _) => Fixtures.ValidSpeechOnly;
        public readonly List<string> Prompts = new();
        public string ModelName => "scripted-player-entry";
        public Task<ProviderResponse> GenerateRawAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            Prompts.Add(systemPrompt);
            return Task.FromResult(new ProviderResponse { Status = ProviderStatus.Success, Text = Respond(systemPrompt, userPrompt) });
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Func<string, string, string> Knowledge = (_, _) => "";
        public Func<string, SelfState?> Self = _ => null;
        public Action<NpcIntent>? OnAction;
        public bool SceneActive;
        public Func<string, PerceptionFrame> Perceive = PerceptionFrame.Unavailable;
        public readonly InMemorySaveStore Store = new();
        public readonly AIWorldSaveRepository Repository;
        public readonly Model Model = new();
        public readonly MainThreadWorkQueue Queue = new();
        public readonly List<NpcIntent> Actions = new();
        public readonly List<string> Actors = new();
        public readonly AppointmentManager Appointments;
        public readonly AiWorldChatServer Server;
        public Fixture(string[]? npcIds = null, Func<string, string?>? personaSelector = null,
            (string Name, string? DisplayName)[]? locations = null)
        {
            var registry = npcIds ?? new[] { "Abigail", "Harvey" };
            locations ??= new[] { ("Beach", (string?)"沙滩"), ("Saloon", (string?)"酒吧") };
            Repository = new AIWorldSaveRepository(Store); Repository.Load();
            Appointments = new AppointmentManager(new SaveDataCommitmentStore(() => Repository));
            var adapter = new FakeFollowAdapter();
            var handler = new FollowIntentHandler(new BehaviorArbitrator(CapabilityRegistry.CreateDemoRegistry(), adapter,
                new NoHardAnchorsProvider(), new DeterministicNpcResolver(registry),
                new DeterministicPlayerResolver("player")), adapter, new FollowSessionManager(Repository.Data!, "player"),
                new DeterministicFollowDecisionSource(FollowDecision.Accept));
            Server = new AiWorldChatServer(new TestMonitor(), new ChatContextAssembler(personaSelector ?? (id => "Persona of " + id))
                { NpcKnowledgeProvider = (id, query) => Knowledge(id, query) },
                Repository, Repository.Memory!, Model, handler, (intent, actor, _) => { Actions.Add(intent); Actors.Add(actor); OnAction?.Invoke(intent); },
                () => "playerLocation=Town", () => (27, 900),
                selfStateProvider: id => Self(id), locationRegistryProvider: () => locations.Select(l => l.Name).ToArray(),
                consentGate: new NpcConsentGate(), appointments: Appointments, maxSchemaCorrectionRetries: 0,
                gameThread: Queue, travelAliasProvider: () => TravelLocationAliases.Build(locations),
                npcRegistryProvider: () => registry, isScriptedSceneActive: () => SceneActive, perceptionProvider: npc => Perceive(npc));
        }
        public async Task<string> Chat(string message, string npc = "Abigail")
        {
            var body = JsonSerializer.Serialize(new { messages = new[] {
                new { role = "system", content = "Respond naturally as " + npc },
                new { role = "user", content = "PLAYER'S MESSAGE: \"" + message + "\"\n\nRESPONSE GUIDANCE:\n- Respond naturally as " + npc + " based on your personality and relationship" }
            } });
            var result = Server.ProcessChatRequestAsync(body);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!result.IsCompleted && DateTime.UtcNow < deadline) { Queue.Drain(); await Task.Delay(1); }
            Assert.True(result.IsCompleted, "production entry did not finish after game updates");
            return await result;
        }
        public void Dispose() { Server.Dispose(); Queue.Dispose(); }
        public async Task<DialogueRewriteCandidate?> Rewrite(string line = "你好，新来的农夫。", CancellationToken token = default)
        {
            var result = Server.RewriteDialogueAsync("Abigail", line, token);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!result.IsCompleted && DateTime.UtcNow < deadline) { Queue.Drain(); await Task.Delay(1); }
            Assert.True(result.IsCompleted);
            return await result;
        }
    }

    private static string Decision(string prompt, string outcome, string? proposedTerms = null, bool wrongBinding = false, object? intent = null)
    {
        var terms = prompt.Split('\n').Single(line => line.StartsWith("{\"requestId\"", StringComparison.Ordinal));
        using var binding = JsonDocument.Parse(terms);
        var b = binding.RootElement;
        return JsonSerializer.Serialize(new {
            speech = "我的决定", intent, memoryCandidates = Array.Empty<object>(), stateDelta = Array.Empty<object>(), schemaVersion = "1",
            decision = new { requestId = wrongBinding ? "wrong-request" : b.GetProperty("requestId").GetString(),
                npcId = b.GetProperty("npcId").GetString(), termsHash = b.GetProperty("termsHash").GetString(),
                outcome, proposedTerms, flavorDialogue = (string?)null, schemaVersion = "1" }
        });
    }

    [Fact]
    public async Task AcceptedAppointment_PersistsWithoutStartingTravel()
    {
        using var f = new Fixture(); f.Model.Respond = (sys, _) => Decision(sys, "ACCEPT");
        var reply = await f.Chat("今晚9点在酒吧见");
        Assert.Contains("21:00", reply);
        var commitment = Assert.Single(f.Appointments.All);
        Assert.Equal("Saloon", commitment.GroundedTarget);
        Assert.Equal(27, commitment.TargetGameDay);
        Assert.Empty(f.Actions);
        Assert.True(f.Repository.Save());
        var reloaded = new AIWorldSaveRepository(f.Store); reloaded.Load();
        var restored = new AppointmentManager(new SaveDataCommitmentStore(() => reloaded)); restored.Load();
        Assert.Equal(commitment.CommitmentId, Assert.Single(restored.All).CommitmentId);
    }

    [Theory]
    [InlineData("DECLINE")]
    [InlineData("UNCLEAR")]
    public async Task Refusal_CannotBeOverriddenByARawTravelIntent(string outcome)
    {
        using var f = new Fixture();
        f.Model.Respond = (sys, _) => Decision(sys, outcome, intent: new {
            intentId = "bypass", type = "travel.to_location", target = "Beach", priority = 0.8, schemaVersion = "1"
        });
        await f.Chat("去沙滩");
        Assert.Empty(f.Actions); Assert.Empty(f.Appointments.All);
    }

    [Fact]
    public async Task AcceptedAccompanyRequest_StartsFollowingThePlayer()
    {
        using var f = new Fixture(); f.Model.Respond = (sys, _) => Decision(sys, "ACCEPT");
        await f.Chat("陪我去沙滩");
        var action = Assert.Single(f.Actions);
        Assert.Equal("social.follow_player", action.Type); Assert.Equal("Player", action.Target);
    }

    [Fact]
    public async Task IncorrectDecisionBinding_DoesNotMoveOrCreateAnAppointment()
    {
        using var f = new Fixture(); f.Model.Respond = (sys, _) => Decision(sys, "ACCEPT", wrongBinding: true);
        var reply = await f.Chat("今晚9点在酒吧见");
        Assert.Contains("没有通过确认", reply); Assert.Empty(f.Actions); Assert.Empty(f.Appointments.All);
    }

    [Fact]
    public async Task UnknownDestination_CannotBeReplacedByTheModel()
    {
        using var f = new Fixture();
        f.Model.Respond = (_, _) => """{"speech":"去沙滩吧","intent":{"intentId":"x","type":"travel.to_location","target":"Beach","priority":0.8,"schemaVersion":"1"},"schemaVersion":"1"}""";
        await f.Chat("去云端小镇");
        Assert.Empty(f.Actions); Assert.Empty(f.Appointments.All);
    }

    [Fact]
    public async Task Counterproposal_RequiresPlayerConfirmationAndANewNpcDecision()
    {
        using var f = new Fixture();
        f.Model.Respond = (sys, _) => Decision(sys, "COUNTERPROPOSE", "明天晚上8点在沙滩见");
        Assert.Contains("确认", await f.Chat("今晚9点在酒吧见"));
        Assert.Empty(f.Actions); Assert.Empty(f.Appointments.All);
        f.Model.Respond = (sys, _) => Decision(sys, "ACCEPT");
        await f.Chat("确认");
        var commitment = Assert.Single(f.Appointments.All);
        Assert.Equal("Beach", commitment.GroundedTarget); Assert.Equal(28, commitment.TargetGameDay);
        Assert.Equal(2000, commitment.TargetGameTime); Assert.Empty(f.Actions);
    }

    [Fact]
    public async Task CancelAppointment_IsAPlayerControlAndDoesNotCallTheModelAgain()
    {
        using var f = new Fixture(); f.Model.Respond = (sys, _) => Decision(sys, "ACCEPT");
        await f.Chat("今晚9点在酒吧见"); await f.Chat("取消约会");
        Assert.Equal(TravelCommitmentStatus.Cancelled, Assert.Single(f.Appointments.All).Status);
        Assert.Single(f.Model.Prompts); Assert.Empty(f.Actions);
    }

    [Fact]
    public async Task RecentDialogue_IsBoundedPerCharacterAndNotPermanentMemory()
    {
        using var f = new Fixture();
        await f.Chat("AbigailOnlyMarker"); await f.Chat("HarveyGreeting", "Harvey");
        Assert.DoesNotContain("AbigailOnlyMarker", f.Model.Prompts[1]);
        await f.Chat("继续聊");
        Assert.Contains("AbigailOnlyMarker", f.Model.Prompts[2]);
        Assert.Empty(f.Repository.Memory!.GetRecentMemories("Abigail", 100));
    }

    [Fact]
    public async Task Sve_CharacterPersonasAndConversationStaySeparate()
    {
        var catalog = new NpcPersonaCatalog(id => id == NpcPersonaCatalog.SveModId);
        using var f = new Fixture(new[] { "Sophia", "MarlonFay" }, id => catalog.GetPersona(id));
        await f.Chat("SophiaPrivateConversation", "Sophia");
        await f.Chat("你好", "MarlonFay");
        Assert.Contains("蓝月葡萄园", f.Model.Prompts[0]);
        Assert.Contains("冒险家公会的马龙", f.Model.Prompts[1]);
        Assert.DoesNotContain("SophiaPrivateConversation", f.Model.Prompts[1]);
        Assert.DoesNotContain("蓝月葡萄园", f.Model.Prompts[1]);
    }

    [Theory]
    [InlineData("SVE_Henchman")]
    [InlineData("GuntherSilvian")]
    [InlineData("SomeMod.NPC-2")]
    [InlineData("NPC With Spaces")]
    [InlineData("模组角色")]
    public async Task ModNpc_FollowUsesTheWholeCanonicalActorId(string npcId)
    {
        using var f = new Fixture(new[] { npcId });
        f.Model.Respond = (sys, _) => Decision(sys, "ACCEPT");
        await f.Chat("陪我去沙滩", npcId);
        Assert.Equal(npcId, Assert.Single(f.Actors));
        Assert.Equal("social.follow_player", Assert.Single(f.Actions).Type);
    }

    [Fact]
    public async Task Sve_NpcNotSpawnedInThisSaveCannotBeCreatedByAPersona()
    {
        var catalog = new NpcPersonaCatalog(id => id == NpcPersonaCatalog.SveModId);
        using var f = new Fixture(new[] { "Abigail" }, id => catalog.GetPersona(id));
        Assert.Contains("没有找到", await f.Chat("你好", "Morgan"));
        Assert.Empty(f.Model.Prompts);
        Assert.Empty(f.Repository.Data!.Characters);
        Assert.Empty(f.Actions);
    }

    [Fact]
    public async Task Sve_LocalizedDestinationAndCanonicalNpcSurviveAppointmentReload()
    {
        var catalog = new NpcPersonaCatalog(id => id == NpcPersonaCatalog.SveModId);
        using var f = new Fixture(new[] { "Sophia" }, id => catalog.GetPersona(id),
            new[] { ("Custom_BlueMoonVineyard", (string?)"蓝月葡萄园") });
        f.Model.Respond = (sys, _) => Decision(sys, "ACCEPT");
        await f.Chat("明天21:00在蓝月葡萄园见", "Sophia");
        f.Repository.Save();
        var loaded = new AIWorldSaveRepository(f.Store); loaded.Load();
        var restored = new AppointmentManager(new SaveDataCommitmentStore(() => loaded));
        restored.Load();
        restored.OnLoad(27, 1000);
        var appointment = Assert.Single(restored.All);
        Assert.Equal("Sophia", appointment.NpcId);
        Assert.Equal("Custom_BlueMoonVineyard", appointment.GroundedTarget);
        Assert.Equal(28, appointment.TargetGameDay);
    }

    [Fact]
    public async Task Sve_AbsentUsesVanillaPersonaAndNeverCallsForAbsentNpc()
    {
        var catalog = new NpcPersonaCatalog(_ => false);
        using var f = new Fixture(new[] { "Abigail" }, id => catalog.GetPersona(id) ?? "Vanilla-only persona");
        await f.Chat("你好");
        Assert.Contains("Vanilla-only persona", Assert.Single(f.Model.Prompts));
        Assert.DoesNotContain("BUILT-IN CHARACTER BACKGROUND", f.Model.Prompts[0]);
        await f.Chat("你好", "Sophia");
        Assert.Single(f.Model.Prompts);
    }

    [Fact] public async Task StorySceneBlocksChatBeforeCallingModel()
    {
        using var f = new Fixture(); f.SceneActive = true;
        Assert.Contains("剧情正在进行", await f.Chat("你好"));
        Assert.Empty(f.Model.Prompts); Assert.Empty(f.Actions);
    }
    [Fact] public async Task LateResponseCannotAcceptAppointmentAfterSceneChanges()
    {
        using var f = new Fixture();
        f.Model.Respond = (sys, _) => { f.SceneActive = true; return Decision(sys, "ACCEPT"); };
        Assert.Contains("状态已经变化", await f.Chat("今晚9点在酒吧见"));
        Assert.Empty(f.Appointments.All); Assert.Empty(f.Actions);
        Assert.Equal(ChatRequestPhase.Expired, f.Server.LastRequest!.Phase);
    }
    [Fact] public async Task LateResponseCannotWriteIntoAnotherSave()
    {
        using var f = new Fixture();
        f.Model.Respond = (sys, _) => { f.Repository.Load(); return Decision(sys, "ACCEPT"); };
        Assert.Contains("状态已经变化", await f.Chat("今晚9点在酒吧见"));
        Assert.Empty(f.Appointments.All); Assert.Empty(f.Actions);
    }
    [Fact] public async Task DisposedServerDiscardsPendingModelResponse()
    {
        using var f = new Fixture();
        f.Model.Respond = (sys, _) => { f.Server.Dispose(); return Decision(sys, "ACCEPT"); };
        Assert.Contains("取消", await f.Chat("今晚9点在酒吧见"));
        Assert.Empty(f.Appointments.All); Assert.Empty(f.Actions);
        Assert.Equal(ChatRequestPhase.Cancelled, f.Server.LastRequest!.Phase);
    }
    [Fact] public async Task ExcessivelyLongInputIsRejectedBeforeSpendingTokens()
    {
        using var f = new Fixture();
        Assert.Contains("太长", await f.Chat(new string('中', 2001)));
        Assert.Empty(f.Model.Prompts);
    }
    [Fact] public async Task RecentDialogueDoesNotCrossSaveBoundary()
    {
        using var f = new Fixture();
        await f.Chat("ONLY_FIRST_SAVE"); f.Repository.Load(); await f.Chat("你好");
        Assert.DoesNotContain("ONLY_FIRST_SAVE", f.Model.Prompts.Last());
    }
    [Fact] public async Task DiagnosticHistoryIsNeverReplayedAsKnowledgeAfterRollback()
    {
        using var f = new Fixture();
        var id = f.Server.Journal.Start("PlayerChat", "Abigail", "Player", "FUTURE_SAVE_SECRET");
        f.Server.Journal.Update(id, speech: "FUTURE_REPLY_SECRET");
        await f.Chat("你好");
        Assert.DoesNotContain("FUTURE_SAVE_SECRET", f.Model.Prompts.Last());
        Assert.DoesNotContain("FUTURE_REPLY_SECRET", f.Model.Prompts.Last());
        Assert.Contains(f.Server.Journal.Snapshot(), r => r.Id == id);
    }

    [Fact] public async Task NativeDialogueContinuesInChatWithoutBecomingFactMemory()
    {
        using var f = new Fixture();
        f.Server.ObserveDialogue("Abigail", "欢迎来镇上，我喜欢冒险。", "native-1");
        await f.Chat("你喜欢什么样的冒险？");
        Assert.Contains("我喜欢冒险", f.Model.Prompts[0]);
        Assert.Empty(f.Repository.Data!.CanonicalExperiences);
        Assert.Empty(f.Repository.Memory!.GetRecentMemories("Abigail", 100));
        Assert.Equal(3, f.Server.Conversations.Get("Abigail").Count);
        await f.Chat("你好", "Harvey");
        Assert.DoesNotContain("我喜欢冒险", f.Model.Prompts.Last());
    }
    private static string RewriteResponse(string speech, string? action = null) => JsonSerializer.Serialize(new {
        speech, intent = action is null ? null : new { intentId = "rewrite-action", type = action, target = "Player", priority = 0.8, schemaVersion = "1" },
        memoryCandidates = Array.Empty<object>(), stateDelta = Array.Empty<object>(), schemaVersion = "1" });
    private static void Following(Fixture f) => f.Repository.Data!.FollowSessions.Add(new FollowSession {
        SessionId = "following", NpcId = "Abigail", PlayerId = "player", StartedAt = "today", Reason = "accepted", SchemaVersion = "1" });

    [Fact] public async Task RewrittenActionRequiresUiCommitAndCanExecuteOnlyOnce()
    {
        using var f = new Fixture(); Following(f);
        f.Model.Respond = (_, _) => RewriteResponse("我想先回去了。", "social.stop_following");
        var proposal = await f.Rewrite(); Assert.NotNull(proposal); Assert.Empty(f.Actions);
        Assert.True(f.Server.AcceptDialogueRewrite(proposal!));
        Assert.Equal("social.stop_following", Assert.Single(f.Actions).Type);
        Assert.False(f.Server.AcceptDialogueRewrite(proposal!)); Assert.Single(f.Actions);
        Assert.Empty(f.Repository.Data!.CanonicalExperiences);
    }
    [Fact] public async Task ChangedWorldRejectsRewriteAtCommit()
    {
        using var f = new Fixture(); Following(f);
        f.Model.Respond = (_, _) => RewriteResponse("我想先回去了。", "social.stop_following");
        var proposal = await f.Rewrite(); Assert.NotNull(proposal);
        f.SceneActive = true; Assert.False(f.Server.AcceptDialogueRewrite(proposal!)); Assert.Empty(f.Actions);
    }
    [Fact] public async Task ChangedSaveRejectsRewriteAtCommit()
    {
        using var f = new Fixture();
        f.Model.Respond = (_, _) => RewriteResponse("欢迎来到这里。");
        var proposal = await f.Rewrite(); Assert.NotNull(proposal);
        f.Repository.Load(); Assert.False(f.Server.AcceptDialogueRewrite(proposal!)); Assert.Empty(f.Actions);
    }
    [Fact] public async Task DisposedServerCannotExecutePreviouslyGeneratedAction()
    {
        using var f = new Fixture(); Following(f);
        f.Model.Respond = (_, _) => RewriteResponse("我想先回去了。", "social.stop_following");
        var proposal = await f.Rewrite(); Assert.NotNull(proposal);
        f.Server.Dispose(); Assert.False(f.Server.AcceptDialogueRewrite(proposal!)); Assert.Empty(f.Actions);
    }
    [Fact] public async Task FailedPresentationCannotDispatchRewriteAction()
    {
        using var f = new Fixture(); Following(f);
        f.Model.Respond = (_, _) => RewriteResponse("我想先回去了。", "social.stop_following");
        var proposal = await f.Rewrite(); Assert.NotNull(proposal);
        Assert.Throws<InvalidOperationException>(() => f.Server.AcceptDialogueRewrite(proposal!, _ => throw new InvalidOperationException("UI unavailable")));
        Assert.Empty(f.Actions); Assert.False(f.Server.AcceptDialogueRewrite(proposal!));
    }
    [Theory]
    [InlineData("social.follow_player")]
    [InlineData("social.stop_following")]
    [InlineData("magic.learn")]
    [InlineData("travel.to_location")]
    public async Task AutoRewriteCannotEscalatePermissions(string action)
    {
        using var f = new Fixture(); f.Model.Respond = (_, _) => RewriteResponse("我打算这样做。", action);
        Assert.Null(await f.Rewrite()); Assert.Empty(f.Actions);
    }
    [Fact] public async Task InvalidOrCancelledRewriteLeavesNativeFallbackUntouched()
    {
        using var f = new Fixture(); f.Model.Respond = (_, _) => RewriteResponse("$action AddMoney 999");
        Assert.Null(await f.Rewrite()); Assert.Empty(f.Actions);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Null(await f.Rewrite(token: cts.Token)); Assert.Single(f.Model.Prompts);
    }
    [Fact] public async Task RewriteDoesNotLeakNewFactsIntoTranscriptBeforeDisplay()
    {
        using var f = new Fixture(); f.Model.Respond = (_, _) => RewriteResponse("准备显示的候选台词。");
        Assert.NotNull(await f.Rewrite()); Assert.Empty(f.Server.Conversations.Get("Abigail"));
        Assert.Empty(f.Repository.Data!.CanonicalExperiences);
    }

    private static PerceptionFrame Nearby(string npc, string id = "test:cat", string state = "resting", string location = "Town") => new(npc, location,
        DateTimeOffset.UtcNow, new[] { new PerceivedEntity(id, "猫", EntityKind.Animal, new(3, 3), state,
            null, EntityHostility.Unknown, true, "test") });

    private static string ObserveResponse(string target = "test:cat") => JsonSerializer.Serialize(new
    {
        speech = "我看看旁边那只动物。", intent = new { intentId = "look", type = "environment.observe", target, priority = .5, schemaVersion = "1" },
        memoryCandidates = Array.Empty<object>(), stateDelta = Array.Empty<object>(), schemaVersion = "1"
    });

    [Fact] public async Task PerceptionFlowsThroughChatWithPrivateVerifiedFeedbackAndNoWorldWrite()
    {
        using var f = new Fixture(); f.Perceive = npc => npc == "Abigail" ? Nearby(npc) : PerceptionFrame.Unavailable(npc);
        f.Model.Respond = (_, _) => ObserveResponse();
        await f.Chat("看看那只动物");
        Assert.Contains("test:cat", f.Model.Prompts[0]);
        Assert.Empty(f.Actions); Assert.Empty(f.Repository.Data!.CanonicalExperiences);
        f.Model.Respond = (_, _) => RewriteResponse("它在休息。");
        await f.Chat("看清楚了吗");
        Assert.Contains("LAST VERIFIED OBSERVATION RESULT", f.Model.Prompts[^1]);
        await f.Chat("你好", "Harvey");
        Assert.DoesNotContain("test:cat", f.Model.Prompts[^1]);
        Assert.DoesNotContain("LAST VERIFIED OBSERVATION", f.Model.Prompts[^1]);
    }
    [Fact] public async Task PerceptionProviderFailureKeepsOrdinaryChatAvailable()
    {
        using var f = new Fixture(); f.Perceive = _ => throw new InvalidOperationException("mod unavailable");
        f.Model.Respond = (_, _) => RewriteResponse("你好。");
        Assert.Equal("你好。", await f.Chat("你好"));
        Assert.Contains("LOCAL PERCEPTION: unavailable", f.Model.Prompts.Single()); Assert.Empty(f.Actions);
    }
    [Fact] public async Task MovingTargetOutOfSightInvalidatesPendingChat()
    {
        using var f = new Fixture(); f.Perceive = npc => Nearby(npc);
        f.Model.Respond = (_, _) => { f.Perceive = PerceptionFrame.Unavailable; return ObserveResponse(); };
        var result = await f.Chat("看看旁边");
        Assert.DoesNotContain("我看看", result); Assert.Empty(f.Actions);
        Assert.Empty(f.Repository.Data!.CanonicalExperiences);
    }
    [Fact] public async Task AutomaticRewriteObservationCommitsOnlyWhenDisplayedAndOnce()
    {
        using var f = new Fixture(); f.Perceive = npc => Nearby(npc); f.Model.Respond = (_, _) => ObserveResponse();
        var candidate = await f.Rewrite(); Assert.NotNull(candidate); Assert.True(candidate!.HasAction);
        Assert.DoesNotContain("LAST VERIFIED OBSERVATION", f.Model.Prompts.Single());
        Assert.True(f.Server.AcceptDialogueRewrite(candidate)); Assert.False(f.Server.AcceptDialogueRewrite(candidate));
        f.Model.Respond = (_, _) => RewriteResponse("你好。"); await f.Chat("怎么样");
        Assert.Contains("LAST VERIFIED OBSERVATION", f.Model.Prompts[^1]); Assert.Empty(f.Actions);
    }
    [Fact] public async Task ChangedTargetAndFailedPresentationNeverCommitObservation()
    {
        using var f = new Fixture(); f.Perceive = npc => Nearby(npc); f.Model.Respond = (_, _) => ObserveResponse();
        var candidate = await f.Rewrite(); Assert.NotNull(candidate);
        f.Perceive = npc => Nearby(npc, "test:replacement");
        Assert.False(f.Server.AcceptDialogueRewrite(candidate!));
        f.Perceive = npc => Nearby(npc); candidate = await f.Rewrite(); Assert.NotNull(candidate);
        Assert.Throws<InvalidOperationException>(() => f.Server.AcceptDialogueRewrite(candidate!, _ => throw new InvalidOperationException()));
        f.Model.Respond = (_, _) => RewriteResponse("你好。"); await f.Chat("看到了吗");
        Assert.DoesNotContain("LAST VERIFIED OBSERVATION", f.Model.Prompts[^1]); Assert.Empty(f.Actions);
    }
    [Fact] public async Task ObservationReceiptExpiresWhenVisibleStateChangesOrSaveChanges()
    {
        using var f = new Fixture(); f.Perceive = npc => Nearby(npc); f.Model.Respond = (_, _) => ObserveResponse();
        await f.Chat("看看猫");
        f.Model.Respond = (_, _) => RewriteResponse("你好。");
        f.Perceive = npc => Nearby(npc, state: "moving"); await f.Chat("现在呢");
        Assert.DoesNotContain("LAST VERIFIED OBSERVATION", f.Model.Prompts[^1]);
        f.Repository.ClearRuntime(); f.Repository.Load(); f.Perceive = npc => Nearby(npc); await f.Chat("你好");
        Assert.DoesNotContain("LAST VERIFIED OBSERVATION", f.Model.Prompts[^1]);
    }
    [Fact] public async Task PerceptionCannotLeakAMismatchedObserversFrame()
    {
        using var f = new Fixture(); f.Perceive = _ => Nearby("Harvey");
        f.Model.Respond = (_, _) => RewriteResponse("你好。"); await f.Chat("看看周围");
        Assert.DoesNotContain("test:cat", f.Model.Prompts.Single());
        Assert.Contains("LOCAL PERCEPTION: unavailable", f.Model.Prompts.Single());
    }
    [Fact] public async Task SceneSummaryEntersPromptButNeverGrantsAGroupAction()
    {
        using var f = new Fixture();
        f.Perceive = npc => Nearby(npc) with { Summaries = new[] {
            new SceneSummary("native", "crop:parsnip", EntityKind.Terrain, "parsnip", "growing", null, EntityHostility.Unknown, 20) } };
        f.Model.Respond = (_, _) => ObserveResponse("crop:parsnip");
        Assert.Null(await f.Rewrite()); Assert.Empty(f.Actions);
        Assert.Contains("SCENE SUMMARY", f.Model.Prompts.Single());
        Assert.Contains("observedCount\":20", f.Model.Prompts.Single());
        Assert.Empty(f.Repository.Data!.CanonicalExperiences);
    }
    [Theory]
    [InlineData("Player")]
    [InlineData("test:invented")]
    public async Task ObservationCannotResolveInventedOrGenericTargets(string target)
    {
        using var f = new Fixture(); f.Perceive = npc => Nearby(npc); f.Model.Respond = (_, _) => ObserveResponse(target);
        Assert.Null(await f.Rewrite()); Assert.Empty(f.Actions);
    }

}
