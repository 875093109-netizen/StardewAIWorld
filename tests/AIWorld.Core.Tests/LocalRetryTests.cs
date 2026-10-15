using AIWorld.Adapters;
using AIWorld.Adapters.Local;
using AIWorld.Adapters.Mock;
using AIWorld.Core.Pipeline;
using AIWorld.Core.Providers;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// PM Local Provider Routing Gate — LOCAL format retry semantics.
///
/// LOCAL structured response mode: normal chat completion → prompt asks JSON only →
/// raw text → existing StructuredResponseParser/Validator. When parse/schema validation
/// fails, retry locally. LOCAL_FORMAT_RETRY = 2. Malformed JSON is NEVER silently repaired
/// and counted as model success; after retry exhaustion the result is a failure
/// (reported as LOCAL_FORMAT_FAILURE by the caller).
/// </summary>
public class LocalRetryTests
{
    private static StructuredResponsePipeline MakePipeline(IAIWorldLanguageModel model)
        => new(model, StructuredResponseSemanticValidator.CreateDemoDefault());

    [Fact]
    public async Task RepeatedMalformedJson_RetriesTwice_ThenFails()
    {
        // 3 malformed responses in a row → initial attempt + 2 retries = 3 calls, still fails.
        var mock = new AIWorld.Adapters.Mock.MockLanguageModel()
            .Enqueue("this is not json { broken")
            .Enqueue("still not json")
            .Enqueue("nope not json either");

        var pipeline = MakePipeline(mock);
        var result = await pipeline.RunAsync("sys", "user", CancellationToken.None, maxSchemaCorrectionRetries: 2, retryOnParseError: true);

        Assert.Equal(3, mock.CallCount); // 1 initial + 2 retries = LOCAL_FORMAT_RETRY=2
        Assert.Equal(StructuredResponseStatus.PARSE_ERROR, result.Status);
        Assert.NotEqual(StructuredResponseStatus.SUCCESS, result.Status);
        // Caller-level marker (AiWorldChatServer) maps this to LOCAL_FORMAT_FAILURE.
    }

    [Fact]
    public async Task MalformedThenValid_RetryRecovers_Success()
    {
        // First response malformed, second valid → retry 1 succeeds.
        var mock = new AIWorld.Adapters.Mock.MockLanguageModel()
            .Enqueue("this is not json { broken")
            .Enqueue(Fixtures.ValidSpeechOnly);

        var pipeline = MakePipeline(mock);
        var result = await pipeline.RunAsync("sys", "user", CancellationToken.None, maxSchemaCorrectionRetries: 2, retryOnParseError: true);

        Assert.Equal(2, mock.CallCount);
        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
        Assert.NotNull(result.Response);
        Assert.Equal("Hello farmer.", result.Response.Speech);
    }

    [Fact]
    public async Task SchemaError_Retries_SameBehavior()
    {
        // Missing required field → SCHEMA_ERROR; retry with valid JSON recovers.
        var mock = new AIWorld.Adapters.Mock.MockLanguageModel()
            .Enqueue(Fixtures.MissingSpeech)
            .Enqueue(Fixtures.ValidSpeechOnly);

        var pipeline = MakePipeline(mock);
        var result = await pipeline.RunAsync("sys", "user", CancellationToken.None, maxSchemaCorrectionRetries: 2);

        Assert.Equal(2, mock.CallCount);
        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
    }

    [Fact]
    public async Task CountingDecorator_TracksLocalCalls()
    {
        var mock = new AIWorld.Adapters.Mock.MockLanguageModel().Enqueue("not json").Enqueue(Fixtures.ValidSpeechOnly);
        var counting = new CountingLanguageModel(mock, LlmProviderKind.Local);

        var pipeline = MakePipeline(counting);
        var result = await pipeline.RunAsync("sys", "user", CancellationToken.None, maxSchemaCorrectionRetries: 2, retryOnParseError: true);

        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
        Assert.Equal(2, counting.CallCount); // LOCAL_LLM_CALL_COUNT = 2
        Assert.Equal(LlmProviderKind.Local, counting.ProviderKind);
    }

    [Fact]
    public void Factory_MockDecision_CreatesMockProvider()
    {
        var p = LlmProviderFactory.Create(LlmRouteDecision.Mock);
        Assert.IsType<AIWorld.Adapters.Mock.MockLanguageModel>(p);
        Assert.Equal("mock/test-model", p.ModelName);
    }

    [Fact]
    public void Factory_LocalDecision_CreatesLocalProvider()
    {
        var p = LlmProviderFactory.Create(LlmRouteDecision.Local);
        Assert.IsType<LocalLanguageModel>(p);
        Assert.Equal("local/aiworld-local-qwen3-8b", p.ModelName);
    }

    [Fact]
    public void Factory_ProductionDecision_CreatesDeepSeekProvider()
    {
        var p = LlmProviderFactory.Create(LlmRouteDecision.Production);
        Assert.IsType<AIWorld.Adapters.DeepSeek.DeepSeekLanguageModel>(p);
        Assert.StartsWith("deepseek/", p.ModelName);
    }

    [Theory]
    [InlineData(LlmRouteDecision.LocalUnavailable)]
    [InlineData(LlmRouteDecision.ProductionBlocked)]
    public void Factory_FailClosedDecisions_Throw(LlmRouteDecision decision)
    {
        Assert.Throws<InvalidOperationException>(() => LlmProviderFactory.Create(decision));
    }
}
