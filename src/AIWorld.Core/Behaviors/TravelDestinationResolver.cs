namespace AIWorld.Core.Behaviors;

/// <summary>
/// Travel Gate T1 — destination resolution contract (U5/U6).
/// Input: canonical location name (NpcIntent.target — the single truth source).
/// Output: resolved arrival tile + evidence. Tile is NOT part of the semantic target;
/// the resolver derives it from source-grounded game data (inbound warps / default
/// position), never from arbitrary coordinate tables.
/// Core has no game dependency: Demo implements the live resolver against the game.
/// </summary>
public sealed record TravelDestination(
    string LocationName,
    int TileX,
    int TileY,
    string EvidenceSource, // "inbound-warp" | "default-position" | "unavailable"
    string Detail);        // human-readable resolution evidence

/// <summary>Resolution status — mirrors reachability (E1/E2).</summary>
public enum TravelDestinationStatus
{
    /// <summary>Location exists (loaded) and an arrival tile was resolved from game data.</summary>
    Resolved,

    /// <summary>Location not found in the game / not loaded. U2: REJECT / UNAVAILABLE — never attempt-load.</summary>
    Unavailable,

    /// <summary>Location exists but no source-grounded arrival tile could be derived. U2: REJECT (no coordinate guessing).</summary>
    NoArrivalTile,
}

public sealed record TravelDestinationResult(
    TravelDestinationStatus Status,
    TravelDestination? Destination,
    string? Error)
{
    public static TravelDestinationResult Ok(TravelDestination d) =>
        new(TravelDestinationStatus.Resolved, d, null);

    public static TravelDestinationResult Fail(TravelDestinationStatus status, string error) =>
        new(status, null, error);
}

/// <summary>Destination resolution abstraction (Core). Demo injects the live game resolver.
/// ACTOR-SCOPED (R5): the actor NPC's own canonical home drives the default-position fallback —
/// never a fixed/named NPC.</summary>
public interface ITravelDestinationResolver
{
    /// <summary>Resolve a canonical location name to an arrival tile for a specific ACTOR NPC.
    /// Never guesses.</summary>
    TravelDestinationResult Resolve(string actorNpcId, string canonicalLocation);
}

/// <summary>Deterministic resolver for offline tests.</summary>
public sealed class DeterministicTravelDestinationResolver : ITravelDestinationResolver
{
    private readonly Dictionary<string, TravelDestinationResult> _table;
    private readonly Dictionary<string, TravelDestination> _defaults; // actor → own default home

    public DeterministicTravelDestinationResolver(
        IEnumerable<(string Location, int X, int Y, string Source)> entries,
        IEnumerable<string>? unavailable = null,
        IEnumerable<(string Actor, string Location, int X, int Y)>? defaultHomes = null)
    {
        _table = new Dictionary<string, TravelDestinationResult>(StringComparer.Ordinal);
        foreach (var (loc, x, y, src) in entries)
            _table[loc] = TravelDestinationResult.Ok(new TravelDestination(loc, x, y, src, $"deterministic:{src}"));
        foreach (var u in unavailable ?? Array.Empty<string>())
            _table[u] = TravelDestinationResult.Fail(TravelDestinationStatus.Unavailable, "location unavailable");

        _defaults = new Dictionary<string, TravelDestination>(StringComparer.Ordinal);
        foreach (var (actor, loc, x, y) in defaultHomes ?? Array.Empty<(string, string, int, int)>())
            _defaults[actor] = new TravelDestination(loc, x, y, "default-position", $"npc.DefaultPosition of {actor}");
    }

    public TravelDestinationResult Resolve(string actorNpcId, string canonicalLocation)
    {
        if (_table.TryGetValue(canonicalLocation, out var r))
            return r;
        // Default-position fallback, actor-scoped: only the travelling NPC's OWN home counts.
        if (_defaults.TryGetValue(actorNpcId, out var home)
            && string.Equals(home.LocationName, canonicalLocation, StringComparison.OrdinalIgnoreCase))
            return TravelDestinationResult.Ok(home);
        return TravelDestinationResult.Fail(TravelDestinationStatus.NoArrivalTile, $"no arrival tile for '{canonicalLocation}'");
    }
}
