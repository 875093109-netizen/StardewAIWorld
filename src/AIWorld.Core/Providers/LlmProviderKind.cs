namespace AIWorld.Core.Providers;

/// <summary>
/// Provider kind used for routing and LLM_PROVIDER trace (PM Local Provider Routing Gate).
/// MOCK = offline test; LOCAL = localhost QA (LM Studio); PRODUCTION = real remote LLM (DeepSeek).
/// </summary>
public enum LlmProviderKind
{
    Mock,
    Local,
    Production,
}
