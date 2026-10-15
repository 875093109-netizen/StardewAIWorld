// Headless contracts for the actual director; not a rendering or game-runtime test.
using AIWorld.Core.Chat;
namespace StardewModdingAPI { public static class Context { public static bool IsWorldReady = true, IsMainPlayer = true; } }
namespace StardewValley
{
    public sealed class Flag { public bool Value; }
    public sealed class Point { public int X, Y; }
    public sealed class Location { public string NameOrUniqueName = "Town"; public List<NPC> characters = new(); }
    public sealed class NPC
    {
        public string Name = ""; public bool IsVillager = true, Moving; public Point TilePoint = new();
        public Location currentLocation = null!; public object? controller;
        public Dictionary<string, string> modData = new(); public string? Spoken;
        public bool isMoving() => Moving; public void showTextAboveHead(string text) => Spoken = text;
    }
    public sealed class Farmer { public Point TilePoint = new(); }
    public sealed class Date { public int TotalDays = 1; }
    public static class Game1
    {
        public static bool eventUp, TimePasses = true; public static object? CurrentEvent, activeClickableMenu;
        public static Location currentLocation = new(); public static Farmer player = new(); public static Date Date = new();
        public static bool shouldTimePass() => TimePasses;
        public static NPC? getCharacterFromName(string id) => currentLocation.characters.FirstOrDefault(n => n.Name == id);
    }
}
namespace AIWorld.Demo
{
    public sealed class SmapiHardAnchorProvider { public static HashSet<string> Anchors = new(); public bool IsHardAnchorActive(string id) => Anchors.Contains(id); }
    public sealed class WorldPerceptionSource { public bool Visible = true; public bool CanSeeNpcs(string observer, IEnumerable<string> targets) => Visible; }
    public sealed class AiWorldChatServer
    {
        public List<string> Speakers = new(); public bool Hold; public CancellationToken PendingToken;
        public TaskCompletionSource<bool>? Pending;
        public Task<bool> VillageTurnAsync(string session, string speaker, string[] members, string history, Func<bool> valid, Action<string> commit, CancellationToken token)
        {
            Speakers.Add(speaker); PendingToken = token;
            if (Hold) { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); token.Register(() => Pending.TrySetResult(false)); return Pending.Task; }
            if (!valid()) return Task.FromResult(false); commit("hello"); return Task.FromResult(true);
        }
    }
}
