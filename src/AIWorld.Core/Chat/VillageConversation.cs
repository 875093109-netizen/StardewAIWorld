namespace AIWorld.Core.Chat;

public sealed record VillageParticipant(string Id, string Location, int X, int Y, bool Available);
public sealed record VillageLine(string Speaker, string Text, string[] HeardBy);

/// <summary>Deterministic membership and turn order. No generated name can join this session.</summary>
public sealed class VillageConversation
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Location { get; }
    public List<string> Members { get; } = new();
    public List<VillageLine> Lines { get; } = new();
    public int Turns { get; private set; }
    private string? _last;
    public VillageConversation(IEnumerable<VillageParticipant> candidates, int maxParticipants)
    {
        var all = candidates.Where(p => p.Available).ToArray();
        var seed = all.FirstOrDefault(p => all.Any(other => p.Id != other.Id && Near(p, other))) ?? all.First();
        Location = seed.Location; Members.Add(seed.Id);
        Refresh(all, Math.Clamp(maxParticipants, 2, 4));
    }
    public static bool Near(VillageParticipant a, VillageParticipant b) => a.Available && b.Available
        && a.Location == b.Location && Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) <= 6;
    public void Refresh(IEnumerable<VillageParticipant> candidates, int maxParticipants)
    {
        var all = candidates.Where(p => p.Available && p.Location == Location).GroupBy(p => p.Id).Select(g => g.First()).ToArray();
        var selected = new List<VillageParticipant>();
        // Existing members first; new arrivals only if mutually within speaking range.
        foreach (var p in all.OrderBy(p => Members.Contains(p.Id) ? 0 : 1).ThenBy(p => p.Id, StringComparer.Ordinal))
            if (selected.Count < maxParticipants && selected.All(m => Near(m, p))) selected.Add(p);
        Members.Clear(); Members.AddRange(selected.Select(p => p.Id));
    }
    public string? NextSpeaker()
    {
        if (Members.Count < 2) return null;
        return Members.Where(m => m != _last).OrderBy(m => Lines.Count(l => l.Speaker == m))
            .ThenBy(m => Members.IndexOf(m)).FirstOrDefault();
    }
    public void Commit(string speaker, string speech)
    {
        if (!Members.Contains(speaker)) throw new InvalidOperationException("Speaker left the session.");
        Lines.Add(new(speaker, speech, Members.ToArray())); Turns++; _last = speaker;
    }
    public string HistoryFor(string speaker) => string.Join("\n", Lines.Where(l => l.HeardBy.Contains(speaker))
        .TakeLast(6).Select(l => $"{l.Speaker}: {l.Text}"));
}
