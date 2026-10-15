using System.Text.Json;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// Travel Gate T1 — NPC travel arrival observation tests.
/// PM §9: NPC arrival must be a REAL objective observation (runtime-verified);
/// intent acceptance / LOCATION_VISITED can never substitute for it.
/// </summary>
public class TravelObservationTests
{
    private static ObjectiveObservation NpcTravelObservation(string? target = "Beach", string? result = "arrived", string? source = "runtime-arrival-predicate")
        => new()
        {
            ObservationId = Guid.NewGuid().ToString("N"),
            Type = ObservationTypes.NpcTravelArrived,
            NpcId = "Abigail",
            GameDate = "spring-10-1",
            TimeOfDay = 601,
            Location = "Beach",
            SchemaVersion = "1",
            Payload = new Dictionary<string, JsonElement>
            {
                ["targetLocation"] = JsonSerializer.SerializeToElement(target ?? ""),
                ["arrivalResult"] = JsonSerializer.SerializeToElement(result ?? ""),
                ["source"] = JsonSerializer.SerializeToElement(source ?? ""),
            },
        };

    // Whitelist extension accepts NPC_TRAVEL_ARRIVED with valid payload.
    [Fact]
    public void NpcTravelArrived_ValidPayload_Accepted()
    {
        var data = new AIWorldSaveData { SaveSchemaVersion = "1" };
        var collector = new ObservationCollector(data);
        var r = collector.Record(NpcTravelObservation());
        Assert.True(r.Accepted);
        Assert.Equal(1, collector.CountForNpc("Abigail"));
        Assert.Equal(ObservationTypes.NpcTravelArrived, r.Observation!.Type);
    }

    // Missing required payload fields → rejected (payload schema per type).
    [Fact]
    public void NpcTravelArrived_MissingPayload_Rejected()
    {
        var data = new AIWorldSaveData { SaveSchemaVersion = "1" };
        var collector = new ObservationCollector(data);
        var bad = NpcTravelObservation();
        bad = new ObjectiveObservation
        {
            ObservationId = bad.ObservationId,
            Type = bad.Type,
            NpcId = bad.NpcId,
            GameDate = bad.GameDate,
            TimeOfDay = bad.TimeOfDay,
            Location = bad.Location,
            SchemaVersion = bad.SchemaVersion,
            Payload = new Dictionary<string, JsonElement> { ["targetLocation"] = JsonSerializer.SerializeToElement("Beach") },
        };
        var r = collector.Record(bad);
        Assert.False(r.Accepted);
        Assert.Contains("payload requires", r.Error);
    }

    // Unregistered observation type still rejected (whitelist integrity).
    [Fact]
    public void UnknownType_StillRejected()
    {
        var data = new AIWorldSaveData { SaveSchemaVersion = "1" };
        var collector = new ObservationCollector(data);
        var o = NpcTravelObservation();
        o = new ObjectiveObservation
        {
            ObservationId = o.ObservationId,
            Type = "NPC_TELEPORTED",
            NpcId = o.NpcId,
            GameDate = o.GameDate,
            TimeOfDay = o.TimeOfDay,
            Location = o.Location,
            SchemaVersion = o.SchemaVersion,
            Payload = o.Payload,
        };
        var r = collector.Record(o);
        Assert.False(r.Accepted);
        Assert.Contains("not in frozen", r.Error);
    }

    // PM §9: intent acceptance ≠ arrival observation. The handler only fires the hook on
    // OBJECTIVE_ARRIVAL; a rejected travel (unavailable) must not create an observation.
    [Fact]
    public void RejectedTravel_NeverCreatesObservation()
    {
        var reg = CapabilityRegistry.CreateDemoRegistry();
        var resolver = new DeterministicTravelDestinationResolver(
            new[] { ("Beach", 20, 4, "inbound-warp") }, unavailable: new[] { "Mine" });
        var arbitrator = new BehaviorArbitrator(reg, new FakeFollowAdapter(),
            new DeterministicHardAnchorProvider(false, null),
            new DeterministicNpcResolver("Abigail"), new DeterministicPlayerResolver("p"),
            travelResolver: resolver);
        var adapter = new FakeTravelAdapter();
        var handler = new TravelIntentHandler(arbitrator, resolver, adapter, new TravelLeaseManager());
        int observations = 0;
        handler.OnTravelArrived += (npc, dest, stamp) => observations++;

        // Unavailable target → rejected at arbitration; no adapter, no arrival hook.
        var outcome = handler.Handle(new NpcIntent
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Type = CapabilityRegistry.CapabilityIds.TravelToLocation,
            Target = "Mine",
            Priority = 0.7f,
            SchemaVersion = "1",
        }, "Abigail", "spring-10-1@0600");
        Assert.False(outcome.Arbitration.Authorized);
        Assert.Equal(0, observations);

        // Adapter failure → STARTED never returned → no lease, no observation.
        adapter.TravelShouldFail = true;
        var fail = handler.Handle(new NpcIntent
        {
            IntentId = Guid.NewGuid().ToString("N"),
            Type = CapabilityRegistry.CapabilityIds.TravelToLocation,
            Target = "Beach",
            Priority = 0.7f,
            SchemaVersion = "1",
        }, "Abigail", "spring-10-1@0600");
        Assert.True(fail.Arbitration.Authorized);
        Assert.Equal("FAILED", fail.Adapter!.Status);
        Assert.Equal(0, observations);
    }

    // Persistence: observations persist via the existing save repository (JSON round-trip).
    [Fact]
    public void NpcTravelArrived_PersistsViaRepository()
    {
        var store = new SerializingStore();
        var repo = new AIWorldSaveRepository(store);
        var load = repo.Load();
        var data = load.Data ?? new AIWorldSaveData { SaveSchemaVersion = "1" };
        var collector = new ObservationCollector(data);
        Assert.True(collector.Record(NpcTravelObservation()).Accepted);

        Assert.True(repo.Save());
        var repo2 = new AIWorldSaveRepository(store);
        Assert.Equal(AIWorldSaveRepository.LoadStatus.Loaded, repo2.Load().Status);
        var obs = repo2.Data!.Characters["Abigail"].Observations;
        Assert.Single(obs);
        Assert.Equal(ObservationTypes.NpcTravelArrived, obs[0].Type);
        Assert.Equal("Beach", obs[0].Payload!["targetLocation"].GetString());
    }

    private sealed class SerializingStore : IAIWorldSaveStore
    {
        private string? _json;
        public AIWorldSaveData? Read() => _json is null ? null : AIWorldSaveRepository.Deserialize(_json);
        public void Write(AIWorldSaveData data) => _json = AIWorldSaveRepository.Serialize(data);
        public void Clear() => _json = null;
    }
}
