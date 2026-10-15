using AIWorld.Core.Behaviors;
using AIWorld.Core.Contracts;
using AIWorld.Core.Pipeline;
using Xunit;

namespace AIWorld.Core.Tests;

/// <summary>
/// M1 SEMANTIC SAFETY CORRECTION — 确定性目标 grounding 回归测试。
/// 覆盖：10 个未知地点逐一拒绝（模型输出任意 canonical target 都必须 REJECT）、
/// 指代 fail closed、模型替换 fail closed、VALID 别名正常放行、未注入 grounding 时
/// 行为向后兼容。
/// </summary>
public class TravelTargetGroundingTests
{
    private static readonly (string Canonical, string[] Aliases)[] AliasTable =
    {
        ("SeedShop", new[] { "种子店" }), ("Beach", new[] { "沙滩" }),
        ("Town", new[] { "小镇" }), ("Mine", new[] { "矿洞" }),
        ("Saloon", new[] { "酒馆" }), ("Forest", new[] { "森林" }),
        ("HarveyRoom", new[] { "诊所" }), ("Mountain", new[] { "山" }),
        ("WizardHouse", new[] { "法师塔" }), ("FarmHouse", new[] { "农场" }),
    };

    private static readonly string[] UnknownLocations =
    {
        "水晶洞窟", "天空之城", "梦幻花园", "海底王国", "云端小镇",
        "地下城堡", "星之殿堂", "月光森林", "太阳神殿", "龙之谷",
    };

    private static readonly string[] CanonicalTargets =
        { "SeedShop", "Beach", "Town", "Mine", "Saloon", "Forest", "HarveyRoom", "Mountain", "WizardHouse", "FarmHouse" };

    private static StructuredResponseSemanticValidator ValidatorWithGrounding()
        => StructuredResponseSemanticValidator.CreateLiveForLocations(
            CanonicalTargets, travelAliases: AliasTable);

    private static NpcStructuredResponse TravelResponse(string target) => new()
    {
        Speech = "好的",
        Intent = new NpcIntent
        {
            IntentId = "test",
            Type = CapabilityRegistry.CapabilityIds.TravelToLocation,
            Target = target,
            Priority = 0.5f,
            SchemaVersion = "1",
        },
        MemoryCandidates = null,
        StateDelta = null,
        SchemaVersion = "1",
    };

    // ── UNKNOWN 地点：10 个未知地点 × 每个 canonical target 的替换 → 全部 REJECT ──

    [Theory]
    [InlineData("水晶洞窟")]
    [InlineData("天空之城")]
    [InlineData("梦幻花园")]
    [InlineData("海底王国")]
    [InlineData("云端小镇")]
    [InlineData("地下城堡")]
    [InlineData("星之殿堂")]
    [InlineData("月光森林")]
    [InlineData("太阳神殿")]
    [InlineData("龙之谷")]
    public void UnknownLocation_AnyCanonicalTarget_Rejected(string unknownLoc)
    {
        var validator = ValidatorWithGrounding();
        foreach (var canonical in CanonicalTargets)
        {
            var outcome = validator.Validate(TravelResponse(canonical), $"带我去{unknownLoc}");
            Assert.False(outcome.Valid, $"未知地点 '{unknownLoc}' + target '{canonical}' 必须 REJECT（模型不得替换/猜测地点）");
            Assert.Contains(outcome.Errors, e => e.Contains("not grounded"));
        }
    }

    // ── UNKNOWN 地点：模型输出 INTENT_NONE → 放行（正确无 travel 行为）──

    [Fact]
    public void UnknownLocation_NoIntent_Accepted()
    {
        var validator = ValidatorWithGrounding();
        var response = new NpcStructuredResponse
        {
            Speech = "我不知道那个地方",
            Intent = null,
            MemoryCandidates = null,
            StateDelta = null,
            SchemaVersion = "1",
        };
        var outcome = validator.Validate(response, "带我去水晶洞窟");
        Assert.True(outcome.Valid);
    }

    // ── 指代 fail closed：那里/老地方/她家 未注册别名 → REJECT ──

    [Theory]
    [InlineData("带我去那里")]
    [InlineData("陪我去老地方")]
    [InlineData("带我去她家")]
    [InlineData("带我去那个地方")]
    [InlineData("带我过去")]
    public void PronounReference_FailClosed(string utterance)
    {
        var validator = ValidatorWithGrounding();
        var outcome = validator.Validate(TravelResponse("Beach"), utterance);
        Assert.False(outcome.Valid, $"指代 '{utterance}' 必须 fail closed");
        Assert.Contains(outcome.Errors, e => e.Contains("not grounded"));
    }

    // ── 模型替换 fail closed：请求 A 地点但 target 输出 B ──

    [Fact]
    public void ModelSubstitution_RequestedSeedShop_TargetBeach_Rejected()
    {
        var validator = ValidatorWithGrounding();
        var outcome = validator.Validate(TravelResponse("Beach"), "带我去种子店");
        Assert.False(outcome.Valid);
        Assert.Contains(outcome.Errors, e => e.Contains("not grounded"));
    }

    // ── 无地点文本 + travel intent → REJECT ──

    [Fact]
    public void NoLocationExpression_TravelIntent_Rejected()
    {
        var validator = ValidatorWithGrounding();
        var outcome = validator.Validate(TravelResponse("Town"), "走，出发吧");
        Assert.False(outcome.Valid);
    }

    // ── VALID 别名正常放行：10 个 canonical 的本地化别名 → target 匹配 ──

    [Theory]
    [InlineData("SeedShop", "种子店")]
    [InlineData("Beach", "沙滩")]
    [InlineData("Town", "小镇")]
    [InlineData("Mine", "矿洞")]
    [InlineData("Saloon", "酒馆")]
    [InlineData("Forest", "森林")]
    [InlineData("HarveyRoom", "诊所")]
    [InlineData("Mountain", "山")]
    [InlineData("WizardHouse", "法师塔")]
    [InlineData("FarmHouse", "农场")]
    public void ValidAlias_Grounded_Accepted(string canonical, string zh)
    {
        var validator = ValidatorWithGrounding();
        var outcome = validator.Validate(TravelResponse(canonical), $"带我去{zh}");
        Assert.True(outcome.Valid, $"{zh} → {canonical} 应通过 grounding");
    }

    // ── canonical 名直接出现在请求文本 → 放行 ──

    [Fact]
    public void CanonicalNameInUtterance_Accepted()
    {
        var validator = ValidatorWithGrounding();
        var outcome = validator.Validate(TravelResponse("Beach"), "take me to Beach please");
        Assert.True(outcome.Valid);
    }

    // ── 未注入 grounding → 行为向后兼容（旧语义不变）──

    [Fact]
    public void WithoutGrounding_UnknownTarget_OnlyKnownLocationCheck()
    {
        var validator = StructuredResponseSemanticValidator.CreateLiveForLocations(CanonicalTargets);
        // target 是 known location → 通过（无 grounding 时不做文本追溯）
        var ok = validator.Validate(TravelResponse("Beach"), "带我去水晶洞窟");
        Assert.True(ok.Valid);
        // target 不是 known location → 仍拒绝
        var bad = validator.Validate(TravelResponse("NonexistentPlace"), "带我去水晶洞窟");
        Assert.False(bad.Valid);
    }
}
