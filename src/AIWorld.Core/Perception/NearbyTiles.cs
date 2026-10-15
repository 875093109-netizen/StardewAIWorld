namespace AIWorld.Core.Perception;

public static class NearbyTiles
{
    private static readonly TilePosition[] Offsets = (from x in Enumerable.Range(-PerceptionEngine.Radius, 2 * PerceptionEngine.Radius + 1)
        from y in Enumerable.Range(-PerceptionEngine.Radius, 2 * PerceptionEngine.Radius + 1)
        where x * x + y * y <= PerceptionEngine.Radius * PerceptionEngine.Radius
        orderby x * x + y * y, x, y
        select new TilePosition(x, y)).ToArray();

    /// <summary>Nearest tiles first, before any consumer applies a work limit.</summary>
    public static IEnumerable<TilePosition> Around(TilePosition center) => Offsets.Select(p => new TilePosition(center.X + p.X, center.Y + p.Y));
}
