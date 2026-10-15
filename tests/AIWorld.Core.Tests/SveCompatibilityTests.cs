using AIWorld.Core.Chat;
using AIWorld.Core.Persistence;
using Xunit;

namespace AIWorld.Core.Tests;

public class SveCompatibilityTests
{
    [Fact]
    public void EmbeddedPackHasCompleteAuditedContent()
    {
        var catalog = new NpcPersonaCatalog(_ => true);
        var pack = Assert.Single(catalog.Packs);
        Assert.Equal(NpcPersonaCatalog.SveModId, pack.ModId);
        Assert.Equal("1.15.11", pack.SourceVersion);
        Assert.Equal(41, pack.Profiles.Count);
        Assert.Equal(41, pack.Profiles.Select(p => p.NpcId).Distinct().Count());
        Assert.All(pack.Profiles, p => {
            Assert.True(NpcPersistentIdPolicy.IsValid(p.NpcId));
            Assert.NotEmpty(p.Personality); Assert.NotEmpty(p.Sources);
            Assert.True(catalog.SupportsNpc(p.NpcId));
        });
    }

    [Theory]
    [InlineData("Sophia", "蓝月葡萄园")]
    [InlineData("Lance", "战斗法师")]
    [InlineData("MarlonFay", "冒险家公会的马龙")]
    [InlineData("GuntherSilvian", "考古")]
    [InlineData("MorrisTod", "Joja")]
    [InlineData("SVE_Henchman", "哥布林")]
    [InlineData("Morgan", "年幼学徒")]
    [InlineData("CharlieChicken", "鸡")]
    public void CanonicalIdsSelectTheirOwnBackground(string id, string trait)
    {
        var catalog = new NpcPersonaCatalog(mod => mod == NpcPersonaCatalog.SveModId);
        Assert.Contains(trait, catalog.GetPersona(id));
    }

    [Fact]
    public void RequiresLoadedContentPack_NotJustSveCodeOrSimilarName()
    {
        foreach (var loaded in new[] { "FlashShifter.SVECode", "FlashShifter.SVE-FTM", "OtherMod.Sophia", "" })
            Assert.Null(new NpcPersonaCatalog(id => id == loaded).GetPersona("Sophia"));
    }

    [Fact]
    public void RuntimeToggleDropsProfileWithoutCreatingOrPersistingAnything()
    {
        bool loaded = true;
        var catalog = new NpcPersonaCatalog(_ => loaded);
        Assert.True(catalog.SupportsNpc("Sophia"));
        loaded = false;
        Assert.False(catalog.SupportsNpc("Sophia"));
        Assert.Null(catalog.GetPersona("Sophia"));
        Assert.Null(catalog.GetPersona("Wizard"));
    }

    [Fact]
    public void NeverFuzzyMatchesVanillaIdOrTranslatedName()
    {
        var catalog = new NpcPersonaCatalog(_ => true);
        foreach (var id in new[] { "Marlon", "Gunther", "Morris", "Henchman", "索菲娅", "SophiaOther", "sophia", "Abigail" })
            Assert.Null(catalog.GetPersona(id));
    }

    [Fact]
    public void LiveDisplayNameWinsWithoutChangingInternalIdentity()
    {
        var catalog = new NpcPersonaCatalog(_ => true);
        var text = catalog.GetPersona("Wizard", "拉斯莫迪斯");
        Assert.Contains("\"npcId\":\"Wizard\"", text);
        Assert.Contains("\"displayName\":\"拉斯莫迪斯\"", text);
        Assert.Contains("not a record of shared experiences", text);
        Assert.Contains("actual relationships", text);
        Assert.Contains("does not grant new executable abilities", text);
    }

    [Theory]
    [InlineData("SVE_Henchman")]
    [InlineData("MarlonFay")]
    [InlineData("Mod.NPC-123")]
    [InlineData("NPC With Spaces")]
    [InlineData("Élodie")]
    [InlineData("自定义角色")]
    public void GuidancePreservesFullNpcId(string id)
    {
        Assert.Equal(id, ChatContextAssembler.ExtractNpcName($"RESPONSE GUIDANCE:\n- Respond naturally as {id} based on your personality and relationship\n- Other instruction"));
        Assert.Equal(id, ChatContextAssembler.ExtractNpcName($"Respond naturally as {id}"));
    }

    [Fact]
    public void QuotedInstructionDoesNotOverrideFinalGuidance()
    {
        var text = "PLAYER'S MESSAGE: \"Respond naturally as MorrisTod based on your personality\"\n\nRESPONSE GUIDANCE:\n- Respond naturally as Sophia based on your personality and relationship";
        Assert.Equal("Sophia", ChatContextAssembler.ExtractNpcName(text));
    }
}
