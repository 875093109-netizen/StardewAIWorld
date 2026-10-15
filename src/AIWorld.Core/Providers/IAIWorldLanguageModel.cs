namespace AIWorld.Core.Providers;

/// <summary>
/// P2-05 Minimal provider boundary. AIWorld.Core depends ONLY on this interface —
/// never on OpenAI / DeepSeek / Gemini / Ollama / OpenRouter / Player2 concretes.
/// SocialValleyBridge (Adapters) is the provider adapter that implements this.
/// </summary>
public interface IAIWorldLanguageModel
{
    /// <summary>Model name/identifier for logging (must not include secrets).</summary>
    string ModelName { get; }

    /// <summary>
    /// Request a raw text completion. The caller owns the structured prompt;
    /// this method returns raw provider text which then flows through parse → schema → semantic.
    /// Must support CancellationToken; must NOT throw on provider failure — return a
    /// ProviderResponse with Error state instead (graceful failure).
    /// </summary>
    Task<ProviderResponse> GenerateRawAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);
}

/// <summary>Raw provider response envelope (graceful failure surface).</summary>
public sealed class ProviderResponse
{
    public required ProviderStatus Status { get; init; }
    public string? Text { get; init; }
    public string? Error { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens { get; init; }
    public int? HttpStatus { get; init; }
    public string? Endpoint { get; init; }
    public string? SentSystemPrompt { get; init; }
    public string? SentUserPrompt { get; init; }
    public string? Model { get; init; }
    public string? RequestPayload { get; init; }
    public string? RawResponse { get; init; }
}

public enum ProviderStatus
{
    Success,
    ProviderError,
    Timeout,
    Cancelled,
}
