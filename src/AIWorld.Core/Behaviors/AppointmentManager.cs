using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>A1 commitment 持久化抽象（Core 无游戏依赖；Demo 用 SMAPI mod data / JSON 实现）。</summary>
public interface ICommitmentStore
{
    void Save(IReadOnlyList<TravelCommitment> commitments);
    IReadOnlyList<TravelCommitment> Load();
}

/// <summary>AppointmentManager.Tick 的结果。</summary>
public sealed class AppointmentTickResult
{
    /// <summary>到出发时间需要 dispatch 的 travel intent（交给 TravelIntentHandler 执行）。</summary>
    public List<NpcIntent> TravelIntentsToDispatch { get; } = new();
    /// <summary>本轮状态发生变化的 commitment。</summary>
    public List<TravelCommitment> StateChanged { get; } = new();
}

/// <summary>
/// A1 — 定时约会调度器（Core，无游戏依赖，离线可测）。
///
/// 接受时只创建 Commitment，绝不立即创建 TravelLease；到出发时间（目标时间 − 预估行程 −
/// 安全余量，依据 NPC 出发时真实位置动态计算）才 dispatch 唯一 travel intent。
/// 提前到达进入 ARRIVED_EARLY/WAITING_FOR_PLAYER，至少等待到约定时间，默认等待窗口 60
/// 游戏分钟；等待窗口结束后自然恢复日程（不瞬移离开）。读档时约会已错过 → MISSED，不补传送。
/// </summary>
public sealed class AppointmentManager
{
    private readonly Dictionary<string, TravelCommitment> _commitments = new(StringComparer.Ordinal);
    private readonly ICommitmentStore? _store;
    private readonly int _waitWindowMinutes;
    private readonly int _safetyMarginMinutes;

    public AppointmentManager(ICommitmentStore? store = null, int waitWindowMinutes = 60, int safetyMarginMinutes = 30)
    {
        _store = store;
        _waitWindowMinutes = waitWindowMinutes;
        _safetyMarginMinutes = safetyMarginMinutes;
    }

    /// <summary>注册一个 commitment（幂等：重复 commitmentId 忽略）。</summary>
    public void Add(TravelCommitment c)
    {
        if (!_commitments.ContainsKey(c.CommitmentId))
            _commitments[c.CommitmentId] = c;
    }

    public TravelCommitment? Get(string commitmentId)
        => _commitments.TryGetValue(commitmentId, out var c) ? c : null;

    public IReadOnlyList<TravelCommitment> All => _commitments.Values.ToList();

    /// <summary>按 NPC 查找处于 DEPARTING 的 commitment（travel 到达/失败回调用）。</summary>
    public TravelCommitment? FindDeparting(string npcId)
        => _commitments.Values.FirstOrDefault(c => c.NpcId == npcId && c.Status == TravelCommitmentStatus.Departing);

    /// <summary>每 tick 推进所有非终态 commitment。不阻塞游戏循环。</summary>
    public AppointmentTickResult Tick(int gameDay, int gameTime, Func<string, int> estimateTravelMinutes, Func<string, bool> isTravelActive)
    {
        var result = new AppointmentTickResult();
        foreach (var c in _commitments.Values.ToList())
        {
            if (IsTerminal(c.Status)) continue;
            if (gameDay > c.TargetGameDay)
            {
                c.Status = TravelCommitmentStatus.Missed;
                c.FailureReason = "appointment day has passed";
                result.StateChanged.Add(c);
                continue;
            }

            switch (c.Status)
            {
                case TravelCommitmentStatus.Accepted:
                case TravelCommitmentStatus.WaitingToDepart:
                    if (c.Status == TravelCommitmentStatus.Accepted)
                    {
                        c.Status = TravelCommitmentStatus.WaitingToDepart;
                        result.StateChanged.Add(c);
                    }
                    if (TravelDeparturePlanner.IsMissed(gameDay, gameTime, c.TargetGameDay, c.TargetGameTime, _waitWindowMinutes))
                    {
                        c.Status = TravelCommitmentStatus.Missed;
                        c.FailureReason = "appointment time passed before departure";
                        result.StateChanged.Add(c);
                        break;
                    }
                    var minutes = Math.Max(0, estimateTravelMinutes(c.NpcId));
                    var departureTime = TravelDeparturePlanner.ComputeDepartureTime(c.TargetGameTime, minutes, _safetyMarginMinutes);
                    if (gameDay == c.TargetGameDay && gameTime >= departureTime && !isTravelActive(c.NpcId))
                    {
                        c.Status = TravelCommitmentStatus.Departing;
                        c.ActualDepartureTime = $"{gameDay}:{gameTime}";
                        result.StateChanged.Add(c);
                        result.TravelIntentsToDispatch.Add(BuildTravelIntent(c));
                    }
                    break;

                case TravelCommitmentStatus.Departing:
                    // 由 OnTravelArrived / OnTravelFailed 推进。
                    break;

                case TravelCommitmentStatus.ArrivedEarly:
                    if (gameTime >= c.TargetGameTime)
                    {
                        c.Status = TravelCommitmentStatus.WaitingForPlayer;
                        result.StateChanged.Add(c);
                    }
                    break;

                case TravelCommitmentStatus.WaitingForPlayer:
                    if (TravelDeparturePlanner.IsWaitWindowElapsed(gameTime, c.TargetGameTime, _waitWindowMinutes))
                    {
                        c.Status = TravelCommitmentStatus.Missed;
                        c.FailureReason = "player did not arrive during the waiting window";
                        result.StateChanged.Add(c);
                    }
                    break;
            }
        }
        return result;
    }

    /// <summary>travel 到达回调：DEPARTING → ARRIVED_EARLY（或 WAITING_FOR_PLAYER）。</summary>
    public void OnTravelArrived(string npcId, int gameDay, int gameTime)
    {
        var c = FindDeparting(npcId);
        if (c is null) return;
        c.ActualArrivalTime = $"{gameDay}:{gameTime}";
        c.Status = gameTime < c.TargetGameTime
            ? TravelCommitmentStatus.ArrivedEarly
            : TravelCommitmentStatus.WaitingForPlayer;
    }

    /// <summary>travel 失败/超时/中断回调：DEPARTING → FAILED。</summary>
    public void OnTravelFailed(string npcId, string reason)
    {
        var c = FindDeparting(npcId);
        if (c is null) return;
        c.Status = TravelCommitmentStatus.Failed;
        c.FailureReason = reason;
    }

    /// <summary>玩家在目标附近出现或完成约定交互 → COMPLETED。</summary>
    public void Complete(string commitmentId)
    {
        var c = Get(commitmentId);
        if (c is null) return;
        if (c.Status is TravelCommitmentStatus.WaitingForPlayer or TravelCommitmentStatus.ArrivedEarly)
            c.Status = TravelCommitmentStatus.Completed;
    }

    /// <summary>玩家取消 → CANCELLED。</summary>
    public void Cancel(string commitmentId, string reason)
    {
        var c = Get(commitmentId);
        if (c is null || IsTerminal(c.Status)) return;
        c.Status = TravelCommitmentStatus.Cancelled;
        c.FailureReason = reason;
    }

    /// <summary>读档：非终态 commitment 若约会时间已错过 → MISSED（不补传送）。</summary>
    public void OnLoad(int gameDay, int gameTime)
    {
        foreach (var c in _commitments.Values)
        {
            if (IsTerminal(c.Status)) continue;
            if (c.Status == TravelCommitmentStatus.Departing)
                c.Status = TravelCommitmentStatus.WaitingToDepart; // leases are session-scoped; re-plan from current runtime position
            if (c.Status is TravelCommitmentStatus.Accepted or TravelCommitmentStatus.WaitingToDepart
                or TravelCommitmentStatus.ArrivedEarly or TravelCommitmentStatus.WaitingForPlayer
                && TravelDeparturePlanner.IsMissed(gameDay, gameTime, c.TargetGameDay, c.TargetGameTime, _waitWindowMinutes))
            {
                c.Status = TravelCommitmentStatus.Missed;
                c.FailureReason = "missed during save/reload";
            }
        }
    }

    public void Save() => _store?.Save(_commitments.Values.ToList());

    public void Load()
    {
        if (_store is null) return;
        _commitments.Clear();
        foreach (var c in _store.Load())
            _commitments[c.CommitmentId] = c;
    }

    private static NpcIntent BuildTravelIntent(TravelCommitment c) => new()
    {
        IntentId = Guid.NewGuid().ToString("N"),
        Type = CapabilityRegistry.CapabilityIds.TravelToLocation,
        Target = c.GroundedTarget,
        Priority = 0.5f,
        SchemaVersion = "1",
        Reason = $"A1 appointment departure ({c.CommitmentId})",
        Params = new Dictionary<string, object?> { ["npcId"] = c.NpcId },
    };

    private static bool IsTerminal(TravelCommitmentStatus s)
        => s is TravelCommitmentStatus.Completed or TravelCommitmentStatus.Declined
            or TravelCommitmentStatus.Missed or TravelCommitmentStatus.Failed
            or TravelCommitmentStatus.Cancelled;
}
