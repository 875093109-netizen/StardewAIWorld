using AIWorld.Core.Pipeline;
using AIWorld.Core.Providers;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>P2-03 invalid / malformed fixtures — each with explicit EXPECTED/ACTUAL/PASS semantics.</summary>
public class InvalidResponseTests
{
    private static async Task<StructuredResponseResult> RunAsync(string rawText)
    {
        var mock = new MockLanguageModel().Enqueue(rawText);
        return await new StructuredResponsePipeline(mock, StructuredResponseSemanticValidator.CreateDemoDefault())
            .RunAsync("sys", "user");
    }

    [Theory]
    [InlineData(Fixtures.InvalidJson, StructuredResponseStatus.PARSE_ERROR)]
    [InlineData(Fixtures.TruncatedJson, StructuredResponseStatus.PARSE_ERROR)]
    [InlineData(Fixtures.MissingSpeech, StructuredResponseStatus.SCHEMA_ERROR)]
    [InlineData(Fixtures.WrongFieldType, StructuredResponseStatus.SCHEMA_ERROR)]
    [InlineData(Fixtures.UnknownIntentType, StructuredResponseStatus.SCHEMA_ERROR)] // schema enum whitelist rejects unknown capabilityId first; semantic capability check is a fallback
    [InlineData(Fixtures.InvalidTarget, StructuredResponseStatus.SEMANTIC_ERROR)]
    [InlineData(Fixtures.IllegalStateDeltaPath, StructuredResponseStatus.SEMANTIC_ERROR)]
    [InlineData(Fixtures.UnexpectedField, StructuredResponseStatus.SCHEMA_ERROR)]
    [InlineData(Fixtures.EmptyResponse, StructuredResponseStatus.PARSE_ERROR)]
    [InlineData(Fixtures.InvalidPriority, StructuredResponseStatus.SCHEMA_ERROR)]
    [InlineData(Fixtures.InvalidEmotionalTone, StructuredResponseStatus.SCHEMA_ERROR)]
    [InlineData(Fixtures.INTENT_NONE_withTarget, StructuredResponseStatus.SCHEMA_ERROR)]
    public async Task InvalidFixture_Rejected_WithExpectedStatus(string raw, StructuredResponseStatus expected)
    {
        var result = await RunAsync(raw);
        // EXPECTED: status = expected. ACTUAL: result.Status. PASS/FAIL asserted below.
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Response); // No response escapes a failed pipeline.
    }

    [Fact]
    public async Task MarkdownFence_ExtractsJson_AndSucceeds()
    {
        var result = await RunAsync(Fixtures.MarkdownFence);
        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
        Assert.Equal("Hello farmer.", result.Response!.Speech);
    }

    [Fact]
    public async Task ProseAroundJson_ExtractsJson_AndSucceeds()
    {
        var result = await RunAsync(Fixtures.ProseAroundJson);
        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
        Assert.Equal("Hello farmer.", result.Response!.Speech);
    }

    [Fact]
    public async Task OversizedResponse_Rejected_AsOversized()
    {
        var result = await RunAsync(Fixtures.OversizedResponse);
        Assert.Equal(StructuredResponseStatus.OVERSIZED, result.Status);
    }

    [Fact]
    public async Task ProviderTimeout_MapsToTimeout()
    {
        var mock = new MockLanguageModel().Enqueue(ProviderStatus.Timeout);
        var result = await new StructuredResponsePipeline(mock, StructuredResponseSemanticValidator.CreateDemoDefault())
            .RunAsync("sys", "user");
        Assert.Equal(StructuredResponseStatus.TIMEOUT, result.Status);
    }

    [Fact]
    public async Task ProviderError_MapsToProviderError()
    {
        var mock = new MockLanguageModel().Enqueue(ProviderStatus.ProviderError, "auth failed");
        var result = await new StructuredResponsePipeline(mock, StructuredResponseSemanticValidator.CreateDemoDefault())
            .RunAsync("sys", "user");
        Assert.Equal(StructuredResponseStatus.PROVIDER_ERROR, result.Status);
        Assert.Contains("auth failed", result.Message);
    }

    [Fact]
    public async Task Cancellation_MapsToCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var mock = new MockLanguageModel().Enqueue(ProviderStatus.Cancelled);
        var result = await new StructuredResponsePipeline(mock, StructuredResponseSemanticValidator.CreateDemoDefault())
            .RunAsync("sys", "user", cts.Token);
        Assert.Equal(StructuredResponseStatus.CANCELLED, result.Status);
    }
}
