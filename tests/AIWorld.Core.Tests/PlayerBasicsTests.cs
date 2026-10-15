using System.Text.Json;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Chat;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using AIWorld.Core.Pipeline;
using Xunit;

namespace AIWorld.Core.Tests;

public class PlayerBasicsTests
{
    private static AIWorldSaveData Data() => new()
    {
        SaveSchemaVersion = "1", Characters = new(), Metadata = new()
    };

    private static PlayerRequest Request(string text = "今晚9点在酒吧见") =>
        new PlayerRequestFactory(new TravelTargetGrounding(Aliases()), () => "player-request")
            .TryCreate("Abigail", text, 27, 900)!;

    private static (string Canonical, string[] Aliases)[] Aliases() =>
        TravelLocationAliases.Build(new[] { ("Saloon", (string?)"星之果实酒吧"), ("Beach", (string?)"沙滩") });

    [Theory]
    [InlineData("今晚9点在酒吧见", "Saloon", 27, 2100)]
    [InlineData("明天晚上8点到沙滩等我", "Beach", 28, 2000)]
    [InlineData("去Saloon", "Saloon", 27, 900)]
    public void ChineseRequest_BindsOriginalTerms(string text, string location, int day, int time)
    {
        var request = Request(text);
        Assert.NotNull(request);
        Assert.Equal(location, request.GroundedTarget);
        Assert.Equal(day, request.RequestedGameDay);
        Assert.Equal(time, request.RequestedGameTime);
        Assert.Equal(PlayerRequest.ComputeTermsHash("Abigail", request.RequestKind, location, day, time), request.TermsHash);
    }

    [Theory]
    [InlineData("去云端小镇")]
    [InlineData("去那里")]
    [InlineData("去酒吧还是沙滩")]
    public void UnresolvedRequest_DoesNotCreateExecutionTerms(string text) => Assert.Null(Request(text));

    [Fact]
    public void Aliases_DoNotInventLocationsOutsideTheLiveWorld()
    {
        var aliases = TravelLocationAliases.Build(new[] { ("Beach", (string?)"海滩") });
        Assert.Single(aliases);
        Assert.Empty(new TravelTargetGrounding(aliases).ExtractGrounded("去酒吧"));
    }

    [Theory]
    [InlineData("ACCEPT", ConsentDisposition.Authorized)]
    [InlineData("DECLINE", ConsentDisposition.Declined)]
    [InlineData("UNCLEAR", ConsentDisposition.Unclear)]
    [InlineData("COUNTERPROPOSE", ConsentDisposition.CounterProposed)]
    public async Task ConversationPipeline_PreservesNpcChoiceAndBoundTerms(string outcome, ConsentDisposition expected)
    {
        var request = Request();
        var prompt = PlayerChatRules.ConsentPrompt(request);
        Assert.Contains(request.RequestId, prompt);
        Assert.Contains(request.TermsHash, prompt);
        var json = JsonSerializer.Serialize(new
        {
            speech = "我的决定。", intent = (object?)null, memoryCandidates = Array.Empty<object>(), stateDelta = Array.Empty<object>(),
            decision = new { requestId = request.RequestId, npcId = request.NpcId, termsHash = request.TermsHash,
                outcome, proposedTerms = outcome == "COUNTERPROPOSE" ? "明天晚上8点在沙滩见" : null, schemaVersion = "1" },
            schemaVersion = "1"
        });
        var pipeline = new StructuredResponsePipeline(new MockLanguageModel().Enqueue(json),
            StructuredResponseSemanticValidator.CreateLiveForLocations(Aliases().Select(a => a.Canonical), travelAliases: Aliases()));
        var parsed = await pipeline.RunAsync(prompt, "玩家说：" + request.OriginalUtterance, playerUtterance: request.OriginalUtterance);
        Assert.Equal(StructuredResponseStatus.SUCCESS, parsed.Status);
        var result = new NpcConsentGate().Evaluate(request, parsed.Response!.Decision!, 27, 900);
        Assert.Equal(expected, result.Disposition);
        if (expected == ConsentDisposition.Authorized)
        {
            Assert.Null(result.TravelIntent);
            Assert.Equal(2100, result.Commitment!.TargetGameTime);
            Assert.Equal("Saloon", result.Commitment.GroundedTarget);
        }
        else { Assert.Null(result.TravelIntent); Assert.Null(result.Commitment); }
    }

    [Theory]
    [InlineData("travel.to_location", false)]
    [InlineData("social.follow_player", true)]
    [InlineData("social.stop_following", true)]
    [InlineData("magic.learn", true)]
    public void DirectIntent_CannotBypassARequestDecision(string type, bool allowedWithoutRequest)
    {
        Assert.False(PlayerChatRules.CanDispatchDirectIntent(type, true));
        Assert.Equal(allowedWithoutRequest, PlayerChatRules.CanDispatchDirectIntent(type, false));
    }

    [Fact]
    public async Task Pipeline_DoesNotGroundTravelFromPromptExamples()
    {
        var pipeline = new StructuredResponsePipeline(new MockLanguageModel().Enqueue(
            """{"speech":"走吧","intent":{"intentId":"x","type":"travel.to_location","target":"Beach","priority":0.5,"schemaVersion":"1"},"schemaVersion":"1"}"""),
            StructuredResponseSemanticValidator.CreateLiveForLocations(new[] { "Beach" }, travelAliases: Aliases()));
        var result = await pipeline.RunAsync("system", "Schema example: Beach. 玩家说去那里", playerUtterance: "去那里");
        Assert.Equal(StructuredResponseStatus.SEMANTIC_ERROR, result.Status);
    }

    [Theory]
    [InlineData(2130, 2200, false)]
    [InlineData(2130, 2230, true)]
    [InlineData(2150, 2240, false)]
    [InlineData(2150, 2250, true)]
    public void WaitingWindow_UsesRealMinutesAcrossHourBoundaries(int target, int now, bool elapsed) =>
        Assert.Equal(elapsed, TravelDeparturePlanner.IsWaitWindowElapsed(now, target, 60));

    [Fact]
    public async Task GameQueue_WaitsForHostUpdateAndReturnsTheSnapshot()
    {
        using var queue = new MainThreadWorkQueue();
        bool accessed = false;
        var task = queue.InvokeAsync(() => { accessed = true; return 42; });
        Assert.False(accessed);
        Assert.False(task.IsCompleted);
        queue.Drain();
        Assert.Equal(42, await task);
    }

    [Fact]
    public async Task LeavingSave_CancelsPendingGameWork()
    {
        var queue = new MainThreadWorkQueue();
        var task = queue.InvokeAsync<int>(() => throw new InvalidOperationException("must never execute"));
        queue.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => task);
    }

    [Fact]
    public void SharedExperience_ProducesOneFactualMemoryAndSurvivesReloadWithoutBleeding()
    {
        var data = Data();
        var sessions = new FollowSessionManager(data, "player");
        var observations = new ObservationCollector(data);
        var memory = new MemoryRepository(data);
        var session = sessions.Start("Abigail", "outing", "spring-1-1@900")!;
        void Observe(string id, string type) {
            Assert.True(observations.Record(new ObjectiveObservation {
                ObservationId = id, Type = type, NpcId = "Abigail", SessionId = session.SessionId,
                GameDate = "spring-1-1", TimeOfDay = 1000, Location = "Mine", SchemaVersion = "1"
            }).Accepted);
            sessions.AppendObservation("Abigail", id);
        }
        Observe("visit", ObservationTypes.LocationVisited);
        Observe("injury", ObservationTypes.PlayerInjured);
        sessions.End("Abigail", "AI_DISMISS", "spring-1-1@1100");
        var service = new MemoryConsolidationService(observations, sessions,
            new MemoryIngestionService(memory, "spring-1-1"), new FactualObservationInterpreter(_ => "矿洞"));
        Assert.Equal(1, service.Consolidate("Abigail", "spring-1-1@1100").Accepted);
        Assert.Equal(0, service.Consolidate("Abigail", "spring-1-1@1100").Accepted);
        var loaded = AIWorldSaveRepository.Deserialize(AIWorldSaveRepository.Serialize(data))!;
        var recovered = new MemoryRepository(loaded);
        var fact = Assert.Single(recovered.GetRelevantMemories("Abigail", "矿洞", 6));
        Assert.Contains("矿洞", fact.Summary);
        Assert.Contains("受伤", fact.Summary);
        Assert.DoesNotContain("担心", fact.Summary);
        Assert.Empty(recovered.GetRecentMemories("Harvey", 6));
    }

    [Fact]
    public void NewMemoryAfterSameDayReload_HasDistinctIdentity()
    {
        var memory = new MemoryRepository(Data());
        var candidate = new MemoryCandidate { NpcId = "Abigail", SourceObservationId = "obs", Summary = "事实记忆", EmotionalTone = "neutral", Importance = 0.5f, SchemaVersion = "1" };
        var first = new MemoryIngestionService(memory, "spring-1-1").Ingest(candidate, MemoryLayer.Episodic);
        var second = new MemoryIngestionService(memory, "spring-1-1").Ingest(candidate, MemoryLayer.Episodic);
        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.NotEqual(first.Record!.MemoryId, second.Record!.MemoryId);
    }
}
