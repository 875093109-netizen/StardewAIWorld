using System.Reflection;
using AIWorld.Core.Providers;

namespace AIWorld.Adapters.SocialValley;

/// <summary>
/// D1 SocialValley Structured Channel — minimal bridge.
///
/// Design (PM Phase 2 §9 / §11):
/// - AIWorld.Core depends ONLY on IAIWorldLanguageModel (never on SocialValley types).
/// - This bridge is the provider adapter: it reaches SocialValley's convergence point
///   `UnifiedAIClient.GetResponse(NPC, string)` WITHOUT copying any of its five
///   provider implementations (Player2/Ollama/OpenRouter/Gemini/OpenAI).
/// - Zero compile-time dependency on SocialValley.dll / Stardew Valley assemblies:
///   the bridge binds at runtime via reflection. This matches R2
///   VERIFIED_REFLECTION_INTEGRATION (no formal Mod API exists on SocialValley either).
/// - Phase 2: bridge is a minimal, compile-verified structure. Real invocation
///   requires an in-game SMAPI runtime (P2-06 integration), so GetResponse is
///   invoked only when a live instance handle is attached.
///
/// Upstream modification: NONE. upstream/SocialValley stays clean (PM §10).
/// </summary>
public sealed class SocialValleyBridge : IAIWorldLanguageModel
{
    // Reflection-bound shape of SocialValley.AI.UnifiedAIClient.GetResponse(NPC, string).
    private const string UnifiedAIClientTypeName = "SocialValley.AI.UnifiedAIClient";
    private const string GetResponseMethodName = "GetResponse";

    private readonly object? _unifiedClientInstance;
    private readonly MethodInfo? _getResponseMethod;
    private readonly Func<object?> _npcAccessor;

    /// <summary>
    /// Creates the bridge. If <paramref name="unifiedClientInstance"/> is null, the bridge
    /// is in "detached" mode: it reports PROVIDER_ERROR on call (graceful failure,
    /// no crash) until a live instance is attached in-game.
    /// </summary>
    public SocialValleyBridge(object? unifiedClientInstance = null, Func<object?>? npcAccessor = null)
    {
        _unifiedClientInstance = unifiedClientInstance;
        _npcAccessor = npcAccessor ?? (() => null);

        if (_unifiedClientInstance is not null)
        {
            var t = _unifiedClientInstance.GetType();
            _getResponseMethod = t.GetMethod(GetResponseMethodName, new[] { typeof(object), typeof(string) });
        }
    }

    /// <summary>Bridge is live only when a UnifiedAIClient instance is attached AND GetResponse resolves.</summary>
    public bool IsLive => _unifiedClientInstance is not null && _getResponseMethod is not null;

    public string ModelName => "socialvalley/unified-client";

    /// <summary>
    /// Invokes SocialValley's existing provider path. The structured prompt is built by
    /// AIWorld (caller); the raw text returned here flows through AIWorld's own
    /// parse → schema → semantic pipeline (never SocialValley's).
    /// </summary>
    public async Task<ProviderResponse> GenerateRawAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (!IsLive || _getResponseMethod is null)
        {
            return new ProviderResponse
            {
                Status = ProviderStatus.ProviderError,
                Error = "SocialValleyBridge not attached to a live UnifiedAIClient instance (requires in-game SMAPI runtime)",
            };
        }

        var npc = _npcAccessor();
        if (npc is null)
        {
            return new ProviderResponse
            {
                Status = ProviderStatus.ProviderError,
                Error = "no NPC handle available for SocialValley.GetResponse",
            };
        }

        try
        {
            // Prompt assembly: AIWorld owns the structured instruction; SocialValley's
            // GetResponse(NPC, prompt) is reused as the transport convergence point.
            var combinedPrompt = $"{systemPrompt}\n\n{userPrompt}";
            var task = (Task<string>?)_getResponseMethod.Invoke(_unifiedClientInstance, new[] { npc, combinedPrompt });

            if (task is null)
            {
                return new ProviderResponse { Status = ProviderStatus.ProviderError, Error = "GetResponse returned null task" };
            }

            string text;
            try
            {
                text = await task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            }
            catch (TimeoutException)
            {
                return new ProviderResponse { Status = ProviderStatus.Timeout, Error = "SocialValley GetResponse timed out (60s)" };
            }
            catch (OperationCanceledException)
            {
                return new ProviderResponse { Status = ProviderStatus.Cancelled, Error = "cancelled" };
            }

            return new ProviderResponse { Status = ProviderStatus.Success, Text = text };
        }
        catch (TargetInvocationException ex)
        {
            // SocialValley GetResponse itself degrades failures to prefixed strings
            // ([Error]/[Timeout]); reflection wrapper exceptions are surfaced here.
            return new ProviderResponse
            {
                Status = ProviderStatus.ProviderError,
                Error = $"SocialValley GetResponse threw: {ex.InnerException?.GetType().Name ?? ex.GetType().Name}",
            };
        }
        catch (Exception ex)
        {
            return new ProviderResponse
            {
                Status = ProviderStatus.ProviderError,
                Error = $"SocialValley bridge error: {ex.GetType().Name}",
            };
        }
    }
}
