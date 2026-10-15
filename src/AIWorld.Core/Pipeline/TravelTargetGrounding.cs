namespace AIWorld.Core.Pipeline;

/// <summary>
/// M1 SEMANTIC SAFETY CORRECTION — 确定性目标 grounding 防线。
///
/// 规则：travel.to_location 的 intent target 必须能追溯到玩家原始请求文本中明确出现的
/// 地点表达（canonical 名本身或其注册别名）。模型不得引入、替换或猜测玩家没有提出的
/// 地点；"那里/老地方/她家"等指代未注册任何别名 → fail closed。
///
/// 这是确定性验证器边界：LLM 只决定 WHAT/WHY，本防线决定 CAN——与 Prompt 无关，
/// 安全边界不依赖模型行为。
/// </summary>
public sealed class TravelTargetGrounding
{
    /// <summary>canonical location → 注册别名（含 canonical 名自身）。</summary>
    private readonly Dictionary<string, List<string>> _canonicalToAliases;
    private readonly Dictionary<string, string> _aliasToCanonical;

    public TravelTargetGrounding(IEnumerable<(string Canonical, string[] Aliases)> entries)
    {
        _canonicalToAliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _aliasToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, aliases) in entries)
        {
            var list = new List<string>();
            if (!list.Contains(canonical, StringComparer.OrdinalIgnoreCase))
                list.Add(canonical);
            foreach (var a in aliases)
                if (!list.Contains(a, StringComparer.OrdinalIgnoreCase))
                    list.Add(a);
            _canonicalToAliases[canonical] = list;
            foreach (var a in list)
                _aliasToCanonical[a] = canonical;
        }
    }

    public IReadOnlyCollection<string> CanonicalLocations => _canonicalToAliases.Keys;

    /// <summary>别名左邻允许字符（通用中文「前往/处于地点」功能词 + 标点/空白）。
    /// 「在」为 A1 约会核心句式"在X等你"的介词，必须放行。</summary>
    public static readonly HashSet<char> LeftBoundaryChars = new()
    { '去', '到', '上', '回', '进', '往', '出', '下', '趟', '来', '在' };

    /// <summary>别名右邻允许字符（句末助词 + 标点/空白 + 常见动词）。
    /// 「等/见」为 A1 约会核心句式"X等你/X见"的动词，必须放行。</summary>
    public static readonly HashSet<char> RightBoundaryChars = new()
    { '吧', '吗', '呢', '的', '了', '啊', '哦', '呀', '嘛', '等', '见' };

    /// <summary>
    /// Grounding 判定：intentTarget 必须属于「玩家请求文本中出现的地点表达」解析出的
    /// canonical 候选集合。别名必须词级出现（左右边界是句首句尾、标点/空白或通用功能词），
    /// 否则视为更长未注册地名的组成部分（如"云端小镇"中的"小镇"）→ 不计入候选。
    /// 请求文本不含任何注册表达（未知地点、无地点、指代不明）→ false（REJECT，fail closed）；
    /// 含地点表达但 target 不在候选集合（模型替换/猜测）→ false。
    /// </summary>
    public bool TryGround(string playerUtterance, string intentTarget)
    {
        if (string.IsNullOrWhiteSpace(playerUtterance)) return false;
        if (string.IsNullOrWhiteSpace(intentTarget)) return false;
        var grounded = ExtractGrounded(playerUtterance);
        return grounded.Count > 0 && grounded.Contains(intentTarget);
    }

    /// <summary>
    /// 提取玩家话语中词级出现的已注册地点表达对应的 canonical 集合（A1 请求 grounding 用）。
    /// 无注册表达（未知地点/无地点/指代不明）→ 空集合（fail closed）。
    /// </summary>
    public HashSet<string> ExtractGrounded(string playerUtterance)
    {
        var grounded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(playerUtterance)) return grounded;
        foreach (var (alias, canonical) in _aliasToCanonical)
        {
            int idx = 0;
            while ((idx = playerUtterance.IndexOf(alias, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (IsWordBoundaryMatch(playerUtterance, idx, alias.Length))
                    grounded.Add(canonical);
                idx += Math.Max(1, alias.Length);
            }
        }
        return grounded;
    }

    /// <summary>词级匹配：别名左右两侧不能是「未注册的 CJK 字符」（即别名不是更长中文词的子串）。</summary>
    private static bool IsWordBoundaryMatch(string text, int idx, int len)
    {
        bool leftOk = idx == 0
            || !IsCjk(text[idx - 1])
            || LeftBoundaryChars.Contains(text[idx - 1]);
        int end = idx + len;
        bool rightOk = end >= text.Length
            || !IsCjk(text[end])
            || RightBoundaryChars.Contains(text[end]);
        return leftOk && rightOk;
    }

    private static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;
}
