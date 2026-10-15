using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// A1 NPC 决定结果。只有 ACCEPT 产生执行权；DECLINE/UNCLEAR 不产生；
/// COUNTERPROPOSE 在玩家明确确认前不产生。
/// </summary>
public enum NpcDecisionOutcome
{
    Accept,
    Decline,
    Unclear,
    CounterPropose,
}

/// <summary>
/// A1 — NPC 对玩家请求的主观决定。由模型产生（依据人格/关系/时间/位置/日程/节日），
/// 但模型只能提供决定，不得直接创建 TravelLease、修改坐标或执行移动。
/// requestId + npcId + termsHash 必须与 PlayerRequest 完全匹配。
/// </summary>
public sealed class NpcDecision
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("npcId")]
    public required string NpcId { get; init; }

    [JsonPropertyName("termsHash")]
    public required string TermsHash { get; init; }

    [JsonPropertyName("outcome")]
    public required NpcDecisionOutcome Outcome { get; init; }

    /// <summary>反提议的新条件（COUNTERPROPOSE 时非空；玩家确认后绑定新 termsHash）。</summary>
    [JsonPropertyName("proposedTerms")]
    public string? ProposedTerms { get; init; }

    /// <summary>模型生成的台词/说明（可选，不参与执行判定）。</summary>
    [JsonPropertyName("flavorDialogue")]
    public string? FlavorDialogue { get; init; }

    /// <summary>Schema 版本。</summary>
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }
}
