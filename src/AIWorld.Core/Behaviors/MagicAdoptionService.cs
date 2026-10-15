using System.Text.Json;
using AIWorld.Core.Persistence;

namespace AIWorld.Core.Behaviors;

/// <summary>
/// Phase 6 — Magic adoption/proficiency state service (A4-owned).
///
/// Enforces contracts §2.3 four-way separation (PM Phase 5 Gate §十五):
///   KNOWLEDGE  → CharacterKnowledge (magic.exists = UNKNOWN/RUMOR/VERIFIED, one-way, no LEARNED)
///   ADOPTION   → state.magic_student (bool)  — Abigail decided to learn magic
///   PROFICIENCY→ state.magic_proficiency (number) + skills.magic — AIWorld-owned proficiency
///   GOAL       → goals.investigate_magic / goals.learn_magic
///
/// NO spell execution here — NPC_NATIVE_MAGIC_CAST is UNSUPPORTED (frozen). This service only
/// manages persistent knowledge/adoption/proficiency/goal state via NpcStateRepository.
/// </summary>
public sealed class MagicAdoptionService
{
    public const string KnowledgeMagicExists = "magic.exists";
    public const string AdoptionPath = "state.magic_student";
    public const string ProficiencyPath = "state.magic_proficiency";
    public const string SkillPath = "skills.magic";
    public const string GoalInvestigate = "goals.investigate_magic";
    public const string GoalLearn = "goals.learn_magic";

    private readonly NpcStateRepository _repository;
    private readonly StateDeltaApplier _applier;

    public MagicAdoptionService(NpcStateRepository repository)
    {
        _repository = repository;
        _applier = new StateDeltaApplier(repository);
    }

    public sealed class AdoptionResult
    {
        public required bool Ok { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>Transition knowledge UNKNOWN→RUMOR→VERIFIED (one-way). Returns the new state or null.</summary>
    public string? ObserveMagic(string npcKey, string gameDate, string? sourceObservationId = null)
        => _applier.ApplyKnowledgeTransition(npcKey, KnowledgeMagicExists, gameDate, sourceObservationId);

    public string? GetKnowledgeState(string npcKey)
        => _repository.GetKnowledgeState(npcKey, KnowledgeMagicExists);

    public bool GetAdoption(string npcKey)
    {
        var v = _repository.GetStateValue(npcKey, AdoptionPath);
        return v is { ValueKind: JsonValueKind.True };
    }

    public double GetProficiency(string npcKey)
    {
        var v = _repository.GetStateValue(npcKey, ProficiencyPath);
        return v is { ValueKind: JsonValueKind.Number } ? v.Value.GetDouble() : 0d;
    }

    public bool HasGoal(string npcKey, string goalPath)
        => _repository.GetStateValue(npcKey, goalPath) is { ValueKind: JsonValueKind.True };

    /// <summary>Set investigate_magic goal (true). Idempotent.</summary>
    public AdoptionResult SetInvestigateGoal(string npcKey, string gameDate)
        => ApplyScalar(npcKey, GoalInvestigate, JsonValueKind.True, gameDate);

    /// <summary>Adopt magic learning: state.magic_student = true + goals.learn_magic = true.</summary>
    public AdoptionResult AdoptMagic(string npcKey, string gameDate)
    {
        var a = ApplyScalar(npcKey, AdoptionPath, JsonValueKind.True, gameDate);
        if (!a.Ok) return a;
        return ApplyScalar(npcKey, GoalLearn, JsonValueKind.True, gameDate);
    }

    /// <summary>Set AIWorld-owned proficiency (skills.magic + state.magic_proficiency).</summary>
    public AdoptionResult SetProficiency(string npcKey, double value, string gameDate)
    {
        if (value < 0 || value > 1) return new AdoptionResult { Ok = false, Error = $"proficiency {value} out of [0,1]" };
        var a = ApplyNumber(npcKey, ProficiencyPath, value, gameDate);
        if (!a.Ok) return a;
        return ApplyNumber(npcKey, SkillPath, value, gameDate);
    }

    /// <summary>Phase 6 runtime checkpoint: knowledge+adoption+proficiency+goal consistent.</summary>
    public sealed class MagicStateSnapshot
    {
        public required string Knowledge { get; init; }
        public required bool Adopted { get; init; }
        public required double Proficiency { get; init; }
        public required bool InvestigateGoal { get; init; }
        public required bool LearnGoal { get; init; }
    }

    public MagicStateSnapshot Snapshot(string npcKey) => new()
    {
        Knowledge = GetKnowledgeState(npcKey) ?? CharacterKnowledge.StateUnknown,
        Adopted = GetAdoption(npcKey),
        Proficiency = GetProficiency(npcKey),
        InvestigateGoal = HasGoal(npcKey, GoalInvestigate),
        LearnGoal = HasGoal(npcKey, GoalLearn),
    };

    private AdoptionResult ApplyScalar(string npcKey, string path, JsonValueKind kind, string gameDate)
    {
        using var doc = JsonDocument.Parse(kind == JsonValueKind.True ? "true" : "false");
        _repository.ApplyStateValue(npcKey, path, doc.RootElement.Clone(), gameDate);
        return new AdoptionResult { Ok = true };
    }

    private AdoptionResult ApplyNumber(string npcKey, string path, double value, string gameDate)
    {
        var text = value == Math.Floor(value)
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        using var doc = JsonDocument.Parse(text);
        _repository.ApplyStateValue(npcKey, path, doc.RootElement.Clone(), gameDate);
        return new AdoptionResult { Ok = true };
    }
}
