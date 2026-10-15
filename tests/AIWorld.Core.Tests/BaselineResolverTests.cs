using AIWorld.Core.Behaviors;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>ABIGAIL BEHAVIOR LIFECYCLE v0.2 — BaselineResolver deterministic tests.</summary>
public class BaselineResolverTests
{
    private static readonly BaselineResolver.ScheduleEntry[] NoSchedule = Array.Empty<BaselineResolver.ScheduleEntry>();

    [Fact]
    public void ResolveBaseline_UsesLatestScheduleEntry_AtOrBeforeCurrentTime()
    {
        var schedule = new[]
        {
            new BaselineResolver.ScheduleEntry(900, "SeedShop", 1, 9),
            new BaselineResolver.ScheduleEntry(1400, "Mine", 8, 8),
        };

        var target = BaselineResolver.ResolveBaseline("Abigail", schedule, 1000, "SeedShop", 1, 9, "Town", 40, 40);

        Assert.Equal("SeedShop", target.LocationName);
        Assert.Equal(1, target.TileX);
        Assert.Equal(9, target.TileY);
        Assert.Equal("schedule", target.Source);
    }

    [Fact]
    public void ResolveBaseline_FallsBackToDefault_WhenNoScheduleApplies()
    {
        var target = BaselineResolver.ResolveBaseline("Abigail", NoSchedule, 600, "SeedShop", 1, 9, "Town", 40, 40);

        Assert.Equal("SeedShop", target.LocationName);
        Assert.Equal(1, target.TileX);
        Assert.Equal(9, target.TileY);
        Assert.Equal("default", target.Source);
    }

    [Fact]
    public void ResolveBaseline_FallsBackToCurrent_WhenNoScheduleAndNoDefault()
    {
        var target = BaselineResolver.ResolveBaseline("Abigail", NoSchedule, 600, "", 0, 0, "Town", 40, 40);

        Assert.Equal("Town", target.LocationName);
        Assert.Equal(40, target.TileX);
        Assert.Equal(40, target.TileY);
        Assert.Equal("current", target.Source);
    }

    [Fact]
    public void ResolveBaseline_IgnoresFutureEntries()
    {
        var schedule = new[]
        {
            new BaselineResolver.ScheduleEntry(1400, "Mine", 8, 8), // future — must not apply at 900
            new BaselineResolver.ScheduleEntry(800, "SeedShop", 1, 9),
        };

        var target = BaselineResolver.ResolveBaseline("Abigail", schedule, 900, "SeedShop", 1, 9, "Town", 40, 40);

        Assert.Equal("SeedShop", target.LocationName);
        Assert.Equal(800, 800); // sanity
    }

    [Fact]
    public void IsAtBaseline_True_WhenSameLocationAndTile()
    {
        var target = new BaselineResolver.BaselineTarget("SeedShop", 1, 9, "schedule");
        Assert.True(BaselineResolver.IsAtBaseline(target, "SeedShop", 1, 9));
    }

    [Fact]
    public void IsAtBaseline_False_WhenDifferentLocation()
    {
        var target = new BaselineResolver.BaselineTarget("SeedShop", 1, 9, "schedule");
        Assert.False(BaselineResolver.IsAtBaseline(target, "Town", 1, 9));
    }

    [Fact]
    public void IsAtBaseline_False_WhenDifferentTile()
    {
        var target = new BaselineResolver.BaselineTarget("SeedShop", 1, 9, "schedule");
        Assert.False(BaselineResolver.IsAtBaseline(target, "SeedShop", 2, 9));
    }

    [Fact]
    public void OwnerConstants_AreDistinct()
    {
        Assert.NotEqual(BaselineResolver.OwnerCanonical, BaselineResolver.OwnerAutonomousGoal);
        Assert.Equal("CANONICAL", BaselineResolver.OwnerCanonical);
        Assert.Equal("AI_AUTONOMOUS_GOAL", BaselineResolver.OwnerAutonomousGoal);
    }
}
