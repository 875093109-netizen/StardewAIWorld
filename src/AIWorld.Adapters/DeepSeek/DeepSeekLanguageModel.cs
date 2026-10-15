using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIWorld.Core.Chat;
using AIWorld.Core.Providers;

namespace AIWorld.Adapters.DeepSeek;

/// <summary>
/// P2 real-LLM provider adapter (DeepSeek). Reads the API key from the
/// DEEPSEEK_API_KEY environment variable ONLY — never from code, docs, logs,
/// fixtures, or SMAPI logs (PM Phase 2 §15).
///
/// This adapter is compiled but only usable when DEEPSEEK_API_KEY is present;
/// otherwise it reports PROVIDER_ERROR (graceful failure, game keeps running).
/// It is NOT exercised in the evidence package unless a credential is available
/// and PM authorizes live integration.
/// </summary>
public sealed class DeepSeekLanguageModel : IAIWorldLanguageModel
{
    private const string ApiKeyEnvVar = "DEEPSEEK_API_KEY";
    private readonly string _endpoint;
    private readonly TalkOptions _options;
    public const string DefaultModel = "deepseek-flash";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;

    public DeepSeekLanguageModel(HttpClient? http = null) : this(http, ReadSetting) { }

    public DeepSeekLanguageModel(HttpClient? http, Func<string, string?> readSetting, TalkOptions? options = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout };
        _options = (options ?? new TalkOptions()).Normalize();
        _endpoint = _options.Endpoint;
        _apiKey = readSetting(ApiKeyEnvVar)?.Trim();
        var model = string.IsNullOrWhiteSpace(_options.Model) ? readSetting("DEEPSEEK_MODEL")?.Trim() : _options.Model;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
    }

    // Steam may retain an old process environment after the user updates their key.
    // Read the persisted Windows user setting first; other platforms keep process semantics.
    private static string? ReadSetting(string name)
    {
        var userValue = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null;
        return string.IsNullOrWhiteSpace(userValue) ? Environment.GetEnvironmentVariable(name) : userValue;
    }

    public string ModelName => $"{(TalkOptions.IsDeepSeek(_endpoint) ? "deepseek" : "compatible")}/{_model}";

    public bool HasCredential => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<ProviderResponse> GenerateRawAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (!TalkOptions.IsAllowedEndpoint(_endpoint)) return new() { Status = ProviderStatus.ProviderError, Error = "Invalid endpoint: use HTTPS or loopback HTTP." };
        if (string.IsNullOrWhiteSpace(_apiKey) && !new Uri(_endpoint).IsLoopback)
        {
            return new ProviderResponse
            {
                Status = ProviderStatus.ProviderError,
                Error = "DEEPSEEK_API_KEY not set (LIVE_LLM_INTEGRATION_BLOCKED_BY_CREDENTIAL)",
            };
        }

        var payload = new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            temperature = _options.Temperature,
            max_tokens = _options.MaxOutputTokens,
            // DeepSeek official API: force strict JSON object output. This is an API
            // capability (not a prompt hack); it materially improves nested-schema
            // adherence. The schema/semantic validators remain the real safety boundary.
            response_format = new { type = "json_object" },
            stream = false,
        };

        var payloadObject = JsonSerializer.SerializeToNode(payload)!;
        if (TalkOptions.IsDeepSeek(_endpoint)) payloadObject["thinking"] = JsonSerializer.SerializeToNode(new { type = _options.Thinking ? "enabled" : "disabled" });
        var requestBody = payloadObject.ToJsonString();
        string Redact(string value) => string.IsNullOrEmpty(_apiKey) ? TalkJournal.Clean(value) : TalkJournal.Clean(value.Replace(_apiKey, "[REDACTED]", StringComparison.Ordinal));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            if (!string.IsNullOrWhiteSpace(_apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            ProviderResponse Result(ProviderStatus status, string? text = null, string? error = null, int? input = null, int? output = null, int? total = null) => new()
            {
                Status = status, Text = text, Error = error, HttpStatus = (int)response.StatusCode, Endpoint = _endpoint,
                SentSystemPrompt = Redact(systemPrompt), SentUserPrompt = Redact(userPrompt), Model = Redact(_model), RequestPayload = Redact(requestBody), RawResponse = Redact(body),
                InputTokens = input, OutputTokens = output, TotalTokens = total
            };
            if (!response.IsSuccessStatusCode)
            {
                return Result(ProviderStatus.ProviderError, error: $"LLM HTTP {(int)response.StatusCode}");
            }
            try
            {
            using var doc = JsonDocument.Parse(body);
            int? Usage(string field) => doc.RootElement.TryGetProperty("usage", out var usage)
                && usage.TryGetProperty(field, out var v) && v.TryGetInt32(out var number) && number >= 0 ? number : null;
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return text is null
                ? Result(ProviderStatus.ProviderError, error: "LLM returned empty content", input: Usage("prompt_tokens"), output: Usage("completion_tokens"), total: Usage("total_tokens"))
                : Result(ProviderStatus.Success, text, input: Usage("prompt_tokens"), output: Usage("completion_tokens"), total: Usage("total_tokens"));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            { return Result(ProviderStatus.ProviderError, error: "Invalid provider response: " + ex.GetType().Name); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderResponse { Status = ProviderStatus.Timeout, Error = "LLM request timed out", Endpoint = _endpoint, Model = Redact(_model), SentSystemPrompt = Redact(systemPrompt), SentUserPrompt = Redact(userPrompt), RequestPayload = Redact(requestBody) };
        }
        catch (OperationCanceledException)
        {
            return new ProviderResponse { Status = ProviderStatus.Cancelled, Error = "cancelled", Endpoint = _endpoint, Model = Redact(_model), SentSystemPrompt = Redact(systemPrompt), SentUserPrompt = Redact(userPrompt), RequestPayload = Redact(requestBody) };
        }
        catch (Exception ex)
        {
            return new ProviderResponse
            {
                Status = ProviderStatus.ProviderError,
                Error = $"DeepSeek client error: {ex.GetType().Name}",
                Endpoint = _endpoint, Model = Redact(_model), SentSystemPrompt = Redact(systemPrompt), SentUserPrompt = Redact(userPrompt), RequestPayload = Redact(requestBody),
            };
        }
    }
}
