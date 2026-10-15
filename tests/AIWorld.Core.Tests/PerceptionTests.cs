using AIWorld.Core.Perception;
using AIWorld.Core.Contracts;
using AIWorld.Core.Pipeline;
using Xunit;

namespace AIWorld.Core.Tests;

public sealed class PerceptionTests
{
    private sealed class Source(string id, Func<IEnumerable<WorldEntitySnapshot>> capture) : IWorldEntitySource
    {
        public string SourceId => id;
        public IEnumerable<WorldEntitySnapshot> Capture(PerceptionQuery observer) => capture();
    }
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly PerceptionQuery Query = new("Abigail", "Town", new(10, 10), "test:self");
    private static WorldEntitySnapshot Entity(string id = "one", int x = 11, int y = 10) => new(id, "creature", EntityKind.Creature, "Town", new(x, y));
    private static PerceptionFrame Frame(params WorldEntitySnapshot[] entities)
    {
        var engine = new PerceptionEngine(); engine.Register(new Source("test", () => entities));
        return engine.Capture(Query, (_, _) => true, Now);
    }
    private static NpcIntent Intent(string target = "test:one") => new()
    { IntentId = "observe-1", Type = PerceptionEngine.ObserveCapability, Target = target, Priority = .5f, SchemaVersion = "1" };

    [Fact] public void ScopeFiltersSelfOtherMapDistanceAndOcclusion()
    {
        var engine = new PerceptionEngine();
        engine.Register(new Source("test", () => new[] { Entity("self"), Entity("far", 30),
            Entity("remote") with { Location = "Beach" }, Entity("wall", 12), Entity() }));
        var frame = engine.Capture(Query, (_, b) => b.X != 12, Now);
        Assert.Equal("test:one", Assert.Single(frame.Entities).Id);
    }
    [Fact] public void WorldOwnershipIsNotAutomaticallyNpcKnowledge()
    {
        var hidden = Assert.Single(Frame(Entity() with { OwnerId = "secret-owner" }).Entities);
        Assert.Null(hidden.KnownOwnerId); Assert.Equal(EntityHostility.Unknown, hidden.Hostility);
        Assert.DoesNotContain("secret-owner", PerceptionEngine.Prompt(Frame(Entity() with { OwnerId = "secret-owner" })));
        Assert.Equal("farmer", Assert.Single(Frame(Entity() with { OwnerId = "farmer", OwnershipObservable = true }).Entities).KnownOwnerId);
    }
    [Fact] public void NearbyCreatureWinsOverSceneryAndBudgetNeverCreatesPartialIds()
    {
        var rows = Enumerable.Range(0, 200).Select(i => Entity("object" + i, 10) with
        { Kind = EntityKind.Object, Name = new string('哈', 1000), VisibleState = new string('"', 1000) })
            .Append(Entity("living", 16)).ToArray();
        var frame = Frame(rows); var prompt = PerceptionEngine.Prompt(frame);
        Assert.Equal("test:living", frame.Entities[0].Id);
        Assert.InRange(frame.Entities.Count, 1, 8); Assert.True(frame.Partial);
        Assert.InRange(prompt.Length, 1, PerceptionEngine.ContextCharacters);
        foreach (var e in frame.Entities) Assert.Contains(e.Id, prompt);
    }
    [Fact] public void ThrowingSourceIsIsolatedAndInfiniteEnumerationIsBounded()
    {
        var failures = new List<string>(); var engine = new PerceptionEngine((s, _) => failures.Add(s));
        engine.Register(new Source("bad", () => throw new InvalidOperationException()));
        engine.Register(new Source("ok", () => new[] { Entity(x: 10) }));
        int consumed = 0;
        IEnumerable<WorldEntitySnapshot> Infinite() { while (true) { consumed++; yield return Entity("n" + consumed); } }
        engine.Register(new Source("large", Infinite));
        var frame = engine.Capture(Query, (_, _) => true, Now);
        Assert.Equal(513, consumed); Assert.Contains("bad", failures); Assert.True(frame.Partial);
        Assert.Contains(frame.Entities, e => e.Id == "ok:one");
    }
    [Fact] public void AmbiguousDuplicateIdentityIsNotActionable()
    {
        Assert.Empty(Frame(Entity(), Entity()).Entities);
        var engine = new PerceptionEngine();
        Assert.True(engine.Register(new Source("mod", () => Array.Empty<WorldEntitySnapshot>())));
        Assert.False(engine.Register(new Source("mod", () => Array.Empty<WorldEntitySnapshot>())));
        Assert.False(engine.Register(new Source("mod:spoof", () => Array.Empty<WorldEntitySnapshot>())));
    }
    [Fact] public void CornerWallsBlockDiagonalSightButTargetItselfCanBeObserved()
    {
        Assert.False(GridSight.Clear(new(0, 0), new(1, 1), (x, y) => x == 1 && y == 0));
        Assert.False(GridSight.Clear(new(0, 0), new(3, 0), (x, y) => x == 1));
        Assert.True(GridSight.Clear(new(0, 0), new(1, 0), (x, y) => x == 1));
        Assert.True(GridSight.Clear(new(3, 3), new(0, 1), (_, _) => false));
        Assert.False(GridSight.Clear(new(int.MaxValue, 0), new(int.MinValue, 0), (_, _) => false));
    }
    [Theory]
    [InlineData("test:absent")]
    [InlineData("Player")]
    [InlineData("Abigail")]
    [InlineData("Town")]
    public void ObserveHasItsOwnTargetAllowlist(string target)
    {
        var validator = StructuredResponseSemanticValidator.CreateLiveForLocations(new[] { "Town" }, new[] { "Abigail" }, observableTargets: new[] { "test:one" });
        var response = new NpcStructuredResponse { Speech = "look", Intent = Intent(target), SchemaVersion = "1" };
        Assert.False(validator.Validate(response).Valid);
        Assert.False(PerceptionEngine.Allows(Intent(target), Frame(Entity())));
    }
    [Fact] public void ObserveRejectsRemovedReplacedExpiredOrOtherObserverTargets()
    {
        var issued = Frame(Entity());
        Assert.True(PerceptionEngine.Observe(Intent(), issued, issued, Now).Success);
        Assert.False(PerceptionEngine.Observe(Intent(), issued, Frame(), Now).Success);
        Assert.False(PerceptionEngine.Observe(Intent(), issued, Frame(Entity("replacement")), Now).Success);
        Assert.False(PerceptionEngine.Observe(Intent(), issued, issued with { ObserverId = "Harvey" }, Now).Success);
        Assert.False(PerceptionEngine.Observe(Intent(), issued, issued with { Location = "Beach" }, Now).Success);
        Assert.False(PerceptionEngine.Observe(Intent(), issued, issued with { CapturedAt = Now.AddSeconds(91) }, Now.AddSeconds(91)).Success);
        Assert.False(PerceptionEngine.Observe(Intent(), issued, issued, Now, blocked: true).Success);
        Assert.False(PerceptionEngine.Observe(Intent(), issued, issued, Now.AddSeconds(3)).Success);
    }
    [Fact] public void ObserveReadsFreshStateAndRequiresCapability()
    {
        var issued = Frame(Entity()); var current = Frame(Entity() with { VisibleState = "resting" });
        Assert.Equal("resting", PerceptionEngine.Observe(Intent(), issued, current, Now).Entity!.State);
        Assert.False(PerceptionEngine.Observe(Intent(), issued, Frame(Entity() with { CanObserve = false }), Now).Success);
        var extra = new NpcIntent { IntentId = "extra", Type = PerceptionEngine.ObserveCapability,
            Target = "test:one", Priority = .5f, SchemaVersion = "1", Params = new() { ["command"] = "attack" } };
        Assert.False(PerceptionEngine.Allows(extra, issued));
    }
    [Fact] public void UnavailableAndEmptyAreExplicitAndDoNotImplyNoEntitiesExist()
    {
        Assert.Contains("unavailable", PerceptionEngine.Prompt(PerceptionFrame.Unavailable("Abigail")));
        Assert.Contains("Absence is not proof", PerceptionEngine.Prompt(Frame()));
    }
    [Fact] public void QuotedModNamesAndEnvironmentStayWithinTheContextCap()
    {
        string location = new('"', 200);
        var engine = new PerceptionEngine();
        engine.Register(new Source(new string('a', 80), () => Enumerable.Range(0, 100).Select(i => Entity(i.ToString()) with
        { Location = location, Name = new string('"', 500), VisibleState = new string('\\', 500), EvidenceSource = new string('"', 500) })));
        var frame = engine.Capture(Query with { Location = location }, (_, _) => true, Now, new string('"', 200));
        Assert.NotEmpty(frame.Entities); Assert.True(frame.Partial);
        Assert.InRange(PerceptionEngine.Prompt(frame).Length, 1, PerceptionEngine.ContextCharacters);
    }

    private static WorldEntitySnapshot Scenery(string id, string definition = "crop:parsnip", EntityKind kind = EntityKind.Terrain)
        => Entity(id) with { Kind = kind, Name = "parsnip crop", DefinitionId = definition, CanAggregate = true };

    [Fact] public void OrdinarySceneryProducesSampleCountsAndOneExactRepresentative()
    {
        var frame = Frame(Enumerable.Range(0, 30).Select(i => Scenery("crop" + i)).ToArray());
        Assert.Equal(30, Assert.Single(frame.Summaries).ObservedCount);
        Assert.Single(frame.Entities); Assert.True(frame.Partial);
        Assert.Contains("NOT map totals", PerceptionEngine.Prompt(frame));
        Assert.False(PerceptionEngine.Allows(Intent("crop:parsnip"), frame));
        Assert.True(PerceptionEngine.Allows(Intent(frame.Entities[0].Id), frame));
    }
    [Fact] public void CountsAreAfterVisibilityAndDuplicateFiltering()
    {
        var engine = new PerceptionEngine();
        engine.Register(new Source("test", () => new[] { Scenery("one"), Scenery("two"), Scenery("far") with { Tile = new(30, 10) },
            Scenery("wall") with { Tile = new(12, 10) }, Scenery("other") with { Location = "Beach" }, Scenery("dup"), Scenery("dup") }));
        var frame = engine.Capture(Query, (_, to) => to.X != 12, Now);
        Assert.Equal(2, Assert.Single(frame.Summaries).ObservedCount);
    }
    [Fact] public void SameLabelDifferentDefinitionsAndStatesNeverMerge()
    {
        var frame = Frame(Scenery("a1", "A"), Scenery("a2", "A"), Scenery("b1", "B"), Scenery("b2", "B"));
        Assert.Equal(2, frame.Summaries.Count); Assert.All(frame.Summaries, s => Assert.Equal(2, s.ObservedCount));
        var states = Frame(Scenery("a1"), Scenery("a2"), Scenery("b1") with { VisibleState = "withered" }, Scenery("b2") with { VisibleState = "withered" });
        Assert.Equal(2, states.Summaries.Count); Assert.Contains(states.Summaries, s => s.State == "withered");
    }
    [Fact] public void UnknownDefinitionsAndLivingIndividualsAreNeverAggregated()
    {
        var frame = Frame(Scenery("one") with { DefinitionId = null }, Scenery("two") with { DefinitionId = null },
            Scenery("cow1", "cow", EntityKind.Animal), Scenery("cow2", "cow", EntityKind.Animal));
        Assert.Empty(frame.Summaries); Assert.Equal(4, frame.Entities.Count);
        Assert.Equal(2, frame.Entities.Count(e => e.Kind == EntityKind.Animal));
    }
    [Fact] public void CategoryReservationsKeepAnimalsObjectsAndTerrainInACrowd()
    {
        var people = Enumerable.Range(0, 50).Select(i => Entity("person" + i, 10) with { Kind = EntityKind.Person });
        var frame = Frame(people.Concat(new[] { Entity("cow", 12) with { Kind = EntityKind.Animal },
            Scenery("chest", "item:chest", EntityKind.Object), Scenery("crop") }).ToArray());
        foreach (var kind in new[] { EntityKind.Person, EntityKind.Animal, EntityKind.Object, EntityKind.Terrain })
            Assert.Contains(frame.Entities, e => e.Kind == kind);
        Assert.InRange(PerceptionEngine.Prompt(frame).Length, 1, PerceptionEngine.ContextCharacters);
    }
    [Fact] public void CountChangesInvalidateCachedRepliesEvenWhenRepresentativeIsUnchanged()
    {
        var before = Frame(Scenery("a"), Scenery("b"));
        var after = Frame(Scenery("a"), Scenery("b"), Scenery("c"));
        Assert.Equal(before.Entities[0].Id, after.Entities[0].Id);
        Assert.NotEqual(PerceptionEngine.Signature(before), PerceptionEngine.Signature(after));
    }
    [Fact] public void SourcesRemainSeparateEvenWhenTheyUseTheSameDefinitionAndLabel()
    {
        var engine = new PerceptionEngine();
        engine.Register(new Source("modA", () => new[] { Scenery("a"), Scenery("b") }));
        engine.Register(new Source("modB", () => new[] { Scenery("a"), Scenery("b") }));
        var frame = engine.Capture(Query, (_, _) => true, Now);
        Assert.Equal(2, frame.Summaries.Count); Assert.All(frame.Summaries, s => Assert.Equal(2, s.ObservedCount));
    }
    [Fact] public void NearbyCellsAreDistanceOrderedBeforeTruncation()
    {
        var tiles = NearbyTiles.Around(new(10, 10)).ToArray();
        Assert.Equal(new TilePosition(10, 10), tiles[0]);
        Assert.Equal(tiles.OrderBy(p => p.DistanceSquared(new(10, 10))), tiles);
        Assert.All(tiles.Take(5), p => Assert.InRange(p.DistanceSquared(new(10, 10)), 0, 1));
    }
}
