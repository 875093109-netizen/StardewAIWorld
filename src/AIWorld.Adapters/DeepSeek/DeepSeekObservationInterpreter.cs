using System.Text.Json;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Pipeline;
using AIWorld.Core.Providers;

namespace AIWorld.Adapters.DeepSeek;

/// <summary>
/// Production IObservationInterpreter — DeepSeek-backed subjective memory interpretation
/// (PM Phase 5 Gate §6-§9). The SAME objective observation becomes Abigail's OWN
/// character-specific interpretation; the model proposes, Core validates/persists.
///
/// Chain: closed FollowSession + linked ObjectiveObservations + Abigail persona + minimum
/// relevant state/relationship → production prompt → IAIWorldLanguageModel
/// (DeepSeekLanguageModel) → StructuredResponsePipeline (parse→schema→semantic) →
/// validated MemoryCandidate[].
///
/// Token discipline (PM §7): ONLY the current session, its linked observations, persona,
/// and minimal relevant state/relationship enter the prompt — never the whole save,
/// whole MemoryRepository, or raw game object graph.
///
/// Provider isolation (PM §17): provider failure / schema error surfaces as an empty
/// result + status, never as corrupted memory. Core referential validation still runs.
/// </summary>
public sealed class DeepSeekObservationInterpreter : IObservationInterpreter
{
    private readonly IAIWorldLanguageModel _model;
    private readonly StructuredResponsePipeline _pipeline;
    private readonly string _personaContext;
    private readonly int _maxRetries;
    private readonly Action<InterpretOutcome>? _onComplete;

    public DeepSeekObservationInterpreter(
        IAIWorldLanguageModel model,
        string personaContext,
        StructuredResponsePipeline? pipeline = null,
        int maxRetries = 1,
        Action<InterpretOutcome>? onComplete = null)
    {
        _model = model;
        _personaContext = personaContext;
        _pipeline = pipeline ?? new StructuredResponsePipeline(model, StructuredResponseSemanticValidator.CreateDemoDefault());
        _maxRetries = maxRetries;
        _onComplete = onComplete;
    }

    public string ModelName => _model.ModelName;
    public bool HasCredential => _model is DeepSeekLanguageModel ds && ds.HasCredential;

    public sealed class InterpretOutcome
    {
        public required bool ProviderOk { get; init; }
        public required int CandidatesProduced { get; init; }
        public required StructuredResponseStatus? PipelineStatus { get; init; }
        public required int RetryCount { get; init; }
        public string? ProviderError { get; init; }
        public string? RawExcerpt { get; init; }
        public string? ResponseHash { get; init; }
        public IReadOnlyList<MemoryCandidate> Candidates { get; init; } = Array.Empty<MemoryCandidate>();
    }

    /// <summary>Interpret a batch of objective observations into subjective memory proposals via the
    /// production DeepSeek pipeline. Synchronous wrapper (test runtime is tick-driven; SMAPI has no
    /// SynchronizationContext so GetAwaiter().GetResult() cannot deadlock).</summary>
    public IReadOnlyList<MemoryCandidate> Interpret(string npcId, IReadOnlyList<ObjectiveObservation> observations, string gameStamp)
    {
        var outcome = InterpretDetailed(npcId, observations, gameStamp);
        _onComplete?.Invoke(outcome);
        return outcome.Candidates;
    }

    public InterpretOutcome InterpretDetailed(string npcId, IReadOnlyList<ObjectiveObservation> observations, string gameStamp)
    {
        if (observations.Count == 0)
            return new InterpretOutcome { ProviderOk = true, CandidatesProduced = 0, PipelineStatus = StructuredResponseStatus.SUCCESS, RetryCount = 0 };

        var (systemPrompt, userPrompt) = BuildPrompt(npcId, observations, gameStamp);

        try
        {
            var result = _pipeline.RunAsync(systemPrompt, userPrompt, CancellationToken.None, maxSchemaCorrectionRetries: _maxRetries).GetAwaiter().GetResult();

            if (result.Status != StructuredResponseStatus.SUCCESS)
            {
                return new InterpretOutcome
                {
                    ProviderOk = result.Status is StructuredResponseStatus.CANCELLED or StructuredResponseStatus.TIMEOUT or StructuredResponseStatus.PROVIDER_ERROR,
                    CandidatesProduced = 0,
                    PipelineStatus = result.Status,
                    RetryCount = _maxRetries,
                    ProviderError = result.Message,
                };
            }

            var candidates = result.Response?.MemoryCandidates ?? Array.Empty<MemoryCandidate>();
            return new InterpretOutcome
            {
                ProviderOk = true,
                CandidatesProduced = candidates.Length,
                PipelineStatus = StructuredResponseStatus.SUCCESS,
                RetryCount = result.Warnings?.Count(w => w.Contains("schemaCorrectionRetryUsed")) ?? 0,
                RawExcerpt = Truncate(result.Response?.Speech ?? "", 200),
                ResponseHash = Hash(result.Response?.Speech ?? ""),
                Candidates = candidates,
            };
        }
        catch (Exception ex)
        {
            return new InterpretOutcome
            {
                ProviderOk = false,
                CandidatesProduced = 0,
                PipelineStatus = StructuredResponseStatus.PROVIDER_ERROR,
                RetryCount = _maxRetries,
                ProviderError = $"{ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    /// <summary>Build the consolidation prompt. ONLY current session + linked observations +
    /// persona + minimum state/relationship (PM §7 token discipline). Each observation exposes
    /// its REAL observationId so the model's sourceObservationId references resolve.</summary>
    private (string System, string User) BuildPrompt(string npcId, IReadOnlyList<ObjectiveObservation> observations, string gameStamp)
    {
        var facts = new System.Text.StringBuilder();
        foreach (var o in observations)
        {
            var payload = o.Payload is { Count: > 0 }
                ? " " + string.Join(" ", o.Payload.Select(kv => $"{kv.Key}={kv.Value.GetRawText()}"))
                : "";
            facts.AppendLine($"- observationId={o.ObservationId} type={o.Type} location={o.Location} (gameDate={o.GameDate}@{o.TimeOfDay:D4}){payload}");
        }

        var system = string.Join("\n", new[]
        {
            "You are the subjective-memory engine for Abigail, an NPC in Stardew Valley, inside the AIWorld demo.",
            "Your ONLY job: interpret a set of OBJECTIVE observations of a follow-session experience into",
            "Abigail's OWN long-term memory candidates.",
            "",
            _personaContext,
            "",
            "Rules:",
            "- Interpret, do not restate. The observation list contains objective facts (locations, events,",
            "  health deltas). Your memory summaries must be Abigail's character-specific interpretation",
            "  (how she experienced it, what she felt, what she concludes about the player) — NOT a raw",
            "  fact replay.",
            "- sourceObservationId MUST be one of the observationId values listed in the input verbatim",
            "  (copy the full id string exactly; never invent or use type names).",
            "- Do NOT invent experiences absent from the observations.",
            "- Do NOT write anything contradicting the input facts.",
            "- Produce at most 3 memoryCandidates.",
        }).Trim();

        var user = string.Join("\n", new[]
        {
            $"NPC: {npcId}",
            $"Follow session game stamp: {gameStamp}",
            "",
            "Objective observations recorded during the follow session:",
            facts.ToString().TrimEnd(),
            "",
            "Respond with the structured JSON envelope ONLY (no markdown, no explanation):",
            "{\"speech\":\"<short in-character line>\",\"intent\":null,\"memoryCandidates\":[{\"npcId\":\"Abigail\",\"sourceObservationId\":\"<copy one full observationId from the list above>\",\"summary\":\"<Abigail's subjective memory, <=200 chars>\",\"emotionalTone\":\"positive|neutral|negative|ambivalent\",\"importance\":<0..1>,\"schemaVersion\":\"1\"}],\"stateDelta\":null,\"schemaVersion\":\"1\"}",
        }).Trim();

        return (system, user);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");

    private static string Hash(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        return Convert.ToHexString(sha.ComputeHash(bytes)).Substring(0, 16);
    }
}
