using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;

namespace AIWorld.Core.Chat;

public sealed record DialogueRewriteCandidate(string Id, string Speech, bool HasAction = false);

/// <summary>Explicit autonomy grants for a daily opening; player-bound travel/follow remains in the consent path.</summary>
public static class DialogueActionPolicy
{
    public static bool Allows(NpcIntent? intent, string npc, AIWorldSaveData data, bool eventActive,
        AIWorld.Core.Perception.PerceptionFrame? perception = null)
    {
        if (eventActive) return false;
        if (intent is null) return true;
        if (intent.Type == AIWorld.Core.Perception.PerceptionEngine.ObserveCapability)
            return perception?.ObserverId == npc && AIWorld.Core.Perception.PerceptionEngine.Allows(intent, perception);
        if (intent.Type == CapabilityRegistry.CapabilityIds.StopFollowing)
            return intent.Target == "Player" && data.FollowSessions.Any(s => s.NpcId == npc && s.IsActive);
        return intent.Type is CapabilityRegistry.CapabilityIds.MagicInvestigate or CapabilityRegistry.CapabilityIds.MagicLearn
            && intent.Target == npc && data.Characters.TryGetValue(npc, out var c)
            && c.Knowledge.TryGetValue("magic.exists", out var knowledge) && knowledge.State == "VERIFIED";
    }
}
