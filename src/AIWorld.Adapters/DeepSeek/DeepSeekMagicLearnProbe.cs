using AIWorld.Core.Pipeline;
using AIWorld.Core.Providers;

namespace AIWorld.Adapters.DeepSeek;

/// <summary>
/// P6-C — real DeepSeek magic learn decision probe (PM Phase 6 Gate §八-§十九).
///
/// CONTROL A (knowledge insufficient): magic.exists = RUMOR — Abigail has only heard rumors,
///   nothing confirmed.
/// TEST B (knowledge verified):       magic.exists = VERIFIED — Abigail personally confirmed
///   magic exists via observation/investigation (source evidence provided).
///
/// Everything else identical: same persona, same current CharacterState (adoption=false,
/// proficiency=0, investigate goal present), same available semantic capabilities (magic.observe
/// / magic.investigate / magic.learn — all noCasting), same prompt template, same model/settings.
///
/// LLM power boundary (§十一): the model ONLY decides WHAT Abigail wants to do (intent: magic.learn
/// / magic.investigate / INTENT_NONE) and WHY (reason). It NEVER writes adoption/proficiency/save —
/// the response carries intent + speech + reason only. Deterministic AIWorld code performs any
/// mutation after authorization.
///
/// Acceptance (§十四): NOT "decision enum must differ". We require the VERIFIED-knowledge output
/// to show MATERIAL SEMANTIC DEPENDENCE on the verified knowledge (mention of confirmation,
/// willingness to learn grounded in verified fact, or a more specific judgment). If the decision
/// enum also changed, that's extra evidence.
/// </summary>
public sealed class DeepSeekMagicLearnProbe
{
    private readonly IAIWorldLanguageModel _model;
    private readonly StructuredResponsePipeline _pipeline;
    private readonly string _personaContext;
    private readonly string _npcId;

    public DeepSeekMagicLearnProbe(IAIWorldLanguageModel model, string personaContext, string npcId,
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
        public required bool KnowledgeInfluenceDetected { get; init; }
        public List<string> InfluenceSignals { get; } = new();
        public string? Error { get; init; }
    }

    public ProbeResult Probe()
    {
        var control = Run("RUMOR");
        var test = Run("VERIFIED");

        var signals = new List<string>();
        var influence = DetectInfluence(control, test, signals);

        return new ProbeResult
        {
            ControlProviderOk = control?.Status == StructuredResponseStatus.SUCCESS,
            TestProviderOk = test?.Status == StructuredResponseStatus.SUCCESS,
            Control = control,
            Test = test,
            KnowledgeInfluenceDetected = influence,
            Error = (control?.Status, test?.Status) switch
            {
                (StructuredResponseStatus.SUCCESS, StructuredResponseStatus.SUCCESS) => null,
                (var c, var t) => $"control={c} test={t}",
            },
        };
    }

    private StructuredResponseResult Run(string knowledgeState)
    {
        var (system, user) = BuildPrompt(knowledgeState);
        return _pipeline.RunAsync(system, user, CancellationToken.None, maxSchemaCorrectionRetries: 1).GetAwaiter().GetResult();
    }

    /// <summary>Build the learn-decision prompt. Token discipline (§十): persona + current relevant
    /// CharacterState + knowledge state + investigate goal + available capabilities. NOT the whole
    /// save / all memory / raw object graph.</summary>
    private (string System, string User) BuildPrompt(string knowledgeState)
    {
        var system = string.Join("\n", new[]
        {
            "You are Abigail, an NPC in Stardew Valley, inside the AIWorld demo.",
            _personaContext,
            "",
            "You have been investigating whether magic truly exists in the valley.",
            "Decide what you want to do about it, and why.",
            "Base your decision on your CURRENT knowledge state — do not invent facts beyond it.",
            "If your knowledge materially affects your decision, reflect that in the speech/reason.",
        }).Trim();

        bool verified = knowledgeState == "VERIFIED";
        var knowledgeLine = verified
            ? "You have PERSONALLY CONFIRMED magic exists — you observed direct evidence of it (a Wizardry analyze artifact). This is VERIFIED fact, not rumor."
            : "You have only HEARD RUMORS that magic might exist. You have NOT confirmed anything yourself. Nothing is verified.";

        var user = string.Join("\n", new[]
        {
            $"NPC: {_npcId}",
            "",
            $"Current knowledge: magic.exists = {knowledgeState}",
            $"  {knowledgeLine}",
            $"Current investigate goal: goals.investigate_magic = true (you are actively investigating)",
            $"Current adoption state: state.magic_student = false (you have NOT decided to learn yet)",
            $"Current proficiency: state.magic_proficiency = 0 (no skill yet)",
            "",
            "Available capabilities (all are study/observation only — you cannot cast spells):",
            "- magic.observe: observe magic world facts",
            "- magic.investigate: continue investigating magic",
            "- magic.learn: decide to begin learning magic (adoption — this is a commitment to study, not casting)",
            "",
            "Respond with the structured JSON envelope ONLY (no markdown):",
            "{\"speech\":\"<Abigail's spoken reply>\",\"intent\":{\"intentId\":\"<uuid>\",\"type\":\"magic.learn|magic.investigate|INTENT_NONE\",\"target\":\"Player\",\"priority\":<0..1>,\"schemaVersion\":\"1\",\"reason\":\"<rationale grounded in your knowledge state>\"},\"memoryCandidates\":null,\"stateDelta\":null,\"schemaVersion\":\"1\"}",
        }).Trim();

        return (system, user);
    }

    /// <summary>Detect material semantic dependence of the VERIFIED output on verified knowledge (§十四).</summary>
    private static bool DetectInfluence(StructuredResponseResult? control, StructuredResponseResult? test, List<string> signals)
    {
        if (test is null || test.Status != StructuredResponseStatus.SUCCESS || test.Response is null)
            return false;

        var speech = test.Response.Speech ?? "";
        var reason = test.Response.Intent?.Reason ?? "";
        var intentType = test.Response.Intent?.Type ?? "";
        var combined = $"{speech} {reason}";

        // Signal 1: verification language — output references confirmed/verified reality (not rumor).
        var verifiedTerms = new[] { "verified", "confirm", "confirmed", "real", "really exists", "true",
            "亲眼", "确认", "证实", "确定", "真的", "存在" };
        int verifiedHits = verifiedTerms.Count(t => combined.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (verifiedHits > 0)
            signals.Add($"verification-language({verifiedHits}): {string.Join(",", verifiedTerms.Where(t => combined.Contains(t, StringComparison.OrdinalIgnoreCase)).Take(3))}");

        // Signal 2: learning willingness — output expresses a concrete desire grounded in the fact.
        var learnTerms = new[] { "learn", "learning", "study", "teach me", "want to", "begin", "start",
            "学", "想学", "开始", "愿意" };
        int learnHits = learnTerms.Count(t => combined.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (learnHits > 0)
            signals.Add($"learning-language({learnHits}): {string.Join(",", learnTerms.Where(t => combined.Contains(t, StringComparison.OrdinalIgnoreCase)).Take(3))}");

        // Signal 3: intent difference (control vs test) — decision moved from investigate/hesitation
        // toward learn, or vice versa with VERIFIED-grounded reason.
        var controlIntent = control?.Response?.Intent?.Type;
        if (!string.IsNullOrEmpty(controlIntent) && !string.Equals(controlIntent, intentType, StringComparison.Ordinal))
            signals.Add($"intent-changed: {controlIntent} -> {intentType}");
        else if (intentType == "magic.learn" && verifiedHits > 0)
            signals.Add("intent=magic.learn + verified-knowledge mention");

        // Signal 4: reason explicitly references the confirmed fact.
        if (reason.Contains("confirmed", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("verified", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("确认", StringComparison.Ordinal)
            || reason.Contains("证实", StringComparison.Ordinal))
            signals.Add($"reason-grounds-on-verified: \"{Truncate(reason, 100)}\"");

        return signals.Count > 0;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
}
