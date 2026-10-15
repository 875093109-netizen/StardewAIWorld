using AIWorld.Adapters.SocialValley;
using AIWorld.Core.Providers;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>P2-05/P2-06 SocialValleyBridge — minimal structure tests (offline).</summary>
public class SocialValleyBridgeTests
{
    [Fact]
    public async Task Detached_ReturnsProviderError_NoCrash()
    {
        var bridge = new SocialValleyBridge(); // no instance attached
        Assert.False(bridge.IsLive);

        var result = await bridge.GenerateRawAsync("sys", "user");

        Assert.Equal(ProviderStatus.ProviderError, result.Status);
        Assert.Contains("not attached", result.Error);
    }

    [Fact]
    public async Task Attached_WithFakeUnifiedClient_InvokesConvergencePoint()
    {
        // Simulate SocialValley.AI.UnifiedAIClient shape via a fake class with the same
        // GetResponse(object npc, string prompt) signature, bound by reflection.
        var fake = new FakeUnifiedClient();
        object? capturedNpc = null;
        var bridge = new SocialValleyBridge(fake, () => "Abigail");

        Assert.True(bridge.IsLive);
        var result = await bridge.GenerateRawAsync("sys", "user");

        Assert.Equal(ProviderStatus.Success, result.Status);
        Assert.Equal("{\"speech\":\"hi\"}", result.Text);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Attached_Timeout_ReturnsTimeout()
    {
        var fake = new FakeUnifiedClient { DelayMs = 61_000 };
        var bridge = new SocialValleyBridge(fake, () => "Abigail");
        var result = await bridge.GenerateRawAsync("sys", "user");
        Assert.Equal(ProviderStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task Attached_NoNpc_ReturnsProviderError()
    {
        var fake = new FakeUnifiedClient();
        var bridge = new SocialValleyBridge(fake, () => null);
        var result = await bridge.GenerateRawAsync("sys", "user");
        Assert.Equal(ProviderStatus.ProviderError, result.Status);
    }

    /// <summary>Fake stand-in for SocialValley.AI.UnifiedAIClient (same public shape, no game dependency).</summary>
    public sealed class FakeUnifiedClient
    {
        public int CallCount { get; private set; }
        public int DelayMs { get; init; }

        // Matches UnifiedAIClient.GetResponse(NPC, string) signature shape (reflection-bound).
        public async Task<string> GetResponse(object npc, string prompt)
        {
            CallCount++;
            if (DelayMs > 0)
                await Task.Delay(DelayMs);
            return "{\"speech\":\"hi\"}";
        }
    }
}
