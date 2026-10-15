namespace AIWorld.Core.Providers;

/// <summary>Routing profile: which environment is asking for an LLM provider.</summary>
public enum LlmRouteProfile
{
    /// <summary>Offline/deterministic tests — always MOCK, never network.</summary>
    OfflineTest,

    /// <summary>DEV_QA runtime — LOCAL only. Never falls back to Production.</summary>
    DevQa,

    /// <summary>Normal player runtime — PRODUCTION (DeepSeek), gated by AllowRealLlm.</summary>
    NormalPlayer,
}

/// <summary>Outcome of a routing decision.</summary>
public enum LlmRouteDecision
{
    Mock,
    Local,
    Production,
    /// <summary>DevQa requested Local but localhost provider is unavailable — FAIL CLOSED, no fallback.</summary>
    LocalUnavailable,
    /// <summary>NormalPlayer requested Production but AllowRealLlm is false — blocked.</summary>
    ProductionBlocked,
}

/// <summary>Options controlling production-call authorization (REAL_LLM_CALL_BUDGET gate).
/// Development/test cost-control mechanism. It does NOT gate ordinary NORMAL_PLAYER
/// production behavior — NormalPlayer always routes to Production (frozen product routing).
/// Reserved for future explicit PM-authorized production acceptance runs.</summary>
public sealed class LlmRoutingOptions
{
    /// <summary>
    /// Explicit authorization gate for production (real remote LLM) calls in development/test
    /// contexts. Default false: ordinary development must never call DeepSeek.
    /// Only explicit PM-authorized runs may set this to true.
    /// NOTE: does NOT affect LlmRouteProfile.NormalPlayer (always Production).
    /// </summary>
    public bool AllowRealLlm { get; init; } = false;
}

/// <summary>
/// Deterministic provider routing (PM Local Provider Routing Gate).
/// Pure logic — no I/O, fully offline-testable.
///
/// Frozen product routing:
///   OfflineTest  → Mock (always)
///   DevQa        → Local; if localhost unavailable → LocalUnavailable (FAIL CLOSED, never Production, never Mock/deterministic)
///   NormalPlayer → Production (ALWAYS — AllowRealLlm is a dev/test cost-control flag and
///                  must NOT disable ordinary player production behavior)
/// </summary>
public static class LlmRouting
{
    public static LlmRouteDecision Resolve(LlmRouteProfile profile, bool localAvailable, LlmRoutingOptions? options = null)
    {
        options ??= new LlmRoutingOptions();
        switch (profile)
        {
            case LlmRouteProfile.OfflineTest:
                return LlmRouteDecision.Mock;

            case LlmRouteProfile.DevQa:
                // DEV_QA must never silently fall back to DeepSeek or to Mock/deterministic.
                return localAvailable ? LlmRouteDecision.Local : LlmRouteDecision.LocalUnavailable;

            case LlmRouteProfile.NormalPlayer:
                // Frozen product routing: NORMAL_PLAYER → PRODUCTION / DEEPSEEK unconditionally.
                // AllowRealLlm (dev/test cost control) must not disable player production behavior.
                return LlmRouteDecision.Production;

            default:
                throw new ArgumentOutOfRangeException(nameof(profile), profile, null);
        }
    }

    /// <summary>Short trace tag for LLM_PROVIDER logging.</summary>
    public static string TraceTag(LlmRouteDecision decision) => decision switch
    {
        LlmRouteDecision.Mock => "MOCK",
        LlmRouteDecision.Local => "LOCAL",
        LlmRouteDecision.Production => "PRODUCTION",
        LlmRouteDecision.LocalUnavailable => "LOCAL_PROVIDER_UNAVAILABLE",
        LlmRouteDecision.ProductionBlocked => "PRODUCTION_BLOCKED",
        _ => "UNKNOWN",
    };
}
