using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIWorld.Core.Providers;

namespace AIWorld.Adapters.Local;

/// <summary>
/// Local QA LLM provider (LM Studio headless, OpenAI-compatible).
/// PM Local Provider Routing Gate — DEV_QA route.
///
/// Transport-only boundary:
///   - owns base URL / model id / request options
///   - does NOT parse StructuredResponse, does NOT validate schema, does NOT
///     duplicate semantic rules (no keyword parser, no intent mapping)
///
/// LOCAL structured response mode (per infra evidence):
///   LM Studio / llama.cpp JSON Schema grammar is unreliable for Chinese,
///   therefore this provider MUST NOT use response_format = json_schema.
///   Primary path: normal chat completion → prompt requests JSON only →
///   raw text → existing StructuredResponseParser/SchemaValidator downstream.
///
/// Non-thinking behavior for Qwen3 is enabled by appending "/no_think" to the
/// system prompt (verified: reasoning_tokens = 0 on localhost).
/// </summary>
public sealed class LocalLanguageModel : IAIWorldLanguageModel
{
    public const string DefaultBaseUrl = "http://127.0.0.1:1234/v1";
    public const string DefaultModel = "aiworld-local-qwen3-8b";
    public const string NoThinkTag = "/no_think";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly bool _appendNoThink;

    public LocalLanguageModel(string? baseUrl = null, string? model = null, bool appendNoThink = true, HttpClient? http = null)
    {
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _model = model ?? DefaultModel;
        _appendNoThink = appendNoThink;
        _http = http ?? new HttpClient { Timeout = Timeout };
    }

    public string BaseUrl => _baseUrl;
    public string Model => _model;
    public string ModelName => $"local/{_model}";

    /// <summary>True when the localhost endpoint responds to GET /v1/models. Never throws.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{_baseUrl}/models", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<ProviderResponse> GenerateRawAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        // Transport-level request options only. No response_format: llama.cpp JSON
        // Schema grammar corrupts Chinese (infra runtime evidence). The caller's
        // prompt asks for JSON; downstream parser/validator own correctness.
        var system = _appendNoThink && !systemPrompt.Contains(NoThinkTag, StringComparison.Ordinal)
            ? systemPrompt + "\n\n" + NoThinkTag
            : systemPrompt;

        var payload = new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userPrompt },
            },
            temperature = 0.7,
            top_p = 0.8,
            max_tokens = 1200,
            stream = false,
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ProviderResponse
                {
                    Status = ProviderStatus.ProviderError,
                    Error = $"LOCAL_HTTP_{(int)response.StatusCode}",
                };
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            var text = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : null;

            return string.IsNullOrEmpty(text)
                ? new ProviderResponse { Status = ProviderStatus.ProviderError, Error = "LOCAL_EMPTY_CONTENT" }
                : new ProviderResponse { Status = ProviderStatus.Success, Text = text };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderResponse { Status = ProviderStatus.Timeout, Error = "LOCAL_TIMEOUT" };
        }
        catch (OperationCanceledException)
        {
            return new ProviderResponse { Status = ProviderStatus.Cancelled, Error = "cancelled" };
        }
        catch (HttpRequestException ex)
        {
            return new ProviderResponse
            {
                Status = ProviderStatus.ProviderError,
                Error = $"LOCAL_PROVIDER_UNAVAILABLE: {ex.GetType().Name}",
            };
        }
        catch (Exception ex)
        {
            return new ProviderResponse
            {
                Status = ProviderStatus.ProviderError,
                Error = $"LOCAL_CLIENT_ERROR: {ex.GetType().Name}",
            };
        }
    }
}
