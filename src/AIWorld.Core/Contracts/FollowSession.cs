using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// FollowSession — one follow behavior's lifecycle record (contracts-draft.md §2.8, frozen v0.2).
/// Phase 4 implements MINIMAL lifecycle + persistence: WHO followed WHOM, when started,
/// whether active, when ended. Experience/observation/memory content is Phase 5.
/// </summary>
public sealed class FollowSession
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("npcId")]
    public required string NpcId { get; init; }

    [JsonPropertyName("playerId")]
    public required string PlayerId { get; init; }

    /// <summary>gameDate+time, shared GameTimeStamp format "{season}-{day}-{year}@{HHMM}".</summary>
    [JsonPropertyName("startedAt")]
    public required string StartedAt { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("endedAt")]
    public string? EndedAt { get; set; }

    /// <summary>PLAYER_DISMISS / AI_DISMISS / FESTIVAL / DAY_END / ERROR</summary>
    [JsonPropertyName("endReason")]
    public string? EndReason { get; set; }

    [JsonPropertyName("observations")]
    public List<string> Observations { get; init; } = new();

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonIgnore]
    public bool IsActive => EndedAt is null;
}
