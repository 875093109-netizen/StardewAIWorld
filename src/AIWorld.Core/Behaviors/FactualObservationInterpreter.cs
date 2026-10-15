using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>Preserve one factual account per outing; subjective interpretation belongs to the LLM.</summary>
public sealed class FactualObservationInterpreter : IObservationInterpreter
{
    private readonly Func<string, string> _locationLabel;
    public FactualObservationInterpreter(Func<string, string>? locationLabel = null)
        => _locationLabel = locationLabel ?? (location => location);
    public IReadOnlyList<MemoryCandidate> Interpret(string npcId, IReadOnlyList<ObjectiveObservation> observations, string gameStamp)
    {
        var relevant = observations.Where(o => o.NpcId == npcId).ToArray();
        if (relevant.Length == 0) return Array.Empty<MemoryCandidate>();
        var places = relevant.Where(o => o.Type == ObservationTypes.LocationVisited)
            .Select(o => _locationLabel(o.Location)).Distinct().Take(6).ToArray();
        var injured = relevant.Any(o => o.Type == ObservationTypes.PlayerInjured);
        var danger = relevant.Any(o => o.Type == ObservationTypes.DangerEncounter);
        var details = new List<string> { "我曾和玩家一起外出。" };
        if (places.Length > 0) details.Add("共同到访：" + string.Join("、", places) + "。");
        if (injured) details.Add("同行期间，我看到玩家受伤。");
        if (danger) details.Add("同行期间，我们所在地点出现了怪物。");
        var summary = string.Join("", details);
        if (summary.Length > 200) summary = summary[..199] + "…";
        return new[] { new MemoryCandidate
        {
            NpcId = npcId, SourceObservationId = relevant[0].ObservationId,
            Summary = summary, EmotionalTone = "neutral",
            Importance = injured || danger ? 0.7f : 0.4f, SchemaVersion = "1"
        } };
    }
}
