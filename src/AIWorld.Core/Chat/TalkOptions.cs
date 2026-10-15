namespace AIWorld.Core.Chat;

/// <summary>User preferences only. None of these settings grant world-action permissions.</summary>
public sealed record TalkOptions
{
    public bool ShowOverlay { get; set; } = true;
    public bool AutoConversations { get; set; } = true;
    public int Left { get; set; } = 24;
    public int Top { get; set; } = 100;
    public int Width { get; set; } = 520;
    public int Height { get; set; } = 290;
    public double Opacity { get; set; } = 0.80;
    public int IntervalSeconds { get; set; } = 90;
    public int MaxParticipants { get; set; } = 4;
    public int MaxTurns { get; set; } = 6;
    public int DailyAmbientCalls { get; set; } = 24;
    public int RecentMemories { get; set; } = 4;
    public bool IncludeEnvironment { get; set; } = true;
    public bool IncludeHistory { get; set; } = true;
    public const string LegacyDefaultPrompt = "使用简体中文。简洁自然，贴合角色性格，回应当前话题，不要复述人物资料。";
    // Empty means the localized built-in style; custom prompts are preserved verbatim.
    public string GlobalPrompt { get; set; } = "";
    public Dictionary<string, string> PersonaNotes { get; set; } = new(StringComparer.Ordinal);
    public string Endpoint { get; set; } = "https://api.deepseek.com/chat/completions";
    // Blank keeps the existing DEEPSEEK_MODEL environment setting.
    public string Model { get; set; } = "";
    public double Temperature { get; set; } = 0.7;
    public int MaxOutputTokens { get; set; } = 1200;
    public bool Thinking { get; set; }
    public int Revision { get; set; } = 1;

    public TalkOptions Normalize() => this with
    {
        Left = Math.Max(0, Left), Top = Math.Max(0, Top), Width = Math.Clamp(Width, 360, 1000), Height = Math.Clamp(Height, 180, 700),
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .35, 1) : .8,
        IntervalSeconds = Math.Clamp(IntervalSeconds, 30, 900), MaxParticipants = Math.Clamp(MaxParticipants, 2, 4),
        MaxTurns = Math.Clamp(MaxTurns, 2, 8), DailyAmbientCalls = Math.Clamp(DailyAmbientCalls, 0, 200),
        RecentMemories = Math.Clamp(RecentMemories, 0, 6),
        GlobalPrompt = ContextBudget.Clip(GlobalPrompt, 6000), Model = ContextBudget.Clip(Model?.Trim(), 150),
        Endpoint = (Endpoint ?? "").Trim(), MaxOutputTokens = Math.Clamp(MaxOutputTokens, 128, 4096),
        Temperature = double.IsFinite(Temperature) ? Math.Clamp(Temperature, 0, 2) : .7,
        PersonaNotes = (PersonaNotes ?? new()).Take(150).ToDictionary(p => p.Key, p => ContextBudget.Clip(p.Value, 2000), StringComparer.Ordinal)
    };

    public static bool IsAllowedEndpoint(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && (uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public static bool IsDeepSeek(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort;
}
