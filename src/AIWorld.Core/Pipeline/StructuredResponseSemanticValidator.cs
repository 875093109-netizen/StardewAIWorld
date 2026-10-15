using AIWorld.Core.Contracts;

namespace AIWorld.Core.Pipeline;

/// <summary>
/// P2-04 Semantic validation. Schema-valid ≠ semantic-valid.
/// Validates against the Phase 2 Demo capability registry, known NPC/location names,
/// StateDelta path whitelist, and MemoryCandidate referential constraints.
/// RETURNS RESULTS ONLY — never executes behavior.
/// </summary>
public sealed class StructuredResponseSemanticValidator
{
    /// <summary>Phase 2 Demo registry: capabilityId → allowed targets (null = any known target).</summary>
    public sealed record CapabilityEntry(string CapabilityId, string[]? AllowedTargets);

    private readonly Dictionary<string, CapabilityEntry> _capabilities;
    private readonly HashSet<string> _knownNpcs;
    private readonly HashSet<string> _knownLocations;
    private readonly HashSet<string> _stateDeltaWhitelist;
    private readonly TravelTargetGrounding? _travelGrounding;
    private readonly HashSet<string> _observableTargets;

    public StructuredResponseSemanticValidator(
        IEnumerable<CapabilityEntry> capabilities,
        IEnumerable<string> knownNpcs,
        IEnumerable<string> knownLocations,
        IEnumerable<string> stateDeltaWhitelist,
        TravelTargetGrounding? travelGrounding = null, IEnumerable<string>? observableTargets = null)
    {
        _capabilities = capabilities.ToDictionary(c => c.CapabilityId, c => c, StringComparer.Ordinal);
        _knownNpcs = knownNpcs.ToHashSet(StringComparer.Ordinal);
        _knownLocations = knownLocations.ToHashSet(StringComparer.Ordinal);
        _stateDeltaWhitelist = stateDeltaWhitelist.ToHashSet(StringComparer.Ordinal);
        _travelGrounding = travelGrounding;
        _observableTargets = (observableTargets ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Default Phase 2 Demo registry (contracts §2.1 + §2.6 whitelist).
    /// NOTE (Travel Gate T1, U5/U6): the location list here is the OFFLINE TEST fallback only.
    /// Production must construct the validator with the LIVE location registry (see
    /// CreateLiveForLocations) — never the static 5-location whitelist.</summary>
    public static StructuredResponseSemanticValidator CreateDemoDefault()
    {
        var capabilities = new[]
        {
            new CapabilityEntry("social.follow_player", new[] { "Player" }),
            new CapabilityEntry("social.stop_following", new[] { "Player" }),
            new CapabilityEntry("magic.observe", null),
            new CapabilityEntry("magic.investigate", null),
            new CapabilityEntry("magic.learn", null),
            new CapabilityEntry("travel.to_location", null), // any known location target (live registry decides validity)
            new CapabilityEntry("INTENT_NONE", new[] { "" }),
        };
        var npcs = new[] { "Abigail", "Sebastian", "Pierre", "Wizard" };
        var locations = new[] { "SeedShop", "WizardHouse", "Mine", "Saloon", "Town" };
        var whitelist = new[]
        {
            "state.mood", "state.stress", "state.energy",
            "state.magic_student", "state.magic_proficiency", "state.magic_learning_state",
            "relationships.player.trust", "relationships.player.friendship", "relationships.player.opinion",
            "knowledge.magic_exists",
            "goals.investigate_magic", "goals.learn_magic",
            "skills.magic",
        };
        return new StructuredResponseSemanticValidator(capabilities, npcs, locations, whitelist);
    }

    /// <summary>
    /// Travel Gate T1 (U5/U6): build the canonical validator against a LIVE location registry.
    /// The travel capability targets canonical location names — validity must be decided by
    /// the actual game location set (Game1.locations at call time), not a static whitelist.
    /// Unregistered capability types are still rejected by the capability map (no drift).
    /// </summary>
    public static StructuredResponseSemanticValidator CreateLiveForLocations(
        IEnumerable<string> knownLocations,
        IEnumerable<string>? knownNpcs = null,
        IEnumerable<string>? extraLocations = null,
        IEnumerable<(string Canonical, string[] Aliases)>? travelAliases = null,
        IEnumerable<string>? observableTargets = null)
    {
        var capabilities = new[]
        {
            new CapabilityEntry("social.follow_player", new[] { "Player" }),
            new CapabilityEntry("social.stop_following", new[] { "Player" }),
            new CapabilityEntry("magic.observe", null),
            new CapabilityEntry("magic.investigate", null),
            new CapabilityEntry("magic.learn", null),
            new CapabilityEntry("travel.to_location", null),
            new CapabilityEntry("INTENT_NONE", new[] { "" }),
        };
        var npcs = knownNpcs ?? new[] { "Abigail", "Sebastian", "Pierre", "Wizard" };
        var whitelist = new[]
        {
            "state.mood", "state.stress", "state.energy",
            "state.magic_student", "state.magic_proficiency", "state.magic_learning_state",
            "relationships.player.trust", "relationships.player.friendship", "relationships.player.opinion",
            "knowledge.magic_exists",
            "goals.investigate_magic", "goals.learn_magic",
            "skills.magic",
        };
        var locations = knownLocations.Concat(extraLocations ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();
        // M1 SEMANTIC SAFETY: grounding 别名表只来自调用方注入（live registry + 本地化别名），
        // 本工厂不硬编码任何语料答案。
        var grounding = travelAliases is { } ta
            ? new TravelTargetGrounding(ta)
            : null;
        var allCapabilities = capabilities.Append(new CapabilityEntry("environment.observe", Array.Empty<string>()));
        return new StructuredResponseSemanticValidator(allCapabilities, npcs, locations, whitelist, grounding, observableTargets);
    }

    public sealed class SemanticOutcome
    {
        public bool Valid { get; set; }
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    public SemanticOutcome Validate(NpcStructuredResponse response)
        => Validate(response, playerUtterance: null);

    /// <summary>
    /// 语义验证（含 M1 SEMANTIC SAFETY 目标 grounding）。<paramref name="playerUtterance"/>
    /// 为玩家原始请求文本：注入 grounding 后，travel intent 的 target 必须能从该文本中的
    /// 地点表达追溯（canonical 名或注册别名），否则 SEMANTIC_ERROR（REJECT，fail closed）。
    /// </summary>
    public SemanticOutcome Validate(NpcStructuredResponse response, string? playerUtterance)
    {
        var outcome = new SemanticOutcome { Valid = true };

        // Intent.
        if (response.Intent is { } intent)
        {
            if (intent.Type == "INTENT_NONE")
            {
                // INTENT_NONE carries no target/priority semantics; acceptable.
            }
            else
            {
                if (!_capabilities.TryGetValue(intent.Type, out var cap))
                {
                    outcome.Errors.Add($"capabilityId '{intent.Type}' not in Phase 2 registry");
                }
                else if (intent.Type == "environment.observe")
                {
                    if (!_observableTargets.Contains(intent.Target) || intent.Params?.Count > 0 || intent.ExpiresAt is not null)
                        outcome.Errors.Add("observation requires an exact currently offered target and no extra parameters");
                }
                else if (cap.AllowedTargets is { Length: > 0 } allowed && !allowed.Contains(intent.Target))
                {
                    outcome.Errors.Add($"intent '{intent.Type}' target '{intent.Target}' not allowed (expected one of: {string.Join(", ", allowed)})");
                }
                else
                {
                    // Target must resolve to known NPC/location/Player.
                    if (!IsResolvableTarget(intent.Target))
                        outcome.Errors.Add($"intent target '{intent.Target}' does not resolve to Player, known NPC, or known location");
                }

                // M1 SEMANTIC SAFETY: travel target must be grounded in the player utterance.
                // Grounding 注入时：未知地点/无地点/指代不明/模型替换 → fail closed。
                if (intent.Type == "travel.to_location"
                    && _travelGrounding is not null
                    && !_travelGrounding.TryGround(playerUtterance ?? "", intent.Target))
                    outcome.Errors.Add($"travel target '{intent.Target}' is not grounded in the player utterance (unknown/unmentioned/pronoun reference — REJECT)");

                if (intent.Priority is < 0f or > 1f)
                    outcome.Errors.Add($"intent priority {intent.Priority} out of [0,1]");
            }
        }

        // MemoryCandidates: referential checks (sourceObservationId existence is A4's job in later phases;
        // Phase 2 only checks fields are well-formed & npc known).
        if (response.MemoryCandidates is { } mems)
        {
            foreach (var m in mems)
            {
                if (!_knownNpcs.Contains(m.NpcId))
                    outcome.Warnings.Add($"memory candidate npcId '{m.NpcId}' not in known NPCs (warn only)");
                if (m.Importance is < 0f or > 1f)
                    outcome.Errors.Add($"memory candidate importance {m.Importance} out of [0,1]");
            }
        }

        // StateDelta: path whitelist + value type sanity.
        if (response.StateDelta is { } deltas)
        {
            foreach (var d in deltas)
            {
                if (!_stateDeltaWhitelist.Contains(d.Path))
                {
                    outcome.Errors.Add($"stateDelta path '{d.Path}' not in whitelist");
                }
                else if (d.Path.StartsWith("persona.", StringComparison.Ordinal)
                      || d.Path.StartsWith("identity.", StringComparison.Ordinal)
                      || d.Path.StartsWith("memory.", StringComparison.Ordinal))
                {
                    outcome.Errors.Add($"stateDelta path '{d.Path}' targets forbidden namespace");
                }

                if (!_knownNpcs.Contains(d.NpcId))
                    outcome.Warnings.Add($"stateDelta npcId '{d.NpcId}' not in known NPCs (warn only)");
            }
        }

        outcome.Valid = outcome.Errors.Count == 0;
        return outcome;
    }

    private bool IsResolvableTarget(string target)
    {
        if (target == "Player") return true;
        if (_knownNpcs.Contains(target)) return true;
        if (_knownLocations.Contains(target)) return true;
        return false;
    }
}
