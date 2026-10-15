using System.Text.Json;
using AIWorld.Core.Chat;

namespace AIWorld.Core.Persistence;

/// <summary>
/// P3 MemoryRepository — persisted memory records per NPC (bounded) + runtime-only recent context.
/// Retrieval is task-specific (bounded queries); full-history dumps are forbidden (PM Phase 3 §16).
/// </summary>
public sealed class MemoryRepository
{
    private readonly AIWorldSaveData _data;
    private bool _dirty;

    public MemoryRepository(AIWorldSaveData data)
    {
        _data = data;
    }

    public bool Dirty => _dirty;
    public void MarkClean() => _dirty = false;

    private NpcPersistentData GetOrCreate(string npcKey)
    {
        if (!_data.Characters.TryGetValue(npcKey, out var npc))
        {
            npc = new NpcPersistentData { NpcId = npcKey };
            _data.Characters[npcKey] = npc;
        }
        return npc;
    }

    /// <summary>Append a memory record, enforcing hard limits. Returns true when appended.</summary>
    public bool Append(string npcKey, MemoryRecord record, bool persist = true)
    {
        var npc = GetOrCreate(npcKey);

        if (persist)
        {
            if (npc.Memories.Count >= MemoryLimits.MaxPersistedMemoriesPerNpc)
                return false; // per-NPC cap
            if (TotalPersistedCount() >= MemoryLimits.MaxTotalPersistedMemories)
                return false; // global cap
            if (record.Summary.Length > MemoryLimits.MaxMemorySummaryChars)
                return false;
            npc.Memories.Add(record);
            _dirty = true;
            return true;
        }

        // Recent context: runtime-only, bounded, NOT persisted.
        npc.RecentContext.Add(record);
        while (npc.RecentContext.Count > MemoryLimits.MaxRecentContextPerNpc)
            npc.RecentContext.RemoveAt(0);
        return true;
    }

    public int TotalPersistedCount()
    {
        int total = 0;
        foreach (var npc in _data.Characters.Values)
            total += npc.Memories.Count;
        return total;
    }

    // ---- Retrieval (task-specific, bounded) ----

    /// <summary>Recent persisted memories for an NPC (newest first), bounded.</summary>
    public IReadOnlyList<MemoryRecord> GetRecentMemories(string npcKey, int limit)
    {
        if (!_data.Characters.TryGetValue(npcKey, out var npc)) return Array.Empty<MemoryRecord>();
        return npc.Memories.AsEnumerable().Reverse().Take(limit).ToList();
    }

    /// <summary>Memories by layer (episodic/semantic/relationship), newest first, bounded.</summary>
    public IReadOnlyList<MemoryRecord> GetRelevantMemories(string npcKey, string query, int limit)
    {
        var terms = ContextBudget.Terms(query);
        _data.Characters.TryGetValue(npcKey, out var npc);
        // Canonical truth remains recallable even when the optional memory cache reaches its cap.
        // Exclude cache copies to avoid returning the same experience twice.
        var cached = (npc?.Memories ?? new List<MemoryRecord>()).Where(m =>
            m.InternalMetadata?.ContainsKey("experienceId") != true);
        var canonical = _data.CanonicalExperiences.Where(e => e.Participants.Contains(npcKey, StringComparer.Ordinal))
            .Select(e => new MemoryRecord { MemoryId = e.ExperienceId, NpcId = npcKey, Layer = MemoryLayer.Episodic,
                Summary = string.Join("；", e.Facts), Importance = e.MemoryImportance,
                CreatedAt = e.OccurredAt, PersistedAt = e.OccurredAt, EmotionalTone = "neutral",
                InternalMetadata = new() { ["experienceId"] = e.ExperienceId } });
        return cached.Concat(canonical).Select((memory, index) => new { memory, index,
            score = ContextBudget.Relevance(memory.Summary, terms) })
            .OrderByDescending(m => m.score).ThenByDescending(m => m.memory.Importance).ThenByDescending(m => m.index)
            .Take(Math.Max(0, limit)).Select(m => m.memory).ToArray();
    }

    public IReadOnlyList<MemoryRecord> GetMemoriesByLayer(string npcKey, MemoryLayer layer, int limit)
    {
        if (!_data.Characters.TryGetValue(npcKey, out var npc)) return Array.Empty<MemoryRecord>();
        return npc.Memories.Where(m => m.Layer == layer).Reverse().Take(limit).ToList();
    }

    /// <summary>Recent runtime context (not persisted).</summary>
    public IReadOnlyList<MemoryRecord> GetRecentContext(string npcKey, int limit)
    {
        if (!_data.Characters.TryGetValue(npcKey, out var npc)) return Array.Empty<MemoryRecord>();
        return npc.RecentContext.AsEnumerable().Reverse().Take(limit).ToList();
    }

    public IReadOnlyDictionary<string, CharacterKnowledge> GetKnowledge(string npcKey)
    {
        if (!_data.Characters.TryGetValue(npcKey, out var npc)) return new Dictionary<string, CharacterKnowledge>();
        return npc.Knowledge;
    }

    public IReadOnlyDictionary<string, JsonElement> GetRelationshipState(string npcKey)
    {
        if (!_data.Characters.TryGetValue(npcKey, out var npc)) return new Dictionary<string, JsonElement>();
        return npc.Relationships;
    }
}
