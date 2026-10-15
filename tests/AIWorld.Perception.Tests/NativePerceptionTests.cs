using AIWorld.Core.Perception;
using AIWorld.Demo;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using Xunit;

namespace AIWorld.Perception.Tests;

public sealed class NativePerceptionTests : IDisposable
{
    private sealed class Monitor : IMonitor
    {
        public readonly List<string> Lines = new();
        public void Log(string text, LogLevel level) => Lines.Add(text);
    }
    private sealed class ModMonster : Monster { }
    private readonly Monitor _monitor = new();
    private readonly GameLocation _location = new();
    private readonly NPC _observer;
    private readonly WorldPerceptionSource _source;
    public NativePerceptionTests()
    {
        Game1.Npcs.Clear(); Context.IsWorldReady = true; SmapiHardAnchorProvider.Blocked = false;
        ItemRegistry.Data.Clear();
        _observer = new NPC { displayName = "Abigail", IsVillager = true, TilePoint = new(10, 10), currentLocation = _location };
        Game1.Npcs["Abigail"] = _observer; _location.characters.Add(_observer);
        Game1.player = new Farmer { currentLocation = new GameLocation { NameOrUniqueName = "Beach" }, TilePoint = new(11, 10) };
        Game1.currentLocation = Game1.player.currentLocation;
        _source = new(_monitor);
    }
    public void Dispose() { Game1.Npcs.Clear(); Game1.player = null; SmapiHardAnchorProvider.Blocked = false; }

    [Fact] public void ReadsNpcLocationInsteadOfPlayersAndHidesUnobservedOwnership()
    {
        var animal = new FarmAnimal { displayName = "Bessie", TilePoint = new(11, 10) }; animal.ownerID.Value = 42;
        _location.animals.Add(1, animal);
        var frame = _source.GetFrame("Abigail");
        Assert.Equal("Town", frame.Location); Assert.Contains("outdoors", frame.Environment);
        var seen = Assert.Single(frame.Entities); Assert.Equal("Bessie", seen.Name); Assert.Null(seen.KnownOwnerId);
        Assert.False(_source.CanSeePlayer("Abigail"));
    }
    [Fact] public void NativeAndCustomMonsterTypesDoNotProveHostility()
    {
        _location.characters.Add(new Monster { displayName = "slime", TilePoint = new(11, 10) });
        _location.characters.Add(new ModMonster { displayName = "pet creature", TilePoint = new(12, 10) });
        Assert.All(_source.GetFrame("Abigail").Entities, e => Assert.Equal(EntityHostility.Unknown, e.Hostility));
    }
    [Fact] public void MapWallFiltersEntitiesAndPassableTileRestoresVisibility()
    {
        _location.characters.Add(new Monster { TilePoint = new(12, 10) });
        _location.Map.Buildings.Tiles[11, 10] = new Tile();
        Assert.Empty(_source.GetFrame("Abigail").Entities);
        _location.Map.Buildings.Tiles[11, 10]!.Properties["Passable"] = "T";
        Assert.Single(_source.GetFrame("Abigail").Entities);
    }
    [Fact] public void ReplacedObjectAndNewSaveNeverReuseIssuedIds()
    {
        _location.objects[new(11, 10)] = new(); var old = Assert.Single(_source.GetFrame("Abigail").Entities).Id;
        Assert.Equal(old, Assert.Single(_source.GetFrame("Abigail").Entities).Id);
        _location.objects[new(11, 10)] = new(); var replaced = Assert.Single(_source.GetFrame("Abigail").Entities).Id;
        Assert.NotEqual(old, replaced); _source.Reset();
        Assert.NotEqual(replaced, Assert.Single(_source.GetFrame("Abigail").Entities).Id);
    }
    [Fact] public void DescriptorEnrichesOnlyRecognizedObjectAndCannotMoveOrReidentifyIt()
    {
        _location.characters.Add(new ModMonster { displayName = "custom", TilePoint = new(11, 10) });
        var before = Assert.Single(_source.GetFrame("Abigail").Entities);
        Assert.True(_source.RegisterNativeDescriptor("Example.Adapter", (obj, original) => obj is ModMonster
            ? original with { LocalId = "spoof", Location = "Beach", Tile = new(40, 40), OwnerId = "farmer", OwnershipObservable = true, Hostility = EntityHostility.Friendly }
            : null));
        var after = Assert.Single(_source.GetFrame("Abigail").Entities);
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.Tile, after.Tile);
        Assert.Equal("farmer", after.KnownOwnerId); Assert.Equal(EntityHostility.Friendly, after.Hostility);
    }
    [Fact] public void FailedDescriptorFallsBackWithoutBreakingCaptureAndLogsOnce()
    {
        _location.characters.Add(new ModMonster { TilePoint = new(11, 10) });
        _source.RegisterNativeDescriptor("broken", (_, _) => throw new InvalidOperationException());
        Assert.Single(_source.GetFrame("Abigail").Entities); Assert.Single(_source.GetFrame("Abigail").Entities);
        Assert.Single(_monitor.Lines);
    }
    [Fact] public void HardAnchorDisabledFeatureOrNoSaveReturnsUnavailable()
    {
        SmapiHardAnchorProvider.Blocked = true; Assert.False(_source.GetFrame("Abigail").Available);
        SmapiHardAnchorProvider.Blocked = false; Context.IsWorldReady = false; Assert.False(_source.GetFrame("Abigail").Available);
        Context.IsWorldReady = true; Assert.False(new WorldPerceptionSource(_monitor, () => false).GetFrame("Abigail").Available);
    }
    [Fact] public void PlayerPresenceRequiresSameMapAndClearSight()
    {
        var player = Game1.player!; player.currentLocation = _location; _location.farmers.Add(player);
        Assert.True(_source.CanSeePlayer("Abigail"));
        player.TilePoint = new(30, 10); Assert.False(_source.CanSeePlayer("Abigail"));
    }
    [Fact] public void FollowingObserverDoesNotRememberInjuryAcrossAnUnseenInterval()
    {
        var data = new AIWorld.Core.Persistence.AIWorldSaveData { SaveSchemaVersion = "1" };
        var collector = new AIWorld.Core.Behaviors.ObservationCollector(data);
        bool visible = true;
        var source = new SmapiObservationSource(_monitor, collector,
            new AIWorld.Core.Behaviors.FollowSessionManager(data, "player"), "Abigail", () => visible, () => false);
        source.Tick(); visible = false; source.Tick(); Game1.player!.health = 50;
        visible = true; source.Tick();
        Assert.DoesNotContain(collector.GetForNpc("Abigail"), e => e.Type == "PLAYER_INJURED");
        Game1.player.health = 40; source.Tick();
        Assert.Single(collector.GetForNpc("Abigail").Where(e => e.Type == "PLAYER_INJURED"));
    }
    [Fact] public void MonsterBaseClassDoesNotCreateDangerMemoryInProductionPath()
    {
        var data = new AIWorld.Core.Persistence.AIWorldSaveData { SaveSchemaVersion = "1" };
        var collector = new AIWorld.Core.Behaviors.ObservationCollector(data);
        Game1.currentLocation!.characters.Add(new ModMonster());
        var source = new SmapiObservationSource(_monitor, collector,
            new AIWorld.Core.Behaviors.FollowSessionManager(data, "player"), "Abigail", () => true, () => false);
        source.Tick(); Assert.DoesNotContain(collector.GetForNpc("Abigail"), e => e.Type == "DANGER_ENCOUNTER");
    }
    [Fact] public void MagicObservationUsesVisibleObjectSampleInsteadOfEntireMap()
    {
        var data = new AIWorld.Core.Persistence.AIWorldSaveData { SaveSchemaVersion = "1" };
        var collector = new AIWorld.Core.Behaviors.ObservationCollector(data);
        var location = Game1.currentLocation!;
        location.objects[new(11, 10)] = new() { Analyze = true, TileLocation = new(11, 10) };
        location.objects[new(40, 40)] = new() { Analyze = true, TileLocation = new(40, 40) };
        var source = new MagicObservationSource(_monitor, collector,
            new AIWorld.Core.Behaviors.FollowSessionManager(data, "player"), "Abigail",
            () => new HashSet<TilePosition> { new(11, 10) });
        source.Tick(); Assert.Single(collector.GetForNpc("Abigail"));
        source.Tick(); Assert.Single(collector.GetForNpc("Abigail"));
    }
    [Fact] public void CropNameAndGroupingUseRegisteredDefinitionIncludingModdedIds()
    {
        ItemRegistry.Data["(O)Mod.Blueberry"] = new() { DisplayName = "模组蓝莓" };
        for (int i = 11; i <= 13; i++)
        {
            var crop = new Crop { Data = new() { HarvestItemId = "(O)Mod.Blueberry" } };
            crop.netSeedIndex.Value = "Mod.BlueberrySeeds";
            _location.terrainFeatures[new(i, 10)] = new StardewValley.TerrainFeatures.HoeDirt { crop = crop };
        }
        var frame = _source.GetFrame("Abigail");
        Assert.Equal("crop: 模组蓝莓", Assert.Single(frame.Entities).Name);
        Assert.Equal("crop:Mod.BlueberrySeeds", Assert.Single(frame.Summaries).DefinitionId);
        Assert.Equal(3, frame.Summaries[0].ObservedCount);
    }
    [Fact] public void UnknownCropDefinitionFallsBackWithoutInventingSpecies()
    {
        _location.terrainFeatures[new(11, 10)] = new StardewValley.TerrainFeatures.HoeDirt { crop = new Crop() };
        var e = Assert.Single(_source.GetFrame("Abigail").Entities);
        Assert.Equal("crop", e.Name); Assert.Null(e.DefinitionId); Assert.False(e.CanAggregate);
    }
    [Fact] public void TreesUseActualFruitNameOrExplicitWildSeedRelation()
    {
        ItemRegistry.Data["Acorn"] = new() { DisplayName = "橡子" };
        var tree = new StardewValley.TerrainFeatures.Tree { Data = new() { SeedItemId = "Acorn" } };
        tree.treeType.Value = "OakType"; tree.growthStage.Value = 5;
        var fruit = new StardewValley.TerrainFeatures.FruitTree { Data = new() { DisplayName = "苹果树" } };
        fruit.treeId.Value = "AppleType"; fruit.growthStage.Value = 4;
        _location.terrainFeatures[new(11, 10)] = tree; _location.terrainFeatures[new(12, 10)] = fruit;
        var frame = _source.GetFrame("Abigail");
        Assert.Contains(frame.Entities, e => e.Name == "苹果树" && e.DefinitionId == "fruit-tree:AppleType");
        Assert.Contains(frame.Entities, e => e.Name == "tree (seed: 橡子)" && e.State == "grown");
    }
    [Fact] public void RenamedObjectsDoNotAggregateWithOrdinaryInstances()
    {
        ItemRegistry.Data["(BC)Chest"] = new() { DisplayName = "Chest" };
        _location.objects[new(11, 10)] = new() { QualifiedItemId = "(BC)Chest", DisplayName = "Alice's keepsakes" };
        _location.objects[new(12, 10)] = new() { QualifiedItemId = "(BC)Chest", DisplayName = "Bob's keepsakes" };
        var frame = _source.GetFrame("Abigail"); Assert.Empty(frame.Summaries); Assert.Equal(2, frame.Entities.Count);
        Assert.All(frame.Entities, e => Assert.False(e.CanAggregate));
    }
    [Fact] public void NativeSourceOrdersAllKindsByDistanceBeforeSourceCap()
    {
        for (int i = 0; i < 300; i++) _location.characters.Add(new Monster { TilePoint = new(16, 10) });
        for (int x = 2; x <= 18; x++)
        for (int y = 2; y <= 18; y++)
        {
            _location.objects[new(x, y)] = new();
            _location.terrainFeatures[new(x, y)] = new StardewValley.TerrainFeatures.Grass();
        }
        var query = new PerceptionQuery("Abigail", "Town", new(10, 10), "");
        var rows = _source.Capture(query).Take(512).ToArray();
        Assert.True(_source.LastCaptureTruncated);
        Assert.Equal(rows.OrderBy(e => e.Tile.DistanceSquared(query.Tile)), rows);
        Assert.Contains(rows.Take(3), e => e.Kind == EntityKind.Object && e.Tile == query.Tile);
        Assert.Contains(rows.Take(3), e => e.Kind == EntityKind.Terrain && e.Tile == query.Tile);
    }
    [Fact] public void NearbyCharacterInsertedAfterLargeRemoteListIsStillRecognized()
    {
        for (int i = 0; i < 4200; i++) _location.characters.Add(new Monster { TilePoint = new(40, 40) });
        _location.characters.Add(new ModMonster { displayName = "nearest", TilePoint = new(11, 10) });
        Assert.Equal("nearest", Assert.Single(_source.GetFrame("Abigail").Entities).Name);
    }
    [Fact] public void ReplantingTheSameSoilDoesNotReuseTheOldCropTarget()
    {
        var soil = new StardewValley.TerrainFeatures.HoeDirt { crop = new Crop() };
        _location.terrainFeatures[new(11, 10)] = soil;
        var old = Assert.Single(_source.GetFrame("Abigail").Entities).Id;
        soil.crop = new Crop();
        Assert.NotEqual(old, Assert.Single(_source.GetFrame("Abigail").Entities).Id);
    }
}
