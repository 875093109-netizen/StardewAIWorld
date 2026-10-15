using System.Text.Json.Serialization;

namespace AIWorld.Core.Persistence;

/// <summary>
/// P3 CharacterKnowledge epistemic state — mirrors contracts v0.2 §2.3.
/// Confidence of knowledge, NOT adoption. LEARNED is NOT a knowledge state (removed per PM v0.2):
/// adoption lives in NpcPersistentData.Skills (magicStudent/magicProficiency).
/// Transition is one-way: UNKNOWN → RUMOR → VERIFIED (no regression).
/// </summary>
public sealed class CharacterKnowledge
{
    public const string StateUnknown = "UNKNOWN";
    public const string StateRumor = "RUMOR";
    public const string StateVerified = "VERIFIED";

    /// <summary>Valid knowledge states. LEARNED deliberately absent.</summary>
    public static readonly string[] ValidStates = { StateUnknown, StateRumor, StateVerified };

    [JsonPropertyName("factId")]
    public required string FactId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("updatedAt")]
    public required string UpdatedAt { get; init; }

    [JsonPropertyName("confidence")]
    public float? Confidence { get; init; }

    [JsonPropertyName("sourceObservationId")]
    public string? SourceObservationId { get; init; }

    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    /// <summary>Returns the allowed next state (UNKNOWN→RUMOR→VERIFIED), or null if already VERIFIED / invalid.</summary>
    public static string? NextState(string current) => current switch
    {
        StateUnknown => StateRumor,
        StateRumor => StateVerified,
        StateVerified => null,
        _ => null,
    };

    public static bool IsValidState(string s) => Array.IndexOf(ValidStates, s) >= 0;
}
