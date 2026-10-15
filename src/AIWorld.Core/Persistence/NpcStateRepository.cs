using System.Text.Json;

namespace AIWorld.Core.Persistence;

/// <summary>
/// P3 NpcStateRepository — in-memory view over AIWorldSaveData for one active save.
/// Owns state/knowledge/goals/relationships/skills mutation (AIWorld-owned only).
/// Dirty tracking: any mutation sets Dirty=true (cleared on successful save).
/// </summary>
public sealed class NpcStateRepository
{
    private readonly AIWorldSaveData _data;
    private bool _dirty;

    public NpcStateRepository(AIWorldSaveData data)
    {
        _data = data;
    }

    public bool Dirty => _dirty;
    public AIWorldSaveData Data => _data;

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

    /// <summary>Apply a scalar to an AIWorld-owned path (state.* / relationships.* / goals.* / skills.*).</summary>
    public void ApplyStateValue(string npcKey, string path, JsonElement value, string gameDate)
    {
        var npc = GetOrCreate(npcKey);
        var parts = path.Split('.');
        var top = parts[0];
        var key = path.Substring(top.Length + 1);

        switch (top)
        {
            case "state": npc.State[key] = value.Clone(); break;
            case "relationships": npc.Relationships[key] = value.Clone(); break;
            case "goals": npc.Goals[key] = value.Clone(); break;
            case "skills": npc.Skills[key] = value.Clone(); break;
            default:
                throw new InvalidOperationException($"path '{path}' not appliable via state repository");
        }
        _dirty = true;
    }

    /// <summary>Read a scalar from state/relationships/goals/skills (returns default when absent).</summary>
    public JsonElement? GetStateValue(string npcKey, string path)
    {
        if (!_data.Characters.TryGetValue(npcKey, out var npc)) return null;
        var parts = path.Split('.');
        if (parts.Length < 2) return null;
        var top = parts[0];
        var key = path.Substring(top.Length + 1);
        Dictionary<string, JsonElement> map = top switch
        {
            "state" => npc.State,
            "relationships" => npc.Relationships,
            "goals" => npc.Goals,
            "skills" => npc.Skills,
            _ => new Dictionary<string, JsonElement>(),
        };
        return map.TryGetValue(key, out var v) ? v : null;
    }

    public string? GetKnowledgeState(string npcKey, string factId)
    {
        if (!_data.Characters.TryGetValue(npcKey, out var npc)) return null;
        return npc.Knowledge.TryGetValue(factId, out var k) ? k.State : null;
    }

    public void ApplyKnowledge(string npcKey, string factId, string state, string gameDate, string? sourceObservationId = null)
    {
        if (!CharacterKnowledge.IsValidState(state))
            throw new InvalidOperationException($"invalid knowledge state '{state}' (LEARNED is not a knowledge state)");
        var npc = GetOrCreate(npcKey);
        npc.Knowledge[factId] = new CharacterKnowledge
        {
            FactId = factId,
            State = state,
            UpdatedAt = gameDate,
            Confidence = state == CharacterKnowledge.StateVerified ? 1f : null,
            SourceObservationId = sourceObservationId,
            SchemaVersion = "1",
        };
        _dirty = true;
    }
}
