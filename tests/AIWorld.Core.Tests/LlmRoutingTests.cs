using AIWorld.Core.Providers;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// PM Local Provider Routing Gate — offline routing decision tests.
/// Frozen product routing:
///   OfflineTest → Mock (always)
///   DevQa → Local (fail closed when unavailable; NEVER Production, NEVER Mock/deterministic)
///   NormalPlayer → Production (ALWAYS; AllowRealLlm is dev/test cost control and must NOT
///                   disable ordinary player production behavior)
/// </summary>
public class LlmRoutingTests
{
    [Fact]
    public void OfflineTest_AlwaysMock_RegardlessOfLocalOrAllow()
    {
        var d1 = LlmRouting.Resolve(LlmRouteProfile.OfflineTest, localAvailable: true,
            new LlmRoutingOptions { AllowRealLlm = true });
        var d2 = LlmRouting.Resolve(LlmRouteProfile.OfflineTest, localAvailable: false,
            new LlmRoutingOptions { AllowRealLlm = false });
        Assert.Equal(LlmRouteDecision.Mock, d1);
        Assert.Equal(LlmRouteDecision.Mock, d2);
    }

    [Fact]
    public void DevQa_LocalAvailable_RoutesLocal()
    {
        var d = LlmRouting.Resolve(LlmRouteProfile.DevQa, localAvailable: true,
            new LlmRoutingOptions { AllowRealLlm = true });
        Assert.Equal(LlmRouteDecision.Local, d);
    }

    [Fact]
    public void DevQa_LocalUnavailable_FailClosed_NeverProduction_NeverMock()
    {
        // Even with AllowRealLlm=true, DEV_QA must NOT fall back to Production.
        var d = LlmRouting.Resolve(LlmRouteProfile.DevQa, localAvailable: false,
            new LlmRoutingOptions { AllowRealLlm = true });
        Assert.Equal(LlmRouteDecision.LocalUnavailable, d);
        Assert.NotEqual(LlmRouteDecision.Production, d);
        Assert.NotEqual(LlmRouteDecision.Mock, d);
    }

    [Fact]
    public void NormalPlayer_AllowRealLlmFalse_StillProduction()
    {
        // BLOCKER 1 fix: AllowRealLlm is dev/test cost control; it must NOT disable
        // ordinary NORMAL_PLAYER production behavior.
        var d = LlmRouting.Resolve(LlmRouteProfile.NormalPlayer, localAvailable: true,
            new LlmRoutingOptions { AllowRealLlm = false });
        Assert.Equal(LlmRouteDecision.Production, d);
    }

    [Fact]
    public void NormalPlayer_AllowRealLlmTrue_Production()
    {
        var d = LlmRouting.Resolve(LlmRouteProfile.NormalPlayer, localAvailable: true,
            new LlmRoutingOptions { AllowRealLlm = true });
        Assert.Equal(LlmRouteDecision.Production, d);
    }

    [Fact]
    public void NormalPlayer_DefaultOptions_StillProduction()
    {
        // Default AllowRealLlm=false must NOT block NormalPlayer.
        var d = LlmRouting.Resolve(LlmRouteProfile.NormalPlayer, localAvailable: true);
        Assert.Equal(LlmRouteDecision.Production, d);
    }

    [Fact]
    public void NormalPlayer_DoesNotDependOnLocalAvailability()
    {
        // NormalPlayer routing never consults localhost availability.
        var d = LlmRouting.Resolve(LlmRouteProfile.NormalPlayer, localAvailable: false);
        Assert.Equal(LlmRouteDecision.Production, d);
    }

    [Fact]
    public void DevQa_NeverMock_EvenWhenLocalUnavailable()
    {
        // Explicit real/local request must never silently degrade to Mock.
        var d = LlmRouting.Resolve(LlmRouteProfile.DevQa, localAvailable: false);
        Assert.NotEqual(LlmRouteDecision.Mock, d);
        Assert.Equal(LlmRouteDecision.LocalUnavailable, d);
    }

    [Theory]
    [InlineData(LlmRouteDecision.Mock, "MOCK")]
    [InlineData(LlmRouteDecision.Local, "LOCAL")]
    [InlineData(LlmRouteDecision.Production, "PRODUCTION")]
    [InlineData(LlmRouteDecision.LocalUnavailable, "LOCAL_PROVIDER_UNAVAILABLE")]
    [InlineData(LlmRouteDecision.ProductionBlocked, "PRODUCTION_BLOCKED")]
    public void TraceTag_MatchesContract(LlmRouteDecision decision, string expected)
    {
        Assert.Equal(expected, LlmRouting.TraceTag(decision));
    }
}
