using System.Runtime.CompilerServices;
using AIWorld.Core.Perception;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Monsters;
using StardewValley.TerrainFeatures;

namespace AIWorld.Demo;

/// <summary>Public SMAPI API. Trusted code adapters only; not an LLM tool or arbitrary reflection endpoint.</summary>
public interface IWorldPerceptionApi
{
    bool RegisterSource(IWorldEntitySource source);
    bool UnregisterSource(string sourceId);
    bool RegisterNativeDescriptor(string adapterId, Func<object, WorldEntitySnapshot, WorldEntitySnapshot?> describe);
    bool UnregisterNativeDescriptor(string adapterId);
}

/// <summary>Main-thread, on-demand perception from the observer's location, not Game1.currentLocation.</summary>
public sealed class WorldPerceptionSource : IWorldEntitySource, IBoundedWorldEntitySource, IWorldPerceptionApi
{
    public string SourceId => "native";
    public bool LastCaptureTruncated { get; private set; }
    private sealed record Identity(string Value);
    private ConditionalWeakTable<object, Identity> _ids = new();
    private readonly Dictionary<string, Func<object, WorldEntitySnapshot, WorldEntitySnapshot?>> _describers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly IMonitor _monitor;
    private readonly Func<bool> _enabled;
    private readonly PerceptionEngine _engine;
    private long _nextId;
    private long _epoch;
    private string Id(object entity) => _ids.GetValue(entity, _ => new($"e{_epoch}-{++_nextId}")).Value;

    public WorldPerceptionSource(IMonitor monitor, Func<bool>? enabled = null)
    {
        _monitor = monitor;
        _enabled = enabled ?? (() => true);
        _engine = new PerceptionEngine(Warn);
        _engine.Register(this);
    }
    private void Warn(string source, Exception ex)
    {
        if (_warned.Add(source)) _monitor.Log($"[PERCEPTION] source={source} unavailable: {ex.GetType().Name}", LogLevel.Warn);
    }
    public bool RegisterSource(IWorldEntitySource source) => _engine.Register(source);
    public bool UnregisterSource(string sourceId) => sourceId != SourceId && _engine.Unregister(sourceId);
    public bool RegisterNativeDescriptor(string adapterId, Func<object, WorldEntitySnapshot, WorldEntitySnapshot?> describe)
        => !string.IsNullOrWhiteSpace(adapterId) && adapterId.Length <= 80 && describe is not null
            && _describers.Count < PerceptionEngine.MaxSources && _describers.TryAdd(adapterId, describe);
    public bool UnregisterNativeDescriptor(string adapterId) => _describers.Remove(adapterId);
    public void Reset() { _ids = new(); _nextId = 0; _epoch++; _warned.Clear(); }

    public bool CanSeePlayer(string npcId)
    {
        var player = Game1.player;
        return player is not null && GetFrame(npcId).Entities.Any(e => e.Id == SourceId + ":" + Id(player));
    }

    public bool CanSeeNpc(string observer, string target)
    {
        var npc = Game1.getCharacterFromName(target);
        return npc is not null && GetFrame(observer).Entities.Any(e => e.Id == SourceId + ":" + Id(npc));
    }

    public bool CanSeeNpcs(string observer, IEnumerable<string> targets)
    {
        var visible = GetFrame(observer).Entities.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        return targets.All(id => Game1.getCharacterFromName(id) is { } npc && visible.Contains(SourceId + ":" + Id(npc)));
    }

    public PerceptionFrame GetFrame(string npcId)
    {
        try
        {
            if (!_enabled() || !Context.IsWorldReady || new SmapiHardAnchorProvider().IsHardAnchorActive(npcId)) return PerceptionFrame.Unavailable(npcId);
            var npc = Game1.getCharacterFromName(npcId);
            var location = npc?.currentLocation;
            if (npc is null || location?.Map is null) return PerceptionFrame.Unavailable(npcId);
            var p = new TilePosition(npc.TilePoint.X, npc.TilePoint.Y);
            var query = new PerceptionQuery(npcId, location.NameOrUniqueName, p, SourceId + ":" + Id(npc));
            return _engine.Capture(query, (a, b) => GridSight.Clear(a, b, (x, y) => BlocksSight(location, x, y)), DateTimeOffset.UtcNow,
                location.IsOutdoors ? (location.IsRainingHere() ? "outdoors, raining" : "outdoors, not raining") : "indoors");
        }
        catch (Exception ex) { Warn("capture", ex); return PerceptionFrame.Unavailable(npcId); }
    }

    private static bool BlocksSight(GameLocation location, int x, int y)
    {
        if (!location.isTileOnMap(x, y)) return true;
        // Explicitly conservative map-layer visibility, not a claim of pixel-perfect game vision.
        var tile = location.Map.GetLayer("Buildings")?.Tiles[x, y];
        return tile is not null && location.doesTileHaveProperty(x, y, "Passable", "Buildings") is null
            && location.doesTileHaveProperty(x, y, "Shadow", "Buildings") is null;
    }

    public IEnumerable<WorldEntitySnapshot> Capture(PerceptionQuery query)
    {
        LastCaptureTruncated = false;
        var location = Game1.getCharacterFromName(query.ObserverId)?.currentLocation;
        if (location is null || location.NameOrUniqueName != query.Location) yield break;
        // Query the observer's map only. Rank characters before truncation so insertion order
        // cannot hide nearby actors. Scenery uses direct radius-limited tile lookups below.
        var characters = location.characters.Cast<Character>().Concat(location.farmers)
            .Concat(location.animals.Values).Where(c => Near(c.TilePoint.X, c.TilePoint.Y, query))
            .OrderBy(c => new TilePosition(c.TilePoint.X, c.TilePoint.Y).DistanceSquared(query.Tile)).Take(257).ToArray();
        LastCaptureTruncated = characters.Length > 256;
        var snapshots = new List<WorldEntitySnapshot>();
        foreach (var c in characters.Take(256))
        {
            if (c is Monster dead && dead.Health <= 0) continue;
            var kind = c switch
            {
                Farmer => EntityKind.Player,
                FarmAnimal or Pet or Horse => EntityKind.Animal,
                Monster => EntityKind.Creature,
                NPC n when n.IsVillager => EntityKind.Person,
                _ => EntityKind.Unknown
            };
            var snapshot = new WorldEntitySnapshot(Id(c), c.displayName, kind, query.Location,
                new(c.TilePoint.X, c.TilePoint.Y), c.isMoving() ? "moving" : "stationary",
                OwnerId: c is FarmAnimal animal ? animal.ownerID.Value.ToString() : null);
            snapshots.Add(Describe(c, snapshot));
        }
        foreach (var position in NearbyTiles.Around(query.Tile))
        {
            var tile = new Vector2(position.X, position.Y);
            if (location.objects.TryGetValue(tile, out var obj))
            {
                var definition = ItemRegistry.GetData(obj.QualifiedItemId);
                snapshots.Add(Describe(obj, new(Id(obj), obj.DisplayName, EntityKind.Object, query.Location, position)
                {
                    DefinitionId = definition is null ? null : "item:" + obj.QualifiedItemId,
                    CanAggregate = definition is not null && obj.DisplayName == definition.DisplayName
                }));
            }
            if (location.terrainFeatures.TryGetValue(tile, out var terrain))
                snapshots.Add(Describe(terrain, DescribeTerrain(terrain, query.Location, position)));
        }
        // Merge characters and scenery by distance BEFORE the engine's per-source limit.
        foreach (var snapshot in snapshots.OrderBy(e => e.Tile.DistanceSquared(query.Tile)).ThenBy(e => e.LocalId, StringComparer.Ordinal))
            yield return snapshot;
    }

    private WorldEntitySnapshot DescribeTerrain(TerrainFeature terrain, string location, TilePosition tile)
    {
        string name = "terrain", state = ""; string? definitionId = null;
        switch (terrain)
        {
            case HoeDirt { crop: { } crop }:
                name = "crop";
                state = crop.dead.Value ? "withered" : "standing crop";
                var harvest = crop.GetData()?.HarvestItemId;
                var cropItem = string.IsNullOrWhiteSpace(harvest) ? null : ItemRegistry.GetData(harvest);
                if (cropItem is not null && !string.IsNullOrWhiteSpace(crop.netSeedIndex.Value))
                {
                    name = "crop: " + cropItem.DisplayName;
                    definitionId = "crop:" + crop.netSeedIndex.Value;
                }
                break;
            case FruitTree fruit:
                name = "fruit tree";
                state = fruit.stump.Value ? "stump" : fruit.growthStage.Value >= 4 ? "grown" : "sapling";
                if (fruit.GetData() is not null)
                {
                    name = fruit.GetDisplayName();
                    definitionId = "fruit-tree:" + fruit.treeId.Value;
                }
                break;
            case Tree tree:
                name = "tree";
                state = tree.stump.Value ? "stump" : tree.growthStage.Value >= 5 ? "grown" : "sapling";
                var treeData = tree.GetData();
                if (treeData is not null)
                {
                    definitionId = "wild-tree:" + tree.treeType.Value;
                    var seed = string.IsNullOrWhiteSpace(treeData.SeedItemId) ? null : ItemRegistry.GetData(treeData.SeedItemId);
                    // WildTreeData has no display-name field; label the real seed relation, never guess a species.
                    if (seed is not null) name = "tree (seed: " + seed.DisplayName + ")";
                }
                break;
            case Grass:
                name = "grass"; // Subtypes need a descriptor; don't merge unknown varieties by this generic name.
                break;
            case HoeDirt:
                name = "tilled soil"; definitionId = "terrain:tilled-soil";
                break;
        }
        // Replanting in the same HoeDirt replaces the crop, not necessarily the soil instance.
        var identity = terrain is HoeDirt { crop: { } planted } ? (object)planted : terrain;
        return new(Id(identity), name, EntityKind.Terrain, location, tile, state)
        { DefinitionId = definitionId, CanAggregate = definitionId is not null };
    }

    private static bool Near(int x, int y, PerceptionQuery query) => new TilePosition(x, y).DistanceSquared(query.Tile)
        <= PerceptionEngine.Radius * PerceptionEngine.Radius;

    private WorldEntitySnapshot Describe(object entity, WorldEntitySnapshot original)
    {
        foreach (var adapter in _describers.ToArray())
        {
            try
            {
                if (adapter.Value(entity, original) is { } described)
                    // Describers enrich semantics, never relocate/reidentify an existing game object.
                    return described with { LocalId = original.LocalId, Location = original.Location, Tile = original.Tile,
                        EvidenceSource = "native/" + adapter.Key };
            }
            catch (Exception ex) { Warn(adapter.Key, ex); }
        }
        return original;
    }
}
