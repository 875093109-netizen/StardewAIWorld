namespace AIWorld.Core.Perception;

public enum EntityKind { Unknown, Person, Player, Animal, Creature, Object, Terrain }
public enum EntityHostility { Unknown, Friendly, Hostile }
public readonly record struct TilePosition(int X, int Y)
{
    public double DistanceSquared(TilePosition other) => ((double)X - other.X) * ((double)X - other.X)
        + ((double)Y - other.Y) * ((double)Y - other.Y);
}

/// <summary>Trusted adapter data, never model output. Ownership truth is separate from permission to disclose it.</summary>
public sealed record WorldEntitySnapshot(
    string LocalId, string Name, EntityKind Kind, string Location, TilePosition Tile,
    string VisibleState = "", string? OwnerId = null, bool OwnershipObservable = false,
    EntityHostility Hostility = EntityHostility.Unknown, bool CanObserve = true, string? EvidenceSource = null)
{
    /// <summary>Stable game/mod definition ID, not a display-name guess. Never an action target.</summary>
    public string? DefinitionId { get; init; }
    /// <summary>Opt-in for ordinary scenery. Living entities are never aggregated even if requested.</summary>
    public bool CanAggregate { get; init; }
}

public sealed record PerceptionQuery(string ObserverId, string Location, TilePosition Tile, string SelfId);

/// <summary>Called only on the game thread. Return current local entities, not inventories, scripts or future spawns.</summary>
public interface IWorldEntitySource
{
    string SourceId { get; }
    IEnumerable<WorldEntitySnapshot> Capture(PerceptionQuery observer);
}

/// <summary>Optional disclosure when an adapter applies its own scan limit before returning rows.</summary>
public interface IBoundedWorldEntitySource
{
    bool LastCaptureTruncated { get; }
}

/// <summary>Safe, copied, per-observer projection. The model never receives WorldEntitySnapshot or live game objects.</summary>
public sealed record PerceivedEntity(string Id, string Name, EntityKind Kind, TilePosition Tile,
    string State, string? KnownOwnerId, EntityHostility Hostility, bool CanObserve, string Source)
{
    public string? DefinitionId { get; init; }
    public bool CanAggregate { get; init; }
}

/// <summary>Counts only the visible scanned sample. No instance ID or action authority.</summary>
public sealed record SceneSummary(string Source, string DefinitionId, EntityKind Kind, string Name,
    string State, string? KnownOwnerId, EntityHostility Hostility, int ObservedCount);

public sealed record PerceptionFrame(string ObserverId, string Location, DateTimeOffset CapturedAt,
    IReadOnlyList<PerceivedEntity> Entities, bool Available = true, bool Partial = false, string Environment = "")
{
    public IReadOnlyList<SceneSummary> Summaries { get; init; } = Array.Empty<SceneSummary>();
    public static PerceptionFrame Unavailable(string observer) => new(observer, "", DateTimeOffset.UtcNow,
        Array.Empty<PerceivedEntity>(), Available: false);
}
