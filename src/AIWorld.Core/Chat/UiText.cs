using System.Globalization;
using System.Text.Json;

namespace AIWorld.Core.Chat;

/// <summary>Game-independent translation catalogs. Only the game thread updates the current locale.</summary>
public static class UiText
{
    public static readonly string[] Supported = { "en", "zh-CN", "zh-TW", "ja", "ko", "es" };
    private static string _language = "zh-CN";
    public static string Language => Volatile.Read(ref _language);
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Catalogs = new(() =>
        Supported.ToDictionary(code => code, code =>
        {
            var assembly = typeof(UiText).Assembly;
            using var stream = assembly.GetManifestResourceStream($"AIWorld.Core.Localization.{code}.json")
                ?? throw new InvalidOperationException("Missing language catalog: " + code);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }));
    public static string Resolve(string? gameCode)
    {
        var code = (gameCode ?? "").Trim().Replace('_', '-').ToLowerInvariant();
        if (code is "zh-tw" or "zh-hk" or "zh-mo" || code.StartsWith("zh-hant")
            || code.Contains("繁體") || code.Contains("繁体") || code.Contains("chinese") && code.Contains("traditional")) return "zh-TW";
        if (code == "zh" || code.StartsWith("zh-cn") || code.StartsWith("zh-hans")) return "zh-CN";
        foreach (var language in new[] { "ja", "ko", "es", "en" })
            if (code == language || code.StartsWith(language + "-")) return language;
        return "en";
    }
    public static bool SetLanguage(string? gameCode)
    {
        var language = Resolve(gameCode);
        return Interlocked.Exchange(ref _language, language) != language;
    }
    public static IReadOnlyDictionary<string, string> Catalog(string language) => Catalogs.Value[Resolve(language)];
    public static string T(string key, params object?[] args) => Get(Language, key, args);
    public static string Get(string language, string key, params object?[] args)
    {
        var catalog = Catalog(language);
        var text = catalog.TryGetValue(key, out var value) ? value : Catalogs.Value["en"].GetValueOrDefault(key, key);
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }
    public static string OutputInstruction(string language)
    {
        var name = Resolve(language) switch
        {
            "zh-CN" => "Simplified Chinese (简体中文)", "zh-TW" => "Traditional Chinese (繁體中文; never Simplified Chinese)",
            "ja" => "Japanese (日本語)", "ko" => "Korean (한국어)", "es" => "Spanish (español)", _ => "English"
        };
        return "\nOUTPUT LANGUAGE: " + name + ". Use this language for all player-facing speech, including ambient dialogue and rewrites. "
            + "This explicit game-language setting overrides the language of quoted context, prior messages and style notes. "
            + "Keep JSON property names, schema values, NPC IDs and action IDs unchanged. Do not translate stored facts or claim translated text is a new event.\n";
    }
    public static string EffectivePrompt(string? configured, string language) => string.IsNullOrWhiteSpace(configured)
        || configured == TalkOptions.LegacyDefaultPrompt ? Get(language, "prompt.default") : configured;
}
