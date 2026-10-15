using System.Text.Json.Serialization;

namespace AIWorld.Core.Contracts;

/// <summary>
/// A1 定时约会状态。持久化，支持存档/读档/冷启动。
/// </summary>
public enum TravelCommitmentStatus
{
    Accepted,
    WaitingToDepart,
    Departing,
    ArrivedEarly,
    WaitingForPlayer,
    Completed,
    Declined,
    Missed,
    Failed,
    Cancelled,
}

/// <summary>
/// A1 — NPC 定时约会承诺。玩家请求 + NPC 明确 ACCEPT 后创建。
/// 接受时只创建 Commitment，不得立即创建 TravelLease；到出发时间才创建唯一 Lease。
/// 全程自然移动；提前到达进入 ARRIVED_EARLY/WAITING_FOR_PLAYER，等待窗口结束后自然恢复日程。
/// </summary>
public sealed class TravelCommitment
{
    [JsonPropertyName("commitmentId")]
    public required string CommitmentId { get; init; }

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("npcId")]
    public required string NpcId { get; init; }

    [JsonPropertyName("groundedTarget")]
    public required string GroundedTarget { get; init; }

    [JsonPropertyName("targetGameDay")]
    public required int TargetGameDay { get; init; }

    [JsonPropertyName("targetGameTime")]
    public required int TargetGameTime { get; init; }

    [JsonPropertyName("acceptedTermsHash")]
    public required string AcceptedTermsHash { get; init; }

    /// <summary>运行时状态（持久化）。</summary>
    [JsonPropertyName("status")]
    public TravelCommitmentStatus Status { get; set; }

    /// <summary>创建时的游戏时间（gameDay * 10000 + gameTime 或单独字段由调用方定义）。</summary>
    [JsonPropertyName("createdAtGameTime")]
    public required string CreatedAtGameTime { get; init; }

    [JsonPropertyName("actualDepartureTime")]
    public string? ActualDepartureTime { get; set; }

    [JsonPropertyName("actualArrivalTime")]
    public string? ActualArrivalTime { get; set; }

    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; set; }

    /// <summary>Schema 版本。</summary>
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }
}
