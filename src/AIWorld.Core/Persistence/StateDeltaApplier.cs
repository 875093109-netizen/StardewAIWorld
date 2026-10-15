using System.Text.Json;
using AIWorld.Core.Contracts;

namespace AIWorld.Core.Persistence;

/// <summary>
/// P3-02 StateDelta Authorization + Apply.
/// Schema valid ≠ Semantic valid ≠ Authorized to persist. A StateDelta may be applied ONLY when:
///   1. schema valid (already enforced upstream)   2. semantic valid (upstream)
///   3. path allowlisted                          4. target NPC resolved
///   5. value type valid                          6. project policy allows
/// Multiple deltas in one response are applied ALL-OR-NOTHING (transactional): if any item fails,
/// the whole batch is rejected — no partial writes (PM Phase 3 §11).
/// Only AIWorld-owned state may be mutated (state.* / relationships.* / knowledge.* / goals.* / skills.*
/// via explicit mapping). Forbidden: arbitrary dotted-path reflection, third-party fields, Farmer, NPC.modData.
/// </summary>
public sealed class StateDeltaApplier
{
    /// <summary>Allowed top-level namespaces (explicit whitelist).</summary>
    public static readonly string[] AllowedNamespaces = { "state", "relationships", "knowledge", "goals", "skills" };

    /// <summary>Explicitly forbidden namespaces (Permanent Persona / identity / memory).</summary>
    public static readonly string[] ForbiddenNamespaces = { "persona", "identity", "memory" };

    private readonly NpcStateRepository _repository;

    public StateDeltaApplier(NpcStateRepository repository)
    {
        _repository = repository;
    }

    public sealed class ApplyResult
    {
        public required bool Applied { get; init; }
        public List<string> Errors { get; } = new();
        /// <summary>Number of deltas applied (0 on batch rejection).</summary>
        public int AppliedCount { get; init; }
    }

    /// <summary>Validate + apply a batch of StateDeltas transactionally (ALL-OR-NOTHING).</summary>
    public ApplyResult ApplyBatch(IReadOnlyList<StateDelta> deltas, string gameDate)
    {
        var result = new ApplyResult { Applied = false, AppliedCount = 0 };

        if (deltas.Count == 0)
            return new ApplyResult { Applied = true, AppliedCount = 0 };

        // Phase 1: validate everything before mutating anything (transaction).
        var staged = new List<(StateDelta Delta, string NpcKey, string Path)>();
        foreach (var d in deltas)
        {
            var err = Validate(d, out var npcKey, out var path);
            if (err is not null)
            {
                result.Errors.Add($"delta '{d.Path}' for '{d.NpcId}': {err}");
                result.Errors.Add("STATE_DELTA_BATCH_REJECTED — no partial writes");
                return result; // reject whole batch
            }
            staged.Add((d, npcKey, path));
        }

        // Phase 2: apply (all validated, so this cannot partially fail).
        foreach (var (delta, npcKey, path) in staged)
        {
            _repository.ApplyStateValue(npcKey, path, delta.Value, gameDate);
        }

        return new ApplyResult { Applied = true, AppliedCount = staged.Count };
    }

    /// <summary>Validate one delta without applying. Returns error string or null when OK.</summary>
    public string? Validate(StateDelta d, out string npcKey, out string path)
    {
        npcKey = NpcPersistentIdPolicy.Resolve(d.NpcId);
        path = d.Path;

        if (!NpcPersistentIdPolicy.IsValid(npcKey))
            return $"npcId '{d.NpcId}' does not resolve to a valid persistent key";

        // Forbidden namespace check FIRST (more precise error than generic not-allowlisted).
        var top = path.Split('.')[0];
        if (Array.IndexOf(ForbiddenNamespaces, top) >= 0)
            return $"path '{path}' targets forbidden namespace '{top}'";
        if (Array.IndexOf(AllowedNamespaces, top) < 0)
            return $"path '{path}' top-level namespace '{top}' not allowlisted";

        // Value must be a JSON scalar.
        if (d.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            return $"value for '{path}' must be a JSON scalar";

        // knowledge.* must be a CharacterKnowledge-compatible state transition.
        if (top == "knowledge")
        {
            var factId = path.Substring("knowledge.".Length);
            if (string.IsNullOrWhiteSpace(factId)) return $"knowledge path '{path}' missing factId";
            var current = _repository.GetKnowledgeState(npcKey, factId);
            if (current is not null && current == CharacterKnowledge.StateVerified)
                return $"knowledge '{factId}' already VERIFIED (one-way transition; no regression)";
            var val = d.Value.ValueKind == JsonValueKind.String ? d.Value.GetString() : null;
            if (val is not null && !CharacterKnowledge.IsValidState(val))
                return $"knowledge state '{val}' invalid (must be UNKNOWN/RUMOR/VERIFIED; LEARNED is not a knowledge state)";
        }

        return null;
    }

    /// <summary>Authorize + apply a single knowledge transition (UNKNOWN→RUMOR→VERIFIED). Returns updated state or null if rejected.</summary>
    public string? ApplyKnowledgeTransition(string npcKey, string factId, string gameDate, string? sourceObservationId = null)
    {
        var current = _repository.GetKnowledgeState(npcKey, factId);
        var next = current is null ? CharacterKnowledge.StateRumor : CharacterKnowledge.NextState(current);
        if (next is null) return null; // already VERIFIED

        _repository.ApplyKnowledge(npcKey, factId, next, gameDate, sourceObservationId);
        return next;
    }
}
