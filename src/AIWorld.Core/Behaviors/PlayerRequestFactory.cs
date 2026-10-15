using System.Text.RegularExpressions;
using AIWorld.Core.Contracts;
using AIWorld.Core.Pipeline;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// A1 — 玩家请求创建器（Core，确定性，离线可测）。从玩家话语 grounding 地点与时间，
/// 生成 PlayerRequest（含不可变 termsHash）。
///
/// 安全规则（fail closed）：
/// - 话语中无已注册地点表达（未知/无地点/指代不明）→ 不创建请求（返回 null，NPC 自然回复）。
/// - 话语匹配到多个不同地点表达 → 不创建请求（要求澄清，避免猜地点）。
/// - 无明确数字时间 → 视为立即请求（ImmediateTravel）；"晚上N点"或 HHMM → FutureAppointment。
/// - groundedTarget 只来自玩家话语，模型不得替换。
/// </summary>
public sealed class PlayerRequestFactory
{
    private readonly TravelTargetGrounding _grounding;
    private readonly Func<string> _idGen;

    // "晚上9点" / "晚9点" / "今晚9点" → 2100；"21:00" / "21：00" → 2100；"9点" → 900（白天假设）。
    private static readonly Regex EveningHour = new(@"晚(?:上)?\s*(\d{1,2})\s*点", RegexOptions.Compiled);
    private static readonly Regex ColonTime = new(@"(\d{1,2})\s*[:：]\s*(\d{2})", RegexOptions.Compiled);
    private static readonly Regex PlainHour = new(@"(?:早上|上午)?\s*(\d{1,2})\s*点", RegexOptions.Compiled);

    public PlayerRequestFactory(TravelTargetGrounding grounding, Func<string>? idGen = null)
    {
        _grounding = grounding;
        _idGen = idGen ?? (() => Guid.NewGuid().ToString("N"));
    }

    /// <summary>尝试从玩家话语创建请求。无法确定性 grounding → null（不产生可执行意图）。</summary>
    public PlayerRequest? TryCreate(string npcId, string utterance, int gameDay, int gameTime)
    {
        if (string.IsNullOrWhiteSpace(npcId) || string.IsNullOrWhiteSpace(utterance)) return null;

        var grounded = _grounding.ExtractGrounded(utterance);
        if (grounded.Count != 1) return null; // 0=无地点/指代不明，>1=多候选需澄清 → fail closed
        var target = grounded.First();

        var kind = new[] { "陪我", "跟我", "一起去", "come with me", "follow me" }
            .Any(expression => utterance.Contains(expression, StringComparison.OrdinalIgnoreCase))
            ? PlayerRequestKind.AccompanyPlayer : PlayerRequestKind.ImmediateTravel;
        int requestedTime = gameTime;
        if (TryParseTime(utterance, out var parsed))
        {
            kind = PlayerRequestKind.FutureAppointment;
            requestedTime = parsed;
        }

        return new PlayerRequest
        {
            RequestId = _idGen(),
            NpcId = npcId,
            RequestKind = kind,
            OriginalUtterance = utterance,
            GroundedTarget = target,
            RequestedGameDay = utterance.Contains("明天", StringComparison.Ordinal) ? gameDay + 1 : gameDay,
            RequestedGameTime = requestedTime,
            TermsHash = PlayerRequest.ComputeTermsHash(npcId, kind, target, utterance.Contains("明天", StringComparison.Ordinal) ? gameDay + 1 : gameDay, requestedTime),
            SchemaVersion = "1",
        };
    }

    private static bool TryParseTime(string utterance, out int time)
    {
        var m = EveningHour.Match(utterance);
        if (m.Success)
        {
            // "晚上N点" 为 12 小时制夜晚：晚上9点=21:00。h∈[6,11]→h+12∈[18,23]。
            int h = int.Parse(m.Groups[1].Value);
            time = (h + 12) * 100;
            return time is >= 1800 and <= 2359;
        }
        m = ColonTime.Match(utterance);
        if (m.Success)
        {
            int h = int.Parse(m.Groups[1].Value);
            int mi = int.Parse(m.Groups[2].Value);
            time = h * 100 + mi;
            return h is >= 0 and <= 23 && mi <= 59;
        }
        m = PlainHour.Match(utterance);
        if (m.Success)
        {
            int h = int.Parse(m.Groups[1].Value);
            time = h * 100;
            return h is >= 6 and <= 23;
        }
        time = 0;
        return false;
    }
}
