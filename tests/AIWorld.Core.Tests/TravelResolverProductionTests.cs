using AIWorld.Core.Behaviors;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// GATE R1 FINAL CLOSURE R5 — non-Abigail production resolver test chain. These tests
/// drive GameWorldTravelDestinationResolver — the SAME implementation the live
/// StardewTravelDestinationResolver delegates to — through the injectable
/// ITravelGameQueries seam. No algorithm copy, no mirror closure, no game dependency.
/// </summary>
public class TravelResolverProductionTests
{
    private sealed class FakeGameQueries : ITravelGameQueries
    {
        private readonly Dictionary<string, TravelLocationQuery> _locations = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TravelNpcQuery> _npcs = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<TravelWarpQuery> _warps = new();
        private readonly HashSet<(string, int, int)> _spawnable = new();

        public FakeGameQueries AddLocation(string name)
        {
            _locations[name] = new TravelLocationQuery(name);
            return this;
        }

        public FakeGameQueries AddNpc(string name, string defaultMap, int tileX, int tileY)
        {
            _npcs[name] = new TravelNpcQuery(name, defaultMap, tileX, tileY);
            return this;
        }

        public FakeGameQueries AddWarp(string source, int x, int y, string target, int tx, int ty)
        {
            _warps.Add(new TravelWarpQuery(source, x, y, target, tx, ty));
            return this;
        }

        public FakeGameQueries MakeSpawnable(string location, int tileX, int tileY)
        {
            _spawnable.Add((location, tileX, tileY));
            return this;
        }

        public TravelLocationQuery? GetLocation(string canonicalLocation)
            => _locations.TryGetValue(canonicalLocation, out var l) ? l : null;

        public TravelNpcQuery? GetNpc(string actorNpcId)
            => _npcs.TryGetValue(actorNpcId, out var n) ? n : null;

        public bool CanSpawnAt(TravelLocationQuery location, int tileX, int tileY)
            => _spawnable.Contains((location.Name, tileX, tileY));

        public IReadOnlyList<TravelWarpQuery> EnumerateWarps() => _warps;
    }

    private static GameWorldTravelDestinationResolver Resolver(FakeGameQueries q) => new(q);

    // ── R5.1: Harvey fallback uses Harvey DefaultMap/DefaultPosition ─────────────
    [Fact]
    public void HarveyFallback_UsesHarveyDefaultMapAndPosition()
    {
        var q = new FakeGameQueries()
            .AddLocation("HarveyRoom")
            .AddNpc("Harvey", "HarveyRoom", 4, 6)
            .MakeSpawnable("HarveyRoom", 4, 6);
        var r = Resolver(q).Resolve("Harvey", "HarveyRoom");

        Assert.Equal(TravelDestinationStatus.Resolved, r.Status);
        Assert.NotNull(r.Destination);
        Assert.Equal("HarveyRoom", r.Destination!.LocationName);
        Assert.Equal(4, r.Destination.TileX);
        Assert.Equal(6, r.Destination.TileY);
        Assert.Equal("default-position", r.Destination.EvidenceSource);
    }

    // ── R5.2: Sebastian cannot consume Harvey fallback ───────────────────────────
    [Fact]
    public void Sebastian_CannotConsumeHarveyFallback()
    {
        var q = new FakeGameQueries()
            .AddLocation("HarveyRoom")
            .AddNpc("Harvey", "HarveyRoom", 4, 6)
            .AddNpc("Sebastian", "SebastianRoom", 1, 1)
            .MakeSpawnable("HarveyRoom", 4, 6);
        var r = Resolver(q).Resolve("Sebastian", "HarveyRoom");

        // No inbound warp to HarveyRoom and Sebastian's OWN default map is SebastianRoom —
        // Harvey's home must NOT resolve Sebastian's travel.
        Assert.Equal(TravelDestinationStatus.NoArrivalTile, r.Status);
        Assert.Null(r.Destination);
    }

    // ── R5.3: mod NPC fallback uses that mod NPC ─────────────────────────────────
    [Fact]
    public void ModNpcFallback_UsesThatModNpc()
    {
        var q = new FakeGameQueries()
            .AddLocation("CustomLocation")
            .AddNpc("ModNpc", "CustomLocation", 9, 3)
            .MakeSpawnable("CustomLocation", 9, 3);
        var r = Resolver(q).Resolve("ModNpc", "CustomLocation");

        Assert.Equal(TravelDestinationStatus.Resolved, r.Status);
        Assert.NotNull(r.Destination);
        Assert.Equal("default-position", r.Destination!.EvidenceSource);
        Assert.Equal(9, r.Destination.TileX);
        Assert.Equal(3, r.Destination.TileY);
    }

    // ── R5.4: unknown NPC fails closed ───────────────────────────────────────────
    [Fact]
    public void UnknownNpc_FailsClosed()
    {
        var q = new FakeGameQueries()
            .AddLocation("HarveyRoom")
            .AddNpc("Harvey", "HarveyRoom", 4, 6)
            .MakeSpawnable("HarveyRoom", 4, 6);
        var r = Resolver(q).Resolve("UnknownNpc", "HarveyRoom");

        Assert.Equal(TravelDestinationStatus.NoArrivalTile, r.Status);
        Assert.Null(r.Destination);
    }

    // ── R5.5: inbound warp remains actor-independent ─────────────────────────────
    [Fact]
    public void InboundWarp_IsActorIndependent()
    {
        var q = new FakeGameQueries()
            .AddLocation("Town")
            .AddWarp("BusStop", 10, 20, "Town", 5, 6)
            .MakeSpawnable("Town", 5, 6);
        // Unknown actor still resolves via the inbound warp — the warp is NOT actor-scoped.
        var r = Resolver(q).Resolve("UnknownNpc", "Town");

        Assert.Equal(TravelDestinationStatus.Resolved, r.Status);
        Assert.NotNull(r.Destination);
        Assert.Equal("inbound-warp", r.Destination!.EvidenceSource);
        Assert.Equal(5, r.Destination.TileX);
        Assert.Equal(6, r.Destination.TileY);
    }

    // ── R5.6: default-position still requires the ACTOR's own home (negative control) ──
    [Fact]
    public void DefaultPosition_RequiresActorOwnHome()
    {
        var q = new FakeGameQueries()
            .AddLocation("Town")
            .AddNpc("Harvey", "HarveyRoom", 4, 6)
            .MakeSpawnable("Town", 4, 6);
        // Harvey's home is HarveyRoom; resolving "Town" for Harvey must NOT use his home tile.
        var r = Resolver(q).Resolve("Harvey", "Town");

        Assert.Equal(TravelDestinationStatus.NoArrivalTile, r.Status);
    }
}
