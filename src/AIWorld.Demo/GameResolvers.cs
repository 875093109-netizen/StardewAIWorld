using AIWorld.Core.Behaviors;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>NPC target resolution against the live game world.</summary>
public sealed class GameNpcResolver : INpcResolver
{
    public bool NpcExists(string npcName) =>
        Game1.getCharacterFromName(npcName) is not null;
}

/// <summary>Player identity resolution from the live game.</summary>
public sealed class GamePlayerResolver : IPlayerResolver
{
    public string ResolvePlayerId() =>
        Game1.player?.UniqueMultiplayerID.ToString() ?? "player";
}
