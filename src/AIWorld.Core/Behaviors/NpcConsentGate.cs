using AIWorld.Core.Contracts;

namespace AIWorld.Core.Behaviors;

/// <summary>A1 同意判定结果分类。</summary>
public enum ConsentDisposition
{
    /// <summary>ACCEPT 且绑定正确 — 产生执行权（travel intent 或 commitment）。</summary>
    Authorized,
    /// <summary>DECLINE — 不产生执行权。</summary>
    Declined,
    /// <summary>UNCLEAR — 不产生执行权。</summary>
    Unclear,
    /// <summary>COUNTERPROPOSE — 玩家确认前不产生执行权。</summary>
    CounterProposed,
    /// <summary>requestId/npcId/termsHash 绑定错误 — fail closed。</summary>
    BindingMismatch,
    /// <summary>同一 requestId 重复决定/重复回调 — 幂等拒绝。</summary>
    DuplicateRequest,
}

/// <summary>NPC 反提议（COUNTERPROPOSE）。玩家明确确认前不得执行。
/// RequestKind 保留原始请求种类（ImmediateTravel / FutureAppointment），确认后按原种类重解析，
/// 避免确认反提议后把即时行程误当成定时约会（只产生 commitment 而不产生 travel intent）。</summary>
public sealed record CounterProposal(
    string ProposalId,
    string RequestId,
    string NpcId,
    string ProposedTerms,
    PlayerRequestKind RequestKind);

/// <summary>同意判定结果。</summary>
public sealed class ConsentResult
{
    public required ConsentDisposition Disposition { get; init; }
    public string? Reason { get; init; }
    /// <summary>ACCEPT + ImmediateTravel 时非空（唯一可执行 travel intent）。</summary>
    public NpcIntent? TravelIntent { get; init; }
    /// <summary>ACCEPT + FutureAppointment 时非空（唯一可创建的 commitment）。</summary>
    public TravelCommitment? Commitment { get; init; }
    /// <summary>COUNTERPROPOSE 时非空。</summary>
    public CounterProposal? CounterProposal { get; init; }

    public bool IsAuthorized => Disposition == ConsentDisposition.Authorized;
}

/// <summary>
/// A1 — NPC 主观同意门（Core，无游戏依赖，离线可测）。
///
/// 玩家请求（PlayerRequest）≠ NPC 接受命令。模型只提供 NpcDecision（outcome + 可选反提议
/// 台词），本门做确定性判定：requestId/npcId/termsHash 三者必须完全匹配，只有明确 ACCEPT
/// 才产生执行权。DECLINE/UNCLEAR 不产生；COUNTERPROPOSE 在玩家确认前不产生。
/// 同一 requestId 重试/重复回调/读档重载不得重复决定。
///
/// 模型不得把玩家没说的地点/时间/NPC 替换成可执行目标：NpcDecision 不含任何 target/day/time
/// 字段，可执行目标只来自已 grounded 的 PlayerRequest；反提议的新条款必须经玩家确认后重新
/// 解析（ConfirmCounterProposal），绑定反提议 ID 与新 termsHash。
/// </summary>
public sealed class NpcConsentGate
{
    private readonly Func<string> _requestIdGenerator;
    private readonly Func<string> _commitmentIdGenerator;
    private readonly Func<string> _proposalIdGenerator;
    private readonly Dictionary<string, string> _processedRequests = new(StringComparer.Ordinal); // requestId → disposition
    private readonly Dictionary<string, CounterProposal> _pendingProposals = new(StringComparer.Ordinal);

    public NpcConsentGate(
        Func<string>? requestIdGenerator = null,
        Func<string>? commitmentIdGenerator = null,
        Func<string>? proposalIdGenerator = null)
    {
        _requestIdGenerator = requestIdGenerator ?? (() => Guid.NewGuid().ToString("N"));
        _commitmentIdGenerator = commitmentIdGenerator ?? (() => Guid.NewGuid().ToString("N"));
        _proposalIdGenerator = proposalIdGenerator ?? (() => Guid.NewGuid().ToString("N"));
    }

    /// <summary>评估 NPC 决定。返回是否产生执行权。</summary>
    public ConsentResult Evaluate(PlayerRequest request, NpcDecision decision, int currentGameDay, int currentGameTime)
    {
        // 幂等：同一 requestId 只决定一次。
        if (_processedRequests.ContainsKey(request.RequestId))
            return new ConsentResult
            {
                Disposition = ConsentDisposition.DuplicateRequest,
                Reason = $"requestId '{request.RequestId}' already decided",
            };

        // 绑定必须完全匹配。
        if (!string.Equals(decision.RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(decision.NpcId, request.NpcId, StringComparison.Ordinal)
            || !string.Equals(decision.TermsHash, request.TermsHash, StringComparison.Ordinal))
            return new ConsentResult
            {
                Disposition = ConsentDisposition.BindingMismatch,
                Reason = "decision requestId/npcId/termsHash does not match request",
            };

        _processedRequests[request.RequestId] = decision.Outcome.ToString();

        switch (decision.Outcome)
        {
            case NpcDecisionOutcome.Accept:
                return Authorize(request, currentGameDay, currentGameTime);

            case NpcDecisionOutcome.Decline:
                return new ConsentResult
                {
                    Disposition = ConsentDisposition.Declined,
                    Reason = decision.FlavorDialogue ?? "NPC declined",
                };

            case NpcDecisionOutcome.Unclear:
                return new ConsentResult
                {
                    Disposition = ConsentDisposition.Unclear,
                    Reason = decision.FlavorDialogue ?? "NPC unclear",
                };

            case NpcDecisionOutcome.CounterPropose:
            {
                var proposal = new CounterProposal(
                    _proposalIdGenerator(),
                    request.RequestId,
                    request.NpcId,
                    decision.ProposedTerms ?? string.Empty,
                    request.RequestKind);
                _pendingProposals[proposal.ProposalId] = proposal;
                return new ConsentResult
                {
                    Disposition = ConsentDisposition.CounterProposed,
                    Reason = decision.FlavorDialogue ?? "NPC counter-proposed",
                    CounterProposal = proposal,
                };
            }

            default:
                return new ConsentResult
                {
                    Disposition = ConsentDisposition.BindingMismatch,
                    Reason = $"unknown outcome '{decision.Outcome}'",
                };
        }
    }

    /// <summary>
    /// 玩家确认反提议。绑定反提议 ID，生成新 requestId 与新 termsHash（绑定 proposalId），
    /// 返回新的 PlayerRequest。不得确认到其他请求。
    /// </summary>
    public PlayerRequest? ConfirmCounterProposal(CounterProposal proposal, string groundedTarget, int gameDay, int gameTime)
    {
        if (!_pendingProposals.TryGetValue(proposal.ProposalId, out var stored))
            return null; // 未登记或已确认的反提议 — fail closed
        if (stored.RequestId != proposal.RequestId || stored.NpcId != proposal.NpcId)
            return null; // 绑定到其他请求 — fail closed

        var npcId = stored.NpcId;
        var kind = stored.RequestKind;
        // 绑定反提议 ID：新 termsHash 纳入 proposalId，确保确认到唯一反提议。
        var boundHash = PlayerRequest.ComputeTermsHash(
            npcId, kind, $"{groundedTarget}:proposal:{proposal.ProposalId}", gameDay, gameTime);

        _pendingProposals.Remove(proposal.ProposalId);

        return new PlayerRequest
        {
            RequestId = _requestIdGenerator(),
            NpcId = npcId,
            RequestKind = kind,
            OriginalUtterance = proposal.ProposedTerms,
            GroundedTarget = groundedTarget,
            RequestedGameDay = gameDay,
            RequestedGameTime = gameTime,
            TermsHash = boundHash,
            SchemaVersion = "1",
        };
    }

    /// <summary>是否有未确认的反提议。</summary>
    public int PendingProposalCount => _pendingProposals.Count;

    private ConsentResult Authorize(PlayerRequest request, int currentGameDay, int currentGameTime)
    {
        if (request.RequestKind is PlayerRequestKind.ImmediateTravel or PlayerRequestKind.AccompanyPlayer)
        {
            var intent = new NpcIntent
            {
                IntentId = Guid.NewGuid().ToString("N"),
                Type = request.RequestKind == PlayerRequestKind.AccompanyPlayer
                    ? CapabilityRegistry.CapabilityIds.FollowPlayer : CapabilityRegistry.CapabilityIds.TravelToLocation,
                Target = request.RequestKind == PlayerRequestKind.AccompanyPlayer ? "Player" : request.GroundedTarget,
                Priority = 0.5f,
                SchemaVersion = "1",
                Reason = "A1 immediate travel (NPC explicitly accepted)",
            };
            return new ConsentResult
            {
                Disposition = ConsentDisposition.Authorized,
                TravelIntent = intent,
            };
        }

        var commitment = new TravelCommitment
        {
            CommitmentId = _commitmentIdGenerator(),
            RequestId = request.RequestId,
            NpcId = request.NpcId,
            GroundedTarget = request.GroundedTarget,
            TargetGameDay = request.RequestedGameDay,
            TargetGameTime = request.RequestedGameTime,
            AcceptedTermsHash = request.TermsHash,
            Status = TravelCommitmentStatus.Accepted,
            CreatedAtGameTime = $"{currentGameDay}:{currentGameTime}",
            SchemaVersion = "1",
        };
        return new ConsentResult
        {
            Disposition = ConsentDisposition.Authorized,
            Commitment = commitment,
        };
    }
}
