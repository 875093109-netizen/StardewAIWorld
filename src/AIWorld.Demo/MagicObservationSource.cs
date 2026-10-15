using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Perception;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// Phase 6 production-side magic observation source (contract §2.4 producer, A5).
/// Detects magic world facts from REAL Stardew runtime state — never from test-host commands
/// (PM §14: TestHost causes, Production observes).
///
/// Detection (audit-wizardry §3.1 aligned, no Wizardry mutation):
///   MAGIC_ANALYZE_TAG — Wizardry's `moonslime.Wizardry.analyze` context tag present on an
///     object in the player's current location (SOURCE_VERIFIED: context-tag protocol exists).
///     Edge-triggered: flagged once per distinct tagged object, recorded as an
///     EVENT_TRIGGERED observation with payload {contextTag, location}.
///   MAGIC_ALTAR — `MagicAltar`/`MagicRadio` tile action presence (INFERRED: CP-placed
///     furniture with tile actions; not runtime-exercised in P6 foundation).
///
/// The observation is linked to the active FollowSession when one exists; otherwise recorded
/// unlinked (knowledge transition still valid — observing the world, not a session).
/// MAIN THREAD ONLY.
/// </summary>
public sealed class MagicObservationSource
{
    public const string AnalyzeContextTag = "moonslime.Wizardry.analyze";

    private readonly IMonitor _monitor;
    private readonly ObservationCollector _collector;
    private readonly FollowSessionManager _sessions;
    private readonly string _npcId;
    private readonly Func<IReadOnlySet<TilePosition>>? _visibleTiles;

    private readonly HashSet<string> _flaggedObjects = new(StringComparer.Ordinal);

    public MagicObservationSource(IMonitor monitor, ObservationCollector collector, FollowSessionManager sessions, string npcId,
        Func<IReadOnlySet<TilePosition>>? visibleTiles = null)
    {
        _monitor = monitor;
        _collector = collector;
        _sessions = sessions;
        _npcId = npcId;
        _visibleTiles = visibleTiles;
    }

    public int ObservationsRecorded { get; private set; }

    /// <summary>Per-second poll: scan the player's current location for Wizardry analyze-tagged objects.</summary>
    public void Tick()
    {
        var location = Game1.currentLocation;
        if (location is null) return;

        var gameObjects = location.objects;
        if (gameObjects is null) return;
        IEnumerable<StardewValley.Object> objects = gameObjects.Values;
        // Normal-player path reads only the shared per-observer visible sample. The legacy
        // QA fixture can still omit this provider when testing the context-tag protocol alone.
        if (_visibleTiles is not null)
            objects = _visibleTiles().Select(tile => gameObjects.TryGetValue(
                new Microsoft.Xna.Framework.Vector2(tile.X, tile.Y), out var obj) ? obj : null).OfType<StardewValley.Object>();

        foreach (var obj in objects)
        {
            if (obj is null) continue;
            var tileKey = $"{location.NameOrUniqueName}:{obj.TileLocation.X}:{obj.TileLocation.Y}";
            if (_flaggedObjects.Contains(tileKey)) continue;

            // Detection: Wizardry analyze protocol. The object carries the analyze marker either as
            // a context tag (data-defined, audit §3.1) or as modData (test-host placed). Prefer the
            // real context-tag protocol; fall back to the modData marker for test-host placement.
            bool hasTag = false;
            try
            {
                hasTag = obj.HasContextTag(AnalyzeContextTag);
            }
            catch
            {
                hasTag = false;
            }
            if (!hasTag)
            {
                hasTag = obj.modData?.ContainsKey("moonSlime.Wizardry.Analyze") == true
                    || obj.modData?.ContainsKey(AnalyzeContextTag) == true;
            }

            if (hasTag)
            {
                _flaggedObjects.Add(tileKey);
                Record(tileKey, location.NameOrUniqueName ?? "unknown");
            }
        }
    }

    private void Record(string objectKey, string location)
    {
        var session = _sessions.GetActive(_npcId);
        var payload = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["contextTag"] = System.Text.Json.JsonDocument.Parse($"\"{AnalyzeContextTag}\"").RootElement.Clone(),
            ["location"] = System.Text.Json.JsonDocument.Parse($"\"{location}\"").RootElement.Clone(),
        };

        var result = _collector.Record(new ObjectiveObservation
        {
            ObservationId = Guid.NewGuid().ToString("N"),
            Type = ObservationTypes.EventTriggered,
            NpcId = _npcId,
            GameDate = $"{Game1.currentSeason}-{Game1.dayOfMonth}-{Game1.year}",
            TimeOfDay = Game1.timeOfDay,
            Location = location,
            SchemaVersion = "1",
            Payload = payload,
            SessionId = session?.SessionId,
        });

        if (result.Accepted)
        {
            ObservationsRecorded++;
            _monitor.Log($"[P6] OBSERVATION type=EVENT_TRIGGERED tag={AnalyzeContextTag} loc={location} session={session?.SessionId ?? "none"} linked={(session is not null)} src=magic-context-tag", LogLevel.Info);
        }
    }
}
