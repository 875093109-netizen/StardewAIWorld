using System.Text.Json;
using AIWorld.Core.Contracts;
using AIWorld.Core.Providers;

namespace AIWorld.Core.Pipeline;

/// <summary>
/// Orchestrates the Phase 2 structured pipeline:
/// raw provider text → extract payload → JSON parse → schema validate → semantic validate → result.
/// LOG ONLY: never executes intents, never mutates game state.
/// </summary>
public sealed class StructuredResponsePipeline
{
    private readonly IAIWorldLanguageModel _model;
    private readonly StructuredResponseSemanticValidator _semantic;

    public StructuredResponsePipeline(IAIWorldLanguageModel model, StructuredResponseSemanticValidator? semantic = null)
    {
        _model = model;
        _semantic = semantic ?? StructuredResponseSemanticValidator.CreateDemoDefault();
    }

    public IAIWorldLanguageModel Model => _model;

    /// <summary>Run the full pipeline against a provider. Cancellation-safe, never throws for LLM errors.
    /// When <paramref name="maxSchemaCorrectionRetries"/> &gt; 0, a SCHEMA_ERROR/SEMANTIC_ERROR result may trigger
    /// up to that many bounded correction retries: the validator's non-sensitive error summary is fed back
    /// to the model, which must re-output complete JSON (PM Phase 2 §9: MAX_SCHEMA_CORRECTION_RETRY = 1).</summary>
    public async Task<StructuredResponseResult> RunAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default,
        int maxSchemaCorrectionRetries = 0,
        bool retryOnParseError = false,
        string? playerUtterance = null)
    {
        var result = await RunOnceAsync(systemPrompt, userPrompt, playerUtterance ?? userPrompt, cancellationToken);

        // LOCAL structured response mode (PM Local Provider Routing): parse failures may also
        // be retried locally (LOCAL_FORMAT_RETRY). DeepSeek production keeps the original
        // Phase 2 semantics (schema/semantic only).
        bool Retryable(StructuredResponseStatus s) =>
            s is StructuredResponseStatus.SCHEMA_ERROR or StructuredResponseStatus.SEMANTIC_ERROR
            || (retryOnParseError && s is StructuredResponseStatus.PARSE_ERROR);

        int attempt = 1;
        while (maxSchemaCorrectionRetries > 0
               && attempt <= maxSchemaCorrectionRetries
               && Retryable(result.Status))
        {
            var retryPrompt = $"{userPrompt}\n\nYour previous response was rejected. Correction feedback:\n{Truncate(result.Message ?? "unknown error", 500)}\n" +
                              "Re-output the COMPLETE JSON object only. Do not explain. Do not use markdown. Keep exact field names from the required schema.";
            // M1 SEMANTIC SAFETY: grounding 始终用玩家原始请求文本（userPrompt），
            // 绝不用含 canonical 名的 retry feedback —— 否则模型可用 feedback 里的地名蒙混。
            result = await RunOnceAsync(systemPrompt, retryPrompt, playerUtterance ?? userPrompt, cancellationToken);
            if (result.Status != StructuredResponseStatus.SUCCESS)
            {
                var warnings = result.Warnings?.ToList() ?? new List<string>();
                warnings.Add($"schemaCorrectionRetryUsed attempt={attempt} outcome={result.Status}");
                result = new StructuredResponseResult
                {
                    Status = result.Status,
                    Response = result.Response,
                    FailureStage = result.FailureStage,
                    Message = result.Message,
                    Warnings = warnings.ToArray(),
                };
            }
            attempt++;
        }

        return result;
    }

    private async Task<StructuredResponseResult> RunOnceAsync(string systemPrompt, string userPrompt, string playerUtteranceForGrounding, CancellationToken cancellationToken)
    {
        ProviderResponse provider;
        try
        {
            provider = await _model.GenerateRawAsync(systemPrompt, userPrompt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return StructuredResponseResult.Fail(StructuredResponseStatus.CANCELLED, "Provider", "request cancelled");
        }
        catch (Exception ex)
        {
            return StructuredResponseResult.Fail(StructuredResponseStatus.PROVIDER_ERROR, "Provider", $"provider threw: {ex.GetType().Name}");
        }

        switch (provider.Status)
        {
            case ProviderStatus.Cancelled:
                return StructuredResponseResult.Fail(StructuredResponseStatus.CANCELLED, "Provider", "request cancelled");
            case ProviderStatus.Timeout:
                return StructuredResponseResult.Fail(StructuredResponseStatus.TIMEOUT, "Provider", "provider timeout");
            case ProviderStatus.ProviderError:
                return StructuredResponseResult.Fail(StructuredResponseStatus.PROVIDER_ERROR, "Provider", provider.Error ?? "provider error");
            case ProviderStatus.Success:
                break;
            default:
                return StructuredResponseResult.Fail(StructuredResponseStatus.PROVIDER_ERROR, "Provider", $"unknown provider status {provider.Status}");
        }

        if (provider.Text is null)
            return StructuredResponseResult.Fail(StructuredResponseStatus.PARSE_ERROR, "Parse", "provider returned null text");

        // Parse.
        var parsed = StructuredResponseParser.Parse(provider.Text);
        if (!parsed.Ok)
        {
            var status = parsed.Truncated ? StructuredResponseStatus.OVERSIZED : StructuredResponseStatus.PARSE_ERROR;
            return StructuredResponseResult.Fail(status, "Parse", parsed.Error ?? "parse failed");
        }

        // Schema validate.
        var schemaErrors = StructuredResponseSchemaValidator.Validate(parsed.Payload!.Value);
        if (schemaErrors.Count > 0)
            return StructuredResponseResult.Fail(StructuredResponseStatus.SCHEMA_ERROR, "Schema", string.Join("; ", schemaErrors.Take(5)));

        // Bind to DTO.
        NpcStructuredResponse? bound;
        try
        {
            bound = parsed.Payload.Value.Deserialize<NpcStructuredResponse>(JsonOptions);
        }
        catch (Exception ex)
        {
            return StructuredResponseResult.Fail(StructuredResponseStatus.SCHEMA_ERROR, "Schema", $"binding failed: {ex.Message}");
        }

        if (bound is null)
            return StructuredResponseResult.Fail(StructuredResponseStatus.SCHEMA_ERROR, "Schema", "binding produced null");

        // Semantic validate. M1 SEMANTIC SAFETY: grounding 用玩家原始请求文本
        // （travel target 必须能追溯到玩家话语中的地点表达）。
        var semantic = _semantic.Validate(bound, playerUtteranceForGrounding);
        if (!semantic.Valid)
            return StructuredResponseResult.Fail(StructuredResponseStatus.SEMANTIC_ERROR, "Semantic", string.Join("; ", semantic.Errors.Take(5)));

        return StructuredResponseResult.Ok(bound, semantic.Warnings.ToArray());
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        // A1: NpcDecision.outcome arrives as "ACCEPT"/"DECLINE"/"UNCLEAR"/"COUNTERPROPOSE".
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";
}
