using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// Observation → MemoryCandidate interpretation boundary (PM Phase 5 §8/§17).
/// The SAME objective observation may yield DIFFERENT interpretations per NPC persona
/// (§13.4). Production uses an LLM-backed interpreter (isolated provider); the
/// deterministic fixture proves the plumbing without provider noise.
/// </summary>
public interface IObservationInterpreter
{
    /// <summary>Interpret a batch of objective observations into subjective memory proposals.</summary>
    IReadOnlyList<MemoryCandidate> Interpret(string npcId, IReadOnlyList<ObjectiveObservation> observations, string gameStamp);
}

/// <summary>
/// Deterministic interpretation fixture (PM §17: plumbing validation, provider-independent).
/// Maps objective facts to persona-shaped subjective summaries with rules — NOT random,
/// NOT LLM. Used as the runtime-test backbone; the production LLM interpreter is a
/// separate isolated adapter.
/// </summary>
public sealed class DeterministicObservationInterpreter : IObservationInterpreter
{
    public IReadOnlyList<MemoryCandidate> Interpret(string npcId, IReadOnlyList<ObjectiveObservation> observations, string gameStamp)
    {
        var result = new List<MemoryCandidate>();
        foreach (var obs in observations)
        {
            var candidate = InterpretOne(npcId, obs);
            if (candidate is not null) result.Add(candidate);
        }
        return result;
    }

    private static MemoryCandidate? InterpretOne(string npcId, ObjectiveObservation obs)
    {
        // Objective fact → subjective persona-shaped interpretation. Each rule maps a FACT to
        // an INTERPRETATION (never claims the fact itself is the interpretation).
        switch (obs.Type)
        {
            case ObservationTypes.PlayerInjured:
                return Mem(npcId, obs, "Player was hurt while we were together. I felt worried.", "negative", 0.7f);
            case ObservationTypes.DangerEncounter:
                return Mem(npcId, obs, "We ran into danger today. It was unsettling, but we got through it.", "negative", 0.6f);
            case ObservationTypes.LocationVisited:
                return Mem(npcId, obs, $"We went to {obs.Location} together today.", "neutral", 0.35f);
            case ObservationTypes.FollowStarted:
                return Mem(npcId, obs, "I decided to follow the player today.", "positive", 0.4f);
            case ObservationTypes.FollowEnded:
                return Mem(npcId, obs, "Our time following each other ended.", "neutral", 0.3f);
            case ObservationTypes.TimeSpentTogether:
                return Mem(npcId, obs, "We spent time together today.", "positive", 0.45f);
            default:
                // Not every observation needs a memory (VALUABLE_DISCOVERY/ITEM_GIFTED/EVENT_TRIGGERED
                // are collectible but not memory-worthy in the Demo fixture).
                return null;
        }
    }

    private static MemoryCandidate Mem(string npcId, ObjectiveObservation obs, string summary, string tone, float importance) => new()
    {
        NpcId = npcId,
        SourceObservationId = obs.ObservationId,
        Summary = summary,
        EmotionalTone = tone,
        Importance = importance,
        SchemaVersion = "1",
    };
}
