using AIWorld.Core.Contracts;
using StardewModdingAPI;
using StardewValley;

namespace AIWorld.Demo;

/// <summary>
/// VanillaCanonicalExperienceSource — canonical experience provider for vanilla Stardew events
/// (PM Character Awareness §4/§5/§8). Reads WORLD TRUTH from the live game:
///   - Heart events: Game1.player.eventsSeen (completed event ids)
///
/// Stardew event data format (source-grounded, verified from installed 1.6 game data):
///   Data/Events/<location> key = "<event ID>/<preconditions>" (split by '/', quote-aware).
///   eventsSeen stores the FIRST SEGMENT (event ID, e.g. "1") — verified via game IL:
///   GameLocation.checkEventPrecondition → Event.SplitPreconditions → parts[0];
///   Event.exitEvent/skipEvent → eventsSeen.Add(Event.id).
///
/// Design rules:
///   - Only produces events for CONFIGURED canonical event semantics (eventId → type/facts/
///     participants/knowledge effects). Unknown ids degrade conservatively (no fabrication).
///   - Actor/recipient knowledge temporally distinct: heart events are shared experiences
///     (participant = the NPC + Player).
///   - MAIL semantics: NOT_IMPLEMENTED / SOURCE_AUDIT_PENDING (PM 2026-08-12).
///     player.mailbox is recipient-side mailbox state and is NOT sufficient evidence that the
///     canonical actor performed a MAIL_SENT action. The generic mail architecture is retained
///     but no runtime mailbox→MAIL_SENT mapping is active until exact vanilla mail
///     trigger/source semantics are grounded.
///   - The caller (Demo driver) is the integration point; this source just reads game truth.
///   - MAIN THREAD ONLY.
/// </summary>
public sealed class VanillaCanonicalExperienceSource
{
    private readonly IMonitor _monitor;

    // Configured canonical event semantics: eventId (FIRST segment of the asset key, e.g. "1")
    // → (type, facts, participants, knowledge effect). Only events AIWorld understands produce
    // experiences; everything else stays unknown.
    // Abigail's Prairie King heart event is the demo positive case (verified from installed game
    // data: Content/Data/Events/SeedShop key "1/f Abigail 500/p Abigail/d Sat" → eventId "1").
    public sealed record CanonicalEventDef(
        string EventId,
        string Type,
        string Actor,
        string Location,
        IReadOnlyList<string> Facts,
        string KnowledgeFactId,
        float MemoryImportance);

    private readonly Dictionary<string, CanonicalEventDef> _eventDefs = new(StringComparer.Ordinal);

    public VanillaCanonicalExperienceSource(IMonitor monitor, IEnumerable<CanonicalEventDef>? eventDefs = null)
    {
        _monitor = monitor;
        _eventDefs = eventDefs?.ToDictionary(d => d.EventId, d => d) ?? new Dictionary<string, CanonicalEventDef>(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, CanonicalEventDef> EventDefs => _eventDefs;

    public void ConfigureEvents(IEnumerable<CanonicalEventDef> eventDefs)
    {
        foreach (var d in eventDefs) _eventDefs[d.EventId] = d;
    }

    /// <summary>Scan the live game for canonical experiences NOT yet ingested. Returns candidate
    /// events (caller passes them through CanonicalExperienceService.Ingest which dedups).
    /// Deterministic: reads player.eventsSeen (stores event IDs = first segment of asset key) —
    /// never invents anything. Unknown ids degrade silently (no fabrication).</summary>
    public IReadOnlyList<CanonicalExperienceEvent> ScanCompletedHeartEvents(string gameStamp)
    {
        var found = new List<CanonicalExperienceEvent>();
        var eventsSeen = Game1.player?.eventsSeen;
        if (eventsSeen is null) return found;

        foreach (var (eventId, def) in _eventDefs)
        {
            if (!eventsSeen.Contains(eventId)) continue; // event did NOT complete → nothing

            var experience = new CanonicalExperienceEvent
            {
                ExperienceId = string.Empty, // recomputed on ingest
                Type = def.Type,
                Source = "VANILLA",
                CanonicalSourceId = "vanilla.heartEvent",
                CanonicalEventId = eventId,
                OccurrenceKey = "once", // one-shot heart events: single stable identity
                Actor = def.Actor,
                Participants = new List<string> { def.Actor, "Player" }, // shared experience
                Facts = new List<string>(def.Facts),
                OccurredAt = gameStamp,
                Location = def.Location,
                KnowledgeEffects = new Dictionary<string, string> { [def.KnowledgeFactId] = "VERIFIED" },
                MemoryImportance = def.MemoryImportance,
                Provenance = ExperienceProvenance.LIVE_OBSERVED,
                SchemaVersion = "1",
            };
            found.Add(experience);
            _monitor.Log($"[AWARE] CANONICAL_EVENT_DETECTED eventId={eventId} type={def.Type} actor={def.Actor} participants={string.Join(",", experience.Participants)}", LogLevel.Info);
        }
        return found;
    }

    /// <summary>MAIL: NOT_IMPLEMENTED / SOURCE_AUDIT_PENDING (PM 2026-08-12).
    /// player.mailbox is RECIPIENT-side mailbox state — it cannot prove the canonical actor
    /// performed a MAIL_SENT action. This method is intentionally disabled; it never returns
    /// candidates. The generic mail architecture is retained for future grounding.
    /// Do NOT re-enable mailbox.Contains(mailId) → MAIL_SENT without source-grounded vanilla
    /// mail trigger semantics.</summary>
    public IReadOnlyList<CanonicalExperienceEvent> ScanMailSent(string gameStamp)
    {
        // NOT_IMPLEMENTED / SOURCE_AUDIT_PENDING: no runtime mail mapping is active.
        return new List<CanonicalExperienceEvent>();
    }

    public sealed record MailDef(string Actor, string Location, IReadOnlyList<string> Facts, string KnowledgeFactId, float MemoryImportance);
    private readonly Dictionary<string, MailDef> _mailDefs = new(StringComparer.Ordinal);

    public void ConfigureMail(IEnumerable<(string mailId, MailDef def)> mailDefs)
    {
        // Retained for future source-grounded mail integration; not active in this phase.
        foreach (var (id, def) in mailDefs) _mailDefs[id] = def;
    }
}
