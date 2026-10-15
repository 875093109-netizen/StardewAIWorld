using System.Text;
using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// A1 玩家请求种类：对话结束后立即同行/前往，或在未来游戏时间前往指定地点等候。
/// </summary>
public enum PlayerRequestKind
{
    ImmediateTravel,
    FutureAppointment,
    AccompanyPlayer,
}

/// <summary>
/// A1 — 玩家对 NPC 提出的请求。玩家提出请求不等于 NPC 接受命令：
/// 只有经过 NPC 主观决定（NpcDecision）且明确 ACCEPT 后才产生执行权。
/// requestId + npcId + termsHash 三者必须与 NPC 决定完全匹配。
/// </summary>
public sealed class PlayerRequest
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("npcId")]
    public required string NpcId { get; init; }

    [JsonPropertyName("requestKind")]
    public required PlayerRequestKind RequestKind { get; init; }

    /// <summary>玩家原始话语（用于 grounding，不得被模型改写）。</summary>
    [JsonPropertyName("originalUtterance")]
    public required string OriginalUtterance { get; init; }

    /// <summary>grounded 后的 canonical 目标地点（确定性解析，非模型猜测）。</summary>
    [JsonPropertyName("groundedTarget")]
    public required string GroundedTarget { get; init; }

    /// <summary>目标游戏日（即时请求 = 当前日）。</summary>
    [JsonPropertyName("requestedGameDay")]
    public required int RequestedGameDay { get; init; }

    /// <summary>目标游戏时间（HHMM，如 2100 = 21:00；即时请求 = 当前时间）。</summary>
    [JsonPropertyName("requestedGameTime")]
    public required int RequestedGameTime { get; init; }

    /// <summary>
    /// 不可变条款哈希。绑定 npcId/kind/groundedTarget/day/time；NPC 决定必须携带
    /// 完全相同的 termsHash，否则视为绑定错误（fail closed）。
    /// </summary>
    [JsonPropertyName("termsHash")]
    public required string TermsHash { get; init; }

    /// <summary>Schema 版本。</summary>
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// 计算不可变条款哈希（SHA256，规范化小写，字段按固定顺序）。确定性、无游戏依赖。
    /// </summary>
    public static string ComputeTermsHash(
        string npcId, PlayerRequestKind kind, string groundedTarget, int gameDay, int gameTime)
    {
        var canonical = $"{npcId.ToLowerInvariant()}|{kind}|{groundedTarget.ToLowerInvariant()}|{gameDay}|{gameTime}";
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
