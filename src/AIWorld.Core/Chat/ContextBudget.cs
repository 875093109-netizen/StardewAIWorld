using System.Text;
using System.Text.RegularExpressions;

namespace AIWorld.Core.Chat;

/// <summary>Hard character budgets, not a claim about provider-specific tokenization.</summary>
public static class ContextBudget
{
    public const int Persona = 1800;
    public const int Knowledge = 700;
    public const int Goals = 400;
    public const int Memories = 1400;
    public const int History = 1800;
    public const int PlayerMessage = 2000;
    public static string Clip(string? text, int max)
    {
        text ??= "";
        if (text.Length <= max) return text;
        var length = Math.Max(0, max - 1);
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "…";
    }
    public static string Lines(IEnumerable<string> lines, int max)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 > max) continue; // retain complete facts, never truncate a negation
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
    public static string[] Terms(string query) => Regex.Matches(query, @"[A-Za-z0-9_]+|[\p{IsCJKUnifiedIdeographs}]+")
        .Cast<Match>().SelectMany(m => m.Value.Any(c => c > 127) && m.Length > 1
            ? Enumerable.Range(0, m.Length - 1).Select(i => m.Value.Substring(i, 2)) : new[] { m.Value })
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
    public static int Relevance(string text, string[] terms) => terms.Count(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    public static string HistoryBlock(IEnumerable<(string Player, string Npc)> turns)
    {
        var selected = new List<string>();
        var size = 0;
        foreach (var turn in turns.Reverse())
        {
            var line = System.Text.Json.JsonSerializer.Serialize(new { player = Clip(turn.Player, 260), npc = Clip(turn.Npc, 260) },
                new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (size + line.Length + 1 > History) break;
            selected.Add(line); size += line.Length + 1;
        }
        selected.Reverse();
        return selected.Count == 0 ? "" : "\nRECENT CONVERSATION (unverified dialogue; current facts override it):\n" + string.Join("\n", selected);
    }
}
