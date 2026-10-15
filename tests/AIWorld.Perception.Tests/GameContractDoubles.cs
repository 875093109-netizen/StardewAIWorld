// Minimal headless doubles for the installed Stardew 1.6 API signatures used by the production
// adapter. These verify extraction/lifecycle behavior, not actual rendering or mod loading.
namespace Microsoft.Xna.Framework
{
    public readonly record struct Vector2(float X, float Y);
    public readonly record struct Point(int X, int Y);
}
namespace StardewModdingAPI
{
    public enum LogLevel { Debug, Info, Warn }
    public interface IMonitor { void Log(string text, LogLevel level); }
    public static class Context { public static bool IsWorldReady = true; }
}
namespace StardewValley
{
    using Microsoft.Xna.Framework;
    public sealed class NetLong { public long Value; }
    public sealed class NetString { public string Value = ""; }
    public sealed class NetBool { public bool Value; }
    public sealed class NetInt { public int Value; }
    public sealed class ItemData { public string DisplayName = ""; }
    public static class ItemRegistry
    {
        public static readonly Dictionary<string, ItemData> Data = new();
        public static ItemData? GetData(string id) => Data.GetValueOrDefault(id);
    }
    public sealed class CropData { public string HarvestItemId = ""; }
    public sealed class Crop
    {
        public readonly NetString netSeedIndex = new();
        public readonly NetBool dead = new();
        public CropData? Data;
        public CropData? GetData() => Data;
    }
    public class Character
    {
        public Point TilePoint;
        public string displayName = "entity";
        public bool Moving;
        public GameLocation? currentLocation;
        public bool isMoving() => Moving;
    }
    public class NPC : Character { public bool IsVillager; }
    public class Farmer : Character { public int health = 100; }
    public class FarmAnimal : Character { public readonly NetLong ownerID = new(); }
    public class Object
    {
        public string DisplayName = "object";
        public string QualifiedItemId = "(O)unknown";
        public Vector2 TileLocation;
        public bool Analyze;
        public readonly Dictionary<string, string> modData = new();
        public bool HasContextTag(string _) => Analyze;
    }
    public class Tile { public readonly Dictionary<string, string> Properties = new(); }
    public class Layer { public readonly Tile?[,] Tiles = new Tile?[64, 64]; }
    public class Map
    {
        public readonly Layer Buildings = new();
        public Layer? GetLayer(string name) => name == "Buildings" ? Buildings : null;
    }
    public class GameLocation
    {
        public string NameOrUniqueName = "Town";
        public Map Map = new();
        public bool IsOutdoors = true;
        public bool Raining;
        public bool IsRainingHere() => Raining;
        public readonly List<NPC> characters = new();
        public readonly List<Farmer> farmers = new();
        public readonly Dictionary<long, FarmAnimal> animals = new();
        public readonly Dictionary<Vector2, Object> objects = new();
        public readonly Dictionary<Vector2, TerrainFeatures.TerrainFeature> terrainFeatures = new();
        public bool isTileOnMap(int x, int y) => x is >= 0 and < 64 && y is >= 0 and < 64;
        public string? doesTileHaveProperty(int x, int y, string property, string layer) => Map.GetLayer(layer)?.Tiles[x, y]?.Properties.GetValueOrDefault(property);
    }
    public static class Game1
    {
        public static Farmer? player;
        public static GameLocation? currentLocation;
        public static string currentSeason = "spring";
        public static int dayOfMonth = 1, year = 1, timeOfDay = 900;
        public static readonly Dictionary<string, NPC> Npcs = new();
        public static NPC? getCharacterFromName(string name) => Npcs.GetValueOrDefault(name);
    }
}
namespace StardewValley.Characters
{
    public class Pet : StardewValley.NPC { }
    public class Horse : StardewValley.NPC { }
}
namespace StardewValley.Monsters { public class Monster : StardewValley.NPC { public int Health = 10; } }
namespace StardewValley.TerrainFeatures
{
    public class TerrainFeature { }
    public sealed class WildTreeData { public string SeedItemId = ""; }
    public sealed class FruitTreeData { public string DisplayName = ""; }
    public class Tree : TerrainFeature
    {
        public readonly StardewValley.NetString treeType = new();
        public readonly StardewValley.NetBool stump = new();
        public readonly StardewValley.NetInt growthStage = new();
        public WildTreeData? Data;
        public WildTreeData? GetData() => Data;
    }
    public class FruitTree : TerrainFeature
    {
        public readonly StardewValley.NetString treeId = new();
        public readonly StardewValley.NetBool stump = new();
        public readonly StardewValley.NetInt growthStage = new();
        public FruitTreeData? Data;
        public FruitTreeData? GetData() => Data;
        public string GetDisplayName() => Data?.DisplayName ?? "ERROR";
    }
    public class Grass : TerrainFeature { }
    public class HoeDirt : TerrainFeature { public StardewValley.Crop? crop; }
}
namespace AIWorld.Demo
{
    public sealed class SmapiHardAnchorProvider
    {
        public static bool Blocked;
        public bool IsHardAnchorActive(string _) => Blocked;
    }
}
