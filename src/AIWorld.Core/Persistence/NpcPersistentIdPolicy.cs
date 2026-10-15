namespace AIWorld.Core.Persistence;

/// <summary>
/// P3 NPC Persistent Identity Policy.
///
/// Evidence basis (Phase 1.5 V1, SOURCE_VERIFIED): `npc.Name` is a Character public field,
/// stable within a single save file (StardewSquad uses Name for IsRecruited matching;
/// SocialValley indexes conversations by NPC name). It is NOT globally unique across
/// modded NPCs in general.
///
/// POLICY (Demo, Phase 3):
///   PRIMARY KEY   = npc.Name（Demo/vanilla NPC，单存档内稳定）
///   SCOPE       = per save slot (AIWorldSaveData is per-save via ReadSaveData/WriteSaveData)
///   EXTENSION   = for future modded NPCs with colliding names, repository layer may
///                 qualify as "Name@ModId" — the repository keeps a stable id mapping,
///                 NOT the contracts (no CONTRACT_CHANGE_REQUEST needed).
///
/// Restrictions: this is a repository implementation policy. It does NOT claim npc.Name is
/// a globally unique stable ID across all mods/saves; it is stable for the Demo scope.
/// </summary>
public static class NpcPersistentIdPolicy
{
    /// <summary>Resolve the persistent key for a Demo NPC. Currently npc.Name (stable within save).</summary>
    public static string Resolve(string npcName) => npcName;

    /// <summary>Validate a persistent key (non-empty, bounded).</summary>
    public static bool IsValid(string key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= 64;
}
