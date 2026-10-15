using AIWorld.Core.Pipeline;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>P2-03 Mock structured response tests — fully offline.</summary>
public class StructuredPipelineTests
{
    private static StructuredResponsePipeline Pipeline(MockLanguageModel mock) =>
        new(mock, StructuredResponseSemanticValidator.CreateDemoDefault());

    [Fact]
    public async Task Valid_SpeechOnly_Succeeds()
    {
        var mock = new MockLanguageModel().Enqueue(Fixtures.ValidSpeechOnly);
        var result = await Pipeline(mock).RunAsync("sys", "user");

        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
        Assert.NotNull(result.Response);
        Assert.Equal("Hello farmer.", result.Response!.Speech);
        Assert.Null(result.Response.Intent);
    }

    [Fact]
    public async Task Valid_NullIntent_Succeeds()
    {
        var mock = new MockLanguageModel().Enqueue(Fixtures.ValidNullIntent);
        var result = await Pipeline(mock).RunAsync("sys", "user");

        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
        Assert.Null(result.Response!.Intent);
    }

    [Fact]
    public async Task Valid_FollowPlayerIntent_Succeeds_LogOnly()
    {
        var mock = new MockLanguageModel().Enqueue(Fixtures.ValidFollowPlayerIntent);
        var result = await Pipeline(mock).RunAsync("sys", "user");

        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
        Assert.NotNull(result.Response!.Intent);
        Assert.Equal("social.follow_player", result.Response.Intent!.Type);
        Assert.Equal("Player", result.Response.Intent.Target);
    }

    [Fact]
    public async Task Valid_MagicInvestigate_Succeeds()
    {
        var mock = new MockLanguageModel().Enqueue(Fixtures.ValidMagicInvestigate);
        var result = await Pipeline(mock).RunAsync("sys", "user");

        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
        Assert.Equal("magic.investigate", result.Response!.Intent!.Type);
        Assert.Equal("WizardHouse", result.Response.Intent.Target);
    }

    [Fact]
    public async Task Valid_TravelToLocation_Succeeds()
    {
        // Gate M1 defect fix: travel.to_location must pass the SCHEMA stage. Before the fix, the
        // semantic validator already registered it, but the schema validator rejected it — an LLM
        // proposing a travel intent was blocked before semantic validation ever ran.
        var mock = new MockLanguageModel().Enqueue(Fixtures.ValidTravelToLocation);
        var result = await Pipeline(mock).RunAsync("sys", "user");

        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
        Assert.Equal("travel.to_location", result.Response!.Intent!.Type);
        Assert.Equal("Mine", result.Response.Intent.Target);
    }

    [Fact]
    public async Task Valid_MemoryCandidates_Succeeds()
    {
        var mock = new MockLanguageModel().Enqueue(Fixtures.ValidMemoryCandidates);
        var result = await Pipeline(mock).RunAsync("sys", "user");

        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
        Assert.Single(result.Response!.MemoryCandidates!);
        Assert.Equal("Abigail", result.Response.MemoryCandidates![0].NpcId);
    }

    [Fact]
    public async Task Valid_StateDeltaProposal_Succeeds_NotApplied()
    {
        var mock = new MockLanguageModel().Enqueue(Fixtures.ValidStateDeltaProposal);
        var result = await Pipeline(mock).RunAsync("sys", "user");

        Assert.Equal(StructuredResponseStatus.SUCCESS, result.Status);
        Assert.Single(result.Response!.StateDelta!);
        Assert.Equal("state.mood", result.Response.StateDelta![0].Path);
        // Phase 2: proposal only — never applied. Nothing in pipeline mutates state.
    }
}
