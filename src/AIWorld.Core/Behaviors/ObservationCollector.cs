using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// ObservationCollector (contract §2.4, A5 producer side) — deterministic service that
/// owns the objective-observation rules. Core has no game dependency, so the collector
/// receives raw observation FACTS (from a Demo-side SMAPI source: Warped/HP polling/
/// monster polling) and enforces:
///   - type whitelist (frozen §2.4)
///   - required fields + per-type payload validation
///   - per-NPC FIFO cap (contract §2.4: 200)
///   - immutability (observations are never mutated after creation; only FIFO eviction of the oldest)
///   - session linkage (optional sessionId; FollowSessionManager additionally enforces
///     "only during active session, never after close" via AppendObservation)
///
/// Phase 5: observations are OBJECTIVE FACTS ONLY — never interpretation
/// ("Abigail became scared" belongs to MemoryCandidate, not here).
/// </summary>
public sealed class ObservationCollector
{
    private readonly AIWorldSaveData _data;
    private readonly int _maxPerNpc;
    private int _sequence;

    public ObservationCollector(AIWorldSaveData data, int maxPerNpc = 200)
    {
        _data = data;
        _maxPerNpc = maxPerNpc;
    }

    public sealed class ObservationResult
    {
        public required bool Accepted { get; init; }
        public ObjectiveObservation? Observation { get; init; }
        public string? Error { get; init; }
    }

    public int TotalCount() => _data.Characters.Values.Sum(n => n.Observations.Count);

    public int CountForNpc(string npcKey) =>
        _data.Characters.TryGetValue(npcKey, out var npc) ? npc.Observations.Count : 0;

    public IReadOnlyList<ObjectiveObservation> GetForNpc(string npcKey)
    {
        if (!_data.Characters.TryGetValue(npcKey, out var npc)) return Array.Empty<ObjectiveObservation>();
        return npc.Observations;
    }

    public ObjectiveObservation? GetById(string observationId)
    {
        foreach (var npc in _data.Characters.Values)
        {
            var hit = npc.Observations.FirstOrDefault(o => o.ObservationId == observationId);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>
    /// Record one objective observation. Returns accepted record or rejection reason.
    /// Never mutates an existing observation (immutability per §2.4).
    /// </summary>
    public ObservationResult Record(ObjectiveObservation observation)
    {
        var err = Validate(observation);
        if (err is not null)
            return new ObservationResult { Accepted = false, Error = err };

        var npc = GetOrCreateNpc(observation.NpcId);
        npc.Observations.Add(observation);

        // Per-NPC FIFO cap (contract §2.4: 200; oldest evicted — observations are immutable,
        // so eviction is the only allowed removal and it drops the OLDEST).
        while (npc.Observations.Count > _maxPerNpc)
            npc.Observations.RemoveAt(0);

        return new ObservationResult { Accepted = true, Observation = observation };
    }

    private NpcPersistentData GetOrCreateNpc(string npcKey)
    {
        if (!_data.Characters.TryGetValue(npcKey, out var npc))
        {
            npc = new NpcPersistentData { NpcId = npcKey };
            _data.Characters[npcKey] = npc;
        }
        return npc;
    }

    private string? Validate(ObjectiveObservation o)
    {
        if (string.IsNullOrWhiteSpace(o.ObservationId)) return "missing observationId";
        if (string.IsNullOrWhiteSpace(o.NpcId)) return "missing npcId";
        if (!ObservationTypes.IsValid(o.Type))
            return $"invalid observation type '{o.Type}' (not in frozen §2.4 whitelist)";
        if (string.IsNullOrWhiteSpace(o.GameDate)) return "missing gameDate";
        if (o.TimeOfDay is < 0 or > 2600) return "timeOfDay out of range";
        if (string.IsNullOrWhiteSpace(o.Location)) return "missing location";
        if (string.IsNullOrWhiteSpace(o.SchemaVersion)) return "missing schemaVersion";
        var payloadErr = ValidatePayload(o.Type, o.Payload);
        if (payloadErr is not null) return payloadErr;
        return null;
    }

    /// <summary>Per-type payload schema (contract §2.4: payload validated per type). Minimal Demo set.</summary>
    private static string? ValidatePayload(string type, Dictionary<string, System.Text.Json.JsonElement>? payload)
    {
        switch (type)
        {
            case ObservationTypes.PlayerInjured:
            case ObservationTypes.NpcInjured:
                // Expected: healthBefore / healthAfter / delta (numbers). Not required (polling may
                // only observe the delta) but if present must be numeric.
                if (payload is not null && payload.TryGetValue("delta", out var d) && d.ValueKind != System.Text.Json.JsonValueKind.Number)
                    return "payload.delta must be numeric";
                break;
            case ObservationTypes.DangerEncounter:
                if (payload is not null && payload.TryGetValue("dangerLevel", out var dl) && dl.ValueKind != System.Text.Json.JsonValueKind.Number)
                    return "payload.dangerLevel must be numeric";
                break;
            case ObservationTypes.TimeSpentTogether:
                if (payload is not null && payload.TryGetValue("minutes", out var m) && m.ValueKind != System.Text.Json.JsonValueKind.Number)
                    return "payload.minutes must be numeric";
                break;
            case ObservationTypes.NpcTravelArrived:
                // Travel T1: targetLocation + arrivalResult + source must be present strings.
                if (payload is null
                    || !payload.TryGetValue("targetLocation", out var tl) || tl.ValueKind != System.Text.Json.JsonValueKind.String
                    || !payload.TryGetValue("arrivalResult", out var ar) || ar.ValueKind != System.Text.Json.JsonValueKind.String
                    || !payload.TryGetValue("source", out var so) || so.ValueKind != System.Text.Json.JsonValueKind.String)
                    return "payload requires targetLocation/arrivalResult/source strings";
                break;
        }
        return null;
    }
}
