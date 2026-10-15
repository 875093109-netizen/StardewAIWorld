using System.Text.Json;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;

namespace AIWorld.Demo;

/// <summary>
/// Phase 5 production-side observation source (contract §2.4 producer, A5).
/// Detects OBJECTIVE facts from real Stardew runtime events/state — never from test-host
/// commands (PM §14: TestHost causes, Production observes). Evidence levels (Phase 1.5 V12):
///   LOCATION_VISITED   — SMAPI Warped event (SOURCE_VERIFIED)
///   PLAYER_INJURED     — player health delta polling (INFERRED, FEASIBLE)
///   DANGER_ENCOUNTER   — monster presence polling in current location (INFERRED, FEASIBLE)
///   FOLLOW_STARTED/END — session lifecycle (RUNTIME_VERIFIED, Phase 4)
/// Observations are linked to the active FollowSession when one exists (contract §2.8:
/// session observations only accept observations produced during the session).
/// MAIN THREAD ONLY.
/// </summary>
public sealed class SmapiObservationSource
{
    private readonly IMonitor _monitor;
    private readonly ObservationCollector _collector;
    private readonly FollowSessionManager _sessions;
    private readonly string _npcId;
    private readonly Func<bool>? _isPresent;
    private readonly Func<bool>? _hasVisibleThreat;

    private string? _lastLocation;
    private int _lastPlayerHealth = -1;
    private bool _dangerFlagged;      // edge-trigger: monster presence flagged once per location visit
    private int _observationsRecorded;

    public SmapiObservationSource(IMonitor monitor, ObservationCollector collector, FollowSessionManager sessions, string npcId, Func<bool>? isPresent = null, Func<bool>? hasVisibleThreat = null)
    {
        _monitor = monitor;
        _collector = collector;
        _sessions = sessions;
        _npcId = npcId;
        _isPresent = isPresent;
        _hasVisibleThreat = hasVisibleThreat;
    }

    public int ObservationsRecorded => _observationsRecorded;

    /// <summary>Handle SMAPI Player.Warped (SOURCE_VERIFIED best source for LOCATION_VISITED).</summary>
    public void OnWarped(string locationName)
    {
        if (string.IsNullOrEmpty(locationName) || _isPresent?.Invoke() == false) return;
        if (_lastLocation == locationName) return; // no-op
        _lastLocation = locationName;
        _lastPlayerHealth = -1;
        _dangerFlagged = false; // new location visit resets danger edge trigger

        Record(ObservationTypes.LocationVisited, locationName, payload: null, "Warped");
    }

    /// <summary>Per-second poll: player health delta (PLAYER_INJURED) + monster presence (DANGER_ENCOUNTER).</summary>
    public void Tick()
    {
        var player = Game1.player;
        if (player is null) return;
        if (_isPresent?.Invoke() == false) { _lastPlayerHealth = -1; return; }

        // Current location (fall back when Warped event not observed yet).
        var location = Game1.currentLocation?.NameOrUniqueName;
        if (location is not null && _lastLocation != location)
        {
            _lastLocation = location;
            _lastPlayerHealth = -1;
            _dangerFlagged = false;
            Record(ObservationTypes.LocationVisited, location, payload: null, "Tick");
        }

        // PLAYER_INJURED: health delta polling (INFERRED/FEASIBLE, V12).
        int health = player.health;
        if (_lastPlayerHealth >= 0 && health < _lastPlayerHealth)
        {
            var payload = new Dictionary<string, JsonElement>
            {
                ["healthBefore"] = JsonSerializer.SerializeToElement(_lastPlayerHealth),
                ["healthAfter"] = JsonSerializer.SerializeToElement(health),
                ["delta"] = JsonSerializer.SerializeToElement(_lastPlayerHealth - health),
            };
            Record(ObservationTypes.PlayerInjured, location ?? "unknown", payload, "health-poll");
        }
        _lastPlayerHealth = health;

        // DANGER_ENCOUNTER: monster presence edge trigger (INFERRED/FEASIBLE, V12).
        if (!_dangerFlagged && Game1.currentLocation is not null)
        {
            // Production supplies verified per-observer hostility. Legacy isolated QA keeps its presence probe.
            bool hasMonster = _hasVisibleThreat?.Invoke()
                ?? (Game1.currentLocation.characters?.OfType<Monster>().Any() == true);
            if (hasMonster)
            {
                _dangerFlagged = true;
                var payload = new Dictionary<string, JsonElement>
                {
                    ["dangerLevel"] = JsonSerializer.SerializeToElement(1),
                };
                Record(ObservationTypes.DangerEncounter, location ?? "unknown", payload, "monster-poll");
            }
        }
    }

    /// <summary>Record FOLLOW_STARTED at session start (RUNTIME_VERIFIED lifecycle fact).</summary>
    public void OnFollowStarted(string locationName) => Record(ObservationTypes.FollowStarted, locationName ?? "unknown", null, "session-start");

    /// <summary>Record FOLLOW_ENDED at session end (RUNTIME_VERIFIED lifecycle fact).</summary>
    public void OnFollowEnded(string locationName) => Record(ObservationTypes.FollowEnded, locationName ?? "unknown", null, "session-end");

    private void Record(string type, string location, Dictionary<string, JsonElement>? payload, string source)
    {
        var stamp = Now();
        var observation = new ObjectiveObservation
        {
            ObservationId = Guid.NewGuid().ToString("N"),
            Type = type,
            NpcId = _npcId,
            GameDate = stamp.Date,
            TimeOfDay = stamp.TimeOfDay,
            Location = location,
            SchemaVersion = "1",
            Payload = payload,
            SessionId = _sessions.GetActive(_npcId)?.SessionId,
        };

        var result = _collector.Record(observation);
        if (!result.Accepted)
        {
            _monitor.Log($"[P5] OBS_REJECTED type={type} err={result.Error}", LogLevel.Warn);
            return;
        }

        _observationsRecorded++;
        // Link to active session (contract §2.8: only during session).
        bool linked = _sessions.AppendObservation(_npcId, observation.ObservationId);
        _monitor.Log($"[P5] OBSERVATION type={type} loc={location} session={observation.SessionId ?? "none"} linked={linked} src={source}", LogLevel.Info);
    }

    private (string Date, int TimeOfDay) Now()
    {
        try
        {
            return ($"{Game1.currentSeason}-{Game1.dayOfMonth}-{Game1.year}", Game1.timeOfDay);
        }
        catch
        {
            return ("unknown-date", 600);
        }
    }
}
