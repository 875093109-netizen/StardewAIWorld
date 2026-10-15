using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Pipeline;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// A1 — 玩家请求创建器测试（确定性离线）。覆盖立即/定时请求、地点 grounding、
/// 多候选 fail closed、无地点 fail closed、termsHash 不可变性。
/// </summary>
public class PlayerRequestFactoryTests
{
    private const string Abigail = "Abigail";

    private static TravelTargetGrounding Grounding() => new(new[]
    {
        ("Beach", new[] { "海滩", "沙滩" }),
        ("Town", new[] { "小镇", "广场" }),
    });

    private static PlayerRequestFactory Factory() =>
        new(Grounding(), () => "req-1");

    [Fact]
    public void Create_ImmediateTravel_WhenNoTimeExpression()
    {
        var req = Factory().TryCreate(Abigail, "我们去海滩吧", 5, 1400);

        Assert.NotNull(req);
        Assert.Equal(PlayerRequestKind.ImmediateTravel, req!.RequestKind);
        Assert.Equal("Beach", req.GroundedTarget);
        Assert.Equal(1400, req.RequestedGameTime);
        Assert.Equal(5, req.RequestedGameDay);
        Assert.Equal("req-1", req.RequestId);
    }

    [Fact]
    public void Create_FutureAppointment_FromEveningExpression()
    {
        var req = Factory().TryCreate(Abigail, "晚上9点在广场等你", 5, 1400);

        Assert.NotNull(req);
        Assert.Equal(PlayerRequestKind.FutureAppointment, req!.RequestKind);
        Assert.Equal("Town", req.GroundedTarget);
        Assert.Equal(2100, req.RequestedGameTime);
    }

    [Fact]
    public void Create_FutureAppointment_FromColonTime()
    {
        var req = Factory().TryCreate(Abigail, "明天21:30在海滩见", 5, 1400);

        Assert.NotNull(req);
        Assert.Equal(PlayerRequestKind.FutureAppointment, req!.RequestKind);
        Assert.Equal("Beach", req.GroundedTarget);
        Assert.Equal(2130, req.RequestedGameTime);
    }

    [Fact]
    public void Create_NoLocation_FailsClosed()
    {
        Assert.Null(Factory().TryCreate(Abigail, "我们见个面吧", 5, 1400));
    }

    [Fact]
    public void Create_MultipleLocations_FailsClosed()
    {
        Assert.Null(Factory().TryCreate(Abigail, "去海滩，再去广场", 5, 1400));
    }

    [Fact]
    public void Create_AppointmentPhrasing_InSquareWaitForYou_Accepted()
    {
        // A1 核心句式"在X等你"：在(左界)+等(右界) 必须放行，不能因边界误判 fail closed。
        var req = Factory().TryCreate(Abigail, "晚上9点在广场等你", 5, 1400);

        Assert.NotNull(req);
        Assert.Equal("Town", req!.GroundedTarget);
        Assert.Equal(PlayerRequestKind.FutureAppointment, req.RequestKind);
        Assert.Equal(2100, req.RequestedGameTime);
    }

    [Fact]
    public void Create_TermsHash_IsDeterministic()
    {
        var a = Factory().TryCreate(Abigail, "晚上9点在广场等你", 5, 1400)!;
        var b = Factory().TryCreate(Abigail, "晚上9点在广场等你", 5, 1400)!;

        Assert.Equal(a.TermsHash, b.TermsHash);
        var expected = PlayerRequest.ComputeTermsHash(Abigail, PlayerRequestKind.FutureAppointment, "Town", 5, 2100);
        Assert.Equal(expected, a.TermsHash);
    }

    [Fact]
    public void Create_DifferentTerms_DifferHash()
    {
        var a = Factory().TryCreate(Abigail, "晚上9点在广场等你", 5, 1400)!;
        var b = Factory().TryCreate(Abigail, "晚上8点在广场等你", 5, 1400)!;

        Assert.NotEqual(a.TermsHash, b.TermsHash);
    }

    [Fact]
    public void Create_UnknownLocation_WordInsideLongerName_FailsClosed()
    {
        // "小镇" 出现在 "云端小镇" 中（右邻为 CJK）→ 不算词级匹配 → fail closed。
        Assert.Null(Factory().TryCreate(Abigail, "我们去云端小镇吧", 5, 1400));
    }

    [Fact]
    public void Create_EmptyInput_FailsClosed()
    {
        Assert.Null(Factory().TryCreate("", "我们去海滩吧", 5, 1400));
        Assert.Null(Factory().TryCreate(Abigail, "", 5, 1400));
    }
}
