using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace AIWorld.Core.Chat;

/// <summary>Optional, embedded character backgrounds. Never registers NPCs or writes save data.</summary>
public sealed class NpcPersonaCatalog
{
    public const string SveModId = "FlashShifter.StardewValleyExpandedCP";
    private static readonly Lazy<IReadOnlyList<PersonaPack>> BuiltInPacks = new(LoadBuiltIn);
    private static readonly JsonSerializerOptions PromptJson = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
    private readonly Func<string, bool> _isModLoaded;

    public NpcPersonaCatalog(Func<string, bool> isModLoaded) => _isModLoaded = isModLoaded;

    public IReadOnlyList<PersonaPack> Packs => BuiltInPacks.Value;

    public bool SupportsNpc(string npcId) => Find(npcId) is not null;

    public string? GetPersona(string npcId, string? liveDisplayName = null)
    {
        var match = Find(npcId);
        if (match is null) return null;
        var (pack, profile) = match.Value;
        var name = string.IsNullOrWhiteSpace(liveDisplayName) ? profile.DisplayName : liveDisplayName;
        // JSON keeps names/data separate from the instructions. The internal key remains unchanged.
        return $"BUILT-IN CHARACTER BACKGROUND ({pack.Name}, source {pack.SourceVersion}):\n"
            + JsonSerializer.Serialize(new { npcId, displayName = name, personality = profile.Personality }, PromptJson) + "\n"
            + "Use the displayName when introducing yourself; npcId is the internal identity used for actions and memories. "
            + "This is background and speaking style, not a record of shared experiences with the player. "
            + "Current world state, actual relationships and verified memories take precedence. "
            + "Never invent completed quests, introductions, unlocks, marriages or shared events. "
            + "Do not reveal private story spoilers unprompted. Do not assume affection or permission from a friendly personality. "
            + "Background magical expertise does not grant new executable abilities. "
            + "Use the player's language and remain free to accept, decline or suggest other terms.";
    }

    private (PersonaPack Pack, NpcPersona Profile)? Find(string npcId)
    {
        foreach (var pack in Packs)
        {
            if (!_isModLoaded(pack.ModId)) continue;
            var profile = pack.Profiles.FirstOrDefault(p => string.Equals(p.NpcId, npcId, StringComparison.Ordinal));
            if (profile is not null) return (pack, profile);
        }
        return null;
    }

    private static IReadOnlyList<PersonaPack> LoadBuiltIn()
    {
        var assembly = typeof(NpcPersonaCatalog).Assembly;
        var packs = new List<PersonaPack>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".personas.json", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            var pack = JsonSerializer.Deserialize<PersonaPack>(stream) ?? throw new InvalidDataException($"Empty persona pack: {resource}");
            if (string.IsNullOrWhiteSpace(pack.ModId) || string.IsNullOrWhiteSpace(pack.SourceVersion)
                || pack.Profiles.Count == 0 || packs.Any(p => p.ModId == pack.ModId)
                || pack.Profiles.Select(p => p.NpcId).Distinct(StringComparer.Ordinal).Count() != pack.Profiles.Count
                || pack.Profiles.Any(p => string.IsNullOrWhiteSpace(p.NpcId) || p.NpcId.Length > 64
                    || string.IsNullOrWhiteSpace(p.DisplayName) || string.IsNullOrWhiteSpace(p.Personality) || p.Sources.Count == 0))
                throw new InvalidDataException($"Invalid persona pack: {resource}");
            packs.Add(pack);
        }
        return packs.AsReadOnly();
    }
}

public sealed record PersonaPack(string ModId, string Name, string SourceVersion, string SourceUrl, IReadOnlyList<NpcPersona> Profiles);
public sealed record NpcPersona(string NpcId, string DisplayName, string Personality, IReadOnlyList<string> Sources);
