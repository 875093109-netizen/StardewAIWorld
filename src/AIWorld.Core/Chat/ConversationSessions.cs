using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIWorld.Core.Chat;

public sealed record ConversationLine(string Role, string Text, string Source);

/// <summary>One bounded, non-authoritative transcript per character, shared by both UI surfaces.
/// The host clears this store on save changes. Dialogue is never promoted to canonical memory.</summary>
public sealed class ConversationSessions
{
    private readonly Dictionary<string, List<ConversationLine>> _lines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> _seen = new(StringComparer.Ordinal);
    public void Clear() { _lines.Clear(); _seen.Clear(); }
    public IReadOnlyList<ConversationLine> Get(string npc) => _lines.TryGetValue(npc, out var lines)
        ? lines.ToArray() : Array.Empty<ConversationLine>();
    public void AddTurn(string npc, string player, string reply)
    {
        Add(npc, new("player", player, "chat"));
        Add(npc, new("npc", reply, "generated"));
    }
    public bool Observe(string npc, string text, string occurrence, string source = "displayed-dialogue")
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!_seen.TryGetValue(npc, out var ids)) _seen[npc] = ids = new();
        if (ids.Contains(occurrence)) return false;
        ids.Enqueue(occurrence); while (ids.Count > 32) ids.Dequeue();
        Add(npc, new("npc", text, source)); return true;
    }
    private void Add(string npc, ConversationLine line)
    {
        if (!_lines.TryGetValue(npc, out var lines)) _lines[npc] = lines = new();
        lines.Add(line with { Text = ContextBudget.Clip(line.Text, 1000) });
        while (lines.Count > 12) lines.RemoveAt(0);
    }
    public string Prompt(string npc)
    {
        var selected = new List<string>(); int size = 0;
        foreach (var line in Get(npc).Reverse())
        {
            var json = JsonSerializer.Serialize(line with { Text = ContextBudget.Clip(line.Text, 260) },
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (size + json.Length + 1 > ContextBudget.History) break;
            selected.Add(json); size += json.Length + 1;
        }
        selected.Reverse();
        return selected.Count == 0 ? "" : "\nSHARED CONVERSATION (spoken words, NOT verified facts; continue the latest topic):\n" + string.Join("\n", selected);
    }
}
