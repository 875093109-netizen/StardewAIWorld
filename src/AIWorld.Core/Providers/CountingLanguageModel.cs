namespace AIWorld.Core.Providers;

/// <summary>
/// Counting decorator over any IAIWorldLanguageModel. Tracks per-kind call counts
/// for MOCK_LLM_CALL_COUNT / LOCAL_LLM_CALL_COUNT / REAL_LLM_CALL_COUNT reporting.
/// Transparent passthrough: does not alter semantics, prompts, or error behavior.
/// </summary>
public sealed class CountingLanguageModel : IAIWorldLanguageModel
{
    private readonly IAIWorldLanguageModel _inner;

    public CountingLanguageModel(IAIWorldLanguageModel inner, LlmProviderKind kind)
    {
        _inner = inner;
        ProviderKind = kind;
    }

    public LlmProviderKind ProviderKind { get; }

    public string ModelName => _inner.ModelName;

    /// <summary>Number of GenerateRawAsync invocations through this decorator.</summary>
    public long CallCount { get; private set; }

    public async Task<ProviderResponse> GenerateRawAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return await _inner.GenerateRawAsync(systemPrompt, userPrompt, cancellationToken);
    }
}
