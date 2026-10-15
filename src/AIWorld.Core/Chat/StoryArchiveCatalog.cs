using System.Text.Json;
using AIWorld.Core.Behaviors;
using AIWorld.Core.Persistence;

namespace AIWorld.Core.Chat;

public sealed class StoryArchiveEntry
{
    public string StoryId { get; set; } = "";
    public string NpcId { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Outcome { get; set; } = "Unknown";
    public string Summary { get; set; } = "";
}
public sealed class StoryArchiveDocument { public List<StoryArchiveEntry> Entries { get; set; } = new(); }

/// <summary>Data files contain sealed material. Only exact, program-proven stage/owner/outcome grants access.</summary>
public sealed class StoryArchiveCatalog
{
    private readonly StoryArchiveEntry[] _entries;
    public StoryArchiveCatalog(StoryArchiveDocument document) => _entries = document.Entries.Take(1000)
        .Where(e => e is not null && e.Summary.Length is > 0 and <= 800).ToArray();
    public static StoryArchiveCatalog BuiltIn { get; } = LoadDefault();
    private static StoryArchiveCatalog LoadDefault()
    {
        using var stream = typeof(StoryArchiveCatalog).Assembly.GetManifestResourceStream("AIWorld.Core.Chat.Stories.story-archive.json")!;
        return new(JsonSerializer.Deserialize<StoryArchiveDocument>(stream)!);
    }
    public string Context(AIWorldSaveData data, string npc) => ContextBudget.Lines(_entries.Where(e =>
        e.NpcId == npc && data.Stories.TryGetValue(e.StoryId, out var s) && s.NpcId == npc
        && s.Evidence is "game-state" or "save-history"
        && s.Stage is StoryStage.Invited or StoryStage.Completed
        && e.Stage == s.Stage.ToString() && e.Outcome == s.Outcome.ToString())
        .Select(e => "- " + e.Summary.Replace('\r', ' ').Replace('\n', ' ')), 900);
}
