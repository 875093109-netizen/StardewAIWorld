using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// CharacterId — collision-safe identity for ANY runtime-discovered NPC (vanilla or modded).
/// PM GLOBAL CHARACTER GENERICITY: display name alone is NOT globally unique.
///
/// Canonical form: "Name" (vanilla / unique) or "Name@ModId" (qualified when needed).
/// The repository layer resolves runtime NPCs to stable CharacterIds; contracts use the
/// canonical string form so persistence stays simple.
/// </summary>
public static class CharacterId
{
    public const char Separator = '@';
    public const int MaxLength = 96;

    /// <summary>Build a qualified id: "Name@ModId". Unqualified when modId is empty.</summary>
    public static string Build(string npcName, string? modId = null)
    {
        var name = (npcName ?? string.Empty).Trim();
        var mod = (modId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name)) return name;
        return string.IsNullOrEmpty(mod) ? name : $"{name}{Separator}{mod}";
    }

    /// <summary>True for a non-empty, bounded id.</summary>
    public static bool IsValid(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= MaxLength;

    /// <summary>The display/actor name portion (before the qualifier).</summary>
    public static string Name(string id)
    {
        if (string.IsNullOrEmpty(id)) return id ?? string.Empty;
        var i = id.IndexOf(Separator);
        return i > 0 ? id[..i] : id;
    }

    /// <summary>The mod qualifier portion, or null when unqualified.</summary>
    public static string? ModId(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var i = id.IndexOf(Separator);
        return i > 0 && i < id.Length - 1 ? id[(i + 1)..] : null;
    }

    /// <summary>Qualify an id if it is not already qualified and a modId is known.
    /// Never double-qualifies.</summary>
    public static string EnsureQualified(string id, string? modId)
    {
        if (string.IsNullOrEmpty(modId) || ModId(id) is not null) return id;
        return Build(Name(id), modId);
    }
}

/// <summary>
/// CharacterDiscovery — how a runtime NPC becomes an AIWorld character.
/// Provider strategy (PM): rich explicit profile → canonical/mod integration profile →
/// runtime canonical NPC data → generic modded-NPC fallback.
/// </summary>
public enum CharacterSourceKind
{
    AIWORLD_RICH,        // full persona/memory/knowledge/goals
    CANONICAL_INTEGRATED, // richer canonical profile/context
    RUNTIME_CANONICAL,    // runtime canonical NPC data
    GENERIC_FALLBACK,     // generic modded-NPC fallback (isolated, no persona bleed)
}
