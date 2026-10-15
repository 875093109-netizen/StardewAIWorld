namespace Microsoft.Xna.Framework
{
    public record struct Vector2(float X, float Y);
    public record struct Point(int X, int Y);
}
namespace StardewModdingAPI
{
    public enum LogLevel { Debug, Info, Warn }
    public interface IMonitor { void Log(string message, LogLevel level); }
}
namespace StardewValley.TerrainFeatures
{
    public class Tree { public StardewValley.Net<int> growthStage = new() { Value = 5 }; }
}
namespace StardewValley
{
    using Microsoft.Xna.Framework;
    public class Net<T> { public T Value = default!; }
    public class PathController { public Stack<Point>? pathToEndPoint = new(); }
    public class SchedulePathDescription
    {
        public Stack<Point> route = new();
        public string targetLocationName = "Town";
        public Point targetTile = new(35, 89);
        public string? endOfRouteBehavior = "penny_read";
    }
    public class GameLocation
    {
        public string NameOrUniqueName = "Town", DisplayName = "鹈鹕镇";
        public bool IsOutdoors = true;
        public Dictionary<Vector2, object> terrainFeatures = new();
    }
    public class Event { public List<NPC> actors = new(); public string id = "event"; }
    public class NPC
    {
        public string Name = "Penny", ScheduleKey = "spring";
        public Dictionary<int, SchedulePathDescription>? Schedule = new();
        public SchedulePathDescription? DirectionsToNewLocation;
        public PathController? controller, temporaryController;
        public bool ignoreScheduleToday, Moving;
        public GameLocation? currentLocation;
        public Point TilePoint;
        public Net<bool> doingEndOfRouteAnimation = new();
        public Net<string> endOfRouteBehaviorName = new();
        public Func<Item, int> Taste = _ => 8;
        public List<string> Evaluated = new();
        public bool isMoving() => Moving;
        public int getGiftTasteForThisItem(Item item) { Evaluated.Add(item.Id); return Taste(item); }
    }
    public class Item(string id) { public string Id = id; }
    public record ItemData(string QualifiedItemId, string DisplayName, string InternalName);
    public class ObjectData { public int Category; }
    public static class ItemRegistry
    {
        public static Dictionary<string, ItemData> Data = new();
        public static ItemData? GetData(string id) => Data.GetValueOrDefault(id);
        public static Item Create(string id) => new(id);
    }
    public static class Game1
    {
        public static Dictionary<string, NPC> Npcs = new();
        public static Dictionary<string, GameLocation> Locations = new();
        public static Dictionary<string, ObjectData> objectData = new();
        public static Dictionary<string, string> NPCGiftTastes = new();
        public static Event? CurrentEvent;
        public static int timeOfDay = 910, dayOfMonth = 11, year = 1;
        public static string currentSeason = "spring";
        public static NPC? getCharacterFromName(string id) => Npcs.GetValueOrDefault(id);
        public static GameLocation? getLocationFromName(string id) => Locations.GetValueOrDefault(id);
    }
}
