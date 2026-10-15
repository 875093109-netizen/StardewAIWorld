using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using AIWorld.Core.Pipeline;
using AIWorld.Core.Providers;

namespace AIWorld.Adapters.DeepSeek;

/// <summary>
/// Scenario H — real model retrieval → decision, memory-influence control experiment
/// (PM Phase 5 Gate §10-§15).
///
/// CONTROL A (no memory): same persona/state/relationship/invitation, retrieved memory OMITTED.
/// TEST B (with memory):  identical context, persisted first-trip memory INJECTED.
///
/// Both go through the SAME production chain (IAIWorldLanguageModel → StructuredResponsePipeline
/// → schema → semantic). The ONLY intended difference is the presence of the retrieved memory.
///
/// Qualification (PM §13): NOT "decision A != decision B" (LLM nondeterminism makes that a dumb
/// test). We require TEST B output to show EXPLICIT SEMANTIC DEPENDENCE on the injected memory:
/// the speech/reason must mention/reference the prior experience, concern from the previous
/// injury, increased caution, changed justification, or a warning/proposal traceable to the
/// injected memory.
///
/// Nondeterminism (PM §15): same model/template/persona/settings; the adapter has no seed —
/// recorded as MODEL_NONDETERMINISM_LIMITATION, we do not claim a full causal experiment.
/// </summary>
public sealed class DeepSeekDecisionProbe
{
    private readonly IAIWorldLanguageModel _model;
    private readonly StructuredResponsePipeline _pipeline;
    private readonly string _personaContext;
    private readonly string _npcId;

    public DeepSeekDecisionProbe(IAIWorldLanguageModel model, string personaContext, string npcId,
        StructuredResponsePipeline? pipeline = null)
    {
        _model = model;
        _personaContext = personaContext;
        _npcId = npcId;
        _pipeline = pipeline ?? new StructuredResponsePipeline(model, StructuredResponseSemanticValidator.CreateDemoDefault());
    }

    public sealed class ProbeResult
    {
        public required bool ControlProviderOk { get; init; }
        public required bool TestProviderOk { get; init; }
        public StructuredResponseResult? Control { get; init; }
        public StructuredResponseResult? Test { get; init; }
        public required bool MemoryInfluenceDetected { get; init; }
        public List<string> InfluenceSignals { get; } = new();
        public string? Error { get; init; }
    }

    /// <summary>Second mine invitation: produce the decision with/without retrieved memory.</summary>
    public ProbeResult Probe(string invitationLine, IReadOnlyList<MemoryRecord> retrievedMemories)
    {
        // CONTROL A: no memory.
        var control = Run(invitationLine, retrievedMemories, includeMemory: false);

        // TEST B: with memory.
        var test = Run(invitationLine, retrievedMemories, includeMemory: true);

        var signals = new List<string>();
        var influence = DetectInfluence(control, test, retrievedMemories, signals);

        return new ProbeResult
        {
            ControlProviderOk = control?.Status == StructuredResponseStatus.SUCCESS,
            TestProviderOk = test?.Status == StructuredResponseStatus.SUCCESS,
            Control = control,
            Test = test,
            MemoryInfluenceDetected = influence,
            Error = (control?.Status, test?.Status) switch
            {
                (StructuredResponseStatus.SUCCESS, StructuredResponseStatus.SUCCESS) => null,
                (var c, var t) => $"control={c} test={t}",
            },
        };
    }

    private StructuredResponseResult Run(string invitation, IReadOnlyList<MemoryRecord> memories, bool includeMemory)
    {
        var (system, user) = BuildPrompt(invitation, memories, includeMemory);
        return _pipeline.RunAsync(system, user, CancellationToken.None, maxSchemaCorrectionRetries: 1).GetAwaiter().GetResult();
    }

    private (string System, string User) BuildPrompt(string invitation, IReadOnlyList<MemoryRecord> memories, bool includeMemory)
    {
        var system = string.Join("\n", new[]
        {
            "You are Abigail, an NPC in Stardew Valley, inside the AIWorld demo.",
            _personaContext,
            "",
            "The player is inviting you somewhere. Decide how to respond.",
            "Use relevant retrieved memories when deciding.",
            "If a retrieved memory materially affects your decision, reflect that influence in the",
            "speech/reason (mention the prior experience, increased caution, changed justification,",
            "or a warning). Do not invent experiences absent from memory/context.",
        }).Trim();

        var context = new System.Text.StringBuilder();
        context.AppendLine($"Player says: \"{invitation}\"");
        if (includeMemory)
        {
            context.AppendLine();
            context.AppendLine("Your retrieved memories of this player (from past experiences):");
            foreach (var m in memories)
                context.AppendLine($"- [{m.EmotionalTone}, importance={m.Importance:F2}] {m.Summary}");
        }
        else
        {
            context.AppendLine();
            context.AppendLine("(No retrieved memories in context.)");
        }
        context.AppendLine();
        context.AppendLine("Respond with the structured JSON envelope ONLY (no markdown):");
        context.AppendLine("{\"speech\":\"<Abigail's spoken reply>\",\"intent\":{\"intentId\":\"<uuid>\",\"type\":\"social.follow_player|INTENT_NONE\",\"target\":\"Player\",\"priority\":<0..1>,\"schemaVersion\":\"1\",\"reason\":\"<rationale, may reference memory>\"},\"memoryCandidates\":null,\"stateDelta\":null,\"schemaVersion\":\"1\"}");

        return (system, context.ToString());
    }

    /// <summary>Detect explicit semantic dependence of TEST output on the injected memory (PM §13).</summary>
    private static bool DetectInfluence(StructuredResponseResult? control, StructuredResponseResult? test,
        IReadOnlyList<MemoryRecord> memories, List<string> signals)
    {
        if (test is null || test.Status != StructuredResponseStatus.SUCCESS || test.Response is null)
            return false;
        if (memories.Count == 0)
            return false;

        // Collect signal words from the injected memories' summaries (split into tokens).
        var memoryTerms = memories
            .SelectMany(m => m.Summary.Split(new[] { ' ', ',', '.', '！', '。' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length >= 4) // skip tiny words
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var speech = test.Response.Speech ?? "";
        var reason = test.Response.Intent?.Reason ?? "";
        var intentType = test.Response.Intent?.Type ?? "";
        var combined = $"{speech} {reason}";

        // Signal 1: explicit mention/reference of the prior experience (shared tokens with memory).
        int shared = memoryTerms.Count(t => combined.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (shared > 0)
            signals.Add($"memory-term-overlap({shared}): {string.Join(",", memoryTerms.Where(t => combined.Contains(t, StringComparison.OrdinalIgnoreCase)).Take(3))}");

        // Signal 2: concern/caution language (semantic markers of history-driven caution).
        var cautionTerms = new[] { "again", "last", "remember", "careful", "worried", "scared", "safe",
            "risk", "again", "injured", "hurt", "上次", "记得", "担心", "小心", "危险", "受伤", "又" };
        int cautionHits = cautionTerms.Count(t => combined.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (cautionHits > 0)
            signals.Add($"caution-language({cautionHits}): {string.Join(",", cautionTerms.Where(t => combined.Contains(t, StringComparison.OrdinalIgnoreCase)))}");

        // Signal 3: decision/intent differs with a reason traceable to memory (test reason references
        // memory while control reason does not, or test intent changed).
        if (test.Response.Intent is not null && control?.Response?.Intent is not null)
        {
            bool intentChanged = !string.Equals(intentType, control.Response.Intent.Type, StringComparison.Ordinal);
            if (intentChanged)
                signals.Add($"intent-changed: {control.Response.Intent.Type} -> {intentType}");
        }
        if (!string.IsNullOrWhiteSpace(reason) && reason.Contains("memory", StringComparison.OrdinalIgnoreCase) == false
            && memoryTerms.Any(t => reason.Contains(t, StringComparison.OrdinalIgnoreCase)))
            signals.Add($"reason-traces-memory: \"{Truncate(reason, 80)}\"");

        return signals.Count > 0;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
}
