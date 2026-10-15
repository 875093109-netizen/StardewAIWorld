using System.Net;
using AIWorld.Adapters;
using AIWorld.Core.Providers;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// PM OVERNIGHT GATE PHASE 3 — network-side production guard.
/// Defense in depth: even if routing resolved Production for DEV_QA, the guard must
/// fail BEFORE any HTTP transport. Verified with a fake HTTP handler: the handler
/// must never be invoked.
/// </summary>
public class ProductionGuardTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int InvocationCount;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref InvocationCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}"),
            });
        }
    }

    [Fact]
    public void CreateGuarded_DevQa_Production_ThrowsBeforeHttp()
    {
        var handler = new CountingHandler();
        using var http = new HttpClient(handler);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LlmProviderFactory.CreateGuarded(LlmRouteDecision.Production, LlmRouteProfile.DevQa, http));

        Assert.Equal("REAL_LLM_FORBIDDEN_IN_DEV_QA", ex.Message);
        Assert.Equal(0, handler.InvocationCount); // NO HTTP transport attempted
    }

    [Fact]
    public void CreateGuarded_NormalPlayer_Production_Allowed()
    {
        var handler = new CountingHandler();
        using var http = new HttpClient(handler);

        var provider = LlmProviderFactory.CreateGuarded(LlmRouteDecision.Production, LlmRouteProfile.NormalPlayer, http);
        Assert.NotNull(provider);
        Assert.Equal(0, handler.InvocationCount); // constructor must not send HTTP either
    }

    [Fact]
    public void CreateGuarded_DevQa_Local_Allowed()
    {
        var handler = new CountingHandler();
        using var http = new HttpClient(handler);

        var provider = LlmProviderFactory.CreateGuarded(LlmRouteDecision.Local, LlmRouteProfile.DevQa, http);
        Assert.NotNull(provider);
        Assert.Equal(0, handler.InvocationCount);
    }

    [Fact]
    public void CreateGuarded_Offline_Mock_Allowed()
    {
        var provider = LlmProviderFactory.CreateGuarded(LlmRouteDecision.Mock, LlmRouteProfile.OfflineTest);
        Assert.NotNull(provider);
    }

    [Fact]
    public void CreateGuarded_FailClosedStates_NotCreatable()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LlmProviderFactory.CreateGuarded(LlmRouteDecision.LocalUnavailable, LlmRouteProfile.DevQa));
        Assert.Throws<InvalidOperationException>(() =>
            LlmProviderFactory.CreateGuarded(LlmRouteDecision.ProductionBlocked, LlmRouteProfile.NormalPlayer));
    }
}
