using System.Text.Json.Serialization;

namespace AIWorld.Core.Behaviors;

/// <summary>WorldCapability runtime registration (contract §2.2). Semantic capability ≠ execution backend.</summary>
public sealed class CapabilityEntry
{
    [JsonPropertyName("capabilityId")]
    public required string CapabilityId { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("availability")]
    public required string Availability { get; init; } // AVAILABLE / UNAVAILABLE

    [JsonPropertyName("backend")]
    public required string Backend { get; init; } // NATIVE / AIWORLD_OWNED / SIMULATED / UNSUPPORTED

    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("adapterId")]
    public string? AdapterId { get; init; }

    [JsonPropertyName("constraints")]
    public Dictionary<string, object?>? Constraints { get; init; }
}

/// <summary>
/// CapabilityRegistry — runtime registry of semantic capabilities (A5-owned at runtime).
/// Demo: social.follow_player / social.stop_following (backend NATIVE via StardewSquadAdapter).
/// Per contract §2.2: backend=UNSUPPORTED must not be registered AVAILABLE.
/// </summary>
public sealed class CapabilityRegistry
{
    private readonly Dictionary<string, CapabilityEntry> _entries = new(StringComparer.Ordinal);

    public static class CapabilityIds
    {
        public const string FollowPlayer = "social.follow_player";
        public const string StopFollowing = "social.stop_following";
        public const string MagicObserve = "magic.observe";
        public const string MagicInvestigate = "magic.investigate";
        public const string MagicLearn = "magic.learn";
        public const string TravelToLocation = "travel.to_location";
        public const string IntentNone = "INTENT_NONE";
        public const string ObserveEnvironment = AIWorld.Core.Perception.PerceptionEngine.ObserveCapability;
    }

    public bool Register(CapabilityEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.CapabilityId)) return false;
        if (entry.Backend == "UNSUPPORTED" && entry.Availability == "AVAILABLE") return false; // contract §2.2
        _entries[entry.CapabilityId] = entry;
        return true;
    }

    public CapabilityEntry? Get(string capabilityId) =>
        _entries.TryGetValue(capabilityId, out var e) ? e : null;

    public bool IsAvailable(string capabilityId)
    {
        var e = Get(capabilityId);
        return e is not null && e.Availability == "AVAILABLE";
    }

    public bool HasExecutableBackend(string capabilityId)
    {
        var e = Get(capabilityId);
        return e is not null && e.Backend is "NATIVE" or "AIWORLD_OWNED" or "SIMULATED";
    }

    public IReadOnlyCollection<CapabilityEntry> All => _entries.Values;

    /// <summary>Create the Phase 4 demo registry (social.* available with NATIVE backend).</summary>
    public static CapabilityRegistry CreateDemoRegistry()
    {
        var reg = new CapabilityRegistry();
        reg.Register(new CapabilityEntry
        {
            CapabilityId = CapabilityIds.FollowPlayer,
            DisplayName = "Follow player",
            Availability = "AVAILABLE",
            Backend = "NATIVE",
            SchemaVersion = "1",
            Description = "NPC follows the player (Stardew Squad adapter).",
            AdapterId = "StardewSquadAdapter",
            Constraints = new Dictionary<string, object?> { ["requiresTarget"] = true },
        });
        reg.Register(new CapabilityEntry
        {
            CapabilityId = CapabilityIds.StopFollowing,
            DisplayName = "Stop following",
            Availability = "AVAILABLE",
            Backend = "NATIVE",
            SchemaVersion = "1",
            Description = "NPC stops following the player (Stardew Squad adapter).",
            AdapterId = "StardewSquadAdapter",
            Constraints = new Dictionary<string, object?> { ["requiresTarget"] = true },
        });

        // Phase 6: magic.* capabilities — AIWorld-owned observation/state operations. These are
        // NOT Wizardry spell execution (NPC_NATIVE_MAGIC_CAST = UNSUPPORTED, frozen); the backend
        // is AIWORLD_OWNED: the NPC observes magic world facts, updates knowledge, and manages
        // adoption/proficiency state — zero Wizardry mutation (audit-wizardry §4).
        reg.Register(new CapabilityEntry
        {
            CapabilityId = CapabilityIds.MagicObserve,
            DisplayName = "Observe magic",
            Availability = "AVAILABLE",
            Backend = "AIWORLD_OWNED",
            SchemaVersion = "1",
            Description = "NPC observes magic world facts (Wizardry analyze context tags, altar/radio presence) — knowledge only, no casting.",
            AdapterId = "MagicObservationAdapter",
            Constraints = new Dictionary<string, object?> { ["noCasting"] = true },
        });
        reg.Register(new CapabilityEntry
        {
            CapabilityId = CapabilityIds.MagicInvestigate,
            DisplayName = "Investigate magic",
            Availability = "AVAILABLE",
            Backend = "AIWORLD_OWNED",
            SchemaVersion = "1",
            Description = "NPC investigates magic (goal state, no spell execution).",
            AdapterId = "MagicObservationAdapter",
            Constraints = new Dictionary<string, object?> { ["noCasting"] = true },
        });
        reg.Register(new CapabilityEntry
        {
            CapabilityId = CapabilityIds.MagicLearn,
            DisplayName = "Learn magic",
            Availability = "AVAILABLE",
            Backend = "AIWORLD_OWNED",
            SchemaVersion = "1",
            Description = "NPC decides to learn magic — adoption/proficiency state only, no Spell.OnCast (Farmer-bound).",
            AdapterId = "MagicObservationAdapter",
            Constraints = new Dictionary<string, object?> { ["noCasting"] = true, ["adoptionOnly"] = true },
        });

        // Travel Gate T1: travel.to_location — backend NATIVE (vanilla PathFindController +
        // warpCharacter, no third-party reflection). target = canonical location name (U5/U6).
        reg.Register(new CapabilityEntry
        {
            CapabilityId = CapabilityIds.TravelToLocation,
            DisplayName = "Travel to location",
            Availability = "AVAILABLE",
            Backend = "NATIVE",
            SchemaVersion = "1",
            Description = "NPC travels to a canonical location (vanilla movement: same-location pathing, cross-location warp + final hop).",
            AdapterId = "NativeTravelAdapter",
            Constraints = new Dictionary<string, object?> { ["requiresTarget"] = true, ["targetIsLocation"] = true },
        });
        reg.Register(new CapabilityEntry
        {
            CapabilityId = CapabilityIds.ObserveEnvironment, DisplayName = "Observe visible entity",
            Availability = "AVAILABLE", Backend = "AIWORLD_OWNED", SchemaVersion = "1",
            AdapterId = "WorldPerceptionSource", Description = "Read-only observation of an offered, currently visible target; runtime permission and recapture required.",
            Constraints = new Dictionary<string, object?> { ["readOnly"] = true, ["requiresVisibleTarget"] = true },
        });
        return reg;
    }
}
