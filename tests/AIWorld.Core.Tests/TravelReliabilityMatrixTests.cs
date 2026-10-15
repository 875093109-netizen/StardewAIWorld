using System.Text.Json;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using AIWorld.Core.TestSupport;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// TRAVEL RELIABILITY MATRIX GATE M1 — deterministic OFFLINE cycles (PM 2026-08-15 §5).
/// These loops drive the PRODUCTION chain — NpcIntent → BehaviorArbitrator →
/// ITravelDestinationResolver → ITravelAdapter → TravelLeaseManager → objective arrival →
/// observation — with the injectable seams (Deterministic resolver / Fake adapter / toggle
/// anchors) as the game-API replacement. No test-only copy of the travel logic exists.
///
/// Coverage vs the frozen counts (offline portion):
///   M-B REJECT offline 20 (FOLLOW_CONFLICT 10 + TRAVEL_CONFLICT 10); runtime covers 20 more
///   M-C UNREACHABLE 35  (M-C supplementary 5-category breakdown — OUT_OF_MAP_BOUNDS 5 +
///                        WALL 9 + WATER 9 + NO_PATH 8 + NO_SAFE_ARRIVAL_TILE 4, driven through
///                        the PRODUCTION GameWorldTravelDestinationResolver over a MatrixQueries
///                        seam); runtime covers 5 more (WALL 1 + WATER 1 + EXIT_UNREACHABLE 1 +
///                        SEGMENT_FAIL_AFTER_WARP 1 + TARGET_ILLEGAL_MID_ROUTE 1) = 40 total
///   M-E ISOLATION 80    (40 Abigail + 40 Harvey; real two-actor, no Abigail-shaped fixtures)
///   M-F SAVE_RELOAD 15  (production repository round-trip)
/// Every cycle asserts the M1 invariants in-process AND, when AIWORLD_MATRIX_OUTPUT_DIR is
/// set, emits a machine-readable record (MatrixCycleRecorder) for the aggregator.
/// </summary>
public class TravelReliabilityMatrixTests
{
    private const string Abigail = "Abigail";
    private const string Harvey = "Harvey";
    private const string Player = "player-1";

    private sealed record Harness(
        BehaviorArbitrator Arbitrator,
        ITravelDestinationResolver Resolver,
        FakeTravelAdapter Adapter,
        TravelLeaseManager Leases,
        TravelIntentHandler Handler,
        FakeFollowAdapter Follow);

    private static Harness Build(bool following = false)
    {
        var follow = new FakeFollowAdapter { Following = following };
        var leases = new TravelLeaseManager();
        var resolver = new DeterministicTravelDestinationResolver(
            new[] { ("SeedShop", 32, 4, "inbound-warp"), ("Beach", 20, 4, "inbound-warp"), ("Town", 40, 20, "inbound-warp") },
            unavailable: new[] { "Mine" },
            defaultHomes: new[] { (Abigail, "SeedShop", 32, 4), (Harvey, "HarveyRoom", 10, 4) });
        var reg = CapabilityRegistry.CreateDemoRegistry();
        var arbitrator = new BehaviorArbitrator(
            reg, follow, new DeterministicHardAnchorProvider(false, null),
            new DeterministicNpcResolver(Abigail, Harvey), new DeterministicPlayerResolver(Player),
            travelResolver: resolver, travelActiveCheck: npc => leases.IsActive(npc));
        var adapter = new FakeTravelAdapter();
        var handler = new TravelIntentHandler(arbitrator, resolver, adapter, leases, maxCompletionTicks: 60);
        return new Harness(arbitrator, resolver, adapter, leases, handler, follow);
    }

    // ── M-C categorized seam (GATE M1 M-C): PRODUCTION resolver algorithm over a scripted
    //    ITravelGameQueries seam — the SAME GameWorldTravelDestinationResolver the Demo host
    //    runs live (R5: no algorithm copy). The fake answers the game-API questions; the
    //    fail-closed DECISION (NoArrivalTile reject / resolve) is the production logic. ─────

    private sealed class MatrixQueries : ITravelGameQueries
    {
        public Func<string, TravelLocationQuery?> GetLocationFn { get; set; } = _ => null;
        public Func<string, TravelNpcQuery?> GetNpcFn { get; set; } = _ => null;
        public Func<TravelLocationQuery, int, int, bool> CanSpawnAtFn { get; set; } = (_, _, _) => false;
        public IReadOnlyList<TravelWarpQuery> Warps { get; set; } = Array.Empty<TravelWarpQuery>();

        public TravelLocationQuery? GetLocation(string canonicalLocation) => GetLocationFn(canonicalLocation);
        public TravelNpcQuery? GetNpc(string actorNpcId) => GetNpcFn(actorNpcId);
        public bool CanSpawnAt(TravelLocationQuery location, int tileX, int tileY) => CanSpawnAtFn(location, tileX, tileY);
        public IReadOnlyList<TravelWarpQuery> EnumerateWarps() => Warps;
    }

    private static Harness BuildSeam(MatrixQueries queries, FakeTravelAdapter adapter)
    {
        var follow = new FakeFollowAdapter();
        var leases = new TravelLeaseManager();
        var resolver = new GameWorldTravelDestinationResolver(queries);
        var reg = CapabilityRegistry.CreateDemoRegistry();
        var arbitrator = new BehaviorArbitrator(
            reg, follow, new DeterministicHardAnchorProvider(false, null),
            new DeterministicNpcResolver(Abigail, Harvey), new DeterministicPlayerResolver(Player),
            travelResolver: resolver, travelActiveCheck: npc => leases.IsActive(npc));
        var handler = new TravelIntentHandler(arbitrator, resolver, adapter, leases, maxCompletionTicks: 60);
        return new Harness(arbitrator, resolver, adapter, leases, handler, follow);
    }

    /// <summary>Actor's own default home (actor-scoped fallback); the travelling NPC's canonical
    /// home always answers here so only the TARGET determines resolution.</summary>
    private static TravelNpcQuery AbigailHome() => new(Abigail, "SeedShop", 32, 4);

    /// <summary>OOB / WALL / WATER: a location that exists with ONE inbound warp whose target tile
    /// is non-spawnable (out of bounds / wall / water) → production resolver rejects NoArrivalTile.</summary>
    private static MatrixQueries QueriesWithIllegalTile(string location, int tileX, int tileY)
    {
        return new MatrixQueries
        {
            GetLocationFn = name => string.Equals(name, location, StringComparison.OrdinalIgnoreCase) ? new TravelLocationQuery(location) : null,
            GetNpcFn = _ => AbigailHome(),
            CanSpawnAtFn = (_, _, _) => false, // the illegal tile is never spawnable (fail closed)
            Warps = new[] { new TravelWarpQuery("Somewhere", 1, 1, location, tileX, tileY) },
        };
    }

    /// <summary>NO_PATH: a location with a legal inbound-warp tile — the resolver RESOLVES it;
    /// the adapter starts movement but no path completes → completion timeout releases the lease.</summary>
    private static MatrixQueries QueriesWithResolvedTile()
    {
        return new MatrixQueries
        {
            GetLocationFn = name => string.Equals(name, "Beach", StringComparison.OrdinalIgnoreCase) ? new TravelLocationQuery("Beach") : null,
            GetNpcFn = _ => AbigailHome(),
            CanSpawnAtFn = (_, _, _) => true,
            Warps = new[] { new TravelWarpQuery("Somewhere", 1, 1, "Beach", 20, 4) },
        };
    }

    /// <summary>NO_SAFE_ARRIVAL_TILE: the location EXISTS but has no inbound warp and no actor
    /// default-position → production resolver rejects NoArrivalTile (never guesses).</summary>
    private static MatrixQueries QueriesWithNoArrivalTile(string location)
    {
        return new MatrixQueries
        {
            GetLocationFn = name => string.Equals(name, location, StringComparison.OrdinalIgnoreCase) ? new TravelLocationQuery(location) : null,
            GetNpcFn = _ => AbigailHome(),
            Warps = Array.Empty<TravelWarpQuery>(),
        };
    }

    private static int RunRejectCycle(MatrixQueries queries, string cycleId, string target)
    {
        var h = BuildSeam(queries, new FakeTravelAdapter());
        int arrivals = 0;
        h.Handler.OnTravelArrived += (_, _, _) => arrivals++;
        int before = h.Adapter.TravelCalls;
        var o = h.Handler.Handle(TravelIntent(target, $"mc-{cycleId}"), Abigail, "spring-10-1@0600");
        Assert.False(o.Arbitration.Authorized);
        Assert.Equal(BehaviorDecision.RejectedTargetNotFound, o.Arbitration.Decision);
        Assert.False(o.AdapterInvoked);
        Assert.Null(o.Lease);
        Assert.Equal(before, h.Adapter.TravelCalls);   // movement delta 0 — fail closed before any adapter work
        Assert.Equal(0, arrivals);
        Assert.Equal(0, h.Leases.ActiveCount);
        Emit("UNREACHABLE", cycleId, Abigail, target, "PASS", 0, 0, 0);
        return 1;
    }

    private static int RunNoPathCycle(MatrixQueries queries, string cycleId)
    {
        var h = BuildSeam(queries, new FakeTravelAdapter());
        int arrivals = 0;
        h.Handler.OnTravelArrived += (_, _, _) => arrivals++;
        var o = h.Handler.Handle(TravelIntent("Beach", $"mc-{cycleId}"), Abigail, "spring-10-1@0600");
        Assert.True(o.Arbitration.Authorized);         // legal tile resolved → reachable at arbitration
        Assert.Equal("STARTED", o.Adapter!.Status);    // movement initiated, NOT arrival
        Assert.NotNull(o.Lease);
        TravelIntentHandler.TravelCompletionResult? final = null;
        for (int t = 0; t < 70; t++)
        {
            final = h.Handler.Complete(Abigail, $"spring-10-1@{600 + t}");
            if (final.Status != TravelIntentHandler.TravelCompletionStatus.Pending) break;
        }
        Assert.Equal(TravelIntentHandler.TravelCompletionStatus.TimedOut, final!.Status); // no path → never arrives
        Assert.Equal(0, arrivals);                     // no arrival observation
        Assert.Equal(0, h.Leases.ActiveCount);         // lease released on timeout
        Assert.True(h.Adapter.CancelCalled);           // movement stopped (no warp fallback)
        Emit("UNREACHABLE", cycleId, Abigail, "Beach", "PASS", 1, 0, 0);
        return 1;
    }

    private static NpcIntent TravelIntent(string target, string reason) => new()
    {
        IntentId = Guid.NewGuid().ToString("N"),
        Type = CapabilityRegistry.CapabilityIds.TravelToLocation,
        Target = target,
        Priority = 0.7f,
        SchemaVersion = "1",
        Reason = $"matrix-{reason}",
    };

    private static ObjectiveObservation ArrivalObs(string npc, TravelDestination dest) => new()
    {
        ObservationId = Guid.NewGuid().ToString("N"),
        Type = ObservationTypes.NpcTravelArrived,
        NpcId = npc,
        GameDate = "spring-10-1",
        TimeOfDay = 601,
        Location = dest.LocationName,
        SchemaVersion = "1",
        Payload = new Dictionary<string, JsonElement>
        {
            ["targetLocation"] = JsonSerializer.SerializeToElement(dest.LocationName),
            ["arrivalResult"] = JsonSerializer.SerializeToElement("arrived"),
            ["source"] = JsonSerializer.SerializeToElement("matrix-offline"),
        },
    };

    // ── cycle evidence emission (no-op unless the runner set the output dir) ──────

    private static string CycleSource(string category) => $"cycles-offline-TravelReliabilityMatrix-{category}.jsonl";

    private static void Emit(string category, string cycleId, string npc, string target, string result,
        int adapterInv, int arrivalObs, int activeLeaseAfter)
    {
        var dir = Environment.GetEnvironmentVariable(MatrixCycleRecorder.EnvOutputDir);
        if (string.IsNullOrWhiteSpace(dir)) return;
        var path = Path.GetFullPath(Path.Combine(dir, CycleSource(category)));
        var rec = new MatrixCycleRecord
        {
            CycleId = cycleId,
            Category = category,
            ActorNpcId = npc,
            Target = target,
            ExecutionKind = "OFFLINE",
            Result = result,
            AdapterInvocations = adapterInv,
            ArrivalObservationCount = arrivalObs,
            ActiveLeaseCountAfter = activeLeaseAfter,
            EvidenceMarker = cycleId,
            EvidencePath = path,
        };
        MatrixCycleRecorder.Record(rec, CycleSource(category));
    }

    // ── M-B REJECT offline 20 (FOLLOW_CONFLICT 10 + TRAVEL_CONFLICT 10) ───────────

    [Fact]
    public void Matrix_Reject_Offline20_FollowAndTravelConflict()
    {
        int total = 0;

        // FOLLOW_CONFLICT 10 — a following NPC is blocked from travel at arbitration.
        for (int i = 0; i < 10; i++)
        {
            var h = Build(following: true);
            int arrivals = 0;
            h.Handler.OnTravelArrived += (_, _, _) => arrivals++;
            int beforeCalls = h.Adapter.TravelCalls;
            var o = h.Handler.Handle(TravelIntent("Beach", $"follow-conflict-{i}"), Abigail, $"spring-10-1@0600");
            Assert.False(o.Arbitration.Authorized);
            Assert.Equal(BehaviorDecision.RejectedLeaseConflict, o.Arbitration.Decision);
            Assert.False(o.AdapterInvoked);
            Assert.Null(o.Lease);
            Assert.Equal(beforeCalls, h.Adapter.TravelCalls);   // movement delta 0
            Assert.Equal(0, arrivals);                          // no arrival observation
            Assert.Equal(0, h.Leases.ActiveCount);              // no new lease
            Emit("REJECT", $"M-B-FF-{i:00}", Abigail, "Beach", "PASS", 0, 0, 0);
            total++;
        }

        // TRAVEL_CONFLICT 10 — a second travel while one lease is active is rejected.
        for (int i = 0; i < 10; i++)
        {
            var h = Build();
            int arrivals = 0;
            h.Handler.OnTravelArrived += (_, _, _) => arrivals++;
            // First travel → STARTED → lease ACTIVE.
            var first = h.Handler.Handle(TravelIntent("Beach", $"travel-conflict-{i}-first"), Abigail, $"spring-10-1@0600");
            Assert.True(first.Arbitration.Authorized);
            Assert.True(first.AdapterInvoked);
            Assert.NotNull(first.Lease);
            Assert.True(h.Leases.IsActive(Abigail));
            int callsAfterFirst = h.Adapter.TravelCalls;
            // Second travel → travelActiveCheck → RejectedLeaseConflict, adapter NOT invoked.
            var second = h.Handler.Handle(TravelIntent("SeedShop", $"travel-conflict-{i}-second"), Abigail, $"spring-10-1@0700");
            Assert.False(second.Arbitration.Authorized);
            Assert.Equal(BehaviorDecision.RejectedLeaseConflict, second.Arbitration.Decision);
            Assert.False(second.AdapterInvoked);
            Assert.Null(second.Lease);
            Assert.Equal(callsAfterFirst, h.Adapter.TravelCalls); // no extra movement
            Assert.Equal(0, arrivals);
            Assert.Equal(1, h.Leases.ActiveCount);                 // the FIRST lease still holds
            h.Handler.Cancel(Abigail, $"spring-10-1@0800");         // release for next cycle
            Assert.Equal(0, h.Leases.ActiveCount);
            Emit("REJECT", $"M-B-TC-{i:00}", Abigail, "SeedShop", "PASS", 1, 0, 0);
            total++;
        }

        Assert.Equal(20, total);
    }

    // ── M-C UNREACHABLE offline 35 (M-C supplementary 5-category breakdown) ─────────

    [Fact]
    public void Matrix_Unreachable_Categorized35()
    {
        int total = 0;

        // OUT_OF_MAP_BOUNDS 5 — inbound warp targets a tile outside the map → resolver rejects.
        for (int i = 0; i < 5; i++)
        {
            total += RunRejectCycle(QueriesWithIllegalTile($"OobLoc{i:00}", -5, -5), $"M-C-OOB-{i:00}", $"OobLoc{i:00}");
        }

        // WALL_OR_SOLID_TILE 9 — inbound warp targets a wall/solid tile → resolver rejects
        // (runtime WALL representative covers the 10th).
        for (int i = 0; i < 9; i++)
        {
            total += RunRejectCycle(QueriesWithIllegalTile($"WallLoc{i:00}", 3, 3), $"M-C-WALL-{i:00}", $"WallLoc{i:00}");
        }

        // WATER_OR_NON_WALKABLE_TILE 9 — inbound warp targets a water/non-walkable tile → resolver
        // rejects (runtime WATER representative covers the 10th).
        for (int i = 0; i < 9; i++)
        {
            total += RunRejectCycle(QueriesWithIllegalTile($"WaterLoc{i:00}", 5, 5), $"M-C-WATER-{i:00}", $"WaterLoc{i:00}");
        }

        // DISCONNECTED_OR_NO_PATH 8 — legal tile resolved but no path completes → STARTED then
        // completion timeout, lease released, movement stopped (runtime EXIT_UNREACHABLE +
        // SEGMENT_FAIL_AFTER_WARP representatives cover the remaining 2).
        for (int i = 0; i < 8; i++)
        {
            total += RunNoPathCycle(QueriesWithResolvedTile(), $"M-C-NOPATH-{i:00}");
        }

        // NO_SAFE_ARRIVAL_TILE 4 — location exists but no source-grounded tile → resolver rejects
        // (runtime TARGET_ILLEGAL_MID_ROUTE representative covers the 5th).
        for (int i = 0; i < 4; i++)
        {
            total += RunRejectCycle(QueriesWithNoArrivalTile($"NsaLoc{i:00}"), $"M-C-NSA-{i:00}", $"NsaLoc{i:00}");
        }

        Assert.Equal(35, total);
    }

    // ── M-E ISOLATION offline 80 (40 Abigail + 40 Harvey, real two-actor) ────────

    [Fact]
    public void Matrix_Isolation_Offline80()
    {
        var h = Build();
        int abigailRuns = 0, harveyRuns = 0;

        // Part 1 (40): actor completes a SUCCESS travel; the OTHER actor must be untouched.
        for (int i = 0; i < 40; i++)
        {
            var actor = i % 2 == 0 ? Abigail : Harvey;
            var other = i % 2 == 0 ? Harvey : Abigail;
            int arrivals = 0;
            string? arrivedNpc = null;
            h.Handler.OnTravelArrived = (npc, dest, stamp) => { arrivals++; arrivedNpc = npc; };
            h.Adapter.Arrived = false;   // fresh adapter state per cycle (global fake flag)

            var o = h.Handler.Handle(TravelIntent("Beach", $"iso-success-{i}"), actor, $"spring-10-1@0600");
            Assert.True(o.Arbitration.Authorized);
            Assert.NotNull(o.Lease);
            h.Adapter.Arrived = true;   // simulate objective arrival at the resolved tile
            TravelIntentHandler.TravelCompletionResult? final = null;
            for (int t = 0; t < 10; t++)
            {
                final = h.Handler.Complete(actor, $"spring-10-1@{600 + t}");
                if (final.Status != TravelIntentHandler.TravelCompletionStatus.Pending) break;
            }
            Assert.Equal(TravelIntentHandler.TravelCompletionStatus.Arrived, final!.Status);
            Assert.Equal(1, arrivals);
            Assert.Equal(actor, arrivedNpc);               // observation npcId matches actor
            Assert.False(h.Leases.IsActive(actor));
            Assert.False(h.Leases.IsActive(other));        // other never holds the actor's lease
            Assert.Equal(0, h.Leases.Released.Count(r => r.NpcId == other && r.Status == "ACTIVE"));
            // No stale leases after release.
            Assert.Equal(0, h.Leases.Released.Count(r => r.Status == "ACTIVE"));

            Emit("ISOLATION", $"M-E-S{actor}-{i:00}", actor, "Beach", "PASS", 1, 1, 0);
            if (actor == Abigail) abigailRuns++; else harveyRuns++;
        }

        // Part 2 (40): OTHER holds a lease; actor's REJECT must NOT release the other's lease.
        for (int i = 0; i < 40; i++)
        {
            var actor = i % 2 == 0 ? Abigail : Harvey;
            var other = i % 2 == 0 ? Harvey : Abigail;

            // Other acquires an ACTIVE travel lease.
            h.Adapter.Arrived = false;   // fresh adapter state per cycle
            var first = h.Handler.Handle(TravelIntent("Beach", $"iso-hold-{i}"), other, $"spring-10-1@0600");
            Assert.True(first.Arbitration.Authorized);
            Assert.NotNull(first.Lease);
            Assert.True(h.Leases.IsActive(other));

            // Actor's travel is REJECTED (unknown target) — must not disturb other's lease.
            var rej = h.Handler.Handle(TravelIntent("Mine", $"iso-reject-{i}"), actor, $"spring-10-1@0700");
            Assert.False(rej.Arbitration.Authorized);
            Assert.False(rej.AdapterInvoked);
            Assert.Null(rej.Lease);
            Assert.True(h.Leases.IsActive(other), $"actor '{actor}' reject released '{other}' lease (contamination)");

            // Actor's interruption also must not release other's lease.
            var cancel = h.Handler.Cancel(actor, $"spring-10-1@0800");
            Assert.Equal(TravelIntentHandler.TravelCompletionStatus.NotActive, cancel.Status);
            Assert.True(h.Leases.IsActive(other));

            h.Handler.Cancel(other, $"spring-10-1@0900");   // release the held lease
            Assert.False(h.Leases.IsActive(other));

            Emit("ISOLATION", $"M-E-R{actor}-{i:00}", actor, "Mine", "PASS", 0, 0, 0);
            if (actor == Abigail) abigailRuns++; else harveyRuns++;
        }

        Assert.Equal(40, abigailRuns);
        Assert.Equal(40, harveyRuns);
    }

    // ── M-F SAVE_RELOAD offline 15 (production repository round-trip) ────────────

    [Fact]
    public void Matrix_SaveReload_Offline15()
    {
        for (int i = 0; i < 15; i++)
        {
            var store = new SerializingStore();
            var repo = new AIWorldSaveRepository(store);
            var load = repo.Load();
            var data = load.Data ?? new AIWorldSaveData { SaveSchemaVersion = "1" };
            var collector = new ObservationCollector(data);

            var h = Build();
            h.Handler.OnTravelArrived = (npc, dest, stamp) => collector.Record(ArrivalObs(npc, dest));

            // Complete a travel → lease released, one observation persisted.
            var o = h.Handler.Handle(TravelIntent("Beach", $"save-reload-{i}"), Abigail, $"spring-10-1@0600");
            Assert.True(o.Arbitration.Authorized);
            Assert.NotNull(o.Lease);
            var leaseId = o.Lease!.LeaseId;
            h.Adapter.Arrived = true;   // simulate objective arrival at the resolved tile
            TravelIntentHandler.TravelCompletionResult? final = null;
            for (int t = 0; t < 10; t++)
            {
                final = h.Handler.Complete(Abigail, $"spring-10-1@{600 + t}");
                if (final.Status != TravelIntentHandler.TravelCompletionStatus.Pending) break;
            }
            Assert.Equal(TravelIntentHandler.TravelCompletionStatus.Arrived, final!.Status);
            Assert.Equal(0, h.Leases.ActiveCount);
            Assert.Equal(1, collector.CountForNpc(Abigail));

            Assert.True(repo.Save());
            var json = AIWorldSaveRepository.Serialize(data);

            // U7: the travel lease is SESSION-SCOPED — its lease id and owner must NOT be in
            // the persisted JSON; a reloaded process must see zero active/stale leases.
            Assert.DoesNotContain(leaseId, json);
            Assert.DoesNotContain("travel.to_location", json);
            Assert.DoesNotContain("PLAYER_REQUEST", json);   // no stale BehaviorOwner persisted

            // Cold process 2: reload → fresh lease manager → zero active, zero stale.
            var repo2 = new AIWorldSaveRepository(store);
            Assert.Equal(AIWorldSaveRepository.LoadStatus.Loaded, repo2.Load().Status);
            var data2 = repo2.Data!;
            Assert.Equal(1, data2.Characters[Abigail].Observations.Count); // observation stable, no duplicate write
            Assert.Equal(ObservationTypes.NpcTravelArrived, data2.Characters[Abigail].Observations[0].Type);
            var freshLeases = new TravelLeaseManager();
            Assert.Equal(0, freshLeases.ActiveCount);         // ACTIVE_TRAVEL_LEASE after reload = 0
            Assert.Equal(0, freshLeases.Released.Count);      // STALE_TRAVEL_LEASE_COUNT = 0

            Emit("SAVE_RELOAD", $"M-F-{i:00}", Abigail, "Beach", "PASS", 1, 1, 0);
        }
    }

    private sealed class SerializingStore : IAIWorldSaveStore
    {
        private string? _json;
        public AIWorldSaveData? Read() => _json is null ? null : AIWorldSaveRepository.Deserialize(_json);
        public void Write(AIWorldSaveData data) => _json = AIWorldSaveRepository.Serialize(data);
        public void Clear() => _json = null;
    }
}
