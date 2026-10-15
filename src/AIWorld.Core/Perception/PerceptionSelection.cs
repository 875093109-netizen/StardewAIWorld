namespace AIWorld.Core.Perception;

/// <summary>Scene counts and exact action targets are separate projections of the same visible sample.</summary>
internal static class PerceptionSelection
{
    private sealed record GroupKey(string Scope, string Source, string Definition, EntityKind Kind, string Name,
        string State, string? Owner, EntityHostility Hostility, bool CanObserve);
    private static GroupKey? Key(PerceivedEntity e) => e.CanAggregate && e.DefinitionId is not null
        && e.Kind is EntityKind.Object or EntityKind.Terrain
        ? new(e.Id.Split(':')[0], e.Source, e.DefinitionId, e.Kind, e.Name, e.State, e.KnownOwnerId, e.Hostility, e.CanObserve) : null;

    private static int Bucket(EntityKind kind) => kind switch
    { EntityKind.Person or EntityKind.Player => 0, EntityKind.Animal or EntityKind.Creature or EntityKind.Unknown => 1, EntityKind.Object => 2, _ => 3 };

    public static PerceptionFrame Select(PerceptionFrame empty, IEnumerable<PerceivedEntity> candidates, TilePosition observer)
    {
        var nearby = candidates.OrderBy(e => e.Tile.DistanceSquared(observer)).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var groups = nearby.Where(e => Key(e) is not null).GroupBy(e => Key(e)!).ToArray();
        var represented = new HashSet<GroupKey>();
        // Only one exact representative per scenery group; named/unknown objects and all living entities stay independent.
        var targets = nearby.Where(e => Key(e) is not { } k || represented.Add(k)).ToArray();
        var queues = Enumerable.Range(0, 4).Select(i => new Queue<PerceivedEntity>(targets.Where(e => Bucket(e.Kind) == i))).ToArray();
        var selected = new List<PerceivedEntity>();
        var summaries = new List<SceneSummary>();
        // Always budget the incomplete marker and keep space for the existing observation receipt.
        int used = PerceptionEngine.Prompt(empty with { Partial = true }).Length;
        int limit = PerceptionEngine.ContextCharacters - 350;
        void TakeOne(Queue<PerceivedEntity> queue)
        {
            while (queue.TryDequeue(out var e))
            {
                int cost = PerceptionEngine.Line(e).Length + 1;
                if (used + cost > limit) continue;
                selected.Add(e); used += cost; return;
            }
        }
        // Each present category gets a first opportunity before any category takes a second slot.
        foreach (var queue in queues) TakeOne(queue);

        var summaryQueues = new[] { EntityKind.Object, EntityKind.Terrain }.Select(kind => new Queue<SceneSummary>(
            groups.Where(g => g.Key.Kind == kind && g.Count() > 1).OrderByDescending(g => g.Count())
                .ThenBy(g => g.First().Tile.DistanceSquared(observer)).ThenBy(g => g.Key.Definition, StringComparer.Ordinal)
                .Select(g => new SceneSummary(g.Key.Scope + "/" + g.Key.Source, g.Key.Definition, g.Key.Kind, g.Key.Name,
                    g.Key.State, g.Key.Owner, g.Key.Hostility, g.Count())))).ToArray();
        int summaryUsed = PerceptionEngine.SummaryHeading.Length;
        for (int round = 0; round < 2; round++)
        foreach (var queue in summaryQueues)
        {
            while (queue.TryDequeue(out var summary))
            {
                int cost = PerceptionEngine.SummaryLine(summary).Length + 1;
                int heading = summaries.Count == 0 ? PerceptionEngine.SummaryHeading.Length : 0;
                if (summaryUsed + cost > 650 || used + cost + heading > limit) continue;
                summaries.Add(summary); summaryUsed += cost; used += cost + heading; break;
            }
        }

        // Unused category slots are borrowed in round-robin order; character cap takes precedence.
        while (selected.Count < PerceptionEngine.MaxEntities && queues.Any(q => q.Count > 0))
        foreach (var queue in queues)
        {
            if (selected.Count == PerceptionEngine.MaxEntities) break;
            TakeOne(queue);
        }
        return empty with
        {
            Entities = selected.AsReadOnly(), Summaries = summaries.AsReadOnly(),
            Partial = empty.Partial || selected.Count < nearby.Length
        };
    }
}
