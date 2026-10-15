using AIWorld.Adapters.DeepSeek;
using AIWorld.Adapters.Local;
using AIWorld.Adapters.Mock;
using AIWorld.Core.Providers;

namespace AIWorld.Adapters;

/// <summary>
/// Creates concrete IAIWorldLanguageModel instances from a resolved routing decision.
/// Transport/endpoint concerns only. AIWorld (Core/Demo) keeps prompt construction,
/// StructuredResponse parsing, schema/semantic validation, intent authorization.
/// </summary>
public static class LlmProviderFactory
{
    /// <summary>
    /// Create a provider guarded by runtime profile (PM OVERNIGHT GATE PHASE 3,
    /// defense in depth). Even if routing ever resolved Production for DEV_QA,
    /// this guard fails BEFORE any HTTP transport is possible.
    /// </summary>
    public static IAIWorldLanguageModel CreateGuarded(
        LlmRouteDecision decision,
        LlmRouteProfile profile,
        HttpClient? http = null)
    {
        if (profile == LlmRouteProfile.DevQa && decision == LlmRouteDecision.Production)
            throw new InvalidOperationException("REAL_LLM_FORBIDDEN_IN_DEV_QA");
        return Create(decision, http);
    }

    public static IAIWorldLanguageModel Create(LlmRouteDecision decision, HttpClient? http = null)
    {
        switch (decision)
        {
            case LlmRouteDecision.Mock:
                return new MockLanguageModel();

            case LlmRouteDecision.Local:
                return new LocalLanguageModel(http: http);

            case LlmRouteDecision.Production:
                return new DeepSeekLanguageModel(http);

            case LlmRouteDecision.LocalUnavailable:
            case LlmRouteDecision.ProductionBlocked:
                throw new InvalidOperationException(
                    $"Decision {decision} is a fail-closed state and cannot be materialized as a provider.");

            default:
                throw new ArgumentOutOfRangeException(nameof(decision), decision, null);
        }
    }

    /// <summary>Local provider availability probe (DEV_QA fail-closed check). Never throws.</summary>
    public static async Task<bool> IsLocalAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var probe = new LocalLanguageModel(http: http);
            return await probe.IsAvailableAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
