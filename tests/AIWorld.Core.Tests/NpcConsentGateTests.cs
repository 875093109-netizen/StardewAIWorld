using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// A1 — NPC 主观同意门测试（确定性离线）。覆盖绑定匹配、ACCEPT/DECLINE/UNCLEAR/
/// COUNTERPROPOSE、幂等、反提议确认绑定与 fail closed。
/// </summary>
public class NpcConsentGateTests
{
    private const string Abigail = "Abigail";
    private const int Day = 5;
    private const int Time = 900;

    private static PlayerRequest CreateRequest(
        string npcId = Abigail,
        PlayerRequestKind kind = PlayerRequestKind.ImmediateTravel,
        string target = "Beach",
        int day = Day,
        int time = Time,
        string? requestId = null)
    {
        var id = requestId ?? "req-1";
        return new PlayerRequest
        {
            RequestId = id,
            NpcId = npcId,
            RequestKind = kind,
            OriginalUtterance = "去海滩",
            GroundedTarget = target,
            RequestedGameDay = day,
            RequestedGameTime = time,
            TermsHash = PlayerRequest.ComputeTermsHash(npcId, kind, target, day, time),
            SchemaVersion = "1",
        };
    }

    private static NpcDecision CreateDecision(
        PlayerRequest r,
        NpcDecisionOutcome outcome = NpcDecisionOutcome.Accept,
        string? termsHash = null,
        string? npcId = null,
        string? requestId = null,
        string? proposedTerms = null)
        => new()
        {
            RequestId = requestId ?? r.RequestId,
            NpcId = npcId ?? r.NpcId,
            TermsHash = termsHash ?? r.TermsHash,
            Outcome = outcome,
            ProposedTerms = proposedTerms,
            SchemaVersion = "1",
        };

    [Fact]
    public void Accept_ImmediateTravel_ProducesTravelIntent()
    {
        var gate = new NpcConsentGate();
        var req = CreateRequest(kind: PlayerRequestKind.ImmediateTravel, target: "Beach");
        var res = gate.Evaluate(req, CreateDecision(req), Day, Time);

        Assert.Equal(ConsentDisposition.Authorized, res.Disposition);
        Assert.NotNull(res.TravelIntent);
        Assert.Equal(CapabilityRegistry.CapabilityIds.TravelToLocation, res.TravelIntent!.Type);
        Assert.Equal("Beach", res.TravelIntent.Target);
        Assert.Null(res.Commitment);
    }

    [Fact]
    public void Accept_FutureAppointment_ProducesCommitment()
    {
        var gate = new NpcConsentGate();
        var req = CreateRequest(kind: PlayerRequestKind.FutureAppointment, target: "Beach", day: 5, time: 2100);
        var res = gate.Evaluate(req, CreateDecision(req), Day, Time);

        Assert.Equal(ConsentDisposition.Authorized, res.Disposition);
        Assert.NotNull(res.Commitment);
        Assert.Equal(TravelCommitmentStatus.Accepted, res.Commitment!.Status);
        Assert.Equal("Beach", res.Commitment.GroundedTarget);
        Assert.Equal(2100, res.Commitment.TargetGameTime);
        Assert.Null(res.TravelIntent);
    }

    [Fact]
    public void Decline_NoExecution()
    {
        var gate = new NpcConsentGate();
        var req = CreateRequest();
        var res = gate.Evaluate(req, CreateDecision(req, NpcDecisionOutcome.Decline), Day, Time);

        Assert.Equal(ConsentDisposition.Declined, res.Disposition);
        Assert.Null(res.TravelIntent);
        Assert.Null(res.Commitment);
    }

    [Fact]
    public void Unclear_NoExecution()
    {
        var gate = new NpcConsentGate();
        var req = CreateRequest();
        var res = gate.Evaluate(req, CreateDecision(req, NpcDecisionOutcome.Unclear), Day, Time);

        Assert.Equal(ConsentDisposition.Unclear, res.Disposition);
        Assert.Null(res.TravelIntent);
        Assert.Null(res.Commitment);
    }

    [Fact]
    public void CounterPropose_NoExecution_ProducesProposal()
    {
        var gate = new NpcConsentGate();
        var req = CreateRequest(kind: PlayerRequestKind.FutureAppointment, target: "Beach", day: 5, time: 2100);
        var res = gate.Evaluate(req, CreateDecision(req, NpcDecisionOutcome.CounterPropose, proposedTerms: "改到 8 点"), Day, Time);

        Assert.Equal(ConsentDisposition.CounterProposed, res.Disposition);
        Assert.NotNull(res.CounterProposal);
        Assert.Null(res.TravelIntent);
        Assert.Null(res.Commitment);
        Assert.Equal(1, gate.PendingProposalCount);
    }

    [Fact]
    public void TermsHashMismatch_FailClosed()
    {
        var gate = new NpcConsentGate();
        var req = CreateRequest(target: "Beach");
        var decision = CreateDecision(req, termsHash: "deadbeef");
        var res = gate.Evaluate(req, decision, Day, Time);

        Assert.Equal(ConsentDisposition.BindingMismatch, res.Disposition);
        Assert.Null(res.TravelIntent);
        Assert.Null(res.Commitment);
    }

    [Fact]
    public void NpcIdMismatch_FailClosed()
    {
        var gate = new NpcConsentGate();
        var req = CreateRequest(npcId: Abigail);
        var decision = CreateDecision(req, npcId: "Harvey");
        var res = gate.Evaluate(req, decision, Day, Time);

        Assert.Equal(ConsentDisposition.BindingMismatch, res.Disposition);
    }

    [Fact]
    public void RequestIdMismatch_FailClosed()
    {
        var gate = new NpcConsentGate();
        var req = CreateRequest(requestId: "req-a");
        var decision = CreateDecision(req, requestId: "req-b");
        var res = gate.Evaluate(req, decision, Day, Time);

        Assert.Equal(ConsentDisposition.BindingMismatch, res.Disposition);
    }

    [Fact]
    public void DuplicateRequest_Idempotent()
    {
        var gate = new NpcConsentGate();
        var req = CreateRequest(requestId: "req-dup");
        var first = gate.Evaluate(req, CreateDecision(req), Day, Time);
        var second = gate.Evaluate(req, CreateDecision(req), Day, Time);

        Assert.Equal(ConsentDisposition.Authorized, first.Disposition);
        Assert.Equal(ConsentDisposition.DuplicateRequest, second.Disposition);
        Assert.Null(second.TravelIntent);
        Assert.Null(second.Commitment);
    }

    [Fact]
    public void ConfirmCounterProposal_BindsProposalId()
    {
        var gate = new NpcConsentGate(requestIdGenerator: () => "req-confirmed");
        var req = CreateRequest(kind: PlayerRequestKind.FutureAppointment, target: "Beach", day: 5, time: 2100, requestId: "req-orig");
        var res = gate.Evaluate(req, CreateDecision(req, NpcDecisionOutcome.CounterPropose, proposedTerms: "改到 8 点"), Day, Time);

        var confirmed = gate.ConfirmCounterProposal(res.CounterProposal!, "Beach", 5, 2000);

        Assert.NotNull(confirmed);
        Assert.NotEqual(req.RequestId, confirmed!.RequestId);
        Assert.NotEqual(req.TermsHash, confirmed.TermsHash);
        Assert.Equal(2000, confirmed.RequestedGameTime);
        Assert.Equal(0, gate.PendingProposalCount);
    }

    // A1-CT：反提议确认必须保留原始请求种类 —— 即时行程反提议确认后重解析须产生 TravelIntent
    //（而非误当成 FutureAppointment 只产生 commitment），行程才能真正出发（运行时 A1-CT 场景）。
    [Fact]
    public void ConfirmCounterProposal_ImmediateTravel_PreservesKind_ProducesTravelIntent()
    {
        var gate = new NpcConsentGate(requestIdGenerator: () => "req-confirmed");
        var req = CreateRequest(kind: PlayerRequestKind.ImmediateTravel, target: "Beach", requestId: "req-im");
        var res = gate.Evaluate(req, CreateDecision(req, NpcDecisionOutcome.CounterPropose, proposedTerms: "改到 8 点"), Day, Time);
        Assert.Equal(ConsentDisposition.CounterProposed, res.Disposition);
        Assert.NotNull(res.CounterProposal);

        var confirmed = gate.ConfirmCounterProposal(res.CounterProposal!, "Beach", Day, Time);
        Assert.NotNull(confirmed);
        Assert.Equal(PlayerRequestKind.ImmediateTravel, confirmed!.RequestKind);

        var exec = gate.Evaluate(confirmed, CreateDecision(confirmed), Day, Time);
        Assert.Equal(ConsentDisposition.Authorized, exec.Disposition);
        Assert.NotNull(exec.TravelIntent);
        Assert.Null(exec.Commitment);
    }

    [Fact]
    public void ConfirmCounterProposal_Unregistered_ReturnsNull()
    {
        var gate = new NpcConsentGate();
        var bogus = new CounterProposal("nope", "req-x", Abigail, "改到 8 点", PlayerRequestKind.FutureAppointment);
        Assert.Null(gate.ConfirmCounterProposal(bogus, "Beach", 5, 2000));
    }

    [Fact]
    public void ConfirmCounterProposal_WrongProposalId_ReturnsNull()
    {
        var gate = new NpcConsentGate();
        var req = CreateRequest(kind: PlayerRequestKind.FutureAppointment, target: "Beach", day: 5, time: 2100, requestId: "req-1");
        var res = gate.Evaluate(req, CreateDecision(req, NpcDecisionOutcome.CounterPropose, proposedTerms: "改到 8 点"), Day, Time);
        var wrong = new CounterProposal("other", res.CounterProposal!.RequestId, res.CounterProposal.NpcId, res.CounterProposal.ProposedTerms, res.CounterProposal.RequestKind);

        Assert.Null(gate.ConfirmCounterProposal(wrong, "Beach", 5, 2000));
        Assert.Equal(1, gate.PendingProposalCount);
    }
}
