using AIWorld.Core.Providers;

namespace AIWorld.Adapters.Mock;

/// <summary>
/// Offline Mock provider for OFFLINE_TEST routing. Completely deterministic,
/// no network. Returns scripted responses (or failure states) per invocation.
/// </summary>
public sealed class MockLanguageModel : IAIWorldLanguageModel
{
    private readonly Queue<Func<ProviderResponse>> _responses = new();

    public MockLanguageModel()
    {
    }

    public MockLanguageModel(IEnumerable<ProviderResponse> scripted)
    {
        foreach (var r in scripted) _responses.Enqueue(() => r);
    }

    public long CallCount { get; private set; }
    public string ModelName => "mock/test-model";

    public MockLanguageModel Enqueue(string rawText)
    {
        _responses.Enqueue(() => new ProviderResponse { Status = ProviderStatus.Success, Text = rawText });
        return this;
    }

    public MockLanguageModel Enqueue(ProviderStatus status, string? error = null)
    {
        _responses.Enqueue(() => new ProviderResponse { Status = status, Error = error });
        return this;
    }

    public MockLanguageModel Enqueue(Func<ProviderResponse> factory)
    {
        _responses.Enqueue(factory);
        return this;
    }

    public Task<ProviderResponse> GenerateRawAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (_responses.Count == 0)
            return Task.FromResult(new ProviderResponse { Status = ProviderStatus.ProviderError, Error = "no scripted response" });
        return Task.FromResult(_responses.Dequeue()());
    }
}
