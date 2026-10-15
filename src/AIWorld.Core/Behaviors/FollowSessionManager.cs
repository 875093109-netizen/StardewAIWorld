using AIWorld.Core.Contracts;
using AIWorld.Core.Persistence;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// FollowSession lifecycle manager (PM D-P4-3). MINIMAL: start/end + persistence via
/// the Phase 3 AIWorld save infrastructure. Session START only after adapter SUCCESS
/// (observable recruited); END only after adapter SUCCESS (observable not-recruited).
/// Reconciliation: persisted session vs Squad runtime state must agree; mismatch is
/// REPORTED (FOLLOW_STATE_RECONCILIATION_REQUIRED), never silently trusted.
/// </summary>
public sealed class FollowSessionManager
{
    private readonly AIWorldSaveData _data;
    private readonly string _playerId;
    private bool _dirty;

    public FollowSessionManager(AIWorldSaveData data, string playerId)
    {
        _data = data;
        _playerId = playerId;
    }

    public bool Dirty => _dirty;
    public void MarkDirty() => _dirty = true;
    public void MarkClean() => _dirty = false;

    public IReadOnlyList<FollowSession> Sessions => _data.FollowSessions;

    /// <summary>Find the active session for an NPC (at most one per contract §2.8).</summary>
    public FollowSession? GetActive(string npcId) =>
        _data.FollowSessions.FirstOrDefault(s => s.NpcId == npcId && s.IsActive);

    public FollowSession? GetLatest(string npcId) =>
        _data.FollowSessions.LastOrDefault(s => s.NpcId == npcId);

    /// <summary>Start a session. Returns null when one is already active (dedup).</summary>
    public FollowSession? Start(string npcId, string reason, string startedAtStamp, string? adapterId = null)
    {
        if (GetActive(npcId) is not null) return null; // contract §2.8: at most one active

        var session = new FollowSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            NpcId = npcId,
            PlayerId = _playerId,
            StartedAt = startedAtStamp,
            Reason = reason,
            SchemaVersion = "1",
        };
        _data.FollowSessions.Add(session);
        return session;
    }

    /// <summary>Close the active session. Returns false when none active.</summary>
    public bool End(string npcId, string endReason, string endedAtStamp)
    {
        var active = GetActive(npcId);
        if (active is null) return false;
        active.EndedAt = endedAtStamp;
        active.EndReason = endReason;
        return true;
    }

    /// <summary>
    /// Append an observationId to the ACTIVE session for an NPC (contract §2.8: observations only
    /// accept observations produced during the session; closed sessions reject appends).
    /// Returns false when no active session or observationId already linked (dedup).
    /// </summary>
    public bool AppendObservation(string npcId, string observationId)
    {
        var active = GetActive(npcId);
        if (active is null) return false;
        if (active.Observations.Contains(observationId)) return false; // dedup
        active.Observations.Add(observationId);
        _dirty = true;
        return true;
    }

    /// <summary>Set the session summary (consolidation marker). Only allowed on a closed session.</summary>
    public bool SetSummary(string npcId, string summary)
    {
        var latest = GetLatest(npcId);
        if (latest is null || latest.IsActive) return false;
        latest.Summary = summary;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// Reconcile persisted session state against the adapter's actual runtime state.
    /// Returns null when consistent; otherwise a description of the mismatch.
    /// </summary>
    public string? Reconcile(string npcId, bool squadIsFollowing)
    {
        var active = GetActive(npcId);
        bool aiWorldSaysActive = active is not null;

        if (aiWorldSaysActive && !squadIsFollowing)
            return $"FOLLOW_STATE_RECONCILIATION_REQUIRED: AIWorld session active for '{npcId}' but Squad not recruited";
        if (!aiWorldSaysActive && squadIsFollowing)
            return $"FOLLOW_STATE_RECONCILIATION_REQUIRED: no AIWorld session for '{npcId}' but Squad recruited";

        return null;
    }
}
