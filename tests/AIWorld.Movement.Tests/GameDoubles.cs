// Headless contracts for production movement code. This is not a simulation of the game engine.
namespace Microsoft.Xna.Framework
{
    public readonly record struct Point(int X, int Y) { public static Point Zero => new(0, 0); }
    public readonly record struct Vector2(float X, float Y) { public static Vector2 Zero => new(0, 0); }
    public readonly record struct Rectangle(int X, int Y, int Width, int Height);
    public class GameTime { }
}
namespace xTile.Dimensions { public readonly record struct Location(int X, int Y); }
namespace StardewModdingAPI
{
    public enum LogLevel { Debug, Info, Warn, Error }
    public interface IMonitor { void Log(string message, LogLevel level); }
}
namespace StardewValley
{
    using Microsoft.Xna.Framework;
    using StardewValley.Pathfinding;
    public enum Gender { Male, Female, Undefined }
    public class Sprite { public void StopAnimation() { } }
    public class Character
    {
        public GameLocation? currentLocation;
        public Point TilePoint;
        public bool Moving;
        public PathFindController? controller;
        public bool isMoving() => Moving;
        public void Halt() { Moving = false; }
    }
    public class ScheduleEntry { public string targetLocationName = ""; public Point targetTile; }
    public class NPC : Character
    {
        public string Name = "Abigail", DefaultMap = "Town";
        public Vector2 DefaultPosition;
        public int FacingDirection, speed = 2;
        public Gender Gender = Gender.Female;
        public Dictionary<int, ScheduleEntry>? Schedule;
        public bool ignoreScheduleToday, followSchedule = true;
        public PathFindController? temporaryController;
        public object? DirectionsToNewLocation;
        public bool IsWalkingInSquare;
        public readonly Sprite Sprite = new();
        public readonly Dictionary<string, string> modData = new();
    }
    public class Farmer : Character
    {
        public long UniqueMultiplayerID = 1;
        public readonly HashSet<string> mailReceived = new();
    }
    public class Layer { public int LayerWidth = 30, LayerHeight = 30; }
    public class Map { public Layer[] Layers = { new() }; }
    public class Warp(int x, int y, string target, int tx, int ty, bool flipFarmer = false)
    { public int X = x, Y = y, TargetX = tx, TargetY = ty; public string TargetName = target; }
    public class Doors
    {
        public readonly Dictionary<Point, string> Data = new();
        public IEnumerable<KeyValuePair<Point, string>> Pairs => Data;
    }
    public class Building
    {
        public Point Door;
        public GameLocation? Indoors;
        public Point getPointForHumanDoor() => Door;
        public GameLocation? GetIndoors() => Indoors;
        public bool HasIndoors() => Indoors != null;
    }
    public class GameLocation
    {
        public string NameOrUniqueName = "Town";
        public Map Map = new();
        public bool Excluded;
        public static bool FestivalClosed;
        public object? currentEvent;
        public readonly HashSet<Point> Blocked = new(), Water = new();
        public readonly List<Warp> warps = new();
        public readonly List<Building> buildings = new();
        public readonly Doors doors = new();
        public readonly Dictionary<Point, Warp> DoorWarps = new();
        public readonly Dictionary<(int, int, string, string), string> Properties = new();
        public bool ShouldExcludeFromNpcPathfinding() => Excluded;
        public bool isTileOnMap(int x, int y) => x >= 0 && y >= 0 && x < Map.Layers[0].LayerWidth && y < Map.Layers[0].LayerHeight;
        public bool isWaterTile(int x, int y) => Water.Contains(new(x, y));
        public bool CanSpawnCharacterHere(Vector2 v) => isTileOnMap((int)v.X, (int)v.Y) && !Blocked.Contains(new((int)v.X, (int)v.Y));
        public bool isTilePassable(xTile.Dimensions.Location v, object viewport) => CanSpawnCharacterHere(new(v.X, v.Y));
        public bool isCollidingPosition(Rectangle r, object viewport, bool isFarmer, int damage, bool glider, Character character,
            bool pathfinding, bool projectile = false, bool ignoreCharacterRequirement = false, bool skipCollisionEffects = false)
            => !CanSpawnCharacterHere(new(r.X / 64, r.Y / 64));
        public string? doesTileHaveProperty(int x, int y, string property, string layer) => Properties.GetValueOrDefault((x, y, property, layer));
        public Warp? getWarpFromDoor(Point p, Character character) => DoorWarps.GetValueOrDefault(p);
        public static bool AreStoresClosedForFestival() => FestivalClosed;
    }
    public static class Utility
    {
        public static void ForEachLocation(Func<GameLocation, bool> f, bool includeInteriors = true, bool includeGenerated = false)
        { foreach (var loc in Game1.locations) if (!f(loc)) break; }
    }
    public static class Game1
    {
        public static bool hasLoadedGame = true, IsMasterGame = true;
        public static int timeOfDay = 900;
        public static object viewport = new();
        public static GameLocation? currentLocation;
        public static Farmer? player;
        public static Farmer MasterPlayer = new();
        public static readonly List<GameLocation> locations = new();
        public static readonly Dictionary<string, NPC> Npcs = new();
        public static int Warps;
        public static GameLocation? getLocationFromName(string name) => locations.FirstOrDefault(l => l.NameOrUniqueName == name);
        public static NPC? getCharacterFromName(string name) => Npcs.GetValueOrDefault(name);
        public static void warpCharacter(NPC npc, GameLocation loc, Vector2 p)
        { Warps++; npc.currentLocation = loc; npc.TilePoint = new((int)p.X, (int)p.Y); }
    }
}
namespace StardewValley.Pathfinding
{
    using Microsoft.Xna.Framework;
    using StardewValley;
    public class PathNode { }
    public static class WarpPathfindingCache { public static readonly Dictionary<string, Gender> GenderRestrictions = new(); }
    public class PathFindController
    {
        public static bool ThrowOnCreate, NoPath;
        public static Func<Point, Point, Stack<Point>?>? Find;
        public Stack<Point>? pathToEndPoint;
        public Point endPoint;
        public GameLocation location;
        public bool NPCSchedule, nonDestructivePathing;
        protected Character character;
        public delegate bool isAtEnd(PathNode node, Point end, GameLocation loc, Character c);
        public static bool isAtEndPoint(PathNode n, Point p, GameLocation l, Character c) => true;
        public static Stack<Point>? findPath(Point start, Point end, isAtEnd predicate, GameLocation l, Character c, int limit)
            => NoPath ? null : Find?.Invoke(start, end) ?? new Stack<Point>(new[] { end, start });
        public PathFindController(Character c, GameLocation loc, Point p, int facing)
        { if (ThrowOnCreate) throw new InvalidOperationException(); character = c; location = loc; endPoint = p; pathToEndPoint = findPath(c.TilePoint, p, isAtEndPoint, loc, c, 2000); }
        public PathFindController(Stack<Point> path, GameLocation loc, Character c, Point end)
        { character = c; location = loc; pathToEndPoint = path; endPoint = end; }
        public virtual bool update(GameTime time)
        { if (pathToEndPoint == null || pathToEndPoint.Count == 0) return true; character.TilePoint = pathToEndPoint.Pop(); return pathToEndPoint.Count == 0; }
    }
}
namespace AIWorld.Demo
{
    public class SmapiHardAnchorProvider
    { public static bool Blocked; public bool IsHardAnchorActive(string _) => Blocked; }
}
