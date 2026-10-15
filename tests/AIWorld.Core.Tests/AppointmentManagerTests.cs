using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// A1 — 定时约会调度器测试（确定性离线）。覆盖状态机、时间触发、提前到达、等待窗口、
/// 错过、失败、取消、持久化与出发时间计算。
/// </summary>
public class AppointmentManagerTests
{
    private const string Abigail = "Abigail";

    private sealed class InMemoryStore : ICommitmentStore
    {
        private List<TravelCommitment> _saved = new();
        public void Save(IReadOnlyList<TravelCommitment> commitments) => _saved = commitments.ToList();
        public IReadOnlyList<TravelCommitment> Load() => _saved.ToList();
    }

    private static TravelCommitment CreateCommitment(
        string npcId = Abigail,
        string target = "Beach",
        int day = 5,
        int time = 2100,
        string? id = null)
        => new()
        {
            CommitmentId = id ?? "cmt-1",
            RequestId = "req-1",
            NpcId = npcId,
            GroundedTarget = target,
            TargetGameDay = day,
            TargetGameTime = time,
            AcceptedTermsHash = "hash-1",
            Status = TravelCommitmentStatus.Accepted,
            CreatedAtGameTime = "5:900",
            SchemaVersion = "1",
        };

    // targetTime=2100, estimate=30min, safety=30min → departure=2000.
    private static int EstimateMinutes(string npcId) => 30;

    [Fact]
    public void Tick_Accepted_MovesToWaitingToDepart()
    {
        var mgr = new AppointmentManager();
        mgr.Add(CreateCommitment());

        var res = mgr.Tick(5, 1900, EstimateMinutes, _ => false);

        Assert.Equal(TravelCommitmentStatus.WaitingToDepart, mgr.Get("cmt-1")!.Status);
        Assert.Empty(res.TravelIntentsToDispatch);
    }

    [Fact]
    public void Tick_BeforeDeparture_NoDispatch()
    {
        var mgr = new AppointmentManager();
        mgr.Add(CreateCommitment());

        var res = mgr.Tick(5, 1950, EstimateMinutes, _ => false); // 1950 < 2000

        Assert.Empty(res.TravelIntentsToDispatch);
        Assert.Equal(TravelCommitmentStatus.WaitingToDepart, mgr.Get("cmt-1")!.Status);
    }

    [Fact]
    public void Tick_AtDeparture_DispatchTravelIntent()
    {
        var mgr = new AppointmentManager();
        mgr.Add(CreateCommitment());

        var res = mgr.Tick(5, 2000, EstimateMinutes, _ => false); // == departure

        Assert.Single(res.TravelIntentsToDispatch);
        Assert.Equal("Beach", res.TravelIntentsToDispatch[0].Target);
        Assert.Equal(TravelCommitmentStatus.Departing, mgr.Get("cmt-1")!.Status);
        Assert.NotNull(mgr.Get("cmt-1")!.ActualDepartureTime);
    }

    [Fact]
    public void Tick_TimeJumpPastDeparture_StillDispatchesFromCurrentPosition()
    {
        var mgr = new AppointmentManager();
        mgr.Add(CreateCommitment());

        var res = mgr.Tick(5, 2030, EstimateMinutes, _ => false); // 跳过 2000，但未错过

        Assert.Single(res.TravelIntentsToDispatch);
        Assert.Equal(TravelCommitmentStatus.Departing, mgr.Get("cmt-1")!.Status);
    }

    [Fact]
    public void Tick_TravelActive_DoesNotDoubleDispatch()
    {
        var mgr = new AppointmentManager();
        mgr.Add(CreateCommitment());

        var res = mgr.Tick(5, 2000, EstimateMinutes, _ => true); // travel 已活跃

        Assert.Empty(res.TravelIntentsToDispatch);
        Assert.Equal(TravelCommitmentStatus.WaitingToDepart, mgr.Get("cmt-1")!.Status);
    }

    [Fact]
    public void OnTravelArrived_Early_ArrivedEarly()
    {
        var mgr = new AppointmentManager();
        mgr.Add(CreateCommitment());
        mgr.Tick(5, 2000, EstimateMinutes, _ => false); // → Departing

        mgr.OnTravelArrived(Abigail, 5, 2030); // < 2100

        var c = mgr.Get("cmt-1")!;
        Assert.Equal(TravelCommitmentStatus.ArrivedEarly, c.Status);
        Assert.NotNull(c.ActualArrivalTime);
    }

    [Fact]
    public void OnTravelArrived_OnTime_WaitingForPlayer()
    {
        var mgr = new AppointmentManager();
        mgr.Add(CreateCommitment());
        mgr.Tick(5, 2000, EstimateMinutes, _ => false);

        mgr.OnTravelArrived(Abigail, 5, 2100); // >= 2100

        Assert.Equal(TravelCommitmentStatus.WaitingForPlayer, mgr.Get("cmt-1")!.Status);
    }

    [Fact]
    public void Tick_ArrivedEarly_AtTargetTime_WaitingForPlayer()
    {
        var mgr = new AppointmentManager();
        var c = CreateCommitment();
        c.Status = TravelCommitmentStatus.ArrivedEarly;
        mgr.Add(c);

        mgr.Tick(5, 2100, EstimateMinutes, _ => false);

        Assert.Equal(TravelCommitmentStatus.WaitingForPlayer, mgr.Get("cmt-1")!.Status);
    }

    [Fact]
    public void Tick_WaitingForPlayer_WindowElapsed_Missed()
    {
        var mgr = new AppointmentManager();
        var c = CreateCommitment();
        c.Status = TravelCommitmentStatus.WaitingForPlayer;
        mgr.Add(c);

        mgr.Tick(5, 2200, EstimateMinutes, _ => false); // one real hour after 21:00

        Assert.Equal(TravelCommitmentStatus.Missed, mgr.Get("cmt-1")!.Status);
    }

    [Fact]
    public void Complete_PlayerInteraction_Completed()
    {
        var mgr = new AppointmentManager();
        var c = CreateCommitment();
        c.Status = TravelCommitmentStatus.WaitingForPlayer;
        mgr.Add(c);

        mgr.Complete("cmt-1");

        Assert.Equal(TravelCommitmentStatus.Completed, mgr.Get("cmt-1")!.Status);
    }

    [Fact]
    public void Tick_MissedAppointment_Missed()
    {
        var mgr = new AppointmentManager();
        mgr.Add(CreateCommitment());

        mgr.Tick(5, 2210, EstimateMinutes, _ => false); // later than the 22:00 waiting deadline

        Assert.Equal(TravelCommitmentStatus.Missed, mgr.Get("cmt-1")!.Status);
    }

    [Fact]
    public void OnLoad_MissedDuringReload_Missed()
    {
        var mgr = new AppointmentManager();
        mgr.Add(CreateCommitment());
        mgr.Tick(5, 1900, EstimateMinutes, _ => false); // → WaitingToDepart

        mgr.OnLoad(5, 2300); // 读档时已错过

        Assert.Equal(TravelCommitmentStatus.Missed, mgr.Get("cmt-1")!.Status);
    }

    [Fact]
    public void OnTravelFailed_Failed()
    {
        var mgr = new AppointmentManager();
        mgr.Add(CreateCommitment());
        mgr.Tick(5, 2000, EstimateMinutes, _ => false); // → Departing

        mgr.OnTravelFailed(Abigail, "target unreachable");

        var c = mgr.Get("cmt-1")!;
        Assert.Equal(TravelCommitmentStatus.Failed, c.Status);
        Assert.Equal("target unreachable", c.FailureReason);
    }

    [Fact]
    public void Cancel_Cancelled()
    {
        var mgr = new AppointmentManager();
        mgr.Add(CreateCommitment());
        mgr.Tick(5, 1900, EstimateMinutes, _ => false);

        mgr.Cancel("cmt-1", "player cancelled");

        Assert.Equal(TravelCommitmentStatus.Cancelled, mgr.Get("cmt-1")!.Status);
    }

    [Fact]
    public void Save_Load_Roundtrip()
    {
        var store = new InMemoryStore();
        var mgr = new AppointmentManager(store);
        mgr.Add(CreateCommitment());
        mgr.Tick(5, 1900, EstimateMinutes, _ => false);
        mgr.Save();

        var restored = new AppointmentManager(store);
        restored.Load();

        Assert.Equal(TravelCommitmentStatus.WaitingToDepart, restored.Get("cmt-1")!.Status);
        Assert.Equal("Beach", restored.Get("cmt-1")!.GroundedTarget);
    }

    [Fact]
    public void Add_DuplicateCommitmentId_Idempotent()
    {
        var mgr = new AppointmentManager();
        mgr.Add(CreateCommitment(id: "cmt-1"));
        mgr.Add(CreateCommitment(id: "cmt-1"));

        Assert.Single(mgr.All);
    }

    [Fact]
    public void DeparturePlanner_ComputesDepartureTime()
    {
        Assert.Equal(2000, TravelDeparturePlanner.ComputeDepartureTime(2100, 30, 30));
        Assert.Equal(2050, TravelDeparturePlanner.ComputeDepartureTime(2100, 0, 10));
        Assert.Equal(900, TravelDeparturePlanner.ToMinutes(1500));
        Assert.Equal(1500, TravelDeparturePlanner.FromMinutes(900));
    }

    [Fact]
    public void Reload_RestoresAllNonTerminalStates()
    {
        // A1 P0: ACCEPTED / WAITING_TO_DEPART / ARRIVED_EARLY / WAITING_FOR_PLAYER 四态
        // 必须真实重载恢复（Save → 新 manager → Load），不得丢失或改动状态。
        var store = new InMemoryStore();
        var mgr1 = new AppointmentManager(store);
        var accepted = CreateCommitment(id: "c-accepted"); accepted.Status = TravelCommitmentStatus.Accepted;
        var waiting = CreateCommitment(id: "c-waiting"); waiting.Status = TravelCommitmentStatus.WaitingToDepart;
        var early = CreateCommitment(id: "c-early"); early.Status = TravelCommitmentStatus.ArrivedEarly;
        var waitingP = CreateCommitment(id: "c-waitingp"); waitingP.Status = TravelCommitmentStatus.WaitingForPlayer;
        mgr1.Add(accepted); mgr1.Add(waiting); mgr1.Add(early); mgr1.Add(waitingP);
        mgr1.Save();

        var mgr2 = new AppointmentManager(store);
        mgr2.Load();
        mgr2.OnLoad(1, 900); // 目标 day=5 未到，不应误标 MISSED

        Assert.Equal(TravelCommitmentStatus.Accepted, mgr2.Get("c-accepted")!.Status);
        Assert.Equal(TravelCommitmentStatus.WaitingToDepart, mgr2.Get("c-waiting")!.Status);
        Assert.Equal(TravelCommitmentStatus.ArrivedEarly, mgr2.Get("c-early")!.Status);
        Assert.Equal(TravelCommitmentStatus.WaitingForPlayer, mgr2.Get("c-waitingp")!.Status);
    }
}
