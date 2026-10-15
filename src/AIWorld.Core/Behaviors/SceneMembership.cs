namespace AIWorld.Core.Behaviors;

public static class SceneMembership
{
    public static bool Contains(string npc, IEnumerable<string>? actorIds) =>
        actorIds?.Contains(npc, StringComparer.Ordinal) == true;
}
