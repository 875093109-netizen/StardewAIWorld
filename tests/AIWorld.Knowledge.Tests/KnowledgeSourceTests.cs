using AIWorld.Core.Chat;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using AIWorld.Demo;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using Xunit;

namespace AIWorld.Knowledge.Tests;

public class KnowledgeSourceTests
{
    private sealed class Monitor : IMonitor { public void Log(string message, LogLevel level) { } }
    private readonly Monitor _monitor = new();
    private readonly NPC _npc;
    private readonly NpcKnowledgeSource _source;
    private readonly Dictionary<string, string> _activities = new() { ["penny_read"] = "读书", ["Sophia_Farm1"] = "在葡萄园劳作" };
    private readonly Dictionary<string, NpcJobRule> _jobRules = new();
    public KnowledgeSourceTests()
    {
        Game1.Npcs.Clear(); Game1.Locations.Clear(); Game1.objectData.Clear(); Game1.NPCGiftTastes.Clear(); ItemRegistry.Data.Clear();
        Game1.CurrentEvent = null; Game1.timeOfDay = 910;
        var town = new GameLocation(); town.terrainFeatures[new(36, 89)] = new Tree(); Game1.Locations["Town"] = town;
        _npc = new NPC { currentLocation = town, TilePoint = new(30, 89) };
        _npc.Schedule![800] = new(); _npc.Schedule[1300] = new() { targetLocationName = "Trailer", endOfRouteBehavior = "penny_dishes" };
        _npc.DirectionsToNewLocation = _npc.Schedule[800]; _npc.controller = new() { pathToEndPoint = _npc.DirectionsToNewLocation.route }; _npc.controller.pathToEndPoint!.Push(new(35, 89));
        Game1.Npcs["Penny"] = _npc;
        _source = new(_monitor, () => _activities, () => _jobRules);
    }

    [Fact] public void JobFollowsActualTravelArrivalAndActivityInsteadOfTheClock()
    {
        var provider = new SelfStateProvider(_monitor, _ => false) { Knowledge = _source };
        Assert.Equal(NpcJobPhase.Travelling, provider.Get("Penny").Job!.Phase);
        Game1.timeOfDay = 1400; // later timetable entry is not proof of arrival
        Assert.Equal("Town", provider.Get("Penny").Job!.Target);
        _npc.TilePoint = new(35, 89); _npc.controller = null;
        Assert.Equal(NpcJobPhase.Arrived, provider.Get("Penny").Job!.Phase);
        Assert.Null(provider.Get("Penny").Job!.Activity);
        _npc.doingEndOfRouteAnimation.Value = true; _npc.endOfRouteBehaviorName.Value = "penny_read";
        var doing = provider.Get("Penny"); Assert.Equal(NpcJobPhase.Doing, doing.Job!.Phase); Assert.Equal("读书", doing.Job.Activity);
        var prompt = new ChatContextAssembler("Penny").BuildSystemPrompt("Penny", new() { SaveSchemaVersion = "1" }, Array.Empty<MemoryRecord>(), doing);
        Assert.Contains("CURRENT JOB", prompt); Assert.Contains("schedule-target-and-live-animation", prompt);
        Assert.Contains("Speech cannot change this state", prompt);
        _npc.ignoreScheduleToday = true;
        Assert.Equal(NpcJobPhase.Interrupted, provider.Get("Penny").Job!.Phase);
    }
    [Fact] public void ReadingBecomesTeachingOnlyWithExactClassroomAnimationAndNearbyStationaryStudent()
    {
        var room = new GameLocation { NameOrUniqueName = "ArchaeologyHouse", IsOutdoors = false };
        Game1.Locations[room.NameOrUniqueName] = room;
        _npc.currentLocation = room; _npc.TilePoint = new(17, 9); _npc.controller = null;
        var route = _npc.Schedule![800]; route.targetLocationName = room.NameOrUniqueName; route.targetTile = _npc.TilePoint;
        _npc.doingEndOfRouteAnimation.Value = true; _npc.endOfRouteBehaviorName.Value = "penny_read";
        var shipped = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, NpcJobRule>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets/job-rules.json")))!;
        foreach (var rule in shipped) _jobRules[rule.Key] = rule.Value;
        var provider = new SelfStateProvider(_monitor, _ => false) { Knowledge = _source };
        Assert.Equal("读书", provider.Get("Penny").Job!.Activity);
        var student = new NPC { Name = "Jas", currentLocation = room, TilePoint = new(19, 10), Moving = true };
        Game1.Npcs["Jas"] = student;
        Assert.Equal("读书", provider.Get("Penny").Job!.Activity);
        student.Moving = false;
        Assert.Equal(UiText.T("job.teaching"), provider.Get("Penny").Job!.Activity);
        student.currentLocation = Game1.Locations["Town"];
        Assert.Equal("读书", provider.Get("Penny").Job!.Activity);
        student.currentLocation = room; _npc.endOfRouteBehaviorName.Value = "unrelated";
        Assert.Equal(NpcJobPhase.Arrived, provider.Get("Penny").Job!.Phase);
        _npc.endOfRouteBehaviorName.Value = "penny_read"; _npc.TilePoint = new(18, 9);
        Assert.NotEqual(NpcJobPhase.Doing, provider.Get("Penny").Job!.Phase);
    }
    [Fact] public void VerifiedPlayerOverridesSuspendAndThenReleaseTheNativeJob()
    {
        bool following = false;
        NpcJobOverride? action = null;
        var provider = new SelfStateProvider(_monitor, _ => following) { Knowledge = _source, JobOverride = _ => action };
        following = true; Assert.Equal(NpcJobPhase.Following, provider.Get("Penny").Job!.Phase);
        action = new(NpcJobOverrideKind.Travel, "Town", 35, 89, false, "blocked");
        Assert.Equal(NpcJobPhase.Blocked, provider.Get("Penny").Job!.Phase);
        action = action with { Failure = null };
        Assert.Equal(NpcJobPhase.Interrupted, provider.Get("Penny").Job!.Phase);
        following = false; action = new(NpcJobOverrideKind.Travel, "Beach", 4, 5, true);
        Assert.Equal(NpcJobPhase.Travelling, provider.Get("Penny").Job!.Phase);
        Assert.Null(provider.Get("Penny").Schedule!.Executing);
        action = action with { Failure = "pathBlocked" };
        Assert.Equal(NpcJobPhase.Blocked, provider.Get("Penny").Job!.Phase);
        action = new(NpcJobOverrideKind.Appointment, "Town"); _npc.controller = null;
        Assert.Equal(NpcJobPhase.Waiting, provider.Get("Penny").Job!.Phase);
        action = action with { Target = "Beach" };
        Assert.Equal(NpcJobPhase.Interrupted, provider.Get("Penny").Job!.Phase);
        action = new(NpcJobOverrideKind.ReturnToSchedule, "Town", 35, 89, true);
        Assert.Equal(NpcJobPhase.Returning, provider.Get("Penny").Job!.Phase);
        Game1.CurrentEvent = new() { actors = new() { _npc } };
        Assert.Equal(NpcJobPhase.Event, provider.Get("Penny").Job!.Phase);
        Game1.CurrentEvent = null; action = null;
        _npc.controller = new() { pathToEndPoint = _npc.DirectionsToNewLocation!.route };
        Assert.Equal(NpcJobPhase.Travelling, provider.Get("Penny").Job!.Phase);
    }
    [Fact] public void FutureAppointmentDoesNotChangeTheJobAndMissingScheduleDoesNotInventOne()
    {
        var provider = new SelfStateProvider(_monitor, _ => false, currentGoal: _ => "Tomorrow 14:00 Beach") { Knowledge = _source };
        Assert.Equal("Town", provider.Get("Penny").Job!.Target);
        Assert.Equal(NpcJobPhase.Travelling, provider.Get("Penny").Job!.Phase);
        _npc.Schedule = null;
        Assert.Equal(NpcJobPhase.Unknown, provider.Get("Penny").Job!.Phase);
        Assert.Equal(NpcJobPhase.Unknown, provider.Get("MissingNpc").Job!.Phase);
    }
    [Fact] public void ShippedActivitiesAndEveryJobPhaseHaveSixLanguageLabels()
    {
        var labels = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets/schedule-activities.json")))!;
        foreach (string language in UiText.Supported)
        {
            var catalog = UiText.Catalog(language);
            foreach (var label in labels.Values) Assert.True(catalog.ContainsKey(label["i18n:".Length..]));
            foreach (var phase in Enum.GetValues<NpcJobPhase>()) Assert.True(catalog.ContainsKey("job.phase." + phase.ToString().ToLowerInvariant()));
            Assert.True(catalog.ContainsKey("job.teaching"));
        }
        _activities["penny_read"] = labels["penny_read"];
        Assert.Equal(UiText.T("job.reading"), _source.Schedule(_npc, CurrentActivity.CANONICAL)!.Executing!.Activity);
    }

    [Fact] public void PennyUsesSelectedOutdoorReadingRouteNotAnInventedLibrary()
    {
        var state = new SelfStateProvider(_monitor, _ => false) { Knowledge = _source }.Get("Penny");
        Assert.Equal("Town", state.Schedule!.Executing!.Location);
        Assert.Equal("读书", state.Schedule.Executing.Activity); Assert.Contains("树", state.Schedule.Executing.Place);
        Assert.Contains("尚未到达", state.Schedule.Phase); Assert.Equal("Trailer", Assert.Single(state.Schedule.Upcoming).Location);
        var prompt = new ChatContextAssembler("Penny").BuildSystemPrompt("Penny", new() { SaveSchemaVersion = "1" }, Array.Empty<MemoryRecord>(), state);
        Assert.Contains("读书", prompt); Assert.Contains("OWN SCHEDULE", prompt); Assert.DoesNotContain("ArchaeologyHouse", prompt);
    }
    [Fact] public void PassingTimetableTimeDoesNotOverrideTheActualEarlierRoute()
    {
        Game1.timeOfDay = 1400;
        var state = _source.Schedule(_npc, CurrentActivity.CANONICAL)!;
        Assert.Equal(800, state.Executing!.Time); Assert.Equal(1300, state.DuePlan!.Time);
        Assert.Contains("尚未到达", state.Phase);
    }
    [Fact] public void ArrivalRequiresExactLocationAndActivityRequiresActualAnimation()
    {
        _npc.TilePoint = new(35, 89); _npc.controller = null;
        Assert.Contains("具体动作是否开始未确认", _source.Schedule(_npc, CurrentActivity.CANONICAL)!.Phase);
        _npc.doingEndOfRouteAnimation.Value = true; _npc.endOfRouteBehaviorName.Value = "penny_read";
        Assert.Contains("正在执行", _source.Schedule(_npc, CurrentActivity.CANONICAL)!.Phase);
        _npc.currentLocation = new() { NameOrUniqueName = "Other" };
        Assert.Contains("未确认", _source.Schedule(_npc, CurrentActivity.CANONICAL)!.Phase);
    }
    [Theory]
    [InlineData(CurrentActivity.EVENT)] [InlineData(CurrentActivity.FOLLOW_PLAYER)] [InlineData(CurrentActivity.PLAYER_REQUEST)]
    public void OverridesSuspendScheduleWithoutErasingPlan(CurrentActivity owner)
    {
        var state = _source.Schedule(_npc, owner)!;
        Assert.Contains("中断", state.Phase); Assert.Null(state.Executing); Assert.NotNull(state.DuePlan);
    }
    [Fact] public void IgnoreFlagAndTemporaryControllerAreAlsoOverrides()
    {
        _npc.ignoreScheduleToday = true;
        Assert.Null(_source.Schedule(_npc, CurrentActivity.CANONICAL)!.Executing);
        _npc.ignoreScheduleToday = false; _npc.temporaryController = new();
        Assert.Contains("中断", _source.Schedule(_npc, CurrentActivity.CANONICAL)!.Phase);
    }
    [Fact] public void ModScheduleRefreshIsReadImmediatelyAndUnrecognisedBehaviorIsNotGuessed()
    {
        _npc.Schedule![800] = new() { targetLocationName = "Custom_BlueMoonVineyard", endOfRouteBehavior = "Sophia_Farm1" };
        Assert.Null(_source.Schedule(_npc, CurrentActivity.CANONICAL)!.Executing); // stale route rejected
        _npc.DirectionsToNewLocation = _npc.Schedule[800];
        _npc.controller!.pathToEndPoint = _npc.DirectionsToNewLocation.route;
        var state = _source.Schedule(_npc, CurrentActivity.CANONICAL)!;
        Assert.Equal("在葡萄园劳作", state.Executing!.Activity);
        _npc.Schedule[800].endOfRouteBehavior = "SomeMod_Secret";
        Assert.Null(_source.Schedule(_npc, CurrentActivity.CANONICAL)!.Executing!.Activity);
    }
    [Fact] public void UpcomingPreviewIsBoundedAndDoesNotExposeScheduleDialogue()
    {
        for (int i = 0; i < 100; i++) _npc.Schedule![1000 + i] = new();
        Assert.Equal(3, _source.Schedule(_npc, CurrentActivity.CANONICAL)!.Upcoming.Length);
        _npc.Schedule!.Clear(); Assert.Null(_source.Schedule(_npc, CurrentActivity.CANONICAL));
    }
    [Fact] public void AnotherModsControllerCannotMasqueradeAsTheRetainedSchedule()
    {
        _npc.controller!.pathToEndPoint = new(); _npc.controller.pathToEndPoint.Push(new(10, 10));
        var state = _source.Schedule(_npc, CurrentActivity.CANONICAL)!;
        Assert.Contains("中断", state.Phase); Assert.Null(state.Executing); Assert.NotNull(state.DuePlan);
    }
    private void Item(string id, string name, string english, int category)
    { Game1.objectData[id] = new() { Category = category }; ItemRegistry.Data["(O)" + id] = new("(O)" + id, name, english); }
    [Fact] public void AskedGiftUsesGameEvaluatorEvenWhenItContradictsDeclaredTaste()
    {
        Item("376", "虞美人", "Poppy", -80); Game1.NPCGiftTastes["Penny"] = "love/376/like//dislike//hate//neutral/";
        _npc.Taste = _ => 6; // a mod's runtime override takes precedence over raw data
        var prompt = _source.Gifts("Penny", "虞美人你喜欢吗？");
        Assert.Contains("讨厌", prompt); Assert.DoesNotContain("\"taste\":\"最爱\"", prompt);
        Assert.Equal("(O)376", Assert.Single(_npc.Evaluated));
    }
    [Fact] public void FruitQueryRetrievesLovedFruitAndNeverClaimsUniqueFavourite()
    {
        Item("254", "甜瓜", "Melon", -79); Item("376", "虞美人", "Poppy", -80);
        Game1.NPCGiftTastes["Penny"] = "love/254 376/like//dislike//hate//neutral/";
        _npc.Taste = _ => 0;
        var prompt = _source.Gifts("Penny", "最喜欢的水果？");
        Assert.Contains("甜瓜", prompt); Assert.Contains("最爱", prompt); Assert.Contains("not exhaustive", prompt);
        Assert.Contains("unique favourite", prompt);
    }
    [Fact] public void CustomItemLookupWorksAndTasteIsNotCached()
    {
        Item("SVE.Special", "特别的水果", "SpecialFruit", -79);
        _npc.Taste = _ => 0;
        Assert.Contains("最爱", _source.Gifts("Penny", "SpecialFruit"));
        _npc.Taste = _ => 4;
        Assert.Contains("不喜欢", _source.Gifts("Penny", "特别的水果"));
    }
    [Theory]
    [InlineData("水果")][InlineData("fruit")][InlineData("果物")][InlineData("과일")][InlineData("fruta")]
    [InlineData("魚")][InlineData("fish")][InlineData("생선")][InlineData("pescado")]
    public void LocalizedCategoryQueriesReachItemsOutsideDefaultCandidateBudget(string query)
    {
        for (int i = 0; i < 100; i++) Item("a" + i, "Stone" + i, "Stone" + i, -12);
        Item("zfruit", "Melon", "Melon", -79); Item("zfish", "Tuna", "Tuna", -4);
        _npc.Taste = _ => 0;
        var prompt = _source.Gifts("Penny", query);
        bool fruit = new[] { "水果", "fruit", "果物", "과일", "fruta" }.Contains(query);
        Assert.Contains(fruit ? "Melon" : "Tuna", prompt);
        Assert.Contains(fruit ? "(O)zfruit" : "(O)zfish", _npc.Evaluated);
    }
    [Fact] public void MetadataInvalidationRefreshesLocalizedModNames()
    {
        Item("376", "旧名", "Poppy", -80); _source.Gifts("Penny", "旧名");
        Item("376", "新名", "Poppy", -80); _source.Invalidate();
        Assert.Contains("新名", _source.Gifts("Penny", "新名"));
    }
    [Fact] public void GiftLookupNeverReadsAnotherNpcsTasteAndHasBoundedEvaluation()
    {
        for (int i = 0; i < 500; i++) Item(i.ToString(), "水果" + i, "Fruit" + i, -79);
        Game1.NPCGiftTastes["Harvey"] = "love/0/like//dislike//hate//neutral/";
        Game1.Npcs["Harvey"] = new() { Name = "Harvey", Taste = _ => throw new Exception("private") };
        var prompt = _source.Gifts("Penny", "水果");
        Assert.DoesNotContain("Harvey", prompt); Assert.True(_npc.Evaluated.Count <= 64); Assert.True(prompt.Length < 2200);
    }
}
