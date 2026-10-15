using AIWorld.Core.Chat;
using AIWorld.Demo;
using StardewModdingAPI;
using StardewValley;
using Xunit;

namespace AIWorld.Talk.Tests;

public sealed class VillageDirectorTests : IDisposable
{
    private readonly TalkOptions _options = new();
    private readonly AiWorldChatServer _server = new();
    private readonly WorldPerceptionSource _perception = new();
    private readonly HashSet<string> _owned = new();
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private readonly VillageChatDirector _director;
    public VillageDirectorTests()
    {
        Context.IsWorldReady = Context.IsMainPlayer = true;
        Game1.currentLocation = new(); Game1.player = new(); Game1.Date = new();
        Game1.eventUp = false; Game1.TimePasses = true; Game1.CurrentEvent = Game1.activeClickableMenu = null;
        SmapiHardAnchorProvider.Anchors.Clear();
        foreach (var id in new[] { "A", "B", "C" }) Game1.currentLocation.characters.Add(new() { Name = id, currentLocation = Game1.currentLocation });
        _director = new(_server, () => _options, _perception, _owned.Contains, () => _now);
    }
    private void Tick(int seconds = 20) { _now = _now.AddSeconds(seconds); _director.Tick(); }
    [Fact] public void ThreeNearbyVillagersTakeTurnsAndCallsAreBounded()
    {
        _options.DailyAmbientCalls = 3;
        for (int i = 0; i < 20; i++) Tick();
        Assert.Equal(new[] { "A", "B", "C" }, _server.Speakers); Assert.Equal(3, _director.CallsToday);
    }
    [Theory]
    [InlineData("farmhand")][InlineData("event")][InlineData("menu")][InlineData("paused")][InlineData("hidden")][InlineData("owned")][InlineData("moving")]
    public void IneligibleWorldOrNpcsNeverTriggerModel(string blocker)
    {
        if (blocker == "farmhand") Context.IsMainPlayer = false;
        if (blocker == "event") Game1.eventUp = true;
        if (blocker == "menu") Game1.activeClickableMenu = new();
        if (blocker == "paused") Game1.TimePasses = false;
        if (blocker == "hidden") _perception.Visible = false;
        if (blocker == "owned") _owned.UnionWith(new[] { "A", "B", "C" });
        if (blocker == "moving") foreach (var npc in Game1.currentLocation.characters) npc.Moving = true;
        Tick(); Assert.Empty(_server.Speakers);
    }
    [Fact] public void PendingRequestIsCancelledByOpeningAMenu()
    {
        _server.Hold = true; Tick(); Assert.Single(_server.Speakers);
        Game1.activeClickableMenu = new(); Tick(); Assert.True(_server.PendingToken.IsCancellationRequested);
    }
    [Fact] public void ChangingSettingsCancelsPendingRequestWithoutStartingAnother()
    {
        _server.Hold = true; Tick(); _options.Revision++; Tick();
        Assert.True(_server.PendingToken.IsCancellationRequested); Assert.Single(_server.Speakers);
    }
    [Fact] public void DailyBudgetResetsOnNextDayButNotEveryTick()
    {
        _options.DailyAmbientCalls = 1;
        for (int i = 0; i < 10; i++) Tick();
        Assert.Single(_server.Speakers); Game1.Date.TotalDays++;
        Tick(180); Assert.Equal(2, _server.Speakers.Count); Assert.Equal(1, _director.CallsToday);
    }
    [Fact] public void ManualStartWorksWithAutomaticTalkDisabled()
    {
        _options.AutoConversations = false; Tick(); Assert.Empty(_server.Speakers);
        _director.RequestConversation(); Tick(); Assert.Single(_server.Speakers);
    }
    public void Dispose() => _director.Dispose();
}
