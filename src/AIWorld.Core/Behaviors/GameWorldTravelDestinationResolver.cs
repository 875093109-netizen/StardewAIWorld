namespace AIWorld.Core.Behaviors;

/// <summary>
/// GATE R1 FINAL CLOSURE R5 — minimal injectable game-query seam for the production
/// travel destination resolver. The resolver's decision logic uses ONLY these queries;
/// Demo injects the live Game1-backed implementation, offline tests inject a
/// deterministic fake and exercise the SAME production algorithm.
/// </summary>
public interface ITravelGameQueries
{
    /// <summary>Loaded game location by canonical name; null when not loaded/missing.</summary>
    TravelLocationQuery? GetLocation(string canonicalLocation);

    /// <summary>Actor NPC lookup (actor-scoped: DefaultMap/DefaultPosition belong to THIS NPC).</summary>
    TravelNpcQuery? GetNpc(string actorNpcId);

    /// <summary>Spawnability check for an arrival tile in the given location.</summary>
    bool CanSpawnAt(TravelLocationQuery location, int tileX, int tileY);

    /// <summary>All warps of all loaded locations (inbound-warp scan source).</summary>
    IReadOnlyList<TravelWarpQuery> EnumerateWarps();
}

public sealed record TravelLocationQuery(string Name);

/// <summary>DefaultTileX/Y &lt; 0 means "no default position set" (unset).</summary>
public sealed record TravelNpcQuery(string Name, string DefaultMap, int DefaultTileX, int DefaultTileY);

public sealed record TravelWarpQuery(string SourceLocationName, int X, int Y, string TargetName, int TargetX, int TargetY)
{
    public string Detail => $"warp from {SourceLocationName}({X},{Y}) → {TargetName}({TargetX},{TargetY})";
}

/// <summary>
/// Travel Gate T1 — production destination resolution algorithm (U5/U6, source-grounded
/// arrival tile). This is the SAME implementation used by the Demo's live
/// StardewTravelDestinationResolver (R5: no algorithm copy, no mirror closure).
///
///   1. INBOUND-WARP (primary, actor-independent): first loaded warp whose TargetName ==
///      destination; its (TargetX, TargetY) is where the game places characters entering.
///      Validated spawnable before use.
///   2. DEFAULT-POSITION (fallback, ACTOR-SCOPED): only when the TRAVELLING actor NPC's
///      own DefaultMap == destination. Never a fixed/named NPC.
///   3. No source-grounded tile → REJECT (NoArrivalTile, U2). Coordinate guessing forbidden.
/// </summary>
public sealed class GameWorldTravelDestinationResolver : ITravelDestinationResolver
{
    private readonly ITravelGameQueries _queries;
    private readonly Action<string>? _log;

    public GameWorldTravelDestinationResolver(ITravelGameQueries queries, Action<string>? log = null)
    {
        _queries = queries;
        _log = log;
    }

    public TravelDestinationResult Resolve(string actorNpcId, string canonicalLocation)
    {
        var loc = _queries.GetLocation(canonicalLocation);
        if (loc is null)
        {
            _log?.Invoke($"[T1] DEST_RESOLVE_UNAVAILABLE target={canonicalLocation} reason=location-not-loaded-or-missing");
            return TravelDestinationResult.Fail(TravelDestinationStatus.Unavailable,
                $"location '{canonicalLocation}' not loaded / not in the game (never attempt-load)");
        }

        // 1. Inbound-warp lookup across all loaded locations (actor-independent).
        foreach (var w in _queries.EnumerateWarps())
        {
            if (!string.Equals(w.TargetName, canonicalLocation, StringComparison.OrdinalIgnoreCase)) continue;
            if (_queries.CanSpawnAt(loc, w.TargetX, w.TargetY))
            {
                _log?.Invoke($"[T1] DEST_RESOLVED target={canonicalLocation} tile=({w.TargetX},{w.TargetY}) source=inbound-warp detail={w.Detail}");
                return TravelDestinationResult.Ok(new TravelDestination(canonicalLocation, w.TargetX, w.TargetY, "inbound-warp", w.Detail));
            }
            _log?.Invoke($"[T1] DEST_RESOLVE_TILE_INVALID target={canonicalLocation} tile=({w.TargetX},{w.TargetY}) reason=not-spawnable");
            break; // first matching warp only — fall through to the default-position fallback
        }

        // 2. Default-position fallback — ACTOR-SCOPED: only the travelling NPC's OWN
        // canonical home counts (R5: Harvey's home never resolves Sebastian's travel).
        var npc = _queries.GetNpc(actorNpcId);
        if (npc is not null
            && string.Equals(npc.DefaultMap, canonicalLocation, StringComparison.OrdinalIgnoreCase)
            && npc.DefaultTileX >= 0 && npc.DefaultTileY >= 0
            && _queries.CanSpawnAt(loc, npc.DefaultTileX, npc.DefaultTileY))
        {
            _log?.Invoke($"[T1] DEST_RESOLVED target={canonicalLocation} tile=({npc.DefaultTileX},{npc.DefaultTileY}) source=default-position npc={actorNpcId}");
            return TravelDestinationResult.Ok(new TravelDestination(canonicalLocation, npc.DefaultTileX, npc.DefaultTileY, "default-position", $"npc.DefaultPosition of {actorNpcId}"));
        }

        // 3. No source-grounded tile → REJECT (never guess).
        _log?.Invoke($"[T1] DEST_RESOLVE_NO_ARRIVAL_TILE target={canonicalLocation} reason=no-inbound-warp-and-no-default-position");
        return TravelDestinationResult.Fail(TravelDestinationStatus.NoArrivalTile,
            $"no source-grounded arrival tile for '{canonicalLocation}' (no inbound warp, no default position)");
    }
}
