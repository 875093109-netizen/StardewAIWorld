using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>Generic fallback: only an actor's own displayed lines, never the unexecuted event script.</summary>
public sealed class ObservedScene
{
    private readonly Dictionary<string, List<string>> _lines = new(StringComparer.Ordinal);
    public string EventId { get; }
    public string Location { get; }
    public string StartedAt { get; }
    public ObservedScene(string eventId, string location, string stamp, IEnumerable<string> actors)
        => (EventId, Location, StartedAt) = (eventId, location, stamp);

    public void Observe(string speaker, string text, IEnumerable<string> currentActors)
    {
        if (!SceneMembership.Contains(speaker, currentActors) || !CharacterId.IsValid(speaker) || string.IsNullOrWhiteSpace(text)) return;
        // Never cut a sentence to fit: it may remove a negation or qualification.
        if (text.Length > 180 || text.Contains("$q") || text.Contains("#$r")) return;
        if (!_lines.TryGetValue(speaker, out var lines)) _lines[speaker] = lines = new();
        if (!lines.Contains(text, StringComparer.Ordinal) && lines.Count < 4) lines.Add(text);
    }

    public IEnumerable<CanonicalExperienceEvent> Complete(bool gameRecordedCompletion)
    {
        if (!gameRecordedCompletion || EventId == "-1") yield break;
        foreach (var (actor, lines) in _lines)
            for (int i = 0; i < lines.Count; i++)
                yield return new CanonicalExperienceEvent
                {
                    ExperienceId = "", Source = "RUNTIME_GENERIC", Type = "OBSERVED_STORY_DIALOGUE",
                    CanonicalSourceId = "runtime.scene." + Location, CanonicalEventId = EventId,
                    OccurrenceKey = actor + ":line:" + i, Actor = actor, Participants = new() { actor },
                    Facts = new() { "你曾在" + Location + "的剧情中说过：" + lines[i] },
                    Location = Location, OccurredAt = StartedAt, MemoryImportance = 0.65f,
                    Provenance = ExperienceProvenance.LIVE_OBSERVED, SchemaVersion = "1"
                };
    }
}
